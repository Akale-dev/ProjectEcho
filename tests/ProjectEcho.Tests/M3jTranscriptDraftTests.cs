using ProjectEcho.Core.Transcription;

internal static class M3jTranscriptDraftTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3j-transcript-drafts"); Directory.CreateDirectory(root);
        run("T38-SYNTH M3J multiple text and timestamp edits save exactly one immutable revision", () => Batch(root));
        run("T38-SYNTH M3J invalid timestamp input survives draft navigation and rejects all-or-nothing save", () => Validation(root));
        run("T38-SYNTH M3J untimed cues gain explicit times without fabricated verification", () => Untimed(root));
        run("T38/T50-SYNTH M3J timing edits preserve provenance and reject changed metadata", () => Provenance(root));
    }

    private static (string Directory, TranscriptRevision Revision) Fixture(string root, string name)
    {
        string meeting = M3TranscriptTests.Meeting(root, name);
        var original = TranscriptService.Import(new(meeting, name, 10000, M3TranscriptTests.Input(meeting, "input.srt",
            "1\n00:00:01,123 --> 00:00:02,456\n第一句\n\n2\n00:00:03,789 --> 00:00:04,987\n第二句\n"), IsSynthetic: true));
        return (meeting, original);
    }

    private static void Batch(string root)
    {
        var (directory, parent) = Fixture(root, "batch");
        var snapshot = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => path, M3TranscriptTests.Hash);
        var draft = new TranscriptRevisionDraft(parent);
        var a = parent.Segments[0]; var b = parent.Segments[1];
        draft.Set(a, "第一句初改", "00:00:00.500", "00:00:02.345");
        draft.Set(b, "第二句已改", "00:00:03.333", "00:00:05.555");
        draft.Set(a, "第一句定稿", "00:00:00.567", "00:00:02.345");
        M3TranscriptTests.Need(draft.ChangedSegmentCount == 2 && TranscriptService.ListRevisions(directory).Count == 1,
            "编辑内存草稿生成了版本。");
        var saved = TranscriptService.CreateEditedRevision(directory, parent.RevisionId, draft.BuildSegments());
        M3TranscriptTests.Need(TranscriptService.ListRevisions(directory).Count == 2 && saved.ParentRevisionId == parent.RevisionId &&
            saved.Segments[0].StartMs == 567 && saved.Segments[1].EndMs == 5555 && saved.Segments[0].Text == "第一句定稿" &&
            saved.Segments[1].Text == "第二句已改", "一次保存丢失多段草稿或时间。");
        M3TranscriptTests.Need(snapshot.All(pair => M3TranscriptTests.Hash(pair.Key) == pair.Value), "保存覆盖原稿。");
        string exported = Path.Combine(root, "batch-edited.srt"); TranscriptSubtitleExporter.Export(directory, saved.RevisionId, exported);
        M3TranscriptTests.Need(File.ReadAllText(exported).Contains("00:00:00,567 --> 00:00:02,345"), "导出未使用更正的毫秒时间。");
        M3TranscriptTests.Reject(() => TranscriptService.CreateEditedRevision(directory, saved.RevisionId, saved.Segments));
        draft.Set(a, a.Text, "00:00:01.123", "00:00:02.456"); draft.Set(b, b.Text, "00:00:03.789", "00:00:04.987");
        M3TranscriptTests.Need(!draft.HasChanges, "撤回原值仍认为草稿未保存。");
    }

    private static void Validation(string root)
    {
        var (directory, parent) = Fixture(root, "validation"); var cue = parent.Segments[0];
        var draft = new TranscriptRevisionDraft(parent);
        foreach (var (start, end) in new[] { ("00:00:", "00:00:02"), ("", "00:00:02"), ("-00:00:01", "00:00:02"),
            ("00:60:00", "00:00:02"), ("00:00:02", "00:00:02"), ("00:00:03", "00:00:02"), ("00:00:02", "00:00:10.001") })
        {
            draft.Set(cue, cue.Text, start, end);
            M3TranscriptTests.Need(draft.HasChanges && draft.InputFor(cue).Start == start && !draft.TryGetSegment(cue, out _, out _),
                "未完整或非法时间被丢弃/接受。");
            M3TranscriptTests.Reject(() => draft.BuildSegments());
            M3TranscriptTests.Need(TranscriptService.ListRevisions(directory).Count == 1, "无效草稿出现新版本。");
        }
        M3TranscriptTests.Need(TranscriptRevisionDraft.TryParseTimestamp("1:02:03,4", out var parsed) && parsed == 3723400 &&
            TranscriptRevisionDraft.FormatTimestamp(parsed) == "01:02:03.400", "小数精度或逗号解析错误。");
    }

    private static void Untimed(string root)
    {
        string directory = M3TranscriptTests.Meeting(root, "untimed");
        var parent = TranscriptService.Import(new(directory, "untimed", 10000, M3TranscriptTests.Input(directory, "input.txt", "保全文字"), IsSynthetic: true));
        var draft = new TranscriptRevisionDraft(parent); var cue = parent.Segments[0];
        draft.Set(cue, cue.Text, "00:00:01.010", "00:00:02.020");
        var saved = TranscriptService.CreateEditedRevision(directory, parent.RevisionId, draft.BuildSegments());
        M3TranscriptTests.Need(saved.Segments[0].TimestampQuality == "approximate" && saved.Segments[0].StartMs == 1010 &&
            TranscriptService.Load(directory, parent.RevisionId).Segments[0].StartMs is null, "手改时间误标已验证或覆盖无时间原稿。");
        var remove = new TranscriptRevisionDraft(saved); remove.Set(saved.Segments[0], cue.Text, "", "");
        var untimed = TranscriptService.CreateEditedRevision(directory, saved.RevisionId, remove.BuildSegments());
        M3TranscriptTests.Need(untimed.Segments[0] is { StartMs: null, EndMs: null, TimestampQuality: "untimed" }, "清除时间保留了伪起点。");
    }

    private static void Provenance(string root)
    {
        var (directory, parent) = Fixture(root, "provenance");
        foreach (var changed in new[] { parent.Segments[0] with { SourceKind = "microphone" },
            parent.Segments[0] with { SpeakerLabel = "另一个人" }, parent.Segments[0] with { SegmentId = "changed" },
            parent.Segments[0] with { StartMs = 500, TimestampQuality = "verified" } })
            M3TranscriptTests.Reject(() => TranscriptService.CreateEditedRevision(directory, parent.RevisionId, [changed, parent.Segments[1]]));
        M3TranscriptTests.Need(TranscriptService.ListRevisions(directory).Count == 1, "更改来源被保存。");
    }
}
