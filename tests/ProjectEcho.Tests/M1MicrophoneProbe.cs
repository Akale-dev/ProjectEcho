using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using ProjectEcho.App;
using ProjectEcho.Capture.Windows;
using ProjectEcho.CaptureHost;
using ProjectEcho.Core;

/// <summary>Explicit short real-microphone probe. Never chooses or substitutes a microphone.</summary>
internal static class M1MicrophoneProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] != "--m1-microphone-probe" || string.IsNullOrWhiteSpace(args[1])) return 2;
        string root = SyntheticAudioProbe.FindProjectRoot();
        string parent = SyntheticAudioProbe.SafeProjectPath(root, ".local/M1MicrophoneIntegration");
        Directory.CreateDirectory(parent);
        string lockPath = Path.Combine(root, ".local", "recording-probe.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Probe lock is a reparse point.");
        using var runLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var existing = Process.GetProcessesByName("ProjectEcho.CaptureHost");
        try { if (existing.Length != 0) { Console.Error.WriteLine("Existing CaptureHost found; microphone probe refused."); return 2; } }
        finally { foreach (var process in existing) process.Dispose(); }
        var microphone = SourceDiscovery.Enumerate().Microphones.SingleOrDefault(device => device.Id == args[1]);
        if (microphone is null) { Console.Error.WriteLine("Explicit microphone is not active; no fallback attempted."); return 2; }
        string run = Path.Combine(parent, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "REAL_MICROPHONE_LOCAL_ONLY"),
            "Contains explicitly selected real microphone PCM and test-owned process audio. Local only. No transcript, replay or upload.");
        Console.WriteLine("Real microphone probe: " + microphone.Name + "; fixed endpoint; about 10 seconds of microphone PCM in a 20-second session.");
        Console.WriteLine("Evidence: " + run);
        string directory = Path.Combine(run, "recording");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var client = new CaptureHostClient();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource<HostEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentDictionary<string, TaskCompletionSource<RecordingAck>>();
        var events = new ConcurrentQueue<object>();
        var health = new ConcurrentQueue<RecordingHealth>();
        var unexpectedAcks = new ConcurrentQueue<string>();
        var controls = new List<ControlObservation>();
        var errors = new List<string>();
        var hashes = new Dictionary<string, string>();
        SyntheticAudioProbe.Producer? producer = null;
        RecordingResult? result = null;
        M1RecoveryResult? recovered = null;
        BoundaryReport? boundary = null;
        SignalStatistics? micSignal = null;
        bool ownedSelection = false, hostExited = false, unchanged = false;
        int? producerExit = null;
        client.EventReceived += value =>
        {
            if (value.Health is { } h) health.Enqueue(h);
            if (value.Kind != "health") events.Enqueue(new { value.Kind, value.RequestId, value.Message, value.Ack });
            if (value.Kind == "started") started.TrySetResult();
            if (value.Kind == "ack" && value.Ack is { } ack)
            {
                if (value.RequestId != ack.RequestId) unexpectedAcks.Enqueue("envelope_id_mismatch:" + ack.RequestId);
                else if (requests.TryGetValue(ack.RequestId, out var request)) request.TrySetResult(ack);
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
            producer = await SyntheticAudioProbe.Producer.StartAsync(root, "target-m1", deadline.Token, args.Length == 3 ? args[2] : null);
            var selection = await SyntheticAudioProbe.FindOwnedSourceAsync(producer, deadline.Token);
            ownedSelection = SyntheticAudioProbe.IsOwnedSelection(selection, producer.Identity);
            if (!ownedSelection) throw new InvalidOperationException("Source is not the newly created owned process.");
            var options = SyntheticAudioProbe.CreateOptions(selection, directory) with
            { MicrophoneEnabled = false, MicrophoneDeviceId = microphone.Id, Title = "真实麦克风边界自动测试（仅本地）" };
            // Normal Stop is at about 20 s. Production CaptureHost also enforces this 30 s recording limit.
            await client.StartRecordingAsync(new RecordingOptions(options, MaximumSeconds: 30, ExpectedMinutes: 1), root,
                () => deadline.IsCancellationRequested);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            await producer.GoAsync(deadline.Token);
            await Delay(1);
            await Control("mic_on", false, true);
            await Delay(4);
            await Control("mic_off", false, false);
            await Delay(3);
            await Control("mic_on", false, true);
            await Delay(3);
            await Control("pause", true, true);
            await Delay(5);
            await Control("resume", false, true);
            await Delay(3);
            await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
            var final = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(25), deadline.Token);
            await client.WaitForExitAsync();
            hostExited = true;
            result = final.RecordingResult ?? throw new IOException("Host did not return M1 recording result.");
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                hashes.Add(Path.GetRelativePath(directory, file), Hash(file));
            recovered = M1Recovery.Read(directory);
            boundary = CheckDisk(directory, recovered, controls);
            micSignal = ReadMicStatistics(recovered.Capture);
            unchanged = hashes.All(pair => Hash(Path.Combine(directory, pair.Key)) == pair.Value);
            if (!result.StorageComplete || !recovered.IsCleanEnd || recovered.Issues.Count != 0) errors.Add("storage_not_clean");
            if (!boundary.Pass) errors.AddRange(boundary.Errors);
            if (!unchanged) errors.Add("original_changed_during_read_only_validation");
            if (!unexpectedAcks.IsEmpty) errors.Add("unexpected_capture_control_ack");
            if (result.DurationUs is < 17_000_000 or > 30_500_000) errors.Add("unexpected_session_duration");
            CheckHealth(health.ToArray(), recovered.Capture.EpochQpc100ns, controls, errors);
        }
        catch (Exception ex)
        {
            errors.Add(ex.GetType().Name + ": " + ex.Message);
            File.WriteAllText(Path.Combine(run, "failure.txt"), ex.ToString());
        }
        finally
        {
            // Closing the pipe invokes production fail-closed handling if a command failed.
            try { await client.DisposeAsync(); hostExited = true; }
            catch (Exception ex) { errors.Add("host_shutdown:" + ex.GetType().Name); }
            // Never stop the remote process while CaptureHost is still expected to read it.
            if (producer is not null)
            {
                try { producerExit = await producer.StopAsync(); }
                catch (Exception ex) { errors.Add("source_shutdown:" + ex.GetType().Name); }
            }
        }
        if (!ownedSelection || !hostExited || producerExit != 0) errors.Add("owned_process_lifecycle_incomplete");
        // A failed control can still leave local microphone originals. Diagnostic
        // counts must not imply no PCM merely because the success path was skipped.
        if (result is null && terminal.Task.IsCompletedSuccessfully) result = terminal.Task.Result.RecordingResult;
        if (recovered is null && File.Exists(Path.Combine(directory, "journal", "capture-index.jsonl")))
        {
            try { recovered = M1Recovery.Read(directory); micSignal = ReadMicStatistics(recovered.Capture); }
            catch (Exception ex) { errors.Add("diagnostic_recovery:" + ex.GetType().Name); }
        }
        var micTracks = recovered?.Capture.Tracks.Where(track => track.SourceId == "mic").ToArray() ?? [];
        var micPackets = micTracks.SelectMany(track => track.Packets).ToArray();
        bool pass = errors.Count == 0 && boundary?.Pass == true && result?.StorageComplete == true;
        var report = new
        {
            Schema = "m1-real-microphone-probe-1", Status = pass ? "PASS_SHORT_MIC_BOUNDARIES" : "FAIL",
            Os = Environment.OSVersion.Version.ToString(), Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            CaptureMode = "ProcessTree/test-owned target-m1 + explicitly selected microphone",
            SelectedMicrophone = microphone, InitiallyEnabled = false, EndpointFallback = false, BrowserOrMeetingOpened = false,
            Privacy = new { LocalOnly = true, Replayed = false, Transcribed = false, Uploaded = false,
                Note = "Physical microphone PCM is retained locally as test evidence; report only contains statistics." },
            Storage = new { ResultComplete = result?.StorageComplete, OfflineCleanEnd = recovered?.IsCleanEnd,
                OriginalsUnchanged = unchanged, RecoveryIssues = recovered?.Issues, ResultWarnings = result?.Warnings },
            BoundaryChecks = boundary, Controls = controls,
            MicSignal = micSignal,
            MicrophoneClock = new { DataExamined = recovered is not null, Frames = micTracks.Sum(track => track.FrameCount), Packets = micPackets.Length,
                ValidQpcPackets = micPackets.Count(packet => packet.TimestampValid),
                ValidDeviceCounterPackets = micPackets.Count(packet => packet.DevicePositionValid),
                Formats = micTracks.Select(track => track.Format).Distinct().ToArray() },
            Scope = new { TwoHourSynchronization = "NOT_RUN", PhysicalUnplug = "NOT_RUN", PlatformMute = "NOT_RUN",
                Note = "Short endpoint operation and persisted capture boundaries only; no physical mute or long-term drift claim." },
            Lifecycle = new { HostExited = hostExited, ProducerExitCode = producerExit, OwnedSelection = ownedSelection },
            ProducerOutput = producer?.ReadyInfo, ProducerFailure = producer?.FailureDiagnostic,
            Errors = errors, Events = events.ToArray(), Health = health.ToArray(),
            UnexpectedAcknowledgements = unexpectedAcks.ToArray(), OriginalHashes = hashes
        };
        string reportPath = Path.Combine(run, "probe-report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{report.Status}; micFrames={micTracks.Sum(track => track.FrameCount)}; boundary={boundary?.Pass}; hostExited={hostExited}");
        Console.WriteLine("Report: " + reportPath);
        return pass ? 0 : 1;

        Task Delay(int seconds) => Task.Delay(TimeSpan.FromSeconds(seconds), deadline.Token);

        async Task Control(string operation, bool paused, bool enabled)
        {
            string id = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<RecordingAck>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!requests.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate request id.");
            ulong requestAt = Now100ns();
            try
            {
                await client.ControlAsync(operation, deviceId: operation == "mic_on" ? microphone.Id : null, requestId: id);
                var ack = await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), deadline.Token);
                if (ack.RequestId != id || ack.Operation != operation || ack.Paused != paused || ack.MicrophoneEnabled != enabled)
                    throw new IOException("Control acknowledgement identity or state mismatch.");
                controls.Add(new(operation, requestAt, ack));
            }
            finally { requests.TryRemove(id, out _); }
        }
    }

    private static BoundaryReport CheckDisk(string directory, M1RecoveryResult recovery, IReadOnlyList<ControlObservation> controls)
    {
        var segments = new Dictionary<string, (string Source, int Rate)>();
        var packets = new List<PacketRange>();
        var boundaries = new List<Boundary>();
        var timingErrors = new List<string>();
        var packetSeen = new HashSet<string>();
        foreach (string line in File.ReadLines(recovery.Capture.IndexPath))
        {
            using var document = JsonDocument.Parse(line);
            var data = document.RootElement.GetProperty("data");
            string type = data.GetProperty("type").GetString()!;
            if (type == "segment") segments.Add(data.GetProperty("segmentId").GetString()!,
                (data.GetProperty("sourceId").GetString()!, data.GetProperty("format").GetProperty("sampleRate").GetInt32()));
            if (type == "packet")
            {
                string id = data.GetProperty("segmentId").GetString()!;
                if (data.TryGetProperty("gapBefore100ns", out var gap) && ulong.Parse(gap.GetString()!, System.Globalization.CultureInfo.InvariantCulture) > 0)
                    timingErrors.Add("reported_audio_gap:" + id);
                bool first = packetSeen.Add(id);
                if (!first && data.GetProperty("flags").EnumerateArray().Any(flag => flag.GetString() == "data_discontinuity"))
                    timingErrors.Add("noninitial_discontinuity:" + id);
                packets.Add(new(id, segments[id].Source, segments[id].Rate,
                    ulong.Parse(data.GetProperty("qpcPosition100ns").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                    data.GetProperty("retainedFrameOffset").GetInt32(), data.GetProperty("frameCount").GetInt32()));
            }
            if (type == "boundary") boundaries.Add(new(data.GetProperty("segmentId").GetString()!,
                data.GetProperty("operation").GetString()!, data.GetProperty("requestId").GetString()!,
                ulong.Parse(data.GetProperty("effectiveQpc100ns").GetString()!, System.Globalization.CultureInfo.InvariantCulture)));
        }
        var errors = CheckRanges(packets, boundaries, controls, recovery.Capture.EpochQpc100ns);
        errors.AddRange(timingErrors);
        if (segments.Count != 5 || segments.Count(pair => pair.Value.Source == "mic") != 3 ||
            segments.Count(pair => pair.Value.Source == "remote") != 2) errors.Add("unexpected_disk_segment_count");
        var expected = controls.ToDictionary(control => control.Ack.RequestId);
        long sequence = 0;
        foreach (string line in File.ReadLines(Path.Combine(directory, "journal", "events.jsonl")))
        {
            using var document = JsonDocument.Parse(line);
            var envelope = document.RootElement;
            var data = envelope.GetProperty("data");
            var hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data.GetRawText())));
            if (envelope.GetProperty("sequence").GetInt64() != sequence++ || envelope.GetProperty("sha256").GetString() != hash)
                errors.Add("persisted_event_hash_or_sequence_invalid");
            string requestId = data.GetProperty("requestId").GetString()!;
            if (expected.Remove(requestId, out var control) &&
                (data.GetProperty("operation").GetString() != control.Operation || data.GetProperty("sessionUs").GetInt64() != control.Ack.EffectiveSessionUs))
                errors.Add("persisted_event_differs_from_ack");
        }
        if (expected.Count != 0) errors.Add("acknowledged_event_missing_on_disk");
        if (Directory.EnumerateFiles(directory, "*.partial", SearchOption.AllDirectories).Any()) errors.Add("unfinalized_partial_file");
        var mic = recovery.Capture.Tracks.Where(track => track.SourceId == "mic").ToArray();
        if (mic.Any(track => track.Packets.Any(packet => !packet.TimestampValid || !packet.DevicePositionValid)))
            errors.Add("microphone_clock_invalid");
        foreach (var track in recovery.Capture.Tracks)
        {
            track.Format.Validate();
            using var reader = new WaveFileReader(track.WavePath);
            if (reader.Length != track.FrameCount * track.Format.BlockAlign || track.Packets.Sum(packet => (long)packet.FrameCount) != track.FrameCount)
                errors.Add("wave_frame_count_mismatch");
        }
        return new(errors.Count == 0, segments.Count, packets.Count, boundaries.Count,
            mic.Sum(track => track.FrameCount / (double)track.Format.SampleRate), errors);
    }

    private static List<string> CheckRanges(IReadOnlyList<PacketRange> packets, IReadOnlyList<Boundary> boundaries,
        IReadOnlyList<ControlObservation> controls, ulong epoch)
    {
        var errors = new List<string>();
        if (controls.Count != 5 || !controls.Select(control => control.Operation).SequenceEqual(new[] { "mic_on", "mic_off", "mic_on", "pause", "resume" }))
        { errors.Add("control_sequence_mismatch"); return errors; }
        var groups = packets.GroupBy(packet => packet.SegmentId).ToArray();
        var mic = groups.Where(group => group.First().SourceId == "mic").OrderBy(group => group.Min(packet => packet.Qpc100ns)).ToArray();
        var remote = groups.Where(group => group.First().SourceId == "remote").OrderBy(group => group.Min(packet => packet.Qpc100ns)).ToArray();
        if (mic.Length != 3 || remote.Length != 2 || groups.Length != 5 || boundaries.Count != 5)
            errors.Add("expected_three_mic_and_two_remote_segments");
        for (int index = 0; index < mic.Length; index++)
        {
            if (index >= 3) break;
            var open = controls[index == 0 ? 0 : index == 1 ? 2 : 4];
            CheckGroup(mic[index], open.RequestQpc100ns, index == 0 ? "mic_off" : index == 1 ? "pause" : "stop",
                index == 0 ? controls[1] : index == 1 ? controls[3] : null);
        }
        for (int index = 0; index < remote.Length; index++)
        {
            if (index >= 2) break;
            CheckGroup(remote[index], index == 0 ? epoch : controls[4].RequestQpc100ns,
                index == 0 ? "pause" : "stop", index == 0 ? controls[3] : null);
        }
        return errors;

        void CheckGroup(IGrouping<string, PacketRange> group, ulong minimumQpc, string operation, ControlObservation? control)
        {
            var matching = boundaries.Where(boundary => boundary.SegmentId == group.Key).ToArray();
            if (matching.Length != 1) { errors.Add("missing_or_duplicate_boundary:" + group.Key); return; }
            var boundary = matching[0];
            if (boundary.Operation != operation || (control is not null && boundary.RequestId != control.Ack.RequestId))
                errors.Add("boundary_control_mismatch:" + group.Key);
            if (control is not null)
            {
                var ackUpper = (Int128)epoch + control.Ack.EffectiveSessionUs * 10L + 9;
                if (boundary.CutoffQpc100ns > ackUpper || boundary.CutoffQpc100ns < control.RequestQpc100ns)
                    errors.Add("boundary_outside_request_ack:" + group.Key);
            }
            double seconds = 0;
            foreach (var packet in group)
            {
                if (packet.Rate <= 0 || packet.Frames <= 0 || packet.Offset < 0) { errors.Add("invalid_packet_range:" + group.Key); continue; }
                Int128 start = (Int128)packet.Qpc100ns * packet.Rate + (Int128)packet.Offset * AudioClock.UnitsPerSecond;
                Int128 end = start + (Int128)packet.Frames * AudioClock.UnitsPerSecond;
                if (start < (Int128)minimumQpc * packet.Rate) errors.Add("pcm_before_explicit_enable_or_resume:" + group.Key);
                if (end > (Int128)boundary.CutoffQpc100ns * packet.Rate) errors.Add("pcm_after_actual_capture_boundary:" + group.Key);
                seconds += packet.Frames / (double)packet.Rate;
            }
            if (seconds < 2.5) errors.Add("too_few_real_frames:" + group.Key);
        }
    }

    private static void CheckHealth(RecordingHealth[] health, ulong epoch, IReadOnlyList<ControlObservation> controls, List<string> errors)
    {
        if (health.Length < 5 || !health.Any(value => value.MicrophoneEnabled && value.MicFrames > 0)) errors.Add("missing_live_microphone_health");
        long onRequestUs = AudioClock.SessionMicroseconds(controls[0].RequestQpc100ns, epoch);
        if (health.Where(value => value.ElapsedUs < onRequestUs).Any(value => value.MicFrames != 0 || value.MicrophoneEnabled))
            errors.Add("initially_disabled_microphone_collected_frames");
        var off = health.Where(value => value.ElapsedUs > controls[1].Ack.EffectiveSessionUs &&
            value.ElapsedUs < AudioClock.SessionMicroseconds(controls[2].RequestQpc100ns, epoch)).ToArray();
        if (off.Length < 2 || off.Any(value => value.MicrophoneEnabled) || off.Select(value => value.MicFrames).Distinct().Count() != 1)
            errors.Add("mic_off_health_did_not_remain_stopped");
        if (off.Length < 2 || off[^1].RemoteFrames <= off[0].RemoteFrames)
            errors.Add("remote_did_not_continue_while_mic_off");
        var paused = health.Where(value => value.ElapsedUs > controls[3].Ack.EffectiveSessionUs &&
            value.ElapsedUs < AudioClock.SessionMicroseconds(controls[4].RequestQpc100ns, epoch)).ToArray();
        if (paused.Length < 2 || paused.Any(value => !value.Paused) || paused.Select(value => (value.MicFrames, value.RemoteFrames)).Distinct().Count() != 1)
            errors.Add("pause_health_did_not_stop_both_sources");
    }

    private static SignalStatistics ReadMicStatistics(CaptureResult capture)
    {
        long samples = 0, nonzero = 0;
        double peak = 0, square = 0;
        foreach (var track in capture.Tracks.Where(track => track.SourceId == "mic"))
        {
            using var reader = new WaveFileReader(track.WavePath);
            int size = track.Format.BitsPerSample / 8;
            byte[] buffer = new byte[4096 * track.Format.BlockAlign];
            int length;
            while ((length = reader.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (length % track.Format.BlockAlign != 0) throw new InvalidDataException("Unaligned microphone PCM.");
                for (int offset = 0; offset < length; offset += size)
                {
                    var span = buffer.AsSpan(offset, size);
                    double value = track.Format.IsFloat ? BitConverter.ToSingle(span) : size switch
                    {
                        2 => BinaryPrimitives.ReadInt16LittleEndian(span) / 32768d,
                        3 => ((span[0] | (span[1] << 8) | (span[2] << 16)) << 8 >> 8) / 8388608d,
                        4 => BinaryPrimitives.ReadInt32LittleEndian(span) / 2147483648d,
                        _ => throw new InvalidDataException("Unsupported microphone sample format.")
                    };
                    if (!double.IsFinite(value)) throw new InvalidDataException("Non-finite microphone sample.");
                    samples++; if (value != 0) nonzero++;
                    peak = Math.Max(peak, Math.Abs(value)); square += value * value;
                }
            }
        }
        return new(samples, nonzero, peak, samples == 0 ? 0 : Math.Sqrt(square / samples),
            peak > .0001 ? "NONZERO_SIGNAL_OBSERVED_CONTENT_NOT_INTERPRETED" : "SILENT_OR_QUIET_NO_SPEECH_REQUIRED");
    }

    /// <summary>Runs only pure range-checker fixtures; no enumeration, device activation or audio.</summary>
    internal static void RunSyntheticChecks()
    {
        const ulong epoch = 100_000_000;
        ulong At(int second) => epoch + (ulong)second * 10_000_000;
        ControlObservation Control(string operation, int request, int effective, string id) =>
            new(operation, At(request), new(id, operation, operation == "pause", operation != "mic_off", effective * 1_000_000));
        ControlObservation[] controls = [Control("mic_on", 2, 2, "on1"), Control("mic_off", 6, 6, "off"),
            Control("mic_on", 10, 10, "on2"), Control("pause", 13, 13, "pause"), Control("resume", 15, 15, "resume")];
        PacketRange[] packets = [new("mic1", "mic", 48000, At(2), 0, 4 * 48000),
            new("mic2", "mic", 48000, At(10), 0, 3 * 48000), new("mic3", "mic", 48000, At(15), 0, 3 * 48000),
            new("remote1", "remote", 48000, At(0), 0, 13 * 48000), new("remote2", "remote", 48000, At(15), 0, 3 * 48000)];
        Boundary[] boundaries = [new("mic1", "mic_off", "off", At(6)), new("mic2", "pause", "pause", At(13)),
            new("mic3", "stop", "stop", At(18)), new("remote1", "pause", "pause", At(13)), new("remote2", "stop", "stop", At(18))];
        Require(CheckRanges(packets, boundaries, controls, epoch).Count == 0, "Valid boundary ranges rejected.");
        var crossing = packets.ToArray(); crossing[0] = crossing[0] with { Frames = crossing[0].Frames + 1 };
        Require(CheckRanges(crossing, boundaries, controls, epoch).Any(error => error.StartsWith("pcm_after_actual_capture_boundary")), "Post-boundary frame accepted.");
        var forbidden = packets.ToArray(); forbidden[1] = forbidden[1] with { Qpc100ns = At(9) };
        Require(CheckRanges(forbidden, boundaries, controls, epoch).Any(error => error.StartsWith("pcm_before_explicit_enable_or_resume")), "PCM during mic-off accepted.");
        var wrongAck = boundaries.ToArray(); wrongAck[0] = wrongAck[0] with { RequestId = "unrelated" };
        Require(CheckRanges(packets, wrongAck, controls, epoch).Any(error => error.StartsWith("boundary_control_mismatch")), "Unrelated boundary acknowledgement accepted.");
        Require(CheckRanges(packets, boundaries.Take(4).ToArray(), controls, epoch).Count != 0, "Missing stop boundary accepted.");
        Require(CheckRanges(packets.Append(new("mic4", "mic", 48000, At(7), 0, 48000)).ToArray(), boundaries, controls, epoch).Count != 0,
            "Unrequested extra microphone segment accepted.");
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static ulong Now100ns() => (ulong)AudioClock.CounterTo100ns(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
    private sealed record ControlObservation(string Operation, ulong RequestQpc100ns, RecordingAck Ack);
    private sealed record PacketRange(string SegmentId, string SourceId, int Rate, ulong Qpc100ns, int Offset, int Frames);
    private sealed record Boundary(string SegmentId, string Operation, string RequestId, ulong CutoffQpc100ns);
    private sealed record BoundaryReport(bool Pass, int Segments, int Packets, int Boundaries, double MicSeconds, IReadOnlyList<string> Errors);
    private sealed record SignalStatistics(long Samples, long NonzeroSamples, double Peak, double Rms, string Observation);
}
