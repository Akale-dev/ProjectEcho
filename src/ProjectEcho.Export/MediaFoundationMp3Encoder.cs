using NAudio.MediaFoundation;
using NAudio.Wave;
using System.Runtime.InteropServices;

namespace ProjectEcho.Export;

/// <summary>File-only, verified 48 kHz / 96 kbps mono Windows MP3 output. No added codec binaries.</summary>
public sealed class MediaFoundationMp3Encoder : IM2AudioEncoder
{
    public const int FlushPaddingFrames = 4096;
    public const int DecodedDelayFrames = 673;
    public const int MaximumOverheadFrames = 8192;
    public const string TimingLimit = "MP3 已按 Windows Media Foundation 解码验证 673 帧开头延迟并登记尾部补齐；外部播放器/转写服务可能使用不同的解码延迟，外部时间戳按近似映射。";
    public string Extension => ".mp3";
    public int BitsPerSecond => 96_000;
    public int SampleRate => 48_000;

    public AudioEncodingResult Encode(string pcmWavePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("MP3 导出需要 Windows Media Foundation。");
        cancellationToken.ThrowIfCancellationRequested();
        string source = M2FileSafety.FullPath(pcmWavePath), target = M2FileSafety.FullPath(destinationPath);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) || File.Exists(target))
            throw new IOException("编码目标已存在或是原始来源；不会覆盖。");
        if (!string.Equals(Path.GetExtension(target), Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("MP3 编码目标必须为 .mp3。", nameof(destinationPath));
        using var sourceLock = M2FileSafety.LockedRead(source);
        string root = Path.GetDirectoryName(target)!; Directory.CreateDirectory(root);
        string raw = M2FileSafety.Inside(root, $".encoding-{Guid.NewGuid():N}.mp3");
        string partial = M2FileSafety.Inside(root, $".encoding-{Guid.NewGuid():N}.mp3");
        try
        {
            MediaFoundationApi.Startup();
            if (!MediaFoundationEncoder.GetEncodeBitrates(AudioSubtypes.MFAudioFormat_MP3, SampleRate, 1).Contains(BitsPerSecond))
                throw new NotSupportedException("此 Windows 缺少 48kHz / 96kbps 单声道 MP3 编码器；混音 WAV 保留。");
            // Every operation checks this installed encoder/decoder pair using
            // generated PCM only. Never assume a delay measured on a different PC.
            double timingCorrelation = VerifyInstalledCodecTiming(root, cancellationToken);
            double gain = 1;
            for (int attempt = 1; attempt <= MediaFoundationM4aEncoder.MaximumEncodingAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested(); long contentFrames;
                using (var pcm = new M2WaveReader(source))
                {
                    if (pcm.Format.IsFloat || pcm.Format.BitsPerSample != 16 || pcm.Format.Channels != 1 ||
                        pcm.Format.SampleRate != SampleRate || pcm.FrameCount <= 0)
                        throw new InvalidDataException("MP3 编码只接受非空的 48kHz 单声道 PCM16 混音 WAV/RF64。");
                    using var input = new RawSourceWaveStream(pcm, new WaveFormat(SampleRate, 16, 1));
                    var padded = new MediaFoundationM4aEncoder.PaddedCancelableProvider(
                        new MediaFoundationM4aEncoder.GainPcmProvider(input, gain), FlushPaddingFrames, cancellationToken);
                    MediaFoundationEncoder.EncodeToMp3(padded, raw, BitsPerSecond);
                    contentFrames = padded.ContentBytes / 2;
                    if (contentFrames != pcm.FrameCount) throw new InvalidDataException("MP3 编码未读取完整输入。");
                }
                cancellationToken.ThrowIfCancellationRequested();
                Mp3FrameProbe.CopyWithoutGeneratedTags(raw, partial, cancellationToken);
                M2FileSafety.DeleteGenerated(root, raw);
                var frames = Mp3FrameProbe.Inspect(partial, cancellationToken) with
                { CalibratedDelayFrames = DecodedDelayFrames, CalibrationCorrelation = timingCorrelation };
                var probe = MediaFoundationM4aEncoder.DecodeProbe(partial, cancellationToken);
                Mp3FrameProbe.ValidateAgainstDecoded(frames, probe.DecodedFrames, SampleRate);
                long padding = probe.DecodedFrames - contentFrames - DecodedDelayFrames;
                if (frames.SampleRate != SampleRate || frames.BitsPerSecond != BitsPerSecond || probe.Channels != 1 ||
                    probe.BitsPerSample != 32 || padding < FlushPaddingFrames ||
                    probe.DecodedFrames - contentFrames > MaximumOverheadFrames || probe.TruePeak is null)
                    throw new InvalidDataException("MP3 解码帧数/格式/延迟超出已验证范围；拒绝发布，混音 WAV 保留。");
                if (probe.TruePeak.TruePeakLinear <= TruePeakProtection.ProcessingCeilingLinear)
                {
                    cancellationToken.ThrowIfCancellationRequested(); M2FileSafety.NoReparse(target); File.Move(partial, target, false);
                    return new(target, probe.ByteLength, probe.DecodedFrames, SampleRate, 1, "mpeg1-layer3",
                        probe.DurationSeconds, contentFrames, DecodedDelayFrames, padding, TimingLimit)
                    { TruePeak = probe.TruePeak, AppliedGain = gain, EncodingAttempts = attempt, Mp3Timeline = frames };
                }
                gain *= Math.Min(.95, Math.Pow(10, (TruePeakProtection.ProcessingCeilingDbTp - .1) / 20) / probe.TruePeak.TruePeakLinear);
                M2FileSafety.DeleteGenerated(root, partial);
            }
            throw new InvalidDataException("MP3 真峰值在有限重编码次数内仍超限；原始 WAV 保留。");
        }
        catch
        {
            M2FileSafety.DeleteGenerated(root, raw); M2FileSafety.DeleteGenerated(root, partial); throw;
        }
    }

    private double VerifyInstalledCodecTiming(string root, CancellationToken token)
    {
        string wave = M2FileSafety.Inside(root, $".mp3-calibration-{Guid.NewGuid():N}.wav");
        string raw = M2FileSafety.Inside(root, $".encoding-{Guid.NewGuid():N}.mp3");
        string clean = M2FileSafety.Inside(root, $".encoding-{Guid.NewGuid():N}.mp3");
        try
        {
            float[] original = new float[8192]; var random = new Random(73); double filtered = 0;
            using (var writer = new WaveFileWriter(wave, new WaveFormat(SampleRate, 16, 1)))
            {
                for (int i = 0; i < original.Length; i++)
                {
                    filtered = filtered * .6 + (random.NextDouble() - .5) * .4;
                    original[i] = (short)(filtered * 15000) / 32768f; writer.WriteSample(original[i]);
                }
                for (int i = 0; i < FlushPaddingFrames; i++) writer.WriteSample(0);
            }
            using (var input = new WaveFileReader(wave))
                MediaFoundationEncoder.EncodeToMp3(new MediaFoundationM4aEncoder.PaddedCancelableProvider(input, 0, token), raw, BitsPerSecond);
            Mp3FrameProbe.CopyWithoutGeneratedTags(raw, clean, token);
            using var decoder = new MediaFoundationReader(clean,
                new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
            if (decoder.WaveFormat.SampleRate != SampleRate || decoder.WaveFormat.Channels != 1 ||
                decoder.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
                throw new InvalidDataException("MP3 本机时钟校准解码格式改变。");
            using var memory = new MemoryStream(); byte[] bytes = new byte[16384]; int count;
            while ((count = decoder.Read(bytes, 0, bytes.Length)) > 0) { token.ThrowIfCancellationRequested(); memory.Write(bytes, 0, count); }
            float[] decoded = MemoryMarshal.Cast<byte, float>(memory.ToArray()).ToArray();
            Mp3FrameProbe.ValidateAgainstDecoded(Mp3FrameProbe.Inspect(clean, token), decoded.Length, SampleRate);
            if (decoded.Length < original.Length + DecodedDelayFrames + FlushPaddingFrames)
                throw new InvalidDataException("MP3 本机校准文件未覆盖完整信号与尾部补齐。");
            double best = -1; int bestDelay = -1;
            for (int delay = 0; delay < 2048; delay++)
            {
                token.ThrowIfCancellationRequested(); double cross = 0, leftEnergy = 0, rightEnergy = 0;
                for (int i = 0; i < 2048; i++)
                { double left = original[i], right = decoded[i + delay]; cross += left * right; leftEnergy += left * left; rightEnergy += right * right; }
                double correlation = cross / Math.Sqrt(leftEnergy * rightEnergy);
                if (correlation > best) { best = correlation; bestDelay = delay; }
            }
            if (bestDelay != DecodedDelayFrames || best < .8 || !double.IsFinite(best))
                throw new NotSupportedException("此 Windows 的 MP3 编解码延迟未通过本机合成信号校准；混音 WAV 保留。");
            return best;
        }
        finally
        {
            M2FileSafety.DeleteGenerated(root, wave); M2FileSafety.DeleteGenerated(root, raw); M2FileSafety.DeleteGenerated(root, clean);
        }
    }
}
