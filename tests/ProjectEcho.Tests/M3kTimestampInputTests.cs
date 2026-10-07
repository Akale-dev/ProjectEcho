using ProjectEcho.Core.Transcription;

internal static class M3kTimestampInputTests
{
    private const long MeetingDurationMs = 2_475_326;

    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3k-timestamp-input"); Directory.CreateDirectory(root);
        run("T38-SYNTH M3K screenshot three-field input stays hours while explicit minute input is exact", ParseScreenshot);
        run("T38-SYNTH M3K timestamp parser retains decimal precision and rejects malformed components", ParseValidation);
        run("T38-SYNTH M3K timestamp feedback distinguishes start end and reversed bounds without changing duration", () => Bounds(root));
        run("T38-SYNTH M3K EOF accepts exact milliseconds and rejects one millisecond beyond recording", () => ExactEnd(root));
        run("T38/T20-SYNTH M3K invalid paired input preserves other drafts and immutable saved revisions", () => DraftPreservation(root));
        run("T38-SYNTH M3K correcting an untimed tail saves once and round-trips millisecond replay times", () => SaveRoundTrip(root));
    }

    private static void ParseScreenshot()
    {
        ExpectParsed("41:09:01", 148_141_000);
        ExpectParsed("41:11:00", 148_260_000);
        ExpectParsed("41:09.01", 2_469_010);
        ExpectParsed("41:11", 2_471_000);
        ExpectParsed("00:41:09.010", 2_469_010);
        ExpectParsed("00:41:11.000", 2_471_000);
        ExpectParsed("1:02:03.4", 3_723_400);
        ExpectParsed("1:02:03,4", 3_723_400);
        M3TranscriptTests.Need(TranscriptRevisionDraft.FormatTimestamp(2_469_010) == "00:41:09.010" &&
            TranscriptRevisionDraft.FormatTimestamp(3_723_400) == "01:02:03.400", "标准时码格式或毫秒精度改变。");
    }

    private static void ParseValidation()
    {
        foreach (var (text, milliseconds) in new (string, long)[]
        {
            ("0:00", 0), ("0:00.001", 1), ("00:00,01", 10), (" 41:09,01 ", 2_469_010),
            ("41:09.1", 2_469_100), ("41:09.123", 2_469_123), ("61:02", 3_662_000),
            ("99999:59.999", 5_999_999_999), ("999:59:59.999", 3_599_999_999)
        }) ExpectParsed(text, milliseconds);
        foreach (string text in new[]
        {
            "41:60", "00:60:00", "00:00:60", "41:09.", "41:09.0001", "41:09,0001",
            "41:9", "1:2:03", "1:02:3", "-1:00", "+1:00", "41:09:01:00", "41：09",
            "41:09:01.0000", "100000:00", "1000:00:00", "999999999999999999999:00", "NaN"
        }) M3TranscriptTests.Need(!TranscriptRevisionDraft.TryParseTimestamp(text, out _), "接受了非法时码：" + text);
        M3TranscriptTests.Need(TranscriptRevisionDraft.TryParseTimestamp(" \t", out var blank) && blank is null &&
            TranscriptRevisionDraft.FormatTimestamp(null) == "", "留空时间未保持无时间语义。");
    }

    private static void Bounds(string root)
    {
        var (_, parent) = Fixture(root, "bounds"); var cue = parent.Segments[0];
        var draft = new TranscriptRevisionDraft(parent); var errors = new List<string>();
        foreach (var (start, end) in new[]
        {
            ("41:09:01", "41:11:00"), // Both are hours; the start itself already exceeds the recording.
            ("41:09.01", "41:11:00"), // Only the end exceeds the recording.
            ("41:11", "41:09.01") // Both are in range, but reversed.
        })
        {
            draft.Set(cue, cue.Text, start, end);
            M3TranscriptTests.Need(!draft.TryGetSegment(cue, out _, out string? error) && !string.IsNullOrWhiteSpace(error),
                "非法边界未提供失败原因。");
            errors.Add(error!);
            M3TranscriptTests.Need(draft.Parent.MeetingDurationMs == MeetingDurationMs, "根据输入放宽或缩短了会议时长。");
        }
        M3TranscriptTests.Need(errors.Distinct(StringComparer.Ordinal).Count() == 3, "开始越界、结束越界和反序仍使用同一错误提示。");
        draft.Set(cue, cue.Text, "41:09.01", "41:11");
        M3TranscriptTests.Need(draft.TryGetSegment(cue, out var corrected, out var correctedError) && correctedError is null &&
            corrected.StartMs == 2_469_010 && corrected.EndMs == 2_471_000, "会议内的分钟输入被错误拒绝或改变。");
        draft.Set(cue, cue.Text, "99999:58", "99999:59");
        M3TranscriptTests.Need(!draft.TryGetSegment(cue, out _, out _), "短格式大分钟数绕过会议时长边界。");
    }

    private static void ExactEnd(string root)
    {
        var (_, parent) = Fixture(root, "exact-end"); var cue = parent.Segments[0];
        var draft = new TranscriptRevisionDraft(parent);
        foreach (var (start, end) in new[] { ("41:15.325", "41:15.326"), ("00:41:15.325", "00:41:15.326") })
        {
            draft.Set(cue, cue.Text, start, end);
            M3TranscriptTests.Need(draft.TryGetSegment(cue, out var exact, out _) && exact.StartMs == MeetingDurationMs - 1 &&
                exact.EndMs == MeetingDurationMs, "精确录音末端或最后一毫秒被舍入拒绝。");
            M3TranscriptTests.Need(draft.BuildSegments()[0].EndMs == MeetingDurationMs, "构建修订丢失精确末端。");
        }
        foreach (var (start, end) in new[] { ("41:15.325", "41:15.327"), ("41:15.326", "41:15.326"), ("41:15.326", "41:15.327") })
        {
            draft.Set(cue, cue.Text, start, end);
            M3TranscriptTests.Need(!draft.TryGetSegment(cue, out _, out _), "末端越界或零时长被接受。");
            M3TranscriptTests.Reject(() => draft.BuildSegments());
        }
        M3TranscriptTests.Need(parent.MeetingDurationMs == MeetingDurationMs, "末端检查改变真实会议时长。");
    }

    private static void DraftPreservation(string root)
    {
        var (meeting, parent) = Fixture(root, "preservation");
        var snapshot = Directory.EnumerateFiles(meeting, "*", SearchOption.AllDirectories).ToDictionary(path => path, M3TranscriptTests.Hash);
        var draft = new TranscriptRevisionDraft(parent); var first = parent.Segments[0]; var second = parent.Segments[1];
        draft.Set(first, "第一段合成修订", "1:00.001", "1:02.003");
        foreach (var (start, end) in new[] { ("41:09.01", ""), ("", "41:11"), ("41:09.", "41:11"), ("41:09:01", "41:11:00") })
        {
            draft.Set(second, "第二段合成修订", start, end);
            M3TranscriptTests.Need(draft.InputFor(second) == new TranscriptSegmentEdit("第二段合成修订", start, end) &&
                draft.ChangedSegmentCount == 2, "无效输入丢失未保存草稿。");
            M3TranscriptTests.Need(draft.TryGetSegment(first, out var other, out _) && other.Text == "第一段合成修订" &&
                other.StartMs == 60_001 && other.EndMs == 62_003, "无效时间影响了其他段草稿。");
            M3TranscriptTests.Reject(() => TranscriptService.CreateEditedRevision(meeting, parent.RevisionId, draft.BuildSegments()));
            M3TranscriptTests.Need(TranscriptService.ListRevisions(meeting).Count == 1, "无效草稿意外保存了部分修订。");
        }
        M3TranscriptTests.Need(snapshot.All(pair => M3TranscriptTests.Hash(pair.Key) == pair.Value), "无效编辑覆盖了原件或原修订。");
        var persisted = TranscriptService.Load(meeting, parent.RevisionId);
        M3TranscriptTests.Need(persisted.MeetingDurationMs == MeetingDurationMs && persisted.Segments.SequenceEqual(parent.Segments),
            "未保存草稿修改了磁盘字幕或时长。");
    }

    private static void SaveRoundTrip(string root)
    {
        var (meeting, parent) = Fixture(root, "roundtrip");
        var snapshot = Directory.EnumerateFiles(meeting, "*", SearchOption.AllDirectories).ToDictionary(path => path, M3TranscriptTests.Hash);
        var draft = new TranscriptRevisionDraft(parent);
        draft.Set(parent.Segments[0], parent.Segments[0].Text, "41:01.001", "41:08.999");
        draft.Set(parent.Segments[1], parent.Segments[1].Text, "41:09.01", "41:11");
        M3TranscriptTests.Need(TranscriptService.ListRevisions(meeting).Count == 1, "输入分钟时码自动生成了修订。");
        var saved = TranscriptService.CreateEditedRevision(meeting, parent.RevisionId, draft.BuildSegments());
        M3TranscriptTests.Need(TranscriptService.ListRevisions(meeting).Count == 2 && saved.ParentRevisionId == parent.RevisionId &&
            saved.MeetingDurationMs == MeetingDurationMs && saved.Segments[1] is
            { StartMs: 2_469_010, EndMs: 2_471_000, TimestampQuality: "approximate" }, "手工修正未一次保存为正确的新修订。");
        string export = Path.Combine(root, "corrected-tail.srt");
        TranscriptSubtitleExporter.Export(meeting, saved.RevisionId, export);
        M3TranscriptTests.Need(File.ReadAllText(export).Contains("00:41:09,010 --> 00:41:11,000", StringComparison.Ordinal),
            "SRT导出没有使用修正后的回听时间。");
        string target = M3TranscriptTests.Meeting(root, "roundtrip-target");
        var imported = TranscriptService.Import(new(target, "roundtrip-target", MeetingDurationMs, export, IsSynthetic: true));
        M3TranscriptTests.Need(imported.Segments.Select(s => (s.StartMs, s.EndMs, s.Text)).SequenceEqual(
            saved.Segments.Select(s => (s.StartMs, s.EndMs, s.Text))), "时码修订导出回导后发生改变。");
        M3TranscriptTests.Need(snapshot.All(pair => M3TranscriptTests.Hash(pair.Key) == pair.Value) &&
            TranscriptService.Load(meeting, parent.RevisionId).Segments.All(s => s.StartMs is null && s.EndMs is null),
            "修正无时间字幕覆盖了保全原稿。");
    }

    private static void ExpectParsed(string input, long expected) => M3TranscriptTests.Need(
        TranscriptRevisionDraft.TryParseTimestamp(input, out var actual) && actual == expected, "时码解析毫秒值错误：" + input);

    private static (string Meeting, TranscriptRevision Revision) Fixture(string root, string name)
    {
        string meeting = M3TranscriptTests.Meeting(root, name);
        File.WriteAllText(Path.Combine(meeting, "meeting.json"),
            "{\"schemaVersion\":\"m1-recording-1\",\"isSynthetic\":true,\"duration100ns\":24753260000}");
        var revision = TranscriptService.Import(new(meeting, name, MeetingDurationMs,
            M3TranscriptTests.Input(meeting, "synthetic.txt", "第一段合成字幕\n\n第二段合成字幕"), IsSynthetic: true));
        return (meeting, revision);
    }
}
