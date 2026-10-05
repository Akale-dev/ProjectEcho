using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

/// <summary>
/// Explicit opt-in rerun of a user-authorized saved meeting, on an independent copy.
/// No capture, playback, network access, source mutation, or private text in console/summary.
/// </summary>
internal static class M3iFullUserAsrProbe
{
    private static readonly JsonSerializerOptions Json = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal static async Task<int> RunAsync(string[] args)
    {
        string[] keys = ["--source", "--output", "--cli", "--model"];
        if (args.Length != 9 || args[0] != "--m3i-full-user-asr-probe" ||
            keys.Any(key => args.Count(value => value == key) != 1) ||
            Enumerable.Range(0, 4).Any(index => !keys.Contains(args[1 + index * 2])))
        {
            Console.Error.WriteLine("Usage: --m3i-full-user-asr-probe --source <authorized saved meeting> --output <new project-local directory> --cli <existing Vulkan whisper-cli.exe> --model <existing matching model>");
            return 2;
        }

        string? output = null;
        string stage = "arguments";
        var errors = new List<object>();
        var checks = new List<string>();
        var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stopwatch = Stopwatch.StartNew();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string? source = null;
        object? inputEvidence = null, inferenceEvidence = null, exportEvidence = null;
        int verifiedOriginals = 0;
        bool originalInventoryUnchanged = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromHours(6));
        using var progress = new NumericProgress();
        try
        {
            Require(OperatingSystem.IsWindows(), "actual_windows");
            string root = WorkspaceRoot();
            string Value(string key) => SafePath(root, args[Array.IndexOf(args, key) + 1]);
            source = Value("--source");
            string candidateOutput = Value("--output"), cli = Value("--cli"), model = Value("--model");
            Require(IsWithin(source, Path.Combine(root, "DEBUG")), "source_is_private_debug_input");
            Require(IsWithin(candidateOutput, Path.Combine(root, ".local", "M3I-ASRFix")), "output_is_local_asrfix_evidence");
            Require(!Directory.Exists(candidateOutput) && !File.Exists(candidateOutput), "output_is_new");
            Require(Directory.Exists(source), "source_exists");
            Directory.CreateDirectory(candidateOutput);
            output = candidateOutput;

            stage = "source_hashes";
            foreach (string file in EnumerateSafeFiles(source)) originals.Add(file, Hash(file));
            Require(originals.Count > 0, "source_inventory_is_nonempty");
            foreach (string file in new[] { cli, model }) assets.Add(file, Hash(file));

            stage = "original_attempt_contract";
            string[] attempts = originals.Keys.Where(path => Path.GetFileName(path) == "attempt.json" &&
                IsWithin(path, Path.Combine(source, "transcripts", "asr"))).ToArray();
            Require(attempts.Length == 1, "one_unambiguous_original_attempt");
            using var attempt = ReadJson(attempts.Single());
            var old = attempt.RootElement;
            using var originalMeeting = ReadJson(Path.Combine(source, "meeting.json"));
            Require(!old.GetProperty("isSynthetic").GetBoolean() &&
                !originalMeeting.RootElement.GetProperty("isSynthetic").GetBoolean(), "real_recording_label_retained");
            string modelHash = old.GetProperty("modelSha256").GetString() ?? "";
            string waveHash = old.GetProperty("inputWaveSha256").GetString() ?? "";
            Require(IsHash(modelHash) && IsHash(waveHash), "original_hash_contract_valid");
            Require(assets[model].Equals(modelHash, StringComparison.OrdinalIgnoreCase), "model_hash_matches_original_attempt");
            string[] waves = originals.Where(pair => Path.GetExtension(pair.Key).Equals(".wav", StringComparison.OrdinalIgnoreCase) &&
                pair.Value.Equals(waveHash, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray();
            Require(waves.Length == 1, "one_exact_original_asr_wave");
            string wave = waves.Single();
            long frames, durationMs;
            using (var reader = new M2WaveReader(wave))
            {
                Require(reader.Format is { SampleRate: 16000, Channels: 1, BitsPerSample: 16, IsFloat: false },
                    "original_input_is_16k_mono_pcm16");
                frames = reader.FrameCount;
                durationMs = checked((frames * 1000 + 15999) / 16000);
                Require(frames > 0 && reader.DataLength == checked(frames * 2), "actual_complete_pcm_frames");
            }
            Require(durationMs == old.GetProperty("meetingDurationMs").GetInt64(), "ceil_frame_duration_matches_original_attempt");
            string meetingId = old.GetProperty("meetingId").GetString() ?? "";
            Require(!string.IsNullOrWhiteSpace(meetingId), "original_meeting_identity_available");
            string language = old.GetProperty("language").GetString() ?? "";
            int threads = old.GetProperty("threads").GetInt32();
            string? prompt = old.TryGetProperty("initialPrompt", out var initialPrompt) &&
                initialPrompt.ValueKind != JsonValueKind.Null ? initialPrompt.GetString() : null;
            string? modelName = old.TryGetProperty("modelName", out var name) ? name.GetString() : null;
            inputEvidence = new
            {
                frames, sampleRate = 16000, channels = 1, bitsPerSample = 16,
                exactDurationMs = frames * 1000m / 16000, ceilDurationMs = durationMs,
                waveSha256 = waveHash, modelSha256 = modelHash, executableSha256 = assets[cli],
                language, threads, reusedOriginalPrompt = true, inputReencoded = false, isSynthetic = false
            };

            stage = "independent_copy";
            string meeting = Path.Combine(output, "meeting");
            string inputDirectory = Path.Combine(meeting, "transcripts", "inputs");
            Directory.CreateDirectory(inputDirectory);
            File.Copy(Path.Combine(source, "meeting.json"), Path.Combine(meeting, "meeting.json"), overwrite: false);
            string copiedWave = Path.Combine(inputDirectory, "audio-16k.wav");
            File.Copy(wave, copiedWave, overwrite: false);
            Require(Hash(copiedWave).Equals(waveHash, StringComparison.OrdinalIgnoreCase), "copy_is_exact_input_bytes");
            assets.Add(copiedWave, Hash(copiedWave));
            assets.Add(Path.Combine(meeting, "meeting.json"), Hash(Path.Combine(source, "meeting.json")));
            var configuration = new WhisperConfiguration(cli, model, language, threads, modelName,
                ModelLicenseAcknowledged: true, Hardware: LocalTranscriptionHardware.Vulkan, GpuDeviceIndex: 0,
                InitialPrompt: prompt);
            Require(WhisperTranscriber.Inspect(configuration).Ready, "production_configuration_ready");

            stage = "production_inference";
            progress.Start();
            WhisperRunResult run;
            try
            {
                run = await Task.Run(() => WhisperTranscriber.RunAsync(
                    new(meeting, meetingId, durationMs, copiedWave, waveHash, configuration, IsSynthetic: false),
                    deadline.Token, progress));
            }
            finally { await progress.StopAsync(); }
            using var newAttempt = ReadJson(Path.Combine(run.AttemptDirectory, "attempt.json"));
            var actual = newAttempt.RootElement;
            string? backend = actual.GetProperty("actualGpuBackend").GetString();
            string? hardwareEvidence = actual.GetProperty("hardwareEvidence").GetString();
            inferenceEvidence = new
            {
                run.Status, run.ElapsedMilliseconds,
                requestedHardware = "Vulkan", actualGpuBackend = backend, hardwareEvidence,
                exitCode = actual.GetProperty("exitCode").ValueKind == JsonValueKind.Number
                    ? actual.GetProperty("exitCode").GetInt32() : (int?)null,
                maximumEnginePercent = progress.MaximumPercent, progressCallbackCount = progress.CallbackCount,
                executableSha256 = assets[cli], modelSha256 = modelHash,
                coreAssemblyVersion = typeof(WhisperTranscriber).Assembly.GetName().Version?.ToString(),
                coreAssemblySha256 = Hash(typeof(WhisperTranscriber).Assembly.Location),
                cliVersion = "NotProbed; identified by executable SHA-256"
            };
            Require(run.Status == "Succeeded" && run.Revision is not null, "full_production_inference_succeeded");
            Require(backend == "Vulkan0" && hardwareEvidence == "SelectedGpuBackend", "actual_vulkan0_no_cpu_fallback");
            Require(actual.GetProperty("inputWaveSha256").GetString() == waveHash &&
                actual.GetProperty("modelSha256").GetString() == modelHash, "production_used_same_audio_and_model");
            Require(!actual.GetProperty("isSynthetic").GetBoolean(), "production_attempt_remains_real");

            stage = "revision_and_exports";
            var revision = TranscriptService.Load(meeting, run.Revision!.RevisionId, deadline.Token);
            Require(!revision.IsSynthetic && revision.MeetingDurationMs == durationMs && revision.Segments.Count > 0,
                "saved_revision_is_real_and_complete_duration");
            Require(revision.Segments.All(segment =>
                segment.StartMs is null ? segment.EndMs is null && segment.TimestampQuality == "untimed" :
                segment.StartMs >= 0 && segment.EndMs > segment.StartMs && segment.EndMs <= durationMs),
                "all_timed_ranges_valid_untimed_remain_untimed");
            int untimed = revision.Segments.Count(segment => segment.StartMs is null);
            string exportedText = TranscriptSubtitleExporter.ExportText(meeting, revision.RevisionId,
                Path.Combine(output, "transcript.txt"), deadline.Token);
            IEnumerable<TranscriptSegment> textOrder = untimed == 0
                ? revision.Segments.OrderBy(segment => segment.StartMs) : revision.Segments;
            string expectedText = string.Concat(textOrder.Select(segment =>
                segment.Text.Replace("\r\n", "\n").Replace('\r', '\n') + "\n"));
            Require(File.ReadAllText(exportedText, Encoding.UTF8) == expectedText, "txt_contains_every_saved_word");
            string exportedJson = Path.Combine(output, "transcript.json");
            WriteNewJson(exportedJson, revision);
            var jsonRoundTrip = TranscriptService.Import(new(meeting, meetingId, durationMs, exportedJson,
                "Local full ASR JSON round trip", IsSynthetic: false), deadline.Token);
            Require(revision.Segments.SequenceEqual(jsonRoundTrip.Segments), "json_round_trip_retains_all_text_and_timestamps");

            string? srtHash = null;
            bool srtRoundTrip = false;
            if (untimed == 0)
            {
                string srt = TranscriptSubtitleExporter.Export(meeting, revision.RevisionId,
                    Path.Combine(output, "transcript.srt"), deadline.Token);
                var reimport = TranscriptService.Import(new(meeting, meetingId, durationMs, srt,
                    "Local full ASR SRT round trip", IsSynthetic: false), deadline.Token);
                Require(revision.Segments.OrderBy(segment => segment.StartMs).Select(segment =>
                    (segment.StartMs, segment.EndMs, segment.Text)).SequenceEqual(reimport.Segments.Select(segment =>
                    (segment.StartMs, segment.EndMs, segment.Text))), "srt_round_trip_retains_all_text_and_timestamps");
                srtHash = Hash(srt);
                srtRoundTrip = true;
            }
            exportEvidence = new
            {
                segmentCount = revision.Segments.Count, timedSegmentCount = revision.Segments.Count - untimed,
                untimedSegmentCount = untimed, warningCount = revision.Warnings.Count,
                characterCount = revision.Segments.Sum(segment => (long)segment.Text.Length),
                lastTimedEndMs = revision.Segments.Where(segment => segment.EndMs is not null)
                    .Select(segment => segment.EndMs).DefaultIfEmpty(null).Max(),
                txtSha256 = Hash(exportedText), jsonSha256 = Hash(exportedJson), srtSha256 = srtHash,
                jsonRoundTripVerified = true, srtExported = untimed == 0, srtRoundTripVerified = srtRoundTrip,
                srtSuppressedForUntimedText = untimed > 0
            };
        }
        catch (Exception error)
        {
            // Exception messages can contain private paths, model prompts or recognized words.
            errors.Add(new { stage, errorType = error.GetType().Name });
            Console.Error.WriteLine($"Full ASR probe failed: stage={stage}; type={error.GetType().Name}.");
        }
        finally
        {
            await progress.StopAsync();
            stage = "immutability_verification";
            foreach (var original in originals)
            {
                try
                {
                    if (Hash(original.Key) == original.Value) verifiedOriginals++;
                    else errors.Add(new { stage, errorType = "OriginalHashChanged" });
                }
                catch (Exception error) { errors.Add(new { stage, errorType = error.GetType().Name }); }
            }
            if (source is not null && originals.Count > 0)
            {
                try
                {
                    originalInventoryUnchanged = EnumerateSafeFiles(source).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        .SetEquals(originals.Keys);
                    if (!originalInventoryUnchanged) errors.Add(new { stage, errorType = "OriginalInventoryChanged" });
                }
                catch (Exception error) { errors.Add(new { stage, errorType = error.GetType().Name }); }
            }
            foreach (var asset in assets)
            {
                try
                {
                    if (Hash(asset.Key) != asset.Value) errors.Add(new { stage, errorType = "InputOrToolHashChanged" });
                }
                catch (Exception error) { errors.Add(new { stage, errorType = error.GetType().Name }); }
            }
        }
        stopwatch.Stop();
        if (output is not null)
        {
            WriteNewJson(Path.Combine(output, "summary.json"), new
            {
                schemaVersion = "m3i-full-user-asr-probe-1", status = errors.Count == 0 ? "PASS" : "FAIL",
                startedAtUtc = started, durationMilliseconds = stopwatch.ElapsedMilliseconds,
                os = Environment.OSVersion.VersionString, architecture = RuntimeInformation.OSArchitecture.ToString(),
                runtime = Environment.Version.ToString(), isSynthetic = false,
                privateTextIncluded = false, privatePathsIncluded = false,
                nativeCaptureStarted = false, microphoneCollected = false, audioPlayed = false, uploaded = false,
                sourceFileCount = originals.Count, verifiedOriginalFileCount = verifiedOriginals,
                originalInventoryUnchanged, allOriginalHashesUnchanged = verifiedOriginals == originals.Count && originals.Count > 0,
                originalFileHashes = originals.Values.Order(StringComparer.Ordinal).ToArray(),
                input = inputEvidence, inference = inferenceEvidence, exports = exportEvidence, checks, errors
            });
        }
        Console.WriteLine($"Full ASR probe: {(errors.Count == 0 ? "PASS" : "FAIL")}; originalFiles={verifiedOriginals}/{originals.Count}; elapsedMs={stopwatch.ElapsedMilliseconds}.");
        return errors.Count == 0 ? 0 : 1;

        void Require(bool condition, string check)
        {
            if (!condition) { stage = check; throw new InvalidDataException(); }
            checks.Add(check);
        }
    }

    private static string WorkspaceRoot()
    {
        string? result = null;
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")) && File.Exists(Path.Combine(current.FullName, "global.json")))
                result = current.FullName;
        return result ?? throw new DirectoryNotFoundException();
    }

    private static string SafePath(string root, string value)
    {
        string full = Path.GetFullPath(Path.Combine(root, value));
        if (!IsWithin(full, root) || full.StartsWith("\\\\", StringComparison.Ordinal) ||
            full.AsSpan(Path.GetPathRoot(full)!.Length).Contains(':')) throw new InvalidDataException();
        NoReparse(full);
        return full;
    }

    private static bool IsWithin(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void NoReparse(string path)
    {
        for (string? item = Path.GetFullPath(path); item is not null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException();
    }

    private static IEnumerable<string> EnumerateSafeFiles(string directory)
    {
        NoReparse(directory);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            NoReparse(entry);
            if (Directory.Exists(entry))
                foreach (string file in EnumerateSafeFiles(entry)) yield return file;
            else yield return entry;
        }
    }

    private static bool IsHash(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);

    private static string Hash(string path)
    {
        NoReparse(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    private static JsonDocument ReadJson(string path)
    {
        NoReparse(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 16 * 1024 * 1024) throw new InvalidDataException();
        return JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static void WriteNewJson(string path, object value)
    {
        NoReparse(path);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, value, Json);
        file.Flush(true);
    }

    private sealed class NumericProgress : IProgress<LocalTranscriptionProgress>, IDisposable
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource stop = new();
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private LocalTranscriptionProgress current = new(LocalTranscriptionStage.Validating);
        private Task? reporter;
        private int? maximumPercent;
        private int callbackCount;
        internal int? MaximumPercent { get { lock (gate) return maximumPercent; } }
        internal int CallbackCount { get { lock (gate) return callbackCount; } }
        public void Report(LocalTranscriptionProgress value)
        {
            lock (gate)
            {
                current = value;
                callbackCount++;
                if (value.Percent is int percent) maximumPercent = Math.Max(maximumPercent ?? percent, percent);
            }
        }
        internal void Start() => reporter = PrintAsync();
        private async Task PrintAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            try
            {
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    LocalTranscriptionProgress snapshot;
                    lock (gate) snapshot = current;
                    Console.WriteLine($"ASR stage={snapshot.Stage}; enginePercent={snapshot.Percent?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; elapsedSeconds={watch.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}.");
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        internal async Task StopAsync()
        {
            stop.Cancel();
            if (reporter is not null) await reporter;
        }
        public void Dispose() => stop.Dispose();
    }
}
