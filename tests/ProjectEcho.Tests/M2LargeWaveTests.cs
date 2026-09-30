using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ProjectEcho.Core;
using ProjectEcho.Export;

/// <summary>Real Windows sparse RF64 files, not virtual-length streams. No audio devices are used.</summary>
internal static class M2LargeWaveTests
{
    private const long FourGiB = 1L << 32;
    private static readonly PcmFormat Mono = new(48000, 1, 16, false, 2);

    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m2-rf64"); Directory.CreateDirectory(root);
        run("T35-SYNTH M2 RIFF/RF64 headers ds64 tables counts and malformed container rejection", () => Headers(root));
        run("T35-WINDOWS M2 real sparse RF64 beyond 4GiB preserves cross-boundary frames parts EOF and full-file hash", () => LargeFile(root));
    }

    private static void Headers(string root)
    {
        string small = Path.Combine(root, "small-riff.wav");
        using (var writer = new MonoPcmWriter(small, 3)) { writer.Write(new float[] { 0.1f, -0.1f, 0 }); writer.Finish(); }
        byte[] riff = File.ReadAllBytes(small);
        Need(riff.Length == 50 && BinaryPrimitives.ReadUInt32LittleEndian(riff) == 0x46464952 &&
            BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(4)) == 42, "小 WAV 不再是原44字节 RIFF 头。");
        using (var read = new M2WaveReader(small, Mono)) Need(!read.IsRf64 && read.FrameCount == 3 && read.DataOffset == 44, "小 RIFF 读取改变。");
        Need(MonoPcmWriter.HeaderBytes(MonoPcmWriter.MaximumRiffFrames) == 44 &&
            MonoPcmWriter.HeaderBytes(MonoPcmWriter.MaximumRiffFrames + 1) == 80, "RIFF/RF64容量切换点不正确。");
        foreach (long bytes in new[] { 46L, 96044, MonoPcmWriter.FileBytes(MonoPcmWriter.MaximumRiffFrames), FourGiB + 31, FourGiB + 4096 })
        {
            long frames = MonoPcmWriter.FramesFittingByteLimit(bytes);
            Need(MonoPcmWriter.FileBytes(frames) <= bytes && MonoPcmWriter.FileBytes(frames + 1) > bytes, "按实际44/80字节头预算不准确。");
        }
        long largeDuration = (long)(((Int128)((1L << 31) + 1024) * 10_000_000 + 47999) / 48000);
        Need(M2TimelineRenderer.FramesForDuration(largeDuration) > MonoPcmWriter.MaximumRiffFrames, "离线帧数仍在4GiB被拒绝。");
        Reject<NotSupportedException>(() => M2TimelineRenderer.FramesForDuration(86400L * 10_000_000 + 1));
        foreach (bool table in new[] { false, true })
        {
            string file = Path.Combine(root, table ? "valid-rf64-table.wav" : "valid-small-rf64.wav");
            byte[] pcm = [1, 0, 254, 255]; File.WriteAllBytes(file, Rf64(pcm, table));
            using var read = new M2WaveReader(file, Mono); byte[] actual = new byte[4];
            Need(read.IsRf64 && read.FrameCount == 2 && read.Read(actual) == 4 && actual.SequenceEqual(pcm) && read.Read(actual) == 0,
                "有效RF64或扩展chunk-size表不能读取至EOF。");
        }
        var corruptions = new (string Name, Action<byte[]> Change)[]
        {
            ("ds64-not-first", bytes => Put32(bytes, 12, 0x4b4e554a)),
            ("short-ds64", bytes => Put32(bytes, 16, 27)),
            ("wrong-riff64", bytes => Put64(bytes, 20, 1)),
            ("wrong-data64", bytes => Put64(bytes, 28, 6)),
            ("wrong-sample-count", bytes => Put64(bytes, 36, 1)),
            ("missing-table-entry", bytes => Put32(bytes, 44, 1)),
            ("non-sentinel-riff-conflict", bytes => Put32(bytes, 4, 1)),
            ("data32-conflict", bytes => Put32(bytes, 76, 2)),
            ("oversized-data", bytes => Put64(bytes, 28, ulong.MaxValue)),
            ("oversized-riff", bytes => Put64(bytes, 20, ulong.MaxValue)),
            ("invalid-format-rate", bytes => Put32(bytes, 60, 1))
        };
        foreach (var corruption in corruptions)
        {
            byte[] bytes = Rf64([1, 0, 2, 0], false); corruption.Change(bytes);
            string path = Path.Combine(root, corruption.Name + ".wav"); File.WriteAllBytes(path, bytes);
            RejectAny(() => { using var _ = new M2WaveReader(path); });
        }
        byte[] duplicate = Rf64([1, 0, 2, 0], false).Concat(new byte[] { 100, 97, 116, 97, 0, 0, 0, 0 }).ToArray();
        Put64(duplicate, 20, (ulong)duplicate.Length - 8);
        string duplicatePath = Path.Combine(root, "duplicate-data.wav"); File.WriteAllBytes(duplicatePath, duplicate);
        Reject<InvalidDataException>(() => { using var _ = new M2WaveReader(duplicatePath); });
        byte[] truncated = Rf64([1, 0, 2, 0], false)[..^1];
        string truncatedPath = Path.Combine(root, "truncated.wav"); File.WriteAllBytes(truncatedPath, truncated);
        Reject<InvalidDataException>(() => { using var _ = new M2WaveReader(truncatedPath); });
        byte[] orphan = Rf64([1, 0, 2, 0], true); Put32(orphan, 52, 0); // ds64 table size = 0 conflicts with actual sentinel JUNK payload
        string orphanPath = Path.Combine(root, "wrong-table-size.wav"); File.WriteAllBytes(orphanPath, orphan);
        RejectAny(() => { using var _ = new M2WaveReader(orphanPath); });
    }

    private static void LargeFile(string root)
    {
        string path = Path.Combine(root, "actual-over-4gib-sparse.wav");
        long frames = (1L << 31) + 1024, fileBytes = MonoPcmWriter.FileBytes(frames);
        const long dataOffset = 80;
        long physicalBoundaryFrame = (FourGiB - dataOffset) / 2, pcmBoundaryFrame = FourGiB / 2;
        var markers = new Dictionary<long, short>
        {
            [0] = 1234,
            [physicalBoundaryFrame - 1] = 2345, [physicalBoundaryFrame] = -2345, [physicalBoundaryFrame + 1] = 3456,
            [pcmBoundaryFrame - 1] = 4567, [pcmBoundaryFrame] = -4567, [pcmBoundaryFrame + 1] = 5678,
            [frames - 1] = -6789
        };
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            // Microsoft winioctl.h: CTL_CODE(FILE_DEVICE_FILE_SYSTEM=9,49,METHOD_BUFFERED=0,FILE_SPECIAL_ACCESS=0).
            // Only this newly generated fixture is marked sparse. If unsupported, fail before extending it.
            if (!DeviceIoControl(file.SafeFileHandle, (9u << 16) | (49u << 2), IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "测试卷不能建立稀疏文件；未创建4GiB数据。");
            Need((File.GetAttributes(path) & FileAttributes.SparseFile) != 0, "系统未确认稀疏属性，拒绝扩大文件。");
            using var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, true);
            MonoPcmWriter.WriteHeader(writer, frames); writer.Flush(); file.SetLength(fileBytes);
            foreach (var marker in markers) { file.Position = dataOffset + marker.Key * 2; writer.Write(marker.Value); }
            writer.Flush(); file.Flush(true);
        }
        long allocated = AllocatedBytes(path);
        Need(new FileInfo(path).Length == fileBytes && fileBytes > FourGiB && allocated <= 1024 * 1024,
            "没有实际>4GiB逻辑文件，或测试意外分配过多磁盘。");
        (long Start, int Count)[] ranges = [(0, 16), (physicalBoundaryFrame - 4, 12), (pcmBoundaryFrame - 4, 12), (frames - 16, 16)];
        var partEvidence = new List<object>();
        using (var read = new M2WaveReader(path, Mono))
        {
            Need(read.IsRf64 && read.FrameCount == frames && read.Length == frames * 2 && read.DataOffset == dataOffset,
                "64位frame/data长度被截断。");
            foreach (var marker in markers) Need(read.ReadFrames(marker.Key, 1)[0] == marker.Value / 32768f, "4GiB附近或末帧PCM不同。");
            for (int index = 0; index < ranges.Length; index++)
            {
                var range = ranges[index]; string part = Path.Combine(root, $"boundary-part-{index + 1}.wav");
                // Same bounded range routine used by production ExportParts, with a
                // real RF64 reader and offsets on both sides of 32-bit boundaries.
                M2ExportService.WriteWaveRange(read, part, range.Start, range.Count, CancellationToken.None);
                using var partReader = new M2WaveReader(part, Mono);
                var samples = partReader.ReadFrames(0, range.Count);
                for (int local = 0; local < range.Count; local++)
                    Need(samples[local] == markers.GetValueOrDefault(range.Start + local) / 32768f, "实际分片跨4GiB后错位或尾帧丢失。");
                partEvidence.Add(new { startFrame = range.Start, frameCount = range.Count,
                    byteLength = new FileInfo(part).Length, sha256 = M2FileSafety.Hash(part, CancellationToken.None) });
            }
            read.Seek(-2, SeekOrigin.End); byte[] tail = new byte[4];
            Need(read.Read(tail) == 2 && BinaryPrimitives.ReadInt16LittleEndian(tail) == -6789 && read.Read(tail) == 0, "EOF遗漏末帧或读到了data以外内容。");
            Reject<ArgumentOutOfRangeException>(() => read.Seek(1, SeekOrigin.End));
            Reject<InvalidDataException>(() => read.ReadFrames(frames, 1));
        }
        // Full-file hashes traverse the entire real logical length, not just the sparse extents.
        // Changing the last frame beyond 4GiB must affect the digest.
        string before;
        using (var held = File.OpenRead(path))
        {
            held.Position = FourGiB + 40; before = M2FileSafety.Hash(held, CancellationToken.None);
            Need(held.Position == FourGiB + 40, "64位hash游标没有恢复。");
        }
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        { file.Position = fileBytes - 2; file.Write(new byte[] { 33, 44 }); file.Flush(true); }
        string after = M2FileSafety.Hash(path, CancellationToken.None);
        Need(before != after, "完整hash忽略了4GiB之后的尾部变更。");
        Need(AllocatedBytes(path) <= 1024 * 1024, "测试写入意外展开了稀疏区。");
        File.WriteAllText(Path.Combine(root, "rf64-evidence.json"), JsonSerializer.Serialize(new
        {
            specification = "https://tech.ebu.ch/docs/tech/tech3306v1_1.pdf",
            sparseOperation = "https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_set_sparse",
            isSynthetic = true, actualLogicalBytes = fileBytes, actualAllocatedBytes = allocated,
            frameCount = frames, dataOffset, physicalBoundaryFrame, pcmBoundaryFrame,
            actualBoundaryParts = partEvidence, wholeFileSha256BeforeTailMutation = before,
            wholeFileSha256AfterTailMutation = after, fixtureRetainedAfterIntentionalTailMutation = true,
            fullLargeFileCompression = "NOT_RUN: separate codec streaming tests; this fixture verifies reader/range export/hash boundaries"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static byte[] CreateRf64Bytes(byte[] pcm) => Rf64(pcm, false);

    private static byte[] Rf64(byte[] pcm, bool table)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write(0x34364652u); writer.Write(uint.MaxValue); writer.Write(0x45564157u);
        writer.Write(0x34367364u); writer.Write(table ? 40u : 28u); writer.Write(0UL); writer.Write((ulong)pcm.Length);
        writer.Write((ulong)pcm.Length / 2); writer.Write(table ? 1u : 0u);
        if (table) { writer.Write(0x4b4e554au); writer.Write(4UL); }
        writer.Write(0x20746d66u); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(48000); writer.Write(96000); writer.Write((ushort)2); writer.Write((ushort)16);
        if (table) { writer.Write(0x4b4e554au); writer.Write(uint.MaxValue); writer.Write(0u); }
        writer.Write(0x61746164u); writer.Write(uint.MaxValue); writer.Write(pcm);
        long length = memory.Length; memory.Position = 20; writer.Write((ulong)(length - 8)); writer.Flush(); return memory.ToArray();
    }

    private static long AllocatedBytes(string path)
    {
        Marshal.SetLastPInvokeError(0); uint low = GetCompressedFileSizeW(path, out uint high);
        if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return checked((long)(((ulong)high << 32) | low));
    }
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Put64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
    private static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidDataException("应拒绝但操作完成：" + typeof(T).Name); }
    private static void RejectAny(Action action)
    { try { action(); } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException or EndOfStreamException) { return; } throw new InvalidDataException("损坏容器被接受。"); }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint controlCode, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string path, out uint high);
}
