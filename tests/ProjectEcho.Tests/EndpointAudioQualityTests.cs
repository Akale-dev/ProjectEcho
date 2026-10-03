using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Export;

/// <summary>New synthetic PCM only; no devices, playback or historical recordings.</summary>
internal static class EndpointAudioQualityTests
{
    private const ulong Epoch = 60_000_000_000;
    private const int OutputRate = 48000;
    private const int PacketCount = 101; // The first, one-second and final anchors have zero jitter.

    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "endpoint-audio-quality");
        foreach (int rate in new[] { 16000, 44100, 48000 })
        {
            int capturedRate = rate;
            run($"T22/T25/T27-SYNTH endpoint {rate} Hz sine rejects packet jitter pitch modulation and unnecessary attenuation",
                () => Waveform(Path.Combine(root, "sine-" + capturedRate), capturedRate, sweep: false));
            run($"T22/T25/T27-SYNTH endpoint {rate} Hz chirp preserves waveform across bounded jitter packets",
                () => Waveform(Path.Combine(root, "chirp-" + capturedRate), capturedRate, sweep: true));
        }
        run("T24/T27-SYNTH unity source gain protects strong coherent dual source before PCM16 clipping",
            () => DualSource(Path.Combine(root, "coherent-dual-source")));
    }

    private static void Waveform(string folder, int rate, bool sweep)
    {
        const double amplitude = .2;
        int packetFrames = rate / 50;
        int inputFrames = packetFrames * PacketCount;
        double duration = (double)inputFrames / rate;
        var samples = new float[inputFrames];
        for (int frame = 0; frame < samples.Length; frame++)
        {
            double time = (double)frame / rate;
            double phase = sweep ? 2 * Math.PI * (200 * time + .5 * (2800 - 200) / duration * time * time)
                : 2 * Math.PI * 997 * time;
            samples[frame] = (float)(amplitude * Math.Sin(phase));
        }
        using (var storage = NewStore(folder))
        {
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
            for (int packet = 0; packet < PacketCount; packet++)
            {
                int offset = packet * packetFrames;
                ulong qpc = Epoch + (ulong)(packet * 200_000L + (packet % 2 == 1 ? 7_600 : 0));
                storage.WritePacket("remote-1", Encode(samples.AsSpan(offset, packetFrames)),
                    new(offset, packetFrames, packetFrames, (ulong)offset, qpc, 0, true, true));
            }
            long duration100ns = PacketCount * 200_000L;
            storage.WriteBoundary("remote-1", "stop", "stop", Epoch + (ulong)duration100ns, (ulong)inputFrames, 0, null);
            storage.Finish(duration100ns, true, []);
        }
        var recovery = M1Recovery.Read(folder);
        Need(recovery.IsCleanEnd && recovery.Issues.Count == 0, "Synthetic quality fixture did not recover cleanly.");
        Need(recovery.Capture.Tracks.SelectMany(track => track.Packets).Any(packet => packet.ClockJitter100ns == 7600)
            && recovery.Capture.Tracks.SelectMany(track => track.Packets).Any(packet => packet.ClockJitter100ns == -7600),
            "Quality fixture did not preserve the alternating original API clocks.");
        var hashes = OriginalHashes(folder);
        var mix = M2ExportService.RenderMeeting(folder, encoder: null);
        using var wave = new M2WaveReader(mix.WavePath);
        Need(wave.FrameCount == PacketCount * 960L, "Quality rendering changed full elapsed duration.");
        var actual = wave.ReadFrames(0, checked((int)wave.FrameCount));
        var reference = new double[actual.Length];
        // Independent nominal device-domain interpolation of the known continuous input,
        // including samples on both sides of packet boundaries. Do not compare against
        // another rendering of the same jitter algorithm or use constant PCM as a proxy.
        for (int frame = 0; frame < reference.Length; frame++)
        {
            double position = (double)frame * rate / OutputRate;
            int low = Math.Min((int)position, samples.Length - 1), high = Math.Min(low + 1, samples.Length - 1);
            double fraction = position - low;
            reference[frame] = samples[low] * (1 - fraction) + samples[high] * fraction;
        }
        var evidence = Compare(actual, reference, trim: 12);
        File.WriteAllText(Path.Combine(folder, "quality-evidence.json"), JsonSerializer.Serialize(new
        {
            SourceRate = rate, Waveform = sweep ? "200-2800 Hz linear chirp" : "997 Hz sine", PacketFrames = packetFrames,
            ApiQpcJitter100ns = 7600, evidence.Gain, evidence.SnrDb, evidence.RelativeResidual,
            FullTimelineFrames = wave.FrameCount, HardwareCapture = "NOT_RUN", PerceptualListening = "NOT_RUN"
        }));
        Need(evidence.Gain is > .985 and < 1.015,
            $"Quiet source was attenuated or amplified without a peak need: gain={evidence.Gain:F6}.");
        Need(evidence.SnrDb > (rate == 16000 ? 30 : 40),
            $"Bounded packet clock jitter modulated the known waveform: SNR={evidence.SnrDb:F2} dB.");
        Need(hashes.All(pair => Hash(pair.Key) == pair.Value), "Quality rendering changed authoritative originals or clocks.");
    }

    private static void DualSource(string folder)
    {
        const int frames = OutputRate;
        var source = new float[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            double time = (double)frame / OutputRate;
            // A nonstationary envelope catches adaptive/piecewise gain in addition to
            // flat-topped clipping. Two identical tracks make the unscaled sum > 1.
            double envelope = .6 + .25 * Math.Sin(2 * Math.PI * 2 * time);
            source[frame] = (float)(envelope * Math.Sin(2 * Math.PI * 997 * time));
        }
        using (var storage = NewStore(folder))
        {
            foreach (string id in new[] { "remote", "mic" })
            {
                storage.AddSegment(id + "-1", id, WaveFormat.CreateIeeeFloatWaveFormat(OutputRate, 1));
                for (int offset = 0; offset < frames; offset += 960)
                    storage.WritePacket(id + "-1", Encode(source.AsSpan(offset, 960)),
                        new(offset, 960, 960, (ulong)offset, Epoch + (ulong)(offset / 960 * 200_000L), 0, true, true));
                storage.WriteBoundary(id + "-1", "stop", "stop", Epoch + 10_000_000, frames, 0, null);
            }
            storage.Finish(10_000_000, true, []);
        }
        var hashes = OriginalHashes(folder);
        var mix = M2ExportService.RenderMeeting(folder, encoder: null);
        using var wave = new M2WaveReader(mix.WavePath);
        Need(wave.FrameCount == frames, "Strong dual-source rendering changed its timeline.");
        var actual = wave.ReadFrames(0, frames);
        var expectedSum = source.Select(sample => 2.0 * sample).ToArray();
        var evidence = Compare(actual, expectedSum, trim: 12);
        var peak = TruePeakProtection.AnalyzeWave(mix.WavePath);
        File.WriteAllText(Path.Combine(folder, "quality-evidence.json"), JsonSerializer.Serialize(new
        {
            evidence.Gain, evidence.SnrDb, evidence.RelativeResidual, peak.SamplePeak, peak.TruePeakLinear,
            peak.DbTp, FullTimelineFrames = wave.FrameCount, HardwareCapture = "NOT_RUN"
        }));
        Need(evidence.Gain is > .43 and < .49, $"Strong mix did not use only necessary uniform attenuation: gain={evidence.Gain:F6}.");
        Need(evidence.SnrDb > 80,
            $"Strong dual source was clipped before peak protection or given varying gain: SNR={evidence.SnrDb:F2} dB.");
        Need(peak.TruePeakLinear <= TruePeakProtection.ProcessingCeilingLinear + 1e-7 && peak.SamplePeak > .75,
            "Strong mix exceeded its verified peak ceiling or was needlessly made quiet.");
        Need(hashes.All(pair => Hash(pair.Key) == pair.Value), "Peak protection changed an original source or clock.");
    }

    private static (double Gain, double SnrDb, double RelativeResidual) Compare(float[] actual, double[] reference, int trim)
    {
        double dot = 0, energy = 0;
        for (int index = trim; index < actual.Length - trim; index++)
        { dot += actual[index] * reference[index]; energy += reference[index] * reference[index]; }
        double gain = dot / energy, residual = 0;
        for (int index = trim; index < actual.Length - trim; index++)
        { double difference = actual[index] - gain * reference[index]; residual += difference * difference; }
        double ratio = residual / (gain * gain * energy);
        return (gain, -10 * Math.Log10(Math.Max(1e-30, ratio)), Math.Sqrt(ratio));
    }

    private static M1Storage NewStore(string folder) => new(folder, Epoch, DateTimeOffset.UtcNow, "Synthetic waveform quality only", true);
    private static byte[] Encode(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 4];
        for (int index = 0; index < samples.Length; index++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * 4, 4), samples[index]);
        return bytes;
    }
    private static Dictionary<string, string> OriginalHashes(string folder) => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetExtension(path) is ".wav" or ".json" or ".jsonl")
        .ToDictionary(path => path, Hash, StringComparer.OrdinalIgnoreCase);
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
