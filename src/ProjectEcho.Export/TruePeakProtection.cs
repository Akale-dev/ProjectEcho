using System.Buffers.Binary;
using ProjectEcho.Core;

namespace ProjectEcho.Export;

public sealed record TruePeakMeasurement(double SamplePeak, double TruePeakLinear, double? DbTp,
    long FrameCount, int SampleRate, string Algorithm);

public sealed record TruePeakProtectionResult(string Path, TruePeakMeasurement Before, TruePeakMeasurement After,
    double AppliedGain, double AttenuationDb);

/// <summary>
/// ITU-R BS.1770-5 Annex 2's specified four-phase FIR, with constant-memory streaming state.
/// This is a true-peak estimate, not a sample ceiling. Fixed attenuation of a derived copy preserves time.
/// </summary>
public static class TruePeakProtection
{
    public const string Algorithm = "ITU-R-BS.1770-5-Annex2-4x-FIR48-v1";
    public const double RequiredCeilingDbTp = -1.0;
    // Annex 2 Attachment 1 gives 0.688 dB maximum grid under-read at fs/2 for 4x.
    // A 1 dB margin also leaves room for FIR ripple and PCM16 requantization.
    public const double ProcessingCeilingDbTp = -2.0;
    public static readonly double ProcessingCeilingLinear = Math.Pow(10, ProcessingCeilingDbTp / 20);

    public static TruePeakMeasurement AnalyzeWave(string path, CancellationToken cancellationToken = default)
    {
        using var input = new M2WaveReader(path);
        ValidateFormat(input.Format);
        return AnalyzePcm16(input, input.Format.SampleRate, cancellationToken);
    }

    public static TruePeakMeasurement AnalyzePcm16(Stream pcm, int sampleRate, CancellationToken cancellationToken = default)
    {
        var meter = new TruePeakMeter(sampleRate);
        var bytes = new byte[32_768];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = pcm.Read(bytes);
            if (read == 0) break;
            meter.AppendPcm16(bytes.AsSpan(0, read));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return meter.Complete();
    }

    /// <summary>Produces a new file even if attenuation is unnecessary. Neither input nor existing targets are overwritten.</summary>
    public static TruePeakProtectionResult ProtectWave(string source, string destination, CancellationToken cancellationToken = default)
    {
        var sourcePath = Path.GetFullPath(source);
        var targetPath = Path.GetFullPath(destination);
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase) || File.Exists(targetPath))
            throw new IOException("真峰值保护必须生成新的派生文件，不能覆盖已有文件。");
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new M2WaveReader(sourcePath);
        ValidateFormat(reader.Format);
        var before = AnalyzePcm16(reader, reader.Format.SampleRate, cancellationToken);
        // 0.01 dB rounding guard ensures the written PCM16 file remains below the processing ceiling.
        var gain = before.TruePeakLinear > ProcessingCeilingLinear
            ? Math.Pow(10, (ProcessingCeilingDbTp - 0.01) / 20) / before.TruePeakLinear : 1;
        var temporary = Path.Combine(Path.GetDirectoryName(targetPath)!, $".truepeak-{Guid.NewGuid():N}.wav");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using (var raw = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[32_768];
                CopyExact(raw, output, reader.DataOffset, buffer, cancellationToken);
                long remaining = reader.DataLength;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(buffer.Length, remaining);
                    raw.ReadExactly(buffer.AsSpan(0, count));
                    if (gain < 1)
                        for (var index = 0; index < count; index += 2)
                        {
                            var sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(index, 2));
                            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(index, 2), checked((short)Math.Round(sample * gain)));
                        }
                    output.Write(buffer, 0, count);
                    remaining -= count;
                }
                CopyExact(raw, output, raw.Length - raw.Position, buffer, cancellationToken);
                output.Flush(true);
            }
            var after = AnalyzeWave(temporary, cancellationToken);
            if (after.FrameCount != before.FrameCount || after.SampleRate != before.SampleRate ||
                after.TruePeakLinear > ProcessingCeilingLinear + 1e-7)
                throw new InvalidDataException("派生 WAV 的真峰值或帧数复核失败；没有发布输出。");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, targetPath, false);
            return new(targetPath, before, after, gain, gain == 1 ? 0 : -20 * Math.Log10(gain));
        }
        catch (Exception error)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { throw new AggregateException($"真峰值保护失败且临时文件未能清理：{temporary}", error, cleanup); }
            throw;
        }
    }

    private static void ValidateFormat(PcmFormat format)
    {
        if (format.IsFloat || format.BitsPerSample != 16 || format.Channels != 1)
            throw new InvalidDataException("真峰值保护仅接受已混合的单声道 PCM16 WAV/RF64。");
    }

    private static void CopyExact(Stream source, Stream target, long bytes, byte[] buffer, CancellationToken token)
    {
        while (bytes > 0)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(bytes, buffer.Length);
            source.ReadExactly(buffer.AsSpan(0, count)); target.Write(buffer, 0, count); bytes -= count;
        }
    }
}

/// <summary>Mono streaming meter; FIR history is retained across arbitrary reads and zero-flushed exactly once at EOF.</summary>
public sealed class TruePeakMeter
{
    // The columns of the normative Annex 2 interpolation example, transcribed from ITU-R BS.1770-5 pp.18–19.
    // Floating point arithmetic needs no 12.04 dB integer headroom pre/post gain.
    private static readonly double[,] Coefficients =
    {
        { .0017089843750, -.0291748046875, -.0189208984375, -.0083007812500 },
        { .0109863281250, .0292968750000, .0330810546875, .0148925781250 },
        { -.0196533203125, -.0517578125000, -.0582275390625, -.0266113281250 },
        { .0332031250000, .0891113281250, .1015625000000, .0476074218750 },
        { -.0594482421875, -.1665039062500, -.2003173828125, -.1022949218750 },
        { .1373291015625, .4650878906250, .7797851562500, .9721679687500 },
        { .9721679687500, .7797851562500, .4650878906250, .1373291015625 },
        { -.1022949218750, -.2003173828125, -.1665039062500, -.0594482421875 },
        { .0476074218750, .1015625000000, .0891113281250, .0332031250000 },
        { -.0266113281250, -.0582275390625, -.0517578125000, -.0196533203125 },
        { .0148925781250, .0330810546875, .0292968750000, .0109863281250 },
        { -.0083007812500, -.0189208984375, -.0291748046875, .0017089843750 }
    };
    private readonly double[] history = new double[24];
    private readonly int sampleRate;
    private int position;
    private long frames;
    private double samplePeak, interpolatedPeak;
    private TruePeakMeasurement? completed;

    public TruePeakMeter(int sampleRate)
    {
        if (sampleRate is < 8000 or > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        this.sampleRate = sampleRate;
    }

    public void AppendPcm16(ReadOnlySpan<byte> bytes)
    {
        if (completed is not null) throw new InvalidOperationException("真峰值检测已结束。");
        if (bytes.Length % 2 != 0) throw new InvalidDataException("PCM16 输入含不完整帧。");
        for (var index = 0; index < bytes.Length; index += 2)
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(index, 2)) / 32768d;
            samplePeak = Math.Max(samplePeak, Math.Abs(value));
            Push(value);
        }
        frames = checked(frames + bytes.Length / 2);
    }

    public void AppendSamples(ReadOnlySpan<float> samples)
    {
        if (completed is not null) throw new InvalidOperationException("真峰值检测已结束。");
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample)) throw new InvalidDataException("真峰值检测输入含非有限值。");
            samplePeak = Math.Max(samplePeak, Math.Abs(sample)); Push(sample);
        }
        frames = checked(frames + samples.Length);
    }

    public TruePeakMeasurement Complete()
    {
        if (completed is not null) return completed;
        for (var index = 0; index < 12; index++) Push(0);
        var peak = Math.Max(samplePeak, interpolatedPeak);
        return completed = new(samplePeak, peak, peak == 0 ? null : 20 * Math.Log10(peak), frames, sampleRate, TruePeakProtection.Algorithm);
    }

    private void Push(double value)
    {
        position = position == 0 ? 11 : position - 1;
        history[position] = history[position + 12] = value;
        double p0 = 0, p1 = 0, p2 = 0, p3 = 0;
        for (var index = 0; index < 12; index++)
        {
            var sample = history[position + index];
            p0 += sample * Coefficients[index, 0]; p1 += sample * Coefficients[index, 1];
            p2 += sample * Coefficients[index, 2]; p3 += sample * Coefficients[index, 3];
        }
        interpolatedPeak = Math.Max(interpolatedPeak, Math.Max(Math.Max(Math.Abs(p0), Math.Abs(p1)), Math.Max(Math.Abs(p2), Math.Abs(p3))));
    }
}
