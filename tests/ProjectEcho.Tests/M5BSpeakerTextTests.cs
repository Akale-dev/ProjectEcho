using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;

internal static class M5BSpeakerTextTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m5b-speaker-text"); Directory.CreateDirectory(root);
        M5eSpeakerDraftTests.Register(run, root);
        run("P5-03 M5B SYNTH speaker body edit preserves Unicode identity clocks parent and unrelated segments", () => Edit(root));
        run("P5-03 M5B SYNTH edit after a manual turn split retains exact source spans and later corrections", () => EditAfterSplit(root));
        run("P5-03 M5B SYNTH split edited text uses explicit cut time and coarse source groups without fake character mapping", () => SplitAfterEdit(root));
        run("P5-03 M5B SYNTH edited body and display names survive reopen SQLite rebuild and all label exports", () => Exports(root));
        run("P5-03 M5B SYNTH old schema reads and new edits retain elapsed and pause-excluded policies", () => Legacy(root));
        run("P5-03 M5B SYNTH edited body undeclared changes forged groups histories and parent hashes are rejected", () => Tamper(root));
        run("P5-03 M5B SYNTH invalid Unicode empty text and cancelled edits never publish a revision", () => Invalid(root));
        run("P5-03 M5B SYNTH model rerun and transcript projection never replace manual text or claim its old token map", () => Rerun(root));
        run("P5-03 M5C SYNTH explicit manual time cuts work without model candidates and preserve both clock policies exports and index", () => ManualTimePolicies(root));
        run("P5-03 M5C SYNTH manual time after text correction retains coarse provenance and all text", () => ManualTimeEdited(root));
        run("P5-03 M5C SYNTH invalid or cancelled manual time cuts preserve originals and publish nothing", () => ManualTimeInvalid(root));
        run("P5-03 M5C SYNTH manual time provenance parent hashes and declared transition are verified on reopen", () => ManualTimeTamper(root));
    }

    private static void Edit(string root)
    {
        var (meeting, source, original) = Fixture(root, "edit");
        string parentHash = M3TranscriptTests.Hash(PathOf(meeting, original)), sourceHash = M3TranscriptTests.Hash(Path.Combine(meeting, source.OriginalFile));
        var segment = original.Segments[0]; string replacement = "不是下降🚀，是增长𠀀。 e\u0301";
        var edit = SpeakerService.EditText(meeting, original.RevisionId, segment.SegmentId, replacement);
        var current = edit.Segments[0];
        Need(edit.SchemaVersion == 2 && edit.ParentRevisionSha256 == parentHash && edit.ParentRevisionId == original.RevisionId &&
            edit.RevisionId != original.RevisionId && current.Text == replacement && current.SegmentId == segment.SegmentId &&
            current.StartMs == segment.StartMs && current.EndMs == segment.EndMs && current.SpeakerId == segment.SpeakerId &&
            current.TextStart == segment.TextStart && current.TextLength == segment.TextLength && current.TextEditGroupId is not null &&
            JsonSerializer.Serialize(edit.Segments[1]) == JsonSerializer.Serialize(original.Segments[1]), "正文修改损失来源/身份/时码或改变其它段。");
        Need(edit.Corrections[^1].PreviousText == segment.Text && edit.Corrections[^1].ReplacementText == replacement &&
            edit.Corrections[^1].Operation == "edit-text", "人工正文修改没有独立before/after来源。");
        var repeated = SpeakerService.EditText(meeting, edit.RevisionId, current.SegmentId, "再校订𠀀没有下降。");
        Need(repeated.Segments[0].TextEditGroupId == current.TextEditGroupId && repeated.Corrections[^1].PreviousText == replacement &&
            SpeakerService.Load(meeting, edit.RevisionId).Segments[0].Text == replacement && SpeakerService.Load(meeting, original.RevisionId).Segments[0].Text == segment.Text,
            "反复修改覆盖父稿或变更源文字身份。");
        Need(M3TranscriptTests.Hash(PathOf(meeting, original)) == parentHash && M3TranscriptTests.Hash(Path.Combine(meeting, source.OriginalFile)) == sourceHash,
            "人物编辑改写原音文或父版本。");
    }

    private static void EditAfterSplit(string root)
    {
        var (meeting, _, original) = Fixture(root, "edit-after-split"); string a = original.Speakers[0].SpeakerId, b = original.Speakers[1].SpeakerId;
        var split = SpeakerService.SplitSegment(meeting, original.RevisionId, original.Segments[0].SegmentId, 3, 2000, a, b);
        var edit = SpeakerService.EditText(meeting, split.RevisionId, split.Segments[1].SegmentId, "人工修正𠀀乙方文字，不套原字长。");
        var renamed = SpeakerService.Rename(meeting, edit.RevisionId, b, "记录员");
        var assigned = SpeakerService.Assign(meeting, renamed.RevisionId, renamed.Segments[0].SegmentId, b);
        var outed = SpeakerService.SplitSpeaker(meeting, assigned.RevisionId, [assigned.Segments[0].SegmentId]);
        var merged = SpeakerService.Merge(meeting, outed.RevisionId, outed.Segments[0].SpeakerId!, b);
        var reopened = SpeakerService.Load(meeting, merged.RevisionId);
        Need(reopened.Segments[1].Text == edit.Segments[1].Text && reopened.Segments[1].TextStart == 3 &&
            reopened.Segments[1].TextLength == split.Segments[1].TextLength && reopened.Segments[1].StartMs == 2000 && reopened.Segments[1].EndMs == 6000 &&
            reopened.Segments[0].Text == split.Segments[0].Text && reopened.Segments[2].Text == original.Segments[1].Text &&
            SpeakerDisplay.Label(reopened, reopened.Segments[1]) == "记录员", "先切点再校字/改人/合并丢失手工修正或其它文字。");
    }

    private static void SplitAfterEdit(string root)
    {
        var (meeting, _, original) = Fixture(root, "split-after-edit"); string a = original.Speakers[0].SpeakerId, b = original.Speakers[1].SpeakerId;
        string replacement = "𠀀甲方第一段。乙方第二段。甲方第三段保留。";
        var edit = SpeakerService.EditText(meeting, original.RevisionId, original.Segments[0].SegmentId, replacement);
        Bad(() => SpeakerService.SplitSegment(meeting, edit.RevisionId, edit.Segments[0].SegmentId, 1, 2000, a, b));
        int firstCut = replacement.IndexOf('乙');
        var split = SpeakerService.SplitSegment(meeting, edit.RevisionId, edit.Segments[0].SegmentId, firstCut, 2000, a, b);
        int secondCut = split.Segments[1].Text.IndexOf('甲');
        var three = SpeakerService.SplitSegment(meeting, split.RevisionId, split.Segments[1].SegmentId, secondCut, 4000, b, a);
        Need(string.Concat(three.Segments.Take(3).Select(segment => segment.Text)) == replacement && three.Segments.Count == 4 &&
            three.Segments.Take(3).All(segment => segment.TextEditGroupId == edit.Segments[0].TextEditGroupId && segment.TextStart == 0 && segment.TextLength == original.Segments[0].TextLength) &&
            three.Segments.Take(3).Select(segment => segment.StartMs).SequenceEqual(new long?[] { 0, 2000, 4000 }) &&
            three.Segments.Take(3).Select(segment => segment.EndMs).SequenceEqual(new long?[] { 2000, 4000, 6000 }),
            "修改后的切点丢字/重字或把新字位置冒充原字时间。");
        var again = SpeakerService.EditText(meeting, three.RevisionId, three.Segments[1].SegmentId, "仅改中段🚀。");
        var final = SpeakerService.Load(meeting, again.RevisionId);
        Need(final.Segments[0].Text == three.Segments[0].Text && final.Segments[2].Text == three.Segments[2].Text &&
            final.Segments[3].Text == original.Segments[1].Text && final.Segments[1].Text == "仅改中段🚀。", "来源组子段再校字污染其它片段。");
        Bad(() => SpeakerService.SplitSegment(meeting, edit.RevisionId, edit.Segments[0].SegmentId, firstCut, 3000, a, b));
    }

    private static void Exports(string root)
    {
        var (meeting, _, original) = Fixture(root, "exports");
        var assigned = SpeakerService.Assign(meeting, original.RevisionId, original.Segments[0].SegmentId, original.Speakers[0].SpeakerId);
        var renamed = SpeakerService.Rename(meeting, assigned.RevisionId, assigned.Speakers[0].SpeakerId, "自定甲");
        var edit = SpeakerService.EditText(meeting, renamed.RevisionId, renamed.Segments[0].SegmentId, "校订专用𠀀文字。");
        var found = SpeakerSearchIndex.Search(meeting, "校订专用");
        Need(found.Count == 1 && found[0].RevisionId == edit.RevisionId && found[0].SpeakerLabel == "自定甲", "SQLite未查询到人物版人工正文及显示标签。");
        File.WriteAllBytes(Path.Combine(meeting, "diarization", "search.sqlite3"), [9, 8, 7]);
        Need(SpeakerSearchIndex.Search(meeting, "校订专用").SequenceEqual(found), "重建索引丢失人物版人工正文。");
        foreach (string extension in new[] { ".txt", ".srt", ".vtt" }) foreach (bool labels in new[] { false, true })
        {
            string path = Path.Combine(root, "m5b-edited-" + labels + extension);
            SpeakerSubtitleExporter.Export(meeting, edit.RevisionId, path, labels);
            string exported = File.ReadAllText(path);
            Need(exported.Contains("校订专用𠀀文字。") && exported.Contains(original.Segments[1].Text) &&
                exported.Contains("[说话人 自定甲]") == labels && !edit.Segments[0].Text.Contains("说话人"), "编辑正文导出标签污染/全文丢失。");
        }
    }

    private static void Legacy(string root)
    {
        foreach (string policy in new[] { "elapsed-v1", "pause-excluded-v1" })
        {
            var (meeting, _, original) = Fixture(root, "legacy-" + policy, policy);
            var json = JsonNode.Parse(File.ReadAllText(PathOf(meeting, original)))!.AsObject();
            json.Remove("parentRevisionSha256"); foreach (var segment in json["segments"]!.AsArray()) segment!.AsObject().Remove("textEditGroupId");
            File.WriteAllText(PathOf(meeting, original), json.ToJsonString(Json));
            var legacy = SpeakerService.Load(meeting, original.RevisionId);
            var edit = SpeakerService.EditText(meeting, legacy.RevisionId, legacy.Segments[0].SegmentId, "旧稿可校订。");
            Need(legacy.SchemaVersion == 1 && SpeakerService.Load(meeting, edit.RevisionId).Audio.TimelinePolicy == policy &&
                edit.Segments[0].StartMs == original.Segments[0].StartMs && edit.Segments[0].EndMs == original.Segments[0].EndMs,
                "旧schema或新旧暂停时间轴不兼容。");
        }
    }

    private static void Tamper(string root)
    {
        var (meeting, _, original) = Fixture(root, "tamper");
        var edit = SpeakerService.EditText(meeting, original.RevisionId, original.Segments[0].SegmentId, "合法人工正文。");
        string path = PathOf(meeting, edit); byte[] pristine = File.ReadAllBytes(path);
        var attacks = new SpeakerRevision[]
        {
            edit with { Segments = [edit.Segments[0] with { Text = "未声明篡改。" }, edit.Segments[1]] },
            edit with { Segments = [edit.Segments[0] with { SpeakerId = original.Speakers[0].SpeakerId, Status = "manual" }, edit.Segments[1]] },
            edit with { Segments = [edit.Segments[0] with { TextStart = 1 }, edit.Segments[1]] },
            edit with { Segments = [edit.Segments[0] with { TextEditGroupId = null }, edit.Segments[1]] },
            edit with { Segments = [edit.Segments[0]] },
            edit with { Corrections = [edit.Corrections[0] with { PreviousText = "伪造父稿。" }] },
            edit with { ParentRevisionSha256 = new string('0', 64) },
            edit with { SchemaVersion = 1 }
        };
        foreach (var attack in attacks)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(attack, Json)); Bad(() => SpeakerService.Load(meeting, edit.RevisionId));
            File.WriteAllBytes(path, pristine);
        }
        File.AppendAllText(PathOf(meeting, original), " "); Bad(() => SpeakerService.Load(meeting, edit.RevisionId));
    }

    private static void Invalid(string root)
    {
        var (meeting, _, original) = Fixture(root, "invalid"); int before = SpeakerService.ListRevisions(meeting).Count;
        foreach (string text in new[] { "", "\t\r\n", "bad\0text", "bad\ud800", new string('a', 100001) })
            Bad(() => SpeakerService.EditText(meeting, original.RevisionId, original.Segments[0].SegmentId, text));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { SpeakerService.EditText(meeting, original.RevisionId, original.Segments[0].SegmentId, "取消不发布。", cancel.Token); throw new Exception("已取消校字仍然保存。"); }
        catch (OperationCanceledException) { }
        Need(before == SpeakerService.ListRevisions(meeting).Count, "无效或取消编辑发布了人物版本。");
    }

    private static void Rerun(string root)
    {
        var (meeting, source, original) = Fixture(root, "rerun");
        var edit = SpeakerService.EditText(meeting, original.RevisionId, original.Segments[0].SegmentId, "校订正文保持。");
        string hash = M3TranscriptTests.Hash(PathOf(meeting, edit));
        var rerun = SpeakerService.CreateRevision(meeting, source.RevisionId, original.AnalysisId);
        var projection = SpeakerDisplay.AsTranscript(edit, source with
        {
            Alignment = new("unused/alignment.json", new string('a', 64), new string('b', 64), null, 16000, 160000, "elapsed-v1", "test")
        });
        Need(rerun.Segments[0].Text == original.Segments[0].Text && SpeakerService.Load(meeting, edit.RevisionId).Segments[0].Text == "校订正文保持。" &&
            M3TranscriptTests.Hash(PathOf(meeting, edit)) == hash && projection.Alignment is null && projection.Segments[0].Text == edit.Segments[0].Text,
            "重跑覆写人工稿或把改字后的显示投影标成旧token可定位。");
    }

    private static void ManualTimePolicies(string root)
    {
        foreach (string policy in new[] { "elapsed-v1", "pause-excluded-v1" })
        {
            var (meeting, source, original) = Fixture(root, "manual-time-" + policy, policy);
            var target = original.Segments[1]; string a = original.Speakers[0].SpeakerId, b = original.Speakers[1].SpeakerId;
            string parentHash = M3TranscriptTests.Hash(PathOf(meeting, original));
            Need(target.CandidateBoundaries.Count == 0, "Fixture needs a model-missed turn.");
            Bad(() => SpeakerService.SplitSegment(meeting, original.RevisionId, target.SegmentId, 4, 7654, a, b));
            var split = SpeakerService.SplitSegmentAtManualTime(meeting, original.RevisionId, target.SegmentId, 4, 7654, a, b);
            var loaded = SpeakerService.Load(meeting, split.RevisionId);
            Need(loaded.SchemaVersion == 2 && loaded.ParentRevisionSha256 == parentHash && loaded.ParentRevisionId == original.RevisionId &&
                loaded.Audio.TimelinePolicy == policy && loaded.Segments[1].StartMs == 7000 && loaded.Segments[1].EndMs == 7654 &&
                loaded.Segments[2].StartMs == 7654 && loaded.Segments[2].EndMs == 9000 &&
                loaded.Segments[1].SpeakerId == a && loaded.Segments[2].SpeakerId == b &&
                loaded.Segments.Skip(1).All(segment => segment.TimingSource == "human-entered-speaker-boundary" && segment.CandidateBoundaries.Count == 0) &&
                string.Concat(loaded.Segments.Select(segment => segment.Text)) == string.Concat(source.Segments.Select(segment => segment.Text)),
                "Explicit manual time was not isolated or lost text/source clock.");
            Need(loaded.Corrections[^1].Operation == "split-segment-manual-time" && loaded.Corrections[^1].TimeMs == 7654 &&
                loaded.Corrections[^1].TextOffset == 4 && M3TranscriptTests.Hash(PathOf(meeting, original)) == parentHash,
                "Manual time lacked provenance or overwrote its parent.");
            foreach (string extension in new[] { ".srt", ".vtt", ".txt" })
            {
                string path = Path.Combine(root, "manual-time-" + policy + extension);
                SpeakerSubtitleExporter.Export(meeting, loaded.RevisionId, path, true);
                string exported = File.ReadAllText(path);
                Need(exported.Contains("[说话人 A]") && exported.Contains("[说话人 B]") && exported.Contains(target.Text[..4]) && exported.Contains(target.Text[4..]),
                    "Manual time export dropped text or speaker labels.");
            }
            var hits = SpeakerSearchIndex.Search(meeting, target.Text[..4]);
            Need(hits.Any(hit => hit.RevisionId == loaded.RevisionId), "Manual cut was not indexed.");
            File.WriteAllBytes(Path.Combine(meeting, "diarization", "search.sqlite3"), [7, 8, 9]);
            Need(SpeakerSearchIndex.Search(meeting, target.Text[..4]).Any(hit => hit.RevisionId == loaded.RevisionId), "Index rebuild lost manual time revision.");
            SpeakerService.CreateRevision(meeting, source.RevisionId, original.AnalysisId);
            Need(SpeakerService.Load(meeting, loaded.RevisionId).Segments[1].EndMs == 7654, "Rerun replaced explicit manual time.");
        }
    }

    private static void ManualTimeEdited(string root)
    {
        var (meeting, _, original) = Fixture(root, "manual-time-edited");
        string a = original.Speakers[0].SpeakerId, b = original.Speakers[1].SpeakerId;
        var edited = SpeakerService.EditText(meeting, original.RevisionId, original.Segments[1].SegmentId, "嗯，后来继续说话。𠀀保留。");
        var split = SpeakerService.SplitSegmentAtManualTime(meeting, edited.RevisionId, edited.Segments[1].SegmentId, 2, 7345, b, a);
        var loaded = SpeakerService.Load(meeting, split.RevisionId);
        Need(loaded.Segments[1].Text + loaded.Segments[2].Text == edited.Segments[1].Text &&
            loaded.Segments.Skip(1).All(segment => segment.TextEditGroupId == edited.Segments[1].TextEditGroupId &&
                segment.TextStart == edited.Segments[1].TextStart && segment.TextLength == edited.Segments[1].TextLength) &&
            loaded.Segments[1].EndMs == 7345 && loaded.Segments[2].StartMs == 7345,
            "Manual time after text edit invented exact source-character mapping or lost words.");
        var renamed = SpeakerService.Rename(meeting, loaded.RevisionId, a, "人工命名");
        Need(SpeakerService.Load(meeting, renamed.RevisionId).Segments[2].Text == loaded.Segments[2].Text,
            "Later correction lost manual-cut edited text.");
    }

    private static void ManualTimeInvalid(string root)
    {
        var (meeting, _, original) = Fixture(root, "manual-time-invalid"); var target = original.Segments[1];
        string a = original.Speakers[0].SpeakerId, b = original.Speakers[1].SpeakerId;
        int before = SpeakerService.ListRevisions(meeting).Count;
        foreach (long time in new long[] { -1, 0, 7000, 9000, 9001, long.MaxValue })
            Bad(() => SpeakerService.SplitSegmentAtManualTime(meeting, original.RevisionId, target.SegmentId, 4, time, a, b));
        foreach (int offset in new[] { -1, 0, 3, target.Text.Length, target.Text.Length + 1 })
            Bad(() => SpeakerService.SplitSegmentAtManualTime(meeting, original.RevisionId, target.SegmentId, offset, 8000, a, b));
        Bad(() => SpeakerService.SplitSegmentAtManualTime(meeting, original.RevisionId, target.SegmentId, 4, 8000, Guid.NewGuid().ToString("N"), b));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { SpeakerService.SplitSegmentAtManualTime(meeting, original.RevisionId, target.SegmentId, 4, 8000, a, b, cancel.Token); throw new Exception("Cancelled manual cut published."); }
        catch (OperationCanceledException) { }
        Need(SpeakerService.ListRevisions(meeting).Count == before, "Invalid or cancelled manual cut published a version.");
    }

    private static void ManualTimeTamper(string root)
    {
        var (meeting, _, original) = Fixture(root, "manual-time-tamper"); var target = original.Segments[1];
        var split = SpeakerService.SplitSegmentAtManualTime(meeting, original.RevisionId, target.SegmentId, 4, 7654,
            original.Speakers[0].SpeakerId, original.Speakers[1].SpeakerId);
        string path = PathOf(meeting, split); byte[] pristine = File.ReadAllBytes(path);
        SpeakerRevision[] attacks =
        [
            split with { SchemaVersion = 1, ParentRevisionSha256 = null },
            split with { ParentRevisionSha256 = new string('0', 64) },
            split with { Corrections = [split.Corrections[^1] with { Operation = "split-segment" }] },
            split with { Corrections = [split.Corrections[^1] with { TimeMs = 8000 }] },
            split with { Segments = split.Segments.Select(segment => segment.TimingSource == "human-entered-speaker-boundary"
                ? segment with { TimingSource = "whisper-exact-token-boundary" } : segment).ToArray() }
        ];
        foreach (var attack in attacks)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(attack, Json)); Bad(() => SpeakerService.Load(meeting, split.RevisionId));
            File.WriteAllBytes(path, pristine);
        }
        File.AppendAllText(PathOf(meeting, original), " "); Bad(() => SpeakerService.Load(meeting, split.RevisionId));
    }

    internal static (string Meeting, TranscriptRevision Source, SpeakerRevision Revision) Fixture(string root, string name, string policy = "elapsed-v1")
    {
        string meeting = M3TranscriptTests.Meeting(root, name);
        string input = M3TranscriptTests.Input(meeting, "source.srt", "1\n00:00:00,000 --> 00:00:06,000\n甲方说乙方说再返回。\n\n2\n00:00:07,000 --> 00:00:09,000\n末段𠀀保留，否定词没有丢。\n");
        var source = TranscriptService.Import(new(meeting, name, 10000, input, IsSynthetic: true));
        var speakers = new[] { new MeetingSpeaker(Guid.NewGuid().ToString("N"), "A"), new MeetingSpeaker(Guid.NewGuid().ToString("N"), "B") };
        var analysis = SpeakerService.SaveAnalysis(meeting, new SpeakerAnalysis(1, name, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            new(new string('a', 64), new string('b', 64), 16000, 160000, policy),
            new("synthetic-text-fixture", "1", new string('c', 64), new string('d', 64), new string('e', 64), null, .9, "synthetic", "global-fixture"),
            speakers, [new(0, 2000, speakers[0].SpeakerId), new(2000, 4000, speakers[1].SpeakerId), new(4000, 6000, speakers[0].SpeakerId), new(7000, 9000, speakers[1].SpeakerId)], [], true));
        return (meeting, source, SpeakerService.CreateRevision(meeting, source.RevisionId, analysis.AnalysisId));
    }
    private static string PathOf(string meeting, SpeakerRevision revision) => Path.Combine(meeting, "diarization", "revisions", revision.RevisionId, "revision.json");
    private static void Need(bool condition, string message) => M3TranscriptTests.Need(condition, message);
    private static void Bad(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("未拒绝无效人工正文修订。"); }
}
