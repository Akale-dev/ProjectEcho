using System.Globalization;
using System.Text.RegularExpressions;

namespace ProjectEcho.Core.Transcription;

/// <summary>Unsaved user edits, including incomplete timestamp input. Never writes files.</summary>
public sealed class TranscriptRevisionDraft(TranscriptRevision parent)
{
    public TranscriptRevision Parent { get; } = parent;
    private readonly Dictionary<string, TranscriptSegmentEdit> edits = new(StringComparer.Ordinal);
    public bool HasChanges => edits.Count > 0;
    public int ChangedSegmentCount => edits.Count;

    public TranscriptSegmentEdit InputFor(TranscriptSegment segment) => edits.GetValueOrDefault(segment.SegmentId)
        ?? new(segment.Text, FormatTimestamp(segment.StartMs), FormatTimestamp(segment.EndMs));

    public void Set(TranscriptSegment segment, string text, string start, string end)
    {
        if (!Parent.Segments.Contains(segment)) throw new ArgumentException("字幕段不属于本草稿的原版本。", nameof(segment));
        var input = new TranscriptSegmentEdit(text, start, end);
        if (TryApply(segment, input, out var updated, out _) && updated == segment) edits.Remove(segment.SegmentId);
        else edits[segment.SegmentId] = input;
    }

    public bool TryGetSegment(TranscriptSegment segment, out TranscriptSegment updated, out string? error)
        => TryApply(segment, InputFor(segment), out updated, out error);

    public IReadOnlyList<TranscriptSegment> BuildSegments()
    {
        var result = new List<TranscriptSegment>(Parent.Segments.Count);
        foreach (var segment in Parent.Segments)
        {
            if (!TryGetSegment(segment, out var updated, out string? error))
                throw new InvalidDataException($"字幕 {segment.SegmentId}：{error}");
            result.Add(updated);
        }
        TranscriptFiles.ValidateSegments(result, Parent.MeetingDurationMs);
        return result;
    }

    private bool TryApply(TranscriptSegment segment, TranscriptSegmentEdit input, out TranscriptSegment updated, out string? error)
    {
        updated = segment; error = null;
        if (string.IsNullOrWhiteSpace(input.Text) || input.Text.Length > TranscriptFiles.MaximumSegmentCharacters || input.Text.Contains('\0'))
        { error = "字幕文字不能为空、包含空字符或超过十万字符。"; return false; }
        if (!TryParseTimestamp(input.Start, out long? start) || !TryParseTimestamp(input.End, out long? end))
        { error = "时间格式须为 时:分:秒.毫秒 或 分:秒.毫秒，小数用点号；无时间字幕请同时留空。"; return false; }
        if ((start is null) != (end is null))
        { error = "开始和结束时间必须同时填写或同时留空。"; return false; }
        if (start > Parent.MeetingDurationMs)
        { error = "开始时间超过本字幕版本的录音时长；请核对解析结果，三段冒号时间的第一段是小时。"; return false; }
        if (end > Parent.MeetingDurationMs)
        { error = "结束时间超过本字幕版本的录音时长；请核对解析结果，三段冒号时间的第一段是小时。"; return false; }
        if (start is not null && end <= start)
        { error = "结束时间必须晚于开始时间。"; return false; }
        string quality = start is null ? "untimed" : start == segment.StartMs && end == segment.EndMs
            ? segment.TimestampQuality : "approximate";
        updated = segment with { Text = input.Text, StartMs = start, EndMs = end, TimestampQuality = quality };
        return true;
    }

    public static string FormatTimestamp(long? milliseconds) => milliseconds is not { } ms ? "" :
        string.Create(CultureInfo.InvariantCulture, $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}");

    public static bool TryParseTimestamp(string text, out long? milliseconds)
    {
        milliseconds = null;
        text = text.Trim();
        if (text.Length == 0) return true;
        // Three components always mean hours:minutes:seconds. Never reinterpret an
        // out-of-range hour value as minutes based on the meeting duration.
        var match = Regex.Match(text, @"\A(?:(?<h>[0-9]{1,3}):(?<m>[0-5][0-9])|(?<m>[0-9]{1,5})):(?<s>[0-5][0-9])(?:[.,](?<f>[0-9]{1,3}))?\z", RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        long hours = match.Groups["h"].Success ? long.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        long minutes = long.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        long seconds = long.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        long fraction = match.Groups["f"].Success ? long.Parse(match.Groups["f"].Value.PadRight(3, '0'), CultureInfo.InvariantCulture) : 0;
        milliseconds = hours * 3_600_000 + minutes * 60_000 + seconds * 1000 + fraction;
        return true;
    }
}

public sealed record TranscriptSegmentEdit(string Text, string Start, string End);
