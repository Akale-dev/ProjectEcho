using ProjectEcho.Core.Diarization;

internal static class M5SpeakerRuntimeTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        run("P5-04 SYNTH native diarization parsing rejects malformed reversed and out-of-bounds intervals", Parse);
        run("P5-04 SYNTH overlap sweeps match exhaustive truth with 50000 intervals and cancellation", Overlaps);
        run("P5-04 SYNTH native configuration rejects absent untrusted options without downloading", () => Inspect(output));
        run("P5-01 SYNTH code order follows first usable nonoverlapping observation with short-only group last", Order);
    }
    private static void Parse()
    {
        var rows = SherpaDiarizationRuntime.ParseOutput("OfflineSpeakerDiarizationConfig(...)\r\nStarted\n0.010 -- 0.210 speaker_00\n0.100 -- 0.310 speaker_01\n0.500 -- 0.700 speaker_00\n", 700);
        Need(rows.Count == 3 && rows[0].StartMs == 10 && rows[2].Speaker == rows[0].Speaker, "Raw identity/time changed.");
        foreach (string raw in new[] { "0.000 -- 0.000 speaker_00", "0.000 -- 0.701 speaker_00", "0.300 -- 0.500 speaker_00\n0.200 -- 0.400 speaker_01", "NaN -- 0.500 speaker_00", "0.00 -- 0.500 speaker_00", "-0.001 -- 0.500 speaker_00", "0.000 -- 0.500 speaker_x" })
            Bad(() => SherpaDiarizationRuntime.ParseOutput(raw, 700));
        Need(SherpaDiarizationRuntime.ParseOutput("Started\n", 1000).Count == 0, "No results fabricated an identity.");
    }
    private static void Overlaps()
    {
        var rng = new Random(505);
        var sample = Enumerable.Range(0, 500).Select(i => new SherpaDiarizationRuntime.NativeInterval(i * 11, i * 11 + rng.Next(1, 300), (i % 7).ToString())).ToArray();
        bool[] result = SherpaDiarizationRuntime.DetectOverlaps(sample);
        for (int i = 0; i < sample.Length; i++) Need(result[i] == sample.Any(other => other.Speaker != sample[i].Speaker && other.StartMs < sample[i].EndMs && other.EndMs > sample[i].StartMs), "Sweep differs from interval truth.");
        var large = Enumerable.Range(0, 50000).Select(i => new SherpaDiarizationRuntime.NativeInterval(i * 11, i * 11 + 10, (i % 7).ToString())).ToArray();
        Need(SherpaDiarizationRuntime.DetectOverlaps(large).All(value => !value), "Adjacent ranges became overlap.");
        using var stop = new CancellationTokenSource(); stop.Cancel();
        try { SherpaDiarizationRuntime.DetectOverlaps(large, stop.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
    }
    private static void Inspect(string output)
    {
        string dir = Path.Combine(output, "m5-runtime-config"); Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "trusted-placeholder.exe"), model = Path.Combine(dir, "model.onnx");
        File.WriteAllText(exe, "configuration test only, never executed"); File.WriteAllText(model, "configuration test only");
        var config = new SherpaDiarizationConfiguration(exe, model, model, ModelLicenseAcknowledged: true);
        Need(SherpaDiarizationRuntime.Inspect(config).Ready, "Valid configuration rejected before runtime verification.");
        Need(!SherpaDiarizationRuntime.Inspect(null).Ready && !SherpaDiarizationRuntime.Inspect(config with { Threads = 17 }).Ready &&
            !SherpaDiarizationRuntime.Inspect(config with { KnownSpeakerCount = 0 }).Ready && !SherpaDiarizationRuntime.Inspect(config with { KnownSpeakerCount = 65 }).Ready &&
            !SherpaDiarizationRuntime.Inspect(config with { ClusteringThreshold = double.NaN }).Ready && !SherpaDiarizationRuntime.Inspect(config with { ModelLicenseAcknowledged = false }).Ready &&
            !SherpaDiarizationRuntime.Inspect(config with { ExecutablePath = model }).Ready, "Invalid configuration accepted.");
    }
    private static void Order()
    {
        SherpaDiarizationRuntime.NativeInterval[] rows = [new(0, 80, "early-short"), new(100, 180, "later-reliable"), new(200, 600, "first-reliable"),
            new(650, 1050, "overlap-only"), new(700, 900, "first-reliable"), new(1100, 1600, "later-reliable")];
        bool[] overlaps = SherpaDiarizationRuntime.DetectOverlaps(rows);
        Need(SherpaDiarizationRuntime.OrderSpeakers(rows, overlaps).SequenceEqual(new[] { "first-reliable", "later-reliable", "early-short", "overlap-only" }), "Short/overlap-only fragments determined early display codes.");
    }
    private static void Need(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Bad(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid raw interval accepted."); }
}
