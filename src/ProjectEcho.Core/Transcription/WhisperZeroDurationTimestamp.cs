using System.Text;
using System.Text.Json;

namespace ProjectEcho.Core.Transcription;

/// <summary>
/// Preserve an in-range, zero-duration Whisper cue without inventing an interval. This policy is
/// internal to the local adapter: ordinary subtitle imports still require strictly positive ranges.
/// </summary>
internal static class WhisperZeroDurationTimestamp
{
    internal const string Policy = "whisper-in-range-zero-duration-own-tokens-or-untimed-v1";
    private const int MaximumDiagnosticDetails = 100;

    internal static void AddDiagnostic(List<string> warnings, string detail)
    {
        if (warnings.Count < MaximumDiagnosticDetails) warnings.Add(detail);
    }

    internal static void AddDiagnosticSummary(List<string> warnings, int total)
    {
        if (total > MaximumDiagnosticDetails)
            warnings.Add($"Whisper 零时长片段共 {total} 个；仅列前 {MaximumDiagnosticDetails} 条诊断，正文和顺序全部保留。" +
                "完整段号及原始时码见本次保留的 whisper-output.srt；本提示没有截断转写文字。");
    }

    internal static List<TranscriptSegment> WithoutUnsupportedTimes(List<TranscriptSegment> original,
        long durationMs, List<string> warnings)
    {
        var result = new List<TranscriptSegment>(original.Count); int zeroCount = 0;
        foreach (TranscriptSegment cue in original)
        {
            if (!IsEligible(cue, durationMs)) { result.Add(cue); continue; }
            result.Add(cue with { StartMs = null, EndMs = null, TimestampQuality = "untimed" });
            zeroCount++;
            AddDiagnostic(warnings, $"Whisper 零时长片段 {cue.SegmentId}：原始 {cue.StartMs}–{cue.EndMs} ms；没有可核对的有效词时间，" +
                "全部文字与顺序保留，标为无时间待校对；未猜测回听区间。");
        }
        AddDiagnosticSummary(warnings, zeroCount);
        return result;
    }

    internal static bool IsEligible(TranscriptSegment cue, long durationMs) =>
        cue.TimestampQuality == "approximate" && cue.StartMs is long start && start >= 0 &&
        start < durationMs && cue.EndMs == start;

    // Only this cue's own complete lexical token sequence can restore its interval. The JSON/SRT
    // correspondence is verified for every cue before calling this method. No neighboring cue,
    // character count, epsilon duration, or clipped out-of-range token contributes a timestamp.
    internal static bool TryRecover(TranscriptSegment source, string text, JsonElement entry,
        int endOfTextToken, long durationMs, out TranscriptSegment recovered)
    {
        recovered = source;
        if (!IsEligible(source, durationMs) || !entry.TryGetProperty("tokens", out var array) ||
            array.ValueKind != JsonValueKind.Array) return false;
        var joined = new StringBuilder(); long first = -1, last = -1;
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) ||
                !id.TryGetInt32(out int tokenId) || tokenId < 0) return false;
            if (tokenId >= endOfTextToken) continue;
            if (!item.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("offsets", out var offsets) || offsets.ValueKind != JsonValueKind.Object ||
                !offsets.TryGetProperty("from", out var a) || !a.TryGetInt64(out long from) ||
                !offsets.TryGetProperty("to", out var b) || !b.TryGetInt64(out long to)) return false;
            string part = value.GetString()!;
            // Zero-length lexical tokens cannot supply a supported duration, including punctuation.
            if (part.Length == 0 || from < 0 || to <= from || from < last || to > durationMs) return false;
            if (first < 0) first = from;
            last = to; joined.Append(part);
            if (joined.Length > TranscriptFiles.MaximumSegmentCharacters) return false;
        }
        if (first < 0 || last <= first || joined.ToString() != text ||
            source.StartMs < first || source.StartMs > last) return false;
        recovered = source with { StartMs = first, EndMs = last };
        return true;
    }
}
