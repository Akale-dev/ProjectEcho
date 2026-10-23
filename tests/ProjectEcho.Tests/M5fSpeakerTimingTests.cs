using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;

internal static class M5fSpeakerTimingTests
{
    internal static void Register(Action<string, Action> run, string root)
    {
        run("P5-03/T258-T260 M5F SYNTH timing correction retains speaker identity and immutable provenance", () => Correction(root));
        run("P5-03/T258-T260 M5F SYNTH invalid speaker draft timing remains findable and saves atomically", () => Invalid(root));
    }

    private static void Correction(string root)
    {
        var (meeting, source, original) = M5BSpeakerTextTests.Fixture(root, "m5f-speaker-timing");
        var named = SpeakerService.Rename(meeting, original.RevisionId, original.Speakers[1].SpeakerId, "本场乙");
        var cleared = SpeakerDisplay.AsTranscript(named, source).Segments.Select(s => s with
            { StartMs = null, EndMs = null, TimestampQuality = "untimed" }).ToArray();
        var parent = SpeakerService.EditSubtitles(meeting, named.RevisionId, cleared);
        var hashes = Directory.EnumerateFiles(meeting, "*", SearchOption.AllDirectories).ToDictionary(path => path, M3TranscriptTests.Hash);
        var draft = new TranscriptRevisionDraft(SpeakerDisplay.AsTranscript(parent, source));
        Need(draft.FindTimingError() == draft.Parent.Segments[0] && draft.FindTimingError(draft.Parent.Segments[^1].SegmentId) == draft.Parent.Segments[0],
            "Speaker presentation lost untimed errors or wrapping.");
        for (int i = 0; i < draft.Parent.Segments.Count; i++)
        {
            var cue = draft.Parent.Segments[i]; var prior = original.Segments[i];
            draft.Set(cue, cue.Text, TranscriptRevisionDraft.FormatTimestamp(prior.StartMs + 123), TranscriptRevisionDraft.FormatTimestamp(prior.EndMs + 321));
        }
        Need(draft.FindTimingError() is null, "Corrected speaker cue remains invalid.");
        var saved = SpeakerService.EditSubtitles(meeting, parent.RevisionId, draft.BuildSegments());
        var reopened = SpeakerService.Load(meeting, saved.RevisionId);
        Need(reopened.ParentRevisionId == parent.RevisionId && reopened.SourceTranscriptRevisionId == source.RevisionId &&
            reopened.AnalysisId == parent.AnalysisId && reopened.Speakers.SequenceEqual(parent.Speakers) &&
            reopened.Segments.Select(s => s.SpeakerId).SequenceEqual(parent.Segments.Select(s => s.SpeakerId)) &&
            reopened.Segments.Select(s => s.Text).SequenceEqual(parent.Segments.Select(s => s.Text)) &&
            reopened.Segments.All(s => s.ManualTiming && s.CandidateBoundaries.Count == 0), "Timing correction rewrote speaker identity/text/provenance.");
        foreach (string extension in new[] { "srt", "vtt" })
        {
            string output = SpeakerSubtitleExporter.Export(meeting, saved.RevisionId, Path.Combine(root, "m5f-speaker." + extension), true);
            Need(File.ReadAllText(output).Contains("本场乙") && File.ReadAllText(output).Contains(extension == "srt" ? "00:00:00,123" : "00:00:00.123"),
                "Speaker export lost display names or millisecond correction.");
        }
        Need(hashes.All(pair => M3TranscriptTests.Hash(pair.Key) == pair.Value), "Timing correction changed originals or parent revision.");
    }

    private static void Invalid(string root)
    {
        var (meeting, source, original) = M5BSpeakerTextTests.Fixture(root, "m5f-speaker-invalid");
        string revisionFile = Path.Combine(meeting, "diarization", "revisions", original.RevisionId, "revision.json");
        string originalHash = M3TranscriptTests.Hash(revisionFile);
        var draft = new TranscriptRevisionDraft(SpeakerDisplay.AsTranscript(original, source));
        var cue = draft.Parent.Segments[^1]; int before = SpeakerService.ListRevisions(meeting).Count;
        draft.Set(cue, "未保存人物正文", "00:00:09.000", "00:00:08.000");
        Need(draft.FindTimingError() == cue && draft.InputFor(cue).Text == "未保存人物正文", "Invalid speaker draft was lost or not found.");
        M3TranscriptTests.Reject(() => SpeakerService.EditSubtitles(meeting, original.RevisionId, draft.BuildSegments()));
        Need(SpeakerService.ListRevisions(meeting).Count == before, "Invalid timing wrote a speaker revision.");
        draft.Set(cue, cue.Text, TranscriptRevisionDraft.FormatTimestamp(cue.StartMs), TranscriptRevisionDraft.FormatTimestamp(cue.EndMs));
        Need(!draft.HasChanges && draft.FindTimingError() is null && draft.FindTimingError(verifiedAudioDurationMs: 8000) == cue,
            "Restoring original timing or verified audio bound failed.");
        var untouched = SpeakerService.Load(meeting, original.RevisionId);
        Need(M3TranscriptTests.Hash(revisionFile) == originalHash &&
            untouched.Segments.Select(s => (s.SegmentId, s.SpeakerId, s.Text, s.StartMs, s.EndMs)).SequenceEqual(
                original.Segments.Select(s => (s.SegmentId, s.SpeakerId, s.Text, s.StartMs, s.EndMs))), "Failed draft modified persisted speaker segments.");
    }

    private static void Need(bool condition, string message) => M3TranscriptTests.Need(condition, message);
}
