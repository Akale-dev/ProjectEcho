using System.Text;
using System.Text.Json;

namespace ProjectEcho.Core.Transcription;

/// <summary>
/// Every positive raw cue outside the audio needs independent evidence. Trust only its own
/// complete in-range lexical token times, or preserve it untimed. The existing small final-cue
/// tolerance remains separate. Structural validation and manual import rules are unchanged.
/// </summary>
internal static class WhisperOverrunTimestamp
{
    internal const string Policy = "whisper-out-of-range-cues-own-in-range-tokens-or-untimed-v2";
    internal const string DiagnosticPrefix = "Whisper 越界时间";
    private const int MaximumDiagnosticDetails = 100;

    internal static void AddDiagnostic(List<string> warnings, string detail)
    {
        if (warnings.Count < MaximumDiagnosticDetails) warnings.Add(detail);
    }

    internal static void AddDiagnosticSummary(List<string> warnings, int total)
    {
        if (total > MaximumDiagnosticDetails)
            warnings.Add($"{DiagnosticPrefix}片段共 {total} 个；仅列前 {MaximumDiagnosticDetails} 条诊断，正文和顺序全部保留。" +
                "完整原段号和原始时码见本次保留的 whisper-output.srt；本提示没有截断转写文字。");
    }

    // General structural and absolute-duration validation must run before any timestamp is removed.
    internal static bool IsOverrun(TranscriptSegment cue, long durationMs) =>
        cue.TimestampQuality == "approximate" && cue.StartMs is long start && start >= 0 &&
        cue.EndMs is long end && end > start && end <= TranscriptFiles.MaximumDurationMs &&
        end > durationMs;

    internal static bool IsLegacyFinalCue(TranscriptSegment cue, int index, int count, long durationMs) =>
        index == count - 1 && IsOverrun(cue, durationMs) && cue.StartMs < durationMs &&
        cue.EndMs - durationMs <= WhisperTimestampBoundary.MaximumOverrunMs;

    internal static bool RequiresNormalization(TranscriptSegment cue, int index, int count, long durationMs) =>
        IsOverrun(cue, durationMs) && !IsLegacyFinalCue(cue, index, count, durationMs);

    internal static TranscriptSegment Untimed(TranscriptSegment source) =>
        source with { StartMs = null, EndMs = null, TimestampQuality = "untimed" };

    internal static string Diagnostic(TranscriptSegment source, long durationMs, TranscriptSegment? recovered = null) =>
        $"{DiagnosticPrefix} {source.SegmentId}：原始 {source.StartMs}–{source.EndMs} ms，完整音频 {durationMs} ms" +
        $"（尾端超出 {source.EndMs - durationMs} ms）；" +
        (recovered is null
            ? (source.StartMs >= durationMs ? "本段从音频结束处或之后开始，没有可回听区间；" : "没有可核对的本段全文有效词时间；") +
                "全部文字与顺序保留，标为无时间待校对；未裁剪词时间或猜测回听区间。"
            : $"按本段全文匹配且全部位于音频内的原始 token 时间恢复为 {recovered.StartMs}–{recovered.EndMs} ms，仍为 approximate，需人工校对。") +
        "已取得的原始模型输出保留。";

    // The caller has matched the complete raw JSON/SRT before reaching here. A token crossing
    // EOF cannot prove its entire word is supported: do not clip it, borrow a neighbor, or add epsilon.
    internal static bool TryRecover(TranscriptSegment source, string text, JsonElement entry,
        int endOfTextToken, long durationMs, out TranscriptSegment recovered)
    {
        recovered = source;
        if (!IsOverrun(source, durationMs) || source.StartMs >= durationMs || !entry.TryGetProperty("tokens", out var array) ||
            array.ValueKind != JsonValueKind.Array) return false;
        var joined = new StringBuilder(); long last = source.StartMs!.Value; bool found = false;
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
            if (part.Length == 0 || from < last || to <= from || to > durationMs) return false;
            joined.Append(part); last = to; found = true;
            if (joined.Length > TranscriptFiles.MaximumSegmentCharacters) return false;
        }
        if (!found || joined.ToString() != text) return false;
        recovered = source with { EndMs = last };
        return true;
    }
}
