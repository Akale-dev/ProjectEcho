using System.Buffers.Binary;
using ProjectEcho.Core;

namespace ProjectEcho.Export;

public sealed record RenderResult(string Path, int SampleRate, int Channels, long FrameCount, IReadOnlyList<string> Warnings);

/// <summary>
/// M0 short-probe renderer: aligns actual packet QPC timestamps and mixes simultaneous samples.
/// This deliberately does not claim long-run clock-drift correction or M2 export acceptance.
/// </summary>
public static class TimelineWaveRenderer
{
    public const int OutputSampleRate = 48_000;
    public const int OutputChannels = 2;
    public const int MaximumSeconds = 60;

    public static RenderResult Render(CaptureResult result, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (result.TimelinePolicy != RecordingTimeline.Elapsed || result.Tracks.Any(track => track.ExcludedPause100ns != 0))
            throw new NotSupportedException("M0短试录混音不处理暂停排除时间轴；请使用完整会议混音。");
        if (result.Tracks.Count is < 1 or > 2) throw new NotSupportedException("M0 混音只接收一或两路原轨。");
        if (result.Duration100ns < 0 || result.Duration100ns > MaximumSeconds * AudioClock.UnitsPerSecond)
            throw new NotSupportedException("M0 混音只用于不超过 60 秒的主动短试录。");

        var target = System.IO.Path.GetFullPath(destinationPath);
        if (result.Tracks.Any(t => string.Equals(System.IO.Path.GetFullPath(t.WavePath), target, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("混音不能覆盖原始来源轨。");
        if (File.Exists(target)) throw new IOException("混音目标已存在；不会覆盖已有文件。");

        var warnings = new List<string>(result.Warnings)
        {
            "M0 逐包 QPC 对齐及线性重采样；长录音漂移修正尚未验证。"
        };
        if (!result.Complete) warnings.Add("原始录音未完整完成，当前混音仅包含可读取的数据。");
        long outputFrames = checked((long)(((Int128)result.Duration100ns * OutputSampleRate + AudioClock.UnitsPerSecond - 1) / AudioClock.UnitsPerSecond));

        foreach (var track in result.Tracks)
        {
            track.Format.Validate();
            if (track.Format.Channels > 2) throw new NotSupportedException("M0 混音只支持单/双声道原轨，其他布局保留原件并报告限制。");
            long nextSourceFrame = 0;
            long previousOutputEnd = long.MinValue;
            foreach (var packet in track.Packets)
            {
                if (!packet.TimestampValid || (packet.Flags & 4) != 0)
                    throw new InvalidDataException("存在无效的 API 时间戳，不能声称已精确对齐；原轨保持不变。");
                if (packet.FrameCount <= 0 || packet.RawFrameCount < packet.FrameCount || packet.WaveFrameOffset != nextSourceFrame)
                    throw new InvalidDataException("原轨 packet 帧数/连续存储偏移无效。");
                nextSourceFrame = checked(nextSourceFrame + packet.FrameCount);
                var start = AudioClock.FrameAt(packet.QpcPosition100ns, result.EpochQpc100ns, OutputSampleRate);
                var end = PacketEndFrame(packet, result.EpochQpc100ns, track.Format.SampleRate);
                // Clock jumps or ambiguous overlaps must be surfaced, not silently added twice.
                if (start < previousOutputEnd)
                    throw new InvalidDataException("同一路 packet 时间范围重叠，M0 混音拒绝隐藏时钟异常。");
                previousOutputEnd = end;
                outputFrames = Math.Max(outputFrames, previousOutputEnd);
            }
            if (nextSourceFrame != track.FrameCount) throw new InvalidDataException("原轨总帧数与 packet 索引不一致。");
            if (track.FrameCount == 0) warnings.Add($"来源 {track.SourceId} 未收到音频包；其时间范围为空缺，不能判作已采集的静音。");
        }

        if (outputFrames <= 0 || outputFrames > (long)MaximumSeconds * OutputSampleRate)
            throw new InvalidDataException("录音时间轴为空、异常或超出 M0 短试录范围。");

        var mix = new float[checked((int)outputFrames * OutputChannels)];
        var gain = 1f / result.Tracks.Count; // Fixed headroom for two sources, no time-varying normalization.
        foreach (var track in result.Tracks)
        {
            using var reader = new PcmWaveReader(track.WavePath, track.Format);
            if (reader.FrameCount != track.FrameCount) throw new InvalidDataException("WAV 数据长度与原轨帧数不一致。");
            foreach (var packet in track.Packets)
            {
                var samples = reader.ReadFrames(packet.WaveFrameOffset, packet.FrameCount);
                var start = AudioClock.FrameAt(packet.QpcPosition100ns, result.EpochQpc100ns, OutputSampleRate);
                var count = checked((int)(PacketEndFrame(packet, result.EpochQpc100ns, track.Format.SampleRate) - start));
                var exactStartFrame = (double)((Int128)packet.QpcPosition100ns - result.EpochQpc100ns) * OutputSampleRate / AudioClock.UnitsPerSecond;
                for (var localFrame = 0; localFrame < count; localFrame++)
                {
                    var outputFrame = start + localFrame;
                    if (outputFrame < 0 || outputFrame >= outputFrames) continue;
                    var position = Math.Clamp((outputFrame - exactStartFrame) * track.Format.SampleRate / OutputSampleRate, 0, packet.FrameCount - 1d);
                    var left = Math.Min((int)position, packet.FrameCount - 1);
                    var right = Math.Min(left + 1, packet.FrameCount - 1);
                    var fraction = (float)(position - left);
                    for (var channel = 0; channel < OutputChannels; channel++)
                    {
                        var sourceChannel = track.Format.Channels == 1 ? 0 : channel;
                        var a = samples[left * track.Format.Channels + sourceChannel];
                        var b = samples[right * track.Format.Channels + sourceChannel];
                        mix[checked((int)outputFrame * OutputChannels + channel)] += (a + (b - a) * fraction) * gain;
                    }
                }
            }
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        var partial = target + ".partial";
        WritePcm16Wave(partial, mix);
        File.Move(partial, target, false);
        return new RenderResult(target, OutputSampleRate, OutputChannels, outputFrames, warnings);
    }

    private static long PacketEndFrame(CapturedPacket packet, ulong epoch, int inputRate)
    {
        var numerator = (((Int128)packet.QpcPosition100ns - epoch) * inputRate + (Int128)packet.FrameCount * AudioClock.UnitsPerSecond) * OutputSampleRate;
        var denominator = (Int128)AudioClock.UnitsPerSecond * inputRate;
        var rounded = numerator >= 0 ? numerator + denominator / 2 : numerator - denominator / 2;
        return checked((long)(rounded / denominator));
    }

    private static void WritePcm16Wave(string path, float[] samples)
    {
        var byteCount = checked(samples.Length * sizeof(short));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(checked(byteCount + 36));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)OutputChannels);
        writer.Write(OutputSampleRate);
        writer.Write(OutputSampleRate * OutputChannels * sizeof(short));
        writer.Write((short)(OutputChannels * sizeof(short)));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(byteCount);
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample)) throw new InvalidDataException("混音数据含无效浮点样本。");
            writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
        }
        writer.Flush();
        stream.Flush(true);
    }
}

internal sealed class PcmWaveReader : IDisposable
{
    private readonly FileStream stream;
    private readonly PcmFormat format;
    private long dataOffset;
    public long FrameCount { get; private set; }

    public PcmWaveReader(string path, PcmFormat expected)
    {
        format = expected;
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { ReadHeader(); }
        catch { stream.Dispose(); throw; }
    }

    private void ReadHeader()
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        if (reader.ReadUInt32() != 0x46464952) throw new InvalidDataException("只支持 RIFF WAV 原轨。");
        var riffBytes = reader.ReadUInt32();
        if ((long)riffBytes + 8 != stream.Length || reader.ReadUInt32() != 0x45564157)
            throw new InvalidDataException("WAV 容器长度或标记无效。");
        bool foundFormat = false, foundData = false;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = reader.ReadUInt32();
            var size = reader.ReadUInt32();
            var payload = stream.Position;
            var end = checked(payload + size);
            if (end > stream.Length) throw new InvalidDataException("WAV chunk 超出文件末尾。");
            if (id == 0x20746D66)
            {
                if (foundFormat || size < 16 || size > 1024) throw new InvalidDataException("WAV fmt chunk 无效。");
                var bytes = reader.ReadBytes((int)size);
                var encoding = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2));
                var rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
                var bytesPerSecond = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
                var align = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12));
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14));
                if (encoding == 0xFFFE)
                {
                    if (size < 40 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(16)) < 22)
                        throw new InvalidDataException("WAVEFORMATEXTENSIBLE 长度不足。");
                    var subFormat = new Guid(bytes.AsSpan(24, 16));
                    encoding = subFormat == new Guid("00000001-0000-0010-8000-00aa00389b71") ? (ushort)1
                        : subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71") ? (ushort)3 : (ushort)0;
                    if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)) != format.ChannelMask)
                        throw new InvalidDataException("WAV channel mask 与已记录格式不一致。");
                }
                if (encoding != (format.IsFloat ? 3 : 1) || channels != format.Channels || rate != format.SampleRate ||
                    align != format.BlockAlign || bits != format.BitsPerSample || bytesPerSecond != (long)rate * align)
                    throw new InvalidDataException("WAV fmt 与已记录格式不一致。");
                foundFormat = true;
            }
            else if (id == 0x61746164)
            {
                if (foundData || size % format.BlockAlign != 0) throw new InvalidDataException("WAV data 帧不完整或重复。");
                dataOffset = payload;
                FrameCount = size / format.BlockAlign;
                foundData = true;
            }
            stream.Position = end + (size & 1);
        }
        if (!foundFormat || !foundData) throw new InvalidDataException("WAV 缺少 fmt 或 data。");
    }

    public float[] ReadFrames(long frameOffset, int frameCount)
    {
        if (frameOffset < 0 || frameCount < 0 || frameOffset > FrameCount - frameCount)
            throw new InvalidDataException("packet 指向 WAV 数据范围以外。");
        stream.Position = checked(dataOffset + frameOffset * format.BlockAlign);
        var bytes = new byte[checked(frameCount * format.BlockAlign)];
        stream.ReadExactly(bytes);
        var samples = new float[checked(frameCount * format.Channels)];
        var width = format.BitsPerSample / 8;
        for (var index = 0; index < samples.Length; index++)
        {
            var value = bytes.AsSpan(index * width, width);
            samples[index] = format.IsFloat ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(value))
                : width == 2 ? BinaryPrimitives.ReadInt16LittleEndian(value) / 32768f
                : width == 3 ? ReadSigned24(value) / 8388608f
                : BinaryPrimitives.ReadInt32LittleEndian(value) / 2147483648f;
            if (!float.IsFinite(samples[index])) throw new InvalidDataException("来源 WAV 含无效浮点样本。");
        }
        return samples;
    }

    private static int ReadSigned24(ReadOnlySpan<byte> bytes)
    {
        var value = bytes[0] | bytes[1] << 8 | bytes[2] << 16;
        return (value << 8) >> 8;
    }

    public void Dispose() => stream.Dispose();
}
