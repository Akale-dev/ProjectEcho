using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectEcho.Core.Transcription;

[JsonConverter(typeof(JsonStringEnumConverter<QwenAlignmentTimingIssueKind>))]
public enum QwenAlignmentTimingIssueKind
{
    NegativeBoundary, ReversedWord, OutsideWindow, NonMonotonicStart, NonMonotonicEnd,
    NonPositiveSentence, OverlappingSentences
}

public sealed record QwenAlignmentTimingIssue(QwenAlignmentTimingIssueKind Kind, int WordNumber,
    long StartSample, long EndSample, long PreviousStartSample, long PreviousEndSample, long InputFrames);

/// <summary>Text and JSON have been checked in full. Only typed timing faults can produce no mapped sentences.</summary>
public sealed record QwenAlignmentInspection(IReadOnlyList<QwenWindowSentence> Sentences,
    IReadOnlyList<QwenAlignmentTimingIssue> TimingIssues, int TimingIssueCount)
{
    public bool TimingComplete => TimingIssueCount == 0;
}

/// <summary>
/// Aggregates raw Qwen ForcedAligner predictions against exact Qwen text. Strict Map retains its
/// original rejection contract. Inspect validates the entire word sequence before classifying
/// acoustic timing faults; it never interpolates, drops words, or constructs replacement times.
/// </summary>
public static class QwenAlignmentSentenceMapper
{
    public const string Policy = "qwen-raw-acoustic-sentence-v1";
    public const string RequiredNativeDiagnostic = "projectecho-alignment: raw-model-boundaries-v1; sample_rate=16000; timestamp_quantum_ms=80";
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
        { "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "e.g", "i.e" };

    public static IReadOnlyList<QwenWindowSentence> Map(string originalText, string wordsJsonPath, long inputFrameCount)
    {
        var inspected = Inspect(originalText, TranscriptFiles.ReadBounded(wordsJsonPath, CancellationToken.None), inputFrameCount);
        if (!inspected.TimingComplete)
            throw new InvalidDataException("原始声学对齐出现反序、无正时长或越界；保留原稿并待定位，不插值修复。");
        return inspected.Sentences;
    }

    public static QwenAlignmentInspection Inspect(string originalText, byte[] wordsJson, long inputFrameCount)
    {
        ArgumentNullException.ThrowIfNull(originalText);
        ArgumentNullException.ThrowIfNull(wordsJson);
        if (string.IsNullOrWhiteSpace(originalText) || originalText.Length > TranscriptFiles.MaximumSegmentCharacters ||
            originalText.Contains('\0') || inputFrameCount <= 0 || inputFrameCount > 16000L * 600 ||
            wordsJson.Length > TranscriptFiles.MaximumFileBytes)
            throw new InvalidDataException("强制对齐文字或窗口长度无效。");
        using var json = TranscriptFiles.ParseJson(wordsJson);
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() is <= 0 or > 50000)
            throw new InvalidDataException("强制对齐必须包含完整词边界数组。");
        var source = Alignable(originalText);
        var words = new List<Word>();
        var issues = new List<QwenAlignmentTimingIssue>();
        int cursor = 0, issueCount = 0;
        long previousStart = 0, previousEnd = 0;
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("word", out var wordValue) || wordValue.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("start_sample", out var startValue) || startValue.ValueKind != JsonValueKind.Number || !startValue.TryGetInt64(out long start) ||
                !item.TryGetProperty("end_sample", out var endValue) || endValue.ValueKind != JsonValueKind.Number || !endValue.TryGetInt64(out long end))
                throw new InvalidDataException("对齐词条结构或采样位置类型无效，不能作为已核对的原稿。");
            string value = wordValue.GetString()!;
            var characters = Alignable(value);
            if (characters.Count == 0 || characters.Count > source.Count - cursor)
                throw new InvalidDataException("对齐词条缺词、空词或多出原文字符，不能作为已核对的原稿。");
            foreach (var character in characters)
                if (character.Value != source[cursor++].Value)
                    throw new InvalidDataException("对齐文字与本次 Qwen 原文不一致；不能套用另一份文字的时间。");
            int first = source[cursor - characters.Count].Start;
            var last = source[cursor - 1];
            int wordNumber = words.Count + 1;
            // Collect timing faults but continue checking every remaining word/field. An early
            // reversed timestamp must not hide missing, changed, or malformed words later on.
            if (start < 0 || end < 0) AddIssue(QwenAlignmentTimingIssueKind.NegativeBoundary, wordNumber, start, end, previousStart, previousEnd);
            if (end < start) AddIssue(QwenAlignmentTimingIssueKind.ReversedWord, wordNumber, start, end, previousStart, previousEnd);
            if (start > inputFrameCount || end > inputFrameCount) AddIssue(QwenAlignmentTimingIssueKind.OutsideWindow, wordNumber, start, end, previousStart, previousEnd);
            if (start < previousStart) AddIssue(QwenAlignmentTimingIssueKind.NonMonotonicStart, wordNumber, start, end, previousStart, previousEnd);
            if (end < previousEnd) AddIssue(QwenAlignmentTimingIssueKind.NonMonotonicEnd, wordNumber, start, end, previousStart, previousEnd);
            words.Add(new(first, last.Start + last.Length, start, end));
            previousStart = start; previousEnd = end;
        }
        if (cursor != source.Count) throw new InvalidDataException("对齐遗漏 Qwen 原文末尾或中间文字，不能发布完成版本。");
        if (issueCount > 0) return new([], issues.AsReadOnly(), issueCount);

        var result = new List<QwenWindowSentence>();
        int textStart = 0, wordStart = 0;
        foreach (int textEnd in SentenceEnds(originalText).Append(originalText.Length).Distinct())
        {
            if (textEnd <= textStart) continue;
            int wordEnd = wordStart;
            while (wordEnd < words.Count && words[wordEnd].TextEnd <= textEnd) wordEnd++;
            if (wordEnd < words.Count && words[wordEnd].TextStart < textEnd || wordEnd == wordStart) continue;
            long start = words[wordStart].Start, end = words[wordEnd - 1].End;
            if (end <= start) AddIssue(QwenAlignmentTimingIssueKind.NonPositiveSentence, wordStart + 1, start, end, 0, 0);
            if (result.Count > 0 && start < result[^1].LocalEndFrame)
                AddIssue(QwenAlignmentTimingIssueKind.OverlappingSentences, wordStart + 1, start, end, result[^1].LocalStartFrame, result[^1].LocalEndFrame);
            result.Add(new(textStart, textEnd - textStart, start, end));
            textStart = textEnd; wordStart = wordEnd;
        }
        if (wordStart != words.Count || result.Count == 0)
            throw new InvalidDataException("未能把完整声学对齐归入原文句子。");
        if (issueCount > 0) return new([], issues.AsReadOnly(), issueCount);
        if (textStart < originalText.Length)
            result[^1] = result[^1] with { TextLength = originalText.Length - result[^1].TextStart };
        return new(result.AsReadOnly(), [], 0);

        void AddIssue(QwenAlignmentTimingIssueKind kind, int wordNumber, long start, long end, long priorStart, long priorEnd)
        {
            issueCount++;
            if (issues.Count < 64) issues.Add(new(kind, wordNumber, start, end, priorStart, priorEnd, inputFrameCount));
        }
    }

    private static List<Character> Alignable(string text)
    {
        var result = new List<Character>(); int offset = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (rune == Rune.ReplacementChar) throw new InvalidDataException("对齐原文含损坏 Unicode 字符。");
            if (Rune.IsLetterOrDigit(rune) || rune.Value == '\'') result.Add(new(rune.Value, offset, rune.Utf16SequenceLength));
            offset += rune.Utf16SequenceLength;
        }
        return result;
    }

    private static IEnumerable<int> SentenceEnds(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch is not ('。' or '！' or '？' or '!' or '?' or '.')) continue;
            if (ch == '.')
            {
                if (i > 0 && i + 1 < text.Length && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1])) continue;
                int start = i;
                while (start > 0 && (char.IsLetter(text[start - 1]) || text[start - 1] == '.')) start--;
                string preceding = text[start..i];
                if (Abbreviations.Contains(preceding) || preceding.Length == 1 && char.IsLetter(preceding[0])) continue;
                if (i + 1 < text.Length && char.IsLetter(text[i + 1])) continue;
            }
            int end = i + 1;
            while (end < text.Length && text[end] is '。' or '！' or '？' or '!' or '?' or '.' or '”' or '’' or '」' or '』' or '"' or ')' or '）' or ']' or '】') end++;
            while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
            yield return end; i = end - 1;
        }
    }

    private readonly record struct Character(int Value, int Start, int Length);
    private readonly record struct Word(int TextStart, int TextEnd, long Start, long End);
}
