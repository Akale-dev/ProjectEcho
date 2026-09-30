using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProjectEcho.Core.Transcription;

internal static partial class TranscriptParser
{
    internal static List<TranscriptSegment> Parse(byte[] bytes, string extension, string meetingId, List<string> warnings)
    {
        string text = TranscriptFiles.Decode(bytes).Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Contains('\0')) throw new InvalidDataException("文本包含 NUL。");
        return extension switch
        {
            ".txt" or ".md" => Untimed(text),
            ".srt" => Subtitles(text, false, warnings),
            ".vtt" => Subtitles(text, true, warnings),
            ".json" => ProjectJson(bytes, meetingId),
            _ => throw new InvalidDataException("只接受 TXT、MD、SRT、VTT 或 ProjectEcho JSON。")
        };
    }

    private static List<TranscriptSegment> Untimed(string text)
    {
        var segments = new List<TranscriptSegment>();
        foreach (string paragraph in Blocks(text.Trim()))
        {
            if (string.IsNullOrWhiteSpace(paragraph)) continue;
            if (paragraph.Length > TranscriptFiles.MaximumSegmentCharacters) throw new InvalidDataException("单段文本超过十万字符，请先按段落拆分。");
            segments.Add(new($"segment-{segments.Count + 1:D6}", null, null, null, "unknown", paragraph.Trim(), "untimed"));
        }
        return segments;
    }

    private static List<TranscriptSegment> Subtitles(string text, bool vtt, List<string> warnings)
    {
        var segments = new List<TranscriptSegment>();
        using var blocks = Blocks(text.Trim()).GetEnumerator();
        if (vtt)
        {
            if (!blocks.MoveNext() || !Regex.IsMatch(blocks.Current.Split('\n', 2)[0], "^WEBVTT(?:[ \\t].*)?$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("VTT 缺少 WEBVTT 文件头。");
            if (blocks.Current.Contains("X-TIMESTAMP-MAP", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("暂不支持 VTT X-TIMESTAMP-MAP；请先转换为局部字幕时间。");
        }
        while (blocks.MoveNext())
        {
            string block = blocks.Current; if (string.IsNullOrWhiteSpace(block)) continue;
            if (block.Length > TranscriptFiles.MaximumSegmentCharacters + 1000) throw new InvalidDataException("单段字幕超过长度限制。");
            string[] lines = block.Split('\n');
            if (vtt && (lines[0] == "STYLE" || lines[0] == "REGION" || lines[0] == "NOTE" || lines[0].StartsWith("NOTE ", StringComparison.Ordinal)))
            { warnings.Add("VTT 注释/样式/区域只保留于原稿，未执行。"); continue; }
            int timingLine = lines[0].Contains("-->", StringComparison.Ordinal) ? 0 : 1;
            if (lines.Length <= timingLine + 1) throw new InvalidDataException("字幕块缺少时间或文本。");
            if (!vtt && timingLine == 1 && !long.TryParse(lines[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException("SRT 字幕序号无效。");
            var match = Timing().Match(lines[timingLine]);
            if (!match.Success || (!vtt && match.Groups[3].Value.Trim().Length != 0)) throw new InvalidDataException("字幕时间行无效。");
            long start = Timestamp(match.Groups[1].Value, vtt), end = Timestamp(match.Groups[2].Value, vtt);
            string content = string.Join('\n', lines.Skip(timingLine + 1)).Trim();
            if (content.Length == 0) throw new InvalidDataException("字幕段没有文本。");
            segments.Add(new($"segment-{segments.Count + 1:D6}", start, end, null, "unknown", content, "approximate"));
            if (segments.Count > TranscriptFiles.MaximumSegments) throw new InvalidDataException("字幕超过五万段。");
        }
        if (segments.Any(s => s.Text.Contains('<'))) warnings.Add("字幕标签按普通文字保存，未推断说话人或执行标记。");
        return segments;
    }

    private static long Timestamp(string value, bool vtt)
    {
        if (vtt && value.Contains(',')) throw new InvalidDataException("VTT 使用点号毫秒。");
        if (!vtt && !value.Contains(',')) throw new InvalidDataException("SRT 使用逗号毫秒。");
        string[] parts = value.Replace(',', '.').Split(':');
        if (parts.Length is < 2 or > 3 || (!vtt && parts.Length != 3)) throw new InvalidDataException("字幕时间格式错误。");
        string[] seconds = parts[^1].Split('.');
        if (seconds.Length != 2 || seconds[0].Length != 2 || seconds[1].Length != 3 || parts[^2].Length != 2)
            throw new InvalidDataException("字幕时间必须包含三位毫秒。");
        long hours = parts.Length == 3 ? ParseNumber(parts[0]) : 0, minutes = ParseNumber(parts[^2]);
        long secs = ParseNumber(seconds[0]), milliseconds = ParseNumber(seconds[1]);
        if (hours > 168 || minutes >= 60 || secs >= 60) throw new InvalidDataException("字幕时间字段越界。");
        return checked(((hours * 60 + minutes) * 60 + secs) * 1000 + milliseconds);
    }

    private static long ParseNumber(string value)
    {
        if (value.Length == 0 || value.Any(c => c is < '0' or > '9') || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long result))
            throw new InvalidDataException("字幕时间包含非数字或溢出。");
        return result;
    }

    private static List<TranscriptSegment> ProjectJson(byte[] bytes, string meetingId)
    {
        using var json = TranscriptFiles.ParseJson(bytes); var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version) || version.GetInt32() is not (1 or 2))
            throw new InvalidDataException("JSON 必须符合 ProjectEcho transcript v1 或 v2 格式。");
        if (root.GetProperty("meetingId").GetString() != meetingId) throw new InvalidDataException("JSON 的 meetingId 与当前会议不符。");
        if (root.GetProperty("segments").GetArrayLength() > TranscriptFiles.MaximumSegments) throw new InvalidDataException("项目 JSON 超过五万段。");
        if (version.GetInt32() == 2)
        {
            var revision = JsonSerializer.Deserialize<TranscriptRevision>(root.GetRawText(), TranscriptFiles.Json) ?? throw new InvalidDataException("转写 JSON 为空。");
            TranscriptFiles.ValidateSegments(revision.Segments, TranscriptFiles.MaximumDurationMs);
            return revision.Segments.Select(s => s with { TimestampQuality = s.StartMs is null ? "untimed" : "approximate" }).ToList();
        }
        string[] required = ["schemaVersion", "isSynthetic", "meetingId", "revisionId", "parentRevisionId", "provider", "inputExportId", "segments"];
        if (root.EnumerateObject().Any(p => !required.Contains(p.Name)) || required.Any(name => !root.TryGetProperty(name, out _)))
            throw new InvalidDataException("项目 JSON v1 字段不符合 schema。");
        _ = root.GetProperty("isSynthetic").GetBoolean();
        foreach (string name in new[] { "meetingId", "revisionId", "provider" })
            if (string.IsNullOrWhiteSpace(root.GetProperty(name).GetString())) throw new InvalidDataException("项目 JSON 必填字符串为空。");
        foreach (string name in new[] { "parentRevisionId", "inputExportId" })
            if (root.GetProperty(name).ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new InvalidDataException("项目 JSON 引用类型无效。");
        var result = new List<TranscriptSegment>();
        string[] segmentFields = ["segmentId", "startMs", "endMs", "speakerLabel", "sourceKind", "text", "timestampQuality"];
        foreach (var item in root.GetProperty("segments").EnumerateArray())
        {
            if (item.EnumerateObject().Any(p => !segmentFields.Contains(p.Name)) || segmentFields.Any(name => !item.TryGetProperty(name, out _)))
                throw new InvalidDataException("项目 JSON 段字段不符合 schema。");
            var segment = JsonSerializer.Deserialize<TranscriptSegment>(item.GetRawText(), TranscriptFiles.Json) ?? throw new InvalidDataException("项目 JSON 段为空。");
            if (segment.TimestampQuality is not ("verified" or "approximate" or "untimed")) throw new InvalidDataException("项目 JSON 时间质量无效。");
            if ((segment.StartMs is null || segment.EndMs is null) && (segment.StartMs is not null || segment.EndMs is not null || segment.TimestampQuality != "untimed"))
                throw new InvalidDataException("项目 JSON 未定时段不一致。");
            result.Add(segment);
        }
        TranscriptFiles.ValidateSegments(result, TranscriptFiles.MaximumDurationMs);
        return result.Select(s => s with { TimestampQuality = s.StartMs is null ? "untimed" : "approximate" }).ToList();
    }

    private static IEnumerable<string> Blocks(string text)
    {
        int position = 0, count = 0;
        // MatchCollection enumerates lazily. Do not Split an attacker-controlled 8 MiB file
        // into millions of paragraph objects before applying the segment limit.
        foreach (Match match in Paragraphs().Matches(text))
        {
            if (++count > TranscriptFiles.MaximumSegments) throw new InvalidDataException("文本/字幕超过五万个块。");
            yield return text[position..match.Index]; position = match.Index + match.Length;
        }
        if (++count > TranscriptFiles.MaximumSegments) throw new InvalidDataException("文本/字幕超过五万个块。");
        yield return text[position..];
    }

    [GeneratedRegex("\\n[ \\t]*\\n+", RegexOptions.CultureInvariant)] private static partial Regex Paragraphs();
    [GeneratedRegex("^\\s*([0-9:.,]+)\\s+-->\\s+([0-9:.,]+)(.*)$", RegexOptions.CultureInvariant)] private static partial Regex Timing();
}
