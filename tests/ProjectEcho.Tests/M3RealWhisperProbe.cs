using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

/// <summary>Explicit opt-in, local-file-only real whisper.cpp probe for the public upstream JFK sample.</summary>
internal static class M3RealWhisperProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is not (4 or 6) || args[0] != "--m3-real-whisper-probe" ||
            (args.Length == 6 && args[4] != "--output"))
        {
            Console.Error.WriteLine("Usage: --m3-real-whisper-probe <whisper-cli.exe> <ggml-base.bin> <jfk.wav> [--output <project-local-directory>]");
            return 2;
        }
        string root = SyntheticAudioProbe.FindProjectRoot();
        string output = SyntheticAudioProbe.SafeProjectPath(root, args.Length == 6 ? args[5] : Path.Combine(root, ".local", "M3RealWhisper",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(output);
        // A unique evidence directory prevents later runs from replacing an earlier failure or success.
        string reportPath = Path.Combine(output, "report.json");
        if (File.Exists(reportPath)) throw new IOException("输出目录已有报告，请为本次运行选择新目录。");
        var errors = new List<string>();
        var checks = new List<object>();
        var runs = new List<object>();
        var subtitleRoundTrips = new List<object>();
        var immutable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string executable = Path.GetFullPath(args[1]), model = Path.GetFullPath(args[2]), sample = Path.GetFullPath(args[3]);
        string? meeting = null, modelHash = null, executableHash = null;
        long durationMs = 0;
        object? importedSummary = null, cancellationEvidence = null;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("此测试只在实际 Windows 上运行。");
            immutable.Add(sample, Hash(sample));
            executableHash = Hash(executable); modelHash = Hash(model);
            immutable.Add(executable, executableHash); immutable.Add(model, modelHash);
            var config = new WhisperConfiguration(executable, model, "en", threads, "base (multilingual)", modelHash, true);
            Require(WhisperTranscriber.Inspect(config).Ready, "real_cli_and_model_ready");

            // Use the exact production import path; no fabricated capture manifest, device clocks, or microphone track.
            var imported = AudioImportService.Import(sample, Path.Combine(output, "recordings"));
            meeting = imported.MeetingDirectory;
            durationMs = checked((long)Math.Round(imported.Mix.FrameCount * 1000d / imported.Mix.SampleRate));
            string meetingId = Path.GetFileName(meeting);
            immutable.Add(imported.OriginalPath, imported.OriginalSha256);
            immutable.Add(imported.Mix.WavePath, imported.Mix.WaveSha256);
            Require(Hash(imported.OriginalPath) == immutable[sample], "imported_original_is_identical_to_public_input");
            var reopened = AudioImportService.Load(meeting);
            Require(reopened.Mix.WaveSha256 == imported.Mix.WaveSha256 && reopened.Mix.FrameCount == imported.Mix.FrameCount,
                "imported_meeting_reopens_with_same_full_timeline");
            var prepared = AsrAudioPreparation.Prepare(imported.Mix.WavePath, imported.Mix.WaveSha256,
                Path.Combine(meeting, "transcripts", "inputs"));
            immutable.Add(prepared.Path, prepared.Sha256);
            Require(Math.Abs(prepared.Frames * 1000d / 16000 - durationMs) <= 1, "asr_preparation_retains_complete_elapsed_timeline");
            importedSummary = new
            {
                meetingDirectory = meeting, originalPath = imported.OriginalPath, originalSha256 = imported.OriginalSha256,
                fullMix = imported.Mix.WavePath, fullMixSha256 = imported.Mix.WaveSha256,
                imported.Mix.FrameCount, imported.Mix.SampleRate, durationMs,
                asrInput = prepared.Path, asrInputSha256 = prepared.Sha256, asrInputFrames = prepared.Frames,
                audioSource = "Existing public speech file; no capture or microphone activation"
            };
            var request = new WhisperRequest(meeting, meetingId, durationMs, prepared.Path, prepared.Sha256, config);

            Console.WriteLine("REAL ASR: production import and 16 kHz preparation completed; starting public JFK sample.");
            var first = await Task.Run(() => WhisperTranscriber.RunAsync(request));
            AddRun("first", first);
            ValidateTranscript(first, "first");
            PreserveTranscript(first);

            // Cancellation is issued only after observing the real CLI as a direct child of this probe.
            // Nothing enumerated here is killed by the probe: the production transcriber owns termination.
            using (var cancellation = new CancellationTokenSource())
            {
                var watch = Stopwatch.StartNew();
                var cancelTask = Task.Run(() => WhisperTranscriber.RunAsync(request with
                    { Configuration = config with { Threads = 1 } }, cancellation.Token));
                Process? observed = null;
                try
                {
                    while (!cancelTask.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(30))
                    {
                        observed = FindOwnedCli(executable);
                        if (observed is not null) break;
                        await Task.Delay(20);
                    }
                    int? observedPid = observed?.Id;
                    cancellation.Cancel();
                    var cancelled = await cancelTask.WaitAsync(TimeSpan.FromSeconds(20));
                    AddRun("cancel_after_real_child_observed", cancelled);
                    bool childExited = observed is not null && await ExitedWithin(observed, TimeSpan.FromSeconds(5));
                    cancellationEvidence = new
                    {
                        observedPid, observedDirectChild = observed is not null, childExited,
                        elapsedMilliseconds = watch.ElapsedMilliseconds, status = cancelled.Status,
                        cancelled.AttemptDirectory
                    };
                    Require(observed is not null, "cancel_observed_actual_whisper_child");
                    Require(cancelled.Status == "Cancelled" && childExited, "cancel_returns_only_after_actual_child_exit");
                    Require(cancelled.Revision is null, "cancel_does_not_publish_a_partial_transcript");
                }
                finally
                {
                    cancellation.Cancel();
                    observed?.Dispose();
                    // Finish cleanup before leaving the probe, including exceptional assertion paths.
                    await cancelTask.WaitAsync(TimeSpan.FromSeconds(30));
                }
            }

            Console.WriteLine("REAL ASR: cancellation completed; rerunning the same prepared input.");
            var rerun = await Task.Run(() => WhisperTranscriber.RunAsync(request));
            AddRun("rerun_after_cancel", rerun);
            ValidateTranscript(rerun, "rerun");
            PreserveTranscript(rerun);
            Require(first.AttemptDirectory != rerun.AttemptDirectory && first.Revision!.RevisionId != rerun.Revision!.RevisionId,
                "same_input_rerun_creates_independent_attempt_and_revision");
            var revisions = TranscriptService.ListRevisions(meeting);
            Require(revisions.Count == 2 && revisions.All(r => r.ParentRevisionId is null), "two_successes_preserved_as_independent_original_versions");
            Require(TranscriptService.Search(meeting, "country").Select(hit => hit.RevisionId).Distinct().Count() == 2,
                "real_transcripts_are_searchable_after_reload");
            foreach (string extension in new[] { ".srt", ".vtt" })
            {
                string exported = TranscriptSubtitleExporter.Export(meeting, rerun.Revision!.RevisionId,
                    Path.Combine(output, "jfk-real-transcript" + extension));
                immutable.Add(exported, Hash(exported));
                var reimported = TranscriptService.Import(new(meeting, meetingId, durationMs, exported,
                    "Real ASR subtitle export round trip"));
                var expected = rerun.Revision.Segments.OrderBy(s => s.StartMs).Select(s => (s.StartMs, s.EndMs, s.Text)).ToArray();
                var actual = reimported.Segments.Select(s => (s.StartMs, s.EndMs, s.Text)).ToArray();
                Require(expected.SequenceEqual(actual), "real_" + extension[1..] + "_export_reimport_preserves_all_text_and_timestamps");
                subtitleRoundTrips.Add(new { format = extension[1..], path = exported, sha256 = immutable[exported],
                    originalRevisionId = rerun.Revision.RevisionId, reimportedRevisionId = reimported.RevisionId,
                    segmentCount = actual.Length, allTextAndTimestampsIdentical = true });
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.ToString());
            Console.Error.WriteLine("REAL ASR probe failed: " + ex.Message);
        }
        finally
        {
            foreach (var pair in immutable)
            {
                try
                {
                    bool unchanged = Hash(pair.Key) == pair.Value;
                    checks.Add(new { name = "immutable_sha256", path = pair.Key, expectedSha256 = pair.Value, passed = unchanged });
                    if (!unchanged) errors.Add("Original or prepared input changed: " + pair.Key);
                }
                catch (Exception ex) { errors.Add("Cannot verify original: " + pair.Key + ": " + ex.Message); }
            }
            using var version = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var report = new
            {
                schemaVersion = "m3-real-whisper-probe-1", status = errors.Count == 0 ? "PASS" : "FAIL", started,
                completedAt = DateTimeOffset.UtcNow,
                platform = new { os = Environment.OSVersion.VersionString, build = version?.GetValue("CurrentBuildNumber"),
                    ubr = version?.GetValue("UBR"), displayVersion = version?.GetValue("DisplayVersion"),
                    architecture = RuntimeInformation.OSArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription },
                executable, executableSha256 = executableHash, model, modelSha256 = modelHash,
                language = "en", threads, cpuOnly = true, imported = importedSummary, runs, cancellationEvidence, subtitleRoundTrips, checks, errors,
                limitations = new[]
                {
                    "One short public English JFK speech sample only; not representative meeting accuracy or mixed-language acceptance.",
                    "Phrase checks establish recognizable sample content; no word-error-rate or speaker-attribution claim.",
                    "ASR subtitle boundaries remain approximate and are not evidence of word-level alignment.",
                    "Real-time factor includes production hashing, model loading, CLI execution and importing; not a general benchmark.",
                    "This probe does not play audio, capture audio, enable microphones, use a network or test WPF seeking."
                }
            };
            using var file = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, report, new JsonSerializerOptions { WriteIndented = true });
        }
        Console.WriteLine($"REAL ASR {(errors.Count == 0 ? "PASS" : "FAIL")}: {reportPath}");
        return errors.Count == 0 ? 0 : 1;

        void Require(bool value, string name)
        {
            checks.Add(new { name, passed = value });
            if (!value) throw new InvalidDataException(name);
        }
        void AddRun(string role, WhisperRunResult result)
        {
            runs.Add(new
            {
                role, result.Status, result.ElapsedMilliseconds,
                realTimeFactor = durationMs > 0 ? (double)result.ElapsedMilliseconds / durationMs : (double?)null,
                result.AttemptDirectory, result.Message, revisionId = result.Revision?.RevisionId, segments = result.Revision?.Segments
            });
            Console.WriteLine($"REAL ASR {role}: {result.Status}; {result.ElapsedMilliseconds} ms; {result.Revision?.Segments.Count ?? 0} segments.");
        }
        void ValidateTranscript(WhisperRunResult result, string role)
        {
            Require(result.Status == "Succeeded" && result.Revision is not null, role + "_successful_real_transcription");
            var segments = result.Revision!.Segments;
            Require(segments.Count > 0 && segments.All(s => s.StartMs >= 0 && s.EndMs > s.StartMs &&
                s.EndMs <= durationMs && s.TimestampQuality == "approximate"), role + "_subtitle_ranges_inside_complete_timeline");
            string text = Regex.Replace(string.Join(" ", segments.Select(s => s.Text)).ToLowerInvariant(), "[^a-z]+", " ").Trim();
            Require(text.Contains("ask not what your country can do for you", StringComparison.Ordinal) &&
                text.Contains("what you can do for your country", StringComparison.Ordinal), role + "_known_jfk_phrases_present");
            Require(segments.All(s => s.SpeakerLabel is null) && !result.Revision.IsSynthetic,
                role + "_no_invented_speaker_or_synthetic_model_label");
            foreach (string suffix in new[] { ".srt", ".txt", ".json" })
                Require(File.Exists(Path.Combine(result.AttemptDirectory, "whisper-output" + suffix)), role + "_raw_output_retained" + suffix);
            foreach (string name in new[] { "stdout.txt", "stderr.txt", "attempt.json" })
                Require(File.Exists(Path.Combine(result.AttemptDirectory, name)), role + "_diagnostic_retained_" + name);
        }
        void PreserveTranscript(WhisperRunResult result)
        {
            string original = Path.Combine(meeting!, result.Revision!.OriginalFile.Replace('/', Path.DirectorySeparatorChar));
            immutable.Add(original, result.Revision.OriginalSha256);
            string revision = Path.Combine(Path.GetDirectoryName(original)!, "revision.json");
            immutable.Add(revision, Hash(revision));
        }
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async Task<bool> ExitedWithin(Process process, TimeSpan timeout)
    {
        try { await process.WaitForExitAsync().WaitAsync(timeout); return process.HasExited; }
        catch (TimeoutException) { return false; }
    }

    private static Process? FindOwnedCli(string executable)
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS, read-only parent identity.
        if (snapshot == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) return null;
            do
            {
                if (entry.ParentProcessId != Environment.ProcessId || !string.Equals(entry.ExeFile,
                    Path.GetFileName(executable), StringComparison.OrdinalIgnoreCase)) continue;
                Process? candidate = null;
                try
                {
                    candidate = Process.GetProcessById(checked((int)entry.ProcessId));
                    if (!candidate.HasExited && string.Equals(candidate.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                        return candidate;
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
