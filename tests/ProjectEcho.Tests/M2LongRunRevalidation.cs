using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Export;

internal static partial class M2LongRunProbe
{
    internal static Task<int> RunRevalidationAsync(string[] args) => Task.Run(() => RevalidateEndedLongRun(args));

    /// <summary>Offline-only entry point. It has no audio-device, producer-launch or CaptureHost-start operation.</summary>
    private static int RevalidateEndedLongRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--m2-revalidate-long-run")
        { Console.Error.WriteLine("Usage: --m2-revalidate-long-run <ended remote-only long probe-report.json>"); return 2; }
        string root = SyntheticAudioProbe.FindProjectRoot();
        string parent = SyntheticAudioProbe.SafeProjectPath(root, ".local/M2LongRunIntegration");
        M2FileSafety.NoReparse(parent); Directory.CreateDirectory(parent);
        string output = Path.Combine(parent, "revalidation-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        string phase = "READINESS", status = "NOT_READY", originalDirectory = "", priorStatus = "", reportHashBefore = "";
        string? priorPath = null;
        var errors = new List<string>(); var warnings = new List<string>();
        var before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var after = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sourceLocks = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        var assemblies = new List<object>(); var originals = new List<MarkerComparison>(); var rendered = new List<MarkerDetection>();
        var parts = new List<CompressedCheck>();
        FileStream? probeLock = null, reportLock = null;
        FullMixResult? mix = null; ExportResult? export = null; CompressedCheck? full = null;
        M1RecoveryResult? recovered = null;
        bool oldHashesMatched = false, originalsUnchanged = false, reportUnchanged = false, workingCopyVerified = false;
        long wavFramesReadToEof = 0;
        DateTimeOffset begun = DateTimeOffset.UtcNow;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(90));
        try
        {
            priorPath = ValidateLongRunReportPath(root, parent, args[1]);
            string run = Path.GetDirectoryName(priorPath)!;
            originalDirectory = Path.Combine(run, "recording");
            M2FileSafety.NoReparse(originalDirectory);
            RefuseAnyActiveCaptureHost();
            string lockPath = SyntheticAudioProbe.SafeProjectPath(root, ".local/recording-probe.lock");
            M2FileSafety.NoReparse(lockPath);
            try { probeLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new RevalidationNotReadyException("recording_probe_or_original_offline_processing_still_active"); }
            RefuseAnyActiveCaptureHost();
            if (!File.Exists(priorPath)) throw new RevalidationNotReadyException("probe_report_not_written_yet");
            if (!File.Exists(Path.Combine(run, "SYNTHETIC_REMOTE_ONLY_MIC_OFF")))
                throw new RevalidationRejectedException("missing_owned_remote_only_run_marker");
            reportLock = M2FileSafety.LockedRead(priorPath);
            if (reportLock.Length > 16L * 1024 * 1024) throw new RevalidationRejectedException("probe_report_exceeds_size_limit");
            reportHashBefore = M2FileSafety.Hash(reportLock, deadline.Token);
            using var document = JsonDocument.Parse(reportLock); RejectDuplicateReportProperties(document.RootElement);
            var prior = document.RootElement;
            priorStatus = prior.GetProperty("Status").GetString() ?? "";
            ValidateEndedRemoteLongProof(prior, originalDirectory);
            if (!Directory.Exists(originalDirectory) || !File.Exists(Path.Combine(originalDirectory, "meeting.json")))
                throw new RevalidationNotReadyException("final_recording_manifest_not_present");
            using (var earlySelection = M2FileSafety.LockedRead(M2FileSafety.Inside(originalDirectory, "selection.json")))
            {
                if (earlySelection.Length > 65536) throw new RevalidationRejectedException("selection_metadata_exceeds_limit");
                using var selection = JsonDocument.Parse(earlySelection);
                RefuseSameLiveProducer(ValidateRevalidationSelection(selection.RootElement, prior));
            }

            phase = "SOURCE_VERIFICATION";
            Progress("VERIFYING_CLOSED_ORIGINALS");
            var expected = ReadOriginalHashManifest(prior.GetProperty("Storage").GetProperty("OriginalHashes"));
            string[] discovered = EnumerateProbeOriginals(originalDirectory).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!discovered.SequenceEqual(expected.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Original file set differs from the old closed-recording hash list; derived folders are excluded.");
            long originalBytes = 0;
            foreach (var entry in expected)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string path = M2FileSafety.Inside(originalDirectory, entry.Key);
                var held = M2FileSafety.LockedRead(path); sourceLocks.Add(entry.Key, held);
                originalBytes = checked(originalBytes + held.Length);
                string actual = M2FileSafety.Hash(held, deadline.Token); before.Add(entry.Key, actual);
                if (!actual.Equals(entry.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Original SHA-256 differs from old report: " + entry.Key);
            }
            oldHashesMatched = true;
            using (var selection = JsonDocument.Parse(sourceLocks["selection.json"]))
            {
                RefuseSameLiveProducer(ValidateRevalidationSelection(selection.RootElement, prior));
            }
            recovered = M1Recovery.Read(originalDirectory, deadline.Token);
            if (!recovered.HasCheckpoint || !recovered.IsCleanEnd || recovered.Issues.Count != 0)
                throw new InvalidDataException("Original checkpoint/storage is not clean: " + string.Join(" | ", recovered.Issues));
            if (recovered.Capture.Tracks.Count == 0 || recovered.Capture.Tracks.Any(t => t.SourceId != "remote") ||
                Directory.Exists(Path.Combine(originalDirectory, "sources", "mic"))) throw new RevalidationRejectedException("unexpected_microphone_or_non_remote_source");
            if (Math.Abs(recovered.Capture.Duration100ns / 10_000_000d - LongSeconds) > 15)
                throw new InvalidDataException("Original elapsed duration is not the completed two-hour programme.");
            CheckContinuity(recovered.Capture, errors);

            var oldMarkers = prior.GetProperty("Markers").Deserialize<MarkerComparison[]>() ?? [];
            if (!oldMarkers.Select(m => m.ProgrammeSecond).SequenceEqual(LongMarkers) ||
                oldMarkers.Any(m => !m.Remote.Found || !double.IsFinite(m.Remote.StartSeconds)))
                throw new RevalidationRejectedException("old_report_does_not_contain_all_known_programme_markers");
            foreach (var marker in oldMarkers)
            {
                deadline.Token.ThrowIfCancellationRequested(); double begin = Math.Max(0, marker.Remote.StartSeconds - 1);
                var detected = Detect(ReadWindow(recovered.Capture, "remote", begin, 4), begin);
                originals.Add(new(marker.ProgrammeSecond, detected, new(false, 0, 0, 0, []), null));
                if (!detected.Found || Math.Abs(detected.StartSeconds - marker.Remote.StartSeconds) > .02)
                    errors.Add("original_marker_differs_from_old_report_over_20ms:" + marker.ProgrammeSecond);
            }
            if (errors.Count != 0) throw new InvalidDataException("Original continuity/marker checks failed; no new render was attempted.");

            assemblies.Add(AssemblyFingerprint(typeof(M2ExportService).Assembly, root));
            assemblies.Add(AssemblyFingerprint(typeof(RecordingSession).Assembly, root));
            assemblies.Add(AssemblyFingerprint(typeof(AudioClock).Assembly, root));
            assemblies.Add(AssemblyFingerprint(typeof(M2LongRunProbe).Assembly, root));
            long frames = checked((long)(((Int128)recovered.Capture.Duration100ns * Rate + 9_999_999) / 10_000_000));
            M2FileSafety.RequireDiskSpace(output, checked(originalBytes + frames * 4 + 256L * 1024 * 1024));
            string workingRecording = M2FileSafety.Inside(output, "recording"); Directory.CreateDirectory(workingRecording);
            phase = "VERIFIED_COPY"; Progress("COPYING_VERIFIED_ORIGINALS");
            foreach (var item in sourceLocks)
            {
                deadline.Token.ThrowIfCancellationRequested(); string path = M2FileSafety.Inside(workingRecording, item.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); item.Value.Position = 0;
                using (var copy = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[65536]; int count;
                    while ((count = item.Value.Read(buffer)) != 0) { deadline.Token.ThrowIfCancellationRequested(); copy.Write(buffer, 0, count); }
                    copy.Flush(true);
                }
                if (M2FileSafety.Hash(path, deadline.Token) != before[item.Key]) throw new InvalidDataException("Verified working copy SHA-256 differs: " + item.Key);
            }
            workingCopyVerified = true;

            phase = "LATEST_RENDER"; Progress("RENDERING_WITH_CURRENT_ASSEMBLIES");
            mix = M2ExportService.RenderMeeting(workingRecording, deadline.Token);
            if (!mix.SourceStorageComplete || mix.WaveTruePeak is null || mix.CompressedPath is null ||
                mix.WaveTruePeak.TruePeakLinear > Math.Pow(10, -1d / 20))
                errors.Add("latest_full_mix_missing_storage_truepeak_or_M4A_verification");
            if (mix.FrameCount != frames || mix.SampleRate != Rate) errors.Add("latest_full_mix_elapsed_frame_count_mismatch");
            using (var pcm = new M2WaveReader(mix.WavePath, new(Rate, 1, 16, false, 2)))
            {
                var buffer = new byte[65536]; long bytes = 0; int count;
                while ((count = pcm.Read(buffer)) != 0) { deadline.Token.ThrowIfCancellationRequested(); bytes = checked(bytes + count); }
                wavFramesReadToEof = bytes / 2;
                if (bytes % 2 != 0 || wavFramesReadToEof != mix.FrameCount || pcm.Position != pcm.Length) errors.Add("latest_WAV_EOF_incomplete");
            }
            foreach (var marker in originals)
            {
                double begin = Math.Max(0, marker.Remote.StartSeconds - 1);
                var detected = Detect(ReadWaveWindow(mix.WavePath, begin, 4), begin); rendered.Add(detected);
                if (!detected.Found || Math.Abs(detected.StartSeconds - marker.Remote.StartSeconds) > .02)
                    errors.Add("latest_WAV_marker_differs_over_20ms:" + marker.ProgrammeSecond);
            }
            if (Hash(mix.WavePath) != mix.WaveSha256) errors.Add("latest_WAV_hash_changed");
            if (mix.CompressedPath is { } compressed)
            {
                full = CheckCompressed(compressed, 0, originals.Select(m => m.Remote.StartSeconds).ToArray(), deadline.Token);
                CheckDecodedMarkers(full, errors);
                if (full.Sha256 != mix.CompressedSha256 || full.DecodedFrames < frames || full.DecodedFrames > frames + 3072)
                    errors.Add("latest_full_M4A_hash_or_EOF_invalid");
            }

            phase = "LATEST_EXPORT"; Progress("EXPORTING_AND_DECODING_ALL_PARTS");
            export = M2ExportService.ExportParts(mix, new(M2ExportFormat.M4a, 20_000_000, 1800, 5, true),
                M2FileSafety.Inside(output, "exports"), deadline.Token);
            if (export.Parts.Count == 0 || export.Parts[0].StartFrame != 0 || export.Parts[^1].EndFrame != frames)
                errors.Add("latest_export_does_not_cover_complete_timeline");
            for (int index = 0; index < export.Parts.Count; index++)
            {
                var part = export.Parts[index];
                if (part.ByteLength != new FileInfo(part.Path).Length || part.ByteLength > 20_000_000 || Hash(part.Path) != part.Sha256)
                    errors.Add("latest_export_actual_bytes_or_hash_invalid:" + index);
                if (part.DecodedDurationSeconds > 1800 || part.TruePeak is null || part.ContainerTimeline is null ||
                    part.TruePeak.TruePeakLinear > Math.Pow(10, -1d / 20))
                    errors.Add("latest_export_duration_truepeak_or_container_verification_missing:" + index);
                if (index > 0 && (part.StartFrame != export.Parts[index - 1].EndFrame - 5 * Rate || part.EndFrame <= export.Parts[index - 1].EndFrame))
                    errors.Add("latest_export_overlap_or_progress_invalid:" + index);
                var expectedMarkers = originals.Select(m => m.Remote.StartSeconds)
                    .Where(time => time >= part.StartUs / 1_000_000d + 1 && time + 2 <= part.EndUs / 1_000_000d).ToArray();
                var decoded = CheckCompressed(part.Path, part.StartUs / 1_000_000d, expectedMarkers, deadline.Token);
                parts.Add(decoded); CheckDecodedMarkers(decoded, errors);
                long contentFrames = part.EndFrame - part.StartFrame;
                if (decoded.Sha256 != part.Sha256 || decoded.DecodedFrames != contentFrames + part.EncoderDelayFrames + part.TrailingPaddingFrames ||
                    decoded.DecodedFrames / (double)Rate > 1800)
                    errors.Add("latest_export_actual_decoded_length_invalid:" + index);
            }
            foreach (var marker in originals)
                if (!parts.Any(part => part.Markers.Any(found => found.Found && Math.Abs(found.StartSeconds - marker.Remote.StartSeconds) <= .03)))
                    errors.Add("programme_marker_missing_from_all_latest_parts:" + marker.ProgrammeSecond);
            status = errors.Count == 0 ? "PASS_LATEST_TWO_HOUR_REMOTE_REVALIDATION" : "FAIL_LATEST_OUTPUT";
        }
        catch (RevalidationNotReadyException ex) { status = "NOT_READY"; warnings.Add(ex.Message); }
        catch (RevalidationRejectedException ex) { status = "REJECTED_INPUT"; errors.Add(ex.Message); }
        catch (OperationCanceledException ex) { status = "CANCELLED_OR_TIMED_OUT"; warnings.Add(ex.Message); }
        catch (Exception ex)
        {
            status = phase == "READINESS" ? "REJECTED_OR_UNREADY_INPUT" : "FAIL_REVALIDATION";
            errors.Add(ex.GetType().Name + ": " + ex.Message);
            File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
        }
        finally
        {
            if (before.Count != 0)
            {
                try
                {
                    foreach (var item in before) after[item.Key] = M2FileSafety.Hash(sourceLocks[item.Key], CancellationToken.None);
                    bool changed = before.Any(item => after.GetValueOrDefault(item.Key) != item.Value);
                    originalsUnchanged = oldHashesMatched && before.Count == sourceLocks.Count && !changed;
                    if (changed) { errors.Add("original_hash_changed_during_revalidation"); status = "FAIL_ORIGINAL_CHANGED"; }
                }
                catch (Exception ex) { errors.Add("original_final_hash_check:" + ex.Message); status = "FAIL_FINAL_VERIFICATION"; }
            }
            if (reportLock is not null)
            {
                try { reportUnchanged = reportHashBefore == M2FileSafety.Hash(reportLock, CancellationToken.None); }
                catch (Exception ex) { errors.Add("old_report_final_hash_check:" + ex.Message); }
                if (!reportUnchanged) { errors.Add("old_report_changed"); status = "FAIL_ORIGINAL_CHANGED"; }
                reportLock.Dispose();
            }
            foreach (var file in sourceLocks.Values) file.Dispose();
            probeLock?.Dispose();
        }
        var report = new
        {
            Schema = "m2-latest-long-run-revalidation-1", Status = status, StartedAtUtc = begun, FinishedAtUtc = DateTimeOffset.UtcNow,
            Phase = phase, PriorReport = priorPath, PriorStatus = priorStatus, PriorReportSha256 = reportHashBefore, PriorReportUnchanged = reportUnchanged,
            OriginalDirectory = originalDirectory, OldHashesMatched = oldHashesMatched, OriginalsUnchanged = originalsUnchanged,
            OriginalHashesBefore = before, OriginalHashesAfter = after, CurrentAssemblies = assemblies,
            Os = Environment.OSVersion.Version.ToString(), Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            CopiedVerifiedOriginals = workingCopyVerified, CopyMethod = "Independent byte copies under this revalidation directory; no hard links, no source-directory derived writes",
            SourceStorageCleanEnd = recovered?.IsCleanEnd, SourceIssues = recovered?.Issues, OriginalMarkers = originals,
            LatestRender = mix, LatestWaveFramesReadToEof = wavFramesReadToEof, LatestRenderedMarkers = rendered,
            LatestCompressedFull = full, LatestExport = export, LatestCompressedParts = parts, Errors = errors, Warnings = warnings,
            CaptureStarted = false, PlaybackStarted = false, MicrophoneOpened = false, Uploaded = false,
            TwoDeviceSynchronization = "NOT_RUN_MICROPHONE_DISABLED", ReadinessFailuresDoNotEstablishSourceCorruption = true
        };
        string outputReport = Path.Combine(output, "revalidation-report.json");
        using (var file = new FileStream(outputReport, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, report, Json); file.Flush(true); }
        Console.WriteLine(status + "; report: " + outputReport);
        return status == "PASS_LATEST_TWO_HOUR_REMOTE_REVALIDATION" ? 0 : status is "NOT_READY" or "REJECTED_INPUT" or "REJECTED_OR_UNREADY_INPUT" ? 3 : 1;

        void Progress(string state)
        {
            File.WriteAllText(Path.Combine(output, "progress.json"), JsonSerializer.Serialize(new
            { Status = state, UpdatedAtUtc = DateTimeOffset.UtcNow, OriginalDirectory = originalDirectory, CaptureStarted = false, PlaybackStarted = false }, Json));
            Console.WriteLine("Offline revalidation: " + state);
        }
    }

    private static string ValidateLongRunReportPath(string root, string parent, string argument)
    {
        string full = SyntheticAudioProbe.SafeProjectPath(root, argument); M2FileSafety.NoReparse(full);
        string? run = Path.GetDirectoryName(full);
        if (Path.GetFileName(full) != "probe-report.json" || run is null ||
            !string.Equals(Path.GetDirectoryName(run), parent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(run).StartsWith("remote-only-long-", StringComparison.Ordinal))
            throw new RevalidationRejectedException("report_must_be_direct_owned_remote_only_long_run_probe_report");
        return full;
    }

    private static void ValidateEndedRemoteLongProof(JsonElement prior, string directory)
    {
        if (prior.GetProperty("Schema").GetString() != "m2-long-run-acoustic-probe-1" ||
            !prior.GetProperty("RemoteOnly").GetBoolean() || !prior.GetProperty("IsLongRun").GetBoolean() ||
            prior.GetProperty("RequestedSeconds").GetInt32() != LongSeconds ||
            prior.GetProperty("MicrophoneId").ValueKind != JsonValueKind.Null ||
            prior.GetProperty("EndpointFallback").GetBoolean() || prior.GetProperty("Uploaded").GetBoolean())
            throw new RevalidationRejectedException("report_does_not_describe_the_authorized_remote_only_two_hour_test");
        if (prior.GetProperty("Status").GetString() is not ("PASS_TWO_HOUR_REMOTE_ENDURANCE" or "FAIL"))
            throw new RevalidationNotReadyException("probe_report_has_no_terminal_long_run_status");
        var life = prior.GetProperty("Lifecycle");
        if (!life.GetProperty("HostExited").GetBoolean() || life.GetProperty("ProducerExitCode").ValueKind == JsonValueKind.Null ||
            life.GetProperty("ProducerExitCode").GetInt32() != 0 || !life.GetProperty("OwnedSelection").GetBoolean())
            throw new RevalidationNotReadyException("owned_producer_and_host_lifecycle_not_confirmed_closed");
        DateTimeOffset finished = prior.GetProperty("FinishedAtUtc").GetDateTimeOffset();
        if (finished > DateTimeOffset.UtcNow.AddMinutes(1)) throw new RevalidationRejectedException("invalid_future_finished_timestamp");
        string? recorded = prior.GetProperty("Storage").GetProperty("Result").GetProperty("SessionDirectory").GetString();
        if (recorded is null || !string.Equals(Path.GetFullPath(recorded), directory, StringComparison.OrdinalIgnoreCase))
            throw new RevalidationRejectedException("recording_must_be_the_report_sibling_recording_directory");
        if (!prior.GetProperty("Storage").GetProperty("OriginalsUnchanged").GetBoolean())
            throw new RevalidationRejectedException("old_report_did_not_verify_immutable_originals");
        var producer = prior.GetProperty("ProducerOutput");
        if (producer.GetProperty("Role").GetString() != "target-sync-long" || producer.GetProperty("SampleRate").GetInt32() != Rate ||
            producer.GetProperty("ToneFrames").GetInt64() != (long)LongSeconds * Rate)
            throw new RevalidationRejectedException("producer_programme_proof_mismatch");
    }

    private static Dictionary<string, string> ReadOriginalHashManifest(JsonElement hashes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in hashes.EnumerateObject())
        {
            string relative = entry.Name.Replace('\\', '/'); string hash = entry.Value.GetString() ?? "";
            if (result.Count >= 10000 || !AllowedOriginalPath(relative) || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)) || !result.TryAdd(relative, hash))
                throw new RevalidationRejectedException("invalid_or_duplicate_original_hash_entry");
        }
        foreach (string required in new[] { "meeting.json", "selection.json", "journal/capture-index.jsonl", "journal/events.jsonl" })
            if (!result.ContainsKey(required)) throw new RevalidationRejectedException("missing_required_original_hash:" + required);
        return result;
    }

    private static ProcessIdentity ValidateRevalidationSelection(JsonElement selection, JsonElement prior)
    {
        RejectDuplicateReportProperties(selection);
        if (selection.GetProperty("sourceMode").GetString() != "ProcessTree" || selection.GetProperty("initialMicrophoneEnabled").GetBoolean() ||
            selection.GetProperty("endpointScopeConfirmed").GetBoolean()) throw new RevalidationRejectedException("stored_selection_is_not_mic_off_process_tree");
        var identity = selection.GetProperty("root").Deserialize<ProcessIdentity>() ?? throw new RevalidationRejectedException("missing_producer_identity");
        var audio = selection.GetProperty("audioProcess").Deserialize<ProcessIdentity>();
        if (identity != audio || identity.ProcessId != prior.GetProperty("ProducerOutput").GetProperty("ProcessId").GetInt32())
            throw new RevalidationRejectedException("stored_source_is_not_the_owned_producer");
        return identity;
    }

    private static bool AllowedOriginalPath(string path) => path is "meeting.json" or "selection.json" or "journal/capture-index.jsonl" or "journal/events.jsonl" ||
        (path.StartsWith("sources/remote/", StringComparison.Ordinal) && path.EndsWith(".wav", StringComparison.Ordinal) &&
        path.Split('/').Length == 3 && Path.GetFileName(path).All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));

    private static IEnumerable<string> EnumerateProbeOriginals(string root)
    {
        var pending = new Stack<string>(); pending.Push(root); int count = 0;
        while (pending.TryPop(out string? directory))
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 11000) throw new RevalidationRejectedException("original_tree_exceeds_limit");
                M2FileSafety.NoReparse(entry); string relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (Directory.Exists(entry))
                {
                    if (relative == "derived") continue;
                    if (relative is not ("journal" or "sources" or "sources/remote")) throw new RevalidationRejectedException("unexpected_original_directory:" + relative);
                    pending.Push(entry);
                }
                else
                {
                    if (!AllowedOriginalPath(relative)) throw new RevalidationRejectedException("unexpected_original_file:" + relative);
                    yield return relative;
                }
            }
    }

    private static void RefuseAnyActiveCaptureHost()
    {
        var processes = Process.GetProcessesByName("ProjectEcho.CaptureHost");
        try { if (processes.Length != 0) throw new RevalidationNotReadyException("active_CaptureHost_found_no_revalidation_started"); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private static void RefuseSameLiveProducer(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartTimeUtcTicks)
                throw new RevalidationNotReadyException("original_owned_producer_is_still_alive");
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { throw new RevalidationNotReadyException("producer_exit_raced_identity_check_retry_after_exit"); }
        catch (System.ComponentModel.Win32Exception) { throw new RevalidationNotReadyException("producer_identity_could_not_be_confirmed_closed"); }
    }
    private static object AssemblyFingerprint(Assembly assembly, string root)
    {
        string location = assembly.Location;
        if (string.IsNullOrEmpty(location)) throw new InvalidOperationException("Assembly has no file provenance.");
        return new { Name = assembly.GetName().Name, Version = assembly.GetName().Version?.ToString(),
            Path = Path.GetRelativePath(root, location), Sha256 = Hash(location), ByteLength = new FileInfo(location).Length };
    }
    private static void RejectDuplicateReportProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new RevalidationRejectedException("duplicate_report_property"); RejectDuplicateReportProperties(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) RejectDuplicateReportProperties(child);
    }

    internal static void RunRevalidationGuardChecks()
    {
        // Pure metadata/path fixtures. Never call the CLI, inspect processes, or
        // create/read a live recording directory in the default test suite.
        string root = SyntheticAudioProbe.FindProjectRoot();
        string parent = Path.Combine(root, ".local", "M2LongRunIntegration");
        string run = Path.Combine(parent, "remote-only-long-SYNTHETIC-GUARD-NOT-A-RECORDING");
        string recording = Path.Combine(run, "recording"), report = Path.Combine(run, "probe-report.json");
        if (ValidateLongRunReportPath(root, parent, report) != report) throw new InvalidDataException("合法报告路径被改变。");
        RevalidationReject<IOException>(() => ValidateLongRunReportPath(root, parent, Path.Combine(root, "probe-report.json")));
        RevalidationReject<IOException>(() => ValidateLongRunReportPath(root, parent, Path.Combine(parent, "short-fixture", "probe-report.json")));
        RevalidationReject<IOException>(() => ValidateLongRunReportPath(root, parent, Path.Combine(run, "other.json")));
        var valid = JsonSerializer.SerializeToNode(new
        {
            Schema = "m2-long-run-acoustic-probe-1", Status = "PASS_TWO_HOUR_REMOTE_ENDURANCE", RemoteOnly = true, IsLongRun = true,
            RequestedSeconds = LongSeconds, MicrophoneId = (string?)null, EndpointFallback = false, Uploaded = false,
            Lifecycle = new { HostExited = true, ProducerExitCode = 0, OwnedSelection = true }, FinishedAtUtc = DateTimeOffset.UtcNow,
            Storage = new { Result = new { SessionDirectory = recording }, OriginalsUnchanged = true },
            ProducerOutput = new { Role = "target-sync-long", SampleRate = Rate, ToneFrames = (long)LongSeconds * Rate }
        })!;
        ValidateEndedRemoteLongProof(JsonSerializer.SerializeToElement(valid), recording);
        foreach (string property in new[] { "RemoteOnly", "IsLongRun" })
        {
            var bad = valid.DeepClone(); bad[property] = false;
            RevalidationReject<RevalidationRejectedException>(() => ValidateEndedRemoteLongProof(JsonSerializer.SerializeToElement(bad), recording));
        }
        var wrongRecording = valid.DeepClone(); wrongRecording["Storage"]!["Result"]!["SessionDirectory"] = Path.Combine(run, "other-recording");
        RevalidationReject<RevalidationRejectedException>(() => ValidateEndedRemoteLongProof(JsonSerializer.SerializeToElement(wrongRecording), recording));
        var active = valid.DeepClone(); active["Lifecycle"]!["HostExited"] = false;
        RevalidationReject<RevalidationNotReadyException>(() => ValidateEndedRemoteLongProof(JsonSerializer.SerializeToElement(active), recording));
        var noExit = valid.DeepClone(); noExit["Lifecycle"]!["ProducerExitCode"] = null;
        RevalidationReject<RevalidationNotReadyException>(() => ValidateEndedRemoteLongProof(JsonSerializer.SerializeToElement(noExit), recording));
        var mic = valid.DeepClone(); mic["MicrophoneId"] = "fixture-only-not-a-device";
        RevalidationReject<RevalidationRejectedException>(() => ValidateEndedRemoteLongProof(JsonSerializer.SerializeToElement(mic), recording));
        var hashes = new Dictionary<string, string>
        {
            ["meeting.json"] = new('a', 64), ["selection.json"] = new('b', 64),
            ["journal/capture-index.jsonl"] = new('c', 64), ["journal/events.jsonl"] = new('d', 64),
            ["sources/remote/remote-1-000001.wav"] = new('e', 64)
        };
        if (ReadOriginalHashManifest(JsonSerializer.SerializeToElement(hashes)).Count != 5) throw new InvalidDataException("合法原件白名单未保留。");
        foreach (string unsafeName in new[] { "../secret.wav", "sources/mic/mic-1.wav", "derived/mixed_full.wav", "sources/remote/a.wav:secret", "sources\\remote\\remote-1-000001.wav" })
        {
            var bad = new Dictionary<string, string>(hashes) { [unsafeName] = new('f', 64) };
            RevalidationReject<RevalidationRejectedException>(() => ReadOriginalHashManifest(JsonSerializer.SerializeToElement(bad)));
        }
        using var duplicate = JsonDocument.Parse("{\"RemoteOnly\":true,\"RemoteOnly\":false}");
        RevalidationReject<RevalidationRejectedException>(() => RejectDuplicateReportProperties(duplicate.RootElement));
    }

    private static void RevalidationReject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidDataException("复验入口没有拒绝非法/未就绪fixture：" + typeof(T).Name); }
    private sealed class RevalidationNotReadyException(string message) : IOException(message);
    private sealed class RevalidationRejectedException(string message) : IOException(message);
}
