using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using ProjectEcho.App;
using ProjectEcho.Capture.Windows;
using ProjectEcho.CaptureHost;
using ProjectEcho.Core;
using ProjectEcho.Export;

/// <summary>Explicit microphone endurance; deliberately makes no acoustic synchronization claim.</summary>
internal static class M3cMicrophoneEnduranceProbe
{
    internal static Task<int> RunAsync(string[] args) => M2LongRunProbe.RunMicrophoneEnduranceAsync(args);
}

internal static partial class M2LongRunProbe
{
    internal static async Task<int> RunMicrophoneEnduranceAsync(string[] args)
    {
        if (args.Length is < 3 or > 5 || args[0] != "--m3c-microphone-endurance-probe" ||
            string.IsNullOrWhiteSpace(args[1]) || args[2] is not ("short" or "medium" or "long")) return 2;
        bool longRun = args[2] == "long";
        bool mediumRun = args[2] == "medium", extendedRun = mediumRun || longRun;
        if ((!extendedRun && args.Length > 4) || (extendedRun && args.Length < 4)) return 2;
        string root = SyntheticAudioProbe.FindProjectRoot();
        string? outputDevice = extendedRun ? args.Length == 5 ? args[4] : null : args.Length == 4 ? args[3] : null;
        string? shortOutput = null;
        if (extendedRun)
        {
            // This qualifies actual storage/clock/codec behavior for this fixed mic, not an acoustic path.
            string proofPath = SyntheticAudioProbe.SafeProjectPath(root, args[3]);
            using var proof = JsonDocument.Parse(File.ReadAllBytes(proofPath));
            var p = proof.RootElement;
            if (p.GetProperty("Schema").GetString() != "m3c-microphone-endurance-1" ||
                !p.TryGetProperty("EligibleForLongRun", out var eligible) || !eligible.GetBoolean() ||
                p.GetProperty("IsLongRun").GetBoolean() || p.GetProperty("RequestedSeconds").GetInt32() != 40 ||
                p.GetProperty("MicrophoneId").GetString() != args[1] ||
                DateTimeOffset.UtcNow - p.GetProperty("FinishedAtUtc").GetDateTimeOffset() > TimeSpan.FromHours(12))
                throw new InvalidDataException("Long microphone endurance requires a recent eligible short readiness report for this exact microphone; source quality remains separate.");
            shortOutput = p.GetProperty("ProducerOutput").GetProperty("OutputDeviceId").GetString();
            outputDevice ??= shortOutput;
        }
        string parent = SyntheticAudioProbe.SafeProjectPath(root, ".local/M3cMicrophoneEndurance");
        Directory.CreateDirectory(parent);
        string lockPath = Path.Combine(root, ".local", "recording-probe.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Probe lock is a reparse point.");
        using var runLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var existing = Process.GetProcessesByName("ProjectEcho.CaptureHost");
        try { if (existing.Length != 0) throw new IOException("Existing CaptureHost found; probe refused."); }
        finally { foreach (var process in existing) process.Dispose(); }
        var microphone = SourceDiscovery.Enumerate().Microphones.SingleOrDefault(device => device.Id == args[1])
            ?? throw new IOException("Explicit microphone is unavailable; no fallback attempted.");
        string run = Path.Combine(parent, args[2] + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "REAL_MICROPHONE_LOCAL_ONLY"),
            "Explicitly authorized fixed microphone PCM and newly created synthetic process only. Local originals retained. No microphone replay, speech interpretation, ASR or upload. NOT acoustic synchronization acceptance.");
        File.WriteAllText(Path.Combine(run, "execution-payload.json"), JsonSerializer.Serialize(new
        {
            Os = Environment.OSVersion.Version.ToString(), Runtime = Environment.Version.ToString(),
            Assemblies = new[] { typeof(M3cMicrophoneEnduranceProbe).Assembly, typeof(RecordingOptions).Assembly,
                typeof(AudioClock).Assembly, typeof(M2ExportService).Assembly, typeof(WaveFileReader).Assembly }
                .Distinct().Select(assembly => new { assembly.FullName, assembly.Location, Sha256 = Hash(assembly.Location) }).ToArray(),
            CaptureHost = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "CaptureHost"), "ProjectEcho.*")
                .Select(path => new { Path = path, Sha256 = Hash(path) }).ToArray()
        }, Json));
        Console.WriteLine("Microphone endurance evidence: " + run);
        int seconds = longRun ? LongSeconds : mediumRun ? MediumSeconds : 40;
        int budgetMinutes = longRun ? 122 : mediumRun ? 22 : 2;
        int partMaximumSeconds = longRun ? 1800 : mediumRun ? 600 : 20;
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
            if (value.Health is { } health)
            {
                Volatile.Write(ref latestHealth, health); Interlocked.Increment(ref healthCount);
                if (health.Paused || !health.MicrophoneEnabled || health.MicStatus.Contains("来源故障", StringComparison.Ordinal) ||
                    health.RemoteStatus.Contains("来源故障", StringComparison.Ordinal)) sourceFault.TrySetResult("capture_source_paused_disabled_or_faulted");
            }
            else events.Enqueue(new { value.Kind, value.RequestId, value.Message, value.Ack });
            if (value.Kind is "recording_saved" or "recording_incomplete" or "error")
            { terminal.TrySetResult(value); started.TrySetException(new IOException("Host ended before startup acknowledgement.")); }
        };
        SyntheticAudioProbe.Producer? producer = null;
        JsonElement? producerOutput = null;
        RecordingResult? result = null;
        M1RecoveryResult? recovery = null;
        FullMixResult? mix = null;
        ExportResult? exported = null;
        CompressedCheck? fullMp3 = null;
        var parts = new List<CompressedCheck>();
        var markers = new List<MarkerDetection>();
        var mixedMarkers = new List<MarkerDetection>();
        var codecComparisons = new List<object>();
        var observations = new List<string>();
        var hashes = new Dictionary<string, string>();
        var errors = new List<string>();
        var qualityErrors = new List<string>();
        var declaredGaps = new List<EnduranceGap>();
        object? tracksEvidence = null;
        object? preflightEvidence = null;
        bool owned = false, hostExited = false, originalsUnchanged = false;
        int? producerExit = null;
        ulong goQpc = 0;
        string recording = Path.Combine(run, "recording");
        DateTimeOffset? captureStartedAt = null, captureFinishedAt = null;
        try
        {
            // Sparse 2%-amplitude marker bursts; no system volume/routing change. Short mode stops the programme at 40 seconds.
            producer = await SyntheticAudioProbe.Producer.StartAsync(root, mediumRun ? "target-sync-medium" : "target-sync-long", deadline.Token, outputDevice);
            producerOutput = producer.ReadyInfo;
            if (extendedRun && (string.IsNullOrEmpty(shortOutput) || producerOutput?.GetProperty("OutputDeviceId").GetString() != shortOutput))
                throw new IOException("Output endpoint differs from short qualification; no microphone recording started.");
            var selection = await SyntheticAudioProbe.FindOwnedSourceAsync(producer, deadline.Token);
            owned = SyntheticAudioProbe.IsOwnedSelection(selection, producer.Identity);
            if (!owned) throw new IOException("Remote source is not the newly created owned process.");
            var options = SyntheticAudioProbe.CreateOptions(selection, recording) with
            { MicrophoneEnabled = true, MicrophoneDeviceId = microphone.Id, Title = "固定麦克风与受控进程耐久测试（仅本地）" };
            var preflight = SourceDiscovery.Preflight(options);
            long reservedBytes = checked(preflight.EstimatedBytesPerSecond * budgetMinutes * 60 * 4 + 64L * 1024 * 1024);
            preflightEvidence = new { preflight.CanStart, preflight.Errors, preflight.Warnings, preflight.EstimatedBytesPerSecond,
                preflight.AvailableBytes, ReservedBytes = reservedBytes, BudgetMinutes = budgetMinutes };
            if (!preflight.CanStart || preflight.AvailableBytes < reservedBytes) throw new IOException("Source or disk budget preflight failed.");
            File.WriteAllText(Path.Combine(run, "preflight.json"), JsonSerializer.Serialize(preflightEvidence, Json));
            await client.StartRecordingAsync(new RecordingOptions(options, seconds + 30, budgetMinutes), root, () => deadline.IsCancellationRequested);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(12), deadline.Token);
            captureStartedAt = DateTimeOffset.UtcNow;
            goQpc = Now100ns();
            await producer.GoAsync(deadline.Token);
            var timer = Stopwatch.StartNew();
            Task done = extendedRun ? producer.WaitDoneAsync(deadline.Token) : Task.Delay(TimeSpan.FromSeconds(seconds), deadline.Token);
            while (!done.IsCompleted)
            {
                var tick = Task.Delay(TimeSpan.FromSeconds(extendedRun ? 30 : 10), deadline.Token);
                var finished = await Task.WhenAny(done, terminal.Task, sourceFault.Task, tick);
                if (finished == terminal.Task) throw new IOException("Host ended before the requested endurance duration.");
                if (finished == sourceFault.Task) throw new IOException(await sourceFault.Task);
                deadline.Token.ThrowIfCancellationRequested();
                if (finished == tick)
                {
                    var health = Volatile.Read(ref latestHealth);
                    File.WriteAllText(Path.Combine(run, "progress.json"), JsonSerializer.Serialize(new
                    { Status = "RECORDING", ElapsedSeconds = timer.Elapsed.TotalSeconds, RequestedSeconds = seconds,
                        HealthEvents = Interlocked.Read(ref healthCount), Health = health, UpdatedAtUtc = DateTimeOffset.UtcNow }, Json));
                    Console.WriteLine($"Recording {timer.Elapsed.TotalSeconds:F0}/{seconds}s; remote={health?.RemoteFrames}; mic={health?.MicFrames}; free={health?.AvailableBytes}");
                }
            }
            await done;
            if (extendedRun && timer.Elapsed.TotalSeconds < seconds + 1)
                await Task.Delay(TimeSpan.FromSeconds(seconds + 1 - timer.Elapsed.TotalSeconds), deadline.Token);
            await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
            var final = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(60), deadline.Token);
            await client.WaitForExitAsync(); hostExited = true; captureFinishedAt = DateTimeOffset.UtcNow;
            producerExit = await producer.StopAsync(); producer = null;
            result = final.RecordingResult ?? throw new IOException("Host returned no recording result.");
            foreach (string file in Directory.EnumerateFiles(recording, "*", SearchOption.AllDirectories))
                hashes.Add(Path.GetRelativePath(recording, file), Hash(file));
            recovery = M1Recovery.Read(recording);
            if (!result.StorageComplete || !recovery.IsCleanEnd || recovery.Issues.Count != 0) errors.Add("storage_or_offline_recovery_incomplete");
            if (result.DurationUs < seconds * 1_000_000L || result.DurationUs > (seconds + 15L) * 1_000_000) errors.Add("elapsed_duration_outside_test_limit");
            if (Directory.EnumerateFiles(recording, "*.partial", SearchOption.AllDirectories).Any()) errors.Add("unfinalized_original_file");
            InspectEnduranceContinuity(recovery.Capture, declaredGaps, qualityErrors, errors);
            var sourceReports = new List<object>();
            foreach (string source in new[] { "remote", "mic" })
            {
                var tracks = recovery.Capture.Tracks.Where(track => track.SourceId == source).ToArray();
                var packets = tracks.SelectMany(track => track.Packets).ToArray();
                double recordedSeconds = tracks.Sum(track => track.FrameCount / (double)track.Format.SampleRate);
                if (tracks.Length < 2 || packets.Length == 0 || recordedSeconds < seconds - 1 || recordedSeconds > seconds + 15) errors.Add("source_duration_or_cross_chunk_coverage:" + source);
                if (tracks.Select(track => track.SegmentId).Distinct().Count() != 1) errors.Add("unexpected_source_restart:" + source);
                if (packets.Any(packet => !packet.TimestampValid || (source == "mic" && !packet.DevicePositionValid))) errors.Add("source_clock_invalid:" + source);
                foreach (var track in tracks)
                {
                    track.Format.Validate();
                    using var reader = new WaveFileReader(track.WavePath);
                    if (reader.Length != track.FrameCount * track.Format.BlockAlign || track.Packets.Sum(packet => (long)packet.FrameCount) != track.FrameCount)
                        errors.Add("source_wave_or_packet_frame_count:" + source);
                }
                sourceReports.Add(new { Source = source, Chunks = tracks.Length, Frames = tracks.Sum(track => track.FrameCount),
                    RecordedSeconds = recordedSeconds, Packets = packets.Length, Formats = tracks.Select(track => track.Format).Distinct().ToArray(),
                    ValidQpcPackets = packets.Count(packet => packet.TimestampValid), ValidDeviceCounterPackets = packets.Count(packet => packet.DevicePositionValid),
                    BoundedQpcJitterPackets = packets.Count(packet => packet.ClockJitter100ns != 0) });
            }
            tracksEvidence = sourceReports;
            double goSession = (double)((Int128)goQpc - recovery.Capture.EpochQpc100ns) / AudioClock.UnitsPerSecond;
            foreach (int marker in longRun ? LongMarkers : mediumRun ? MediumMarkers : new[] { 2 })
            {
                double begin = Math.Max(0, goSession + marker - 1);
                var detection = Detect(ReadWindow(recovery.Capture, "remote", begin, 4), begin);
                markers.Add(detection);
                if (!detection.Found) errors.Add("owned_remote_marker_missing:" + marker);
            }
            if (errors.Count == 0)
            {
                using var offline = new CancellationTokenSource(TimeSpan.FromMinutes(longRun ? 70 : mediumRun ? 20 : 5));
                File.WriteAllText(Path.Combine(run, "progress.json"), JsonSerializer.Serialize(new
                { Status = "OFFLINE_RENDERING_AND_MP3", HostExited = hostExited, ProducerExitCode = producerExit, UpdatedAtUtc = DateTimeOffset.UtcNow }, Json));
                mix = M2ExportService.RenderMeeting(recording, new MediaFoundationMp3Encoder(), offline.Token);
                if (mix.CompressedPath is null || !mix.CompressedPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) errors.Add("full_MP3_not_generated");
                foreach (var marker in markers)
                {
                    var detection = Detect(ReadWaveWindow(mix.WavePath, Math.Max(0, marker.StartSeconds - 1), 4), Math.Max(0, marker.StartSeconds - 1));
                    mixedMarkers.Add(detection);
                    // Concurrent microphone sound or acoustic echo can obscure/shift a marker in the true mono mix.
                    // The source marker remains mandatory; codec completeness is compared against the actual mixed waveform below.
                    if (!detection.Found || Math.Abs(detection.StartSeconds - marker.StartSeconds) > .02)
                        observations.Add("mixed_remote_marker_obscured_or_shifted_not_evidence_of_missing_original:" + marker.StartSeconds);
                }
                if (mix.CompressedPath is not null)
                {
                    using var renderManifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(mix.Directory, "render.json")));
                    var compressed = renderManifest.RootElement.GetProperty("compressed");
                    long delay = compressed.GetProperty("encoderDelayFrames").GetInt64(), padding = compressed.GetProperty("trailingPaddingFrames").GetInt64();
                    fullMp3 = CheckCompressed(mix.CompressedPath, -delay / (double)Rate, mixedMarkers.Where(marker => marker.Found).Select(marker => marker.StartSeconds).ToArray(), offline.Token);
                    CheckDecodedMarkers(fullMp3, observations);
                    if (fullMp3.DecodedFrames != mix.FrameCount + delay + padding || fullMp3.Sha256 != mix.CompressedSha256) errors.Add("full_MP3_EOF_or_hash_invalid");
                    var content = CompareDecodedMixWindows(mix.WavePath, mix.CompressedPath, 0, mix.FrameCount, delay,
                        markers.Select(marker => (long)Math.Round(Math.Max(0, marker.StartSeconds - 1) * Rate)), offline.Token);
                    codecComparisons.Add(new { File = Path.GetFileName(mix.CompressedPath), Windows = content });
                    if (content.Any(window => !window.Pass)) errors.Add("full_MP3_waveform_content_not_preserved");
                }
                exported = M2ExportService.ExportParts(mix, new(M2ExportFormat.Mp3, 20_000_000, partMaximumSeconds, 5, true), Path.Combine(run, "exports"), offline.Token);
                if (exported.Parts[0].StartFrame != 0 || exported.Parts[^1].EndFrame != mix.FrameCount) errors.Add("MP3_export_timeline_incomplete");
                for (int index = 0; index < exported.Parts.Count; index++)
                {
                    var part = exported.Parts[index];
                    if (new FileInfo(part.Path).Length != part.ByteLength || part.ByteLength > 20_000_000 || part.Sha256 != Hash(part.Path)) errors.Add("MP3_export_size_or_hash_invalid");
                    if (index > 0 && part.StartFrame != exported.Parts[index - 1].EndFrame - 5 * Rate) errors.Add("MP3_export_overlap_invalid");
                    var expected = mixedMarkers.Where(marker => marker.Found).Select(marker => marker.StartSeconds).Where(time => time >= part.StartUs / 1_000_000d + 1 && time + 2 <= part.EndUs / 1_000_000d).ToArray();
                    var decoded = CheckCompressed(part.Path, part.StartUs / 1_000_000d - part.EncoderDelayFrames / (double)Rate, expected, offline.Token);
                    parts.Add(decoded); CheckDecodedMarkers(decoded, observations);
                    if (decoded.DecodedFrames != part.EndFrame - part.StartFrame + part.EncoderDelayFrames + part.TrailingPaddingFrames) errors.Add("MP3_part_EOF_incomplete");
                    if (decoded.DecodedFrames / (double)Rate > partMaximumSeconds) errors.Add("MP3_part_duration_exceeds_limit");
                    var content = CompareDecodedMixWindows(mix.WavePath, part.Path, part.StartFrame, part.EndFrame, part.EncoderDelayFrames,
                        markers.Select(marker => (long)Math.Round(Math.Max(0, marker.StartSeconds - 1) * Rate)), offline.Token);
                    codecComparisons.Add(new { File = Path.GetFileName(part.Path), Windows = content });
                    if (content.Any(window => !window.Pass)) errors.Add("MP3_part_waveform_content_not_preserved:" + index);
                }
            }
            originalsUnchanged = hashes.All(pair => Hash(Path.Combine(recording, pair.Key)) == pair.Value);
            if (!originalsUnchanged) errors.Add("originals_changed_during_offline_validation");
        }
        catch (Exception ex)
        { errors.Add(ex.GetType().Name + ": " + ex.Message); File.WriteAllText(Path.Combine(run, "failure.txt"), ex.ToString()); }
        finally
        {
            try { await client.DisposeAsync(); hostExited = true; } catch (Exception ex) { errors.Add("host_shutdown:" + ex.GetType().Name); }
            if (producer is not null)
                try { producerExit = await producer.StopAsync(); } catch (Exception ex) { errors.Add("producer_shutdown:" + ex.GetType().Name); }
        }
        if (!owned || !hostExited || producerExit != 0) errors.Add("owned_process_lifecycle_incomplete");
        if (result is null && terminal.Task.IsCompletedSuccessfully) result = terminal.Task.Result.RecordingResult;
        bool codecComplete = mix is not null && fullMp3 is not null && exported is not null && parts.Count == exported.Parts.Count;
        bool storageComplete = result?.StorageComplete == true && recovery?.IsCleanEnd == true && recovery.Issues.Count == 0 && originalsUnchanged;
        bool eligibleForLongRun = !extendedRun && errors.Count == 0 && storageComplete && codecComplete;
        string status = errors.Count != 0 ? "FAIL" : qualityErrors.Count != 0 ? "PARTIAL_STARTUP_GAP_STORAGE_CODEC_VALIDATED"
            : longRun ? "PASS_TWO_HOUR_MICROPHONE_ENDURANCE" : mediumRun ? "PASS_TWENTY_MINUTE_MICROPHONE_ENDURANCE" : "PASS_SHORT_MICROPHONE_ENDURANCE";
        var report = new
        {
            Schema = "m3c-microphone-endurance-1", Status = status, DurationMode = args[2], IsLongRun = longRun, RequestedSeconds = seconds,
            EligibleForLongRun = eligibleForLongRun,
            EligibilityReason = eligibleForLongRun ? qualityErrors.Count == 0 ? "storage, clock, boundaries and codec ready"
                : "declared startup gap retained; storage/codec ready, continuity not passed" : "not a successful short readiness qualification",
            QualityStatus = qualityErrors.Count == 0 && errors.Count == 0 ? "PASS_NO_DECLARED_GAPS" : "NOT_PASSED",
            QualityErrors = qualityErrors, DeclaredGaps = declaredGaps,
            Checks = new { Duration = result is not null && result.DurationUs >= seconds * 1_000_000L && result.DurationUs <= (seconds + 15L) * 1_000_000,
                Storage = storageComplete, PersistedBoundaries = recovery?.IsCleanEnd == true && recovery.Issues.Count == 0,
                Codec = codecComplete && !errors.Any(error => error.Contains("MP3", StringComparison.Ordinal)) },
            FinishedAtUtc = DateTimeOffset.UtcNow, CaptureStartedAtUtc = captureStartedAt, CaptureFinishedAtUtc = captureFinishedAt,
            Os = Environment.OSVersion.Version.ToString(), Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            MicrophoneId = microphone.Id, MicrophoneName = microphone.Name, ProducerOutput = producerOutput,
            Mode = "ProcessTree/newly created synthetic process plus explicitly selected fixed microphone", EndpointFallback = false,
            OutputVolumeOrRouteChanged = false, MicrophoneReplayed = false, Transcribed = false, Uploaded = false,
            TwoDeviceSynchronization = "NOT_ESTABLISHED_NO_ACOUSTIC_REFERENCE_REQUIRED_OR_CLAIMED",
            Preflight = preflightEvidence, Sources = tracksEvidence,
            Storage = new { RecordingDirectory = recording, Result = result, OfflineCleanEnd = recovery?.IsCleanEnd, Issues = recovery?.Issues,
                MicrophoneFilesExist = Directory.Exists(Path.Combine(recording, "sources", "mic")), OriginalHashes = hashes, OriginalsUnchanged = originalsUnchanged },
            RemoteMarkers = markers, MixedMarkers = mixedMarkers, Mix = mix, FullMp3 = fullMp3, Export = exported, CompressedParts = parts,
            CodecContentChecks = codecComparisons, Observations = observations,
            HealthEvents = Interlocked.Read(ref healthCount), LastHealth = latestHealth,
            Lifecycle = new { OwnedSelection = owned, HostExited = hostExited, ProducerExitCode = producerExit }, Events = events.ToArray(), Errors = errors.Concat(qualityErrors).ToArray(),
            NotEstablished = new[] { "Acoustic two-device synchronization", "Human listening", "Physical unplug", "Lock/sleep behavior", "Private meeting or browser compatibility", "Speech meaning or ASR accuracy" }
        };
        string reportPath = Path.Combine(run, "probe-report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, Json));
        Console.WriteLine(status + "; report: " + reportPath);
        return errors.Count != 0 ? 1 : qualityErrors.Count != 0 ? 3 : 0;
    }

    private static void InspectEnduranceContinuity(CaptureResult capture, List<EnduranceGap> gaps, List<string> qualityErrors, List<string> errors)
    {
        var packetsPerSegment = new Dictionary<string, int>();
        foreach (string line in File.ReadLines(capture.IndexPath))
        {
            using var row = JsonDocument.Parse(line); var packet = row.RootElement.GetProperty("data");
            if (packet.GetProperty("type").GetString() != "packet") continue;
            string segment = packet.GetProperty("segmentId").GetString()!;
            int ordinal = packetsPerSegment.GetValueOrDefault(segment) + 1; packetsPerSegment[segment] = ordinal;
            if (!packet.TryGetProperty("gapBefore100ns", out var gapValue)) continue;
            ulong gap = ulong.Parse(gapValue.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            if (gap == 0) continue;
            bool startupOnly = ordinal == 2 && gap <= 50_000 && packet.GetProperty("gapReason").GetString() == "unobserved_audio_interval";
            gaps.Add(new(segment, ordinal, packet.GetProperty("sessionStartUs").GetInt64(), gap, startupOnly));
            qualityErrors.Add("reported_audio_gap:" + segment + ":" + gap);
            if (!startupOnly) errors.Add("nonstartup_or_large_declared_gap:" + segment + ":" + gap);
        }
        foreach (var track in capture.Tracks)
            if (track.Packets.Any(packet => !packet.TimestampValid)) errors.Add("invalid_QPC:" + track.SourceId);
    }

    private sealed record EnduranceGap(string Segment, int PacketOrdinal, long SessionUs, ulong Gap100ns, bool StartupOnlyWithin5ms);

    /// <summary>Checks actual mixed content independently of whether environmental mic sound masks a test tone.</summary>
    private static IReadOnlyList<CodecContentWindow> CompareDecodedMixWindows(string wavePath, string compressedPath,
        long startFrame, long endFrame, long delayFrames, IEnumerable<long> markerStarts, CancellationToken token)
    {
        int windowFrames = (int)Math.Min(Rate * 4, endFrame - startFrame);
        if (windowFrames <= 480) throw new InvalidDataException("Codec comparison window is too short.");
        long[] starts = markerStarts.Where(frame => frame >= startFrame && frame + windowFrames <= endFrame)
            .Concat([startFrame, startFrame + (endFrame - startFrame - windowFrames) / 2, endFrame - windowFrames])
            .Distinct().Order().ToArray();
        var windows = starts.Select(frame => new { Frame = frame, DecodedStart = frame - startFrame + delayFrames,
            Reference = ReadWaveWindow(wavePath, frame / (double)Rate, windowFrames / (double)Rate), Decoded = new float[windowFrames] }).ToArray();
        using var decoder = new MediaFoundationReader(compressedPath);
        if (decoder.WaveFormat.SampleRate != Rate || decoder.WaveFormat.Channels != 1 || decoder.WaveFormat.BitsPerSample != 16)
            throw new InvalidDataException("Unexpected MP3 decoder format for content comparison.");
        byte[] buffer = new byte[32768]; long totalFrames = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested(); int bytes = decoder.Read(buffer, 0, buffer.Length);
            if (bytes == 0) break;
            if (bytes % 2 != 0) throw new InvalidDataException("Truncated decoded PCM frame.");
            int count = bytes / 2;
            foreach (var window in windows)
            {
                long first = Math.Max(totalFrames, window.DecodedStart), last = Math.Min(totalFrames + count, window.DecodedStart + windowFrames);
                for (long frame = first; frame < last; frame++)
                    window.Decoded[frame - window.DecodedStart] = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(checked((int)(frame - totalFrames) * 2), 2)) / 32768f;
            }
            totalFrames += count;
        }
        return windows.Select(window =>
        {
            double referenceRms = Math.Sqrt(window.Reference.Sum(sample => (double)sample * sample) / windowFrames);
            double decodedRms = Math.Sqrt(window.Decoded.Sum(sample => (double)sample * sample) / windowFrames);
            double best = 0; int bestLag = 0;
            if (referenceRms > .000001 && decodedRms > .000001)
                for (int lag = -240; lag <= 240; lag += 8)
                {
                    double dot = 0, left = 0, right = 0;
                    for (int frame = 240; frame < windowFrames - 240; frame += 8)
                    {
                        double a = window.Reference[frame], b = window.Decoded[frame + lag];
                        dot += a * b; left += a * a; right += b * b;
                    }
                    double correlation = left > 0 && right > 0 ? dot / Math.Sqrt(left * right) : 0;
                    if (correlation > best) { best = correlation; bestLag = lag; }
                }
            bool complete = totalFrames >= window.DecodedStart + windowFrames;
            bool pass = complete && (referenceRms <= .000001 ? decodedRms <= .0001 : decodedRms > .000001 && best >= .65);
            return new CodecContentWindow(window.Frame, windowFrames, referenceRms, decodedRms, best, bestLag, complete, pass);
        }).ToArray();
    }

    private sealed record CodecContentWindow(long SourceStartFrame, int Frames, double ReferenceRms, double DecodedRms,
        double Correlation, int BestLagFrames, bool Complete, bool Pass);
}
