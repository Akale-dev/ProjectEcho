using System.Buffers.Binary;
using System.Text;

namespace ProjectEcho.Core.Transcription;

public sealed record QwenWindowAudioFile(string Path, string Sha256, long Frames, bool IsDigitalSilence);

/// <summary>
/// Keeps a read lock on one hash-verified complete PCM input and extracts bounded windows with a 64 KiB
/// buffer. No resampling, silence removal, source concatenation or timing inference occurs here.
/// </summary>
public sealed class QwenWindowAudio : IDisposable
{
    private readonly FileStream source;
    private readonly long dataOffset;
    private bool disposed;
    public string InputSha256 { get; }
    public long FrameCount { get; }

    public QwenWindowAudio(string path, string expectedSha256, CancellationToken cancellationToken = default)
    {
        if (!TranscriptFiles.IsHash(expectedSha256)) throw new InvalidDataException("Qwen 完整音频缺少有效 SHA-256。");
        source = TranscriptFiles.ReadLock(path);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            InputSha256 = TranscriptFiles.Hash(source, cancellationToken);
            if (!string.Equals(InputSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Qwen 完整音频哈希已改变。");
            (dataOffset, FrameCount) = ReadHeader(source, cancellationToken);
        }
        catch { source.Dispose(); throw; }
    }

    public QwenWindowAudioFile WriteWindow(QwenWindowPlan plan, QwenWindow window, string attemptDirectory,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        plan.ValidateWindow(window);
        if (plan.TotalFrames != FrameCount) throw new InvalidDataException("窗口计划与实际完整音频帧数不同。");
        cancellationToken.ThrowIfCancellationRequested();
        string root = TranscriptFiles.LocalPath(attemptDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Qwen 窗口只写入已有的独立尝试目录。");
        string path = TranscriptFiles.Inside(root, $"window-{window.Index:D6}.wav");
        string partial = TranscriptFiles.Inside(root, $"window-{window.Index:D6}-{Guid.NewGuid():N}.partial");
        bool digitalSilence = true;
        try
        {
            // CreateNew and non-overwriting Move preserve previous attempts, including partial outputs.
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(Header(window.InputFrameCount));
                source.Position = checked(dataOffset + window.InputStartFrame * 2);
                long remainingBytes = (long)window.InputFrameCount * 2;
                var buffer = new byte[65536];
                while (remainingBytes > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = checked((int)Math.Min(buffer.Length, remainingBytes));
                    source.ReadExactly(buffer.AsSpan(0, count));
                    if (digitalSilence && buffer.AsSpan(0, count).ContainsAnyExcept((byte)0)) digitalSilence = false;
                    output.Write(buffer, 0, count);
                    remainingBytes -= count;
                }
                output.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            string hash;
            using (var verification = TranscriptFiles.ReadLock(partial))
            {
                var format = ReadHeader(verification, cancellationToken);
                if (format.Frames != window.InputFrameCount) throw new InvalidDataException("Qwen 窗口尾帧校验失败。");
                hash = TranscriptFiles.Hash(verification, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, path, false);
            return new(path, hash, window.InputFrameCount, digitalSilence);
        }
        catch
        {
            // A uniquely named partial stays in this attempt for diagnosis; it is never a successful WAV.
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        source.Dispose();
        disposed = true;
    }

    private static byte[] Header(int frames)
    {
        var result = new byte[44];
        "RIFF"u8.CopyTo(result); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), checked((uint)(36L + frames * 2L)));
        "WAVEfmt "u8.CopyTo(result.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24), QwenWindowPlan.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28), QwenWindowPlan.SampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(32), 2); BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(34), 16);
        "data"u8.CopyTo(result.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40), checked((uint)(frames * 2L)));
        return result;
    }

    private static (long Offset, long Frames) ReadHeader(Stream file, CancellationToken token)
    {
        file.Position = 0;
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        if (file.Length < 44) throw new InvalidDataException("Qwen 输入 WAV 不完整。");
        string container = ReadTag(reader);
        uint riffBytes = reader.ReadUInt32();
        if (container is not ("RIFF" or "RF64") || ReadTag(reader) != "WAVE" ||
            container == "RIFF" && riffBytes + 8L != file.Length || container == "RF64" && riffBytes != uint.MaxValue)
            throw new InvalidDataException("Qwen 输入必须是完整本地 RIFF/RF64 WAV。");
        bool hasFormat = false, hasDs64 = false;
        long data = -1, bytes = -1, rf64Data = -1, rf64Frames = -1;
        int chunks = 0;
        while (file.Position < file.Length)
        {
            token.ThrowIfCancellationRequested();
            if (++chunks > 10000 || file.Length - file.Position < 8) throw new InvalidDataException("WAV 分块不完整或数量超限。");
            string kind = ReadTag(reader);
            uint chunkBytes = reader.ReadUInt32();
            long length = chunkBytes;
            if (chunkBytes == uint.MaxValue)
            {
                if (container != "RF64" || kind != "data" || !hasDs64) throw new InvalidDataException("RF64 缺少可核验的数据大小。");
                length = rf64Data;
            }
            long start = file.Position;
            if (length < 0 || length > file.Length - start || (length & 1) != 0 && length == file.Length - start)
                throw new InvalidDataException("WAV 数据块超过文件边界或缺少填充。");
            long end = checked(start + length + (length & 1));
            if (kind == "ds64")
            {
                if (container != "RF64" || hasDs64 || length < 28) throw new InvalidDataException("RF64 大小块无效。");
                ulong full = reader.ReadUInt64(), audio = reader.ReadUInt64(), frames = reader.ReadUInt64();
                uint entries = reader.ReadUInt32();
                if (full != (ulong)(file.Length - 8) || audio > long.MaxValue || frames > long.MaxValue ||
                    entries != 0 || length != 28)
                    throw new InvalidDataException("RF64 大小声明不匹配或带有不支持的额外大数据块。");
                rf64Data = (long)audio; rf64Frames = (long)frames; hasDs64 = true;
            }
            else if (kind == "fmt ")
            {
                if (hasFormat || length < 16) throw new InvalidDataException("WAV 格式块重复或不完整。");
                ushort encoding = reader.ReadUInt16(), channels = reader.ReadUInt16();
                uint rate = reader.ReadUInt32(), byteRate = reader.ReadUInt32();
                ushort alignment = reader.ReadUInt16(), bits = reader.ReadUInt16();
                if (encoding != 1 || channels != 1 || rate != QwenWindowPlan.SampleRate ||
                    byteRate != QwenWindowPlan.SampleRate * 2 || alignment != 2 || bits != 16)
                    throw new InvalidDataException("Qwen 分窗只接收已准备的 16 kHz 单声道 PCM16 完整 WAV。");
                hasFormat = true;
            }
            else if (kind == "data")
            {
                if (data >= 0 || length < 2 || length % 2 != 0) throw new InvalidDataException("WAV 数据为空、重复或不是完整帧。");
                data = start; bytes = length;
                if (container == "RF64" && (!hasDs64 || rf64Data != length || rf64Frames != length / 2))
                    throw new InvalidDataException("RF64 音频帧数声明不匹配。");
            }
            file.Position = end;
        }
        if (!hasFormat || data < 0 || container == "RF64" && !hasDs64 ||
            bytes / 2 > 7L * 24 * 60 * 60 * QwenWindowPlan.SampleRate)
            throw new InvalidDataException("WAV 缺少格式或数据，或超过七天限制。");
        return (data, bytes / 2);
    }

    private static string ReadTag(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        if (bytes.Length != 4) throw new InvalidDataException("WAV 标识不完整。");
        return Encoding.ASCII.GetString(bytes);
    }
}
