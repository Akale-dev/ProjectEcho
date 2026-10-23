using ProjectEcho.Core.Transcription;

internal static class M3pTranscriptTimingTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3p-timing"); Directory.CreateDirectory(root);
        run("T38/T39-SYNTH M3P untimed cues stay intact and navigation wraps", () => Navigation(root));
        run("T38-SYNTH M3P malformed draft timing is found without losing user input", () => InvalidDraft(root));
        run("T38/T39-SYNTH M3P timing diagnostics ignore text errors and valid overlaps", () => ValidTiming(root));
        run("T38/T39-SYNTH M3P verified audio bound finds and clears overrun", () => AudioBound(root));
        run("T38/T39-SYNTH M3P corrected timestamps save and export losslessly", () => CorrectAndExport(root));
        run("T38-SYNTH M3P long transcript finds late errors in original cue order", () => LongTranscript(root));
    }

    private static TranscriptRevision Fixture(string root, string name)
    {
        string directory = M3TranscriptTests.Meeting(root, name);
        return TranscriptService.Import(new(directory, name, 10000,
            M3TranscriptTests.Input(directory, "source.srt", "1\n00:00:01,123 --> 00:00:02,456\none\n\n2\n00:00:02,000 --> 00:00:03,456\ntwo\n\n3\n00:00:05,123 --> 00:00:06,456\nthree\n"), IsSynthetic: true));
    }

    private static void Navigation(string root)
    {
        var revision = Fixture(root, "navigation");
        var segments = revision.Segments.Select((s, i) => i > 0 ? s with { StartMs = null, EndMs = null, TimestampQuality = "untimed" } : s).ToArray();
        var draft = new TranscriptRevisionDraft(revision with { Segments = segments });
        Need(draft.FindTimingError() == segments[1], "First missing timestamp not found.");
        Need(draft.FindTimingError(segments[1].SegmentId) == segments[2] && draft.FindTimingError(segments[2].SegmentId) == segments[1], "Next/wrap failed.");
        Need(!draft.HasChanges && draft.BuildSegments().SequenceEqual(segments), "Finding errors changed original text/times.");
        draft.Set(segments[1], segments[1].Text, "00:00:02.000", "00:00:03.456");
        Need(draft.FindTimingError() == segments[2] && draft.FindTimingError(segments[2].SegmentId) == segments[2], "Corrected cue not skipped or only error did not wrap.");
    }

    private static void InvalidDraft(string root)
    {
        var revision = Fixture(root, "invalid"); var cue = revision.Segments[1]; var draft = new TranscriptRevisionDraft(revision);
        foreach (var (start, end) in new[] { ("00:00:", "00:00:03"), ("", "00:00:03"), ("00:00:03", ""),
            ("00:00:03", "00:00:03"), ("00:00:04", "00:00:03"), ("-00:00:01", "00:00:03"), ("00:00:03", "00:00:10.001") })
        {
            draft.Set(cue, "user draft", start, end);
            Need(draft.FindTimingError() == cue && draft.TimingErrorFor(cue) is not null &&
                draft.InputFor(cue) == new TranscriptSegmentEdit("user draft", start, end), "Invalid input disappeared or escaped diagnostics.");
        }
    }

    private static void ValidTiming(string root)
    {
        var revision = Fixture(root, "valid"); var draft = new TranscriptRevisionDraft(revision);
        Need(draft.FindTimingError() is null, "Valid overlap incorrectly flagged.");
        draft.Set(revision.Segments[0], "", "00:00:01.123", "00:00:02.456");
        Need(draft.FindTimingError() is null && !draft.TryGetSegment(revision.Segments[0], out _, out _), "Text-only error mislabeled as timestamp error.");
        var reversed = new TranscriptRevisionDraft(revision with { Segments = revision.Segments.Reverse().ToArray() });
        Need(reversed.FindTimingError() is null, "Exportable cue ordering incorrectly flagged.");
    }

    private static void AudioBound(string root)
    {
        var revision = Fixture(root, "audio"); var draft = new TranscriptRevisionDraft(revision);
        Need(draft.FindTimingError(verifiedAudioDurationMs: 6000) == revision.Segments[2], "Verified audio overrun not found.");
        Need(draft.FindTimingError(verifiedAudioDurationMs: 6456) is null, "Exact end bound flagged.");
        draft.Set(revision.Segments[2], "three", "00:00:05.123", "00:00:06.000");
        Need(draft.FindTimingError(verifiedAudioDurationMs: 6000) is null, "Fixed bound not cleared.");
    }

    private static void CorrectAndExport(string root)
    {
        var original = Fixture(root, "export"); string directory = Path.Combine(root, "export");
        var segments = original.Segments.Select((s, i) => i == 1 ? s with { StartMs = null, EndMs = null, TimestampQuality = "untimed" } : s).ToArray();
        var parent = TranscriptService.CreateEditedRevision(directory, original.RevisionId, segments);
        var hashes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(p => p, M3TranscriptTests.Hash);
        string srt = Path.Combine(root, "corrected.srt"); File.WriteAllText(srt, "existing export");
        M3TranscriptTests.Reject(() => TranscriptSubtitleExporter.Export(directory, parent.RevisionId, srt, overwrite: true));
        Need(File.ReadAllText(srt) == "existing export", "Rejected export overwrote destination.");
        var draft = new TranscriptRevisionDraft(parent); var cue = parent.Segments[1];
        draft.Set(cue, cue.Text, "00:00:02.123", "00:00:03.456");
        Need(draft.FindTimingError() is null, "Correction not recognized.");
        var saved = TranscriptService.CreateEditedRevision(directory, parent.RevisionId, draft.BuildSegments());
        foreach (string extension in new[] { "srt", "vtt" })
        {
            string file = Path.Combine(root, "corrected." + extension);
            TranscriptSubtitleExporter.Export(directory, saved.RevisionId, file, overwrite: true);
            var roundtrip = TranscriptService.Import(new(directory, "export", 10000, file, IsSynthetic: true));
            Need(roundtrip.Segments.Select(s => (s.Text, s.StartMs, s.EndMs)).SequenceEqual(saved.Segments.Select(s => (s.Text, s.StartMs, s.EndMs))), "Corrected export lost text or milliseconds.");
        }
        Need(hashes.All(p => M3TranscriptTests.Hash(p.Key) == p.Value), "Correction changed an existing original/revision.");
    }

    private static void LongTranscript(string root)
    {
        var parent = Fixture(root, "long");
        var segments = Enumerable.Range(0, 10000).Select(i => new TranscriptSegment("cue-" + i, i * 1000L, i * 1000L + 900, null, "unknown", "synthetic " + i, "approximate")).ToArray();
        segments[8765] = segments[8765] with { StartMs = null, EndMs = null, TimestampQuality = "untimed" };
        var draft = new TranscriptRevisionDraft(parent with { MeetingDurationMs = 10800000, Segments = segments });
        Need(draft.FindTimingError() == segments[8765] && draft.FindTimingError(segments[9999].SegmentId) == segments[8765], "Late three-hour-scale cue missed.");
        draft.Set(segments[8765], segments[8765].Text, "02:26:05.000", "02:26:05.900");
        Need(draft.FindTimingError() is null && draft.BuildSegments().Count == 10000, "Long correction lost cues.");
    }

    private static void Need(bool condition, string message) => M3TranscriptTests.Need(condition, message);
}
