using System.Globalization;
using System.Text;
using ProjectEcho.Core.Diarization;

internal static class M5DiarizationTimestampTests
{
    private const string SegmentationHash = "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079";
    private const long FractionalTailFrames = 1182893;

    internal static void Register(Action<string, Action> run, string output)
    {
        run("P5-04 SYNTH native EOF centre maps to exact frame-complete duration with an audit adjustment", ExactTail);
        run("P5-04 SYNTH both EOF centre grid points retain overlapping speakers and every raw interval", OverlappingTails);
        run("P5-04 SYNTH EOF normalization rejects off-grid and beyond-grid endpoints", RejectOffGrid);
        run("P5-04 SYNTH EOF normalization requires the trusted model and unpadded partial-window contract", ContractIsolation);
        run("P5-04 SYNTH EOF normalization rejects starts beyond the actual last sample and malformed intervals", InvalidIntervals);
        run("P5-04 SYNTH native float timestamp formatting remains exact near the three-hour limit", LongFloatAndCulture);
        run("P5-04 SYNTH in-range timestamps and strict parser remain unchanged", UnchangedIntervals);
        run("P5-04 SYNTH timestamp normalization enforces result bounds and cancellation", BoundsAndCancellation);
    }

    private static void ExactTail()
    {
        // Only synthetic numeric intervals; no source recording or transcript is loaded.
        // Metadata: 270-sample stride, 991-sample receptive field, 16 kHz.
        // floor(1182893 / 270) = 4381; centre = 73.96034375 s -> native 73.960.
        const string raw = "Started\n1.000 -- 2.000 speaker_04\n57.001 -- 73.960 speaker_09\n";
        var result = SherpaDiarizationTimestamps.Normalize(raw, FractionalTailFrames, SegmentationHash);
        Need(result.Intervals.Count == 2 && result.Adjustments.Count == 1, "EOF handling lost or invented a row/adjustment.");
        Need(result.Intervals[0] == new SherpaDiarizationRuntime.NativeInterval(1000, 2000, "04"), "An in-range row changed.");
        Need(result.Intervals[1] == new SherpaDiarizationRuntime.NativeInterval(57001, 73931, "09"), "EOF mapping changed identity/start or dropped the last fractional millisecond.");
        var adjustment = result.Adjustments[0];
        Need(adjustment.IntervalIndex == 1 && adjustment.RawEndMs == 73960 && adjustment.NormalizedEndMs == 73931,
            "The original end and exact affected row were not retained in the audit record.");
        Need(!string.IsNullOrWhiteSpace(result.Policy), "Normalization did not identify its timestamp policy.");
    }

    private static void OverlappingTails()
    {
        // Both 73.943 and 73.960 are model grid centres after this input's EOF.
        const string raw = "1.000 -- 2.000 speaker_04\n57.001 -- 73.960 speaker_09\n65.000 -- 73.943 speaker_02\n";
        var result = SherpaDiarizationTimestamps.Normalize(raw, FractionalTailFrames, SegmentationHash);
        Need(result.Intervals.Count == 3 && result.Adjustments.Count == 2, "Multiple EOF overlaps were dropped or merged.");
        Need(result.Intervals.Select(x => x.Speaker).SequenceEqual(new[] { "04", "09", "02" }), "Native identities/order changed.");
        Need(result.Intervals[1].StartMs == 57001 && result.Intervals[2].StartMs == 65000 &&
            result.Intervals[1].EndMs == 73931 && result.Intervals[2].EndMs == 73931, "Overlapping tails were not independently bounded at source EOF.");
        Need(result.Adjustments.Select(x => x.IntervalIndex).SequenceEqual(new[] { 1, 2 }) &&
            result.Adjustments.Select(x => x.RawEndMs).SequenceEqual(new long[] { 73960, 73943 }), "Raw overlapping ends cannot be reconstructed.");
        Need(SherpaDiarizationRuntime.DetectOverlaps(result.Intervals).SequenceEqual(new[] { false, true, true }), "Tail normalization changed real overlap status.");
    }

    private static void RejectOffGrid()
    {
        foreach (string end in new[] { "73.932", "73.942", "73.944", "73.959", "73.961", "74.000" })
            Bad(() => SherpaDiarizationTimestamps.Normalize($"57.001 -- {end} speaker_00\n", FractionalTailFrames, SegmentationHash));
        // A large real overrun must not be converted into a successful bounded interval.
        Bad(() => SherpaDiarizationTimestamps.Normalize("1.000 -- 173.960 speaker_00\n", FractionalTailFrames, SegmentationHash));
    }

    private static void ContractIsolation()
    {
        const string fractional = "57.001 -- 73.960 speaker_00\n";
        Bad(() => SherpaDiarizationTimestamps.Normalize(fractional, FractionalTailFrames, new string('0', 64)));
        Bad(() => SherpaDiarizationTimestamps.Normalize(fractional, FractionalTailFrames, SegmentationHash, paddedFrames: 1));
        // The <=10 s / padded-to-11 s path is a separate contract, not an EOF-grid exemption.
        Bad(() => SherpaDiarizationTimestamps.Normalize("9.000 -- 10.021 speaker_00\n", 160000, SegmentationHash, paddedFrames: 16000));
        Bad(() => SherpaDiarizationTimestamps.Normalize("9.000 -- 10.021 speaker_00\n", 160000, SegmentationHash));
        Bad(() => SherpaDiarizationTimestamps.Normalize("8.000 -- 11.000 speaker_00\n", 144000, SegmentationHash, paddedFrames: 32000));
        // Exactly 11 s has no padded native last chunk; one additional real sample does.
        Bad(() => SherpaDiarizationTimestamps.Normalize("9.000 -- 11.017 speaker_00\n", 176000, SegmentationHash));
        var justOverTen = SherpaDiarizationTimestamps.Normalize("9.000 -- 10.021 speaker_00\n", 160001, SegmentationHash);
        var justOverEleven = SherpaDiarizationTimestamps.Normalize("9.000 -- 11.017 speaker_00\n", 176001, SegmentationHash);
        Need(justOverTen.Intervals.Single().EndMs == 10001 && justOverEleven.Intervals.Single().EndMs == 11001,
            "One-sample partial-window boundaries were rounded down or rejected.");
    }

    private static void InvalidIntervals()
    {
        foreach (string raw in new[]
        {
            "73.931 -- 73.960 speaker_00", // Start is after exact EOF despite matching its ceil-millisecond.
            "73.943 -- 73.960 speaker_00", // Entirely beyond source audio.
            "73.960 -- 73.960 speaker_00",
            "73.960 -- 73.943 speaker_00",
            "57.001 -- 73.960 speaker_00\n56.000 -- 73.943 speaker_01",
            "-0.001 -- 73.960 speaker_00",
            "NaN -- 73.960 speaker_00",
            "57.001 -- Infinity speaker_00",
            "57.001 -- 73.96 speaker_00"
        }) Bad(() => SherpaDiarizationTimestamps.Normalize(raw, FractionalTailFrames, SegmentationHash));
        var lastPartialMillisecond = SherpaDiarizationTimestamps.Normalize("73.930 -- 73.960 speaker_00", FractionalTailFrames, SegmentationHash);
        Need(lastPartialMillisecond.Intervals.Single() == new SherpaDiarizationRuntime.NativeInterval(73930, 73931, "00"),
            "A real last partial millisecond was deleted.");
    }

    private static void LongFloatAndCulture()
    {
        // Native float operations yield 10800.015, while a double-only rational formula
        // yields 10800.014. This hard-coded expectation guards the actual CLI contract.
        const long frames = 172799999;
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var result = SherpaDiarizationTimestamps.Normalize("10799.000 -- 10800.015 speaker_00", frames, SegmentationHash);
            Need(result.Intervals.Single().EndMs == 10800000 && result.Adjustments.Single().RawEndMs == 10800015,
                "Three-hour float endpoint/locale mapping differs from native CLI output.");
            Bad(() => SherpaDiarizationTimestamps.Normalize("10799.000 -- 10800.014 speaker_00", frames, SegmentationHash));
            Bad(() => SherpaDiarizationTimestamps.Normalize("10799.000 -- 10800.016 speaker_00", frames, SegmentationHash));
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
    }

    private static void UnchangedIntervals()
    {
        const string raw = "OfflineSpeakerDiarizationConfig(...)\r\nStarted\n0.010 -- 0.210 speaker_00\n0.100 -- 0.310 speaker_01\n57.001 -- 73.927 speaker_00\n";
        var expected = SherpaDiarizationRuntime.ParseOutput(raw, 73931);
        foreach (string hash in new[] { SegmentationHash, new string('0', 64) })
        {
            var result = SherpaDiarizationTimestamps.Normalize(raw, FractionalTailFrames, hash);
            Need(result.Intervals.SequenceEqual(expected) && result.Adjustments.Count == 0, "Strictly in-range timestamps were modified.");
        }
        var empty = SherpaDiarizationTimestamps.Normalize("Started\n", FractionalTailFrames, SegmentationHash);
        Need(empty.Intervals.Count == 0 && empty.Adjustments.Count == 0, "Empty output invented a speaker or correction.");
        Bad(() => SherpaDiarizationRuntime.ParseOutput("57.001 -- 73.960 speaker_00", 73931));
    }

    private static void BoundsAndCancellation()
    {
        var raw = new StringBuilder();
        for (int i = 0; i < 50000; i++) raw.Append("0.000 -- 0.001 speaker_00\n");
        Need(SherpaDiarizationTimestamps.Normalize(raw.ToString(), FractionalTailFrames, SegmentationHash).Intervals.Count == 50000,
            "The documented 50000-row bound changed.");
        raw.Append("57.001 -- 73.960 speaker_00\n");
        Bad(() => SherpaDiarizationTimestamps.Normalize(raw.ToString(), FractionalTailFrames, SegmentationHash));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            SherpaDiarizationTimestamps.Normalize("57.001 -- 73.960 speaker_00", FractionalTailFrames, SegmentationHash, cancellationToken: stop.Token);
            throw new Exception("Timestamp normalization ignored cancellation.");
        }
        catch (OperationCanceledException) { }
    }

    private static void Need(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Bad(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Invalid native interval/contract was accepted.");
    }
}
