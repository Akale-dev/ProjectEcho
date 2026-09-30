using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using ProjectEcho.App;
using ProjectEcho.Capture.Windows;
using ProjectEcho.CaptureHost;
using ProjectEcho.Core;

/// <summary>Explicit real-Windows probe. Only its newly created synthetic process is captured; mic remains absent.</summary>
internal static class M1IntegrationProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1 || args[0] != "--m1-audio-probe") return 2;
        string root = SyntheticAudioProbe.FindProjectRoot();
        string parent = SyntheticAudioProbe.SafeProjectPath(root, ".local/M1AudioIntegration");
        Directory.CreateDirectory(parent);
        string lockPath = Path.Combine(root, ".local", "recording-probe.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Probe lock is a reparse point.");
        using var runLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var existing = Process.GetProcessesByName("ProjectEcho.CaptureHost");
        try { if (existing.Length != 0) { Console.Error.WriteLine("Existing CaptureHost found; M1 probe refused."); return 2; } }
        finally { foreach (var process in existing) process.Dispose(); }
        string run = Path.Combine(parent, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "SYNTHETIC_SOURCE_REAL_WASAPI"), "Owned test process only; real production M1 Host/IPC; no browser, meeting, microphone or endpoint capture.");
        Console.WriteLine("M1 live probe: owned process only, microphone OFF; waits for started before synthetic playback.");
        Console.WriteLine("Evidence: " + run);
        string directory = Path.Combine(run, "recording");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var client = new CaptureHostClient();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource<HostEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentDictionary<string, TaskCompletionSource<RecordingAck>>();
        var events = new ConcurrentQueue<object>();
        var unexpectedAcks = new ConcurrentQueue<string>();
        var errors = new List<string>();
        var hashes = new Dictionary<string, string>();
        SyntheticAudioProbe.Producer? producer = null;
        RecordingResult? result = null;
        M1RecoveryResult? recovered = null;
        SignalReport? signal = null;
        StorageReport? storage = null;
        bool ownedSelection = false, hostExited = false, sourceDone = false, unchanged = false, micAbsent = false, stopRequestedTwice = false;
        int? producerExit = null;
        RecordingAck? pause = null, resume = null, bookmark = null;
        client.EventReceived += value =>
        {
            if (value.Kind != "health") events.Enqueue(new { value.Kind, value.RequestId, value.Message, value.Ack });
            if (value.Kind == "started") started.TrySetResult();
            if (value.Kind == "ack" && value.Ack is { } ack)
            {
                if (requests.TryGetValue(ack.RequestId, out var request)) request.TrySetResult(ack);
                else unexpectedAcks.Enqueue(ack.Operation + ":" + ack.RequestId);
            }
            if (value.Kind == "control_error" && requests.TryGetValue(value.RequestId, out var rejected))
                rejected.TrySetException(new IOException("Control rejected: " + value.Message));
            if (value.Kind is "recording_saved" or "recording_incomplete" or "error")
            {
                terminal.TrySetResult(value);
                started.TrySetException(new IOException("Host ended before successful startup."));
                foreach (var request in requests.Values) request.TrySetException(new IOException("Host ended before control acknowledgement."));
            }
        };
        try
        {
            producer = await SyntheticAudioProbe.Producer.StartAsync(root, "target-m1", deadline.Token);
            var selection = await SyntheticAudioProbe.FindOwnedSourceAsync(producer, deadline.Token);
            ownedSelection = SyntheticAudioProbe.IsOwnedSelection(selection, producer.Identity);
            if (!ownedSelection) throw new InvalidOperationException("Source is not the newly created owned process.");
            var options = SyntheticAudioProbe.CreateOptions(selection, directory);
            await client.StartRecordingAsync(new RecordingOptions(options, MaximumSeconds: 55, ExpectedMinutes: 1), root,
                () => deadline.IsCancellationRequested);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            await producer.GoAsync(deadline.Token);
            events.Enqueue(new { Kind = "tone-go-after-started" });
            var done = producer.WaitDoneAsync(deadline.Token);
            await Task.Delay(TimeSpan.FromSeconds(8), deadline.Token);
            bookmark = await ControlAsync("bookmark");
            pause = await ControlAsync("pause");
            if (!pause.Paused || pause.MicrophoneEnabled) throw new IOException("Pause acknowledgement state mismatch.");
            await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
            resume = await ControlAsync("resume");
            if (resume.Paused || resume.MicrophoneEnabled) throw new IOException("Resume acknowledgement state mismatch.");
            await done;
            sourceDone = true;
            // The source already allows 500 ms after provider completion. This extra
            // margin leaves a >30 s resumed segment while protecting the final tone.
            await Task.Delay(500, deadline.Token);
            await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
            await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
            stopRequestedTwice = true;
            var final = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(25), deadline.Token);
            await client.WaitForExitAsync();
            hostExited = true;
            result = final.RecordingResult ?? throw new IOException("Host did not return M1 recording result.");
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                hashes.Add(Path.GetRelativePath(directory, file), Hash(file));
            recovered = M1Recovery.Read(directory);
            micAbsent = recovered.Capture.Tracks.All(track => track.SourceId == "remote") &&
                !Directory.EnumerateFiles(directory, "mic*", SearchOption.AllDirectories).Any();
            storage = CheckStorage(directory, recovered, pause, resume, bookmark);
            signal = Analyze(recovered.Capture);
            unchanged = hashes.All(pair => Hash(Path.Combine(directory, pair.Key)) == pair.Value);
            if (!result.StorageComplete || !recovered.IsCleanEnd) errors.Add("storage_not_clean");
            if (!micAbsent) errors.Add("unexpected_microphone_data");
            if (!storage.Pass) errors.AddRange(storage.Errors);
            if (!signal.Pass) errors.AddRange(signal.Errors);
            if (!unchanged) errors.Add("original_changed_during_read_only_validation");
            if (!unexpectedAcks.IsEmpty) errors.Add("unexpected_capture_control_ack");
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
            // Keep the producer alive (silent after done) until the recording Host
            // has actually exited. Producer.StopAsync only targets the owned child.
            if (producer is not null)
            {
                try { producerExit = await producer.StopAsync(); }
                catch (Exception ex) { errors.Add("source_shutdown:" + ex.GetType().Name); }
            }
        }
        if (!ownedSelection || !hostExited || producerExit != 0 || !sourceDone) errors.Add("owned_process_lifecycle_incomplete");
        var packets = recovered?.Capture.Tracks.SelectMany(track => track.Packets).ToArray() ?? [];
        bool pass = errors.Count == 0 && result?.StorageComplete == true && storage?.Pass == true && signal?.Pass == true;
        var report = new
        {
            Schema = "m1-real-audio-probe-1", Status = pass ? "PASS_WITH_CLOCK_LIMITATIONS" : "FAIL",
            Os = Environment.OSVersion.Version.ToString(), Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            CaptureMode = "ProcessTree/test-owned target-m1", MicrophoneEnabled = false, EndpointFallback = false,
            BrowserOrMeetingOpened = false, OwnedSelection = ownedSelection,
            Storage = new { ResultComplete = result?.StorageComplete, OfflineCleanEnd = recovered?.IsCleanEnd,
                MicFilesAbsent = micAbsent, OriginalsUnchanged = unchanged, Checks = storage, RecoveryIssues = recovered?.Issues },
            SyntheticSignal = signal,
            ClockLimitations = new { Status = packets.Any(packet => !packet.DevicePositionValid) ? "QPC_REFERENCED_DEVICE_COUNTER_LIMITED" : "SHORT_SINGLE_SOURCE_ONLY",
                QpcValidPackets = packets.Count(packet => packet.TimestampValid), InvalidQpcPackets = packets.Count(packet => !packet.TimestampValid),
                DeviceCounterUnavailablePackets = packets.Count(packet => !packet.DevicePositionValid),
                TwoHourSynchronization = "NOT_RUN", IndependentDeviceDrift = "NOT_ESTABLISHED",
                Note = "Signal and storage success do not establish hardware microphone privacy, dual-device synchronization or platform/browser isolation." },
            Lifecycle = new { HostExited = hostExited, ProducerExitCode = producerExit, ProducerSubmitted40Seconds = sourceDone,
                StopRequestedTwice = stopRequestedTwice },
            Errors = errors, Events = events.ToArray(), UnexpectedAcknowledgements = unexpectedAcks.ToArray(), OriginalHashes = hashes
        };
        string reportPath = Path.Combine(run, "probe-report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{report.Status}; storage={storage?.Pass}; signal={signal?.Pass}; hostExited={hostExited}; microphone=OFF");
        Console.WriteLine("Report: " + reportPath);
        return pass ? 0 : 1;

        async Task<RecordingAck> ControlAsync(string operation)
        {
            string id = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<RecordingAck>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!requests.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate request id.");
            try
            {
                await client.ControlAsync(operation, requestId: id);
                var ack = await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), deadline.Token);
                if (ack.RequestId != id || ack.Operation != operation) throw new IOException("Control acknowledgement mismatch.");
                return ack;
            }
            finally { requests.TryRemove(id, out _); }
        }
    }

    private static StorageReport CheckStorage(string directory, M1RecoveryResult recovery, RecordingAck pause,
        RecordingAck resume, RecordingAck bookmark)
    {
        var errors = new List<string>();
        var segments = new HashSet<string>();
        var chunkFrames = new Dictionary<string, long>();
        var chunkSegments = new Dictionary<string, string>();
        var boundaries = new List<(string Operation, long At)>();
        foreach (string line in File.ReadLines(recovery.Capture.IndexPath))
        {
            using var document = JsonDocument.Parse(line);
            var data = document.RootElement.GetProperty("data");
            switch (data.GetProperty("type").GetString())
            {
                case "segment": segments.Add(data.GetProperty("segmentId").GetString()!); break;
                case "packet":
                    string id = data.GetProperty("chunkId").GetString()!;
                    chunkFrames[id] = chunkFrames.GetValueOrDefault(id) + data.GetProperty("frameCount").GetInt32();
                    chunkSegments[id] = data.GetProperty("segmentId").GetString()!;
                    break;
                case "boundary": boundaries.Add((data.GetProperty("operation").GetString()!, data.GetProperty("effectiveSessionUs").GetInt64())); break;
            }
        }
        double gap = (resume.EffectiveSessionUs - pause.EffectiveSessionUs) / 1_000_000d;
        var perSegment = chunkFrames.GroupBy(pair => chunkSegments[pair.Key])
            .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value));
        bool crossed = chunkFrames.Values.Any(frames => frames == 30L * ProbeToneSource.SampleRate) &&
            perSegment.Any(pair => pair.Value > (long)(30.2 * ProbeToneSource.SampleRate) && chunkSegments.Values.Count(id => id == pair.Key) >= 2);
        var orderedTracks = recovery.Capture.Tracks.Where(track => track.Packets.Count > 0)
            .OrderBy(track => track.Packets[0].QpcPosition100ns).ToArray();
        double maximumPacketGap = 0;
        var ranges = orderedTracks.SelectMany(track => track.Packets.Select(packet => (
            Start: ((double)packet.QpcPosition100ns - recovery.Capture.EpochQpc100ns) / 10_000_000 + packet.RetainedFrameOffset / (double)track.Format.SampleRate,
            End: ((double)packet.QpcPosition100ns - recovery.Capture.EpochQpc100ns) / 10_000_000 + (packet.RetainedFrameOffset + packet.FrameCount) / (double)track.Format.SampleRate)))
            .OrderBy(range => range.Start).ToArray();
        for (int i = 1; i < ranges.Length; i++) maximumPacketGap = Math.Max(maximumPacketGap, ranges[i].Start - ranges[i - 1].End);
        if (segments.Count < 2) errors.Add("resume_did_not_create_new_segment");
        if (!crossed) errors.Add("resumed_segment_did_not_cross_30_second_chunk");
        if (gap is < 1.8 or > 3.5 || maximumPacketGap is < 1.8 or > 3.7) errors.Add("pause_timeline_gap_mismatch");
        if (!boundaries.Any(boundary => boundary.Operation == "pause") || !boundaries.Any(boundary => boundary.Operation == "stop")) errors.Add("missing_persisted_capture_boundary");
        if (bookmark.Operation != "bookmark" || bookmark.EffectiveSessionUs > pause.EffectiveSessionUs) errors.Add("bookmark_ack_order_mismatch");
        var expectedEvents = new Dictionary<string, string>
        { [bookmark.RequestId] = "bookmark", [pause.RequestId] = "pause", [resume.RequestId] = "resume" };
        long eventSequence = 0;
        foreach (string line in File.ReadLines(Path.Combine(directory, "journal", "events.jsonl")))
        {
            using var document = JsonDocument.Parse(line);
            var envelope = document.RootElement;
            var data = envelope.GetProperty("data");
            var eventHash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data.GetRawText())));
            if (envelope.GetProperty("sequence").GetInt64() != eventSequence++ || envelope.GetProperty("sha256").GetString() != eventHash)
                errors.Add("persisted_event_hash_or_sequence_invalid");
            string requestId = data.GetProperty("requestId").GetString()!;
            if (expectedEvents.TryGetValue(requestId, out var operation))
            {
                if (data.GetProperty("operation").GetString() != operation) errors.Add("persisted_control_operation_mismatch");
                expectedEvents.Remove(requestId);
            }
        }
        if (expectedEvents.Count != 0) errors.Add("acknowledged_control_event_not_persisted");
        if (Directory.EnumerateFiles(directory, "*.partial", SearchOption.AllDirectories).Any()) errors.Add("unfinalized_partial_file");
        return new(errors.Count == 0, segments.Count, chunkFrames.Count, gap, maximumPacketGap, crossed, perSegment, errors);
    }

    private static SignalReport Analyze(CaptureResult capture)
    {
        var windows = new List<ToneWindow>();
        foreach (var track in capture.Tracks)
        {
            if (!track.Format.IsFloat || track.Format.BitsPerSample != 32 || track.Format.Channels != 2 || track.Format.SampleRate != ProbeToneSource.SampleRate)
                throw new InvalidDataException("M1 probe expected the production process-loopback Float32 stereo format.");
            using var reader = new WaveFileReader(track.WavePath);
            int framesPerWindow = track.Format.SampleRate / 5;
            var bytes = new byte[framesPerWindow * track.Format.BlockAlign];
            long offset = 0;
            while (offset < track.FrameCount)
            {
                int wanted = (int)Math.Min(framesPerWindow, track.FrameCount - offset), used = 0;
                while (used < wanted * track.Format.BlockAlign)
                {
                    int count = reader.Read(bytes, used, wanted * track.Format.BlockAlign - used);
                    if (count == 0) throw new EndOfStreamException("Probe WAV ended before its persisted frame count.");
                    used += count;
                }
                var packet = track.Packets.First(packet => offset >= packet.WaveFrameOffset && offset < packet.WaveFrameOffset + packet.FrameCount);
                double at = ((double)packet.QpcPosition100ns - capture.EpochQpc100ns) / 10_000_000 +
                    (packet.RetainedFrameOffset + offset - packet.WaveFrameOffset) / (double)track.Format.SampleRate;
                double sine660 = 0, cosine660 = 0, sine880 = 0, cosine880 = 0;
                for (int frame = 0; frame < wanted; frame++)
                {
                    float sample = BitConverter.ToSingle(bytes, frame * track.Format.BlockAlign);
                    if (!float.IsFinite(sample)) throw new InvalidDataException("Non-finite probe sample.");
                    double position = frame / (double)track.Format.SampleRate;
                    sine660 += sample * Math.Sin(2 * Math.PI * 660 * position); cosine660 += sample * Math.Cos(2 * Math.PI * 660 * position);
                    sine880 += sample * Math.Sin(2 * Math.PI * 880 * position); cosine880 += sample * Math.Cos(2 * Math.PI * 880 * position);
                }
                windows.Add(new(at, wanted / (double)track.Format.SampleRate,
                    2 * Math.Sqrt(sine660 * sine660 + cosine660 * cosine660) / wanted,
                    2 * Math.Sqrt(sine880 * sine880 + cosine880 * cosine880) / wanted));
                offset += wanted;
            }
        }
        double mainPeak = windows.Select(window => window.Amplitude660).DefaultIfEmpty().Max();
        double tailPeak = windows.Where(window => window.StartSeconds > 32).Select(window => window.Amplitude880).DefaultIfEmpty().Max();
        double mainSeconds = mainPeak > .0002 ? windows.Where(window => window.Amplitude660 > Math.Max(.0002, mainPeak * .1)).Sum(window => window.Seconds * window.Amplitude660 / mainPeak) : 0;
        var tail = windows.Where(window => window.StartSeconds > 32 && window.Amplitude880 > Math.Max(.0002, tailPeak * .1)).ToArray();
        double tailSeconds = tailPeak > .0002 ? tail.Sum(window => window.Seconds * window.Amplitude880 / tailPeak) : 0;
        bool early880 = windows.Any(window => window.StartSeconds is > 4 and < 8 && window.Amplitude880 > .0002);
        var errors = new List<string>();
        if (mainSeconds is < 4.75 or > 5.25) errors.Add("five_second_660_signal_missing_or_truncated");
        if (!early880) errors.Add("initial_880_marker_missing");
        if (tailSeconds is < .75 or > 1.25 || tail.Length < 4) errors.Add("final_880_marker_missing_or_truncated");
        return new(errors.Count == 0, mainPeak, mainSeconds, early880, tailPeak, tailSeconds,
            tail.Length == 0 ? null : tail.Min(window => window.StartSeconds),
            tail.Length == 0 ? null : tail.Max(window => window.StartSeconds + window.Seconds), errors, windows);
    }

    private static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    private sealed record StorageReport(bool Pass, int SegmentCount, int ChunkCount, double AcknowledgedPauseSeconds,
        double LargestPacketTimelineGapSeconds, bool CrossedThirtySecondChunk, Dictionary<string, long> SegmentFrames, IReadOnlyList<string> Errors);
    private sealed record ToneWindow(double StartSeconds, double Seconds, double Amplitude660, double Amplitude880);
    private sealed record SignalReport(bool Pass, double Main660Peak, double Main660EquivalentSeconds, bool Initial880Detected,
        double Final880Peak, double Final880EquivalentSeconds, double? Final880StartSeconds, double? Final880EndSeconds,
        IReadOnlyList<string> Errors, IReadOnlyList<ToneWindow> Windows)
    {
        public string Method => "200ms rectangular coherent amplitude, left channel of generated identical stereo; original WAV read only; packet QPC maps windows onto elapsed timeline.";
        public string ClockIntegrity => "NOT_ESTABLISHED_BY_SIGNAL_ANALYSIS";
    }
}
