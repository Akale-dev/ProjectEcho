using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

/// <summary>Explicit real Vulkan lifecycle probe. Reads only the fixed public JFK sample and existing local models.</summary>
internal static class M3dVulkanFullProbe
{
    private const string PublicSampleSha256 = "59dfb9a4acb36fe2a2affc14bacbee2920ff435cb13cc314a08c13f66ba7860e";
    private const string Prompt = "John F. Kennedy, United States, country; ACME, EBITDA, EPS, free cash flow. 公司名、人名、ticker、金融行业术语。";

    internal static async Task<int> RunAsync(string[] args)
    {
        string[] keys = ["--cli", "--model", "--second-model", "--audio", "--output"];
        if (args.Length != 11 || args[0] != "--m3d-vulkan-full-probe" ||
            !keys.All(key => args.Count(value => value == key) == 1) ||
            Enumerable.Range(0, 5).Any(index => !keys.Contains(args[1 + index * 2])))
        {
            Console.Error.WriteLine("Usage: --m3d-vulkan-full-probe --cli <Vulkan whisper-cli.exe> --model <ggml-base.bin> --second-model <ggml-large*.bin> --audio <verified public jfk.wav> --output <new project-local-directory>");
            return 2;
        }
        string Value(string key) => Path.GetFullPath(args[Array.IndexOf(args, key) + 1]);
        string executable = Value("--cli"), baseModel = Value("--model"), largeModel = Value("--second-model"), audio = Value("--audio");
        string output = SyntheticAudioProbe.SafeProjectPath(SyntheticAudioProbe.FindProjectRoot(), Value("--output"));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Vulkan 验收需要新的空证据目录，不覆盖旧结果。");
        Directory.CreateDirectory(output);
        var errors = new List<string>(); var checks = new List<object>(); var runs = new List<object>();
        var immutable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        object? importedEvidence = null, cancellationEvidence = null, versionEvidence = null;
        string? meeting = null; long durationMs = 0;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            Require(OperatingSystem.IsWindows(), "actual_windows_platform");
            Require(Hash(audio) == PublicSampleSha256, "only_fixed_public_jfk_sample_is_accepted");
            Require(Path.GetFileName(baseModel).Equals("ggml-base.bin", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(largeModel).StartsWith("ggml-large", StringComparison.OrdinalIgnoreCase) &&
                Path.GetExtension(largeModel).Equals(".bin", StringComparison.OrdinalIgnoreCase), "existing_multilingual_base_and_large_models");
            foreach (string path in new[] { audio, executable, baseModel, largeModel }) immutable.Add(path, Hash(path));
            Require(immutable[baseModel] != immutable[largeModel], "different_model_bytes");

            var imported = await Task.Run(() => AudioImportService.Import(audio, Path.Combine(output, "recordings"), deadline.Token));
            meeting = imported.MeetingDirectory;
            durationMs = checked((long)Math.Round(imported.Mix.FrameCount * 1000d / imported.Mix.SampleRate));
            Require(imported.OriginalSha256 == PublicSampleSha256 && durationMs == 11000, "production_import_preserves_public_original_and_full_11_second_timeline");
            immutable.Add(imported.OriginalPath, imported.OriginalSha256); immutable.Add(imported.Mix.WavePath, imported.Mix.WaveSha256);
            var prepared = await Task.Run(() => AsrAudioPreparation.Prepare(imported.Mix.WavePath, imported.Mix.WaveSha256,
                Path.Combine(meeting, "transcripts", "inputs"), deadline.Token));
            immutable.Add(prepared.Path, prepared.Sha256);
            importedEvidence = new { imported.MeetingDirectory, imported.OriginalPath, imported.OriginalSha256,
                imported.Mix.WavePath, imported.Mix.WaveSha256, imported.Mix.FrameCount, imported.Mix.SampleRate,
                DurationMs = durationMs, PreparedPath = prepared.Path, PreparedSha256 = prepared.Sha256, prepared.Frames };
            var config = new WhisperConfiguration(executable, baseModel, "en", 2, Path.GetFileNameWithoutExtension(baseModel),
                ModelLicenseAcknowledged: true, Hardware: LocalTranscriptionHardware.Vulkan, GpuDeviceIndex: 0, InitialPrompt: Prompt);
            var request = new WhisperRequest(meeting, Path.GetFileName(meeting), durationMs, prepared.Path, prepared.Sha256, config);
            Require(WhisperTranscriber.Inspect(config).Ready, "local_vulkan_configuration_ready_for_runtime_verification");

            var first = await Run("vulkan_base", request);
            Validate(first, "vulkan_base");
            var firstRevision = first.Revision!;
            var reviewed = TranscriptService.CreateRevision(meeting, firstRevision.RevisionId, firstRevision.Segments,
                "Automated version-retention probe; text unchanged, not a human accuracy review.");
            Preserve(Path.Combine(meeting, "transcripts", "reviewed", reviewed.RevisionId));
            var largeRequest = request with { Configuration = config with { ModelPath = largeModel, ModelName = Path.GetFileNameWithoutExtension(largeModel) } };
            var large = await Run("vulkan_large", largeRequest);
            Validate(large, "vulkan_large");
            var largeRevision = large.Revision!;
            Require(first.AttemptDirectory != large.AttemptDirectory && firstRevision.RevisionId != largeRevision.RevisionId,
                "base_and_large_create_separate_attempts_and_original_versions");

            // Observe only this probe's direct child. The production adapter alone owns stopping it.
            // Recognizing is triggered by the actual main: processing diagnostic, after model setup.
            int beforeCancel = TranscriptService.ListRevisions(meeting).Count;
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                cancellation.CancelAfter(TimeSpan.FromSeconds(90));
                var recognizing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var progress = new ConcurrentQueue<object>();
                var cancelElapsed = Stopwatch.StartNew();
                var observer = new InlineProgress(value =>
                {
                    progress.Enqueue(new { ElapsedMs = cancelElapsed.ElapsedMilliseconds, Stage = value.Stage.ToString(), value.Percent });
                    if (value.Stage == LocalTranscriptionStage.Recognizing) recognizing.TrySetResult();
                });
                Process? child = null;
                Console.WriteLine("M3D VULKAN starting: cancel_real_large_inference");
                var cancelTask = Task.Run(() => WhisperTranscriber.RunAsync(largeRequest, cancellation.Token, observer));
                try
                {
                    while (!cancelTask.IsCompleted && cancelElapsed.Elapsed < TimeSpan.FromSeconds(60))
                    {
                        child = FindOwnedCli(executable);
                        if (child is not null) break;
                        await Task.Delay(10, deadline.Token);
                    }
                    Require(child is not null, "cancel_observed_real_direct_child_with_matching_executable");
                    await Task.WhenAny(recognizing.Task, cancelTask).WaitAsync(TimeSpan.FromSeconds(60), deadline.Token);
                    bool inferenceReached = recognizing.Task.IsCompleted;
                    bool runningAtCancellation = !cancelTask.IsCompleted && !child!.HasExited;
                    long cancelAtMs = cancelElapsed.ElapsedMilliseconds;
                    cancellation.Cancel();
                    var cancelled = await cancelTask.WaitAsync(TimeSpan.FromSeconds(20));
                    bool childExited = await ExitedWithin(child!, TimeSpan.FromSeconds(5));
                    SaveRun("cancel_real_large_inference", cancelled, largeRequest, requireSuccess: false);
                    cancellationEvidence = new { ObservedPid = child!.Id, DirectChild = true, MatchingExecutable = true,
                        InferenceReached = inferenceReached, RunningAtCancellation = runningAtCancellation, CancelAtMs = cancelAtMs,
                        CompletionAfterCancelMs = cancelElapsed.ElapsedMilliseconds - cancelAtMs, ChildExited = childExited,
                        cancelled.Status, cancelled.AttemptDirectory, Progress = progress.ToArray() };
                    Require(inferenceReached && runningAtCancellation, "cancel_reached_actual_large_model_inference_before_completion");
                    Require(cancelled.Status == "Cancelled" && cancelled.Revision is null && childExited,
                        "cancel_finishes_owned_process_and_never_publishes_partial_revision");
                    Require(TranscriptService.ListRevisions(meeting).Count == beforeCancel, "cancel_preserves_previous_version_count");
                }
                finally
                {
                    cancellation.Cancel();
                    // Do not leave the owned inference running on an assertion or timeout path.
                    try { await cancelTask.WaitAsync(TimeSpan.FromSeconds(30)); }
                    finally { child?.Dispose(); }
                }
            }

            var retry = await Run("vulkan_base_retry_after_cancel", request);
            Validate(retry, "vulkan_base_retry_after_cancel");
            var reopened = TranscriptService.ListRevisions(meeting);
            Require(reopened.Count == 4 && reopened.Select(item => item.RevisionId).Distinct().Count() == 4,
                "disk_reopen_keeps_three_originals_and_existing_review");
            Require(new[] { first.AttemptDirectory, large.AttemptDirectory, retry.AttemptDirectory }.Distinct().Count() == 3,
                "retry_creates_its_own_attempt");
            Require(TranscriptService.Load(meeting, firstRevision.RevisionId).Segments.SequenceEqual(firstRevision.Segments) &&
                TranscriptService.Load(meeting, largeRevision.RevisionId).Segments.SequenceEqual(largeRevision.Segments) &&
                TranscriptService.Load(meeting, reviewed.RevisionId).ParentRevisionId == firstRevision.RevisionId,
                "old_models_and_review_reopen_unchanged_after_large_cancellation_and_retry");
            versionEvidence = reopened.Select(item => new { item.RevisionId, item.ParentRevisionId, item.ModelName,
                item.OriginalFile, item.OriginalSha256, item.ImportedAt }).ToArray();
        }
        catch (Exception ex) { errors.Add(ex.ToString()); Console.Error.WriteLine("M3D VULKAN failed: " + ex.Message); }
        finally
        {
            foreach (var pair in immutable)
            {
                try
                {
                    bool passed = Hash(pair.Key) == pair.Value;
                    checks.Add(new { Name = "immutable_sha256", Path = pair.Key, Expected = pair.Value, Passed = passed });
                    if (!passed) errors.Add("Immutable input/output changed: " + pair.Key);
                }
                catch (Exception ex) { errors.Add("Cannot verify immutable file: " + pair.Key + ": " + ex.Message); }
            }
            elapsed.Stop();
            using var os = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            using var report = new FileStream(Path.Combine(output, "report.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(report, new
            {
                SchemaVersion = "m3d-vulkan-full-probe-1", Status = errors.Count == 0 ? "PASS" : "FAIL", StartedAtUtc = started,
                CompletedAtUtc = DateTimeOffset.UtcNow, ElapsedMilliseconds = elapsed.ElapsedMilliseconds,
                Platform = new { Os = Environment.OSVersion.VersionString, Build = os?.GetValue("CurrentBuildNumber"), Ubr = os?.GetValue("UBR"),
                    DisplayVersion = os?.GetValue("DisplayVersion"), Architecture = RuntimeInformation.OSArchitecture.ToString(), Runtime = RuntimeInformation.FrameworkDescription },
                Executable = executable, BaseModel = baseModel, LargeModel = largeModel, PublicSample = audio,
                ExpectedPublicSampleSha256 = PublicSampleSha256, RequestedHardware = "Vulkan", ExpectedBackend = "Vulkan0",
                Language = "en", Threads = 2, InitialPrompt = Prompt, Imported = importedEvidence,
                Runs = runs, Cancellation = cancellationEvidence, Versions = versionEvidence, Checks = checks, Errors = errors,
                ImmutableFiles = immutable.Select(item => new { Path = item.Key, Sha256 = item.Value }).ToArray(),
                CaptureStarted = false, MicrophoneEnabled = false, AudioPlayed = false, Uploaded = false, Downloaded = false,
                Limitations = new[] {
                    "Vulkan0 is engine-reported backend selection, not independent GPU utilization or a portable performance guarantee.",
                    "Only an existing fixed public 11-second English speech sample; not financial, Chinese, mixed-language or long-meeting accuracy acceptance.",
                    "Initial prompt is checked for literal persistence, not proof of accuracy improvement.",
                    "Elapsed time includes input validation, model hashing/loading, inference and saving.",
                    "No source audio, external models, configuration or existing user meetings are modified." }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        Console.WriteLine($"M3D VULKAN {(errors.Count == 0 ? "PASS" : "FAIL")}: {Path.Combine(output, "report.json")}");
        return errors.Count == 0 ? 0 : 1;

        void Require(bool passed, string name)
        {
            checks.Add(new { Name = name, Passed = passed });
            if (!passed) throw new InvalidDataException(name);
        }
        async Task<WhisperRunResult> Run(string role, WhisperRequest current)
        {
            Console.WriteLine("M3D VULKAN starting: " + role);
            var result = await Task.Run(() => WhisperTranscriber.RunAsync(current, deadline.Token));
            SaveRun(role, result, current, requireSuccess: true);
            return result;
        }
        void SaveRun(string role, WhisperRunResult result, WhisperRequest current, bool requireSuccess)
        {
            using var attempt = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.AttemptDirectory, "attempt.json")));
            var data = attempt.RootElement;
            runs.Add(new { Role = role, result.Status, result.ElapsedMilliseconds, result.AttemptDirectory, result.Message,
                RevisionId = result.Revision?.RevisionId, Segments = result.Revision?.Segments, EngineAttempt = data.Clone() });
            Preserve(result.AttemptDirectory);
            if (result.Revision is { } revision) Preserve(Path.GetDirectoryName(Path.Combine(meeting!, revision.OriginalFile))!);
            Require(data.GetProperty("requestedHardware").GetString() == "Vulkan" && data.GetProperty("gpuDeviceIndex").GetInt32() == 0 &&
                data.GetProperty("initialPrompt").GetString() == Prompt && data.GetProperty("modelSha256").GetString() == immutable[current.Configuration.ModelPath] &&
                data.GetProperty("inputWaveSha256").GetString() == current.ExpectedWaveSha256 &&
                data.GetProperty("executableSha256").GetString() == immutable[executable], role + "_exact_configuration_and_hashes_persisted");
            Require(data.GetProperty("actualGpuBackend").GetString() == "Vulkan0" &&
                data.GetProperty("hardwareEvidence").GetString() == "SelectedGpuBackend", role + "_engine_reported_actual_vulkan0");
            if (requireSuccess) Require(result.Status == "Succeeded" && result.Revision is not null, role + "_successful_real_inference");
            Console.WriteLine($"M3D VULKAN {role}: {result.Status}; {result.ElapsedMilliseconds} ms.");
        }
        void Validate(WhisperRunResult result, string role)
        {
            var revision = result.Revision!;
            var segments = revision.Segments;
            Require(segments.Count > 0 && segments.All(item => item.StartMs >= 0 && item.EndMs > item.StartMs && item.EndMs <= durationMs &&
                item.TimestampQuality == "approximate" && item.SpeakerLabel is null) && !revision.IsSynthetic,
                role + "_non_synthetic_approximate_timestamps_within_full_timeline");
            string text = Regex.Replace(string.Join(" ", segments.Select(item => item.Text)).ToLowerInvariant(), "[^a-z]+", " ");
            Require(text.Contains("ask not what your country can do for you", StringComparison.Ordinal) &&
                text.Contains("what you can do for your country", StringComparison.Ordinal), role + "_known_public_phrases");
            foreach (string extension in new[] { ".srt", ".txt", ".json" })
                Require(File.Exists(Path.Combine(result.AttemptDirectory, "whisper-output" + extension)), role + "_retained_raw_output" + extension);
        }
        void Preserve(string directory)
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) immutable.TryAdd(path, Hash(path));
        }
    }

    private sealed class InlineProgress(Action<LocalTranscriptionProgress> report) : IProgress<LocalTranscriptionProgress>
    { public void Report(LocalTranscriptionProgress value) => report(value); }

    private static string Hash(string path)
    { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }

    private static async Task<bool> ExitedWithin(Process process, TimeSpan timeout)
    {
        try { await process.WaitForExitAsync().WaitAsync(timeout); return process.HasExited; }
        catch (TimeoutException) { return false; }
    }

    private static Process? FindOwnedCli(string executable)
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) return null;
            do
            {
                if (entry.ParentProcessId != Environment.ProcessId || !entry.ExeFile.Equals(Path.GetFileName(executable), StringComparison.OrdinalIgnoreCase)) continue;
                Process? candidate = null;
                try
                {
                    candidate = Process.GetProcessById(checked((int)entry.ProcessId));
                    if (!candidate.HasExited && string.Equals(candidate.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return candidate;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                candidate?.Dispose();
            } while (Process32Next(snapshot, ref entry));
            return null;
        }
        finally { CloseHandle(snapshot); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
