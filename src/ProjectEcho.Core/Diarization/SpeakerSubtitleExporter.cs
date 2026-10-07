using System.Globalization;
using System.Text;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.Core.Diarization;

/// <summary>Labels are output decoration, never part of the authoritative utterance.</summary>
public static class SpeakerSubtitleExporter
{
    public static string Export(string meetingDirectory, string revisionId, string destinationPath,
        bool includeSpeakerLabels = true, CancellationToken cancellationToken = default, bool overwrite = false)
    {
        var revision = SpeakerService.Load(meetingDirectory, revisionId, cancellationToken);
        string destination = TranscriptFiles.LocalPath(destinationPath), extension = Path.GetExtension(destination).ToLowerInvariant();
        if (extension is not (".txt" or ".srt" or ".vtt")) throw new InvalidDataException("请选择 TXT、SRT 或 VTT 文件。");
        for (string? ancestor = Path.GetDirectoryName(destination); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if (File.Exists(Path.Combine(ancestor, "meeting.json"))) throw new InvalidDataException("请把字幕导出到会议原件目录之外。");
        if (extension != ".txt" && revision.Segments.Any(segment => segment.StartMs is null)) throw new InvalidDataException("无时间文字不能导出同步字幕。");
        var text = new StringBuilder(extension == ".vtt" ? "WEBVTT\n\n" : ""); int number = 0;
        IEnumerable<SpeakerSegment> ordered = revision.Segments.All(segment => segment.StartMs is not null)
            ? revision.Segments.OrderBy(segment => segment.StartMs) : revision.Segments;
        foreach (var segment in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string content = segment.Text.Replace("\r\n", "\n").Replace('\r', '\n');
            string prefix = includeSpeakerLabels ? "[说话人 " + SpeakerDisplay.Label(revision, segment) + "] " : "";
            if (extension == ".txt") { text.Append(prefix).Append(content).Append('\n'); continue; }
            if (content.Split('\n').Any(string.IsNullOrWhiteSpace) || content.Contains("-->", StringComparison.Ordinal))
                throw new InvalidDataException("字幕包含空段或时间箭头，请校订正文后再导出。");
            text.Append(++number).Append('\n').Append(Timestamp(segment.StartMs!.Value, extension == ".vtt"))
                .Append(" --> ").Append(Timestamp(segment.EndMs!.Value, extension == ".vtt")).Append('\n')
                .Append(prefix).Append(content).Append("\n\n");
        }
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(text.ToString());
        if (bytes.Length > TranscriptFiles.MaximumFileBytes) throw new InvalidDataException("导出超过8MiB。");
        string directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("导出目录不存在。");
        string temporary = Path.Combine(directory, ".projectecho-speakers-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); TranscriptFiles.WriteNew(temporary, bytes);
            cancellationToken.ThrowIfCancellationRequested(); TranscriptFiles.NoReparse(destination); File.Move(temporary, destination, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return destination;
    }

    private static string Timestamp(long milliseconds, bool vtt) => string.Create(CultureInfo.InvariantCulture,
        $"{milliseconds / 3600000:D2}:{milliseconds / 60000 % 60:D2}:{milliseconds / 1000 % 60:D2}{(vtt ? '.' : ',')}{milliseconds % 1000:D3}");
}
