using System.Buffers.Binary;

namespace ProjectEcho.Export;

public sealed record Mp3FrameTimeline(int SampleRate, int Channels, int BitsPerSecond,
    long FrameCount, long EncodedPcmFrames, long AudioBytes, long DecoderTrimFrames)
{
    public int? CalibratedDelayFrames { get; init; }
    public double? CalibrationCorrelation { get; init; }
}

/// <summary>Strict MPEG-1 Layer III frame walk for our tag-free, constant-rate mono output.</summary>
public static class Mp3FrameProbe
{
    public const int SamplesPerFrame = 1152;
    public const int MediaFoundationDecoderTrimFrames = 528;

    public static Mp3FrameTimeline Inspect(string path, CancellationToken cancellationToken = default)
    {
        using var input = M2FileSafety.LockedRead(path);
        Span<byte> header = stackalloc byte[4];
        long frames = 0; int sampleRate = 0, bitrate = 0;
        int[] bitrates = [0, 32000, 40000, 48000, 56000, 64000, 80000, 96000, 112000, 128000, 160000, 192000, 224000, 256000, 320000];
        int[] rates = [44100, 48000, 32000];
        while (input.Position < input.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long start = input.Position;
            if (input.Length - start < 4) throw new InvalidDataException("MP3 尾部含不完整帧头。");
            input.ReadExactly(header); uint value = BinaryPrimitives.ReadUInt32BigEndian(header);
            int rateIndex = (int)(value >> 10 & 3), bitIndex = (int)(value >> 12 & 15);
            if ((value & 0xFFE00000) != 0xFFE00000 || (value >> 19 & 3) != 3 || (value >> 17 & 3) != 1 ||
                (value >> 6 & 3) != 3 || rateIndex == 3 || bitIndex is 0 or 15)
                throw new InvalidDataException("MP3 含未验证的 MPEG 版本/声道/码率或额外标签。");
            int currentRate = rates[rateIndex], currentBitrate = bitrates[bitIndex];
            if (frames > 0 && (currentRate != sampleRate || currentBitrate != bitrate))
                throw new InvalidDataException("MP3 帧格式在文件中发生变化。");
            sampleRate = currentRate; bitrate = currentBitrate;
            int bytes = 144 * bitrate / sampleRate + (int)(value >> 9 & 1);
            if (bytes < 4 || input.Length - start < bytes) throw new InvalidDataException("MP3 尾部音频帧被截断。");
            input.Position = checked(start + bytes); frames++;
        }
        if (frames == 0) throw new InvalidDataException("MP3 不包含音频帧。");
        return new(sampleRate, 1, bitrate, frames, checked(frames * SamplesPerFrame), input.Length, MediaFoundationDecoderTrimFrames);
    }

    public static void ValidateAgainstDecoded(Mp3FrameTimeline timeline, long decodedFrames, int sampleRate)
    {
        if (timeline.SampleRate != sampleRate || timeline.Channels != 1 || timeline.FrameCount <= 0 ||
            timeline.EncodedPcmFrames != checked(timeline.FrameCount * SamplesPerFrame) || timeline.AudioBytes <= 0 ||
            timeline.DecoderTrimFrames != MediaFoundationDecoderTrimFrames ||
            timeline.EncodedPcmFrames - timeline.DecoderTrimFrames != decodedFrames)
            throw new InvalidDataException("MP3 完整帧扫描与实际 EOF 解码长度不一致。");
    }

    internal static void CopyWithoutGeneratedTags(string source, string target, CancellationToken token)
    {
        using var input = M2FileSafety.LockedRead(source);
        Span<byte> header = stackalloc byte[10]; input.ReadExactly(header);
        if (header[..3].SequenceEqual("ID3"u8))
        {
            if (header[3] is not (3 or 4) || header[4] != 0 || header[5] != 0 ||
                header[6] >= 128 || header[7] >= 128 || header[8] >= 128 || header[9] >= 128)
                throw new InvalidDataException("Windows MP3 编码器生成了未验证的 ID3 头。");
            long offset = 10 + ((long)header[6] << 21 | (long)header[7] << 14 | (long)header[8] << 7 | header[9]);
            if (offset >= input.Length) throw new InvalidDataException("MP3 标签长度超出文件。");
            input.Position = offset;
        }
        else input.Position = 0;
        // The Windows sink emits iTunSMPB values that can underflow for short
        // inputs. Keep the untouched MPEG audio frames; do not publish misleading
        // gapless metadata or user/machine tags. Delay/padding lives in our manifest.
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[65536]; int count;
        while ((count = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
        output.Flush(true);
    }
}
