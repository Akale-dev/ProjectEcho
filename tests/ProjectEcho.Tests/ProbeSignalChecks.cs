using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

/// <summary>
/// Offline analysis of the owned-process probe: five seconds at 660 Hz, then one at 880 Hz.
/// No device enumeration, playback, recording, or modification of captured originals.
/// </summary>
internal static class ProbeSignalChecks
{
    private const double WindowSeconds = .2;
    private const double DetectionFloor = .0002;
    private const double ForbiddenNoiseFloor = .00002;
    private const double ForbiddenRelativeDb = -35;
    private static readonly Guid FloatTag = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmTag = new("00000001-0000-0010-8000-00aa00389b71");

    public static ProbeSignalReport Analyze(string wavePath)
    {
        var path = ProjectPath(wavePath);
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Probe analysis requires a project-local WAV.");

        // Deny simultaneous writes/deletes while hashing and reading the same original.
        using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sourceHash = Convert.ToHexString(SHA256.HashData(original));
        original.Position = 0;
        using var reader = new WaveFileReader(original);
        var format = reader.WaveFormat;
        var tag = format is WaveFormatExtensible ext ? ext.SubFormat
            : new Guid((int)format.Encoding, 0, 0x0010, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71);
        var isFloat = tag == FloatTag;
        var bytesPerSample = format.BitsPerSample / 8;
        if (!(isFloat && format.BitsPerSample == 32 || tag == PcmTag && format.BitsPerSample == 16) ||
            format.Channels is < 1 or > 8 || format.SampleRate is < 8_000 or > 192_000 ||
            format.BlockAlign != format.Channels * bytesPerSample || reader.Length <= 0 ||
            reader.Length % format.BlockAlign != 0 || reader.TotalTime.TotalSeconds > 12)
            throw new InvalidDataException("Expected nonempty PCM16/Float32 probe audio of at most 12 seconds.");

        var framesPerWindow = (int)Math.Round(format.SampleRate * WindowSeconds);
        var buffer = new byte[framesPerWindow * format.BlockAlign];
        var channelSamples = Enumerable.Range(0, format.Channels).Select(_ => new double[framesPerWindow]).ToArray();
        var windows = new List<ProbeSignalWindow>();
        long frames = 0;
        while (true)
        {
            var used = 0;
            while (used < buffer.Length)
            {
                var read = reader.Read(buffer, used, buffer.Length - used);
                if (read == 0) break;
                used += read;
            }
            if (used == 0) break;
            if (used % format.BlockAlign != 0) throw new InvalidDataException("Partial sample frame in probe WAV.");
            var frameCount = used / format.BlockAlign;
            for (var frame = 0; frame < frameCount; frame++)
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var sampleBytes = buffer.AsSpan(frame * format.BlockAlign + channel * bytesPerSample, bytesPerSample);
                var sample = isFloat ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sampleBytes))
                    : BinaryPrimitives.ReadInt16LittleEndian(sampleBytes) / 32768d;
                if (!double.IsFinite(sample)) throw new InvalidDataException("Non-finite sample in probe WAV.");
                channelSamples[channel][frame] = sample;
            }
            // Max per channel preserves a signal present on one channel and catches forbidden
            // audio on any channel; arithmetic downmix could hide opposite-phase contamination.
            var amplitudes = new double[3];
            foreach (var samples in channelSamples)
            {
                var measured = Measure(samples, frameCount, format.SampleRate);
                for (var tone = 0; tone < 3; tone++) amplitudes[tone] = Math.Max(amplitudes[tone], measured[tone]);
            }
            windows.Add(new(windows.Count, frames / (double)format.SampleRate,
                frameCount / (double)format.SampleRate, amplitudes[0], amplitudes[1], amplitudes[2]));
            frames += frameCount;
        }
        if (frames != reader.Length / format.BlockAlign) throw new InvalidDataException("Incomplete probe WAV read.");

        var targetPeak = windows.Max(window => window.Target660Amplitude);
        var tailPeak = windows.Max(window => window.Tail880Amplitude);
        var forbiddenPeak = windows.Max(window => window.Forbidden440Amplitude);
        var threshold = Math.Max(DetectionFloor, targetPeak * .1);
        var target = Summarize(windows, window => window.Target660Amplitude, threshold, targetPeak);
        var tail = Summarize(windows, window => window.Tail880Amplitude, threshold, tailPeak);
        var targetDetected = target.ActiveWindowCount >= 24;
        var tailDetected = tail.ActiveWindowCount >= 4;
        var allowedForbidden = Math.Max(ForbiddenNoiseFloor, targetPeak * Math.Pow(10, ForbiddenRelativeDb / 20));
        var isolationPass = targetDetected && forbiddenPeak <= allowedForbidden;
        var sequenceSpan = target.StartSeconds is double start && tail.EndSeconds is double end ? end - start : (double?)null;
        // Amplitude-weighted duration also rejects a short blip repeated at the two endpoints.
        // This is a signal-envelope check with a 200ms window tolerance, NOT a clock audit.
        var targetDurationPass = target.EquivalentToneSeconds is >= 4.84 and <= 5.16;
        var tailDurationPass = tail.EquivalentToneSeconds is >= .84 and <= 1.16;
        var orderPass = target.EndSeconds is double targetEnd && tail.StartSeconds is double tailStart &&
            tailStart >= targetEnd - WindowSeconds - 1e-6 && tailStart <= targetEnd + 1e-6;
        var sequencePass = targetDetected && tailDetected && target.IsContiguous && tail.IsContiguous &&
            targetDurationPass && tailDurationPass && orderPass && sequenceSpan is >= 5.8 and <= 6.4;
        var failures = new List<string>();
        if (!targetDetected) failures.Add("target660_missing_or_too_short");
        if (!tailDetected) failures.Add("tail880_missing_or_too_short");
        if (!targetDurationPass) failures.Add("target660_duration_mismatch");
        if (!tailDurationPass) failures.Add("tail880_duration_mismatch");
        if (!target.IsContiguous || !tail.IsContiguous) failures.Add("signal_gap");
        if (!orderPass) failures.Add("target_tail_order_mismatch");
        if (sequenceSpan is null or < 5.8 or > 6.4) failures.Add("six_second_signal_span_mismatch");
        if (forbiddenPeak > allowedForbidden) failures.Add("forbidden440_leakage");

        using var verifyOriginal = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var finalHash = Convert.ToHexString(SHA256.HashData(verifyOriginal));
        if (sourceHash != finalHash) throw new InvalidDataException("Probe original changed during analysis.");
        return new(sourceHash, finalHash, true, format.SampleRate, format.Channels, format.BitsPerSample,
            isFloat ? "Float32" : "PCM16", frames, frames / (double)format.SampleRate, WindowSeconds,
            threshold, ForbiddenRelativeDb, ForbiddenNoiseFloor, allowedForbidden,
            forbiddenPeak, targetPeak > 0 ? forbiddenPeak / targetPeak : null,
            target, tail, targetDetected, tailDetected, isolationPass, sequenceSpan, sequencePass,
            sequencePass && isolationPass, failures, windows);
    }

    public static void RunSyntheticTests(string output)
    {
        var directory = Path.Combine(ProjectPath(output), "probe-signal-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "TEST_FIXTURE"), "Synthetic WAV only. No device capture or playback.");
        var normal = Analyze(Fixture("normal", delay: .073));
        Require(normal.SignalPass && normal.OriginalUnchanged, "Valid shifted Float32 signal was rejected.");
        Require(normal.Target660Detected && normal.Tail880Detected && normal.Forbidden440Absent, "Valid signal subchecks failed.");
        _ = JsonSerializer.Serialize(normal); // NaN/Infinity cannot silently enter the report.
        Require(Analyze(Fixture("pcm16", pcm16: true, sampleRate: 44_100, delay: .117)).SignalPass,
            "Valid PCM16 signal was rejected.");
        Require(Analyze(Fixture("opposite-phase", oppositePhase: true)).SignalPass,
            "Opposite phase channels must not cancel the target during detection.");
        var leakage = Analyze(Fixture("forbidden440", forbidden: true));
        Require(!leakage.SignalPass && !leakage.Forbidden440Absent && leakage.SequencePass,
            "Mixed sibling 440 Hz must fail isolation while target sequence remains detectable.");
        var noTail = Analyze(Fixture("missing-tail", tailSeconds: 0));
        Require(!noTail.SignalPass && !noTail.Tail880Detected, "Missing tail passed.");
        Require(!Analyze(Fixture("short-tail", tailSeconds: .4)).SignalPass, "A short tail blip passed as a full second.");
        Require(!Analyze(Fixture("short-target", targetSeconds: 3)).SignalPass, "A short target passed as five seconds.");
        Require(!Analyze(Fixture("gap", gap: true)).SignalPass, "An internal signal gap passed.");
        var silence = Analyze(Fixture("silence", silence: true));
        Require(!silence.SignalPass && !silence.Target660Detected && !silence.Tail880Detected,
            "Silence passed as a valid signal.");
        var nonFinite = Fixture("nonfinite", nonFinite: true);
        Reject(() => Analyze(nonFinite), "Non-finite samples were accepted.");
        var malformed = Path.Combine(directory, "malformed.wav");
        File.WriteAllText(malformed, "TEST_FIXTURE_NOT_A_WAV");
        Reject(() => Analyze(malformed), "Malformed WAV was accepted.");
        var partial = Fixture("partial-frame");
        using (var stream = new FileStream(partial, FileMode.Open, FileAccess.Write))
        {
            stream.Position = 40;
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(checked((int)stream.Length - 45));
        }
        Reject(() => Analyze(partial), "Partial sample frame was accepted.");

        string Fixture(string name, bool pcm16 = false, int sampleRate = 48_000, double delay = .07,
            double targetSeconds = 5, double tailSeconds = 1, bool forbidden = false,
            bool silence = false, bool gap = false, bool oppositePhase = false, bool nonFinite = false)
        {
            var path = Path.Combine(directory, name + ".wav");
            const int channels = 2;
            var sampleWidth = pcm16 ? 2 : 4;
            var frames = (int)Math.Ceiling((delay + targetSeconds + tailSeconds + .3) * sampleRate);
            using var writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write));
            writer.Write("RIFF"u8); writer.Write(36 + frames * channels * sampleWidth); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)(pcm16 ? 1 : 3)); writer.Write((short)channels);
            writer.Write(sampleRate); writer.Write(sampleRate * channels * sampleWidth);
            writer.Write((short)(channels * sampleWidth)); writer.Write((short)(sampleWidth * 8));
            writer.Write("data"u8); writer.Write(frames * channels * sampleWidth);
            for (var frame = 0; frame < frames; frame++)
            {
                var position = frame / (double)sampleRate - delay;
                double sample = 0;
                if (!silence && position >= 0 && position < targetSeconds + tailSeconds)
                {
                    var frequency = position < targetSeconds ? 660 : 880;
                    sample = .02 * Math.Sin(2 * Math.PI * frequency * position);
                    if (gap && position is >= 2 and < 2.6) sample = 0;
                }
                for (var channel = 0; channel < channels; channel++)
                {
                    var value = channel == 1 && oppositePhase ? -sample : sample;
                    // Put contamination on one channel to catch unsafe downmix implementations.
                    if (forbidden && channel == 1 && position >= 0 && position < 6)
                        value += .02 * Math.Sin(2 * Math.PI * 440 * position);
                    if (pcm16) writer.Write((short)Math.Round(value * 32767));
                    else writer.Write(nonFinite && frame == 17 ? float.NaN : (float)value);
                }
            }
            return path;
        }
    }

    private static double[] Measure(double[] samples, int count, int sampleRate)
    {
        var previous = new double[3];
        var beforePrevious = new double[3];
        int[] frequencies = [440, 660, 880];
        var coefficients = frequencies.Select(frequency => 2 * Math.Cos(2 * Math.PI * frequency / sampleRate)).ToArray();
        double weightSum = 0;
        for (var i = 0; i < count; i++)
        {
            var weight = count <= 2 ? 1 : .5 * (1 - Math.Cos(2 * Math.PI * i / (count - 1)));
            var weighted = samples[i] * weight;
            weightSum += weight;
            for (var tone = 0; tone < 3; tone++)
            {
                var current = weighted + coefficients[tone] * previous[tone] - beforePrevious[tone];
                beforePrevious[tone] = previous[tone];
                previous[tone] = current;
            }
        }
        var result = new double[3];
        for (var tone = 0; tone < 3; tone++)
        {
            var power = previous[tone] * previous[tone] + beforePrevious[tone] * beforePrevious[tone] -
                coefficients[tone] * previous[tone] * beforePrevious[tone];
            result[tone] = 2 * Math.Sqrt(Math.Max(0, power)) / weightSum;
            if (!double.IsFinite(result[tone])) throw new InvalidDataException("Non-finite probe measurement.");
        }
        return result;
    }

    private static ProbeToneInterval Summarize(IReadOnlyList<ProbeSignalWindow> windows,
        Func<ProbeSignalWindow, double> amplitude, double threshold, double peak)
    {
        var active = windows.Where(window => amplitude(window) >= threshold).ToArray();
        if (active.Length == 0) return new(0, null, null, 0, false);
        var equivalent = active.Sum(window => window.DurationSeconds * amplitude(window) / peak);
        return new(active.Length, active[0].StartSeconds, active[^1].StartSeconds + active[^1].DurationSeconds,
            equivalent, active.Length == active[^1].Index - active[0].Index + 1);
    }

    private static string ProjectPath(string supplied)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Project root not found.");
        var full = Path.GetFullPath(supplied);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root.FullName) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Probe files must remain inside ProjectEcho.");
        for (FileSystemInfo? current = File.Exists(full) ? new FileInfo(full) : new DirectoryInfo(full); current is not null;)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Probe paths cannot traverse links.");
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
        return full;
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or EndOfStreamException)
        { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record ProbeSignalWindow(int Index, double StartSeconds, double DurationSeconds,
    double Forbidden440Amplitude, double Target660Amplitude, double Tail880Amplitude);

internal sealed record ProbeToneInterval(int ActiveWindowCount, double? StartSeconds, double? EndSeconds,
    double EquivalentToneSeconds, bool IsContiguous);

internal sealed record ProbeSignalReport(string SourceSha256Before, string SourceSha256After, bool OriginalUnchanged,
    int SampleRate, int Channels, int BitsPerSample, string Format, long Frames, double DurationSeconds,
    double WindowSeconds, double DetectionThreshold, double ForbiddenRelativeThresholdDb, double ForbiddenAbsoluteNoiseFloor,
    double AllowedForbidden440Amplitude, double Forbidden440PeakAmplitude, double? ForbiddenToTargetAmplitudeRatio,
    ProbeToneInterval Target660, ProbeToneInterval Tail880, bool Target660Detected, bool Tail880Detected,
    bool Forbidden440Absent, double? SequenceSpanSeconds, bool SequencePass, bool SignalPass,
    IReadOnlyList<string> Failures, IReadOnlyList<ProbeSignalWindow> Windows)
{
    public string Method => "Non-overlapping 200ms Hann; coherent-gain-corrected peak amplitude; maximum across channels.";
    public string ExpectedSignal => "5s at 660Hz then 1s at 880Hz; concurrent sibling 440Hz must be absent.";
    public string DurationTolerance => "Equivalent tone duration +/-160ms; observed outer window span 5.8..6.4s.";
    public string ClockIntegrity => "NOT_ESTABLISHED_BY_SIGNAL_ANALYSIS";
    public string Limitations => "Offline controlled-tone evidence only. Does not prove clock integrity, every boundary frame, listening quality, browser/platform isolation, microphone privacy, or independently verify the control output was nonzero.";
}
