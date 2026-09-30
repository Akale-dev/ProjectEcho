using System.Globalization;
using System.Text;

namespace ProjectEcho.Core.Transcription;

/// <summary>Exports the selected immutable revision on its existing full-meeting clock.</summary>
public static class TranscriptSubtitleExporter
{
    public static string Export(string meetingDirectory, string revisionId, string destinationPath,
        CancellationToken cancellationToken = default, bool overwrite = false)
    {
        string meeting = TranscriptFiles.Meeting(meetingDirectory);
        string destination = TranscriptFiles.LocalPath(destinationPath);
        string extension = Path.GetExtension(destination).ToLowerInvariant();
        if (extension is not (".srt" or ".vtt")) throw new InvalidDataException("请选择 SRT 或 VTT 字幕文件。");
        // Export is never allowed to overwrite originals/revisions in any meeting folder.
        for (string? ancestor = Path.GetDirectoryName(destination); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if (File.Exists(Path.Combine(ancestor, "meeting.json")))
                throw new InvalidDataException("请把字幕导出到会议原件目录之外，避免覆盖原稿或修订。");
        var revision = TranscriptService.Load(meeting, revisionId, cancellationToken);
        TranscriptFiles.ValidateSegments(revision.Segments, revision.MeetingDurationMs);
        if (revision.Segments.Any(s => s.StartMs is null || s.EndMs is null))
            throw new InvalidDataException("此版本含无时间文字，不能生成同步字幕；请先使用带时间的识别或字幕版本。");
        bool vtt = extension == ".vtt";
        var text = new StringBuilder(vtt ? "WEBVTT\n\n" : "");
        int number = 0;
        // Chronological, stable order; overlaps and repeated utterances are retained.
        foreach (var segment in revision.Segments.OrderBy(s => s.StartMs))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string content = segment.Text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (content.Split('\n').Any(string.IsNullOrWhiteSpace) || content.Contains("-->", StringComparison.Ordinal))
                throw new InvalidDataException("字幕文字含空白分段或时间箭头，请先在新修订中移除这些字幕控制内容后导出。");
            text.Append((++number).ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append(Timestamp(segment.StartMs!.Value, vtt)).Append(" --> ").Append(Timestamp(segment.EndMs!.Value, vtt)).Append('\n')
                .Append(content).Append("\n\n");
        }
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(text.ToString());
        if (bytes.Length > TranscriptFiles.MaximumFileBytes) throw new InvalidDataException("导出字幕超过 8 MiB，不能保证再次导入；请选择较小版本。");
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("字幕输出目录不存在。");
        string temporary = Path.Combine(directory, ".projectecho-subtitle-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            TranscriptFiles.WriteNew(temporary, bytes);
            cancellationToken.ThrowIfCancellationRequested();
            TranscriptFiles.NoReparse(destination);
            File.Move(temporary, destination, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return destination;
    }

    private static string Timestamp(long milliseconds, bool vtt) => string.Create(CultureInfo.InvariantCulture,
        $"{milliseconds / 3_600_000:D2}:{milliseconds / 60_000 % 60:D2}:{milliseconds / 1000 % 60:D2}{(vtt ? '.' : ',')}{milliseconds % 1000:D3}");
}
