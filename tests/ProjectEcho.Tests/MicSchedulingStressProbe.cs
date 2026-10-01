using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using ProjectEcho.App;
using ProjectEcho.Capture.Windows;
using ProjectEcho.CaptureHost;
using ProjectEcho.Core;

internal static class MicSchedulingStressProbe
{
    internal static Task<int> RunAsync(string[] args) => M2LongRunProbe.RunSchedulingStressAsync(args);
    internal static Task<int> RunCpuChildAsync(string[] args) => CpuStressChild.RunAsync(args);
}

internal static partial class M2LongRunProbe
{
    internal static async Task<int> RunSchedulingStressAsync(string[] args)
    {
        if (args.Length != 3 || args[0] != "--mic-scheduling-stress-probe" || string.IsNullOrWhiteSpace(args[1]) || string.IsNullOrWhiteSpace(args[2])) return 2;
        string root = SyntheticAudioProbe.FindProjectRoot();
        string parent = SyntheticAudioProbe.SafeProjectPath(root, ".local/MicSchedulingStress"); Directory.CreateDirectory(parent);
        string lockPath = Path.Combine(root, ".local", "recording-probe.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Probe lock is a reparse point.");
        using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var hosts = Process.GetProcessesByName("ProjectEcho.CaptureHost");
        try { if (hosts.Length != 0) throw new IOException("Existing CaptureHost found."); } finally { foreach (var host in hosts) host.Dispose(); }
        var snapshot = SourceDiscovery.Enumerate();
        if (!snapshot.Microphones.Any(mic => mic.Id == args[1]) || !snapshot.RenderDevices.Any(device => device.Id == args[2]))
            throw new IOException("Explicit mic/output unavailable; no substitution.");
        string run = Path.Combine(parent, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        Console.WriteLine("Scheduling stress evidence: " + run);
        File.WriteAllText(Path.Combine(run, "execution-payload.json"), JsonSerializer.Serialize(new
        { CreatedAtUtc = DateTimeOffset.UtcNow, Assemblies = new[] { typeof(RecordingOptions).Assembly, typeof(AudioClock).Assembly, typeof(MicSchedulingStressProbe).Assembly }
            .Select(assembly => new { assembly.Location, Sha256 = Hash(assembly.Location) }),
            FixedMicrophoneId = args[1], FixedOutputId = args[2], PcmLocalOnly = true, Uploaded = false }, Json));
        var arms = new List<object>(); bool allPass = true;
        foreach (bool enable in new[] { false, true })
        {
            var outcome = await Arm(enable); arms.Add(outcome.Report); allPass &= outcome.Pass;
            if (!outcome.SafeToContinue) break;
        }
        var report = new { Schema = "mic-scheduling-stress-1", Status = allPass && arms.Count == 2 ? "PASS_BOUNDED_CPU_LOAD_BOTH_ARMS" : "FAIL",
            FinishedAtUtc = DateTimeOffset.UtcNow, Arms = arms, Order = "off then on; not randomized",
            Limitation = "Only two fixed 10-second ordinary-priority CPU bursts per 90-second recording. Both passing does not reproduce or explain the earlier build-associated gaps and does not guarantee all CPU/IO/GC loads or two-hour continuity.",
            Uploaded = false, ReplayedMic = false, TranscribedMic = false, SystemSettingsChanged = false, EndpointFallback = false };
        string reportPath = Path.Combine(run, "report.json"); File.WriteAllText(reportPath, JsonSerializer.Serialize(report, Json));
        Console.WriteLine(report.Status + "; report: " + reportPath); return allPass && arms.Count == 2 ? 0 : 1;

        async Task<(object Report, bool Pass, bool SafeToContinue)> Arm(bool enabled)
        {
            string folder = Path.Combine(run, enabled ? "mmcss-on" : "mmcss-off"); Directory.CreateDirectory(folder);
            string recording = Path.Combine(folder, "recording");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            var client = new CaptureHostClient(); SyntheticAudioProbe.Producer? producer = null; OwnedCpuStress? cpu = null;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminal = new TaskCompletionSource<HostEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var health = new ConcurrentQueue<object>(); var events = new ConcurrentQueue<object>();
            var errors = new List<string>(); var quality = new List<string>(); var gaps = new List<EnduranceGap>();
            var markers = new List<MarkerDetection>(); var hashes = new Dictionary<string, string>();
            RecordingResult? result = null; M1RecoveryResult? recovered = null;
            bool owned = false, hostExited = false, unchanged = false; int? producerExit = null;
            ulong goQpc = 0;
            client.EventReceived += value =>
            {
                if (value.Kind == "started") started.TrySetResult();
                if (value.Health is { } h)
                {
                    health.Enqueue(new { ObservedQpc100ns = Now100ns(), Health = h });
                    if (h.Paused || !h.MicrophoneEnabled || h.MicStatus.Contains("来源故障", StringComparison.Ordinal) || h.RemoteStatus.Contains("来源故障", StringComparison.Ordinal)) failed.TrySetResult();
                }
                else events.Enqueue(new { ObservedQpc100ns = Now100ns(), value.Kind, value.Message, value.Ack });
                if (value.Kind is "recording_saved" or "recording_incomplete" or "error")
                { terminal.TrySetResult(value); started.TrySetException(new IOException("Host ended before startup.")); }
            };
            try
            {
                cpu = await OwnedCpuStress.StartAsync(root, deadline.Token);
                producer = await SyntheticAudioProbe.Producer.StartAsync(root, "target-sync-stress", deadline.Token, args[2]);
                var source = await SyntheticAudioProbe.FindOwnedSourceAsync(producer, deadline.Token);
                owned = SyntheticAudioProbe.IsOwnedSelection(source, producer.Identity);
                if (!owned) throw new IOException("Source ownership mismatch.");
                var selection = SyntheticAudioProbe.CreateOptions(source, recording) with { MicrophoneEnabled = true, MicrophoneDeviceId = args[1], Title = "受控CPU负载与录音线程调度对照" };
                await client.StartRecordingAsync(new RecordingOptions(selection, 110, 2, EnableMultimediaScheduling: enabled), root, () => deadline.IsCancellationRequested);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(12), deadline.Token);
                goQpc = Now100ns(); await producer.GoAsync(deadline.Token); await cpu.GoAsync(deadline.Token);
                Console.WriteLine("MMCSS " + (enabled ? "ON" : "OFF") + ": recording 90 seconds, CPU bursts at 20 and 50 seconds.");
                Task done = producer.WaitDoneAsync(deadline.Token);
                if (await Task.WhenAny(done, terminal.Task, failed.Task) != done) throw new IOException("Capture ended or source became paused/disabled/faulted during stress.");
                await done;
                await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
                var final = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(30), deadline.Token);
                await client.WaitForExitAsync(); hostExited = true;
                producerExit = await producer.StopAsync(); producer = null;
                await cpu.DisposeAsync();
                result = final.RecordingResult ?? throw new IOException("Missing saved recording result.");
                foreach (string file in Directory.EnumerateFiles(recording, "*", SearchOption.AllDirectories)) hashes.Add(Path.GetRelativePath(recording, file), Hash(file));
                recovered = M1Recovery.Read(recording);
                if (!recovered.HasCheckpoint || recovered.Issues.Count != 0) errors.Add("disk_index_or_hash_validation_failed");
                if (!result.StorageComplete || !recovered.IsCleanEnd) quality.Add("source_or_saved_completeness_not_passed");
                if (result.DurationUs < 89_000_000 || result.DurationUs > 100_000_000) errors.Add("duration_outside_stress_range");
                InspectEnduranceContinuity(recovered.Capture, gaps, quality, errors);
                foreach (var track in recovered.Capture.Tracks)
                {
                    using var wave = new WaveFileReader(track.WavePath);
                    if (wave.Length != track.FrameCount * track.Format.BlockAlign || track.Packets.Sum(packet => (long)packet.FrameCount) != track.FrameCount) errors.Add("wave_frame_count_mismatch");
                    if (track.Packets.Any(packet => !packet.TimestampValid || track.SourceId == "mic" && !packet.DevicePositionValid)) errors.Add("invalid_raw_clock");
                }
                double goSession = (double)((Int128)goQpc - recovered.Capture.EpochQpc100ns) / AudioClock.UnitsPerSecond;
                foreach (int marker in StressMarkers)
                {
                    double begin = Math.Max(0, goSession + marker - 1); var value = Detect(ReadWindow(recovered.Capture, "remote", begin, 4), begin); markers.Add(value);
                    if (!value.Found) errors.Add("remote_marker_missing:" + marker);
                }
                var schedule = result.Diagnostics?.Scheduling;
                if (result.Diagnostics is null || result.Diagnostics.Sources.Count != 2) errors.Add("source_timing_diagnostics_missing");
                if (enabled && (schedule is null || !schedule.Registered || !schedule.PriorityApplied || !schedule.Released || schedule.TaskName != "Audio")) errors.Add("MMCSS_Audio_NORMAL_registration_or_release_not_confirmed");
                if (!enabled && schedule is not null) errors.Add("MMCSS_registered_while_disabled");
                if (result.Diagnostics?.Sources.SingleOrDefault(value => value.SourceId == "mic")?.ProcessingMode != "shared-raw") errors.Add("mic_processing_mode_not_shared_raw");
                if (cpu.Events.Count(value => value.GetProperty("Type").GetString() == "burst_end") != 2 ||
                    !cpu.Events.Any(value => value.GetProperty("Type").GetString() == "done") || cpu.ExitCode != 0) errors.Add("CPU_programme_not_complete");
                unchanged = hashes.All(pair => Hash(Path.Combine(recording, pair.Key)) == pair.Value);
                if (!unchanged) errors.Add("original_changed_during_validation");
            }
            catch (Exception ex) { errors.Add(ex.GetType().Name + ": " + ex.Message); File.WriteAllText(Path.Combine(folder, "failure.txt"), ex.ToString()); }
            finally
            {
                try { await client.DisposeAsync(); hostExited = true; } catch (Exception ex) { errors.Add("host_cleanup:" + ex.Message); }
                if (producer is not null) try { producerExit = await producer.StopAsync(); } catch (Exception ex) { errors.Add("source_cleanup:" + ex.Message); }
                if (cpu is not null) try { await cpu.DisposeAsync(); } catch (Exception ex) { errors.Add("CPU_cleanup:" + ex.Message); }
            }
            if (!owned || !hostExited || producerExit != 0 || cpu?.ExitCode != 0) errors.Add("owned_process_cleanup_incomplete");
            bool pass = errors.Count == 0 && quality.Count == 0;
            bool safe = owned && hostExited && producerExit == 0 && cpu?.ExitCode == 0 && recovered?.HasCheckpoint == true && recovered.Issues.Count == 0 && unchanged &&
                errors.All(error => error.StartsWith("nonstartup_or_large_declared_gap:", StringComparison.Ordinal));
            var armReport = new { Status = pass ? "PASS_BOUNDED_CPU_STRESS_ARM" : "FAIL", EnableMultimediaScheduling = enabled,
                FinishedAtUtc = DateTimeOffset.UtcNow, Result = result, RawCaptureQualityPassed = pass, QualityErrors = quality, Errors = errors,
                SourceStorageComplete = result?.StorageComplete,
                MicDeclaredGapCount = gaps.Count(gap => gap.Segment.StartsWith("mic-", StringComparison.Ordinal)),
                RemoteDeclaredGapCount = gaps.Count(gap => gap.Segment.StartsWith("remote-", StringComparison.Ordinal)),
                CaptureDiagnostics = result?.Diagnostics,
                Disk = new { recovered?.HasCheckpoint, recovered?.IsCleanEnd, recovered?.Issues, OriginalsUnchanged = unchanged, OriginalHashes = hashes },
                Sources = recovered?.Capture.Tracks.GroupBy(track => track.SourceId).Select(group => new { Source = group.Key, Frames = group.Sum(track => track.FrameCount),
                    Packets = group.Sum(track => track.Packets.Count), Formats = group.Select(track => track.Format).Distinct().ToArray() }),
                Gaps = gaps, Markers = markers, CpuReady = cpu?.ReadyInfo, CpuEvents = cpu?.Events.ToArray(), Health = health.ToArray(), Events = events.ToArray(),
                Lifecycle = new { OwnedSelection = owned, HostExited = hostExited, ProducerExitCode = producerExit, CpuExitCode = cpu?.ExitCode },
                NextArmSafeToRun = safe, PerPacketObservedTime = "not recorded; diagnostics and health observation times are not audio alignment inputs" };
            File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(armReport, Json));
            Console.WriteLine(armReport.Status + "; MMCSS=" + enabled + "; report: " + Path.Combine(folder, "report.json"));
            return (armReport, pass, safe);
        }
    }
}
