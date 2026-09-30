using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using ProjectEcho.App;
using ProjectEcho.Capture.Windows;
using ProjectEcho.CaptureHost;
using ProjectEcho.Core;

/// <summary>Explicit fifteen-minute real-Windows digital-silence test, limited to a newly created render-only child.</summary>
internal static class M1SilenceProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1 || args[0] != "--m1-silence-probe") return 2;
        string root = SyntheticAudioProbe.FindProjectRoot();
        string parent = SyntheticAudioProbe.SafeProjectPath(root, ".local/M1SilenceIntegration");
        Directory.CreateDirectory(parent);
        string lockPath = Path.Combine(root, ".local", "recording-probe.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Silence probe lock is a reparse point.");
        using var runLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var existing = Process.GetProcessesByName("ProjectEcho.CaptureHost");
        try { if (existing.Length != 0) { Console.Error.WriteLine("Existing CaptureHost found; silence probe refused."); return 2; } }
        finally { foreach (var process in existing) process.Dispose(); }
        string run = Path.Combine(parent, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "SYNTHETIC_SILENCE_REAL_WASAPI"), "900 seconds of generated digital-zero render PCM; real owned-process capture; no microphone, endpoint fallback, browser or meeting.");
        string recording = Path.Combine(run, "recording"), progressPath = Path.Combine(run, "progress.json");
        Console.WriteLine("M1 15-minute silence probe: newly created process only; microphone OFF; no audible samples.");
        Console.WriteLine("Evidence: " + run);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(950));
        var totalWatch = Stopwatch.StartNew();
        var capturedWatch = new Stopwatch();
        var client = new CaptureHostClient();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource<HostEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new ConcurrentQueue<object>();
        var errors = new List<string>();
        var hashes = new Dictionary<string, string>();
        SyntheticAudioProbe.Producer? producer = null;
        RecordingResult? result = null;
        M1RecoveryResult? recovery = null;
        PcmReport? pcm = null;
        RecordingHealth? latestHealth = null;
        bool ownedSelection = false, hostExited = false, producerDone = false, endedEarly = false, originalsUnchanged = false, micAbsent = false;
        int unexpectedTransitions = 0;
        int? producerExit = null;
        double? sourceCompletionSeconds = null;
        client.EventReceived += value =>
        {
            if (value.Kind == "health" && value.Health is { } health) Volatile.Write(ref latestHealth, health);
            else events.Enqueue(new { value.Kind, value.Message, value.Ack });
            if (value.Kind == "ack") Interlocked.Increment(ref unexpectedTransitions);
            if (value.Kind == "started") started.TrySetResult();
            if (value.Kind is "recording_saved" or "recording_incomplete" or "error")
            {
                terminal.TrySetResult(value);
                started.TrySetException(new IOException("Host terminated before startup."));
            }
        };
        WriteProgress("starting");
        try
        {
            producer = await SyntheticAudioProbe.Producer.StartAsync(root, "target-silence", deadline.Token);
            var selection = await SyntheticAudioProbe.FindOwnedSourceAsync(producer, deadline.Token);
            ownedSelection = SyntheticAudioProbe.IsOwnedSelection(selection, producer.Identity);
            if (!ownedSelection) throw new IOException("Silence source was not created by this probe.");
            await client.StartRecordingAsync(new RecordingOptions(SyntheticAudioProbe.CreateOptions(selection, recording),
                MaximumSeconds: 925, ExpectedMinutes: 16), root, () => deadline.IsCancellationRequested);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            await producer.GoAsync(deadline.Token);
            capturedWatch.Start();
            WriteProgress("recording_silence");
            var done = producer.WaitDoneAsync(deadline.Token);
            while (!done.IsCompleted)
            {
                var winner = await Task.WhenAny(done, terminal.Task, Task.Delay(TimeSpan.FromSeconds(30), deadline.Token));
                deadline.Token.ThrowIfCancellationRequested();
                if (winner == terminal.Task)
                {
                    endedEarly = true;
                    throw new IOException("Host ended while the 900-second silence source was still active.");
                }
                if (winner != done) WriteProgress("recording_silence");
            }
            await done;
            producerDone = true; sourceCompletionSeconds = capturedWatch.Elapsed.TotalSeconds;
            if (sourceCompletionSeconds < 899.5) throw new IOException("Producer consumed 900 seconds sooner than allowed by real-time playback.");
            if (terminal.Task.IsCompleted) { endedEarly = true; throw new IOException("Host ended before explicit Stop."); }
            // Producer done itself includes the required 500 ms of following silence.
            WriteProgress("stopping_and_saving");
            await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
            var final = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(20), deadline.Token);
            await client.WaitForExitAsync();
            hostExited = true; capturedWatch.Stop();
            result = final.RecordingResult ?? throw new IOException("Host omitted the M1 result.");
            WriteProgress("validating_originals");
            foreach (string file in Directory.EnumerateFiles(recording, "*", SearchOption.AllDirectories))
            {
                deadline.Token.ThrowIfCancellationRequested();
                hashes.Add(Path.GetRelativePath(recording, file), Hash(file));
            }
            recovery = await Task.Run(() => M1Recovery.Read(recording)).WaitAsync(deadline.Token);
            pcm = CheckPcm(recovery.Capture, deadline.Token);
            micAbsent = recovery.Capture.Tracks.All(track => track.SourceId == "remote") &&
                !Directory.EnumerateFiles(recording, "mic*", SearchOption.AllDirectories).Any();
            originalsUnchanged = hashes.All(pair => Hash(Path.Combine(recording, pair.Key)) == pair.Value);
            if (!result.StorageComplete || !recovery.IsCleanEnd) errors.Add("storage_or_offline_validation_incomplete");
            if (recovery.Capture.Duration100ns < 8_995_000_000L || result.DurationUs < 899_500_000L) errors.Add("elapsed_timeline_shorter_than_899_5_seconds");
            if (!pcm.Pass) errors.AddRange(pcm.Errors);
            if (!micAbsent) errors.Add("unexpected_microphone_data");
            if (!originalsUnchanged) errors.Add("originals_changed_during_verification");
            if (Volatile.Read(ref unexpectedTransitions) != 0) errors.Add("unexpected_control_transition");
            if (Volatile.Read(ref latestHealth) is { Paused: true }) errors.Add("silence_capture_paused_unexpectedly");
            if (Directory.EnumerateFiles(recording, "*.partial", SearchOption.AllDirectories).Any()) errors.Add("unfinalized_originals");
        }
        catch (Exception ex)
        {
            errors.Add(ex.GetType().Name + ": " + ex.Message);
            File.WriteAllText(Path.Combine(run, "failure.txt"), ex.ToString());
        }
        finally
        {
            try { await client.DisposeAsync(); hostExited = true; }
            catch (Exception ex) { errors.Add("host_shutdown:" + ex.GetType().Name); }
            if (producer is not null)
            {
                try { producerExit = await producer.StopAsync(); }
                catch (Exception ex) { errors.Add("producer_cleanup:" + ex.GetType().Name); }
            }
        }
        if (!ownedSelection || !hostExited || producerExit != 0 || !producerDone || endedEarly) errors.Add("owned_process_lifecycle_or_duration_failed");
        bool pass = errors.Count == 0 && pcm?.Pass == true;
        var packets = recovery?.Capture.Tracks.SelectMany(track => track.Packets).ToArray() ?? [];
        var report = new
        {
            Schema = "m1-fifteen-minute-silence-probe-1", Status = pass ? "PASS_WITH_CLOCK_LIMITATIONS" : "FAIL",
            Os = Environment.OSVersion.Version.ToString(), CaptureMode = "ProcessTree/test-owned target-silence",
            MicrophoneEnabled = false, EndpointFallback = false, BrowserOrMeetingOpened = false,
            Duration = new { RequiredSourceSeconds = 900, SourceCompletionWallSeconds = sourceCompletionSeconds,
                RecordedElapsedSeconds = result?.DurationUs / 1_000_000d, RecoveredElapsedSeconds = recovery?.Capture.Duration100ns / 10_000_000d,
                DidNotEndDuringSilence = !endedEarly && producerDone, TotalTestSeconds = totalWatch.Elapsed.TotalSeconds },
            Storage = new { Complete = result?.StorageComplete, OfflineCleanEnd = recovery?.IsCleanEnd, MicDataAbsent = micAbsent,
                OriginalsUnchanged = originalsUnchanged, Issues = recovery?.Issues, Pcm = pcm },
            ClockLimitations = new { Status = packets.Any(packet => !packet.DevicePositionValid) ? "QPC_REFERENCED_DEVICE_COUNTER_LIMITED" : "SINGLE_SOURCE_SILENCE_ONLY",
                DeviceCounterUnavailablePackets = packets.Count(packet => !packet.DevicePositionValid),
                InvalidQpcPackets = packets.Count(packet => !packet.TimestampValid), TwoHourSynchronization = "NOT_RUN",
                IndependentDeviceDrift = "NOT_ESTABLISHED", RealMic = "NOT_RUN" },
            Lifecycle = new { HostExited = hostExited, ProducerExitCode = producerExit, ProducerSubmitted900Seconds = producerDone },
            Events = events.ToArray(), OriginalHashes = hashes, Errors = errors
        };
        string reportPath = Path.Combine(run, "probe-report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        WriteProgress(pass ? "complete_with_clock_limitations" : "failed");
        Console.WriteLine($"{report.Status}; seconds={result?.DurationUs / 1_000_000d:F3}; zeroPcm={pcm?.AllPcmBytesZero}; hostExited={hostExited}; mic=OFF");
        Console.WriteLine("Report: " + reportPath);
        return pass ? 0 : 1;

        void WriteProgress(string stage)
        {
            // Only stage and elapsed time are written, never audio, source metadata or
            // partial claims of success. Atomic replacement keeps read-only monitoring simple.
            string temporary = progressPath + ".partial";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { Stage = stage, ElapsedSeconds = totalWatch.Elapsed.TotalSeconds }));
            File.Move(temporary, progressPath, overwrite: true);
        }
    }

    private static PcmReport CheckPcm(CaptureResult result, CancellationToken token)
    {
        var errors = new List<string>();
        long frames = 0, bytes = 0;
        bool allZero = true, chunkBounds = true;
        double packetSeconds = 0, firstPacket = double.PositiveInfinity, lastPacketEnd = 0, maximumGap = 0;
        var ranges = new List<(double Start, double End)>();
        foreach (var track in result.Tracks)
        {
            token.ThrowIfCancellationRequested();
            if (track.Format.SampleRate != ProbeToneSource.SampleRate || track.Format.Channels != ProbeToneSource.Channels ||
                !track.Format.IsFloat || track.Format.BitsPerSample != 32) throw new InvalidDataException("Unexpected silence-probe process-loopback format.");
            chunkBounds &= track.FrameCount <= 30L * track.Format.SampleRate;
            using var reader = new WaveFileReader(track.WavePath);
            long expectedBytes = checked(track.FrameCount * track.Format.BlockAlign);
            if (reader.Length != expectedBytes) errors.Add("wav_bytes_differ_from_persisted_frame_count");
            var buffer = new byte[65536]; long consumed = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int count = reader.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                if (buffer.AsSpan(0, count).IndexOfAnyExcept((byte)0) >= 0) allZero = false;
                consumed += count;
            }
            if (consumed != expectedBytes) errors.Add("could_not_read_all_confirmed_pcm");
            frames += track.FrameCount; bytes += consumed;
            packetSeconds += track.FrameCount / (double)track.Format.SampleRate;
            foreach (var packet in track.Packets)
            {
                if (!packet.TimestampValid) errors.Add("invalid_packet_qpc");
                double at = ((double)packet.QpcPosition100ns - result.EpochQpc100ns) / 10_000_000d + packet.RetainedFrameOffset / (double)track.Format.SampleRate;
                double end = at + packet.FrameCount / (double)track.Format.SampleRate;
                ranges.Add((at, end)); firstPacket = Math.Min(firstPacket, at); lastPacketEnd = Math.Max(lastPacketEnd, end);
            }
        }
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 1; i < ranges.Count; i++) maximumGap = Math.Max(maximumGap, ranges[i].Start - ranges[i - 1].End);
        double span = double.IsFinite(firstPacket) ? lastPacketEnd - firstPacket : 0;
        if (!allZero) errors.Add("nonzero_pcm_in_digital_silence_capture");
        if (!chunkBounds) errors.Add("chunk_exceeded_30_seconds");
        if (packetSeconds < 899.5 || span < 899.5) errors.Add("confirmed_pcm_or_packet_timeline_shorter_than_899_5_seconds");
        if (Math.Abs(span - packetSeconds) > .5 || maximumGap > .25) errors.Add("pcm_frame_duration_does_not_match_observed_packet_timeline");
        if (Math.Abs(result.Duration100ns / 10_000_000d - packetSeconds) > 1) errors.Add("session_duration_differs_from_actual_frame_duration");
        return new(errors.Count == 0, allZero, chunkBounds, result.Tracks.Count, frames, bytes, packetSeconds, span, maximumGap, errors);
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private sealed record PcmReport(bool Pass, bool AllPcmBytesZero, bool EveryChunkAtMostThirtySeconds, int ChunkCount,
        long FrameCount, long PcmBytesRead, double FrameDurationSeconds, double PacketTimelineSpanSeconds,
        double LargestPacketGapSeconds, IReadOnlyList<string> Errors);
}
