using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using NAudio.MediaFoundation;
using ProjectEcho.App;
using ProjectEcho.Capture.Windows;
using ProjectEcho.CaptureHost;
using ProjectEcho.Core;
using ProjectEcho.Export;

/// <summary>Explicit acoustic synchronization test. Never selects a replacement mic or changes output routing/volume.</summary>
internal static partial class M2LongRunProbe
{
    internal const int ShortSeconds = 26, StressSeconds = 90, MediumSeconds = 1200, LongSeconds = 7200;
    private const int Rate = 48000, Hop = 240, Window = 960;
    private static readonly int[] Frequencies = [733, 997, 1319, 1699];
    private static readonly int[] ShortMarkers = [2, 12, 22];
    private static readonly int[] LongMarkers = [2, 1802, 3602, 5402, 7192];
    private static readonly int[] MediumMarkers = [2, 602, 1192];
    private static readonly int[] StressMarkers = [2, 25, 45, 55, 82];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static float MarkerSample(int frame, bool longRun) => MarkerSampleAt(frame, longRun ? LongMarkers : ShortMarkers);

    internal static float MediumMarkerSample(int frame) => MarkerSampleAt(frame, MediumMarkers);
    internal static float StressMarkerSample(int frame) => MarkerSampleAt(frame, StressMarkers);

    private static float MarkerSampleAt(int frame, int[] markers)
    {
        int second = frame / Rate;
        foreach (int marker in markers)
        {
            if (second < marker || second > marker + 1) continue;
            int offset = frame - marker * Rate;
            int slot = offset / (Rate * 3 / 10), local = offset % (Rate * 3 / 10);
            int length = Rate * 18 / 100;
            if (slot >= Frequencies.Length || local >= length) return 0;
            double envelope = Math.Clamp(Math.Min(local, length - 1 - local) / (Rate * .01), 0, 1);
            return (float)(ProbeToneSource.PeakAmplitude * envelope * Math.Sin(2 * Math.PI * Frequencies[slot] * local / Rate));
        }
        return 0;
    }

    internal static Task<int> RunAsync(string[] args) => RunCoreAsync(args, remoteOnly: false);

    internal static Task<int> RunEnduranceAsync(string[] args)
    {
        if (args.Length is < 1 or > 3 || args[0] != "--m2-endurance-probe" || (args.Length > 1 && args[1] is not ("short" or "long")))
            return Task.FromResult(2);
        string mode = args.Length > 1 ? args[1] : "long";
        string[] forwarded = args.Length == 3
            ? mode == "long" ? ["--m2-sync-probe", "MICROPHONE_DISABLED", mode, "", args[2]] : ["--m2-sync-probe", "MICROPHONE_DISABLED", mode, args[2]]
            : ["--m2-sync-probe", "MICROPHONE_DISABLED", mode];
        return RunCoreAsync(forwarded, remoteOnly: true);
    }

    private static async Task<int> RunCoreAsync(string[] args, bool remoteOnly)
    {
        if (args.Length is < 3 or > 5 || args[0] != "--m2-sync-probe" || args[2] is not ("short" or "long") || string.IsNullOrWhiteSpace(args[1])) return 2;
        bool longRun = args[2] == "long";
        if (!longRun && args.Length > 4) return 2;
        string? requestedOutputDevice = longRun ? args.Length == 5 ? args[4] : null : args.Length == 4 ? args[3] : null;
        string? qualifiedOutputDevice = null;
        string root = SyntheticAudioProbe.FindProjectRoot();
        string parent = SyntheticAudioProbe.SafeProjectPath(root, ".local/M2LongRunIntegration");
        Directory.CreateDirectory(parent);
        if (longRun && !remoteOnly)
        {
            if (args.Length < 4) throw new ArgumentException("Long test requires the successful short acoustic report path.");
            string proofPath = SyntheticAudioProbe.SafeProjectPath(root, args[3]);
            using var proof = JsonDocument.Parse(File.ReadAllBytes(proofPath));
            var p = proof.RootElement;
            if (p.GetProperty("Status").GetString() != "PASS_ACOUSTIC_MARKER_SHORT" || p.GetProperty("MicrophoneId").GetString() != args[1] ||
                DateTimeOffset.UtcNow - p.GetProperty("FinishedAtUtc").GetDateTimeOffset() > TimeSpan.FromHours(12))
                throw new InvalidDataException("A recent successful acoustic qualification for this exact microphone is required.");
            qualifiedOutputDevice = p.GetProperty("ProducerOutput").GetProperty("OutputDeviceId").GetString();
            requestedOutputDevice ??= qualifiedOutputDevice;
        }
        string lockPath = Path.Combine(root, ".local", "recording-probe.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Probe lock is a reparse point.");
        using var runLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var existing = Process.GetProcessesByName("ProjectEcho.CaptureHost");
        try { if (existing.Length != 0) throw new IOException("Existing CaptureHost found; synchronization probe refused."); }
        finally { foreach (var process in existing) process.Dispose(); }
        var microphone = remoteOnly ? null : SourceDiscovery.Enumerate().Microphones.SingleOrDefault(device => device.Id == args[1])
            ?? throw new IOException("The explicitly selected microphone is unavailable; no fallback attempted.");
        string run = Path.Combine(parent, (remoteOnly ? "remote-only-" : "") + (longRun ? "long-" : "short-") + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, remoteOnly ? "SYNTHETIC_REMOTE_ONLY_MIC_OFF" : "REAL_MICROPHONE_LOCAL_ONLY"),
            remoteOnly ? "Only the newly created synthetic process is captured. Microphone disabled for the entire run; no microphone client or PCM. This is NOT a dual-device synchronization test."
            : "Explicit fixed-microphone acoustic marker test. Local PCM retained. No speech interpretation, replay, transcription or upload.");
        Console.WriteLine("Synchronization evidence: " + run);
        int seconds = longRun ? LongSeconds : ShortSeconds;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 180));
        var client = new CaptureHostClient();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource<HostEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceFault = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new ConcurrentQueue<object>();
        RecordingHealth? latestHealth = null;
        long healthCount = 0;
        client.EventReceived += value =>
        {
            if (value.Kind == "started") started.TrySetResult();
            if (value.Health is { } h)
            {
                Volatile.Write(ref latestHealth, h); Interlocked.Increment(ref healthCount);
                if (h.Paused || (!remoteOnly && !h.MicrophoneEnabled) || (remoteOnly && (h.MicrophoneEnabled || h.MicFrames != 0)) ||
                    h.MicStatus.Contains("来源故障", StringComparison.Ordinal) || h.RemoteStatus.Contains("来源故障", StringComparison.Ordinal))
                    sourceFault.TrySetResult("source_paused_disabled_or_faulted");
            }
            else events.Enqueue(new { value.Kind, value.RequestId, value.Message, value.Ack });
            if (value.Kind is "recording_saved" or "recording_incomplete" or "error")
            {
                terminal.TrySetResult(value);
                started.TrySetException(new IOException("Host ended before the startup acknowledgement."));
            }
        };
        SyntheticAudioProbe.Producer? producer = null;
        JsonElement? producerOutput = null;
        string? producerFailure = null;
        RecordingResult? result = null;
        M1RecoveryResult? recovery = null;
        FullMixResult? mix = null;
        ExportResult? exported = null;
        CompressedCheck? compressedFull = null;
        var compressedParts = new List<CompressedCheck>();
        var errors = new List<string>();
        var markerResults = new List<MarkerComparison>();
        var mixMarkers = new List<MarkerDetection>();
        var hashes = new Dictionary<string, string>();
        bool hostExited = false, owned = false, originalsUnchanged = false, acousticBlocked = false;
        int? producerExit = null;
        ulong goQpc = 0;
        string directory = Path.Combine(run, "recording");
        try
        {
            producer = await SyntheticAudioProbe.Producer.StartAsync(root, longRun ? "target-sync-long" : "target-sync-short", deadline.Token, requestedOutputDevice);
            producerOutput = producer.ReadyInfo;
            if (longRun && !remoteOnly && (string.IsNullOrEmpty(qualifiedOutputDevice) || producerOutput?.GetProperty("OutputDeviceId").GetString() != qualifiedOutputDevice))
                throw new IOException("The default output endpoint changed since short qualification; no microphone recording started.");
            var selection = await SyntheticAudioProbe.FindOwnedSourceAsync(producer, deadline.Token);
            owned = SyntheticAudioProbe.IsOwnedSelection(selection, producer.Identity);
            if (!owned) throw new IOException("Source identity does not match the newly created test process.");
            var options = SyntheticAudioProbe.CreateOptions(selection, directory) with
            { MicrophoneEnabled = !remoteOnly, MicrophoneDeviceId = microphone?.Id,
                Title = remoteOnly ? "远端合成标记耐久测试（mic始终关闭）" : longRun ? "两小时公共声学标记同步测试（仅本地）" : "公共声学标记短预检（仅本地）" };
            await client.StartRecordingAsync(new RecordingOptions(options, seconds + 30, longRun ? 122 : 1), root, () => deadline.IsCancellationRequested);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(12), deadline.Token);
            goQpc = Now100ns();
            await producer.GoAsync(deadline.Token);
            var done = producer.WaitDoneAsync(deadline.Token);
            var timer = Stopwatch.StartNew();
            while (!done.IsCompleted)
            {
                var tick = Task.Delay(TimeSpan.FromSeconds(30), deadline.Token);
                var finished = await Task.WhenAny(done, terminal.Task, sourceFault.Task, tick);
                if (finished == terminal.Task) throw new IOException("Host ended before the complete marker programme.");
                if (finished == sourceFault.Task) throw new IOException(await sourceFault.Task);
                deadline.Token.ThrowIfCancellationRequested();
                if (finished == tick)
                {
                    var h = Volatile.Read(ref latestHealth);
                    var progress = new { Status = "RECORDING", ElapsedSeconds = timer.Elapsed.TotalSeconds, RequestedSeconds = seconds,
                        HealthEvents = Interlocked.Read(ref healthCount), Health = h, UpdatedAtUtc = DateTimeOffset.UtcNow };
                    File.WriteAllText(Path.Combine(run, "progress.json"), JsonSerializer.Serialize(progress, Json));
                    Console.WriteLine($"Recording {timer.Elapsed.TotalSeconds:F0}/{seconds}s; remote={h?.RemoteFrames}; mic={h?.MicFrames}; free={h?.AvailableBytes}");
                }
            }
            await done;
            // Output hardware may run slightly faster than the nominal sample clock.
            // Keep an actual monotonic two hours, plus a one-second margin, before Stop.
            if (longRun && timer.Elapsed.TotalSeconds < seconds + 1)
                await Task.Delay(TimeSpan.FromSeconds(seconds + 1 - timer.Elapsed.TotalSeconds), deadline.Token);
            await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
            var final = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(40), deadline.Token);
            await client.WaitForExitAsync(); hostExited = true;
            // The producer must finish before potentially long hashing/rendering;
            // its independent hard deadline protects recording, not postprocessing.
            producerExit = await producer.StopAsync(); producerFailure = producer.FailureDiagnostic; producer = null;
            result = final.RecordingResult ?? throw new IOException("Missing final recording result.");
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) hashes.Add(Path.GetRelativePath(directory, file), Hash(file));
            recovery = M1Recovery.Read(directory);
            if (!result.StorageComplete || !recovery.IsCleanEnd) errors.Add("storage_or_checkpoint_not_complete");
            if (Math.Abs(result.DurationUs / 1_000_000d - seconds) > 15) errors.Add("elapsed_duration_outside_test_limit");
            var mic = recovery.Capture.Tracks.Where(track => track.SourceId == "mic").ToArray();
            if (remoteOnly && (mic.Length != 0 || Directory.Exists(Path.Combine(directory, "sources", "mic")))) errors.Add("microphone_storage_created_while_disabled");
            if (!remoteOnly && (mic.Length == 0 || mic.Any(track => track.Format.SampleRate != Rate || track.Packets.Any(packet => !packet.TimestampValid || !packet.DevicePositionValid))))
                errors.Add("microphone_format_or_clock_invalid");
            CheckContinuity(recovery.Capture, errors);
            double goSession = (double)((Int128)goQpc - recovery.Capture.EpochQpc100ns) / AudioClock.UnitsPerSecond;
            foreach (int marker in longRun ? LongMarkers : ShortMarkers)
            {
                double sliceStart = Math.Max(0, goSession + marker - 1);
                var remote = Detect(ReadWindow(recovery.Capture, "remote", sliceStart, 4), sliceStart);
                var local = remoteOnly ? new MarkerDetection(false, 0, 0, 0, []) : Detect(ReadWindow(recovery.Capture, "mic", sliceStart, 4), sliceStart);
                markerResults.Add(new(marker, remote, local, remote.Found && local.Found ? local.StartSeconds - remote.StartSeconds : null));
            }
            if (markerResults.Any(marker => !marker.Remote.Found)) errors.Add("test_owned_remote_marker_missing");
            if (!remoteOnly && markerResults.Any(marker => !marker.Microphone.Found))
            {
                acousticBlocked = true;
                errors.Add("BLOCKED_no_reliable_acoustic_marker_at_fixed_microphone_no_volume_or_route_changed");
            }
            if (!remoteOnly && !acousticBlocked && markerResults.All(marker => marker.DelaySeconds.HasValue))
            {
                double reference = markerResults[0].DelaySeconds!.Value;
                if (markerResults.Any(marker => Math.Abs(marker.DelaySeconds!.Value - reference) > .1)) errors.Add("acoustic_delay_drift_exceeds_100ms");
                if (markerResults.Any(marker => marker.DelaySeconds is < -.03 or > 1)) errors.Add("implausible_acoustic_path_delay");
            }
            if (errors.Count == 0)
            {
                // Production full-timeline renderer, after capture has closed. No microphone audio is replayed.
                using var rendering = new CancellationTokenSource(TimeSpan.FromMinutes(longRun ? 40 : 3));
                File.WriteAllText(Path.Combine(run, "progress.json"), JsonSerializer.Serialize(new { Status = "OFFLINE_RENDERING", HostExited = hostExited, ProducerExitCode = producerExit, UpdatedAtUtc = DateTimeOffset.UtcNow }, Json));
                mix = remoteOnly ? M2ExportService.RenderMeeting(directory, rendering.Token) : M2ExportService.RenderMeeting(directory, encoder: null, rendering.Token);
                foreach (var marker in markerResults)
                {
                    double begin = Math.Max(0, marker.Remote.StartSeconds - 1);
                    var rendered = Detect(ReadWaveWindow(mix.WavePath, begin, 4), begin);
                    mixMarkers.Add(rendered);
                    if (!rendered.Found || Math.Abs(rendered.StartSeconds - marker.Remote.StartSeconds) > .02) errors.Add("rendered_marker_time_differs_over_20ms");
                }
                if (remoteOnly)
                {
                    if (mix.CompressedPath is null) errors.Add("full_M4A_not_generated");
                    else
                    {
                        compressedFull = CheckCompressed(mix.CompressedPath, 0, markerResults.Select(marker => marker.Remote.StartSeconds).ToArray(), rendering.Token);
                        CheckDecodedMarkers(compressedFull, errors);
                        if (compressedFull.Sha256 != mix.CompressedSha256) errors.Add("full_M4A_hash_changed");
                        if (compressedFull.DecodedFrames < mix.FrameCount || compressedFull.DecodedFrames > mix.FrameCount + 3072) errors.Add("full_M4A_length_incomplete");
                    }
                    exported = M2ExportService.ExportParts(mix, new ExportOptions(M2ExportFormat.M4a, 20_000_000, longRun ? 1800 : 12, 5, true),
                        Path.Combine(run, "exports"), rendering.Token);
                    if (exported.Parts[0].StartFrame != 0 || exported.Parts[^1].EndFrame != mix.FrameCount) errors.Add("export_timeline_not_complete");
                    for (int index = 0; index < exported.Parts.Count; index++)
                    {
                        var part = exported.Parts[index];
                        if (new FileInfo(part.Path).Length != part.ByteLength || part.ByteLength > 20_000_000 || Hash(part.Path) != part.Sha256) errors.Add("export_bytes_or_hash_invalid");
                        if (index > 0 && part.StartFrame != exported.Parts[index - 1].EndFrame - 5 * Rate) errors.Add("export_overlap_invalid");
                        var expectedMarkers = markerResults.Select(marker => marker.Remote.StartSeconds)
                            .Where(time => time >= part.StartUs / 1_000_000d + 1 && time + 2 <= part.EndUs / 1_000_000d).ToArray();
                        var check = CheckCompressed(part.Path, part.StartUs / 1_000_000d, expectedMarkers, rendering.Token);
                        compressedParts.Add(check); CheckDecodedMarkers(check, errors);
                        if (check.Sha256 != part.Sha256) errors.Add("export_hash_changed_during_decode");
                        long contentFrames = part.EndFrame - part.StartFrame;
                        if (check.DecodedFrames < contentFrames || check.DecodedFrames > contentFrames + 3072) errors.Add("export_decoded_tail_length_incomplete");
                    }
                    double tailTime = markerResults[^1].Remote.StartSeconds;
                    if (!compressedParts.Any(part => part.Markers.Any(marker => marker.Found && Math.Abs(marker.StartSeconds - tailTime) < .03)))
                        errors.Add("final_short_marker_missing_from_decoded_export");
                }
            }
            originalsUnchanged = hashes.All(pair => Hash(Path.Combine(directory, pair.Key)) == pair.Value);
            if (!originalsUnchanged) errors.Add("original_hash_changed");
        }
        catch (Exception ex)
        {
            errors.Add(ex.GetType().Name + ": " + ex.Message);
            File.WriteAllText(Path.Combine(run, "failure.txt"), ex.ToString());
        }
        finally
        {
            try { await client.DisposeAsync(); hostExited = true; } catch (Exception ex) { errors.Add("host_shutdown:" + ex.GetType().Name); }
            if (producer is not null)
                try { producerExit = await producer.StopAsync(); producerFailure = producer.FailureDiagnostic; } catch (Exception ex) { errors.Add("producer_shutdown:" + ex.GetType().Name); }
        }
        if (!hostExited || !owned || producerExit != 0) errors.Add("owned_process_lifecycle_incomplete");
        var delays = markerResults.Where(marker => marker.DelaySeconds.HasValue).Select(marker => marker.DelaySeconds!.Value).ToArray();
        string status = errors.Count == 0 ? remoteOnly ? longRun ? "PASS_TWO_HOUR_REMOTE_ENDURANCE" : "PASS_SHORT_REMOTE_ENDURANCE"
            : longRun ? "PASS_TWO_HOUR_ACOUSTIC_SYNC" : "PASS_ACOUSTIC_MARKER_SHORT" : acousticBlocked ? "BLOCKED_ACOUSTIC_PATH" : "FAIL";
        var report = new
        {
            Schema = "m2-long-run-acoustic-probe-1", Status = status, FinishedAtUtc = DateTimeOffset.UtcNow,
            IsLongRun = longRun, RemoteOnly = remoteOnly, RequestedSeconds = seconds, MicrophoneId = microphone?.Id, MicrophoneName = microphone?.Name,
            Os = Environment.OSVersion.Version.ToString(), Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            Mode = remoteOnly ? "ProcessTree/test-owned synthetic source; microphone never enabled" : "ProcessTree/test-owned source + explicitly fixed microphone", PeakAmplitude = ProbeToneSource.PeakAmplitude,
            TwoDeviceSynchronization = remoteOnly ? "NOT_RUN_MICROPHONE_DISABLED" : longRun ? "SEE_ACOUSTIC_MARKERS" : "SHORT_QUALIFICATION_ONLY",
            ProducerOutput = producerOutput,
            ProducerFailure = producerFailure,
            ExplicitTestOutputDeviceId = requestedOutputDevice,
            OutputVolumeOrRouteChanged = false, EndpointFallback = false, SpeechInterpreted = false, ReplayedMicrophone = false, Uploaded = false,
            AcousticPath = new { Method = "Four ordered frequency bursts at fixed known programme times; 20ms analysis windows / 5ms hops. Delay is measured from actual remote and microphone PCM on API-QPC timelines.",
                InitialDelaySeconds = delays.Length == 0 ? (double?)null : delays[0],
                MaximumDelayChangeSeconds = delays.Length == 0 ? (double?)null : delays.Max(value => Math.Abs(value - delays[0])),
                AcceptanceLimitSeconds = .1, MeasurementResolutionSeconds = .005,
                Limitation = "Measured delay includes render endpoint, acoustic propagation and microphone processing. No path-delay correction is inferred from network latency. Stable path is required; no physical device movement is controlled." },
            Markers = markerResults, RenderedMarkers = mixMarkers,
            RemoteProgrammeVsQpcSeconds = markerResults.Count < 2 || markerResults.Any(marker => !marker.Remote.Found) ? (double?)null :
                markerResults[^1].Remote.StartSeconds - markerResults[0].Remote.StartSeconds - (markerResults[^1].ProgrammeSecond - markerResults[0].ProgrammeSecond),
            Storage = new { Result = result, OfflineCleanEnd = recovery?.IsCleanEnd, Issues = recovery?.Issues, OriginalHashes = hashes, OriginalsUnchanged = originalsUnchanged },
            Render = mix, Export = exported, CompressedFull = compressedFull, CompressedParts = compressedParts,
            HealthEvents = Interlocked.Read(ref healthCount), LastHealth = latestHealth, Events = events.ToArray(),
            Lifecycle = new { HostExited = hostExited, ProducerExitCode = producerExit, OwnedSelection = owned }, Errors = errors,
            NotEstablished = new[] { "Human listening", "Hardware unplug", "Lock/sleep behavior", "Platform/browser compatibility", "Private speech meaning" }
        };
        string reportPath = Path.Combine(run, "probe-report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, Json));
        Console.WriteLine(status + "; report: " + reportPath);
        return errors.Count == 0 ? 0 : acousticBlocked ? 3 : 1;
    }

    private static CompressedCheck CheckCompressed(string path, double fileStartSeconds, double[] markerTimes, CancellationToken token)
    {
        MediaFoundationApi.Startup();
        using var input = new MediaFoundationReader(path);
        if (input.WaveFormat.SampleRate != Rate || input.WaveFormat.Channels != 1 || input.WaveFormat.BitsPerSample != 16)
            throw new InvalidDataException("Unexpected compressed decode format.");
        var windows = markerTimes.Select(time => new
        {
            StartFrame = Math.Max(0, (long)Math.Round((time - fileStartSeconds - 1) * Rate)), Samples = new float[Rate * 4]
        }).ToArray();
        long frames = 0; var buffer = new byte[32768];
        while (true)
        {
            token.ThrowIfCancellationRequested(); int bytes = input.Read(buffer, 0, buffer.Length);
            if (bytes == 0) break;
            if (bytes % 2 != 0) throw new InvalidDataException("Incomplete decoded PCM frame.");
            int count = bytes / 2;
            foreach (var window in windows)
            {
                long first = Math.Max(frames, window.StartFrame), end = Math.Min(frames + count, window.StartFrame + window.Samples.Length);
                for (long frame = first; frame < end; frame++)
                    window.Samples[frame - window.StartFrame] = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(checked((int)(frame - frames) * 2), 2)) / 32768f;
            }
            frames += count;
        }
        var markers = windows.Select(window => Detect(window.Samples, fileStartSeconds + window.StartFrame / (double)Rate)).ToArray();
        return new(Path.GetFileName(path), frames, Hash(path), markerTimes, markers);
    }

    private static void CheckDecodedMarkers(CompressedCheck check, List<string> errors)
    {
        for (int index = 0; index < check.ExpectedSessionSeconds.Count; index++)
            if (!check.Markers[index].Found || Math.Abs(check.Markers[index].StartSeconds - check.ExpectedSessionSeconds[index]) > .03)
                errors.Add("compressed_marker_missing_or_shifted:" + check.FileName + ":" + index);
    }

    private static void CheckContinuity(CaptureResult capture, List<string> errors)
    {
        foreach (string line in File.ReadLines(capture.IndexPath))
        {
            using var row = JsonDocument.Parse(line); var data = row.RootElement.GetProperty("data");
            if (data.GetProperty("type").GetString() != "packet") continue;
            if (data.TryGetProperty("gapBefore100ns", out var gap) && ulong.Parse(gap.GetString()!, CultureInfo.InvariantCulture) > 0)
            { errors.Add("reported_audio_gap"); break; }
        }
        foreach (var track in capture.Tracks)
            if (track.Packets.Any(packet => !packet.TimestampValid)) errors.Add("invalid_QPC:" + track.SourceId);
    }

    private static float[] ReadWindow(CaptureResult capture, string source, double start, double seconds)
    {
        int frames = checked((int)Math.Round(seconds * Rate)); long first = (long)Math.Round(start * Rate);
        var output = new float[frames];
        foreach (var track in capture.Tracks.Where(track => track.SourceId == source))
        {
            if (track.Format.SampleRate != Rate) throw new InvalidDataException("Synchronization qualification requires the verified fixed 48kHz combination.");
            using var wav = new WaveFileReader(track.WavePath);
            foreach (var packet in track.Packets)
            {
                long packetStart = AudioClock.FrameAt(packet.QpcPosition100ns, capture.EpochQpc100ns, Rate) + packet.RetainedFrameOffset;
                long begin = Math.Max(first, packetStart), end = Math.Min(first + frames, packetStart + packet.FrameCount);
                if (end <= begin) continue;
                int skip = checked((int)(begin - packetStart)), count = checked((int)(end - begin));
                wav.Position = checked((packet.WaveFrameOffset + skip) * track.Format.BlockAlign);
                byte[] pcm = new byte[checked(count * track.Format.BlockAlign)]; wav.ReadExactly(pcm);
                for (int frame = 0; frame < count; frame++) output[begin - first + frame] = Mono(pcm.AsSpan(frame * track.Format.BlockAlign, track.Format.BlockAlign), track.Format);
            }
        }
        return output;
    }

    private static float[] ReadWaveWindow(string path, double start, double seconds)
    {
        using var wav = new WaveFileReader(path);
        var format = CaptureSession.Describe(wav.WaveFormat);
        if (format.SampleRate != Rate) throw new InvalidDataException("Unexpected mix rate.");
        var samples = new float[(int)Math.Round(seconds * Rate)];
        wav.Position = Math.Min(wav.Length, checked((long)Math.Round(start * Rate) * format.BlockAlign));
        var bytes = new byte[samples.Length * format.BlockAlign]; int read = 0;
        while (read < bytes.Length) { int count = wav.Read(bytes, read, bytes.Length - read); if (count == 0) break; read += count; }
        for (int frame = 0; frame < read / format.BlockAlign; frame++) samples[frame] = Mono(bytes.AsSpan(frame * format.BlockAlign, format.BlockAlign), format);
        return samples;
    }

    private static float Mono(ReadOnlySpan<byte> bytes, PcmFormat format)
    {
        int width = format.BitsPerSample / 8; double result = 0;
        for (int channel = 0; channel < format.Channels; channel++)
        {
            var value = bytes.Slice(channel * width, width);
            result += format.IsFloat ? BinaryPrimitives.ReadSingleLittleEndian(value) : width switch
            { 2 => BinaryPrimitives.ReadInt16LittleEndian(value) / 32768d,
                3 => ((value[0] | value[1] << 8 | value[2] << 16) << 8 >> 8) / 8388608d,
                4 => BinaryPrimitives.ReadInt32LittleEndian(value) / 2147483648d, _ => throw new InvalidDataException("Unknown PCM.") };
        }
        if (!double.IsFinite(result)) throw new InvalidDataException("Nonfinite PCM.");
        return (float)(result / format.Channels);
    }

    private static MarkerDetection Detect(float[] samples, double beginSeconds)
    {
        int count = Math.Max(0, (samples.Length - Window) / Hop + 1);
        var energy = new double[Frequencies.Length, count];
        for (int tone = 0; tone < Frequencies.Length; tone++)
        {
            var sine = new double[Window]; var cosine = new double[Window]; double weights = 0;
            for (int frame = 0; frame < Window; frame++)
            {
                double window = .5 - .5 * Math.Cos(2 * Math.PI * frame / (Window - 1)); weights += window;
                sine[frame] = window * Math.Sin(2 * Math.PI * Frequencies[tone] * frame / Rate);
                cosine[frame] = window * Math.Cos(2 * Math.PI * Frequencies[tone] * frame / Rate);
            }
            for (int step = 0; step < count; step++)
            {
                double a = 0, b = 0;
                for (int frame = 0; frame < Window; frame++) { a += samples[step * Hop + frame] * sine[frame]; b += samples[step * Hop + frame] * cosine[frame]; }
                energy[tone, step] = 2 * Math.Sqrt(a * a + b * b) / weights;
            }
        }
        double best = 0; int selected = -1; const int slotSteps = 60, middleSteps = 16, gapSteps = 48;
        for (int step = 0; step + slotSteps * 3 + gapSteps < count; step++)
        {
            double score = 1;
            for (int tone = 0; tone < Frequencies.Length; tone++)
            {
                int on = step + tone * slotSteps + middleSteps, off = step + tone * slotSteps + gapSteps;
                double wanted = energy[tone, on]; double unwanted = energy[tone, off];
                for (int other = 0; other < Frequencies.Length; other++) if (other != tone) unwanted = Math.Max(unwanted, energy[other, on]);
                if (wanted < .00003 || wanted < unwanted * 5) { score = 0; break; }
                score *= Math.Min(1000, wanted / Math.Max(.000003, unwanted));
            }
            if (score > best) { best = score; selected = step; }
        }
        if (selected < 0)
        {
            var observedPeaks = new List<double>();
            for (int tone = 0; tone < Frequencies.Length; tone++)
            {
                double peak = 0; for (int step = 0; step < count; step++) peak = Math.Max(peak, energy[tone, step]);
                observedPeaks.Add(peak);
            }
            return new(false, 0, 0, 0, observedPeaks);
        }
        var starts = new List<double>(); var peaks = new List<double>();
        for (int tone = 0; tone < Frequencies.Length; tone++)
        {
            int middle = selected + tone * slotSteps + middleSteps;
            int low = Math.Max(0, middle - 35), high = Math.Min(count - 1, middle + 20);
            double peak = 0; for (int step = low; step <= high; step++) peak = Math.Max(peak, energy[tone, step]);
            peaks.Add(peak); int edge = middle;
            while (edge > low && energy[tone, edge - 1] >= peak * .25) edge--;
            starts.Add(beginSeconds + (edge * Hop + Window / 2d) / Rate - tone * .3);
        }
        double start = starts.Order().ElementAt(starts.Count / 2);
        double spread = starts.Max() - starts.Min();
        return new(spread <= .035, start, Math.Pow(best, .25), spread, peaks);
    }

    internal static void RunSyntheticChecks()
    {
        foreach (int position in StressMarkers)
        {
            var stressWindow = new float[Rate * 4];
            for (int frame = 0; frame < stressWindow.Length; frame++)
                stressWindow[frame] = StressMarkerSample((position - 1) * Rate + frame);
            var stressMarker = Detect(stressWindow, position - 1);
            if (!stressMarker.Found || Math.Abs(stressMarker.StartSeconds - position) > .02)
                throw new InvalidDataException("CPU stress programme marker missing or shifted.");
        }
        foreach (int position in MediumMarkers)
        {
            var mediumWindow = new float[Rate * 4];
            for (int frame = 0; frame < mediumWindow.Length; frame++)
                mediumWindow[frame] = MediumMarkerSample((position - 1) * Rate + frame);
            var mediumMarker = Detect(mediumWindow, position - 1);
            if (!mediumMarker.Found || Math.Abs(mediumMarker.StartSeconds - position) > .02)
                throw new InvalidDataException("Twenty-minute programme marker missing or shifted.");
        }
        foreach (double offset in new[] { 0, .073, .225 })
        {
            var samples = new float[Rate * 4]; var noise = new Random(2948);
            int shift = (int)Math.Round(offset * Rate);
            for (int i = 0; i < samples.Length; i++) samples[i] = .1f * MarkerSample(i - shift + Rate, false) + (float)((noise.NextDouble() - .5) * .000002);
            var marker = Detect(samples, 0);
            if (!marker.Found || Math.Abs(marker.StartSeconds - (1 + offset)) > .02) throw new InvalidDataException("Marker detector missed a synthetic delayed four-tone marker.");
        }
        var silence = Detect(new float[Rate * 4], 0);
        if (silence.Found) throw new InvalidDataException("Silence qualified as an acoustic marker.");
        var single = new float[Rate * 4];
        for (int i = 0; i < single.Length; i++) single[i] = (float)(.01 * Math.Sin(2 * Math.PI * 733 * i / Rate));
        if (Detect(single, 0).Found) throw new InvalidDataException("Single unrelated tone qualified as four ordered markers.");
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static ulong Now100ns() => (ulong)AudioClock.CounterTo100ns(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
    private sealed record MarkerDetection(bool Found, double StartSeconds, double Contrast, double EdgeSpreadSeconds, IReadOnlyList<double> TonePeaks);
    private sealed record MarkerComparison(int ProgrammeSecond, MarkerDetection Remote, MarkerDetection Microphone, double? DelaySeconds);
    private sealed record CompressedCheck(string FileName, long DecodedFrames, string Sha256, IReadOnlyList<double> ExpectedSessionSeconds, IReadOnlyList<MarkerDetection> Markers);
}
