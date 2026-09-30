using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Export;

/// <summary>Actual installed Windows MP3 encoder/decoder, synthetic files only; never capture/playback.</summary>
internal static class M2Mp3Tests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        run("T28/T29-WINDOWS MP3 calibrated delay EOF tiny and fractional lengths preserve first/tail", () => RoundTrip(output));
        run("T27/T28-WINDOWS MP3 actual true-peak retries cancellation source locks and overwrite protection", () => SafetyAndPeak(output));
        run("T28/T29/T32/T33-WINDOWS MP3 full mix and parts retain timed first-tail markers bytes overlap and privacy", () => Export(output));
    }

    private static void RoundTrip(string output)
    {
        string root = Path.Combine(output, "m2-mp3-codec"); Directory.CreateDirectory(root);
        var evidence = new List<object>();
        foreach (int frames in new[] { 1, 960, 6576, 154416, 1446000 })
        {
            string source = Path.Combine(root, $"noise-{frames}.wav");
            float[] samples = Noise(frames); TruePeakTests.WriteWave(source, samples);
            string hash = Hash(source);
            var encoded = new MediaFoundationMp3Encoder().Encode(source, Path.ChangeExtension(source, ".mp3"));
            var decoded = Read(encoded.Path);
            Need(encoded.SourceFrames == frames && decoded.LongLength == encoded.DecodedFrames, "MP3 source/EOF frame count changed.");
            Need(encoded.EncoderDelayFrames == 673 && encoded.TrailingPaddingFrames >= 4096 &&
                encoded.DecodedFrames == frames + encoded.EncoderDelayFrames + encoded.TrailingPaddingFrames,
                "MP3 did not account for full delay and flush padding.");
            var walk = Mp3FrameProbe.Inspect(encoded.Path);
            Mp3FrameProbe.ValidateAgainstDecoded(walk, decoded.LongLength, 48000);
            Need(walk.AudioBytes == new FileInfo(encoded.Path).Length && walk.BitsPerSecond == 96000,
                "MP3 frame bytes or actual bitrate differs.");
            Need(encoded.Mp3Timeline is { CalibratedDelayFrames: 673, CalibrationCorrelation: > .8 }, "Installed codec pair was not calibrated.");
            double? first = null, last = null;
            if (frames >= 6576)
            {
                first = Correlation(samples, decoded, 0, 4096, 673);
                last = Correlation(samples, decoded, frames - 4096, 4096, 673);
                Need(first > .8 && last > .8, "MP3 first/tail waveform missing or mapped at wrong delay.");
            }
            Need(Hash(source) == hash, "MP3 changed original WAV.");
            evidence.Add(new { frames, encoded.ByteLength, encoded.DecodedFrames, encoded.EncoderDelayFrames,
                encoded.TrailingPaddingFrames, encoded.Mp3Timeline, firstCorrelation = first, tailCorrelation = last, status = "PASS" });
        }
        string valid = Path.Combine(root, "noise-6576.mp3"), truncated = Path.Combine(root, "truncated.mp3");
        File.WriteAllBytes(truncated, File.ReadAllBytes(valid)[..^1]);
        Reject<InvalidDataException>(() => Mp3FrameProbe.Inspect(truncated));
        File.WriteAllText(Path.Combine(root, "mp3-codec-evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void SafetyAndPeak(string output)
    {
        string root = Path.Combine(output, "m2-mp3-safety"); Directory.CreateDirectory(root);
        string source = Path.Combine(root, "intersample.wav");
        TruePeakTests.WriteWave(source, Enumerable.Range(0, 8192).Select(i => i % 4 < 2 ? .8f : -.8f).ToArray());
        string hash = Hash(source), target = Path.Combine(root, "protected.mp3"); var encoder = new MediaFoundationMp3Encoder();
        var encoded = encoder.Encode(source, target);
        var decoded = Read(target); double reference = TruePeakTests.ReferencePeak32x(decoded);
        Need(reference <= Math.Pow(10, -1d / 20) + 1e-6 && encoded.EncodingAttempts > 1 && encoded.AppliedGain < 1,
            "MP3 codec peak overshoot was not independently protected.");
        Reject<IOException>(() => encoder.Encode(source, target)); Reject<IOException>(() => encoder.Encode(source, source));
        string cancelled = Path.Combine(root, "cancelled.mp3");
        using (var token = new CancellationTokenSource())
        {
            token.Cancel(); Reject<OperationCanceledException>(() => encoder.Encode(source, cancelled, token.Token));
        }
        using (var token = new CancellationTokenSource())
        {
            token.CancelAfter(5); Reject<OperationCanceledException>(() => encoder.Encode(source, cancelled, token.Token));
        }
        Need(!File.Exists(cancelled) && !Directory.EnumerateFiles(root, ".encoding-*").Any() &&
            !Directory.EnumerateFiles(root, ".mp3-calibration-*").Any(), "MP3 cancellation left a committed/temporary file.");
        Need(Hash(source) == hash, "MP3 peak retry/cancel changed original.");
        File.WriteAllText(Path.Combine(root, "mp3-safety-evidence.json"), JsonSerializer.Serialize(new
        { encoded.EncodingAttempts, encoded.AppliedGain, reference32xTruePeak = reference, sourceUnchanged = true,
            cancelledOutputAbsent = true, overwriteRejected = true, truncatedFrameRejected = true }));
    }

    private static void Export(string output)
    {
        string root = Path.Combine(output, "m2-mp3-export"), session = Path.Combine(root, "synthetic-private-title");
        Directory.CreateDirectory(root); const int rate = 48000, frames = 154416; const ulong epoch = 40_000_000_000;
        float[] source = new float[frames];
        var markerRanges = new[] { (Start: 4800, End: 12000, Hz: 660), (Start: frames - 8400, End: frames - 1200, Hz: 1320) };
        foreach (var marker in markerRanges)
            for (int i = marker.Start; i < marker.End; i++) source[i] = (float)(.55 * Math.Sin(2 * Math.PI * marker.Hz * i / rate));
        using (var storage = new M1Storage(session, epoch, DateTimeOffset.UtcNow, "private-title-must-not-export", true))
        {
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
            for (int offset = 0; offset < frames; offset += 480)
            {
                int count = Math.Min(480, frames - offset); byte[] pcm = new byte[count * 4];
                for (int i = 0; i < count; i++) BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan(i * 4), source[offset + i]);
                storage.WritePacket("remote-1", pcm, new(offset, count, count, (ulong)offset,
                    epoch + (ulong)((long)offset * 10_000_000 / rate), 0, true));
            }
            storage.WriteBoundary("remote-1", "stop", "stop", epoch + 32_170_000, frames, 0, null);
            storage.Finish(32_170_000, true, []);
        }
        var original = Directory.EnumerateFiles(session, "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash);
        var mix = M2ExportService.RenderMeeting(session, new MediaFoundationMp3Encoder());
        Need(mix.CompressedPath?.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) == true,
            "Full MP3 failed: " + string.Join(" | ", mix.Warnings));
        var full = Read(mix.CompressedPath!); var markerEvidence = new List<object>();
        foreach (var marker in markerRanges)
        {
            int observedStart = FindSignalStart(full, marker.Start - 1200 + 673, marker.End + 673);
            Need(Math.Abs(observedStart - marker.Start - 673) < 240, "MP3 first/tail marker start differs by more than 5 ms.");
            Need(Rms(full, marker.End - 480 + 673, 480) > .1, "MP3 last marker tail is missing.");
            markerEvidence.Add(new { marker.Start, marker.End, observedStart, expectedDelayFrames = 673 });
        }
        var export = M2ExportService.ExportParts(mix, new(M2ExportFormat.Mp3, 20000, 1, .1, true), Path.Combine(root, "exports"));
        Need(export.Parts.Count >= 4 && export.Parts[0].StartFrame == 0 && export.Parts[^1].EndFrame == frames, "MP3 parts lost start/tail coverage.");
        long previousEnd = 0;
        foreach (var part in export.Parts)
        {
            var pcm = Read(part.Path); var probe = MediaFoundationM4aEncoder.DecodeProbe(part.Path);
            Need(part.ByteLength == new FileInfo(part.Path).Length && part.ByteLength <= 20000 && probe.DurationSeconds <= 1,
                "MP3 part exceeds actual bytes or decoded duration limit.");
            Need(pcm.LongLength == part.EndFrame - part.StartFrame + part.EncoderDelayFrames + part.TrailingPaddingFrames,
                "MP3 part EOF does not preserve complete mapped source range.");
            Need(part.StartFrame <= previousEnd && part.EndFrame > previousEnd, "MP3 parts gap or fail to advance.");
            if (previousEnd != 0) Need(previousEnd - part.StartFrame == 4800, "MP3 part overlap changed.");
            previousEnd = part.EndFrame;
            foreach (var marker in markerRanges)
            {
                long start = Math.Max(part.StartFrame, marker.Start), end = Math.Min(part.EndFrame, marker.End);
                if (end - start >= 480)
                    Need(Rms(pcm, (int)(end - part.StartFrame - 480 + part.EncoderDelayFrames), 480) > .1,
                        "MP3 part discarded an included first/tail marker.");
            }
        }
        string text = File.ReadAllText(export.ManifestPath!); using var manifest = JsonDocument.Parse(text);
        Need(manifest.RootElement.GetProperty("format").GetString() == "mp3" && !text.Contains(session) &&
            !text.Contains("private-title") && !text.Contains("remote-1"), "MP3 manifest format/privacy mismatch.");
        foreach (var part in manifest.RootElement.GetProperty("parts").EnumerateArray())
            Need(part.GetProperty("spans")[0].GetProperty("fileStartFrame").GetInt64() == 673,
                "MP3 manifest timestamp mapping omitted calibrated delay.");
        Need(original.All(pair => File.Exists(pair.Key) && Hash(pair.Key) == pair.Value), "MP3 export modified recording originals.");
        File.WriteAllText(Path.Combine(root, "mp3-full-parts-evidence.json"), JsonSerializer.Serialize(new
        { fullSourceFrames = frames, fullDecodedFrames = full.Length, markerEvidence, parts = export.Parts,
            originalHashesUnchanged = true, noCapturePlayback = true }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static float[] Noise(int count)
    { var result = new float[count]; var random = new Random(73); double value = 0; for (int i = 0; i < count; i++) { value = value * .6 + (random.NextDouble() - .5) * .4; result[i] = (short)(value * 15000) / 32768f; } return result; }
    private static float[] Read(string path)
    {
        using var reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
        using var memory = new MemoryStream(); byte[] bytes = new byte[65536]; int count;
        while ((count = reader.Read(bytes, 0, bytes.Length)) > 0) memory.Write(bytes, 0, count);
        return MemoryMarshal.Cast<byte, float>(memory.ToArray()).ToArray();
    }
    private static double Correlation(float[] left, float[] right, int start, int count, int delay)
    { double cross = 0, l = 0, r = 0; for (int i = start; i < start + count; i++) { cross += left[i] * right[i + delay]; l += left[i] * left[i]; r += right[i + delay] * right[i + delay]; } return cross / Math.Sqrt(l * r); }
    private static double Rms(float[] values, int start, int count) => Math.Sqrt(values.Skip(start).Take(count).Sum(v => (double)v * v) / count);
    private static int FindSignalStart(float[] values, int start, int end)
    { for (int i = start; i < end - 48; i += 24) if (Rms(values, i, 48) > .05) return i; throw new InvalidDataException("Timed marker missing."); }
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidDataException("Expected " + typeof(T).Name); }
}
