using System.Globalization;

namespace ProjectEcho.Core.Diarization;

public sealed record SherpaTimestampAdjustment(int IntervalIndex, long RawEndMs, long NormalizedEndMs);
public sealed record SherpaTimestampResult(IReadOnlyList<SherpaDiarizationRuntime.NativeInterval> Intervals,
    IReadOnlyList<SherpaTimestampAdjustment> Adjustments, string Policy);

/// <summary>
/// The pinned pyannote model reports receptive-field centres, including up to two centres
/// beyond EOF in its final padded window. Only those exact native grid values are mapped
/// to the real audio boundary; this never modifies PCM, words, starts, or speaker identities.
/// </summary>
public static class SherpaDiarizationTimestamps
{
    public const string Policy = "sherpa-1.13.8-pyannote3-eof-centres-v1";
    private const string ModelHash = "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079";
    private const long MaximumFrames = 16000L * 3 * 3600;

    public static SherpaTimestampResult Normalize(string output, long frameCount, string segmentationModelSha256,
        long paddedFrames = 0, CancellationToken cancellationToken = default)
    {
        if (frameCount <= 0 || frameCount > MaximumFrames || paddedFrames < 0)
            throw new InvalidDataException("说话人时间映射的原始帧数无效。");
        cancellationToken.ThrowIfCancellationRequested();
        long sourceEndMs = (frameCount * 1000 + 15999) / 16000;
        var allowed = new HashSet<long>();
        // The <=10s adapter padding path is deliberately not covered by this exception.
        // Upstream's final-window truncation applies only with an incomplete 1s window shift.
        if (paddedFrames == 0 && frameCount > 160000 && (frameCount - 160000) % 16000 != 0 &&
            string.Equals(segmentationModelSha256, ModelHash, StringComparison.OrdinalIgnoreCase))
        {
            long lastFrame = frameCount / 270;
            for (long index = Math.Max(0, lastFrame - 2); index <= lastFrame; index++)
            {
                // Match ComputeResult float scale/offset and the CLI's %.3f output.
                // Do not replace this with an arbitrary millisecond tolerance or double grid.
                float scale = 270f / 16000;
                float offset = 0.5f * 991 / 16000;
                float centre = index * scale + offset;
                long printedMs = checked((long)(decimal.Parse(((double)centre).ToString("F3", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) * 1000));
                if (printedMs > sourceEndMs) allowed.Add(printedMs);
            }
        }
        long envelopeMs = allowed.Count == 0 ? sourceEndMs : Math.Max(sourceEndMs, allowed.Max());
        var raw = SherpaDiarizationRuntime.ParseOutput(output, envelopeMs);
        var normalized = new List<SherpaDiarizationRuntime.NativeInterval>(raw.Count);
        var adjustments = new List<SherpaTimestampAdjustment>();
        for (int i = 0; i < raw.Count; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var interval = raw[i];
            if (interval.EndMs > sourceEndMs)
            {
                if (!allowed.Contains(interval.EndMs) || interval.StartMs * 16 >= frameCount || interval.StartMs >= sourceEndMs)
                    throw new InvalidDataException($"说话人第{i + 1}条区间 {interval.StartMs}–{interval.EndMs} ms 超过音频 {sourceEndMs} ms，且不符合已验证的末帧中心网格；未保存分析。");
                adjustments.Add(new(i, interval.EndMs, sourceEndMs));
                interval = interval with { EndMs = sourceEndMs };
            }
            normalized.Add(interval);
        }
        return new(normalized.AsReadOnly(), adjustments.AsReadOnly(), Policy);
    }
}
