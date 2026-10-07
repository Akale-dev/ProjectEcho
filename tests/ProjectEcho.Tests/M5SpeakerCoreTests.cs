using System.Text;
using System.Text.Json;
using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;

internal static class M5SpeakerCoreTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m5-speaker-core"); Directory.CreateDirectory(root);
        run("P5-02 SYNTH exact text boundaries split without losing negation punctuation or Unicode", () => Reliable(root));
        run("P5-02 SYNTH missing ambiguous and mismatched token evidence preserves all text", () => Fallback(root));
        run("P5-02 SYNTH overlap short utterance no speech and untimed retain text without invented identities", () => Uncertain(root));
        run("P5-03 SYNTH rename assign merge split and manual boundary produce immutable revisions", () => Corrections(root));
        run("P5-03 SYNTH rerun retains human names identities assignments and original analysis", () => Rerun(root));
        run("P5-03 SYNTH labels export separately and plain subtitle reimport never infers identity", () => Export(root));
        run("P5-03 SYNTH cancelled save and export leave committed data and destinations untouched", () => Cancel(root));
        run("P5-02 SYNTH Whisper exact saved tokens bind wave policy and reject manually changed words", () => Whisper(root));
        run("P5-02 SYNTH Whisper merged decode windows and punctuation-split sentences retain real token lineage", () => WhisperMerged(root));
        run("P5-02 SYNTH Qwen raw word reuse applies one window offset with hash bound original text", () => Qwen(root));
        run("P5-02 SYNTH source waveform and timeline policy mismatch are rejected", () => Binding(root));
        run("P5-03 SYNTH corrupt source analysis and out-of-range source text do not load", () => Integrity(root));
        run("P5-02 SYNTH three hour full text mapping preserves final utterance and meeting-local repeated IDs", () => Long(root));
    }

    private static void Reliable(string root)
    {
        var (meeting, source, analysis) = Fixture(root, "reliable", "不是12%，是13%。𠀀保留。", [new(0, 2000, "A"), new(2000, 4000, "B")]);
        var evidence = new SpeakerMappingEvidence(source.RevisionId, SpeakerService.TranscriptFingerprint(source), analysis.Audio,
            [new(source.Segments[0].SegmentId, 6, 2000, 2000, "synthetic-verified-boundary")], []);
        var segments = SpeakerMapper.Map(source, analysis, evidence);
        Need(segments.Count == 2 && string.Concat(segments.Select(s => s.Text)) == source.Segments[0].Text &&
            segments[0].Text == "不是12%，" && segments[1].Text == "是13%。𠀀保留。" && segments[0].EndMs == 2000 && segments[1].StartMs == 2000,
            "Reliable boundary lost text or allocated time by length.");
        Need(segments.All(s => s.Status == "assigned") && segments[0].SpeakerId != segments[1].SpeakerId, "Verified turn labels missing.");
        Need(TranscriptService.Load(meeting, source.RevisionId).Segments.Count == 1, "Mapping rewrote input.");
    }

    private static void Fallback(string root)
    {
        var (_, source, analysis) = Fixture(root, "fallback", "不是12%，是13%。", [new(0, 2000, "A"), new(2000, 4000, "B")]);
        var plain = SpeakerMapper.Map(source, analysis);
        Need(plain.Count == 1 && plain[0].Text == source.Segments[0].Text && plain[0].Status == "multiple-speakers" && plain[0].CandidateBoundaries.Single().TimeMs == 2000,
            "Missing evidence guessed a text cut.");
        var ambiguous = new SpeakerMappingEvidence(source.RevisionId, SpeakerService.TranscriptFingerprint(source), analysis.Audio,
            [new(source.Segments[0].SegmentId, 5, 1970, 2000, "fixture"), new(source.Segments[0].SegmentId, 6, 2000, 2030, "fixture")], []);
        Need(SpeakerMapper.Map(source, analysis, ambiguous).Count == 1, "Ambiguous acoustic boundary guessed a cut.");
        Bad(() => SpeakerMapper.Map(source, analysis, ambiguous with { TranscriptRevisionId = Guid.NewGuid().ToString("N") }));
        Bad(() => SpeakerMapper.Map(source, analysis, ambiguous with { Audio = analysis.Audio with { TimelinePolicy = "pause-excluded-v1" } }));
    }

    private static void Uncertain(string root)
    {
        var (_, source, analysis) = Fixture(root, "uncertain", "短答复与完整文字。", [new(0, 2500, "A"), new(2000, 4000, "B")]);
        var overlap = SpeakerMapper.Map(source, analysis).Single(); Need(overlap.Status == "overlap-uncertain" && overlap.SpeakerId is null && overlap.Text == source.Segments[0].Text, "Overlap invented independent utterances.");
        var shortAnalysis = analysis with { Intervals = [analysis.Intervals[0] with { StartMs = 100, EndMs = 180 }] };
        Need(SpeakerMapper.Map(source, shortAnalysis).Single().Status == "unconfirmed", "80ms assigned a confident speaker.");
        Need(SpeakerMapper.Map(source, analysis with { Intervals = [] }).Single().Text == source.Segments[0].Text, "No speech deleted subtitle.");
        var untimed = source with { Segments = [source.Segments[0] with { StartMs = null, EndMs = null, TimestampQuality = "untimed" }] };
        var mapped = SpeakerMapper.Map(untimed, analysis).Single(); Need(mapped.Status == "untimed" && mapped.StartMs is null && mapped.SpeakerId is null, "Untimed text acquired inferred time/identity.");
    }

    private static void Corrections(string root)
    {
        var (meeting, source, analysis) = Fixture(root, "corrections", "不是12%，是13%。", [new(0, 2000, "A"), new(2000, 4000, "B")]);
        var original = SpeakerService.CreateRevision(meeting, source.RevisionId, analysis.AnalysisId); string a = original.Speakers[0].SpeakerId, b = original.Speakers[1].SpeakerId;
        var split = SpeakerService.SplitSegment(meeting, original.RevisionId, original.Segments[0].SegmentId, 6, 2000, a, b);
        var renamed = SpeakerService.Rename(meeting, split.RevisionId, a, "主持人甲");
        Need(renamed.Speakers[0].SpeakerId == a && renamed.Segments[0].Text == "不是12%，" && SpeakerDisplay.Label(renamed, renamed.Segments[0]) == "主持人甲", "Rename changed identity or text.");
        var assigned = SpeakerService.Assign(meeting, renamed.RevisionId, renamed.Segments[1].SegmentId, a);
        var outed = SpeakerService.SplitSpeaker(meeting, assigned.RevisionId, [assigned.Segments[1].SegmentId]);
        string c = outed.Segments[1].SpeakerId!; Need(c != a && c != b && outed.Speakers.Single(s => s.SpeakerId == c).Code == "C", "Split out reused identity.");
        var merged = SpeakerService.Merge(meeting, outed.RevisionId, c, a);
        var again = SpeakerService.SplitSpeaker(meeting, merged.RevisionId, [merged.Segments[1].SegmentId]);
        Need(again.Speakers.Single(s => s.SpeakerId == again.Segments[1].SpeakerId).Code == "D", "Reused retired display code.");
        Need(string.Concat(again.Segments.Select(s => s.Text)) == source.Segments[0].Text && SpeakerService.Load(meeting, split.RevisionId).Segments[1].SpeakerId == b, "Correction changed prior revision/text.");
        Bad(() => SpeakerService.SplitSegment(meeting, original.RevisionId, original.Segments[0].SegmentId, 6, 1800, a, b));
        Bad(() => SpeakerService.Rename(meeting, original.RevisionId, a, "bad\nname"));
        Bad(() => SpeakerService.Assign(meeting, original.RevisionId, original.Segments[0].SegmentId, Guid.NewGuid().ToString("N")));
    }

    private static void Rerun(string root)
    {
        var (meeting, source, analysis) = Fixture(root, "rerun", "全文原文。", [new(0, 4000, "A")]);
        var first = SpeakerService.CreateRevision(meeting, source.RevisionId, analysis.AnalysisId);
        var manual = SpeakerService.Rename(meeting, first.RevisionId, first.Speakers[0].SpeakerId, "用户命名");
        string hash = M3TranscriptTests.Hash(Path.Combine(meeting, "diarization", "revisions", manual.RevisionId, "revision.json"));
        var speaker = new MeetingSpeaker(Guid.NewGuid().ToString("N"), "A");
        var rerun = SpeakerService.SaveAnalysis(meeting, analysis with { AnalysisId = Guid.NewGuid().ToString("N"), Speakers = [speaker], Intervals = [new(0, 4000, speaker.SpeakerId)] });
        var second = SpeakerService.CreateRevision(meeting, source.RevisionId, rerun.AnalysisId);
        Need(second.Speakers[0].DisplayName is null && second.Speakers[0].SpeakerId != first.Speakers[0].SpeakerId &&
            SpeakerService.Load(meeting, manual.RevisionId).Speakers[0].DisplayName == "用户命名" && hash == M3TranscriptTests.Hash(Path.Combine(meeting, "diarization", "revisions", manual.RevisionId, "revision.json")), "Rerun replaced manual revision.");
    }

    private static void Export(string root)
    {
        var (meeting, source, analysis) = Fixture(root, "exports", "中文与English🚀没有下降。", [new(0, 4000, "A")]);
        var version = SpeakerService.CreateRevision(meeting, source.RevisionId, analysis.AnalysisId);
        version = SpeakerService.Rename(meeting, version.RevisionId, version.Speakers[0].SpeakerId, "发言甲");
        foreach (string ext in new[] { ".txt", ".srt", ".vtt" }) foreach (bool labels in new[] { false, true })
        {
            string output = Path.Combine(root, "export-" + labels + ext);
            SpeakerSubtitleExporter.Export(meeting, version.RevisionId, output, labels);
            string text = File.ReadAllText(output); Need(text.Contains(source.Segments[0].Text) && text.Contains("[说话人 发言甲]") == labels, "Export label switch changed body.");
            if (ext != ".txt")
            {
                var imported = TranscriptService.Import(new(meeting, source.MeetingId, 10000, output, IsSynthetic: true));
                Need(imported.Segments[0].SpeakerLabel is null && imported.Segments[0].StartMs == 0 && imported.Segments[0].EndMs == 4000, "Reimport inferred a speaker or shifted time.");
            }
        }
        Bad(() => SpeakerSubtitleExporter.Export(meeting, version.RevisionId, Path.Combine(meeting, "forbidden.srt")));
    }

    private static void Cancel(string root)
    {
        var (meeting, source, analysis) = Fixture(root, "cancel", "完整原文。", [new(0, 4000, "A")]);
        var revision = SpeakerService.CreateRevision(meeting, source.RevisionId, analysis.AnalysisId);
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); int count = SpeakerService.ListRevisions(meeting).Count;
        Cancelled(() => SpeakerService.Rename(meeting, revision.RevisionId, revision.Speakers[0].SpeakerId, "取消", cancel.Token));
        string destination = M3TranscriptTests.Input(root, "cancelled.txt", "已有目标");
        Cancelled(() => SpeakerSubtitleExporter.Export(meeting, revision.RevisionId, destination, true, cancel.Token, true));
        Need(File.ReadAllText(destination) == "已有目标" && SpeakerService.ListRevisions(meeting).Count == count, "Cancel overwrote authoritative result.");
    }

    private static void Whisper(string root)
    {
        var (meeting, source, analysis) = Fixture(root, "whisper-mapping", "不是下降，是增长。", [new(0, 2000, "A"), new(2000, 4000, "B")]);
        byte[] evidence = JsonSerializer.SerializeToUtf8Bytes(new { model = new { multilingual = true }, transcription = new[] { new
        { text = source.Segments[0].Text, offsets = new { from = 0, to = 4000 }, tokens = new[] {
            new { id = 100, text = "不是下降，", offsets = new { from = 0, to = 2000 } }, new { id = 100, text = "是增长。", offsets = new { from = 2000, to = 4000 } } } } } });
        source = WithEvidence(meeting, source, analysis.Audio, "whisper-exact-token-boundaries-v1", evidence);
        var mapping = SpeakerAlignmentReader.Read(meeting, source, analysis.Audio); Need(mapping.Boundaries.Count == 1 && mapping.Boundaries[0].TextOffset == 5, "Whisper reliable token not reused.");
        Need(SpeakerService.CreateRevision(meeting, source.RevisionId, analysis.AnalysisId).Segments.Count == 2, "Whisper token did not split turn.");
        var edit = TranscriptService.CreateRevision(meeting, source.RevisionId, [source.Segments[0] with { Text = "是增长。" }]);
        Need(SpeakerAlignmentReader.Read(meeting, edit, analysis.Audio).Boundaries.Count == 0, "Manually shortened text borrowed old token positions.");
    }

    private static void Qwen(string root)
    {
        var (meeting, source, analysis) = Fixture(root, "qwen-mapping", "甲方，乙方。", [new(0, 2000, "A"), new(2000, 4000, "B")]);
        source = source with { Segments = [source.Segments[0] with { StartMs = 1000, EndMs = 5000 }] };
        analysis = SpeakerService.SaveAnalysis(meeting, analysis with { AnalysisId = Guid.NewGuid().ToString("N"), Intervals = [new(1000, 3000, analysis.Speakers[0].SpeakerId), new(3000, 5000, analysis.Speakers[1].SpeakerId)] });
        string attemptId = Guid.NewGuid().ToString("N"), attempt = Path.Combine(meeting, "transcripts", "asr", attemptId); Directory.CreateDirectory(attempt);
        string words = Path.Combine(attempt, "words.json"); File.WriteAllText(words, JsonSerializer.Serialize(new[] {
            new { word = "甲方", start_sample = 0, end_sample = 32000 }, new { word = "乙方", start_sample = 32000, end_sample = 64000 } }));
        byte[] evidence = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "m4-qwen-alignment-1", expectedWaveSha256 = analysis.Audio.InputWaveSha256, timelinePolicy = analysis.Audio.TimelinePolicy,
            attempt = attemptId, windowEvidence = new[] { new { window = new { inputStartFrame = 16000, inputFrameCount = 64000 },
                evidence = new { originalTextSha256 = QwenWindowAlignment.TextSha256(source.Segments[0].Text) }, rawWords = "words.json", rawWordsSha256 = M3TranscriptTests.Hash(words),
                sentences = new[] { new { text = source.Segments[0].Text, textStart = 0, startMs = 1000, endMs = 5000 } } } } });
        source = WithEvidence(meeting, source, analysis.Audio, QwenTranscriber.Policy, evidence);
        var map = SpeakerAlignmentReader.Read(meeting, source, analysis.Audio);
        Need(map.Boundaries.Count == 1 && map.Boundaries[0].LeftEndMs == 3000 && map.Boundaries[0].RightStartMs == 3000 && map.Boundaries[0].TextOffset == 3, "Qwen applied wrong or repeated window offset.");
        var result = SpeakerService.CreateRevision(meeting, source.RevisionId, analysis.AnalysisId);
        Need(result.Segments.Count == 2 && string.Concat(result.Segments.Select(s => s.Text)) == source.Segments[0].Text, "Qwen split lost punctuation.");
        File.AppendAllText(words, " "); Need(SpeakerAlignmentReader.Read(meeting, source, analysis.Audio).Boundaries.Count == 0, "Qwen changed raw hash accepted.");
    }

    private static void WhisperMerged(string root)
    {
        foreach (string variant in new[] { "english-comma", "basic" })
        {
            string meeting = M3TranscriptTests.Meeting(root, "whisper-lineage-" + variant), wave = Path.Combine(meeting, "input.wav");
            using (var writer = new BinaryWriter(File.Create(wave)))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(96036); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(96000);
                for (int i = 0; i < 48000; i++) writer.Write((short)(i % 100));
            }
            string model = M3TranscriptTests.Input(meeting, "fixture.bin", "synthetic-sentence-" + variant);
            var request = new WhisperRequest(meeting, Path.GetFileName(meeting), 3000, wave, M3TranscriptTests.Hash(wave),
                new(Path.Combine(AppContext.BaseDirectory, "ProjectEcho.Tests.exe"), model, ModelLicenseAcknowledged: true), true,
                new string('f', 64), "pause-excluded-v1");
            var result = WhisperTranscriber.RunAsync(request).GetAwaiter().GetResult();
            Need(result.Status == "Succeeded" && result.Revision?.Alignment is not null, "Whisper fixture failed: " + result.Message);
            var mapping = SpeakerAlignmentReader.Read(meeting, result.Revision!, new(request.ExpectedWaveSha256, request.SourceMixSha256, 16000, 48000, request.TimelinePolicy));
            Need(mapping.Boundaries.Count > 0, "Merged/split Whisper token lineage missing.");
            if (variant == "english-comma") Need(result.Revision!.Segments.Single().Text == "Hello, world." && mapping.Boundaries.Single().TextOffset == 7 &&
                mapping.Boundaries[0].LeftEndMs == 400 && mapping.Boundaries[0].RightStartMs == 400, "English joining space invented or shifted a word time.");
            Need(result.Revision!.Alignment!.TimelinePolicy == "pause-excluded-v1", "New pause timeline policy was lost in persisted Whisper token provenance.");
        }
    }

    private static void Binding(string root)
    {
        var (meeting, source, analysis) = Fixture(root, "binding", "完整原文。", [new(0, 4000, "A")]);
        source = WithEvidence(meeting, source, analysis.Audio, "unknown-policy", Encoding.UTF8.GetBytes("{}"));
        var wrongPolicy = SpeakerService.SaveAnalysis(meeting, analysis with { AnalysisId = Guid.NewGuid().ToString("N"), Audio = analysis.Audio with { TimelinePolicy = "pause-excluded-v1" } });
        Bad(() => SpeakerService.CreateRevision(meeting, source.RevisionId, wrongPolicy.AnalysisId));
        var wrongAudio = SpeakerService.SaveAnalysis(meeting, analysis with { AnalysisId = Guid.NewGuid().ToString("N"), Audio = analysis.Audio with { InputWaveSha256 = new string('f', 64) } });
        Bad(() => SpeakerService.CreateRevision(meeting, source.RevisionId, wrongAudio.AnalysisId));
        var valid = SpeakerService.CreateRevision(meeting, source.RevisionId, analysis.AnalysisId); Need(valid.Audio.TimelinePolicy == "elapsed-v1", "Legacy elapsed policy removed pauses.");
    }

    private static void Integrity(string root)
    {
        var (meeting, source, analysis) = Fixture(root, "integrity", "全文不得重复。", [new(0, 4000, "A")]);
        var revision = SpeakerService.CreateRevision(meeting, source.RevisionId, analysis.AnalysisId);
        string path = Path.Combine(meeting, "diarization", "revisions", revision.RevisionId, "revision.json");
        File.WriteAllText(path, JsonSerializer.Serialize(revision with { Segments = [revision.Segments[0] with { TextStart = 1 }] }, Json)); Bad(() => SpeakerService.Load(meeting, revision.RevisionId));
        File.WriteAllText(path, JsonSerializer.Serialize(revision, Json));
        string analysisPath = Path.Combine(meeting, "diarization", "analyses", analysis.AnalysisId, "analysis.json"); File.AppendAllText(analysisPath, " "); Bad(() => SpeakerService.Load(meeting, revision.RevisionId));
        File.AppendAllText(Path.Combine(meeting, source.OriginalFile), "变更"); Bad(() => SpeakerService.Load(meeting, revision.RevisionId));
        File.WriteAllText(Path.Combine(meeting, "meeting.json"), "{\"meetingId\":\"other\",\"isSynthetic\":true}");
        Bad(() => SpeakerService.LoadAnalysis(meeting, analysis.AnalysisId));
    }

    private static void Long(string root)
    {
        var (_, original, analysis) = Fixture(root, "long", "占位。", [new(0, 4000, "A"), new(4000, 8000, "B")]);
        var transcript = original with { MeetingDurationMs = 10800000, Segments = Enumerable.Range(0, 5400).Select(i =>
            new TranscriptSegment("s" + i, i * 2000L, (i + 1) * 2000L, null, "unknown", $"第{i}句，不是零。", "approximate")).ToArray() };
        analysis = analysis with { Audio = analysis.Audio with { FrameCount = 10800L * 16000 }, Intervals = transcript.Segments.Select((s, i) =>
            new SpeakerInterval(s.StartMs!.Value, s.EndMs!.Value, analysis.Speakers[i % 2].SpeakerId)).ToArray() };
        var result = SpeakerMapper.Map(transcript, analysis);
        Need(result.Count == 5400 && result[^1].EndMs == 10800000 && result.All(s => s.Status == "assigned") &&
            string.Concat(result.Select(s => s.Text)) == string.Concat(transcript.Segments.Select(s => s.Text)) && result[0].SpeakerId == result[5398].SpeakerId,
            "Three-hour text mapping lost tail or reset global identities.");
    }

    private static (string Meeting, TranscriptRevision Source, SpeakerAnalysis Analysis) Fixture(string root, string name, string text, IReadOnlyList<SpeakerInterval> intervals)
    {
        string meeting = M3TranscriptTests.Meeting(root, name);
        var source = TranscriptService.Import(new(meeting, name, 10000, M3TranscriptTests.Input(meeting, "source.srt", "1\n00:00:00,000 --> 00:00:04,000\n" + text), IsSynthetic: true));
        var speakers = intervals.Select(i => i.SpeakerId).Where(id => id is not null).Distinct().Select((id, index) => new MeetingSpeaker(Guid.NewGuid().ToString("N"), id!)).ToArray();
        var analysis = SpeakerService.SaveAnalysis(meeting, new(1, name, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            new(new string('a', 64), new string('b', 64), 16000, 160000, "elapsed-v1"), new("fixture", "1", new string('c', 64), new string('d', 64), new string('e', 64), null, .9, "synthetic", "fixture-global"),
            speakers, intervals.Select(i => i with { SpeakerId = i.SpeakerId is null ? null : speakers.Single(s => s.Code == i.SpeakerId).SpeakerId }).ToArray(), [], true));
        return (meeting, source, analysis);
    }

    private static TranscriptRevision WithEvidence(string meeting, TranscriptRevision source, SpeakerAudioBinding audio, string policy, byte[] evidence)
    {
        string path = Path.Combine(meeting, "transcripts", "original", source.RevisionId, "alignment.json"); File.WriteAllBytes(path, evidence);
        source = source with { Alignment = new("transcripts/original/" + source.RevisionId + "/alignment.json", M3TranscriptTests.Hash(path), audio.InputWaveSha256, audio.SourceMixSha256, audio.SampleRate, audio.FrameCount, audio.TimelinePolicy, policy) };
        File.WriteAllText(Path.Combine(meeting, "transcripts", "original", source.RevisionId, "revision.json"), JsonSerializer.Serialize(source, Json)); return source;
    }
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Bad(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Invalid mapping accepted."); }
    private static void Cancelled(Action action) { try { action(); } catch (OperationCanceledException) { return; } throw new InvalidOperationException("Cancelled operation committed."); }
}
