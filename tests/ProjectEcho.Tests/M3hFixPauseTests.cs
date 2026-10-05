using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Export;

/// <summary>
/// Production capture thread, writer, recovery and codecs with in-memory PCM and an explicit QPC.
/// These checks never enumerate, activate, record or play a real audio device.
/// </summary>
internal static class M3hFixPauseTests
{
    private const ulong Epoch = 90_000_000;
    private const string Mic = "synthetic-m3hfix-microphone";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3hfix-pause");
        run("T08/T15/T51/T52-M3Hfix pause acknowledges stopped dual sources and retains only pre-boundary queued PCM",
            () => CaptureBoundary(Path.Combine(root, "capture-boundary")));
        run("T15/T19/T22-M3Hfix repeated pauses freeze health bookmarks duration and preserve explicit mic-off on resume",
            () => MultiplePauses(Path.Combine(root, "multiple-pauses")));
        run("T15/T22-M3Hfix delayed resume initialization is excluded from recording time and PCM placement",
            () => ResumePreparation(Path.Combine(root, "resume-preparation"), stopInstead: false));
        run("T08/T15/T51-M3Hfix Stop during delayed resume never starts a new source or extends paused duration",
            () => ResumePreparation(Path.Combine(root, "stop-during-resume"), stopInstead: true));
        run("T08/T15/T22-M3Hfix resumed microphone preparation keeps both sources stopped and retains consent",
            () => ResumeMicrophonePreparation(Path.Combine(root, "resume-microphone-preparation")));
        run("T15/T22-M3Hfix failed resume remains frozen until successful explicit retry",
            () => ResumeFailure(Path.Combine(root, "resume-failure")));
        run("T15/T22/T28/T29-M3Hfix WAV and actual MP3 join resumed PCM without pause padding and preserve EOF",
            () => CompressedTimeline(Path.Combine(root, "compressed-timeline")));
        foreach (var item in new[] { (Name: "exact", Early100ns: 100_000L, Jitter100ns: 0L),
            (Name: "fractional", Early100ns: 100_001L, Jitter100ns: 0L),
            (Name: "device-jitter", Early100ns: 100_000L, Jitter100ns: 625L),
            (Name: "device-negative-jitter", Early100ns: 100_000L, Jitter100ns: -625L) })
        {
            var fixture = item;
            run("T08/T15/T22/T52-M3Hfix resumed raw packet prefix excludes paused PCM and retains raw clocks " + fixture.Name,
                () => ResumeRawPrefix(Path.Combine(root, "resume-prefix-" + fixture.Name), fixture.Early100ns, fixture.Jitter100ns));
        }
        run("T15/T22-M3Hfix maximum recording duration counts active time across a long pause",
            () => MaximumActiveDuration(Path.Combine(root, "maximum-active-duration")));
        run("T15/T22/T52-M3Hfix offline pause audit keeps raw capture boundaries separate from recording duration",
            M1IntegrationProbe.RunTimelineSyntheticChecks);
    }

    private static void CaptureBoundary(string folder)
    {
        var clock = new Clock(); var factory = new Factory();
        var health = new ConcurrentQueue<RecordingHealth>();
        var acknowledgements = new ConcurrentQueue<RecordingAck>();
        var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        Source? held = null;
        try
        {
            session.HealthChanged += health.Enqueue; session.Acknowledged += acknowledgements.Enqueue;
            Await(session.StartAsync(Options(folder, true)));
            var remote = factory.Source("remote", 0); var mic = factory.Source("mic", 0);
            PushBoth(remote, mic, Block(Epoch, 0, 480, 11), Block(Epoch, 0, 480, 13));
            clock.Set(Epoch + 100_000); Wait(() => remote.Emitted == 1 && mic.Emitted == 1, "First dual-source packets not read.");
            byte[] expectedRemote = NoiseBytes(480, 11).Concat(NoiseBytes(360, 17)).ToArray();
            byte[] expectedMic = NoiseBytes(480, 13).Concat(NoiseBytes(360, 19)).ToArray();
            PushBoth(remote, mic, Block(Epoch + 100_000, 480, 480, 17), Block(Epoch + 100_000, 480, 480, 19));
            clock.Set(Epoch + 150_000); Wait(() => remote.Emitted == 2 && mic.Emitted == 2, "Cross-boundary packets not queued.");

            held = mic; mic.BlockStop = true;
            // Releasing native services may itself be slow. The pause timestamp
            // belongs to stopped collection, not to final COM/disposal latency.
            mic.Disposing = () => clock.Set(Epoch + 1_175_000);
            var pause = session.ControlAsync("pause-actual-boundary", "pause");
            Need(mic.StopEntered.Wait(Timeout), "Pause never entered microphone Stop.");
            Need(!pause.IsCompleted && !acknowledgements.Any(a => a.RequestId == "pause-actual-boundary"),
                "Pause acknowledged before the capture-thread Stop completed.");
            // Packets still queued by the fake API are deliberately returned whole. The
            // production boundary filter must discard them after both sources stop.
            PushBoth(remote, mic, Block(Epoch + 200_000, 960, 480, 23), Block(Epoch + 200_000, 960, 480, 29));
            clock.Set(Epoch + 175_000); mic.AllowStop.Set();
            var paused = Await(pause);
            Need(paused.Paused && paused.MicrophoneEnabled && paused.EffectiveSessionUs == 17_500,
                "Pause used request time instead of the actual capture-thread boundary, or changed mic consent.");
            Need(remote.Stops == 1 && mic.Stops == 1 && remote.Disposes == 1 && mic.Disposes == 1,
                "Pause acknowledged before both selected sources stopped and released.");
            int remoteReads = remote.Reads, micReads = mic.Reads;
            clock.Set(Epoch + 70_000_000);
            var marker = Await(session.ControlAsync("paused-bookmark", "bookmark"));
            Need(marker.EffectiveSessionUs == 17_500, "Bookmark advanced during pause.");
            Wait(() => health.Any(h => h.Paused && h.ElapsedUs == 17_500 && h.RemotePeak == 0 && h.MicPeak == 0),
                "Paused health is not frozen and zero-level.");
            var result = Await(session.StopAsync("stop-while-paused"));
            Need(result.DurationUs == 17_500 && result.StorageComplete, "Stop while paused appended elapsed silence or failed storage.");
            Need(remote.Reads == remoteReads && mic.Reads == micReads && factory.Count("remote") == 1 && factory.Count("mic") == 1,
                "Paused session continued PCM reads or opened another device.");
            Need(ReadSourceBytes(folder, "remote").SequenceEqual(expectedRemote) && ReadSourceBytes(folder, "mic").SequenceEqual(expectedMic),
                "Pre-boundary queued frames were lost or post-boundary microphone PCM persisted.");
            var recovered = M1Recovery.Read(folder);
            Need(recovered.IsCleanEnd && recovered.Issues.Count == 0 && recovered.Capture.Duration100ns == 175_000,
                "Disk reconstruction appended paused time or rejected the exact cutoffs.");
            var boundaries = Journal(folder).Where(row => Text(row, "type") == "boundary").ToArray();
            Need(boundaries.Length == 2 && boundaries.All(b => Text(b, "operation") == "pause" &&
                ulong.Parse(Text(b, "effectiveQpc100ns"), CultureInfo.InvariantCulture) == Epoch + 175_000),
                "Pause did not preserve raw dual-source cutoff QPC values.");
            Evidence(folder, new { result.DurationUs, recovered.Capture.Duration100ns, retainedFramesPerSource = 840,
                pausedWall100ns = 69_825_000, captureThreadBoundaryAcknowledged = true, micPcmAfterBoundary = false });
        }
        finally { held?.AllowStop.Set(); Await(session.DisposeAsync().AsTask()); }
    }

    private static void MultiplePauses(string folder)
    {
        var clock = new Clock(); var factory = new Factory(); var health = new ConcurrentQueue<RecordingHealth>();
        var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        try
        {
            session.HealthChanged += health.Enqueue;
            Await(session.StartAsync(Options(folder, true)));
            var remote1 = factory.Source("remote", 0); var mic = factory.Source("mic", 0);
            PushBoth(remote1, mic, Block(Epoch, 0, 4800, 31), Block(Epoch, 0, 4800, 37));
            clock.Set(Epoch + 1_000_000); Wait(() => remote1.Emitted == 1 && mic.Emitted == 1, "Initial multi-pause packets missing.");
            Need(Await(session.ControlAsync("pause-1", "pause")).EffectiveSessionUs == 100_000, "First pause timing mismatch.");
            clock.Set(Epoch + 11_000_000);
            Need(Await(session.ControlAsync("pause-repeat", "pause")).EffectiveSessionUs == 100_000, "Repeated pause reset the frozen clock.");
            Need(Await(session.ControlAsync("bookmark-paused-1", "bookmark")).EffectiveSessionUs == 100_000, "Paused bookmark moved.");
            var off = Await(session.ControlAsync("mic-off-paused", "mic_off"));
            Need(off.Paused && !off.MicrophoneEnabled && off.EffectiveSessionUs == 100_000, "MicOff while paused changed clock/state.");
            var resume1 = Await(session.ControlAsync("resume-1", "resume"));
            Need(!resume1.Paused && !resume1.MicrophoneEnabled && resume1.EffectiveSessionUs == 100_000 && factory.Count("mic") == 1,
                "Resume restarted the microphone after an explicit paused MicOff or counted paused time.");
            var remote2 = factory.Source("remote", 1); remote2.Push(Block(Epoch + 11_000_000, 0, 2400, 41));
            clock.Set(Epoch + 11_500_000); Wait(() => remote2.Emitted == 1, "First resumed packet not read.");
            Need(Await(session.ControlAsync("pause-2", "pause")).EffectiveSessionUs == 150_000, "Second pause reset instead of accumulating active time.");
            clock.Set(Epoch + 14_500_000);
            Need(Await(session.ControlAsync("bookmark-paused-2", "bookmark")).EffectiveSessionUs == 150_000, "Second paused bookmark moved.");
            Need(Await(session.ControlAsync("resume-2", "resume")).EffectiveSessionUs == 150_000, "Second resume counted paused duration.");
            var remote3 = factory.Source("remote", 2); remote3.Push(Block(Epoch + 14_500_000, 0, 7200, 43));
            clock.Set(Epoch + 16_000_000); Wait(() => remote3.Emitted == 1, "Second resumed packet not read.");
            Need(Await(session.ControlAsync("bookmark-final", "bookmark")).EffectiveSessionUs == 300_000, "Final bookmark differs from active time.");
            var result = Await(session.StopAsync());
            Need(result.StorageComplete && result.DurationUs == 300_000, "Multi-pause completion included removed intervals.");
            Need(factory.Count("remote") == 3 && factory.Count("mic") == 1 && mic.Stops == 1 && mic.Disposes == 1,
                "Resume reused a stopped segment or silently restarted microphone collection.");
            var disk = M1Recovery.Read(folder);
            Need(disk.IsCleanEnd && disk.Issues.Count == 0 && disk.Capture.Duration100ns == 3_000_000,
                "Multi-pause disk duration does not match the acknowledged timeline.");
            Need(disk.Capture.Tracks.Where(t => t.SourceId == "remote").SelectMany(t => t.Packets).Select(p => p.QpcPosition100ns)
                .SequenceEqual(new[] { Epoch, Epoch + 11_000_000, Epoch + 14_500_000 }), "Pause removal rewrote original packet QPC.");
            Need(Events(folder).Where(e => Text(e, "operation") == "bookmark").Select(e => e.GetProperty("sessionUs").GetInt64())
                .SequenceEqual(new long[] { 100_000, 150_000, 300_000 }), "Persisted bookmarks do not use the compact recording timeline.");
            Need(health.Any(h => h.Paused && h.ElapsedUs == 100_000) && health.Any(h => h.Paused && h.ElapsedUs == 150_000),
                "UI health never observed the two exact frozen times.");
            Evidence(folder, new { result.DurationUs, originalWall100ns = 16_000_000, removedPause100ns = 13_000_000,
                microphoneReopenedAfterExplicitOff = false, originalQpcPreserved = true });
        }
        finally { Await(session.DisposeAsync().AsTask()); }
    }

    private static void ResumePreparation(string folder, bool stopInstead)
    {
        var clock = new Clock(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var factory = new Factory { OpeningRemote = attempt => { if (attempt == 2) { entered.Set(); Need(release.Wait(Timeout), "Resume gate timed out."); } } };
        var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        var health = new ConcurrentQueue<RecordingHealth>(); session.HealthChanged += health.Enqueue;
        try
        {
            Await(session.StartAsync(Options(folder, false)));
            var first = factory.Source("remote", 0); first.Push(Block(Epoch, 0, 4800, 47));
            clock.Set(Epoch + 1_000_000); Wait(() => first.Emitted == 1, "Preparation fixture packet not consumed.");
            Await(session.ControlAsync("pause", "pause")); clock.Set(Epoch + 6_000_000);
            var resume = session.ControlAsync("resume-delayed", "resume");
            Need(entered.Wait(Timeout) && !resume.IsCompleted, "Resume did not wait for actual source readiness.");
            clock.Set(Epoch + 36_000_000);
            Need(factory.Count("remote") == 1 && first.Reads > 0 && first.Disposes == 1, "Initialization unexpectedly started or reused a source.");
            if (stopInstead)
            {
                var stop = session.StopAsync("stop-before-resume-ready"); release.Set();
                Expect<OperationCanceledException>(() => Await(resume));
                var result = Await(stop);
                Need(result.DurationUs == 100_000 && factory.Source("remote", 1).Starts == 0,
                    "Stop during resume activated a source or included initialization/paused time.");
                Need(ReadSourceBytes(folder, "remote").Length == 4800 * 2, "Stop during resume lost the saved prefix or wrote late PCM.");
                Evidence(folder, new { result.DurationUs, resumeSourceStarted = false, rawElapsed100ns = 36_000_000 });
            }
            else
            {
                release.Set(); var resumed = Await(resume);
                Need(!resumed.Paused && resumed.EffectiveSessionUs == 100_000, "Source initialization waiting entered active duration.");
                var second = factory.Source("remote", 1); Need(second.Starts == 1, "Successful resume did not start the prepared source.");
                second.Push(Block(Epoch + 36_000_000, 0, 4800, 53)); clock.Set(Epoch + 37_000_000);
                Wait(() => second.Emitted == 1, "Prepared resumed source did not supply PCM.");
                var result = Await(session.StopAsync()); var disk = M1Recovery.Read(folder);
                Need(result.StorageComplete && result.DurationUs == 200_000 && disk.Capture.Duration100ns == 2_000_000 && disk.Issues.Count == 0,
                    "Delayed-resume packet placement or saved duration includes initialization silence.");
                var elapsed = health.Select(item => item.ElapsedUs).ToArray();
                Need(elapsed.Zip(elapsed.Skip(1)).All(pair => pair.First <= pair.Second),
                    "A health snapshot sampled before delayed Resume overwrote its acknowledged recording time with an older value.");
                var mix = M2ExportService.RenderMeeting(folder, null); using var reader = new M2WaveReader(mix.WavePath);
                Need(reader.FrameCount == 9600, "Delayed resume inserted a silent hole into the listening WAV.");
                Evidence(folder, new { result.DurationUs, reader.FrameCount, preparationExcluded100ns = 30_000_000, noAudioDevices = true });
            }
        }
        finally { release.Set(); Await(session.DisposeAsync().AsTask()); }
    }

    private static void ResumeFailure(string folder)
    {
        var clock = new Clock(); var factory = new Factory { OpeningRemote = attempt =>
            { if (attempt == 2) { clock.Set(Epoch + 16_000_000); throw new IOException("Synthetic resume source failure."); } } };
        var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        try
        {
            Await(session.StartAsync(Options(folder, false)));
            var source = factory.Source("remote", 0); source.Push(Block(Epoch, 0, 4800, 59));
            clock.Set(Epoch + 1_000_000); Wait(() => source.Emitted == 1, "Failure fixture packet missing.");
            Await(session.ControlAsync("pause", "pause")); clock.Set(Epoch + 6_000_000);
            Expect<IOException>(() => Await(session.ControlAsync("resume-fails", "resume")));
            var stillPaused = Await(session.ControlAsync("after-failed-resume", "bookmark"));
            Need(stillPaused.Paused && stillPaused.EffectiveSessionUs == 100_000 && factory.Count("remote") == 1,
                "Failed resume advanced the frozen clock or opened another source.");
            clock.Set(Epoch + 21_000_000); Need(Await(session.ControlAsync("resume-retry", "resume")).EffectiveSessionUs == 100_000,
                "Explicit retry included the failed initialization wait.");
            var retry = factory.Source("remote", 1); retry.Push(Block(Epoch + 21_000_000, 0, 4800, 61));
            clock.Set(Epoch + 22_000_000); Wait(() => retry.Emitted == 1, "Retried source did not supply PCM.");
            var result = Await(session.StopAsync());
            Need(result.DurationUs == 200_000 && M1Recovery.Read(folder).Capture.Duration100ns == 2_000_000,
                "Successful retry failed to preserve compact duration.");
            Evidence(folder, new { result.DurationUs, failedResumeStayedPaused = true, exactSourceRetried = true });
        }
        finally { Await(session.DisposeAsync().AsTask()); }
    }

    private static void ResumeMicrophonePreparation(string folder)
    {
        var clock = new Clock(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var factory = new Factory { PreparingMicrophone = attempt =>
            { if (attempt == 2) { entered.Set(); Need(release.Wait(Timeout), "Resumed mic preparation gate timed out."); } } };
        var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        try
        {
            Await(session.StartAsync(Options(folder, true)));
            var remote1 = factory.Source("remote", 0); var mic1 = factory.Source("mic", 0);
            PushBoth(remote1, mic1, Block(Epoch, 0, 4800, 73), Block(Epoch, 0, 4800, 79));
            clock.Set(Epoch + 1_000_000); Wait(() => remote1.Emitted == 1 && mic1.Emitted == 1, "Mic resume initial packets missing.");
            Need(Await(session.ControlAsync("pause", "pause")).MicrophoneEnabled, "Pause lost microphone consent.");
            clock.Set(Epoch + 6_000_000); var resume = session.ControlAsync("resume", "resume");
            Need(entered.Wait(Timeout), "Resume did not prepare the consented microphone.");
            var remote2 = factory.Source("remote", 1);
            Need(!resume.IsCompleted && remote2.Starts == 0 && remote2.Reads == 0 && factory.Count("mic") == 1,
                "Resume started remote or collected PCM while the consented microphone was still being prepared.");
            clock.Set(Epoch + 26_000_000); release.Set(); var ack = Await(resume);
            Need(!ack.Paused && ack.MicrophoneEnabled && ack.EffectiveSessionUs == 100_000,
                "Resumed microphone preparation changed consent or enlarged recording time.");
            var mic2 = factory.Source("mic", 1); Need(remote2.Starts == 1 && mic2.Starts == 1, "Both prepared sources did not start.");
            PushBoth(remote2, mic2, Block(Epoch + 26_000_000, 0, 2400, 83), Block(Epoch + 26_000_000, 0, 2400, 89));
            clock.Set(Epoch + 26_500_000); Wait(() => remote2.Emitted == 1 && mic2.Emitted == 1, "Resumed dual-source packets missing.");
            var result = Await(session.StopAsync()); var disk = M1Recovery.Read(folder);
            Need(result.StorageComplete && result.DurationUs == 150_000 && disk.IsCleanEnd && disk.Capture.Duration100ns == 1_500_000,
                "Resumed dual-source recording did not use the compact clock.");
            Need(disk.Capture.Tracks.Where(t => t.SourceId == "mic").Sum(t => t.FrameCount) == 7200 &&
                disk.Capture.Tracks.Where(t => t.SourceId == "remote").Sum(t => t.FrameCount) == 7200,
                "Mic resume lost authorized active PCM or kept paused samples.");
            Evidence(folder, new { result.DurationUs, microphoneConsentRetained = true, bothSourcesWaitedForPreparation = true,
                removedPause100ns = 25_000_000, microphoneFrames = 7200 });
        }
        finally { release.Set(); Await(session.DisposeAsync().AsTask()); }
    }

    private static void CompressedTimeline(string folder)
    {
        var clock = new Clock(); var factory = new Factory(); var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        const int halfFrames = 33600; const long half100ns = 7_000_000;
        byte[] firstBytes = NoiseBytes(halfFrames, 67), secondBytes = NoiseBytes(halfFrames, 71);
        try
        {
            Await(session.StartAsync(Options(folder, false)));
            var first = factory.Source("remote", 0); first.Push(Block(Epoch, 0, halfFrames, 67));
            clock.Set(Epoch + half100ns); Wait(() => first.Emitted == 1, "Export initial PCM was not read.");
            Await(session.ControlAsync("pause", "pause")); clock.Set(Epoch + half100ns + 6_000_000);
            Await(session.ControlAsync("resume", "resume")); var second = factory.Source("remote", 1);
            second.Push(Block(Epoch + half100ns + 6_000_000, 0, halfFrames, 71));
            clock.Set(Epoch + 2 * half100ns + 6_000_000); Wait(() => second.Emitted == 1, "Export resumed PCM was not read.");
            var result = Await(session.StopAsync()); Need(result.StorageComplete && result.DurationUs == 1_400_000, "Export fixture duration incorrect.");
            var originals = Directory.EnumerateFiles(Path.Combine(folder, "sources"), "*.wav", SearchOption.AllDirectories)
                .ToDictionary(path => path, Hash);
            var mix = M2ExportService.RenderMeeting(folder, null);
            byte[] expected = firstBytes.Concat(secondBytes).ToArray(), actual = ReadPcmBytes(mix.WavePath);
            Need(actual.Length == expected.Length && SamplesWithinOne(actual, expected),
                "WAV lost first/tail frames, inserted pause silence, or misplaced the resumed join.");
            var encoded = new MediaFoundationMp3Encoder().Encode(mix.WavePath, Path.Combine(Path.GetDirectoryName(mix.WavePath)!, "pause-joined.mp3"));
            float[] decoded = Decode(encoded.Path), reference = PcmFloats(expected);
            Need(encoded.SourceFrames == halfFrames * 2 && decoded.LongLength == encoded.DecodedFrames &&
                encoded.DecodedFrames == halfFrames * 2 + encoded.EncoderDelayFrames + encoded.TrailingPaddingFrames,
                "Compressed EOF omitted active PCM or counted removed pause duration.");
            var probe = Mp3FrameProbe.Inspect(encoded.Path); Mp3FrameProbe.ValidateAgainstDecoded(probe, decoded.LongLength, 48000);
            var positions = new[] { 0, halfFrames - 2048, halfFrames, halfFrames * 2 - 4096 };
            double[] correlations = positions.Select(start => Correlation(reference, decoded, start, 4096, checked((int)encoded.EncoderDelayFrames))).ToArray();
            Need(correlations.All(value => value > .8), "MP3 lost or moved first/resume-join/tail PCM after pause removal.");
            Need(originals.All(pair => Hash(pair.Key) == pair.Value), "Mix or compression rewrote original source PCM.");
            Evidence(folder, new { result.DurationUs, sourceFrames = halfFrames * 2, encoded.DecodedFrames, encoded.EncoderDelayFrames,
                encoded.TrailingPaddingFrames, correlations, originalHashesUnchanged = true, removedPause100ns = 6_000_000 });
        }
        finally { Await(session.DisposeAsync().AsTask()); }
    }

    private static void ResumeRawPrefix(string folder, long early100ns, long jitter100ns)
    {
        var clock = new Clock(); var factory = new Factory(); var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        const int initialFrames = 48;
        ulong resumeAt = Epoch + 10_010_000;
        ulong rawStart = checked(resumeAt - (ulong)early100ns);
        ulong tailStart = checked((ulong)((Int128)rawStart + 200_000 + jitter100ns));
        ulong end = tailStart + 100_000;
        int prefix = checked((int)(((Int128)early100ns * 48000 + 9_999_999) / 10_000_000));
        var expectedRemote = NoiseBytes(initialFrames, 97).Concat(NoiseBytes(960, 103).Skip(prefix * 2)).Concat(NoiseBytes(480, 107)).ToArray();
        var expectedMic = NoiseBytes(initialFrames, 109).Concat(NoiseBytes(960, 127).Skip(prefix * 2)).Concat(NoiseBytes(480, 131)).ToArray();
        try
        {
            Await(session.StartAsync(Options(folder, true)));
            var oldRemote = factory.Source("remote", 0); var oldMic = factory.Source("mic", 0);
            PushBoth(oldRemote, oldMic, Block(Epoch, 0, initialFrames, 97), Block(Epoch, 0, initialFrames, 109));
            clock.Set(Epoch + 10_000); Wait(() => oldRemote.Emitted == 1 && oldMic.Emitted == 1, "Tiny active prefix was not read.");
            Need(Await(session.ControlAsync("pause", "pause")).EffectiveSessionUs == 1000, "Tiny pause boundary changed.");
            clock.Set(resumeAt); Need(Await(session.ControlAsync("resume", "resume")).EffectiveSessionUs == 1000, "Tiny active resume included pause.");
            var remote = factory.Source("remote", 1); var mic = factory.Source("mic", 1);
            PushBoth(remote, mic, Block(rawStart - 100_000, 0, 480, 137), Block(rawStart - 100_000, 0, 480, 139));
            PushBoth(remote, mic, Block(rawStart, 480, 960, 103), Block(rawStart, 480, 960, 127));
            PushBoth(remote, mic, Block(tailStart, 1440, 480, 107), Block(tailStart, 1440, 480, 131));
            clock.Set(end); Wait(() => remote.Emitted == 3 && mic.Emitted == 3, "Resumed raw-prefix packets were not consumed.");
            var result = Await(session.StopAsync());
            Need(result.StorageComplete, "Resumed prefix/jitter recording failed: " + string.Join(" | ", result.Warnings));
            Need(ReadSourceBytes(folder, "remote").SequenceEqual(expectedRemote) && ReadSourceBytes(folder, "mic").SequenceEqual(expectedMic),
                "Paused raw prefix survived, ceil frame was retained, or valid resumed/tail PCM was lost.");
            var disk = M1Recovery.Read(folder);
            Need(disk.IsCleanEnd && disk.Issues.Count == 0, "Resumed raw prefix cannot reconstruct without live state.");
            foreach (string source in new[] { "remote", "mic" })
            {
                var tracks = disk.Capture.Tracks.Where(t => t.SourceId == source).ToArray();
                Need(tracks.Length == 2, "Resumed source lost its distinct segment.");
                var packets = tracks[1].Packets;
                Need(packets.Count == 2 && packets[0].QpcPosition100ns == rawStart && packets[0].DevicePosition == 480 &&
                    packets[0].RawFrameCount == 960 && packets[0].RetainedFrameOffset == prefix && packets[0].FrameCount == 960 - prefix &&
                    packets[0].WaveFrameOffset == 0, "Retained prefix rewrote raw clocks or failed to persist its within-raw-packet offset.");
                Need(packets[1].QpcPosition100ns == tailStart && packets[1].DevicePosition == 1440 && packets[1].RawFrameCount == 480 &&
                    packets[1].RetainedFrameOffset == 0 && packets[1].FrameCount == 480 && packets[1].ClockJitter100ns == jitter100ns,
                    "Resumed packet filtering lost the tail or changed bounded device-clock jitter evidence.");
            }
            long active100ns = checked(10_000 + (long)(end - resumeAt));
            Need(disk.Capture.Duration100ns == active100ns && result.DurationUs == active100ns / 10,
                "Resumed prefix metadata enlarged or truncated the compact timeline.");
            var mix = M2ExportService.RenderMeeting(folder, null); byte[] mixedBytes = ReadPcmBytes(mix.WavePath);
            Need(mixedBytes.Length / 2 == (long)(((Int128)active100ns * 48000 + 9_999_999) / 10_000_000),
                "Mixed WAV frame count includes paused prefix or drops the final partial timeline frame.");
            if (early100ns == 100_000 && jitter100ns == 0)
                Need(SamplesWithinOne(mixedBytes, AddPcm(expectedRemote, expectedMic)), "Exact resumed join inserted a hole, overlap or changed the original/tail waveform.");
            float[] mixed = PcmFloats(mixedBytes);
            Need(mixed.TakeLast(24).Any(sample => Math.Abs(sample) > .001), "Mixed waveform lost the resumed tail.");
            Evidence(folder, new { early100ns, prefixFrames = prefix, retainedFramesPerSource = expectedRemote.Length / 2,
                jitter100ns, active100ns, result.DurationUs, rawQpcAndDevicePreserved = true, pausedPrefixPcmPersisted = false });
        }
        finally { Await(session.DisposeAsync().AsTask()); }
    }

    private static void MaximumActiveDuration(string folder)
    {
        var clock = new Clock(); var factory = new Factory(); var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        try
        {
            Await(session.StartAsync(Options(folder, false) with { MaximumSeconds = 1 }));
            var first = factory.Source("remote", 0); first.Push(Block(Epoch, 0, 4800, 149));
            clock.Set(Epoch + 1_000_000); Wait(() => first.Emitted == 1, "Duration-limit initial PCM was not read.");
            Await(session.ControlAsync("pause", "pause")); clock.Set(Epoch + 501_000_000);
            var frozen = Await(session.ControlAsync("still-paused", "bookmark"));
            Need(frozen.Paused && frozen.EffectiveSessionUs == 100_000 && !session.Completion.IsCompleted,
                "Paused wall time exhausted the maximum active recording duration.");
            Await(session.ControlAsync("resume", "resume")); var second = factory.Source("remote", 1);
            second.Push(Block(Epoch + 501_000_000, 0, 43200, 151)); clock.Set(Epoch + 510_000_000);
            var result = Await(session.Completion); var disk = M1Recovery.Read(folder);
            Need(result.StorageComplete && result.DurationUs == 1_000_000 && disk.Capture.Duration100ns == 10_000_000 &&
                disk.Capture.Tracks.Sum(t => t.FrameCount) == 48000, "Active duration limit failed to retain the exact resumed tail.");
            Evidence(folder, new { result.DurationUs, maximumSeconds = 1, pausedWallSeconds = 50, retainedFrames = 48000 });
        }
        finally { Await(session.DisposeAsync().AsTask()); }
    }

    private static RecordingOptions Options(string directory, bool mic) => new(new CaptureOptions(SourceMode.EndpointLoopback,
        null, "synthetic-render", true, mic, Mic, directory, "SYNTHETIC M3Hfix paused recording"), MaximumSeconds: 60, ExpectedMinutes: 1);
    private static void PushBoth(Source remote, Source mic, RecordingBlock remoteBlock, RecordingBlock micBlock)
    { remote.Push(remoteBlock); mic.Push(micBlock); }
    private static RecordingBlock Block(ulong qpc, ulong frame, int frames, int seed) =>
        new(NoiseBytes(frames, seed), frames, frames, frame, qpc, 0, true, true, 0);
    private static byte[] NoiseBytes(int frames, int seed)
    {
        var bytes = new byte[frames * 2]; var random = new Random(seed); double value = 0;
        for (int i = 0; i < frames; i++)
        { value = value * .6 + (random.NextDouble() - .5) * .4; BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)(value * 15000)); }
        return bytes;
    }
    private static byte[] ReadSourceBytes(string folder, string source) => Directory.EnumerateFiles(Path.Combine(folder, "sources", source), "*.wav")
        .Order(StringComparer.Ordinal).SelectMany(ReadPcmBytes).ToArray();
    private static byte[] ReadPcmBytes(string path)
    { using var reader = new M2WaveReader(path); using var bytes = new MemoryStream(); reader.CopyTo(bytes); return bytes.ToArray(); }
    private static bool SamplesWithinOne(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i += 2)
            if (Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(left.AsSpan(i)) - BinaryPrimitives.ReadInt16LittleEndian(right.AsSpan(i))) > 1) return false;
        return true;
    }
    private static float[] PcmFloats(byte[] pcm) => Enumerable.Range(0, pcm.Length / 2)
        .Select(i => BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)) / 32768f).ToArray();
    private static byte[] AddPcm(byte[] left, byte[] right)
    {
        Need(left.Length == right.Length, "Synthetic two-track lengths differ."); var sum = new byte[left.Length];
        for (int i = 0; i < left.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(sum.AsSpan(i), checked((short)
            (BinaryPrimitives.ReadInt16LittleEndian(left.AsSpan(i)) + BinaryPrimitives.ReadInt16LittleEndian(right.AsSpan(i)))));
        return sum;
    }
    private static float[] Decode(string path)
    {
        using var reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
        using var memory = new MemoryStream(); byte[] buffer = new byte[65536]; int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0) memory.Write(buffer, 0, count);
        return MemoryMarshal.Cast<byte, float>(memory.ToArray()).ToArray();
    }
    private static double Correlation(float[] expected, float[] actual, int start, int count, int delay)
    {
        double cross = 0, left = 0, right = 0;
        for (int i = start; i < start + count; i++)
        { cross += expected[i] * actual[i + delay]; left += expected[i] * expected[i]; right += actual[i + delay] * actual[i + delay]; }
        return cross / Math.Sqrt(left * right);
    }
    private static JsonElement[] Journal(string folder) => JsonLines(Path.Combine(folder, "journal", "capture-index.jsonl"));
    private static JsonElement[] Events(string folder) => JsonLines(Path.Combine(folder, "journal", "events.jsonl"));
    private static JsonElement[] JsonLines(string path) => File.ReadLines(path)
        .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.GetProperty("data").Clone(); }).ToArray();
    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()!;
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Evidence(string folder, object data) => File.WriteAllText(Path.Combine(folder, "pause-test-evidence.json"),
        JsonSerializer.Serialize(new { isSynthetic = true, actualAudioDevicesOpened = false, capturePlayback = false, data }, new JsonSerializerOptions { WriteIndented = true }));
    private static void Await(Task task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();
    private static T Await<T>(Task<T> task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Wait(Func<bool> condition, string message)
    { var timer = Stopwatch.StartNew(); while (!condition()) { if (timer.Elapsed >= Timeout) throw new InvalidDataException(message); Thread.Sleep(1); } }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidDataException("Expected " + typeof(T).Name); }

    private sealed class Clock
    {
        private long value = (long)Epoch;
        internal ulong Read() => checked((ulong)Interlocked.Read(ref value));
        internal void Set(ulong next) => Interlocked.Exchange(ref value, checked((long)next));
    }
    private sealed class Factory : IRecordingSourceFactory
    {
        private readonly ConcurrentQueue<Source> opened = new(); private int attempts, micPreparations;
        internal Action<int>? OpeningRemote;
        internal Action<int>? PreparingMicrophone;
        internal int Count(string id) => opened.Count(source => source.Id == id);
        internal Source Source(string id, int index) => opened.Where(source => source.Id == id).ElementAt(index);
        public void Preflight(RecordingOptions options) { }
        public void ValidateRemote(CaptureOptions options) { }
        public IRecordingAudioSource OpenRemote(CaptureOptions options)
        { OpeningRemote?.Invoke(Interlocked.Increment(ref attempts)); var source = new Source("remote"); opened.Enqueue(source); return source; }
        public IRecordingAudioSource OpenMicrophone(string deviceId)
        { Need(deviceId == Mic, "Microphone selection changed."); var source = new Source("mic"); opened.Enqueue(source); return source; }
        public IPreparedRecordingSource PrepareMicrophone(string deviceId)
        { PreparingMicrophone?.Invoke(Interlocked.Increment(ref micPreparations)); return new DeferredRecordingSource(() => OpenMicrophone(deviceId)); }
    }
    private sealed class Source(string id) : IRecordingAudioSource
    {
        private readonly ConcurrentQueue<RecordingBlock> queue = new();
        private readonly int owner = Environment.CurrentManagedThreadId;
        internal int Starts, Stops, Disposes, Reads, Emitted;
        internal bool BlockStop;
        internal Action? Disposing;
        internal readonly ManualResetEventSlim StopEntered = new(), AllowStop = new();
        public string Id => id;
        public WaveFormat WaveFormat { get; } = new(48000, 16, 1);
        public PcmFormat Format { get; } = new(48000, 1, 16, false, 2);
        internal void Push(RecordingBlock block) => queue.Enqueue(block);
        private void SameThread() => Need(Environment.CurrentManagedThreadId == owner, "Source left the owning capture thread.");
        public void Start() { SameThread(); Interlocked.Increment(ref Starts); }
        public void Stop()
        { SameThread(); Interlocked.Increment(ref Stops); StopEntered.Set(); if (BlockStop) Need(AllowStop.Wait(Timeout), "Synthetic Stop gate timed out."); }
        public RecordingBlock? Read(ulong epochQpc100ns, ulong? cutoffQpc100ns)
        {
            SameThread(); Need(Starts == 1 && Disposes == 0, "PCM read occurred before Start or after Dispose."); Interlocked.Increment(ref Reads);
            if (!queue.TryDequeue(out var block)) return null;
            Interlocked.Increment(ref Emitted); return block;
        }
        public void Dispose() { SameThread(); Disposing?.Invoke(); Interlocked.Increment(ref Disposes); }
    }
}
