using System.Text.Json;
using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;

internal static class M5eSpeakerDraftTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    internal static void Register(Action<string, Action> run, string root)
    {
        run("P5-03 M5E SYNTH atomic batch text and time edits retain speakers source and parent", () => Batch(root));
        run("P5-03 M5E SYNTH batch validation cancellation and tampered provenance publish nothing", () => Invalid(root));
    }
    private static void Batch(string root)
    {
        var (meeting, source, original) = M5BSpeakerTextTests.Fixture(root, "m5e-batch");
        string hash = M3TranscriptTests.Hash(PathOf(meeting, original));
        var draft = new TranscriptRevisionDraft(SpeakerDisplay.AsTranscript(original, source));
        var first = draft.Parent.Segments[0]; var second = draft.Parent.Segments[1];
        draft.Set(first, "第一段人工校订。", "00:00:00.123", "00:00:06.321");
        draft.Set(second, "末段𠀀保留，新文字。", "00:00:07.123", "00:00:09.999");
        int before = SpeakerService.ListRevisions(meeting).Count;
        var saved = SpeakerService.EditSubtitles(meeting, original.RevisionId, draft.BuildSegments());
        var reopened = SpeakerService.Load(meeting, saved.RevisionId);
        Need(SpeakerService.ListRevisions(meeting).Count == before + 1 && reopened.Corrections[^1].SubtitleEdits?.Count == 2 &&
            reopened.ParentRevisionId == original.RevisionId && reopened.ParentRevisionSha256 == hash &&
            reopened.Segments.Select(s => s.SpeakerId).SequenceEqual(original.Segments.Select(s => s.SpeakerId)) &&
            reopened.Segments[0].StartMs == 123 && reopened.Segments[1].EndMs == 9999 &&
            reopened.Segments.All(s => s.ManualTiming && s.TimingSource == "human-edited-subtitle" && s.CandidateBoundaries.Count == 0),
            "Batch save lost source/identity or explicit manual timing.");
        var renamed = SpeakerService.Rename(meeting, saved.RevisionId, saved.Speakers[0].SpeakerId, "本场甲");
        Need(SpeakerService.Load(meeting, renamed.RevisionId).Segments[0].Text == "第一段人工校订。", "Later correction lost batch text.");
        var split = SpeakerService.SplitSegmentAtManualTime(meeting, renamed.RevisionId, renamed.Segments[0].SegmentId, 3, 3000,
            renamed.Speakers[0].SpeakerId, renamed.Speakers[1].SpeakerId);
        Need(SpeakerService.Load(meeting, split.RevisionId).Segments.Count == 3, "Manual split after timing edit failed.");
        string output = SpeakerSubtitleExporter.Export(meeting, saved.RevisionId, Path.Combine(root, "batch.srt"), true);
        Need(File.ReadAllText(output).Contains("00:00:00,123") && M3TranscriptTests.Hash(PathOf(meeting, original)) == hash,
            "Export lost manual precision or original changed.");
        var untimed = SpeakerDisplay.AsTranscript(saved, source).Segments.Select(s => s with { StartMs = null, EndMs = null, TimestampQuality = "untimed" }).ToArray();
        var clear = SpeakerService.EditSubtitles(meeting, saved.RevisionId, untimed);
        Need(SpeakerService.Load(meeting, clear.RevisionId).Segments.All(s => s.StartMs is null), "Explicit time clearing was not retained.");
        Bad(() => SpeakerSubtitleExporter.Export(meeting, clear.RevisionId, Path.Combine(root, "untimed.srt"), true));
    }
    private static void Invalid(string root)
    {
        var (meeting, source, original) = M5BSpeakerTextTests.Fixture(root, "m5e-batch-invalid");
        var segments = SpeakerDisplay.AsTranscript(original, source).Segments;
        int before = SpeakerService.ListRevisions(meeting).Count;
        Bad(() => SpeakerService.EditSubtitles(meeting, original.RevisionId, segments.Reverse().ToArray()));
        Bad(() => SpeakerService.EditSubtitles(meeting, original.RevisionId, segments.Select((s, i) => i == 1 ? s with { EndMs = 10001 } : s).ToArray()));
        Bad(() => SpeakerService.EditSubtitles(meeting, original.RevisionId, segments.Select((s, i) => i == 0 ? s with { SpeakerLabel = "伪造姓名" } : s).ToArray()));
        var edits = segments.Select(s => s with { Text = s.Text + "校订", StartMs = s.StartMs + 123, EndMs = s.EndMs + 321 }).ToArray();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { SpeakerService.EditSubtitles(meeting, original.RevisionId, edits, cancel.Token); throw new Exception("Cancelled batch published."); }
        catch (OperationCanceledException) { }
        Need(SpeakerService.ListRevisions(meeting).Count == before, "Invalid batch published a partial version.");
        var saved = SpeakerService.EditSubtitles(meeting, original.RevisionId, edits);
        string path = PathOf(meeting, saved); byte[] pristine = File.ReadAllBytes(path);
        foreach (var attack in new[]
        {
            saved with { Segments = saved.Segments.Select((s, i) => i == 0 ? s with { ManualTiming = false } : s).ToArray() },
            saved with { Segments = saved.Segments.Select((s, i) => i == 0 ? s with { StartMs = 111 } : s).ToArray() },
            saved with { ParentRevisionSha256 = new string('0', 64) },
            saved with { Corrections = [saved.Corrections[^1] with { SubtitleEdits = [saved.Corrections[^1].SubtitleEdits![0]] }] }
        })
        {
            File.WriteAllText(path, JsonSerializer.Serialize(attack, Json)); Bad(() => SpeakerService.Load(meeting, saved.RevisionId));
            File.WriteAllBytes(path, pristine);
        }
    }
    private static string PathOf(string meeting, SpeakerRevision revision) => Path.Combine(meeting, "diarization", "revisions", revision.RevisionId, "revision.json");
    private static void Need(bool condition, string message) => M3TranscriptTests.Need(condition, message);
    private static void Bad(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid batch accepted."); }
}
