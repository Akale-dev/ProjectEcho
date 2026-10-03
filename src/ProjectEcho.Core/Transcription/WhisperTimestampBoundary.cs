namespace ProjectEcho.Core.Transcription;

/// <summary>
/// Whisper timestamps are estimates, including its final timestamp. Permit only a small final-cue
/// overrun at the already verified complete WAV boundary; never repair arbitrary invalid intervals.
/// This policy is only used by the local Whisper adapter, never by manual transcript imports.
/// </summary>
internal static class WhisperTimestampBoundary
{
    internal const string Policy = "whisper-final-cue-overrun-at-most-250ms-v1";
    internal const long MaximumOverrunMs = 250;

    internal static List<TranscriptSegment> Normalize(List<TranscriptSegment> segments, long durationMs,
        List<string> warnings)
    {
        for (int index = 0; index < segments.Count; index++)
        {
            var cue = segments[index];
            if (cue.EndMs is not long end || end <= durationMs) continue;
            long overrun = end - durationMs;
            // Both boundaries must already describe a positive interval. A cue beginning at or
            // after EOF has no audio support; do not invent a start time, drop its words, or give
            // it a fabricated one-millisecond duration. Earlier/overlapping overruns also fail.
            if (index != segments.Count - 1 || cue.StartMs is not long start || start < 0 ||
                start >= durationMs || end <= start || cue.TimestampQuality != "approximate" ||
                overrun > MaximumOverrunMs)
                throw new InvalidDataException($"Whisper 转写段 {cue.SegmentId} 的时间 {cue.StartMs}–{end} ms 超出完整音频 {durationMs} ms" +
                    $"（尾端超出 {overrun} ms）。仅允许最后一段在音频内开始、末端超出不超过 {MaximumOverrunMs} ms；原始输出已保留，未猜测或丢弃文字。");

            var result = new List<TranscriptSegment>(segments);
            result[index] = cue with { EndMs = durationMs };
            warnings.Add($"Whisper 尾端时间已按完整音频边界修正：{cue.SegmentId} 结束 {end} ms → {durationMs} ms" +
                $"（超出 {overrun} ms，上限 {MaximumOverrunMs} ms）。原始 SRT/JSON 和全部文字保留；时间仍为 approximate，需人工校对。");
            return result;
        }
        return segments;
    }
}
