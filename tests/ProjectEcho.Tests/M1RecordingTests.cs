using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

/// <summary>
/// Drives the production capture thread and disk writer with in-memory sources and a controlled QPC.
/// No source enumeration, WASAPI activation, microphone access or playback occurs in this suite.
/// </summary>
internal static class M1RecordingTests
{
    private const string FixedMic = "synthetic-fixed-mic";
    private const ulong Epoch = 10_000_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    public static void Run(string output)
    {
        Lifecycle(Path.Combine(output, "m1-recording-lifecycle"));
        LowDisk(Path.Combine(output, "m1-recording-low-disk"));
        RefusesOverwrite(Path.Combine(output, "m1-recording-existing"));
        WriterFailure(Path.Combine(output, "m1-recording-writer-failure"));
        foreach (string operation in new[] { "pause", "mic_off" })
            foreach (bool failDispose in new[] { false, true })
                UnconfirmedStop(Path.Combine(output, $"m1-recording-{operation}-{(failDispose ? "dispose" : "stop")}-failure"), operation, failDispose);
        MicStartFailure(Path.Combine(output, "m1-recording-mic-start-failure"));
        ConcurrentStopControl(Path.Combine(output, "m1-recording-concurrent-stop"));
        DeviceClockClassification(Path.Combine(output, "m1-recording-device-clock"));
    }

    private static void Lifecycle(string directory)
    {
        var clock = new TestClock();
        var factory = new FakeFactory();
        var health = new ConcurrentQueue<RecordingHealth>();
        var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        FakeSource? gateSource = null;
        try
        {
            session.HealthChanged += health.Enqueue;
            Await(session.StartAsync(Options(directory)));
            var remote1 = factory.Source("remote", 0);
            Wait(() => health.Any(item => item.RemoteStatus == "尚未收到音频包"), "No-packet health was not published.");
            Require(factory.MicOpenCount == 0, "Mic disabled at startup must not open a mic source.");

            remote1.Push(Block(Epoch, 0, 480, 0, silent: true));
            clock.Set(Epoch + 100_000);
            Wait(() => remote1.Emitted == 1, "Remote silent packet was not consumed.");
            Await(session.ControlAsync("health-silent", "bookmark"));
            Wait(() => health.Any(item => item.RemoteStatus.Contains("SILENT", StringComparison.Ordinal)),
                "Acquired silence was confused with no packets.");

            // The second raw packet spans Pause's actual capture-thread cutoff: only its first 240 frames survive.
            remote1.Push(Block(Epoch + 100_000, 480, 480, 2000));
            clock.Set(Epoch + 150_000);
            Wait(() => remote1.Emitted == 2, "Cross-Pause packet was not queued.");
            var pause = Await(session.ControlAsync("pause-1", "pause"));
            Require(pause.Paused && !pause.MicrophoneEnabled && remote1.StopCalls == 1 && remote1.DisposeCalls == 1,
                "Pause acknowledged before source stop/dispose or changed mic consent.");
            int readsAfterPause = remote1.ReadCalls;
            remote1.Push(Block(Epoch + 200_000, 960, 480, 3000));
            Thread.Sleep(30);
            Require(remote1.ReadCalls == readsAfterPause, "Pause acknowledgement did not stop reads.");
            Require(factory.MicOpenCount == 0, "Disabled microphone was opened/read during Pause.");

            clock.Set(Epoch + 500_000);
            var resumed = Await(session.ControlAsync("resume-1", "resume"));
            Require(!resumed.Paused && !resumed.MicrophoneEnabled, "Resume changed explicit mic selection.");
            var remote2 = factory.Source("remote", 1);
            Require(!ReferenceEquals(remote1, remote2) && remote2.StartCalls == 1, "Resume reused the stopped source.");
            remote2.Push(Block(Epoch + 500_000, 0, 480, 4000));
            clock.Set(Epoch + 600_000);
            Wait(() => remote2.Emitted == 1, "Resumed source was not read.");

            Expect<InvalidOperationException>(() => Await(session.ControlAsync("wrong-mic", "mic_on", "other-device")));
            Expect<InvalidOperationException>(() => Await(session.ControlAsync("missing-mic", "mic_on")));
            Require(factory.MicOpenCount == 0, "A different microphone was opened before rejection.");
            var on = Await(session.ControlAsync("mic-on-1", "mic_on", FixedMic));
            var mic = factory.Source("mic", 0);
            Require(on.MicrophoneEnabled && factory.RequestedMics.SequenceEqual([FixedMic]),
                "Mic-on did not use precisely the chosen device.");
            mic.Push(Block(Epoch + 600_000, 0, 480, 5000));
            clock.Set(Epoch + 700_000);
            Wait(() => mic.Emitted == 1, "Mic packet was not read.");

            // 28542 * 48000 / 1e7 = 137.0016: save exactly 137 whole frames, never the crossing frame.
            mic.Push(Block(Epoch + 700_000, 480, 480, 6000));
            clock.Set(Epoch + 728_542);
            Wait(() => mic.Emitted == 2, "Cross-MicOff packet was not pending.");
            var off = Await(session.ControlAsync("mic-off-1", "mic_off"));
            Require(!off.MicrophoneEnabled && mic.StopCalls == 1 && mic.DisposeCalls == 1,
                "Mic-off acknowledged before its source actually stopped/released.");
            int micReadsAfterAck = mic.ReadCalls;
            mic.Push(Block(Epoch + 800_000, 960, 480, 12000));
            Thread.Sleep(30);
            Require(mic.ReadCalls == micReadsAfterAck && factory.MicOpenCount == 1,
                "Mic-off continued collection or silently replaced the device.");

            // The stop gate proves Completion does not report Saved merely when Stop is requested.
            // Tail block is partly after the fixed cutoff; production must retain exactly 240 frames.
            clock.Set(Epoch + 900_000);
            remote2.Push(Block(Epoch + 850_000, 480, 480, 7000));
            Wait(() => remote2.Emitted == 2, "Cross-Stop tail was not pending.");
            gateSource = remote2;
            remote2.BlockStop = true;
            var stop1 = session.StopAsync("stop-exact");
            var stop2 = session.StopAsync("stop-duplicate");
            Require(ReferenceEquals(stop1, stop2), "Repeated Stop must share one completion task.");
            Require(remote2.StopEntered.Wait(Timeout), "Capture source did not enter Stop.");
            Require(!stop1.IsCompleted && !File.Exists(Path.Combine(directory, "meeting.json")),
                "Session claimed completion before capture-thread boundary and writer finalization.");
            remote2.AllowStop.Set();
            var result = Await(stop1);
            Require(result.StorageComplete, "Synthetic valid boundary session failed: " + string.Join(" | ", result.Warnings));
            Require(ReferenceEquals(result, Await(session.StopAsync("stop-after-save"))), "Stop after completion changed the result.");
            Require(remote1.StopCalls == 1 && remote2.StopCalls == 1 && mic.StopCalls == 1,
                "Duplicate Stop repeated physical source stops.");
            Require(factory.RemoteOpenCount == 2 && factory.MicOpenCount == 1,
                "Unexpected source lifecycle changed recording scope.");
            VerifyFinalized(directory, result);

            var rows = Journal(directory);
            var segments = rows.Where(Is("segment")).ToArray();
            Require(segments.Count(row => Text(row, "sourceId") == "remote") == 2
                && segments.Count(row => Text(row, "sourceId") == "mic") == 1,
                "Resume did not persist a new segment.");
            string micSegment = Text(segments.Single(row => Text(row, "sourceId") == "mic"), "segmentId");
            var boundaries = rows.Where(Is("boundary")).ToArray();
            Require(boundaries.Length == 3 && boundaries.Count(row => Text(row, "operation") == "stop") == 1,
                "Stop/Pause/MicOff boundaries were missing or duplicated.");
            Require(Text(boundaries.Single(row => Text(row, "operation") == "mic_off"), "segmentId") == micSegment,
                "Mic boundary was assigned to another source.");
            foreach (var boundary in boundaries) VerifyPreCutoff(rows, boundary);
            Require(boundaries.Single(row => Text(row, "operation") == "pause").GetProperty("discardedFrameCount").GetInt64() == 240,
                "Pause did not account for its forbidden tail.");
            Require(boundaries.Single(row => Text(row, "operation") == "mic_off").GetProperty("discardedFrameCount").GetInt64() == 343,
                "Mic-off did not account for its crossing frame and later tail.");

            var remotePcm = ReadSource(directory, "remote");
            var micPcm = ReadSource(directory, "mic");
            var expectedRemote = new short[480].Concat(Samples(240, 2000)).Concat(Samples(480, 4000)).Concat(Samples(240, 7000)).ToArray();
            var expectedMic = Samples(480, 5000).Concat(Samples(137, 6000)).ToArray();
            Require(remotePcm.SequenceEqual(expectedRemote), "Remote saved samples lost a valid tail or included post-boundary PCM.");
            Require(micPcm.SequenceEqual(expectedMic), "Mic saved samples lost a valid tail or included post-boundary PCM.");
            Require(mic.ReadCalls == micReadsAfterAck, "Mic was read again during final remote Stop.");
        }
        finally
        {
            gateSource?.AllowStop.Set();
            Await(session.DisposeAsync().AsTask());
            MarkSynthetic(directory);
        }
    }

    private static void LowDisk(string directory)
    {
        var clock = new TestClock(); var factory = new FakeFactory(); long remaining = 1L << 40;
        var session = new RecordingSession(factory, clock.Read, _ => Interlocked.Read(ref remaining));
        try
        {
            Await(session.StartAsync(Options(directory)));
            var remote = factory.Source("remote", 0);
            remote.Push(Block(Epoch, 0, 480, 8000)); clock.Set(Epoch + 100_000);
            Wait(() => remote.Emitted == 1, "Low-disk fixture packet was not read.");
            Await(session.ControlAsync("before-low-disk", "bookmark"));
            Interlocked.Exchange(ref remaining, 1);
            clock.Set(Epoch + 5_100_000);
            var result = Await(session.Completion);
            Require(!result.StorageComplete && result.Warnings.Any(message => message.Contains("磁盘", StringComparison.Ordinal)),
                "Low disk did not end with an explicit degraded result.");
            Require(remote.StopCalls == 1 && remote.DisposeCalls == 1 && factory.MicOpenCount == 0,
                "Low disk did not safely stop exact selected sources.");
            VerifyFinalized(directory, result);
            Require(ReadSource(directory, "remote").SequenceEqual(Samples(480, 8000)), "Low disk discarded already accepted PCM.");
        }
        finally { Await(session.DisposeAsync().AsTask()); MarkSynthetic(directory); }
    }

    private static void RefusesOverwrite(string directory)
    {
        Directory.CreateDirectory(directory);
        string protectedFile = Path.Combine(directory, "existing.txt"); File.WriteAllText(protectedFile, "preserve me");
        var factory = new FakeFactory(); var session = new RecordingSession(factory, () => Epoch, _ => 1L << 40);
        try
        {
            Expect<IOException>(() => Await(session.StartAsync(Options(directory))));
            var result = Await(session.Completion);
            Require(!result.StorageComplete && factory.RemoteOpenCount == 0 && factory.MicOpenCount == 0,
                "Invalid output directory activated a source or reported Saved.");
            Require(File.ReadAllText(protectedFile) == "preserve me", "Existing originals were overwritten.");
        }
        finally { Await(session.DisposeAsync().AsTask()); MarkSynthetic(directory); }
    }

    private static void WriterFailure(string directory)
    {
        var clock = new TestClock();
        var factory = new FakeFactory();
        var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        try
        {
            var options = Options(directory);
            Await(session.StartAsync(options with { Selection = options.Selection with { MicrophoneEnabled = true } }));
            var remote = factory.Source("remote", 0);
            var mic = factory.Source("mic", 0);
            Require(Directory.EnumerateFiles(Path.Combine(directory, "sources", "remote"), "*.wav.partial").Count() == 1,
                "Writer did not prepare the real original before Start.");
            // Deliberately inconsistent synthetic metadata reaches the real writer's SILENT invariant.
            // This injects a writer validation failure during active recording, not an actual disk I/O failure.
            remote.Push(Block(Epoch, 0, 480, 9000) with { Flags = 2 }); clock.Set(Epoch + 100_000);
            var result = Await(session.Completion);
            Require(!result.StorageComplete && result.Warnings.Count != 0,
                "Writer failure was reported as saved or omitted from warnings.");
            Require(remote.StopCalls == 1 && remote.DisposeCalls == 1 && mic.StopCalls == 1 && mic.DisposeCalls == 1
                && factory.MicOpenCount == 1,
                "Writer failure did not stop all active collection.");
            Require(!File.Exists(result.ManifestPath), "Writer validation failure produced a successful manifest.");
            Require(remote.Emitted == 1 && !Directory.EnumerateFiles(directory, "*.wav", SearchOption.AllDirectories).Any(),
                "Rejected packet was finalized as valid original PCM.");
        }
        finally { Await(session.DisposeAsync().AsTask()); MarkSynthetic(directory); }
    }

    private static void UnconfirmedStop(string directory, string operation, bool failDispose)
    {
        var clock = new TestClock(); var factory = new FakeFactory();
        var acknowledgements = new ConcurrentQueue<RecordingAck>();
        var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        try
        {
            session.Acknowledged += acknowledgements.Enqueue;
            var options = Options(directory);
            Await(session.StartAsync(options with { Selection = options.Selection with { MicrophoneEnabled = true } }));
            var remote = factory.Source("remote", 0); var mic = factory.Source("mic", 0);
            remote.Push(Block(Epoch, 0, 480, 1000)); mic.Push(Block(Epoch, 0, 480, 2000));
            clock.Set(Epoch + 100_000);
            Wait(() => remote.Emitted == 1 && mic.Emitted == 1, "Unconfirmed-stop fixture packets were not read.");
            Await(session.ControlAsync("before-stop-fault", "bookmark"));
            mic.ThrowOnStop = !failDispose; mic.ThrowOnDispose = failDispose;
            string requestId = "must-not-ack-" + operation;
            Expect<IOException>(() => Await(session.ControlAsync(requestId, operation)));
            var result = Await(session.Completion);
            Require(!result.StorageComplete, "An unconfirmed source stop/release was reported complete.");
            Require(!acknowledgements.Any(ack => ack.RequestId == requestId),
                "Pause/MicOff emitted a successful acknowledgement despite unconfirmed stop/release.");
            Require(remote.StopCalls == 1 && remote.DisposeCalls == 1 && mic.StopCalls == 1 && mic.DisposeCalls == 1,
                "A source stop/dispose exception prevented cleanup of the other active source.");
            int remoteReads = remote.ReadCalls, micReads = mic.ReadCalls;
            Thread.Sleep(10);
            Require(remoteReads == remote.ReadCalls && micReads == mic.ReadCalls,
                "PCM collection continued after an unconfirmed shutdown ended the session.");
        }
        finally { Await(session.DisposeAsync().AsTask()); MarkSynthetic(directory); }
    }

    private static void MicStartFailure(string directory)
    {
        var clock = new TestClock(); int micAttempts = 0;
        var factory = new FakeFactory { MicrophoneStarting = () =>
            { if (Interlocked.Increment(ref micAttempts) == 1) throw new IOException("Synthetic mic Start failure."); } };
        var acknowledgements = new ConcurrentQueue<RecordingAck>();
        var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        try
        {
            session.Acknowledged += acknowledgements.Enqueue;
            Await(session.StartAsync(Options(directory)));
            var remote = factory.Source("remote", 0);
            remote.Push(Block(Epoch, 0, 480, 3000)); clock.Set(Epoch + 100_000);
            Wait(() => remote.Emitted == 1, "Mic-start-failure remote packet was not read.");
            Expect<IOException>(() => Await(session.ControlAsync("mic-start-fails", "mic_on", FixedMic)));
            var failedMic = factory.Source("mic", 0);
            Require(failedMic.StartCalls == 0 && failedMic.ReadCalls == 0 && failedMic.DisposeCalls == 1,
                "Failed mic Start left a readable or undisposed source.");
            Require(!acknowledgements.Any(ack => ack.RequestId == "mic-start-fails") && remote.StopCalls == 0,
                "Mic Start failure acknowledged success or unnecessarily closed remote capture.");
            Await(session.ControlAsync("remote-still-active", "bookmark"));
            Require(failedMic.ReadCalls == 0 && !session.Completion.IsCompleted,
                "Failed mic remained in the active drain loop or terminated unrelated remote capture.");
            var retry = Await(session.ControlAsync("mic-start-retry", "mic_on", FixedMic));
            var replacement = factory.Source("mic", 1);
            Require(retry.MicrophoneEnabled && replacement.StartCalls == 1 && failedMic.ReadCalls == 0
                && factory.RequestedMics.SequenceEqual([FixedMic, FixedMic]),
                "Explicit same-device retry reused the failed source or silently changed microphones.");
            replacement.Push(Block(Epoch + 100_000, 0, 480, 4000)); clock.Set(Epoch + 200_000);
            Wait(() => replacement.Emitted == 1, "Retried mic source was not read.");
            Await(session.StopAsync("stop-after-mic-retry"));
            Require(failedMic.ReadCalls == 0 && failedMic.DisposeCalls == 1 && replacement.DisposeCalls == 1,
                "Final Stop reused or double-disposed the failed mic.");
        }
        finally { Await(session.DisposeAsync().AsTask()); MarkSynthetic(directory); }
    }

    private static void ConcurrentStopControl(string directory)
    {
        var clock = new TestClock(); var factory = new FakeFactory();
        var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        try
        {
            Await(session.StartAsync(Options(directory)));
            var remote = factory.Source("remote", 0);
            remote.Push(Block(Epoch, 0, 480, 6000)); clock.Set(Epoch + 100_000);
            Wait(() => remote.Emitted == 1, "Concurrent-stop packet was not read.");
            var repeated1 = session.ControlAsync("same-bookmark", "bookmark");
            var repeated2 = session.ControlAsync("same-bookmark", "bookmark");
            Require(ReferenceEquals(repeated1, repeated2), "Same request ID must share one acknowledgement task.");
            Await(repeated1);
            Expect<InvalidOperationException>(() => Await(session.ControlAsync("same-bookmark", "pause")));

            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int settledControls = 0;
            var calls = Enumerable.Range(0, 24).Select(async index =>
            {
                await launch.Task.ConfigureAwait(false);
                try { await session.ControlAsync("racing-" + index, "bookmark").ConfigureAwait(false); }
                catch (InvalidOperationException) { }
                catch (OperationCanceledException) { }
                Interlocked.Increment(ref settledControls);
            }).ToList();
            for (int index = 0; index < 4; index++)
            {
                int stopIndex = index;
                calls.Add(StopAfterLaunch());
                async Task StopAfterLaunch()
                { await launch.Task.ConfigureAwait(false); await session.StopAsync("racing-stop-" + stopIndex).ConfigureAwait(false); }
            }
            launch.SetResult();
            Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            Require(settledControls == 24 && session.Completion.IsCompletedSuccessfully,
                "A concurrent Stop/Control call was left pending after session completion.");
            Require(remote.StopCalls == 1 && remote.DisposeCalls == 1, "Racing Stops repeated source cleanup.");
            var events = File.ReadLines(Path.Combine(directory, "journal", "events.jsonl"))
                .Select(line => { using var doc = JsonDocument.Parse(line); return doc.RootElement.GetProperty("data").Clone(); }).ToArray();
            Require(events.Count(item => Text(item, "requestId") == "same-bookmark") == 1,
                "Duplicate request wrote duplicate bookmark events.");
            Expect<InvalidOperationException>(() => Await(session.ControlAsync("after-stop", "bookmark")));
        }
        finally { Await(session.DisposeAsync().AsTask()); MarkSynthetic(directory); }
    }

    private static RecordingOptions Options(string directory) => new(new CaptureOptions(SourceMode.ProcessTree, null,
        null, false, false, FixedMic, directory, "SYNTHETIC M1 capture-thread test"), MaximumSeconds: 60, ExpectedMinutes: 1);

    private static void DeviceClockClassification(string root)
    {
        var cases = new[]
        {
            (Name: "process-all-zero", Mode: SourceMode.ProcessTree, Mic: false, Device: new ulong[] { 0, 0, 0 }, Limited: true),
            (Name: "endpoint-all-zero", Mode: SourceMode.EndpointLoopback, Mic: false, Device: new ulong[] { 0, 0, 0 }, Limited: false),
            (Name: "mic-all-zero", Mode: SourceMode.ProcessTree, Mic: true, Device: new ulong[] { 0, 0, 0 }, Limited: false),
            (Name: "process-nonzero-regression", Mode: SourceMode.ProcessTree, Mic: false, Device: new ulong[] { 960, 480, 960 }, Limited: false),
            (Name: "process-zero-then-progress", Mode: SourceMode.ProcessTree, Mic: false, Device: new ulong[] { 0, 0, 480 }, Limited: false)
        };
        foreach (var item in cases)
        {
            string directory = Path.Combine(root, item.Name);
            var clock = new TestClock(); var factory = new FakeFactory();
            var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
            try
            {
                var options = Options(directory);
                options = options with { Selection = options.Selection with
                    { Mode = item.Mode, MicrophoneEnabled = item.Mic, EndpointScopeConfirmed = item.Mode == SourceMode.EndpointLoopback } };
                Await(session.StartAsync(options));
                var remote = factory.Source("remote", 0);
                var measured = item.Mic ? factory.Source("mic", 0) : remote;
                var health = new CaptureClockHealth();
                for (int index = 0; index < item.Device.Length; index++)
                {
                    ulong qpc = Epoch + (ulong)index * 100_000;
                    var quality = health.Observe(checked((long)item.Device[index]), checked((long)qpc), 480, false, Epoch);
                    measured.Push(Block(qpc, item.Device[index], 480, 1000 + index * 1000) with
                        { TimestampValid = quality.QpcValid, DevicePositionValid = quality.DevicePositionValid });
                    if (item.Mic) remote.Push(Block(qpc, (ulong)index * 480, 480, 7000));
                }
                clock.Set(Epoch + 300_000);
                Wait(() => measured.Emitted == 3 && (!item.Mic || remote.Emitted == 3), "Clock-classification packets were not consumed.");
                Await(session.ControlAsync("after-clock-packets", "bookmark"));
                var result = Await(session.StopAsync());
                Require(result.StorageComplete == item.Limited,
                    $"{item.Name}: device anomaly was classified as a known limitation, or known limitation failed storage.");
                bool anomaly = result.Warnings.Any(message => message.Contains("设备帧位置异常", StringComparison.Ordinal));
                bool limitation = result.Warnings.Any(message => message.Contains("设备帧计数不可依赖", StringComparison.Ordinal));
                Require(anomaly == !item.Limited && limitation == item.Limited,
                    $"{item.Name}: warning must distinguish an anomalous counter from the known process-loopback limitation.");
                VerifyFinalized(directory, result);
                var disk = M1Recovery.Read(directory);
                Require(disk.IsCleanEnd == result.StorageComplete && !disk.Capture.Complete,
                    "Storage success must not promote unavailable/abnormal device clocks into legacy Complete=true.");
                var rows = Journal(directory);
                string sourceId = item.Mic ? "mic" : "remote";
                string segment = Text(rows.Single(row => Is("segment")(row) && Text(row, "sourceId") == sourceId), "segmentId");
                var packets = rows.Where(row => Is("packet")(row) && Text(row, "segmentId") == segment).ToArray();
                Require(packets.Select(row => ulong.Parse(Text(row, "deviceFramePosition"), CultureInfo.InvariantCulture)).SequenceEqual(item.Device),
                    "Device anomaly handling replaced or omitted raw API device positions.");
                Require(packets.Select(row => ulong.Parse(Text(row, "qpcPosition100ns"), CultureInfo.InvariantCulture))
                    .SequenceEqual(new[] { Epoch, Epoch + 100_000, Epoch + 200_000 }), "Device anomaly handling changed valid API QPC.");
                Require(ReadSource(directory, sourceId).SequenceEqual(Samples(480, 1000).Concat(Samples(480, 2000)).Concat(Samples(480, 3000))),
                    "Device anomaly handling destroyed recorded PCM with independently valid QPC.");
            }
            finally { Await(session.DisposeAsync().AsTask()); MarkSynthetic(directory); }
        }
    }
    private static short[] Samples(int frames, int seed) => Enumerable.Range(0, frames).Select(index => (short)(seed + index)).ToArray();
    private static RecordingBlock Block(ulong qpc, ulong device, int frames, int seed, bool silent = false)
    {
        byte[] pcm = new byte[frames * 2];
        if (!silent)
            for (int index = 0; index < frames; index++) BitConverter.TryWriteBytes(pcm.AsSpan(index * 2, 2), (short)(seed + index));
        return new(pcm, frames, frames, device, qpc, silent ? 2 : 0, true, true, 0);
    }

    private static void VerifyFinalized(string directory, RecordingResult result)
    {
        Require(File.Exists(result.ManifestPath), "Completion returned before meeting.json existed.");
        Require(!Directory.EnumerateFiles(directory, "*.partial", SearchOption.AllDirectories).Any(), "Completion left a partial WAV/manifest.");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath));
        Require(manifest.RootElement.GetProperty("storageComplete").GetBoolean() == result.StorageComplete,
            "Saved result disagrees with persisted storage state.");
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
        Require(Journal(directory).Last().GetProperty("type").GetString() == "checkpoint", "Final storage checkpoint is missing.");
    }
    private static List<JsonElement> Journal(string directory) => File.ReadLines(Path.Combine(directory, "journal", "capture-index.jsonl"))
        .Select(line => { using var doc = JsonDocument.Parse(line); return doc.RootElement.GetProperty("data").Clone(); }).ToList();
    private static Func<JsonElement, bool> Is(string type) => row => Text(row, "type") == type;
    private static string Text(JsonElement row, string property) => row.GetProperty(property).GetString()!;
    private static void VerifyPreCutoff(List<JsonElement> rows, JsonElement boundary)
    {
        string segment = Text(boundary, "segmentId");
        ulong cutoff = ulong.Parse(Text(boundary, "effectiveQpc100ns"), CultureInfo.InvariantCulture);
        foreach (var packet in rows.Where(row => Is("packet")(row) && Text(row, "segmentId") == segment))
        {
            ulong qpc = ulong.Parse(Text(packet, "qpcPosition100ns"), CultureInfo.InvariantCulture);
            int offset = packet.GetProperty("retainedFrameOffset").GetInt32(), count = packet.GetProperty("frameCount").GetInt32();
            Require((Int128)qpc * 48000 + (Int128)(offset + count) * 10_000_000 <= (Int128)cutoff * 48000,
                "PCM extends beyond the actual capture-thread boundary.");
        }
    }
    private static short[] ReadSource(string directory, string source)
    {
        var samples = new List<short>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(directory, "sources", source), "*.wav").Order(StringComparer.Ordinal))
        {
            using var wav = new WaveFileReader(path);
            Require(wav.WaveFormat.SampleRate == 48000 && wav.WaveFormat.Channels == 1 && wav.WaveFormat.BitsPerSample == 16,
                "Unexpected synthetic original format.");
            byte[] bytes = new byte[checked((int)wav.Length)]; int read = 0;
            while (read < bytes.Length)
            { int count = wav.Read(bytes, read, bytes.Length - read); Require(count > 0, "WAV ended before its declared PCM."); read += count; }
            for (int index = 0; index < bytes.Length; index += 2) samples.Add(BitConverter.ToInt16(bytes, index));
        }
        return samples.ToArray();
    }
    private static void MarkSynthetic(string directory)
    {
        if (Directory.Exists(directory)) File.WriteAllText(Path.Combine(directory, "TEST_FIXTURE"),
            "SYNTHETIC in-memory sources driven through production RecordingSession. No audio device was opened; not Windows capture evidence.");
    }
    private static void Await(Task task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();
    private static T Await<T>(Task<T> task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();
    private static void Wait(Func<bool> predicate, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate()) { if (timer.Elapsed >= Timeout) throw new InvalidOperationException(failure); Thread.Sleep(1); }
    }
    private static void Require(bool condition, string failure) { if (!condition) throw new InvalidOperationException(failure); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }

    private sealed class TestClock
    {
        private long value = (long)Epoch;
        public ulong Read() => checked((ulong)Interlocked.Read(ref value));
        public void Set(ulong timestamp) => Interlocked.Exchange(ref value, checked((long)timestamp));
    }
    private sealed class FakeFactory : IRecordingSourceFactory
    {
        private readonly ConcurrentQueue<FakeSource> opened = new();
        public ConcurrentQueue<string> RequestedMics { get; } = new();
        public Action? RemoteStarting { get; init; }
        public Action? MicrophoneStarting { get; init; }
        public int RemoteOpenCount => opened.Count(source => source.Id == "remote");
        public int MicOpenCount => opened.Count(source => source.Id == "mic");
        public FakeSource Source(string id, int index) => opened.Where(source => source.Id == id).ElementAt(index);
        public void Preflight(RecordingOptions options) { }
        public void ValidateRemote(CaptureOptions options) { }
        public IRecordingAudioSource OpenRemote(CaptureOptions options)
        { var source = new FakeSource("remote", RemoteStarting); opened.Enqueue(source); return source; }
        public IRecordingAudioSource OpenMicrophone(string deviceId)
        { RequestedMics.Enqueue(deviceId); var source = new FakeSource("mic", MicrophoneStarting); opened.Enqueue(source); return source; }
    }
    private sealed class FakeSource(string id, Action? starting = null) : IRecordingAudioSource
    {
        private readonly ConcurrentQueue<RecordingBlock> blocks = new();
        private int readCalls, emitted, starts, stops, disposes;
        public string Id { get; } = id;
        public WaveFormat WaveFormat { get; } = new WaveFormat(48000, 16, 1);
        public PcmFormat Format { get; } = new(48000, 1, 16, false, 2);
        public int ReadCalls => Volatile.Read(ref readCalls);
        public int Emitted => Volatile.Read(ref emitted);
        public int StartCalls => Volatile.Read(ref starts);
        public int StopCalls => Volatile.Read(ref stops);
        public int DisposeCalls => Volatile.Read(ref disposes);
        public bool BlockStop, ThrowOnStop, ThrowOnDispose;
        public ManualResetEventSlim StopEntered { get; } = new();
        public ManualResetEventSlim AllowStop { get; } = new();
        public void Push(RecordingBlock block) => blocks.Enqueue(block);
        public void Start() { starting?.Invoke(); Interlocked.Increment(ref starts); }
        public void Stop()
        {
            Interlocked.Increment(ref stops); StopEntered.Set();
            if (BlockStop && !AllowStop.Wait(Timeout)) throw new TimeoutException("Synthetic Stop gate timed out.");
            if (ThrowOnStop) throw new IOException("Synthetic source Stop failure.");
        }
        public RecordingBlock? Read(ulong epochQpc100ns, ulong? cutoffQpc100ns)
        {
            Interlocked.Increment(ref readCalls);
            Require(StartCalls == 1 && DisposeCalls == 0, "Read occurred before Start or after Dispose.");
            if (!blocks.TryDequeue(out var block)) return null;
            // Deliberately return the whole in-memory block, exercising production's second boundary filter.
            Interlocked.Increment(ref emitted); return block;
        }
        public void Dispose()
        { Interlocked.Increment(ref disposes); if (ThrowOnDispose) throw new IOException("Synthetic source Dispose failure."); }
    }
}
