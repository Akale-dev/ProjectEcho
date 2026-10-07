using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

/// <summary>
/// File-driven pause storage and real local Vulkan ASR. Reads only the hash-pinned
/// public JFK fixture; never opens audio devices, private recordings or networking.
/// </summary>
internal static class M3hFixAsrProbe
{
    private const string PublicSampleSha256 = "59dfb9a4acb36fe2a2affc14bacbee2920ff435cb13cc314a08c13f66ba7860e";
    private const int Rate = 16000, SplitFrame = 88000, TotalFrames = 176000;
    private const ulong Epoch = 90_000_000_000, Pause100ns = 173_450_000;

    internal static async Task<int> RunAsync(string[] args)
    {
        string[] keys = ["--cli", "--model", "--audio", "--output"];
        if (args.Length != 9 || args[0] != "--m3hfix-asr-probe" ||
            !keys.All(key => args.Count(value => value == key) == 1) ||
            Enumerable.Range(0, 4).Any(index => !keys.Contains(args[1 + index * 2])))
        {
            Console.Error.WriteLine("Usage: --m3hfix-asr-probe --cli <existing Vulkan whisper-cli.exe> --model <existing ggml-base.bin> --audio <hash-pinned public jfk.wav> --output <new project-local-directory>");
            return 2;
        }
        string root = SyntheticAudioProbe.FindProjectRoot();
        string Value(string key) => SyntheticAudioProbe.SafeProjectPath(root, args[Array.IndexOf(args, key) + 1]);
        string cli = Value("--cli"), model = Value("--model"), audio = Value("--audio"), output = Value("--output");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Use a new empty probe evidence directory; previous evidence is never overwritten.");
        Directory.CreateDirectory(output);
        var immutable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var checks = new List<string>(); var errors = new List<string>();
        object? storageEvidence = null, waveformEvidence = null, mp3Evidence = null, engineEvidence = null;
        var subtitleEvidence = new List<object>();
        DateTimeOffset started = DateTimeOffset.UtcNow; var stopwatch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            Require(OperatingSystem.IsWindows(), "Actual Windows platform");
            Require(Hash(audio) == PublicSampleSha256, "Input is exactly the existing public JFK fixture");
            Require(Path.GetFileName(model).Equals("ggml-base.bin", StringComparison.OrdinalIgnoreCase), "Existing multilingual base model");
            foreach (string path in new[] { audio, cli, model }) immutable.Add(path, Hash(path));
            byte[] pcm;
            using (var reader = new M2WaveReader(audio))
            {
                Require(reader.Format is { SampleRate: Rate, Channels: 1, BitsPerSample: 16, IsFloat: false } &&
                    reader.FrameCount == TotalFrames, "Public sample is 11 seconds of 16 kHz mono PCM16");
                pcm = new byte[checked((int)reader.DataLength)]; reader.ReadExactly(pcm);
            }
            string paused = Path.Combine(output, "recordings", "file-driven-public-paused");
            string reference = Path.Combine(output, "recordings", "file-driven-public-reference");
            CreateMeeting(paused, pcm, withPause: true);
            CreateMeeting(reference, pcm, withPause: false);
            Preserve(paused); Preserve(reference);
            var recovered = M1Recovery.Read(paused, timeout.Token);
            var tracks = recovered.Capture.Tracks.OrderBy(track => track.ExcludedPause100ns).ToArray();
            Require(recovered.HasCheckpoint && recovered.IsCleanEnd && recovered.Issues.Count == 0 &&
                recovered.Capture.TimelinePolicy == RecordingTimeline.ExcludePaused &&
                recovered.Capture.Duration100ns == 110_000_000 && tracks.Length == 2 &&
                tracks.Sum(track => track.FrameCount) == TotalFrames, "Disk-only recovery retains two segments and exactly 11 active seconds");
            Require(tracks[0].ExcludedPause100ns == 0 && tracks[1].ExcludedPause100ns == Pause100ns &&
                tracks[1].Packets[0].QpcPosition100ns == Epoch + 55_000_000 + Pause100ns &&
                tracks[1].Packets[0].DevicePosition == 0, "Recovered raw QPC/device clocks retain the synthetic pause and separate offset");
            byte[] reconstructed = tracks.SelectMany(track => PcmBytes(track.WavePath)).ToArray();
            Require(pcm.AsSpan().SequenceEqual(reconstructed), "All source PCM and the final frame survive the pause split byte-for-byte");
            storageEvidence = new { Source = "Hash-pinned public JFK; file-driven synthetic clocks, not native capture", Epoch,
                RawElapsed100ns = 110_000_000UL + Pause100ns, Pause100ns, ActiveDuration100ns = recovered.Capture.Duration100ns,
                SplitFrame, SampleRate = Rate, recovered.Capture.TimelinePolicy, recovered.Issues,
                Tracks = tracks.Select(track => new { track.SegmentId, track.FrameCount, track.ExcludedPause100ns,
                    FirstRawQpc100ns = track.Packets[0].QpcPosition100ns, LastRawQpc100ns = track.Packets[^1].QpcPosition100ns,
                    track.WavePath, Sha256 = Hash(track.WavePath) }).ToArray() };

            var mix = M2ExportService.RenderMeeting(paused, encoder: null, timeout.Token);
            var referenceMix = M2ExportService.RenderMeeting(reference, encoder: null, timeout.Token);
            Require(mix.SourceStorageComplete && mix.SampleRate == 48000 && mix.FrameCount == 528000 &&
                mix.DurationUs == 11_000_000 && referenceMix.FrameCount == mix.FrameCount, "WAV mix has no pause padding or shortened tail");
            float[] mixed = Samples(mix.WavePath), referenceSamples = Samples(referenceMix.WavePath);
            int join48k = SplitFrame * 3;
            double full = Correlation(referenceSamples, mixed, 0, mixed.Length);
            double first = Correlation(referenceSamples, mixed, 0, 48000);
            double join = Correlation(referenceSamples, mixed, join48k - 2400, 4800);
            double tail = Correlation(referenceSamples, mixed, mixed.Length - 48000, 48000);
            // A new segment does not interpolate across the pause boundary. At 16 -> 48 kHz
            // this may affect the two subframes immediately before the join, not its clock.
            int different = Enumerable.Range(0, mixed.Length).Count(i => mixed[i] != referenceSamples[i]);
            Require(full > .9999 && first > .99999 && join > .995 && tail > .99999,
                "Full/head/join/tail waveform matches the continuous reference without clock stretch");
            Require(Enumerable.Range(0, mixed.Length).Where(i => i < join48k - 2 || i >= join48k)
                .All(i => mixed[i] == referenceSamples[i]), "Every 48 kHz frame outside the two boundary interpolation subframes is identical");

            var prepared = AsrAudioPreparation.Prepare(mix.WavePath, mix.WaveSha256,
                Path.Combine(paused, "transcripts", "inputs"), timeout.Token);
            var referencePrepared = AsrAudioPreparation.Prepare(referenceMix.WavePath, referenceMix.WaveSha256,
                Path.Combine(reference, "transcripts", "inputs"), timeout.Token);
            float[] preparedSamples = Samples(prepared.Path), referencePreparedSamples = Samples(referencePrepared.Path);
            Require(prepared.Frames == TotalFrames && referencePrepared.Frames == TotalFrames &&
                prepared.SourceFrames == 528000, "ASR normalization retains exactly 176000 frames / 11000 ms");
            double preparedCorrelation = Correlation(referencePreparedSamples, preparedSamples, 0, TotalFrames);
            double preparedJoin = Correlation(referencePreparedSamples, preparedSamples, SplitFrame - 800, 1600);
            Require(preparedCorrelation > .9999 && preparedJoin > .995, "ASR normalized PCM preserves the continuous reference and pause join");
            waveformEvidence = new { mix.WavePath, mix.FrameCount, mix.DurationUs, mix.WaveAppliedGain,
                ReferenceWave = referenceMix.WavePath, FullCorrelation = full, FirstSecondCorrelation = first,
                Join100msCorrelation = join, TailSecondCorrelation = tail, Different48kFrames = different,
                prepared.Path, prepared.Frames, prepared.Sha256, PreparedFullCorrelation = preparedCorrelation,
                PreparedJoin100msCorrelation = preparedJoin, BoundaryInterpolationAllowanceFrames = 2 };
            foreach (string path in new[] { mix.WavePath, referenceMix.WavePath, prepared.Path, referencePrepared.Path }) immutable.Add(path, Hash(path));

            var encoded = new MediaFoundationMp3Encoder().Encode(mix.WavePath, Path.Combine(output, "paused-public.mp3"), timeout.Token);
            float[] decoded = DecodeMp3(encoded.Path);
            int delay = checked((int)encoded.EncoderDelayFrames);
            double mp3First = Correlation(mixed, decoded, 0, 48000, delay);
            double mp3Join = Correlation(mixed, decoded, join48k - 2400, 4800, delay);
            double mp3Tail = Correlation(mixed, decoded, mixed.Length - 48000, 48000, delay);
            Require(encoded.SourceFrames == mix.FrameCount && encoded.DecodedFrames == decoded.Length &&
                encoded.DecodedFrames == mix.FrameCount + encoded.EncoderDelayFrames + encoded.TrailingPaddingFrames &&
                mp3First > .95 && mp3Join > .95 && mp3Tail > .95, "Actual MP3 encode/decode preserves first/join/tail with its registered codec delay");
            mp3Evidence = new { encoded, FirstCorrelation = mp3First, JoinCorrelation = mp3Join, TailCorrelation = mp3Tail };
            immutable.Add(encoded.Path, Hash(encoded.Path));

            var config = new WhisperConfiguration(cli, model, "en", 2, "base (multilingual)",
                ModelLicenseAcknowledged: true, Hardware: LocalTranscriptionHardware.Vulkan, GpuDeviceIndex: 0);
            Require(WhisperTranscriber.Inspect(config).Ready, "Existing local Vulkan CLI and model are ready");
            Console.WriteLine("M3Hfix: real local Vulkan inference on file-driven 11-second public speech.");
            var result = await WhisperTranscriber.RunAsync(new(paused, Path.GetFileName(paused), 11000,
                prepared.Path, prepared.Sha256, config, IsSynthetic: true), timeout.Token);
            using var attempt = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.AttemptDirectory, "attempt.json")));
            engineEvidence = new { result.Status, result.ElapsedMilliseconds, result.Message, result.AttemptDirectory,
                EngineAttempt = attempt.RootElement.Clone(), result.Revision };
            Require(result.Status == "Succeeded" && result.Revision is not null, "Real local inference succeeded");
            Require(attempt.RootElement.GetProperty("actualGpuBackend").GetString() == "Vulkan0" &&
                attempt.RootElement.GetProperty("hardwareEvidence").GetString() == "SelectedGpuBackend",
                "Engine itself reports Vulkan0; CPU fallback is not counted as Vulkan success");
            var revision = result.Revision!;
            Require(revision.Segments.Count > 0 && revision.Segments.All(s => s.StartMs >= 0 && s.EndMs > s.StartMs &&
                s.EndMs <= 11000 && s.TimestampQuality == "approximate") &&
                revision.Segments.Any(s => s.StartMs >= 5500 || s.EndMs > 5500),
                "Transcription spans the resumed portion within the compact 11000 ms timeline");
            string text = Regex.Replace(string.Join(" ", revision.Segments.Select(s => s.Text)).ToLowerInvariant(), "[^a-z]+", " ");
            Require(text.Contains("ask not what your country can do for you", StringComparison.Ordinal) &&
                text.Contains("what you can do for your country", StringComparison.Ordinal), "Expected public phrases survived pause/mix/ASR preparation");
            Preserve(result.AttemptDirectory);
            foreach (string extension in new[] { ".srt", ".vtt" })
            {
                string path = TranscriptSubtitleExporter.Export(paused, revision.RevisionId, Path.Combine(output, "paused-public" + extension));
                var roundTrip = TranscriptService.Import(new(paused, Path.GetFileName(paused), 11000, path, IsSynthetic: true));
                Require(revision.Segments.OrderBy(s => s.StartMs).Select(s => (s.StartMs, s.EndMs, s.Text))
                    .SequenceEqual(roundTrip.Segments.Select(s => (s.StartMs, s.EndMs, s.Text))),
                    extension + " export/reimport retains every millisecond and does not reapply the pause offset");
                subtitleEvidence.Add(new { Path = path, Sha256 = Hash(path), CueCount = roundTrip.Segments.Count,
                    LastCueEndMs = roundTrip.Segments.Max(s => s.EndMs) });
            }
            Require(immutable.All(pair => File.Exists(pair.Key) && Hash(pair.Key) == pair.Value), "All public sources, originals, clocks, CLI/model and derived ASR inputs remain unchanged");
        }
        catch (Exception ex) { errors.Add(ex.ToString()); Console.Error.WriteLine(ex.Message); }
        var summary = new { Status = errors.Count == 0 ? "PASS" : "FAIL", StartedAtUtc = started,
            DurationMilliseconds = stopwatch.ElapsedMilliseconds, OS = Environment.OSVersion.VersionString,
            Architecture = RuntimeInformation.OSArchitecture.ToString(), Runtime = Environment.Version.ToString(),
            SourceMode = "FILE-DRIVEN PUBLIC SPEECH / SYNTHETIC QPC PAUSE / REAL LOCAL VULKAN ASR",
            NativeCaptureStarted = false, MicrophoneCollected = false, Uploaded = false,
            Checks = checks, Errors = errors, Storage = storageEvidence, Waveform = waveformEvidence,
            Mp3 = mp3Evidence, Inference = engineEvidence, Subtitles = subtitleEvidence, ImmutableHashes = immutable,
            Limitations = "Synthetic pause storage is not a native capture/long-run/real-microphone test. Actual Vulkan inference is verified separately; model cue boundaries remain approximate, not guaranteed 10 ms acoustic alignment." };
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"M3Hfix ASR probe: {summary.Status}; {Path.Combine(output, "summary.json")}");
        return errors.Count == 0 ? 0 : 1;

        void Require(bool condition, string check) { if (!condition) throw new InvalidDataException(check); checks.Add(check); }
        void Preserve(string directory)
        { foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) immutable.TryAdd(path, Hash(path)); }
    }

    private static void CreateMeeting(string directory, byte[] pcm, bool withPause)
    {
        using var storage = new M1Storage(directory, Epoch, DateTimeOffset.UtcNow, "PUBLIC FILE-DRIVEN pause probe",
            isSynthetic: true, timelinePolicy: withPause ? RecordingTimeline.ExcludePaused : RecordingTimeline.Elapsed);
        var ranges = withPause ? new[] { (Start: 0, End: SplitFrame, Offset: 0UL), (Start: SplitFrame, End: TotalFrames, Offset: Pause100ns) }
            : new[] { (Start: 0, End: TotalFrames, Offset: 0UL) };
        for (int segment = 0; segment < ranges.Length; segment++)
        {
            var range = ranges[segment]; string id = "remote-" + segment;
            storage.AddSegment(id, "remote", new WaveFormat(Rate, 16, 1), excludedPause100ns: range.Offset);
            if (segment > 0) storage.WriteEvent("resume", "public-resume", range.Start * 1_000_000L / Rate);
            for (int frame = range.Start; frame < range.End; frame += 320)
            {
                int count = Math.Min(320, range.End - frame), localFrame = frame - range.Start;
                ulong qpc = Epoch + checked((ulong)frame * 625) + range.Offset;
                storage.WritePacket(id, pcm.AsSpan(frame * 2, count * 2).ToArray(),
                    new(localFrame, count, count, (ulong)localFrame, qpc, 0, true));
            }
            string operation = segment == ranges.Length - 1 ? "stop" : "pause";
            storage.WriteBoundary(id, "public-" + operation, operation,
                Epoch + checked((ulong)range.End * 625) + range.Offset, (ulong)(range.End - range.Start), 0, null);
            storage.WriteEvent(operation, "public-" + operation, range.End * 1_000_000L / Rate);
        }
        var result = storage.Finish(110_000_000, complete: true, warnings: []);
        if (!result.StorageComplete) throw new InvalidDataException("Synthetic public storage did not finish cleanly.");
    }

    private static byte[] PcmBytes(string path)
    { using var reader = new M2WaveReader(path); var bytes = new byte[checked((int)reader.DataLength)]; reader.ReadExactly(bytes); return bytes; }
    private static float[] Samples(string path)
    { using var reader = new M2WaveReader(path); return reader.ReadFrames(0, checked((int)reader.FrameCount)); }
    private static float[] DecodeMp3(string path)
    {
        using var reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
        using var memory = new MemoryStream(); byte[] bytes = new byte[65536]; int count;
        while ((count = reader.Read(bytes, 0, bytes.Length)) > 0) memory.Write(bytes, 0, count);
        return MemoryMarshal.Cast<byte, float>(memory.ToArray()).ToArray();
    }
    private static double Correlation(float[] left, float[] right, int start, int count, int delay = 0)
    {
        double cross = 0, l = 0, r = 0;
        for (int i = start; i < start + count; i++) { cross += (double)left[i] * right[i + delay]; l += (double)left[i] * left[i]; r += (double)right[i + delay] * right[i + delay]; }
        return cross / Math.Sqrt(l * r);
    }
    private static string Hash(string path)
    { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
}
