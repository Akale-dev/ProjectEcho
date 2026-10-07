using System.Security.Cryptography;
using System.Text;

namespace ProjectEcho.Core.Transcription;

/// <summary>Real alignment evidence. Window/VAD/character interpolation are deliberately not timing sources.</summary>
public sealed record QwenWindowAlignmentEvidence(string Source, string Version, string ExecutableSha256,
    string ModelSha256, string InputWaveSha256, string OriginalTextSha256);

/// <summary>UTF-16 ranges address exact immutable raw text; audio frames are local to this window input.</summary>
public sealed record QwenWindowSentence(int TextStart, int TextLength, long LocalStartFrame, long LocalEndFrame);

public enum QwenWindowSentenceOwnership { Inside, ContextOnly, CrossesBoundary }

public sealed record QwenMappedSentence(int WindowIndex, int TextStart, int TextLength, string Text,
    long StartFrame, long EndFrame, long StartMs, long EndMs, string TimestampQuality,
    string TimingSource, QwenWindowSentenceOwnership Ownership);

/// <summary>
/// Validates actual sentence alignment and applies one window offset. This does not produce alignment:
/// an adapter must supply independently verified frame boundaries tied to the exact Qwen text and audio.
/// Overlap is retained and labeled for explicit reconciliation, never clipped or deduplicated by words.
/// </summary>
public static class QwenWindowAlignment
{
    public static IReadOnlyList<QwenMappedSentence> Map(QwenWindowPlan plan, QwenWindow window, string originalText,
        string expectedInputWaveSha256, QwenWindowAlignmentEvidence evidence, IReadOnlyList<QwenWindowSentence> sentences)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.ValidateWindow(window);
        ArgumentNullException.ThrowIfNull(originalText);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(sentences);
        if (originalText.Length == 0 || originalText.Length > TranscriptFiles.MaximumSegmentCharacters || originalText.Contains('\0') ||
            sentences.Count == 0 || sentences.Count > 50000)
            throw new InvalidDataException("Qwen 对齐必须保留完整非空原文及实际句子边界。");
        if (evidence.Source is not ("forced-alignment" or "model-native") ||
            string.IsNullOrWhiteSpace(evidence.Version) || evidence.Version.Length > 200 ||
            !TranscriptFiles.IsHash(evidence.ExecutableSha256) || !TranscriptFiles.IsHash(evidence.ModelSha256) ||
            !TranscriptFiles.IsHash(expectedInputWaveSha256) ||
            !string.Equals(expectedInputWaveSha256, evidence.InputWaveSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(TextSha256(originalText), evidence.OriginalTextSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("句子对齐来源或音频/原文哈希不匹配；窗口、VAD及另一份转写不能冒充本原文对齐。");
        var mapped = new List<QwenMappedSentence>(sentences.Count);
        int textEnd = 0;
        long audioEnd = 0;
        foreach (var sentence in sentences)
        {
            if (sentence is null || sentence.TextStart < textEnd || sentence.TextLength <= 0 ||
                sentence.TextStart > originalText.Length || sentence.TextLength > originalText.Length - sentence.TextStart ||
                sentence.LocalStartFrame < audioEnd || sentence.LocalEndFrame <= sentence.LocalStartFrame ||
                sentence.LocalEndFrame > window.InputFrameCount ||
                !IsTextBoundary(originalText, sentence.TextStart) || !IsTextBoundary(originalText, sentence.TextStart + sentence.TextLength))
                throw new InvalidDataException("句子文字范围或本窗口音频边界无效、重叠或超出输入，拒绝猜测时间。");
            RequireOnlyWhitespace(originalText.AsSpan(textEnd, sentence.TextStart - textEnd));
            string text = originalText.Substring(sentence.TextStart, sentence.TextLength);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("句子不能仅包含空白。");
            long start = checked(window.InputStartFrame + sentence.LocalStartFrame);
            long end = checked(window.InputStartFrame + sentence.LocalEndFrame);
            var ownership = start >= window.OwnedStartFrame && end <= window.OwnedEndFrame
                ? QwenWindowSentenceOwnership.Inside
                : end <= window.OwnedStartFrame || start >= window.OwnedEndFrame
                    ? QwenWindowSentenceOwnership.ContextOnly : QwenWindowSentenceOwnership.CrossesBoundary;
            // Floor start, ceil end: millisecond display never discards sub-millisecond input frames.
            long startMs = start * 1000 / QwenWindowPlan.SampleRate;
            long endMs = (end * 1000 + QwenWindowPlan.SampleRate - 1) / QwenWindowPlan.SampleRate;
            mapped.Add(new(window.Index, sentence.TextStart, sentence.TextLength, text, start, end,
                startMs, endMs, "approximate", evidence.Source, ownership));
            textEnd = sentence.TextStart + sentence.TextLength;
            audioEnd = sentence.LocalEndFrame;
        }
        RequireOnlyWhitespace(originalText.AsSpan(textEnd));
        return mapped.AsReadOnly();
    }

    public static string TextSha256(string originalText) =>
        Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(originalText)));

    private static bool IsTextBoundary(string text, int offset) =>
        offset <= 0 || offset >= text.Length || !char.IsHighSurrogate(text[offset - 1]) || !char.IsLowSurrogate(text[offset]);

    private static void RequireOnlyWhitespace(ReadOnlySpan<char> text)
    {
        foreach (char value in text)
            if (!char.IsWhiteSpace(value)) throw new InvalidDataException("句子对齐遗漏原文字符，不能发布不完整转写。");
    }
}
