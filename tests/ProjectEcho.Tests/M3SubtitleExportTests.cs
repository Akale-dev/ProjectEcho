using System.Text;
using ProjectEcho.Core.Transcription;

internal static class M3SubtitleExportTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3-subtitle-export"); Directory.CreateDirectory(root);
        run("T38-SYNTH SRT/VTT export retains Unicode millisecond times over one hour overlaps and revisions", () => RoundTrips(root));
        run("T38/T50-SYNTH subtitle export rejects untimed text malformed cues and meeting destinations", () => Rejections(root));
        run("T20/T50-SYNTH cancelled or conflicting subtitle export preserves originals and existing files", () => Atomicity(root));
    }

    private static void RoundTrips(string root)
    {
        string directory = Path.Combine(root, "roundtrip"); Directory.CreateDirectory(directory);
        string meeting = M3TranscriptTests.Meeting(directory, "meeting");
        const long durationMs = 3_700_000;
        string input = M3TranscriptTests.Input(directory, "input.srt",
            "1\n01:00:01,123 --> 01:00:02,987\n中文与 English 🚀\n第二行 — 确认 1.23\n\n" +
            "2\n00:00:00,007 --> 00:00:01,019\n同一句\n\n" +
            "3\n01:00:01,123 --> 01:00:01,456\n相同开始、不同结束\n\n" +
            "4\n01:00:02,000 --> 01:00:03,111\n同一句\n");
        var original = TranscriptService.Import(new(meeting, "meeting", durationMs, input, IsSynthetic: true));
        var reviewed = TranscriptService.CreateRevision(meeting, original.RevisionId,
            original.Segments.Select((segment, index) => index == 0 ? segment with { Text = "已修订 中文与 English 🚀\n第二行 — 确认 1.23" } : segment).ToArray());
        var initialHashes = HashAll(meeting);
        foreach (string extension in new[] { ".srt", ".vtt" })
        {
            string path = Path.Combine(directory, "export" + extension);
            string actualPath = TranscriptSubtitleExporter.Export(meeting, reviewed.RevisionId, path);
            M3TranscriptTests.Need(actualPath == path, "导出返回错误路径。");
            var bytes = File.ReadAllBytes(path);
            M3TranscriptTests.Need(!bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), "字幕意外加入 BOM。");
            string text = new UTF8Encoding(false, true).GetString(bytes);
            M3TranscriptTests.Need(text.Contains(extension == ".srt" ? "01:00:01,123" : "01:00:01.123"), "小时或毫秒时间丢失。");
            M3TranscriptTests.Need(extension != ".vtt" || text.StartsWith("WEBVTT\n\n", StringComparison.Ordinal), "VTT 缺必要文件头。");
            var roundTrip = TranscriptService.Import(new(meeting, "meeting", durationMs, path, IsSynthetic: true));
            var expected = reviewed.Segments.OrderBy(s => s.StartMs).Select(s => (s.StartMs, s.EndMs, s.Text));
            var actual = roundTrip.Segments.Select(s => (s.StartMs, s.EndMs, s.Text));
            M3TranscriptTests.Need(expected.SequenceEqual(actual), "导出再导入改变 Unicode、修订文字、毫秒、顺序或重叠。");
            M3TranscriptTests.Need(roundTrip.Segments.Count(s => s.Text == "同一句") == 2 && roundTrip.Warnings.Any(w => w.Contains("重叠")), "重复强调或重叠字幕被删除。");
        }
        Unchanged(initialHashes);
        M3TranscriptTests.Need(TranscriptService.Load(meeting, original.RevisionId).Segments[0].Text.StartsWith("中文", StringComparison.Ordinal), "导出修订改变原稿。");
    }

    private static void Rejections(string root)
    {
        string directory = Path.Combine(root, "rejections"); Directory.CreateDirectory(directory);
        string meeting = M3TranscriptTests.Meeting(directory, "meeting");
        var untimed = TranscriptService.Import(new(meeting, "meeting", 10000,
            M3TranscriptTests.Input(directory, "untimed.txt", "没有时间的会议笔记"), IsSynthetic: true));
        string destination = Path.Combine(directory, "rejected.srt");
        Reject<InvalidDataException>(() => TranscriptSubtitleExporter.Export(meeting, untimed.RevisionId, destination));
        var timed = TranscriptService.Import(new(meeting, "meeting", 10000,
            M3TranscriptTests.Input(directory, "timed.srt", "1\n00:00:01,001 --> 00:00:02,009\n正常字幕"), IsSynthetic: true));
        foreach (string text in new[] { "前半\n\n后半", "前半\n  \n后半", "正文含 --> 时间箭头" })
        {
            var reviewed = TranscriptService.CreateRevision(meeting, timed.RevisionId, [timed.Segments[0] with { Text = text }]);
            Reject<InvalidDataException>(() => TranscriptSubtitleExporter.Export(meeting, reviewed.RevisionId, destination));
        }
        string original = Path.Combine(meeting, timed.OriginalFile);
        string originalHash = M3TranscriptTests.Hash(original);
        Reject<InvalidDataException>(() => TranscriptSubtitleExporter.Export(meeting, timed.RevisionId, original, overwrite: true));
        Reject<InvalidDataException>(() => TranscriptSubtitleExporter.Export(meeting, timed.RevisionId, Path.Combine(meeting, "export.srt")));
        string otherMeeting = M3TranscriptTests.Meeting(directory, "other-meeting");
        Reject<InvalidDataException>(() => TranscriptSubtitleExporter.Export(meeting, timed.RevisionId, Path.Combine(otherMeeting, "export.vtt")));
        Reject<InvalidDataException>(() => TranscriptSubtitleExporter.Export(meeting, timed.RevisionId, Path.Combine(directory, "unsupported.txt")));
        M3TranscriptTests.Need(!File.Exists(destination) && M3TranscriptTests.Hash(original) == originalHash, "拒绝后仍有输出或原件已改变。");
        M3TranscriptTests.Need(!Directory.EnumerateFiles(directory, ".projectecho-subtitle-*.tmp").Any(), "拒绝导出留下临时文件。");
    }

    private static void Atomicity(string root)
    {
        string directory = Path.Combine(root, "atomicity"); Directory.CreateDirectory(directory);
        string meeting = M3TranscriptTests.Meeting(directory, "meeting");
        var timed = TranscriptService.Import(new(meeting, "meeting", 10000,
            M3TranscriptTests.Input(directory, "source.srt", "1\n00:00:01,001 --> 00:00:02,009\n不可覆盖的原稿"), IsSynthetic: true));
        var initialHashes = HashAll(meeting);
        string existing = M3TranscriptTests.Input(directory, "existing.srt", "用户已有文件");
        string before = M3TranscriptTests.Hash(existing);
        Reject<IOException>(() => TranscriptSubtitleExporter.Export(meeting, timed.RevisionId, existing));
        M3TranscriptTests.Need(M3TranscriptTests.Hash(existing) == before, "未请求覆盖却改变目标。");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Reject<OperationCanceledException>(() => TranscriptSubtitleExporter.Export(meeting, timed.RevisionId, existing, cancellation.Token, overwrite: true));
        string absent = Path.Combine(directory, "cancelled.vtt");
        Reject<OperationCanceledException>(() => TranscriptSubtitleExporter.Export(meeting, timed.RevisionId, absent, cancellation.Token));
        M3TranscriptTests.Need(M3TranscriptTests.Hash(existing) == before && !File.Exists(absent), "取消仍覆盖或生成目标。");
        TranscriptSubtitleExporter.Export(meeting, timed.RevisionId, existing, overwrite: true);
        M3TranscriptTests.Need(File.ReadAllText(existing).Contains("不可覆盖的原稿"), "显式覆盖未写入所选字幕。");
        M3TranscriptTests.Need(!Directory.EnumerateFiles(directory, ".projectecho-subtitle-*.tmp").Any(), "冲突或取消后没有清理临时文件。");
        Unchanged(initialHashes);
    }

    private static Dictionary<string, string> HashAll(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, M3TranscriptTests.Hash);

    private static void Unchanged(Dictionary<string, string> hashes)
    {
        foreach (var pair in hashes)
            M3TranscriptTests.Need(M3TranscriptTests.Hash(pair.Key) == pair.Value, "字幕导出改变原件或既有版本：" + pair.Key);
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("未以预期异常拒绝导出：" + typeof(T).Name);
    }
}
