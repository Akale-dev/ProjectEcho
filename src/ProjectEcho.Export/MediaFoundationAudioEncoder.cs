using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ProjectEcho.Export;

public sealed record AudioEncodingResult(string Path, long ByteLength, long DecodedFrames, int SampleRate,
    int Channels, string Codec, double DurationSeconds, long SourceFrames = 0, long EncoderDelayFrames = 0,
    long TrailingPaddingFrames = 0, string? TimingWarning = null)
{
    public TruePeakMeasurement? TruePeak { get; init; }
    public double AppliedGain { get; init; } = 1;
    public int EncodingAttempts { get; init; } = 1;
    public M4aContainerTimeline? ContainerTimeline { get; init; }
    public Mp3FrameTimeline? Mp3Timeline { get; init; }
}

public interface IM2AudioEncoder
{
    string Extension { get; }
    int BitsPerSecond { get; }
    AudioEncodingResult Encode(string pcmWavePath, string destinationPath, CancellationToken cancellationToken);
}

public sealed record AudioDecodeProbe(long DecodedFrames, int SampleRate, int Channels, int BitsPerSample,
    double DurationSeconds, long ByteLength, double Peak)
{
    public TruePeakMeasurement? TruePeak { get; init; }
}

/// <summary>
/// File-only Windows AAC/M4A codec. Uses the verified NAudio 3.1.0 APIs; never opens capture/playback devices.
/// The input is an already mixed mono PCM16 WAV. Original recordings are never changed.
/// </summary>
public sealed class MediaFoundationM4aEncoder : IM2AudioEncoder
{
    public const int FlushPaddingFrames = 2048;
    public const int MaximumEncodingAttempts = 4;
    public const string TimingLimit = "Windows Media Foundation 解码已验证首标记无偏移；AAC 尾部补齐单独登记。外部播放器/转写服务的 priming 处理未统一验证，外部时间戳仅作近似映射。";
    public string Extension => ".m4a";
    public int BitsPerSecond { get; }
    public int SampleRate { get; }

    public MediaFoundationM4aEncoder(int sampleRate = 48_000, int bitsPerSecond = 96_000)
    {
        if (sampleRate is not (48_000 or 32_000)) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (bitsPerSecond is not (64_000 or 96_000)) throw new ArgumentOutOfRangeException(nameof(bitsPerSecond));
        SampleRate = sampleRate;
        BitsPerSecond = bitsPerSecond;
    }

    public AudioEncodingResult Encode(string pcmWavePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("M4A 导出需要 Windows Media Foundation。");
        cancellationToken.ThrowIfCancellationRequested();
        var source = M2FileSafety.FullPath(pcmWavePath);
        var target = M2FileSafety.FullPath(destinationPath);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) || File.Exists(target))
            throw new IOException("编码目标已存在或是原始来源；不会覆盖。");
        if (!string.Equals(Path.GetExtension(target), Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("AAC 容器目标必须为 .m4a。", nameof(destinationPath));
        using var sourceLock = M2FileSafety.LockedRead(source);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = Path.Combine(Path.GetDirectoryName(target)!, $".encoding-{Guid.NewGuid():N}.m4a");
        try
        {
            MediaFoundationApi.Startup();
            double gain = 1;
            for (var attempt = 1; attempt <= MaximumEncodingAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long contentFrames;
                using (var pcm = new M2WaveReader(source))
                {
                    if (pcm.Format.IsFloat || pcm.Format.BitsPerSample != 16 || pcm.Format.Channels != 1 || pcm.DataLength <= 0)
                        throw new InvalidDataException("M4A 编码只接受非空的单声道 PCM16 混音 WAV/RF64。");
                    using var input = new RawSourceWaveStream(pcm, new WaveFormat(pcm.Format.SampleRate, 16, 1));
                    IWaveProvider converted = new GainPcmProvider(input, gain);
                    if (input.WaveFormat.SampleRate != SampleRate)
                        converted = new SampleToWaveProvider16(new WdlResamplingSampleProvider(converted.ToSampleProvider(), SampleRate));
                    var cancellable = new PaddedCancelableProvider(converted, FlushPaddingFrames, cancellationToken);
                    // Refuse nearest-bitrate substitution so byte budgets describe the actual requested codec setting.
                    var supported = MediaFoundationEncoder.GetEncodeBitrates(AudioSubtypes.MFAudioFormat_AAC, SampleRate, 1);
                    if (!supported.Contains(BitsPerSecond))
                        throw new NotSupportedException($"此 Windows 缺少所需的 AAC 编码器（{SampleRate} Hz / {BitsPerSecond} bps）；WAV 原件保留。");
                    MediaFoundationEncoder.EncodeToAac(cancellable, partial, BitsPerSecond);
                    contentFrames = cancellable.ContentBytes / 2;
                    var expected = (long)Math.Round((double)pcm.FrameCount * SampleRate / pcm.Format.SampleRate);
                    if (Math.Abs(contentFrames - expected) > 1)
                        throw new InvalidDataException("重采样器未提供完整输入时长，拒绝发布压缩文件。");
                }
                cancellationToken.ThrowIfCancellationRequested();
                var probe = DecodeProbe(partial, cancellationToken);
                var containerTimeline = M4aTimelineProbe.Inspect(partial, cancellationToken);
                M4aTimelineProbe.ValidateAgainstDecoded(containerTimeline, probe.DecodedFrames, probe.SampleRate);
                var padding = probe.DecodedFrames - contentFrames;
                if (probe.SampleRate != SampleRate || probe.Channels != 1 || probe.BitsPerSample != 32 ||
                    padding < FlushPaddingFrames || padding >= FlushPaddingFrames + 1024 || probe.TruePeak is null)
                    throw new InvalidDataException("压缩文件解码帧数/格式不符合完整性要求，拒绝发布；混音 WAV 保留。");
                if (probe.TruePeak.TruePeakLinear <= TruePeakProtection.ProcessingCeilingLinear)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(partial, target, false);
                    return new(target, probe.ByteLength, probe.DecodedFrames, probe.SampleRate, probe.Channels, "aac-m4a",
                        probe.DurationSeconds, contentFrames, 0, padding, TimingLimit)
                    { TruePeak = probe.TruePeak, AppliedGain = gain, EncodingAttempts = attempt, ContainerTimeline = containerTimeline };
                }
                // Re-encode from the unchanged source, never from an already lossy or clipped output.
                gain *= Math.Min(.95, Math.Pow(10, (TruePeakProtection.ProcessingCeilingDbTp - .1) / 20) / probe.TruePeak.TruePeakLinear);
                File.Delete(partial);
            }
            throw new InvalidDataException("压缩后真峰值在有限重编码次数内仍超限；没有发布压缩文件，原始 WAV 保留。");
        }
        catch (Exception error)
        {
            // Only this operation's randomly named derived temporary output is removed.
            try { File.Delete(partial); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                throw new AggregateException($"编码未完成，临时派生文件清理失败：{partial}。原始 WAV 保持不变。", error, cleanupError);
            }
            throw;
        }
    }

    public static AudioEncodingResult Encode(string wavPath, string destination, int sampleRate, int bitrate,
        CancellationToken cancellationToken = default) => new MediaFoundationM4aEncoder(sampleRate, bitrate).Encode(wavPath, destination, cancellationToken);

    /// <summary>Read all decoded PCM to EOF; container metadata alone never counts as completeness evidence.</summary>
    public static AudioDecodeProbe DecodeProbe(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MediaFoundationApi.Startup();
        // Float output prevents the decoder's PCM16 conversion from hiding codec overshoot by clipping.
        using var input = new MediaFoundationReader(Path.GetFullPath(path),
            new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
        var format = input.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32 || format.Channels != 1)
            throw new InvalidDataException("解码探针得到不受支持的格式。");
        var bytes = new byte[format.BlockAlign * 16_384];
        long length = 0;
        var meter = new TruePeakMeter(format.SampleRate);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(bytes, 0, bytes.Length);
            if (read == 0) break;
            if (read % format.BlockAlign != 0) throw new InvalidDataException("解码输出含不完整音频帧。");
            length = checked(length + read);
            meter.AppendSamples(MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, read)));
        }
        if (length <= 0) throw new InvalidDataException("压缩文件没有可解码音频。");
        var frames = length / format.BlockAlign;
        var truePeak = meter.Complete();
        return new(frames, format.SampleRate, format.Channels, format.BitsPerSample,
            (double)frames / format.SampleRate, new FileInfo(path).Length, truePeak.SamplePeak) { TruePeak = truePeak };
    }

    internal sealed class GainPcmProvider(IWaveProvider source, double gain) : IWaveProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(Span<byte> buffer)
        {
            var count = source.Read(buffer);
            if (gain < 1)
                for (var index = 0; index < count; index += 2)
                    BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(index, 2),
                        checked((short)Math.Round(BinaryPrimitives.ReadInt16LittleEndian(buffer.Slice(index, 2)) * gain)));
            return count;
        }
    }

    internal sealed class PaddedCancelableProvider(IWaveProvider source, int paddingFrames, CancellationToken token) : IWaveProvider
    {
        private bool sourceEnded;
        private long paddingBytes = checked(paddingFrames * source.WaveFormat.BlockAlign);
        public long ContentBytes { get; private set; }
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            var length = buffer.Length - buffer.Length % WaveFormat.BlockAlign;
            if (length == 0) return 0;
            if (!sourceEnded)
            {
                var read = source.Read(buffer[..length]);
                if (read > 0)
                {
                    if (read % WaveFormat.BlockAlign != 0) throw new InvalidDataException("编码输入含不完整帧。");
                    ContentBytes = checked(ContentBytes + read);
                    return read;
                }
                sourceEnded = true;
            }
            var padding = (int)Math.Min(length, paddingBytes);
            buffer[..padding].Clear();
            paddingBytes -= padding;
            return padding;
        }
    }
}
