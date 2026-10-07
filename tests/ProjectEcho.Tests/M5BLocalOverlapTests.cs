using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;

internal static class M5BLocalOverlapTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        run("M5B SYNTH a short overlapping interruption does not contaminate disjoint subtitle cues", LocalOverlap);
        run("M5B SYNTH nested overlaps and half-open cue boundaries keep uncertainty only where evidenced", NestedOverlap);
        run("M5B SYNTH first reliable voice survives a short interruption and excludes a fragment-only second group", ReliableGroups);
        run("M5B SYNTH subword token cuts and tiny speaker edge fragments remain complete for human confirmation", UnreliableCuts);
    }
    private static void LocalOverlap()
    {
        var (source, analysis) = Fixture();
        source = source with { Segments = [Cue("before", 1000, 2000), Cue("overlap", 2400, 2700), Cue("after", 3200, 5000)] };
        analysis = analysis with { Intervals = [new(841, 6106, "A", Overlap: true), new(2427, 2545, "B", "uncertain", true)] };
        var mapped = SpeakerMapper.Map(source, analysis);
        Need(mapped[0].SpeakerId == "A" && mapped[0].Status == "assigned" && mapped[2].SpeakerId == "A" && mapped[2].Status == "assigned", "Whole-interval overlap flag contaminated non-overlap cues.");
        Need(mapped[1].Status == "overlap-uncertain" && mapped[1].SpeakerId is null, "Actual short overlap became a confident single identity.");
        Need(string.Concat(mapped.Select(segment => segment.Text)) == string.Concat(source.Segments.Select(segment => segment.Text)), "Overlap mapping changed text.");
    }
    private static void NestedOverlap()
    {
        var (source, analysis) = Fixture();
        source = source with { Segments = [Cue("left", 100, 1000), Cue("inside", 1000, 1100), Cue("right", 1100, 1500)] };
        analysis = analysis with { Intervals = [new(0, 9000, "A", Overlap: true), new(100, 200, "A"), new(1000, 1100, "B", Overlap: true)] };
        var mapped = SpeakerMapper.Map(source, analysis);
        Need(mapped[0].Status == "assigned" && mapped[1].Status == "overlap-uncertain" && mapped[2].Status == "assigned", "Nested interval or half-open boundary mishandled.");
    }
    private static TranscriptSegment Cue(string id, long start, long end) => new(id, start, end, null, "mixed", "Synthetic complete " + id, "approximate");
    private static void ReliableGroups()
    {
        SherpaDiarizationRuntime.NativeInterval[] rows = [new(841, 6106, "A"), new(2427, 2545, "B"), new(7000, 7800, "C")];
        var usable = SherpaDiarizationRuntime.FirstUsableObservations(rows);
        Need(usable.Count == 2 && usable["A"] == 841 && usable["C"] == 7000 && !usable.ContainsKey("B"), "118ms fragment was promoted or a long partly overlapping voice was erased.");
        Need(SherpaDiarizationRuntime.OrderSpeakers(rows, SherpaDiarizationRuntime.DetectOverlaps(rows)).SequenceEqual(new[] { "A", "C", "B" }), "First reliable observation order ignored exclusive portions.");
        using var stop = new CancellationTokenSource(); stop.Cancel();
        try { SherpaDiarizationRuntime.FirstUsableObservations(rows, stop.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
    }
    private static void UnreliableCuts()
    {
        var (source, analysis) = Fixture();
        source = source with { Segments = [new("word", 0, 2000, null, "mixed", "We disagree.", "approximate")] };
        analysis = analysis with { Intervals = [new(0, 1000, "A"), new(1000, 2000, "B")] };
        var evidence = new SpeakerMappingEvidence(source.RevisionId, SpeakerService.TranscriptFingerprint(source), analysis.Audio,
            [new("word", 5, 1000, 1000, "whisper-exact-token-boundary")], []);
        var mapped = SpeakerMapper.Map(source, analysis, evidence);
        Need(mapped.Count == 1 && mapped[0].Text == "We disagree." && mapped[0].Status == "multiple-speakers", "A BPE split inside a word was used as a speaker boundary.");
        evidence = evidence with { Boundaries = [new("word", 3, 1000, 1000, "whisper-exact-token-boundary")] };
        Need(SpeakerMapper.Map(source, analysis, evidence).Count == 2, "A valid inter-word acoustic cut was blocked.");
        analysis = analysis with { Intervals = [new(0, 1900, "A"), new(1900, 2000, "B")] };
        evidence = evidence with { Boundaries = [new("word", 3, 1900, 1900, "whisper-exact-token-boundary")] };
        Need(SpeakerMapper.Map(source, analysis, evidence).Count == 1, "A 100ms edge fragment acquired a confident automatic text attribution.");
    }
    private static (TranscriptRevision, SpeakerAnalysis) Fixture() =>
        (new(2, true, "fixture", "revision", null, "fixture", null, DateTimeOffset.UtcNow, 10000, null, null, null, null, [], "unused", new('a', 64), null, [], []),
         new(1, "fixture", "analysis", DateTimeOffset.UtcNow, new(new('b', 64), null, 16000, 160000, "elapsed-v1"),
             new("synthetic", "1", new('c', 64), new('d', 64), new('e', 64), 2, .9, "synthetic", "global"), [new("A", "A"), new("B", "B")], [], [], true));
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
