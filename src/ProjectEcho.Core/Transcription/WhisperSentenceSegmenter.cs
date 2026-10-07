using System.Text;
using System.Text.Json;

namespace ProjectEcho.Core.Transcription;

/// <summary>
/// Sentence punctuation is a text heuristic; times are only copied from matching raw Whisper tokens.
/// Missing, conflicting or unusable timing leaves the original cue intact. Never interpolate by text length.
/// </summary>
internal static class WhisperSentenceSegmenter
{
    internal const string Policy = "sentence-punctuation-preferred-phrase-boundaries-v2";
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
        { "Mr", "Mrs", "Ms", "Dr", "Prof", "Sr", "Jr", "St", "vs", "etc", "e.g", "i.e" };

    internal static List<TranscriptSegment> Normalize(List<TranscriptSegment> original, string jsonPath,
        long durationMs, List<string> warnings, CancellationToken token)
    {
        var fallbackWarnings = new List<string>();
        var fallback = WhisperZeroDurationTimestamp.WithoutUnsupportedTimes(original, durationMs, fallbackWarnings);
        // Validate before JSON processing or joining. Negative/reversed/out-of-range zero cues,
        // invalid text/identity and absolute hard-limit violations remain fatal.
        TranscriptFiles.ValidateSegments(fallback, TranscriptFiles.MaximumDurationMs);
        // Prepare every raw out-of-range cue for absent/corrupt JSON without changing the raw
        // list used for complete JSON/SRT correspondence. No neighbor or whole-file shift is used.
        var fallbackOverrunWarnings = new List<string>(); int overrunCount = 0;
        for (int index = 0; index < original.Count; index++)
        {
            if (!WhisperOverrunTimestamp.RequiresNormalization(original[index], index, original.Count, durationMs)) continue;
            fallback[index] = WhisperOverrunTimestamp.Untimed(original[index]); overrunCount++;
            WhisperOverrunTimestamp.AddDiagnostic(fallbackOverrunWarnings, WhisperOverrunTimestamp.Diagnostic(original[index], durationMs));
        }
        WhisperOverrunTimestamp.AddDiagnosticSummary(fallbackOverrunWarnings, overrunCount);
        fallbackWarnings.AddRange(fallbackOverrunWarnings);
        // JSON full includes a large amount of per-token metadata; keep a separate, bounded local limit.
        try
        {
            using var json = TranscriptFiles.ParseJson(TranscriptFiles.ReadBounded(jsonPath, token, 64 * 1024 * 1024));
            JsonElement root = json.RootElement;
            var entries = root.GetProperty("transcription");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != original.Count)
                throw new InvalidDataException("JSON/SRT 段数量不一致。");
            int endOfTextToken = root.GetProperty("model").GetProperty("multilingual").GetBoolean() ? 50257 : 50256;
            var entryArray = entries.EnumerateArray().ToArray();
            // Match the complete output before trusting any per-token time; a late mismatch must
            // not leave earlier recovered timestamps or their warnings in a partial result.
            for (int index = 0; index < original.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                JsonElement entry = entryArray[index]; TranscriptSegment source = original[index];
                if ((entry.GetProperty("text").GetString() ?? "").Trim() != source.Text ||
                    !TryOffsets(entry, out long from, out long to) || from != source.StartMs || to != source.EndMs)
                    throw new InvalidDataException("JSON/SRT 文字或时间不一致。");
            }
            var recoveryWarnings = new List<string>(); int zeroCount = 0;
            var overrunWarnings = new List<string>();
            var result = new List<WhisperPhraseFragment>(); int splitCount = 0, unchangedCount = 0;
            for (int index = 0; index < original.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                TranscriptSegment source = original[index]; JsonElement entry = entryArray[index];
                string text = entry.GetProperty("text").GetString() ?? "";
                if (WhisperOverrunTimestamp.RequiresNormalization(source, index, original.Count, durationMs))
                {
                    if (WhisperOverrunTimestamp.TryRecover(source, text, entry, endOfTextToken, durationMs, out var recovered))
                    {
                        WhisperOverrunTimestamp.AddDiagnostic(overrunWarnings, WhisperOverrunTimestamp.Diagnostic(source, durationMs, recovered));
                        source = recovered;
                    }
                    else
                    {
                        result.Add(new(fallback[index]));
                        WhisperOverrunTimestamp.AddDiagnostic(overrunWarnings, WhisperOverrunTimestamp.Diagnostic(source, durationMs));
                        continue;
                    }
                }
                else if (WhisperZeroDurationTimestamp.IsEligible(source, durationMs))
                {
                    zeroCount++;
                    if (WhisperZeroDurationTimestamp.TryRecover(source, text, entry, endOfTextToken, durationMs, out var recovered))
                    {
                        WhisperZeroDurationTimestamp.AddDiagnostic(recoveryWarnings, $"Whisper 零时长片段 {source.SegmentId}：原始 {source.StartMs}–{source.EndMs} ms；" +
                            $"按本段全文匹配且单调有效的原始 token 时间恢复为 {recovered.StartMs}–{recovered.EndMs} ms，仍为 approximate，需人工校对。");
                        source = recovered;
                    }
                    else
                    {
                        result.Add(new(fallback[index]));
                        WhisperZeroDurationTimestamp.AddDiagnostic(recoveryWarnings, $"Whisper 零时长片段 {source.SegmentId}：原始 {source.StartMs}–{source.EndMs} ms；没有可核对的有效词时间，" +
                            "全部文字与顺序保留，标为无时间待校对；未猜测回听区间。");
                        continue;
                    }
                }
                List<WhisperPhraseFragment> cues = Split(source, text, entry, endOfTextToken);
                // Preserve the old small final-cue clipping policy, but do not let splitting
                // create earlier out-of-range subcues or a final subcue beginning beyond EOF.
                if (WhisperOverrunTimestamp.IsLegacyFinalCue(source, index, original.Count, durationMs) &&
                    (cues.Take(cues.Count - 1).Any(cue => cue.Cue.EndMs > durationMs) || cues[^1].Cue.StartMs >= durationMs))
                    cues = [new(source)];
                result.AddRange(cues);
                if (cues.Count > 1) splitCount += cues.Count(cue => cue.Boundary == WhisperPhraseBoundary.Sentence);
                else if (SentenceEnds(text).Any(position => position < text.TrimEnd().Length)) unchangedCount++;
            }
            // A length limit is a display guard, not a sentence boundary. Prefer an already
            // emitted clause mark or a verified token gap at a word boundary before the guard.
            var merged = WhisperPhraseReflow.Merge(result, EndsSentence, token, out int merges, out int preferredCuts);
            if (merged.Count > TranscriptFiles.MaximumSegments) throw new InvalidDataException("分句后超过五万段。");
            for (int i = 0; i < merged.Count; i++) merged[i] = merged[i] with { SegmentId = $"segment-{i + 1:D6}" };
            WhisperZeroDurationTimestamp.AddDiagnosticSummary(recoveryWarnings, zeroCount);
            WhisperOverrunTimestamp.AddDiagnosticSummary(overrunWarnings, overrunCount);
            warnings.AddRange(recoveryWarnings);
            warnings.AddRange(overrunWarnings);
            warnings.Add($"字幕按句末标点分句（拆分 {splitCount} 处、合并相邻未完句 {merges} 处）；边界只取模型 token/原段时间，仍为 approximate，不是强制对齐。");
            if (preferredCuts > 0) warnings.Add($"{preferredCuts} 处长度保护边界优先采用原有分句标点或词间 token 停顿；未补写标点，也不保证是语义句末。");
            if (unchangedCount > 0) warnings.Add($"{unchangedCount} 个原段含句末标点但没有可用的 token 分句边界，保留原段时间，未按字数猜测。");
            return merged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException or DecoderFallbackException)
        {
            warnings.Add("未取得可核对的完整 token 时间，保留模型原段；未按字数推算句子时间。可重新转写生成独立版本。");
            warnings.AddRange(fallbackWarnings);
            return fallback;
        }
    }

    private static List<WhisperPhraseFragment> Split(TranscriptSegment source, string text, JsonElement entry, int endOfTextToken)
    {
        if (!entry.TryGetProperty("tokens", out var array) || array.ValueKind != JsonValueKind.Array) return [new(source)];
        var tokens = new List<(int End, long From, long To)>(); var joined = new StringBuilder(); long lastEnd = source.StartMs!.Value;
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || !id.TryGetInt32(out int tokenId) || tokenId < 0) return [new(source)];
            if (tokenId >= endOfTextToken) continue; // Whisper control/timestamp tokens are not transcript words.
            if (!item.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String) return [new(source)];
            string part = value.GetString()!;
            if (!TryOffsets(item, out long from, out long to) || from < lastEnd || to < from || to > source.EndMs) return [new(source)];
            joined.Append(part); tokens.Add((joined.Length, from, to)); lastEnd = to;
            if (joined.Length > TranscriptFiles.MaximumSegmentCharacters) return [new(source)];
        }
        if (joined.ToString() != text) return [new(source)];
        var sentenceEnds = SentenceEnds(text).ToHashSet();
        int trimmedLength = text.TrimEnd().Length;
        var result = new List<WhisperPhraseFragment>(); int startCharacter = 0; long startTime = source.StartMs.Value;
        for (int tokenIndex = 0; tokenIndex < tokens.Count - 1; tokenIndex++)
        {
            int endCharacter = tokens[tokenIndex].End;
            if (endCharacter >= trimmedLength) break;
            long endTime = tokens[tokenIndex].To, nextTime = tokens[tokenIndex + 1].From;
            if (endTime <= startTime || nextTime < endTime || nextTime >= source.EndMs) continue;
            var boundary = sentenceEnds.Contains(endCharacter) ? WhisperPhraseBoundary.Sentence :
                WhisperPhraseReflow.PreferredBoundary(text, endCharacter, nextTime - endTime);
            if (boundary == WhisperPhraseBoundary.Raw) continue;
            string sentence = text[startCharacter..endCharacter].Trim();
            if (sentence.Length == 0) continue;
            result.Add(new(source with { StartMs = startTime, EndMs = endTime, Text = sentence }, boundary,
                SeparatorBefore(text, startCharacter)));
            startCharacter = endCharacter; startTime = nextTime;
        }
        string tail = text[startCharacter..].Trim();
        if (tail.Length > 0) result.Add(new(source with { StartMs = startTime, Text = tail },
            SeparatorBefore: SeparatorBefore(text, startCharacter)));
        return result.Count == 0 ? [new(source)] : result;
    }

    private static string? SeparatorBefore(string text, int position)
    {
        if (position == 0) return null;
        int end = position;
        while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
        return text[position..end];
    }

    private static bool EndsSentence(string text) => SentenceEnds(text).LastOrDefault() == text.TrimEnd().Length;

    private static IEnumerable<int> SentenceEnds(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            char c = text[index];
            if (c is not ('。' or '！' or '？' or '!' or '?' or '.')) continue;
            if (c == '.')
            {
                // Decimal/version/URL dots and common titles/initials are not reliable sentence ends.
                if (index + 1 < text.Length && char.IsLetterOrDigit(text[index + 1])) continue;
                int wordStart = index;
                while (wordStart > 0 && (char.IsLetter(text[wordStart - 1]) || text[wordStart - 1] == '.')) wordStart--;
                string word = text[wordStart..index];
                if (Abbreviations.Contains(word) || word.Length == 1 && char.IsLetter(word[0]) || word.Contains('.')) continue;
            }
            while (index + 1 < text.Length && (text[index + 1] is '。' or '！' or '？' or '!' or '?' or '.' or
                '"' or '\'' or '”' or '’' or '」' or '』' or '》' or ')' or '）' or ']' or '】')) index++;
            yield return index + 1;
        }
    }

    private static bool TryOffsets(JsonElement value, out long from, out long to)
    {
        from = to = -1;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("offsets", out var offsets) &&
            offsets.ValueKind == JsonValueKind.Object && offsets.TryGetProperty("from", out var a) && a.TryGetInt64(out from) &&
            offsets.TryGetProperty("to", out var b) && b.TryGetInt64(out to);
    }
}
