using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

/// <summary>Opt-in real local model probe, restricted to the previously verified public upstream JFK sample.</summary>
internal static class M3dRealWhisperProbe
{
    private const string PublicSampleSha256 = "59dfb9a4acb36fe2a2affc14bacbee2920ff435cb13cc314a08c13f66ba7860e";
    private const string FinancialPrompt = "Financial analyst conference call. Company: ACME; ticker: ACME; analyst: Jane Smith; EBITDA, EPS, free cash flow, net interest margin. 公司名、人名、ticker、金融行业术语。";

    internal static async Task<int> RunAsync(string[] args)
    {
        string[] keys = ["--cli", "--model", "--second-model", "--audio", "--output"];
        if (args.Length != 11 || args[0] != "--m3d-real-whisper-probe" ||
            !keys.All(key => args.Count(value => value == key) == 1) ||
            Enumerable.Range(0, 5).Any(index => !keys.Contains(args[1 + index * 2])))
        {
            Console.Error.WriteLine("Usage: --m3d-real-whisper-probe --cli <existing whisper-cli.exe> --model <existing base model> --second-model <existing large model> --audio <verified public jfk.wav> --output <new project-local-directory>");
            return 2;
        }
        string Value(string key) => Path.GetFullPath(args[Array.IndexOf(args, key) + 1]);
        string executable = Value("--cli"), firstModel = Value("--model"), secondModel = Value("--second-model"), audio = Value("--audio");
        string output = SyntheticAudioProbe.SafeProjectPath(SyntheticAudioProbe.FindProjectRoot(), Value("--output"));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("使用新的空证据目录，不能覆盖已有验收结果。");
        Directory.CreateDirectory(output);
        string reportPath = Path.Combine(output, "report.json");
        var checks = new List<object>(); var errors = new List<string>(); var runs = new List<object>();
        var immutable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var exported = new List<object>();
        string? meeting = null; long durationMs = 0;
        object? importedSummary = null, versionSummary = null;
        string vulkanOutcome = "NOT_RUN";
        int threads = 2;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var total = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("真实本地识别探针仅运行于 Windows。");
            string sampleHash = Hash(audio);
            if (sampleHash != PublicSampleSha256)
                throw new InvalidDataException("此显式探针只接受已核验的官方公开 JFK 样本（docs/47），不读取会议录音进行识别。");
            if (!string.Equals(Path.GetFileName(firstModel), "ggml-base.bin", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(secondModel).StartsWith("ggml-large", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(secondModel), ".bin", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请选择现有 base 和 large 系列模型；探针不下载或替换模型。");
            immutable.Add(audio, sampleHash); immutable.Add(executable, Hash(executable));
            immutable.Add(firstModel, Hash(firstModel)); immutable.Add(secondModel, Hash(secondModel));
            Check(immutable[firstModel] != immutable[secondModel], "two_distinct_existing_models");

            var imported = await Task.Run(() => AudioImportService.Import(audio, Path.Combine(output, "recordings"), deadline.Token));
            meeting = imported.MeetingDirectory;
            durationMs = checked((long)Math.Round(imported.Mix.FrameCount * 1000d / imported.Mix.SampleRate));
            immutable.Add(imported.OriginalPath, imported.OriginalSha256); immutable.Add(imported.Mix.WavePath, imported.Mix.WaveSha256);
            Check(imported.OriginalSha256 == PublicSampleSha256 && durationMs == 11000, "public_sample_import_exact_bytes_and_full_11_second_timeline");
            var prepared = await Task.Run(() => AsrAudioPreparation.Prepare(imported.Mix.WavePath, imported.Mix.WaveSha256,
                Path.Combine(meeting, "transcripts", "inputs"), deadline.Token));
            immutable.Add(prepared.Path, prepared.Sha256);
            importedSummary = new { Directory = meeting, imported.OriginalPath, imported.OriginalSha256,
                FullMix = imported.Mix.WavePath, FullMixSha256 = imported.Mix.WaveSha256, imported.Mix.FrameCount,
                imported.Mix.SampleRate, DurationMs = durationMs, AsrInput = prepared.Path, AsrInputSha256 = prepared.Sha256 };
            var configuration = new WhisperConfiguration(executable, firstModel, "en", threads,
                ModelName: Path.GetFileNameWithoutExtension(firstModel), ModelLicenseAcknowledged: true,
                Hardware: LocalTranscriptionHardware.Cpu, InitialPrompt: FinancialPrompt);
            if (!WhisperTranscriber.Inspect(configuration).Ready) throw new InvalidDataException(WhisperTranscriber.Inspect(configuration).Message);
            var request = new WhisperRequest(meeting, Path.GetFileName(meeting), durationMs, prepared.Path, prepared.Sha256, configuration);

            var first = await Run("cpu_base_financial_prompt", request);
            ValidateTranscript(first, "cpu_base");
            TranscriptRevision? reviewed = null;
            if (first.Revision is { } original)
            {
                // An unchanged-text child exercises retention without pretending that
                // this automated probe performed a human accuracy correction.
                reviewed = TranscriptService.CreateRevision(meeting, original.RevisionId, original.Segments,
                    "Automated immutable-version lifecycle probe; text unchanged, not a human accuracy correction.");
                PreserveFiles(Path.Combine(meeting, "transcripts", "reviewed", reviewed.RevisionId));
            }
            var second = await Run("cpu_second_model_financial_prompt", request with { Configuration = configuration with
                { ModelPath = secondModel, ModelName = Path.GetFileNameWithoutExtension(secondModel) } });
            ValidateTranscript(second, "cpu_second_model");
            Check(first.AttemptDirectory != second.AttemptDirectory && first.Revision?.RevisionId != second.Revision?.RevisionId,
                "different_model_runs_create_independent_attempts_and_revisions");
            var automatic = await Run("auto_base_financial_prompt", request with { Configuration = configuration with { Hardware = LocalTranscriptionHardware.Auto } });
            ValidateTranscript(automatic, "auto_base");

            int countBeforeVulkan = TranscriptService.ListRevisions(meeting).Count;
            var vulkan = await Run("explicit_vulkan_base", request with { Configuration = configuration with { Hardware = LocalTranscriptionHardware.Vulkan } });
            int countAfterVulkan = TranscriptService.ListRevisions(meeting).Count;
            if (vulkan.Status == "Failed")
            {
                bool rejected = vulkan.Revision is null && vulkan.Message.Contains("Vulkan", StringComparison.Ordinal) && countBeforeVulkan == countAfterVulkan;
                Check(rejected, "unavailable_vulkan_explicit_failure_without_new_successful_version");
                vulkanOutcome = rejected ? "PASS_EXPLICIT_UNAVAILABLE_BACKEND_REJECTION" : "FAIL_UNEXPECTED_VULKAN_FAILURE";
            }
            else if (vulkan.Status == "Succeeded")
            {
                // A future machine may have an actual Vulkan CLI. Validate its reported
                // selection; do not hard-code today's dependency set into this probe.
                using var attempt = JsonDocument.Parse(File.ReadAllText(Path.Combine(vulkan.AttemptDirectory, "attempt.json")));
                string? actual = attempt.RootElement.GetProperty("actualGpuBackend").GetString();
                bool selected = actual?.StartsWith("Vulkan", StringComparison.Ordinal) == true && countAfterVulkan == countBeforeVulkan + 1;
                Check(selected, "available_vulkan_requires_actual_selected_backend_evidence");
                ValidateTranscript(vulkan, "actual_vulkan");
                vulkanOutcome = selected ? "PASS_ACTUAL_SELECTED_VULKAN_BACKEND" : "FAIL_MISSING_VULKAN_EVIDENCE";
            }
            else { Check(false, "explicit_vulkan_has_terminal_success_or_clear_failure"); vulkanOutcome = "FAIL_" + vulkan.Status; }

            var reopened = TranscriptService.ListRevisions(meeting);
            int expected = new[] { first, second, automatic, vulkan }.Count(run => run.Status == "Succeeded") + (reviewed is null ? 0 : 1);
            Check(reopened.Count == expected && reopened.Select(revision => revision.RevisionId).Distinct().Count() == expected,
                "disk_reopen_preserves_every_successful_model_version_and_review");
            if (first.Revision is { } old)
            {
                var reloaded = TranscriptService.Load(meeting, old.RevisionId);
                Check(reloaded.ModelName == old.ModelName && reloaded.Segments.SequenceEqual(old.Segments), "first_model_original_survives_new_model_auto_and_vulkan_attempts");
                if (reviewed is not null) Check(TranscriptService.Load(meeting, reviewed.RevisionId).ParentRevisionId == old.RevisionId,
                    "earlier_review_parent_survives_new_model_runs");
                foreach (string extension in new[] { ".txt", ".srt", ".vtt" })
                {
                    string destination = Path.Combine(output, "selected-old-base-version" + extension);
                    if (extension == ".txt") TranscriptSubtitleExporter.ExportText(meeting, old.RevisionId, destination);
                    else TranscriptSubtitleExporter.Export(meeting, old.RevisionId, destination);
                    bool matches = old.Segments.All(segment => File.ReadAllText(destination).Contains(segment.Text.Trim(), StringComparison.Ordinal));
                    Check(matches, "old_base_version_selected_export_" + extension[1..]);
                    exported.Add(new { Format = extension, Path = destination, Sha256 = Hash(destination), SelectedRevisionId = old.RevisionId });
                }
            }
            versionSummary = reopened.Select(revision => new { revision.RevisionId, revision.ParentRevisionId, revision.ModelName,
                revision.ImportedAt, revision.OriginalFile, revision.OriginalSha256 }).ToArray();
        }
        catch (Exception ex) { errors.Add(ex.ToString()); Console.Error.WriteLine("M3D REAL ASR probe: " + ex.Message); }
        finally
        {
            foreach (var pair in immutable)
            {
                try { Check(Hash(pair.Key) == pair.Value, "unchanged_sha256:" + pair.Key); }
                catch (Exception ex) { errors.Add("Cannot recheck immutable file: " + pair.Key + ": " + ex.Message); }
            }
            total.Stop();
            using var os = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            using var report = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(report, new
            {
                SchemaVersion = "m3d-real-whisper-probe-1", Status = errors.Count == 0 ? "PASS" : "FAIL",
                StartedAtUtc = startedAt, CompletedAtUtc = DateTimeOffset.UtcNow, TotalElapsedMilliseconds = total.ElapsedMilliseconds,
                Platform = new { Os = Environment.OSVersion.VersionString, Build = os?.GetValue("CurrentBuildNumber"), Ubr = os?.GetValue("UBR"),
                    DisplayVersion = os?.GetValue("DisplayVersion"), Architecture = RuntimeInformation.OSArchitecture.ToString(), Runtime = RuntimeInformation.FrameworkDescription },
                Executable = executable, CliFileVersion = FileVersionInfo.GetVersionInfo(executable).FileVersion,
                LocalBackendFiles = Directory.EnumerateFiles(Path.GetDirectoryName(executable)!, "ggml*.dll").Select(Path.GetFileName).Order().ToArray(),
                FirstModel = firstModel, SecondModel = secondModel, Audio = audio, ExpectedPublicSampleSha256 = PublicSampleSha256,
                Language = "en", Threads = threads, InitialPrompt = FinancialPrompt, Imported = importedSummary,
                Runs = runs, VulkanOutcome = vulkanOutcome, Versions = versionSummary, Exports = exported,
                ImmutableFiles = immutable.Select(pair => new { Path = pair.Key, Sha256 = pair.Value }).ToArray(), Checks = checks, Errors = errors,
                CaptureStarted = false, MicrophoneEnabled = false, AudioPlayed = false, Uploaded = false, Downloaded = false,
                Limitations = new[] {
                    "Only the fixed public 11-second English JFK sample; not a financial call, bilingual meeting or long meeting quality assessment.",
                    "The financial initial prompt tests literal passing and persistence. It does not establish improved recognition accuracy.",
                    "Elapsed times include model hashing/loading and result saving and must not be extrapolated to other recordings or hardware.",
                    "An unavailable Vulkan rejection is not actual AMD/Vulkan inference or acceleration acceptance.",
                    "Reported GPU selection is engine stderr evidence, not independent GPU-utilization profiling.",
                    "No models, application settings, external files, original recordings or existing transcript versions are changed." }
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        Console.WriteLine($"M3D REAL ASR {(errors.Count == 0 ? "PASS" : "FAIL")}: {reportPath}");
        return errors.Count == 0 ? 0 : 1;

        void Check(bool passed, string name)
        {
            checks.Add(new { Name = name, Passed = passed });
            if (!passed) errors.Add(name);
        }
        async Task<WhisperRunResult> Run(string role, WhisperRequest request)
        {
            Console.WriteLine("M3D REAL ASR starting: " + role);
            var result = await Task.Run(() => WhisperTranscriber.RunAsync(request, deadline.Token));
            string attemptFile = Path.Combine(result.AttemptDirectory, "attempt.json");
            using var attempt = JsonDocument.Parse(File.ReadAllText(attemptFile));
            var data = attempt.RootElement;
            Check(data.GetProperty("initialPrompt").GetString() == FinancialPrompt &&
                data.GetProperty("requestedHardware").GetString() == request.Configuration.Hardware.ToString(), role + "_persisted_prompt_and_requested_hardware");
            runs.Add(new { Role = role, Model = request.Configuration.ModelName, RequestedHardware = request.Configuration.Hardware.ToString(),
                result.Status, result.ElapsedMilliseconds, result.AttemptDirectory, result.Message, RevisionId = result.Revision?.RevisionId,
                Segments = result.Revision?.Segments, EngineAttempt = data.Clone() });
            PreserveFiles(result.AttemptDirectory);
            if (result.Revision is { } revision) PreserveFiles(Path.GetDirectoryName(Path.Combine(meeting!, revision.OriginalFile))!);
            Console.WriteLine($"M3D REAL ASR {role}: {result.Status}; {result.ElapsedMilliseconds} ms; {result.Revision?.Segments.Count ?? 0} segments.");
            return result;
        }
        void ValidateTranscript(WhisperRunResult result, string role)
        {
            Check(result.Status == "Succeeded" && result.Revision is not null, role + "_successful_real_transcription");
            if (result.Revision is not { } revision) return;
            Check(revision.Segments.Count > 0 && revision.Segments.All(segment => segment.StartMs >= 0 && segment.EndMs > segment.StartMs &&
                segment.EndMs <= durationMs && segment.TimestampQuality == "approximate" && segment.SpeakerLabel is null), role + "_valid_complete_timeline_approximate_subtitles");
            string normalized = Regex.Replace(string.Join(" ", revision.Segments.Select(segment => segment.Text)).ToLowerInvariant(), "[^a-z]+", " ");
            Check(normalized.Contains("ask not what your country can do for you", StringComparison.Ordinal) &&
                normalized.Contains("what you can do for your country", StringComparison.Ordinal), role + "_known_jfk_phrases_present");
        }
        void PreserveFiles(string directory)
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) immutable.TryAdd(path, Hash(path));
        }
    }

    private static string Hash(string path)
    { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
}
