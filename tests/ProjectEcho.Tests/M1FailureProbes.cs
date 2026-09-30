using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ProjectEcho.App;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

/// <summary>Explicit fault injection into one newly created test Host; never accepts a PID or external recording path.</summary>
internal static class M1FailureProbes
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("--m1-disconnect-probe" or "--m1-crash-probe")) return 2;
        bool crash = args[0] == "--m1-crash-probe";
        string root = SyntheticAudioProbe.FindProjectRoot();
        string parent = SyntheticAudioProbe.SafeProjectPath(root, ".local/M1FailureIntegration");
        Directory.CreateDirectory(parent);
        string lockPath = Path.Combine(root, ".local", "recording-probe.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Failure probe lock is a reparse point.");
        using var runLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var existing = Process.GetProcessesByName("ProjectEcho.CaptureHost");
        try { if (existing.Length != 0) { Console.Error.WriteLine("Existing CaptureHost found; failure probe refused."); return 2; } }
        finally { foreach (var process in existing) process.Dispose(); }
        string run = Path.Combine(parent, (crash ? "crash-" : "disconnect-") + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "SYNTHETIC_SOURCE_REAL_WASAPI"), "Deliberate failure injection into this probe's own CaptureHost; generated test audio only; mic OFF; no meeting/browser/system-loopback.");
        Console.WriteLine($"M1 {(crash ? "crash recovery" : "parent-disconnect safety")} probe: own process only, microphone OFF.");
        Console.WriteLine("Evidence: " + run);
        string recording = Path.Combine(run, "recording"), destination = Path.Combine(run, "recovered-copy");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(crash ? 85 : 45));
        var client = new CaptureHostClient();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new ConcurrentQueue<object>();
        var errors = new List<string>();
        var hashes = new Dictionary<string, string>();
        SyntheticAudioProbe.Producer? producer = null;
        Process? ownedHost = null;
        M1RecoveryResult? recovered = null, copiedRead = null;
        CaptureResult? copy = null;
        WatermarkReport? watermarks = null;
        bool ownedSelection = false, injectionPerformed = false, hostExited = false, forcedCleanup = false,
            originalsUnchanged = false, micAbsent = false, manifestPresent = false, thirtySecondBlock = false;
        int partialFiles = 0;
        int? producerExit = null, hostExit = null;
        double? exitDelayMs = null;
        client.EventReceived += value =>
        {
            if (value.Kind != "health") events.Enqueue(new { value.Kind, value.Message, value.Ack });
            if (value.Kind == "started") started.TrySetResult();
            if (value.Kind is "recording_saved" or "recording_incomplete" or "error")
                started.TrySetException(new IOException("Host ended before started: " + value.Kind));
        };
        try
        {
            producer = await SyntheticAudioProbe.Producer.StartAsync(root, crash ? "target-m1" : "target", deadline.Token);
            var selection = await SyntheticAudioProbe.FindOwnedSourceAsync(producer, deadline.Token);
            ownedSelection = SyntheticAudioProbe.IsOwnedSelection(selection, producer.Identity);
            if (!ownedSelection) throw new IOException("Refused capture of a process not created by this probe.");
            await client.StartRecordingAsync(new RecordingOptions(SyntheticAudioProbe.CreateOptions(selection, recording),
                MaximumSeconds: crash ? 55 : 15, ExpectedMinutes: 1), root, () => deadline.IsCancellationRequested);
            ownedHost = ObserveOwnedHost(client);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            await producer.GoAsync(deadline.Token);
            await Task.Delay(TimeSpan.FromSeconds(crash ? 32 : 2), deadline.Token);
            var exitWatch = Stopwatch.StartNew();
            if (crash)
            {
                if (ownedHost.HasExited) throw new IOException("Host exited before intended crash injection.");
                // This observer is derived only from the Process object created by this
                // client. Its executable/start identity and stable process handle were
                // verified immediately; there is no arbitrary PID lookup or kill API.
                ownedHost.Kill(entireProcessTree: false);
                injectionPerformed = true;
                await ownedHost.WaitForExitAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
                await client.DisposeAsync();
            }
            else
            {
                // Production Dispose closes its pipe and stops heartbeats. Deliberately
                // do not call StopAsync: only Host's parent-loss path can save this run.
                injectionPerformed = true;
                await client.DisposeAsync();
                await ownedHost.WaitForExitAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            }
            exitWatch.Stop(); exitDelayMs = exitWatch.Elapsed.TotalMilliseconds;
            hostExited = ownedHost.HasExited; hostExit = ownedHost.ExitCode;
            foreach (string file in Directory.EnumerateFiles(recording, "*", SearchOption.AllDirectories))
                hashes.Add(Path.GetRelativePath(recording, file), Hash(file));
            recovered = M1Recovery.Read(recording);
            manifestPresent = File.Exists(Path.Combine(recording, "meeting.json"));
            micAbsent = recovered.Capture.Tracks.All(track => track.SourceId == "remote") &&
                !Directory.EnumerateFiles(recording, "mic*", SearchOption.AllDirectories).Any();
            partialFiles = Directory.EnumerateFiles(recording, "*.partial", SearchOption.AllDirectories).Count();
            thirtySecondBlock = recovered.Capture.Tracks.Any(track => track.FrameCount == track.Format.SampleRate * 30L &&
                track.WavePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
            if (!recovered.HasCheckpoint || !recovered.Capture.Tracks.Any(track => track.FrameCount > 0)) errors.Add("no_checkpoint_confirmed_audio");
            if (!micAbsent) errors.Add("unexpected_microphone_data");
            if (crash)
            {
                if (manifestPresent || recovered.IsCleanEnd) errors.Add("crashed_recording_incorrectly_marked_clean");
                if (!thirtySecondBlock || partialFiles == 0) errors.Add("crash_did_not_preserve_full_block_and_partial");
                watermarks = CheckWatermarks(recording, recovered);
                if (!watermarks.Pass) errors.AddRange(watermarks.Errors);
                copy = M1Recovery.RecoverTo(recording, destination);
                copiedRead = M1Recovery.Read(destination);
                if (copy.Complete || !copiedRead.HasCheckpoint || copy.Tracks.Sum(track => track.FrameCount) != recovered.Capture.Tracks.Sum(track => track.FrameCount) ||
                    copiedRead.Capture.Tracks.Sum(track => track.FrameCount) != copy.Tracks.Sum(track => track.FrameCount))
                    errors.Add("recovery_copy_did_not_preserve_exact_checkpoint_frames");
                if (Directory.EnumerateFiles(destination, "*.partial", SearchOption.AllDirectories).Any()) errors.Add("recovery_copy_not_finalized");
            }
            else if (!manifestPresent || !recovered.IsCleanEnd || partialFiles != 0)
                errors.Add("parent_disconnect_did_not_finish_valid_manifest_and_originals");
            originalsUnchanged = hashes.All(pair => Hash(Path.Combine(recording, pair.Key)) == pair.Value);
            if (!originalsUnchanged) errors.Add("original_changed_during_recovery");
        }
        catch (Exception ex)
        {
            errors.Add(ex.GetType().Name + ": " + ex.Message);
            File.WriteAllText(Path.Combine(run, "failure.txt"), ex.ToString());
        }
        finally
        {
            try { await client.DisposeAsync(); }
            catch (Exception ex) { errors.Add("client_shutdown:" + ex.GetType().Name); }
            if (ownedHost is not null)
            {
                try
                {
                    if (!ownedHost.HasExited)
                    {
                        forcedCleanup = true;
                        errors.Add("owned_host_required_forced_cleanup");
                        ownedHost.Kill(entireProcessTree: false);
                        await ownedHost.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    hostExited = ownedHost.HasExited; hostExit = ownedHost.ExitCode;
                }
                catch (Exception ex) { errors.Add("owned_host_cleanup:" + ex.GetType().Name); }
                finally { ownedHost.Dispose(); }
            }
            if (producer is not null)
            {
                try { producerExit = await producer.StopAsync(); }
                catch (Exception ex) { errors.Add("producer_cleanup:" + ex.GetType().Name); }
            }
        }
        if (!injectionPerformed || !ownedSelection || !hostExited || producerExit != 0 || forcedCleanup) errors.Add("fault_injection_or_owned_child_lifecycle_incomplete");
        bool pass = errors.Count == 0;
        var packets = recovered?.Capture.Tracks.SelectMany(track => track.Packets).ToArray() ?? [];
        var report = new
        {
            Schema = "m1-failure-probe-1", Test = crash ? "owned_host_crash_and_checkpoint_recovery" : "parent_pipe_disconnect_safely_stops_capture",
            Status = pass ? "PASS_WITH_CLOCK_LIMITATIONS" : "FAIL", Os = Environment.OSVersion.Version.ToString(),
            CaptureMode = "ProcessTree/test-owned producer", MicrophoneEnabled = false, EndpointFallback = false, BrowserOrMeetingOpened = false,
            Fault = new { Kind = crash ? "Kill_this_clients_Host_only" : "Dispose_production_client_without_Stop", InjectionPerformed = injectionPerformed,
                DelayAfterGoSeconds = crash ? 32 : 2, HostExitDelayMilliseconds = exitDelayMs, ForcedCleanup = forcedCleanup },
            OrphanSafety = new { Applicable = !crash, HostExited = hostExited, HostExitCode = hostExit,
                ManifestPresent = manifestPresent, CleanOfflineEnd = recovered?.IsCleanEnd },
            Recovery = new { Applicable = crash, HasCheckpoint = recovered?.HasCheckpoint, OriginalPartialFiles = partialFiles,
                FullThirtySecondBlockPreserved = thirtySecondBlock, WatermarkChecks = watermarks,
                VerifiedOriginalFrames = recovered?.Capture.Tracks.Sum(track => track.FrameCount),
                RecoveredCopyFrames = copy?.Tracks.Sum(track => track.FrameCount), CopyReadableFromDisk = copiedRead?.HasCheckpoint,
                OriginalIssues = recovered?.Issues, CopyIssues = copiedRead?.Issues, OriginalsUnchanged = originalsUnchanged },
            PrivacyAndLifecycle = new { OwnedSelection = ownedSelection, MicDataAbsent = micAbsent, HostExited = hostExited, ProducerExitCode = producerExit },
            ClockLimitations = new { Status = packets.Any(packet => !packet.DevicePositionValid) ? "QPC_REFERENCED_DEVICE_COUNTER_LIMITED" : "SHORT_SINGLE_SOURCE_ONLY",
                DeviceCounterUnavailablePackets = packets.Count(packet => !packet.DevicePositionValid), InvalidQpcPackets = packets.Count(packet => !packet.TimestampValid),
                TwoHourSynchronization = "NOT_RUN", IndependentDeviceDrift = "NOT_ESTABLISHED", RealMicrophonePrivacy = "NOT_RUN" },
            Events = events.ToArray(), OriginalHashes = hashes, Errors = errors
        };
        string reportPath = Path.Combine(run, "probe-report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{report.Status}; test={report.Test}; hostExited={hostExited}; mic=OFF; originalsUnchanged={originalsUnchanged}");
        Console.WriteLine("Report: " + reportPath);
        return pass ? 0 : 1;
    }

    private static Process ObserveOwnedHost(CaptureHostClient client)
    {
        // Test-only reflection, intentionally kept out of production client APIs.
        var field = typeof(CaptureHostClient).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("Production client's owned Host process field was not found.");
        var original = field.GetValue(client) as Process ?? throw new IOException("This client has not created a Host.");
        string expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "CaptureHost", "ProjectEcho.CaptureHost.exe"));
        if (original.HasExited || !string.Equals(original.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("This client's process is not the expected bundled CaptureHost.");
        long started = original.StartTime.ToUniversalTime().Ticks;
        var observer = Process.GetProcessById(original.Id);
        try
        {
            _ = observer.SafeHandle; // Pin the process object; later PID reuse cannot redirect this handle.
            if (observer.StartTime.ToUniversalTime().Ticks != started ||
                !string.Equals(observer.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Owned Host identity changed while acquiring its observation handle.");
            return observer;
        }
        catch { observer.Dispose(); throw; }
    }

    private static WatermarkReport CheckWatermarks(string directory, M1RecoveryResult recovery)
    {
        var errors = new List<string>();
        var expected = new Dictionary<string, (long Frames, long Bytes, long Offset)>();
        long uncommittedPacketFrames = 0;
        bool truncatedJournalTail = false;
        foreach (string line in File.ReadLines(recovery.Capture.IndexPath))
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { truncatedJournalTail = true; break; }
            using (document)
            {
                var envelope = document.RootElement; var data = envelope.GetProperty("data");
                long sequence = envelope.GetProperty("sequence").GetInt64();
                if (sequence == recovery.LastCheckpointSequence)
                    foreach (var chunk in data.GetProperty("chunks").EnumerateArray())
                        expected.Add(chunk.GetProperty("path").GetString()!, (chunk.GetProperty("committedFrameCount").GetInt64(),
                            chunk.GetProperty("committedPcmBytes").GetInt64(), chunk.GetProperty("dataOffsetBytes").GetInt64()));
                if (sequence > recovery.LastCheckpointSequence && data.GetProperty("type").GetString() == "packet")
                    uncommittedPacketFrames += data.GetProperty("frameCount").GetInt32();
            }
        }
        long uncommittedPhysicalBytes = 0, recoveredFrames = 0;
        foreach (var track in recovery.Capture.Tracks)
        {
            string relative = Path.GetRelativePath(directory, track.WavePath).Replace('\\', '/');
            if (relative.EndsWith(".partial", StringComparison.Ordinal)) relative = relative[..^8];
            if (!expected.TryGetValue(relative, out var watermark) || track.FrameCount != watermark.Frames)
            { errors.Add("recovered_track_exceeds_or_mismatches_checkpoint"); continue; }
            long physical = new FileInfo(track.WavePath).Length;
            if (physical < watermark.Offset + watermark.Bytes) errors.Add("physical_original_shorter_than_checkpoint");
            uncommittedPhysicalBytes += Math.Max(0, physical - watermark.Offset - watermark.Bytes);
            recoveredFrames += track.FrameCount;
        }
        if (expected.Count != recovery.Capture.Tracks.Count || expected.Values.Sum(value => value.Frames) != recoveredFrames)
            errors.Add("recovery_did_not_return_exact_checkpoint_frame_set");
        return new(errors.Count == 0, recovery.LastCheckpointSequence, recoveredFrames, uncommittedPacketFrames,
            uncommittedPhysicalBytes, truncatedJournalTail,
            "Returned PCM must equal only the last committed checkpoint. Any indexed/physical later bytes are excluded; zero observed tail is reported as zero, never fabricated.", errors);
    }

    private static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    private sealed record WatermarkReport(bool Pass, long LastCheckpointSequence, long ConfirmedFrames,
        long ObservedUncommittedIndexedFrames, long ObservedUncommittedPhysicalBytes, bool TruncatedJournalTail,
        string Policy, IReadOnlyList<string> Errors);
}
