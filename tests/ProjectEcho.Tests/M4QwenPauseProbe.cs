using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

/// <summary>
/// Explicit public-file-only M3Hfix timeline to real Qwen/raw alignment probe. Never activates,
/// enumerates or plays an audio device. New and legacy files preserve the same raw packet clocks.
/// </summary>
internal static class M4QwenPauseProbe
{
    private const string PublicSha256 = "59dfb9a4acb36fe2a2affc14bacbee2920ff435cb13cc314a08c13f66ba7860e";
    private const int Rate = 16000, SplitFrame = 88000, ActiveFrames = 176000, PauseFrames = 277520;
    private const ulong Epoch = 90_000_000_000, Pause100ns = 173_450_000;
    private const long Active100ns = 110_000_000, Elapsed100ns = Active100ns + (long)Pause100ns;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(string[] args)
    {
        string[] keys = ["--cli", "--model", "--aligner", "--aligner-model", "--audio", "--output"];
        bool alignmentVulkan = args.Length == 14 && args[^1] == "--alignment-vulkan";
        if ((!alignmentVulkan && args.Length != 13) || args[0] != "--m4-qwen-pause-probe" ||
            !keys.All(key => args.Count(value => value == key) == 1) ||
            Enumerable.Range(0, 6).Any(index => !keys.Contains(args[1 + index * 2])))
        {
            Console.Error.WriteLine("Usage: --m4-qwen-pause-probe --cli <Qwen Vulkan exe> --model <external ASR GGUF> --aligner <raw-v1 exe> --aligner-model <external GGUF> --audio <hash-pinned public JFK WAV> --output <new project-local directory>");
            return 2;
        }
        string project = SyntheticAudioProbe.FindProjectRoot();
        string Value(string key) => args[Array.IndexOf(args, key) + 1];
        string output = SyntheticAudioProbe.SafeProjectPath(project, Value("--output"));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Use a new empty probe directory; prior evidence is never overwritten.");
        Directory.CreateDirectory(output);
        string audio = SyntheticAudioProbe.SafeProjectPath(project, Value("--audio"));
        string executable = RuntimeInput(Value("--cli"), project), model = RuntimeInput(Value("--model"), project);
        string aligner = RuntimeInput(Value("--aligner"), project), alignmentModel = RuntimeInput(Value("--aligner-model"), project);
        var immutable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var checks = new List<string>(); var errors = new List<string>();
        var storageEvidence = new List<object>(); var waveEvidence = new List<object>();
        var inferenceEvidence = new List<object>(); var subtitleEvidence = new List<object>();
        var firstCue = new Dictionary<string, long>(); var lastCue = new Dictionary<string, long>();
        var clockHashes = new Dictionary<string, string>(); var originalWaveHashes = new Dictionary<string, string[]>();
        var watch = Stopwatch.StartNew(); DateTimeOffset started = DateTimeOffset.UtcNow;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            Require(OperatingSystem.IsWindows(), "Actual Windows execution");
            Require(Hash(audio) == PublicSha256, "Hash-pinned 11-second public JFK source");
            foreach (string path in new[] { audio, executable, model, aligner, alignmentModel }) immutable.TryAdd(path, Hash(path));
            byte[] pcm;
            using (var input = new M2WaveReader(audio))
            {
                Require(input.Format is { SampleRate: Rate, Channels: 1, BitsPerSample: 16, IsFloat: false } &&
                    input.FrameCount == ActiveFrames, "Public source is complete 16 kHz mono PCM16");
                pcm = new byte[checked((int)input.DataLength)]; input.ReadExactly(pcm);
            }
            var configuration = new LocalTranscriptionConfiguration(QwenTranscriber.BackendId, executable, model, "en", 6,
                "Qwen3-ASR-0.6B-Q8_0 / public pause probe", ModelLicenseAcknowledged: true,
                Hardware: LocalTranscriptionHardware.Vulkan, GpuDeviceIndex: 0,
                QwenAlignment: new(aligner, alignmentModel, alignmentVulkan ? LocalTranscriptionHardware.Vulkan : LocalTranscriptionHardware.Cpu));
            Require(new QwenTranscriber().Inspect(configuration).Ready, "Selected native Qwen and raw-aligner files are ready");
            foreach (bool exclude in new[] { true, false })
            {
                string id = exclude ? "pause-excluded" : "legacy-elapsed";
                string policy = exclude ? RecordingTimeline.ExcludePaused : RecordingTimeline.Elapsed;
                long duration100ns = exclude ? Active100ns : Elapsed100ns;
                long durationMs = duration100ns / 10000;
                int expectedFrames = exclude ? ActiveFrames : ActiveFrames + PauseFrames;
                string meeting = Path.Combine(output, "recordings", id);
                string reference = Path.Combine(output, "recordings", id + "-continuous-reference");
                try
                {
                    Console.WriteLine("M4 Qwen pause probe: " + id);
                    CreatePausedMeeting(meeting, pcm, policy);
                    if (!exclude) MakeLegacyFixture(meeting); // Fixture construction only; freeze all files afterward.
                    byte[] referencePcm = exclude ? pcm : InsertPause(pcm);
                    CreateContinuousReference(reference, referencePcm);
                    Preserve(meeting); Preserve(reference);
                    var recovered = M1Recovery.Read(meeting, timeout.Token);
                    var tracks = recovered.Capture.Tracks.OrderBy(track => track.Packets[0].QpcPosition100ns).ToArray();
                    Require(recovered.HasCheckpoint && recovered.IsCleanEnd && recovered.Issues.Count == 0 &&
                        recovered.Capture.TimelinePolicy == policy && recovered.Capture.Duration100ns == duration100ns &&
                        tracks.Length == 2 && tracks.Sum(track => track.FrameCount) == ActiveFrames, id + ": disk-only policy and duration recovery");
                    Require(tracks[0].ExcludedPause100ns == 0 && tracks[1].ExcludedPause100ns == (exclude ? Pause100ns : 0) &&
                        tracks[0].Packets[0].QpcPosition100ns == Epoch &&
                        tracks[1].Packets[0].QpcPosition100ns == Epoch + 55_000_000 + Pause100ns &&
                        tracks.All(track => track.Packets[0].DevicePosition == 0 && track.Packets.Sum(packet => packet.FrameCount) == SplitFrame),
                        id + ": raw QPC/device/frame count retained independently of pause offset");
                    Require(pcm.AsSpan().SequenceEqual(tracks.SelectMany(track => PcmBytes(track.WavePath)).ToArray()),
                        id + ": every original PCM byte and final frame preserved");
                    clockHashes[id] = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(tracks.Select(track => track.Packets))));
                    originalWaveHashes[id] = tracks.Select(track => Hash(track.WavePath)).ToArray();
                    storageEvidence.Add(new
                    {
                        Id = id, MeetingDirectory = meeting, TimelinePolicy = policy, LegacyMissingPolicyFixture = !exclude,
                        Epoch, Pause100ns, RawElapsed100ns = Elapsed100ns, Active100ns, RecoveredDuration100ns = recovered.Capture.Duration100ns,
                        OriginalPcmFrames = ActiveFrames, RawPacketClockSha256 = clockHashes[id], recovered.Issues,
                        Tracks = tracks.Select(track => new { track.SegmentId, track.FrameCount, track.ExcludedPause100ns,
                            FirstPacket = track.Packets[0], LastPacket = track.Packets[^1], WaveSha256 = Hash(track.WavePath) }).ToArray()
                    });

                    var mix = M2ExportService.RenderMeeting(meeting, encoder: null, timeout.Token);
                    var referenceMix = M2ExportService.RenderMeeting(reference, encoder: null, timeout.Token);
                    Require(mix.SourceStorageComplete && mix.SampleRate == 48000 && mix.FrameCount == expectedFrames * 3L &&
                        mix.DurationUs == durationMs * 1000 && referenceMix.FrameCount == mix.FrameCount,
                        id + ": complete 48 kHz mix follows this recording timeline exactly");
                    float[] mixed = Samples(mix.WavePath), expected = Samples(referenceMix.WavePath);
                    int firstBoundary = SplitFrame * 3, secondBoundary = (SplitFrame + (exclude ? 0 : PauseFrames)) * 3;
                    double all = Correlation(expected, mixed, 0, mixed.Length);
                    double head = Correlation(expected, mixed, 0, 48000), tail = Correlation(expected, mixed, mixed.Length - 48000, 48000);
                    double before = Correlation(expected, mixed, firstBoundary - 2400, 4800);
                    double resumed = Correlation(expected, mixed, secondBoundary - 2400, 4800);
                    Require(all > .9999 && head > .99999 && tail > .99999 && before > .995 && resumed > .995,
                        id + ": full/head/pause-boundary/resume-boundary/tail match continuous reference");
                    Require(Enumerable.Range(0, mixed.Length).Where(index =>
                        !(index >= firstBoundary - 2 && index < firstBoundary) &&
                        !(index >= secondBoundary - 2 && index < secondBoundary)).All(index => mixed[index] == expected[index]),
                        id + ": every mix sample outside the declared resampler boundary subframes is identical");
                    if (!exclude)
                        Require(mixed.AsSpan(firstBoundary, PauseFrames * 3).ContainsAnyExcept(0f) == false,
                            id + ": all 17.345 seconds of legacy pause remain explicit mixed silence");
                    var prepared = AsrAudioPreparation.Prepare(mix.WavePath, mix.WaveSha256,
                        Path.Combine(meeting, "transcripts", "inputs"), timeout.Token);
                    var referencePrepared = AsrAudioPreparation.Prepare(referenceMix.WavePath, referenceMix.WaveSha256,
                        Path.Combine(reference, "transcripts", "inputs"), timeout.Token);
                    float[] preparedPcm = Samples(prepared.Path), expectedPrepared = Samples(referencePrepared.Path);
                    Require(prepared.Frames == expectedFrames && referencePrepared.Frames == expectedFrames &&
                        prepared.SourceFrames == mix.FrameCount, id + ": full 16 kHz ASR frames keep the correct pause policy");
                    double preparedCorrelation = Correlation(expectedPrepared, preparedPcm, 0, expectedFrames);
                    double preparedTail = Correlation(expectedPrepared, preparedPcm, expectedFrames - Rate, Rate);
                    Require(preparedCorrelation > .9999 && preparedTail > .9999, id + ": ASR preparation preserves full waveform and tail");
                    if (!exclude)
                        Require(preparedPcm.AsSpan(SplitFrame + Rate, PauseFrames - 2 * Rate).ContainsAnyExcept(0f) == false,
                            id + ": legacy pause is retained by 16 kHz preparation, not cut by silence inference");
                    waveEvidence.Add(new { Id = id, mix.FrameCount, mix.DurationUs, mix.WaveSha256,
                        FullCorrelation = all, HeadCorrelation = head, TailCorrelation = tail, FirstBoundaryCorrelation = before,
                        ResumeBoundaryCorrelation = resumed, prepared.Frames, prepared.Sha256,
                        AsrFullCorrelation = preparedCorrelation, AsrTailCorrelation = preparedTail,
                        AllowedInterpolationDifferenceSubframesPerBoundary = 2 });
                    foreach (string path in new[] { mix.WavePath, referenceMix.WavePath, prepared.Path, referencePrepared.Path }) immutable.TryAdd(path, Hash(path));

                    var request = new LocalTranscriptionRequest(meeting, Path.GetFileName(meeting), durationMs,
                        prepared.Path, prepared.Sha256, configuration, IsSynthetic: true,
                        SourceMixSha256: mix.WaveSha256, TimelinePolicy: policy);
                    var result = await new QwenTranscriber().RunAsync(request, timeout.Token,
                        new ProbeProgress(value => Console.WriteLine(id + ": " + value.Stage + " " + value.Percent)));
                    using var attempt = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.AttemptDirectory, "attempt.json")));
                    inferenceEvidence.Add(new { Id = id, Status = result.Status.ToString(), result.ElapsedMilliseconds, result.Message,
                        result.AttemptDirectory, Attempt = attempt.RootElement.Clone(), result.Revision });
                    Require(result.Status == LocalTranscriptionStatus.Succeeded && result.Revision is not null,
                        id + ": real Qwen recognition and raw acoustic alignment complete");
                    Require(attempt.RootElement.GetProperty("actualBackend").GetString() == "Vulkan0" &&
                        attempt.RootElement.GetProperty("alignmentHardware").GetString() == (alignmentVulkan ? "Vulkan0" : "CPU") &&
                        attempt.RootElement.GetProperty("coverage").GetProperty("complete").GetBoolean(),
                        id + ": actual Vulkan ASR, CPU alignment and complete frame coverage reported");
                    var revision = result.Revision!;
                    Require(revision.IsSynthetic && revision.MeetingId == request.MeetingId &&
                        revision.Alignment is { } provenance && provenance.TimelinePolicy == policy &&
                        provenance.InputWaveSha256 == prepared.Sha256 && provenance.SourceMixSha256 == mix.WaveSha256 &&
                        provenance.FrameCount == expectedFrames && revision.Segments.Count > 0 &&
                        revision.Segments.All(segment => segment.StartMs >= 0 && segment.EndMs > segment.StartMs &&
                            segment.EndMs <= durationMs && segment.TimestampQuality == "approximate"),
                        id + ": transcript keeps identity, synthetic flag, hashes and one valid recording timeline");
                    string text = Regex.Replace(string.Join(" ", revision.Segments.Select(segment => segment.Text)).ToLowerInvariant(), "[^a-z]+", " ");
                    Require(text.Contains("ask not what your country can do for you", StringComparison.Ordinal) &&
                        text.Contains("what you can do for your country", StringComparison.Ordinal), id + ": both public phrases and resumed tail survive ASR");
                    firstCue[id] = revision.Segments.Min(segment => segment.StartMs!.Value);
                    lastCue[id] = revision.Segments.Max(segment => segment.EndMs!.Value);
                    Require(lastCue[id] >= durationMs - 2000, id + ": last sentence reaches actual final speech near the audio tail");
                    if (!exclude) Require(lastCue[id] >= (SplitFrame + PauseFrames) * 1000L / Rate,
                        id + ": resumed words remain after the legacy pause rather than at compact time");
                    Require(TranscriptService.Load(meeting, revision.RevisionId).Segments.SequenceEqual(revision.Segments),
                        id + ": disk reopen retains every sentence and millisecond");
                    using (var alignment = JsonDocument.Parse(File.ReadAllText(Path.Combine(meeting, revision.Alignment!.EvidenceFile))))
                    {
                        long acousticEnd = 0;
                        foreach (var window in alignment.RootElement.GetProperty("windowEvidence").EnumerateArray())
                        {
                            long offset = window.GetProperty("window").GetProperty("inputStartFrame").GetInt64();
                            using var words = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.AttemptDirectory, window.GetProperty("rawWords").GetString()!)));
                            acousticEnd = Math.Max(acousticEnd, offset + words.RootElement.EnumerateArray().Max(word => word.GetProperty("end_sample").GetInt64()));
                        }
                        Require(lastCue[id] == (acousticEnd * 1000 + Rate - 1) / Rate,
                            id + ": terminal subtitle uses the actual raw acoustic frame with one window offset");
                    }
                    Preserve(result.AttemptDirectory);
                    foreach (string extension in new[] { ".srt", ".vtt" })
                    {
                        string exported = TranscriptSubtitleExporter.Export(meeting, revision.RevisionId, Path.Combine(output, id + extension));
                        var imported = TranscriptService.Import(new(meeting, request.MeetingId, durationMs, exported, IsSynthetic: true));
                        Require(revision.Segments.OrderBy(segment => segment.StartMs).Select(segment => (segment.StartMs, segment.EndMs, segment.Text.Trim()))
                            .SequenceEqual(imported.Segments.Select(segment => (segment.StartMs, segment.EndMs, segment.Text.Trim()))),
                            id + ": " + extension + " round trip preserves all cues, text and milliseconds without reapplying pause");
                        subtitleEvidence.Add(new { Id = id, Format = extension, Path = exported, Sha256 = Hash(exported),
                            CueCount = imported.Segments.Count, LastEndMs = imported.Segments.Max(segment => segment.EndMs) });
                    }
                }
                catch (Exception ex) { errors.Add(id + ": " + ex); Console.Error.WriteLine(id + ": " + ex.Message); }
            }
            Require(firstCue.Count == 2 && lastCue.Count == 2, "Both recording policies produced complete timed model versions");
            Require(clockHashes["pause-excluded"] == clockHashes["legacy-elapsed"] &&
                originalWaveHashes["pause-excluded"].SequenceEqual(originalWaveHashes["legacy-elapsed"]),
                "New and legacy cases have byte-identical original WAVs and identical raw QPC/device/frame metadata");
            Require(Math.Abs(firstCue["legacy-elapsed"] - firstCue["pause-excluded"]) <= 1000,
                "Leading speech stays on the same origin for new and legacy timelines");
            Require(Math.Abs(lastCue["legacy-elapsed"] - lastCue["pause-excluded"] - (long)Pause100ns / 10000) <= 1000,
                "Legacy tail contains the pause once; new tail contains it zero times (model-boundary tolerance 1000 ms)");
        }
        catch (Exception ex) { errors.Add(ex.ToString()); Console.Error.WriteLine(ex.Message); }
        var hashChecks = immutable.Select(pair => new { Path = pair.Key, ExpectedSha256 = pair.Value,
            Unchanged = File.Exists(pair.Key) && Hash(pair.Key) == pair.Value }).ToArray();
        if (hashChecks.Any(value => !value.Unchanged)) errors.Add("One or more original/public/runtime/input files changed.");
        var assemblies = new[] { typeof(QwenTranscriber).Assembly, typeof(M1Storage).Assembly, typeof(M2ExportService).Assembly }
            .Distinct().Select(assembly => new { assembly.Location, Sha256 = Hash(assembly.Location), assembly.FullName }).ToArray();
        var summary = new
        {
            Status = errors.Count == 0 ? "PASS" : "FAIL", StartedUtc = started, ElapsedMs = watch.ElapsedMilliseconds,
            OS = Environment.OSVersion.VersionString, Architecture = RuntimeInformation.OSArchitecture.ToString(), Runtime = Environment.Version.ToString(),
            SourceMode = "HASH-PINNED PUBLIC JFK / SYNTHETIC DISK QPC PAUSE / REAL QWEN VULKAN + RAW CPU ALIGNMENT",
            NativeCaptureStarted = false, MicrophoneCollected = false, AudioPlayed = false, Uploaded = false,
            Checks = checks, Errors = errors, Storage = storageEvidence, Waveform = waveEvidence,
            Inference = inferenceEvidence, Subtitles = subtitleEvidence, ImmutableHashes = hashChecks, LoadedProductionAssemblies = assemblies,
            Limitations = "Synthetic pause construction and real local inference are separate claims. This is not native hardware recording, three-hour quality, microphone, natural multilingual meeting or clean-Windows validation. Times remain acoustic model estimates."
        };
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, Json));
        Console.WriteLine("M4 Qwen pause probe: " + summary.Status + "; " + Path.Combine(output, "summary.json"));
        return errors.Count == 0 ? 0 : 1;

        void Require(bool condition, string check) { if (!condition) throw new InvalidDataException(check); checks.Add(check); }
        void Preserve(string directory)
        { foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) immutable.TryAdd(path, Hash(path)); }
    }

    private static void CreatePausedMeeting(string directory, byte[] pcm, string policy)
    {
        bool exclude = policy == RecordingTimeline.ExcludePaused;
        using var storage = new M1Storage(directory, Epoch, DateTimeOffset.UtcNow, "PUBLIC synthetic QPC pause / Qwen timeline probe", true, policy);
        for (int segment = 0; segment < 2; segment++)
        {
            string id = "remote-" + segment;
            int start = segment * SplitFrame, end = start + SplitFrame;
            ulong rawPause = segment == 0 ? 0 : Pause100ns, excluded = exclude ? rawPause : 0;
            storage.AddSegment(id, "remote", new WaveFormat(Rate, 16, 1), excludedPause100ns: excluded);
            if (segment > 0) storage.WriteEvent("resume", "public-resume", start * 1_000_000L / Rate + (exclude ? 0 : (long)Pause100ns / 10));
            for (int frame = start; frame < end; frame += 320)
            {
                int count = Math.Min(320, end - frame), local = frame - start;
                storage.WritePacket(id, pcm.AsSpan(frame * 2, count * 2).ToArray(),
                    new(local, count, count, (ulong)local, Epoch + (ulong)frame * 625 + rawPause, 0, true));
            }
            string operation = segment == 0 ? "pause" : "stop";
            storage.WriteBoundary(id, "public-" + operation, operation, Epoch + (ulong)end * 625 + rawPause, SplitFrame, 0, null);
            storage.WriteEvent(operation, "public-" + operation, end * 1_000_000L / Rate + (exclude ? 0 : (long)rawPause / 10));
        }
        var result = storage.Finish(exclude ? Active100ns : Elapsed100ns, true, []);
        if (!result.StorageComplete) throw new InvalidDataException("File-driven pause storage did not finish cleanly.");
    }

    private static void CreateContinuousReference(string directory, byte[] pcm)
    {
        int total = pcm.Length / 2;
        using var storage = new M1Storage(directory, Epoch, DateTimeOffset.UtcNow, "PUBLIC synthetic continuous PCM reference", true);
        storage.AddSegment("reference", "remote", new WaveFormat(Rate, 16, 1));
        for (int frame = 0; frame < total; frame += 320)
        {
            int count = Math.Min(320, total - frame);
            storage.WritePacket("reference", pcm.AsSpan(frame * 2, count * 2).ToArray(), new(frame, count, count, (ulong)frame, Epoch + (ulong)frame * 625, 0, true));
        }
        storage.WriteBoundary("reference", "stop", "stop", Epoch + (ulong)total * 625, (ulong)total, 0, null);
        storage.WriteEvent("stop", "stop", total * 1_000_000L / Rate);
        if (!storage.Finish(total * 625L, true, []).StorageComplete) throw new InvalidDataException("Continuous reference storage failed.");
    }

    private static void MakeLegacyFixture(string directory)
    {
        string journal = Path.Combine(directory, "journal", "capture-index.jsonl");
        var lines = File.ReadAllLines(journal).Select(line =>
        {
            var envelope = JsonNode.Parse(line)!.AsObject(); var data = envelope["data"]!.AsObject();
            data.Remove("timelinePolicy"); data.Remove("excludedPause100ns");
            envelope["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToJsonString())));
            return envelope.ToJsonString();
        }).ToArray();
        File.WriteAllLines(journal, lines, new UTF8Encoding(false));
        string manifest = Path.Combine(directory, "meeting.json"); var root = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
        root.Remove("timelinePolicy"); foreach (var chunk in root["chunks"]!.AsArray()) chunk!.AsObject().Remove("ExcludedPause100ns");
        File.WriteAllText(manifest, root.ToJsonString(), new UTF8Encoding(false));
    }

    private static byte[] InsertPause(byte[] pcm)
    {
        byte[] result = new byte[(ActiveFrames + PauseFrames) * 2];
        pcm.AsSpan(0, SplitFrame * 2).CopyTo(result);
        pcm.AsSpan(SplitFrame * 2).CopyTo(result.AsSpan((SplitFrame + PauseFrames) * 2));
        return result;
    }

    private static string RuntimeInput(string path, string project)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(project.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith("H:\\AI\\Qwen\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Probe runtime/model must be in ProjectEcho or the explicitly authorized H:\\AI\\Qwen folder.");
        for (string? current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Probe runtime/model cannot use reparse points.");
        if (!File.Exists(full)) throw new FileNotFoundException("Missing native runtime/model.", full);
        return full;
    }

    private static byte[] PcmBytes(string path)
    { using var reader = new M2WaveReader(path); byte[] bytes = new byte[checked((int)reader.DataLength)]; reader.ReadExactly(bytes); return bytes; }
    private static float[] Samples(string path)
    { using var reader = new M2WaveReader(path); return reader.ReadFrames(0, checked((int)reader.FrameCount)); }
    private static double Correlation(float[] reference, float[] actual, int start, int count)
    {
        double cross = 0, left = 0, right = 0;
        for (int index = start; index < start + count; index++)
        { cross += (double)reference[index] * actual[index]; left += (double)reference[index] * reference[index]; right += (double)actual[index] * actual[index]; }
        return cross / Math.Sqrt(left * right);
    }
    private static string Hash(string path)
    { using var input = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(input)); }
    private sealed class ProbeProgress(Action<LocalTranscriptionProgress> report) : IProgress<LocalTranscriptionProgress>
    { public void Report(LocalTranscriptionProgress value) => report(value); }
}
