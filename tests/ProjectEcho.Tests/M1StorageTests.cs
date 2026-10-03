using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

internal static class M1StorageTests
{
    private const ulong Epoch = 10_000_000_000;
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m1-storage"); Directory.CreateDirectory(root);
        run("T20/T22/T51/T53-SYNTH M1 30-second split preserves random packet tail and raw clocks", () => LongSplit(root));
        run("T15/T22/T52-SYNTH M1 segment restart retains format and actual boundary", () => SegmentRestart(root));
        run("T20/T53-SYNTH M1 excludes uncheckpointed tail and recovers immutable copies", () => CrashTail(root));
        run("T20/T53-SYNTH M1 refuses an uncheckpointed recording", () => NoCheckpoint(root));
        run("T53-SYNTH M1 rejects bad hashes duplicate properties sequence errors and packet bounds", () => CorruptIndex(root));
        run("T20/T53-SYNTH M1 rejects corrupt or truncated confirmed PCM", () => CorruptPcm(root));
        run("T50/T53-SYNTH M1 rejects escaped paths and ambiguous final/partial", () => UnsafePaths(root));
        run("T22/T53-SYNTH M1 verifies exact data token bytes and segment anchors", () => ExactBytes(root));
        run("T54-SYNTH M1 writer readiness prepares real WAV and retains no-data segment", () => NoDataReadiness(root));
        run("T50/T53-SYNTH M1 rejects actual Windows junction inside fixture", () => JunctionPath(root));
        run("T22/T53-SYNTH M1 rejects unfinished raw packet and unreported QPC gaps/overlaps", () => RawContinuity(root));
        run("T14/T54-SYNTH M1 preserves explicit no-callback gap and rejects overlap before PCM", () => DeclaredGap(root));
        run("T14/T53/T54-SYNTH M1 checkpoint preserves paused and empty elapsed timeline", () => ObservedTimeline(root));
        run("T20/T53-SYNTH M1 recovery preserves verified bookmarks and excludes event tails", () => RecoveryEvents(root));
        run("T20/T53-SYNTH M1 manifest cannot enlarge confirmed time or promote future bookmarks", () => ManifestTimeAuthority(root));
        run("T20/T53-SYNTH M1 malformed manifest numbers cannot block checkpoint recovery", () => ManifestNumericFailure(root));
        run("T22/T53-SYNTH M1 rejects packet clocks before the session epoch", () => BeforeSessionEpoch(root));
        run("T34-SYNTH cancelled source verification preserves originals and remains retryable", () =>
        {
            string path = Small(root, "cancel-source-verification");
            var before = Fingerprints(path);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Reject<OperationCanceledException>(() => M1Recovery.Read(path, cancel.Token));
            Need(Fingerprints(path).OrderBy(x => x.Key).SequenceEqual(before.OrderBy(x => x.Key)), "取消校验更改了原件。");
            Need(M1Recovery.Read(path).IsCleanEnd, "取消校验损坏后续重试。");
        });
    }

    private static void LongSplit(string root)
    {
        string path = Path.Combine(root, "long-split"); const int rate = 8000, frames = rate * 61 + 173;
        var expected = new byte[frames * 4];
        for (int i = 0; i < frames; i++) BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(i * 4), (i % 1000) / 2000f);
        using (var storage = new M1Storage(path, Epoch, DateTimeOffset.UtcNow, "合成 61 秒跨块", true))
        {
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
            var random = new Random(53151); int sent = 0;
            while (sent < frames)
            {
                int count = Math.Min(random.Next(1, 2049), frames - sent);
                storage.WritePacket("remote-1", expected.AsSpan(sent * 4, count * 4).ToArray(), Packet(sent, count, rate)); sent += count;
            }
            storage.WriteBoundary("remote-1", "stop", "stop", End(frames, rate), (ulong)frames, 0, null);
            var result = storage.Finish((long)(End(frames, rate) - Epoch), true, []);
            Need(result.StorageComplete && result.Chunks.Count == 3, "61秒应保存3个完整块。");
            Need(result.Chunks[0].FrameCount == rate * 30 && result.Chunks[1].FrameCount == rate * 30 && result.Chunks[2].FrameCount == rate + 173,
                "30秒块边界或最后173帧丢失。");
        }
        var disk = M1Recovery.Read(path);
        Need(disk.IsCleanEnd, string.Join(" | ", disk.Issues));
        Need(disk.Capture.Tracks.Sum(t => t.FrameCount) == frames, "离线恢复帧数不匹配。");
        Need(disk.Capture.Tracks.SelectMany(Pcm).SequenceEqual(expected), "跨块 PCM 不同或尾帧丢失。");
        var packets = disk.Capture.Tracks.SelectMany(t => t.Packets).ToArray();
        Need(packets.Any(p => p.RetainedFrameOffset > 0), "随机包没有覆盖分块拆包。");
        foreach (var p in packets)
            Need(p.QpcPosition100ns == Epoch + p.DevicePosition * 10_000_000 / rate && p.RetainedFrameOffset + p.FrameCount <= p.RawFrameCount,
                "原始QPC/device或保留偏移被改写。");
    }

    private static void SegmentRestart(string root)
    {
        string path = Path.Combine(root, "restart");
        using (var storage = new M1Storage(path, Epoch, DateTimeOffset.UtcNow, "暂停后更换格式合成", true))
        {
            storage.AddSegment("remote-1", "remote", new WaveFormat(8000, 16, 1));
            storage.WritePacket("remote-1", new byte[320], Packet(0, 160, 8000));
            storage.WriteBoundary("remote-1", "pause-1", "pause", End(160, 8000), 160, 0, null);
            storage.WriteEvent("pause", "pause-1", 20000);
            Reject<InvalidDataException>(() => storage.WritePacket("remote-1", new byte[2], Packet(160, 1, 8000)));
            storage.WriteEvent("resume", "resume-1", 1_000_000);
            storage.AddSegment("remote-2", "remote", new WaveFormatExtensible(44100, 32, 2, false, 24, 3));
            var second = new CapturedPacket(0, 101, 106, 77000, Epoch + 10_000_000, 0, true);
            storage.WritePacket("remote-2", new byte[808], second);
            ulong cutoff = second.QpcPosition100ns + (ulong)((101L * 10_000_000 + 44099) / 44100);
            storage.WriteBoundary("remote-2", "stop-1", "stop", cutoff, 77101, 5, "after_boundary");
            storage.Finish((long)(cutoff - Epoch), true, []);
        }
        var result = M1Recovery.Read(path);
        Need(result.IsCleanEnd && result.Capture.Tracks.Count == 2, string.Join(" | ", result.Issues));
        Need(result.Capture.Tracks[1].Format.SampleRate == 44100 && result.Capture.Tracks[1].Packets[0].RawFrameCount == 106, "恢复丢失实际格式/原始帧数。");
        Need(!Directory.Exists(Path.Combine(path, "sources", "mic")), "mic未启用仍创建mic目录。");
    }

    private static void CrashTail(string root)
    {
        string path = Path.Combine(root, "crash-tail");
        using (var storage = new M1Storage(path, Epoch, DateTimeOffset.UtcNow, "checkpoint尾部合成", true))
        {
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
            storage.WritePacket("remote-1", new byte[640], Packet(0, 160, 8000)); storage.Checkpoint();
            storage.WritePacket("remote-1", new byte[640], Packet(160, 160, 8000));
        }
        File.AppendAllText(Path.Combine(path, "journal", "capture-index.jsonl"), "{\"sequence\":", new UTF8Encoding(false));
        var before = Fingerprints(path);
        var recovered = M1Recovery.Read(path);
        Need(recovered.HasCheckpoint && !recovered.IsCleanEnd && recovered.Capture.Tracks.Single().FrameCount == 160, "未确认尾部被默认恢复。");
        var copy = M1Recovery.RecoverTo(path, Path.Combine(root, "crash-recovered"));
        Need(copy.Tracks.Single().FrameCount == 160 && !copy.Complete, "恢复副本错误地宣称完整。");
        Need(Fingerprints(path).OrderBy(x => x.Key).SequenceEqual(before.OrderBy(x => x.Key)), "恢复修改了原件。");
        Need(Pcm(copy.Tracks.Single()).Length == 640, "恢复文件未裁至已确认水位。");
    }

    private static void NoCheckpoint(string root)
    {
        string path = Path.Combine(root, "no-checkpoint");
        using (var storage = new M1Storage(path, Epoch, DateTimeOffset.UtcNow, "无checkpoint合成", true))
        {
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
            storage.WritePacket("remote-1", new byte[4], Packet(0, 1, 8000));
        }
        var result = M1Recovery.Read(path);
        Need(!result.HasCheckpoint && result.Capture.Tracks.Count == 0, "无checkpoint音频被当成已保存。");
    }

    private static void CorruptIndex(string root)
    {
        foreach (string test in new[] { "bad-hash", "duplicate-property", "wrong-sequence", "out-of-range", "watermark", "overlap", "forbidden-frame" })
        {
            string path = Small(root, test); var lines = File.ReadAllLines(Index(path));
            int packetLine = Array.FindIndex(lines, l => Type(l) == "packet");
            if (test == "bad-hash") lines[packetLine] = lines[packetLine].Replace("\"frameCount\":160", "\"frameCount\":159");
            else if (test == "duplicate-property") lines[packetLine] = lines[packetLine].Replace("\"sequence\":2", "\"sequence\":2,\"sequence\":2");
            else if (test == "wrong-sequence") lines[packetLine] = lines[packetLine].Replace("\"sequence\":2", "\"sequence\":20");
            else if (test == "out-of-range") lines[packetLine] = Mutate(lines[packetLine], d => d["retainedFrameOffset"] = 1);
            else if (test == "watermark") { int i = Array.FindIndex(lines, l => Type(l) == "checkpoint"); lines[i] = Mutate(lines[i], d => d["chunks"]![0]!["committedFrameCount"] = 999); }
            else if (test == "overlap")
            {
                lines[packetLine] = Mutate(lines[packetLine], d => d["chunkFrameOffset"] = 1);
            }
            else { int i = Array.FindIndex(lines, l => Type(l) == "boundary"); lines[i] = Mutate(lines[i], d => d["firstForbiddenDeviceFrame"] = "159"); }
            File.WriteAllLines(Index(path), lines, new UTF8Encoding(false));
            var result = M1Recovery.Read(path);
            Need(!result.IsCleanEnd && result.Issues.Count != 0, "接受非法索引：" + test);
        }
    }

    private static void CorruptPcm(string root)
    {
        foreach (bool truncate in new[] { false, true })
        {
            string path = Small(root, truncate ? "pcm-truncated" : "pcm-bitrot");
            string wav = Directory.EnumerateFiles(Path.Combine(path, "sources"), "*.wav", SearchOption.AllDirectories).Single();
            using (var file = new FileStream(wav, FileMode.Open, FileAccess.ReadWrite))
            {
                if (truncate) file.SetLength(file.Length - 1);
                else { file.Position = file.Length - 1; file.WriteByte(123); }
            }
            var result = M1Recovery.Read(path);
            Need(!result.IsCleanEnd && result.Capture.Tracks.Count == 0 && result.Issues.Any(x => x.Contains("未恢复")), "损坏PCM未显式报告损失。");
        }
    }

    private static void UnsafePaths(string root)
    {
        foreach (string unsafePath in new[] { "../escape.wav", "C:/escape.wav", "sources/a.wav:stream", "sources/../escape.wav", "sources/x./a.wav", "sources\\escape.wav" })
        {
            string path = Small(root, "unsafe-" + Guid.NewGuid().ToString("N")); var lines = File.ReadAllLines(Index(path));
            int packet = Array.FindIndex(lines, l => Type(l) == "packet");
            lines[packet] = Mutate(lines[packet], d => d["path"] = unsafePath); File.WriteAllLines(Index(path), lines, new UTF8Encoding(false));
            Need(!M1Recovery.Read(path).IsCleanEnd, "接受越界路径：" + unsafePath);
        }
        string ambiguous = Small(root, "ambiguous");
        string file = Directory.EnumerateFiles(Path.Combine(ambiguous, "sources"), "*.wav", SearchOption.AllDirectories).Single();
        File.Copy(file, file + ".partial");
        Need(!M1Recovery.Read(ambiguous).IsCleanEnd, "同时存在final/partial时猜测了原件。");
    }

    private static void ExactBytes(string root)
    {
        string path = Small(root, "exact-data-bytes"); var lines = File.ReadAllLines(Index(path));
        for (int i = 0; i < lines.Length; i++)
        {
            var doc = JsonNode.Parse(lines[i])!; string data = doc["data"]!.ToJsonString().Replace("\":", "\": ");
            lines[i] = Envelope(doc["sequence"]!.GetValue<long>(), data);
        }
        File.WriteAllLines(Index(path), lines, new UTF8Encoding(false));
        Need(M1Recovery.Read(path).IsCleanEnd, "精确字节hash的合法空白被重序列化误判。");
        int segment = Array.FindIndex(lines, l => Type(l) == "segment");
        lines[segment] = Mutate(lines[segment], d => d["anchor"]!["qpcPosition100ns"] = (Epoch + 1).ToString());
        File.WriteAllLines(Index(path), lines, new UTF8Encoding(false));
        Need(!M1Recovery.Read(path).IsCleanEnd, "错误segment锚点被接受。");
    }

    private static void NoDataReadiness(string root)
    {
        string path = Path.Combine(root, "no-data-ready");
        using (var storage = new M1Storage(path, Epoch, DateTimeOffset.UtcNow, "无callback合成", true))
        {
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
            string prepared = Path.Combine(path, "sources", "remote", "remote-1-000001.wav.partial");
            Need(File.Exists(prepared) && new FileInfo(prepared).Length >= 44, "writer就绪时实际WAV头尚未创建。");
            storage.Checkpoint();
            storage.WriteBoundary("remote-1", "stop", "stop", Epoch + 10_000_000, null, 0, null);
            storage.Finish(10_000_000, true, ["无callback，不代表静音"]);
        }
        var recovered = M1Recovery.Read(path);
        Need(recovered.IsCleanEnd && recovered.Capture.Tracks.Single().FrameCount == 0 && recovered.Capture.Tracks.Single().Packets.Count == 0,
            "无callback段没有可重建为空轨或被伪造静音。" + string.Join(" | ", recovered.Issues));
        string failed = Path.Combine(root, "readiness-failure");
        string conflict = Path.Combine(failed, "sources", "remote", "remote-1-000001.wav.partial");
        Directory.CreateDirectory(Path.GetDirectoryName(conflict)!); File.WriteAllBytes(conflict, [1, 2, 3]);
        using var writer = new M1Storage(failed, Epoch, DateTimeOffset.UtcNow, "就绪失败合成", true);
        Reject<IOException>(() => writer.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(8000, 1)));
        Need(File.ReadAllBytes(conflict).SequenceEqual(new byte[] { 1, 2, 3 }), "准备失败覆盖了原文件。");
    }

    private static void JunctionPath(string root)
    {
        string path = Small(root, "real-junction");
        string destination = Path.Combine(path, "sources", "remote"), junction = Path.Combine(path, "sources", "linked");
        // Both ends are newly generated synthetic fixture directories. Keep the junction
        // with ignored evidence; no recursive deletion or external target is involved.
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{junction.Replace("'", "''")}' -Target '{destination.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("测试junction创建进程未启动。");
        if (!process.WaitForExit(10000)) { process.Kill(); throw new TimeoutException("测试junction创建超时。"); }
        Need(process.ExitCode == 0, "junction创建失败（此项未通过）：" + process.StandardError.ReadToEnd());
        Need((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0, "测试没有建立真实重解析点。");
        var lines = File.ReadAllLines(Index(path)); int packet = Array.FindIndex(lines, l => Type(l) == "packet");
        lines[packet] = Mutate(lines[packet], d => d["path"] = "sources/linked/remote-1-000001.wav");
        File.WriteAllLines(Index(path), lines, new UTF8Encoding(false));
        var read = M1Recovery.Read(path);
        Need(!read.IsCleanEnd && read.Issues.Any(i => i.Contains("重解析点")), "reader未拒绝真实junction路径。");
    }

    private static void RawContinuity(string root)
    {
        foreach (string defect in new[] { "prefix", "qpc-gap", "qpc-overlap" })
        {
            string path = Path.Combine(root, defect);
            using (var storage = new M1Storage(path, Epoch, DateTimeOffset.UtcNow, "原始包连续性合成", true))
            {
                storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
                storage.WritePacket("remote-1", new byte[640], Packet(0, 160, 8000));
                storage.WritePacket("remote-1", new byte[640], Packet(160, 160, 8000));
                storage.WriteBoundary("remote-1", "stop", "stop", End(320, 8000), 320, 0, null);
                storage.Finish(400000, true, []);
            }
            var lines = File.ReadAllLines(Index(path));
            int first = Array.FindIndex(lines, l => Type(l) == "packet"), second = Array.FindIndex(lines, first + 1, l => Type(l) == "packet");
            if (defect == "prefix") lines[first] = Mutate(lines[first], d => d["rawFrameCount"] = 161);
            else
            {
                long delta = defect == "qpc-gap" ? 2501 : -2501; ulong qpc = (ulong)((long)Epoch + 200000 + delta);
                lines[second] = Mutate(lines[second], d => { d["qpcPosition100ns"] = qpc.ToString(); d["sessionStartUs"] = (long)((qpc - Epoch) / 10); });
            }
            File.WriteAllLines(Index(path), lines, new UTF8Encoding(false));
            var read = M1Recovery.Read(path);
            Need(!read.IsCleanEnd && read.Issues.Any(i => i.Contains(defect == "prefix" ? "被截断原包" : "QPC 范围")), "未报告原包异常被接受：" + defect);
        }
    }

    private static void DeclaredGap(string root)
    {
        string path = Path.Combine(root, "declared-no-callback-gap");
        using (var storage = new M1Storage(path, Epoch, DateTimeOffset.UtcNow, "无callback后继续合成", true))
        {
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
            storage.WritePacket("remote-1", new byte[640], Packet(0, 160, 8000));
            storage.WritePacket("remote-1", new byte[640], Packet(160, 160, 8000) with { QpcPosition100ns = Epoch + 10_000_000 });
            storage.WriteBoundary("remote-1", "stop", "stop", Epoch + 10_200_000, 320, 0, null);
            var result = storage.Finish(10_200_000, true, []);
            Need(result.StorageComplete && result.Warnings.Any(w => w.Contains("明确缺口")), "无callback gap被停止/当成静音/未警告。");
        }
        var recovered = M1Recovery.Read(path);
        Need(recovered.IsCleanEnd && recovered.Capture.Tracks.Single().FrameCount == 320 && recovered.Capture.Duration100ns == 10_200_000,
            "明确缺口不能从磁盘恢复。" + string.Join(" | ", recovered.Issues));
        var lines = File.ReadAllLines(Index(path));
        Need(lines.Any(l => l.Contains("\"gapBefore100ns\":\"9800000\"")), "缺口实际长度未保存。");

        string blocked = Path.Combine(root, "overlap-rejected-before-pcm");
        using (var storage = new M1Storage(blocked, Epoch, DateTimeOffset.UtcNow, "重叠拒写合成", true))
        {
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
            storage.WritePacket("remote-1", new byte[640], Packet(0, 160, 8000));
            // QPC-only remote packets have no independent contiguous-device
            // evidence; even a small >one-frame overlap must still be rejected.
            var invalid = Packet(160, 160, 8000) with { QpcPosition100ns = Epoch + 197499, DevicePositionValid = false };
            Reject<InvalidDataException>(() => storage.WritePacket("remote-1", new byte[640], invalid));
            storage.WriteBoundary("remote-1", "stop", "stop", End(160, 8000), 160, 0, null);
            Need(storage.Finish(200000, true, []).Chunks.Single().FrameCount == 160, "非法包在拒绝前已写入PCM。");
        }
        Need(M1Recovery.Read(blocked).IsCleanEnd, "被拒绝包破坏了已获准原件。");
    }

    private static void ObservedTimeline(string root)
    {
        string paused = Path.Combine(root, "paused-observed-crash");
        using (var storage = new M1Storage(paused, Epoch, DateTimeOffset.UtcNow, "暂停中崩溃合成", true))
        {
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
            storage.WritePacket("remote-1", new byte[640], Packet(0, 160, 8000));
            storage.WriteBoundary("remote-1", "pause", "pause", End(160, 8000), 160, 0, null);
            storage.SetObservedSessionUs(12_345_678); storage.Checkpoint();
        }
        var recovered = M1Recovery.Read(paused);
        Need(recovered.HasCheckpoint && !recovered.IsCleanEnd && recovered.Capture.Duration100ns == 123_456_780,
            "暂停时长被截到末包/暂停开始。");
        string empty = Path.Combine(root, "empty-observed-crash");
        using (var storage = new M1Storage(empty, Epoch, DateTimeOffset.UtcNow, "无callback经过时间合成", true))
        {
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
            storage.SetObservedSessionUs(4_567_890); storage.Checkpoint();
        }
        var noPcm = M1Recovery.RecoverTo(empty, Path.Combine(root, "empty-observed-recovered"));
        Need(noPcm.Duration100ns == 45_678_900 && noPcm.Tracks.Count == 0 && !noPcm.Complete,
            "没有PCM时不能恢复已确认经过时间。");

        string subUs = Path.Combine(root, "submicrosecond-stop");
        using (var storage = new M1Storage(subUs, Epoch, DateTimeOffset.UtcNow, "100ns精确Stop合成", true))
        {
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(44100, 1));
            storage.WritePacket("remote-1", new byte[4], Packet(0, 1, 44100));
            storage.WriteBoundary("remote-1", "stop", "stop", Epoch + 227, 1, 0, null);
            storage.Finish(227, true, []);
        }
        var sub = M1Recovery.Read(subUs);
        Need(sub.IsCleanEnd && sub.Capture.Duration100ns == 227, "观测us舍入把时间轴延长过精确Stop。" + string.Join(" | ", sub.Issues));
    }

    private static void RecoveryEvents(string root)
    {
        foreach (bool future in new[] { false, true })
        {
            string path = Path.Combine(root, future ? "events-future" : "events-bad-tail");
            using (var storage = new M1Storage(path, Epoch, DateTimeOffset.UtcNow, "书签恢复合成", true))
            {
                storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
                storage.WritePacket("remote-1", new byte[640], Packet(0, 160, 8000));
                storage.WriteBoundary("remote-1", "pause", "pause", End(160, 8000), 160, 0, null);
                storage.WriteEvent("pause", "pause", 20000);
                storage.WriteEvent("bookmark", "bookmark-1", 1_000_000);
                storage.SetObservedSessionUs(2_000_000); storage.Checkpoint();
                if (future) storage.WriteEvent("bookmark", "future-bookmark", 3_000_000);
            }
            string events = Path.Combine(path, "journal", "events.jsonl");
            if (!future) File.AppendAllText(events, "{\"schemaVersion\":1", new UTF8Encoding(false));
            var before = Fingerprints(path);
            var copy = M1Recovery.RecoverTo(path, Path.Combine(root, future ? "events-future-recovered" : "events-prefix-recovered"));
            var copied = File.ReadAllLines(Path.Combine(copy.SessionDirectory, "journal", "events.jsonl"));
            Need(copied.Length == 2 && copied[1].Contains("bookmark-1") && !copied.Any(l => l.Contains("future-bookmark")),
                "合法书签丢失或未确认事件被复制。");
            Need(copy.Warnings.Any(w => w.Contains("events")) && !copy.Complete, "事件尾部缺失没有明确报告。");
            Need(Fingerprints(path).OrderBy(p => p.Key).SequenceEqual(before.OrderBy(p => p.Key)), "恢复书签时修改原件。");
        }
    }

    private static void ManifestTimeAuthority(string root)
    {
        const long confirmed = 200001;
        foreach (string defect in new[] { "inflate", "late-hash", "late-warnings", "late-complete-type", "legacy-grow", "legacy-same" })
        {
            string path = Fixture("manifest-" + defect, true);
            string manifestPath = Path.Combine(path, "meeting.json"); var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            if (defect is "inflate" or "legacy-grow") manifest["duration100ns"] = 20_000_000;
            else if (defect != "legacy-same") manifest["duration100ns"] = confirmed + 9;
            manifest["warnings"] = new JsonArray("should-not-commit");
            if (defect == "late-hash") manifest["chunks"]![0]!["FileSha256"] = new string('0', 64);
            if (defect == "late-warnings") manifest["warnings"] = new JsonArray(123);
            if (defect == "late-complete-type") manifest["storageComplete"] = "true";
            File.WriteAllText(manifestPath, manifest.ToJsonString(), new UTF8Encoding(false));
            if (defect.StartsWith("legacy", StringComparison.Ordinal))
            {
                var lines = File.ReadAllLines(Index(path));
                for (int i = 0; i < lines.Length; i++)
                    if (Type(lines[i]) == "checkpoint") lines[i] = Mutate(lines[i], d => d.AsObject().Remove("observedSessionUs"));
                File.WriteAllLines(Index(path), lines, new UTF8Encoding(false));
            }
            var originals = Fingerprints(path);
            var read = M1Recovery.Read(path);
            Need(read.HasCheckpoint && !read.IsCleanEnd && read.Capture.Duration100ns == confirmed,
                "manifest污染了已确认经过时间：" + defect);
            if (defect != "legacy-same") Need(!read.Capture.Warnings.Contains("should-not-commit"), "未验证manifest的warnings提前提交。");
            if (defect.StartsWith("legacy", StringComparison.Ordinal))
                Need(read.Issues.Any(i => i.Contains("缺少observedSessionUs")), "旧checkpoint没有明确降级。");
            var recovered = M1Recovery.RecoverTo(path, Path.Combine(root, "manifest-recovered-" + defect));
            var events = File.ReadAllLines(Path.Combine(recovered.SessionDirectory, "journal", "events.jsonl"));
            Need(recovered.Duration100ns == confirmed && events.Length == 1 && events[0].Contains("confirmed-bookmark") &&
                !events.Any(l => l.Contains("future-bookmark")), "坏manifest使未来书签越过确认水位：" + defect);
            Need(Fingerprints(path).OrderBy(p => p.Key).SequenceEqual(originals.OrderBy(p => p.Key)), "manifest恢复改变了原件。");
        }
        foreach (int remainder in new[] { 0, 9, 10 })
        {
            string path = Fixture("manifest-remainder-" + remainder, false);
            string manifestPath = Path.Combine(path, "meeting.json"); var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            manifest["duration100ns"] = confirmed + remainder;
            File.WriteAllText(manifestPath, manifest.ToJsonString(), new UTF8Encoding(false));
            var read = M1Recovery.Read(path);
            Need(read.IsCleanEnd == (remainder <= 9) && read.Capture.Duration100ns == (remainder <= 9 ? confirmed + remainder : confirmed),
                "manifest 100ns取整余量边界错误：" + remainder);
        }

        string Fixture(string name, bool futureEvent)
        {
            string path = Path.Combine(root, name);
            using var storage = new M1Storage(path, Epoch, DateTimeOffset.UtcNow, "manifest时长权威合成", true);
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
            storage.WritePacket("remote-1", new byte[640], Packet(0, 160, 8000));
            storage.WriteBoundary("remote-1", "stop", "stop", Epoch + (ulong)confirmed, 160, 0, null);
            storage.WriteEvent("bookmark", "confirmed-bookmark", 10000);
            if (futureEvent) storage.WriteEvent("bookmark", "future-bookmark", 20001);
            storage.Finish(confirmed, true, []);
            return path;
        }
    }

    private static void ManifestNumericFailure(string root)
    {
        foreach (string field in new[] { "duration100ns", "finalCheckpointSequence", "chunkFrameCount", "formatSampleRate" })
        {
            string path = Small(root, "manifest-invalid-number-" + field);
            string manifestPath = Path.Combine(path, "meeting.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            if (field == "chunkFrameCount") manifest["chunks"]![0]!["FrameCount"] = JsonNode.Parse("1e80");
            else if (field == "formatSampleRate") manifest["chunks"]![0]!["Format"]!["SampleRate"] = JsonNode.Parse("1e80");
            else manifest[field] = JsonNode.Parse("1e80");
            File.WriteAllText(manifestPath, manifest.ToJsonString(), new UTF8Encoding(false));
            var originals = Fingerprints(path);
            var read = M1Recovery.Read(path);
            Need(read.HasCheckpoint && !read.IsCleanEnd && read.Capture.Tracks.Single().FrameCount == 160 &&
                read.Capture.Duration100ns == 200000 && read.Issues.Any(i => i.Contains("manifest 验证失败")),
                "非法manifest数字使已确认PCM无法恢复或被标完整：" + field);
            var recovered = M1Recovery.RecoverTo(path, Path.Combine(root, "manifest-invalid-number-copy-" + field));
            Need(!recovered.Complete && recovered.Tracks.Single().FrameCount == 160 && Pcm(recovered.Tracks.Single()).Length == 640,
                "非法manifest数字阻塞恢复副本：" + field);
            Need(Fingerprints(path).OrderBy(p => p.Key).SequenceEqual(originals.OrderBy(p => p.Key)), "恢复非法manifest时改变原件。");
        }
    }

    private static void BeforeSessionEpoch(string root)
    {
        string writerPath = Path.Combine(root, "before-epoch-write");
        using (var storage = new M1Storage(writerPath, Epoch, DateTimeOffset.UtcNow, "早于会话起点合成", true))
        {
            Reject<InvalidDataException>(() => storage.AddSegment("invalid-anchor", "remote", WaveFormat.CreateIeeeFloatWaveFormat(8000, 1),
                Packet(0, 160, 8000) with { QpcPosition100ns = Epoch - 200000 }));
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
            Reject<InvalidDataException>(() => storage.WritePacket("remote-1", new byte[640],
                Packet(0, 160, 8000) with { QpcPosition100ns = Epoch - 200000 }));
            storage.WriteBoundary("remote-1", "stop", "stop", Epoch, null, 0, null);
            Need(storage.Finish(0, true, []).Chunks.Single().FrameCount == 0, "拒绝早于epoch的包之前已保存PCM。");
        }
        string readerPath = Small(root, "before-epoch-index");
        var lines = File.ReadAllLines(Index(readerPath));
        for (int i = 0; i < lines.Length; i++)
        {
            if (Type(lines[i]) == "segment") lines[i] = Mutate(lines[i], d => d["anchor"]!["qpcPosition100ns"] = (Epoch - 200000).ToString());
            else if (Type(lines[i]) == "packet") lines[i] = Mutate(lines[i], d => d["qpcPosition100ns"] = (Epoch - 200000).ToString());
        }
        File.WriteAllLines(Index(readerPath), lines, new UTF8Encoding(false));
        var read = M1Recovery.Read(readerPath);
        Need(!read.IsCleanEnd && read.Capture.Tracks.Count == 0 && read.Issues.Any(i => i.Contains("起点")),
            "早于session epoch的有效包被钳为0并伪装完整。");
    }

    private static string Small(string root, string name)
    {
        string path = Path.Combine(root, name);
        using var storage = new M1Storage(path, Epoch, DateTimeOffset.UtcNow, "损坏注入合成", true);
        storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
        storage.WritePacket("remote-1", new byte[640], Packet(0, 160, 8000));
        storage.WriteBoundary("remote-1", "stop", "stop", End(160, 8000), 160, 0, null);
        storage.Finish(200000, true, []); return path;
    }
    private static CapturedPacket Packet(long offset, int frames, int rate) => new(offset, frames, frames, (ulong)offset,
        Epoch + (ulong)(offset * 10_000_000 / rate), 0, true);
    private static ulong End(long frames, int rate) => Epoch + (ulong)((frames * 10_000_000 + rate - 1) / rate);
    private static string Index(string directory) => Path.Combine(directory, "journal", "capture-index.jsonl");
    private static string Type(string line) { using var doc = JsonDocument.Parse(line); return doc.RootElement.GetProperty("data").GetProperty("type").GetString()!; }
    private static string Mutate(string line, Action<JsonNode> mutation)
    {
        var node = JsonNode.Parse(line)!; mutation(node["data"]!);
        return Envelope(node["sequence"]!.GetValue<long>(), node["data"]!.ToJsonString());
    }
    private static string Envelope(long sequence, string data) => $"{{\"schemaVersion\":1,\"isSynthetic\":true,\"sequence\":{sequence},\"data\":{data},\"sha256\":\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data)))}\"}}";
    private static byte[] Pcm(CapturedTrack track)
    {
        using var reader = new WaveFileReader(track.WavePath); var result = new byte[reader.Length];
        Need(reader.Read(result, 0, result.Length) == result.Length, "WAV读取不完整。"); return result;
    }
    private static Dictionary<string, string> Fingerprints(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(directory, p), p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))));
    private static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidDataException("预期拒绝未发生。"); }
}
