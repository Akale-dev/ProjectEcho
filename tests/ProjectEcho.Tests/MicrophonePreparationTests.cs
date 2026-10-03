using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

/// <summary>Blocked synthetic negotiation; never activates a real device or captures audio.</summary>
internal static class MicrophonePreparationTests
{
    private const ulong Epoch = 80_000_000;
    private const string Mic = "synthetic-selected-mic";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    internal static void Register(Action<string, Action> run, string output)
    {
        run("T08/T51/T54-SYNTH initial microphone negotiation finishes before either source starts", () => Initial(Path.Combine(output, "mic-prepare-initial")));
        run("T08/T51/T54-SYNTH slow mid-session microphone negotiation preserves remote PCM and capture-thread service ownership", () => MidSession(Path.Combine(output, "mic-prepare-mid")));
        foreach (string operation in new[] { "mic_off", "pause", "stop" })
        {
            string cancel = operation;
            run($"T08/T51/T52-SYNTH {operation} cancels pending microphone without waiting or starting late PCM",
                () => Cancel(Path.Combine(output, "mic-prepare-cancel-" + cancel), cancel));
        }
        run("T08/T51-SYNTH Stop during initial microphone negotiation never starts either source", () => InitialStop(Path.Combine(output, "mic-prepare-initial-stop")));
        run("T08/T51/T54-SYNTH automatic scheduling-gap pause cancels pending microphone before late preparation returns", () => SystemGap(Path.Combine(output, "mic-prepare-system-gap")));
        run("T08/T54-SYNTH microphone preparation failure leaves remote active and explicit same-device retry works", () => Failure(Path.Combine(output, "mic-prepare-failure")));
    }

    private static void Initial(string folder)
    {
        using var factory = new Factory(); var clock = new Clock();
        var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        try
        {
            var start = session.StartAsync(Options(folder, true));
            Need(factory.Entered.Wait(Timeout), "Initial preparation did not run.");
            Need(factory.Remote is { Starts: 0, Reads: 0 } && factory.Microphone is null && !start.IsCompleted,
                "A source started or collected PCM while microphone negotiation was blocked.");
            factory.Release.Set(); Await(start);
            Need(factory.Remote.Starts == 1 && factory.Microphone?.Starts == 1, "Prepared sources did not start.");
            var microphone = factory.Microphone!;
            Need(factory.PreparationThread != factory.Remote.OwnerThread && factory.Remote.OwnerThread == microphone.OwnerThread,
                "Preparation/service ownership was not split between MTA preparation and original capture threads.");
            Await(session.StopAsync());
            Need(factory.Remote.Disposes == 1 && microphone.Disposes == 1, "Prepared sources were not released once.");
        }
        finally { factory.Release.Set(); Await(session.DisposeAsync().AsTask()); }
    }

    private static void MidSession(string folder)
    {
        using var factory = new Factory(); var clock = new Clock();
        var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        try
        {
            Await(session.StartAsync(Options(folder, false)));
            Need(factory.PrepareCalls == 0, "MicOff initialized a microphone.");
            var on = session.ControlAsync("on", "mic_on", Mic);
            Need(factory.Entered.Wait(Timeout), "Mid-session preparation did not run.");
            factory.Remote.Push(Block(0)); clock.Set(Epoch + 100000);
            Wait(() => factory.Remote.Emitted == 1, "Slow mic activation starved remote packet draining.");
            Need(!on.IsCompleted && factory.Microphone is null, "Mic-on was acknowledged before actual capture startup.");
            factory.Release.Set(); Need(Await(on).MicrophoneEnabled, "Prepared microphone was not acknowledged.");
            var microphone = factory.Microphone!;
            Need(microphone.OwnerThread == factory.Remote.OwnerThread && microphone.OwnerThread != factory.PreparationThread,
                "Microphone capture service moved away from the capture thread.");
            microphone.Push(Block(0) with { QpcPosition100ns = Epoch + 100000 }); clock.Set(Epoch + 200000);
            Wait(() => microphone.Emitted == 1, "Mic packet was not consumed after authorized Start.");
            var off = Await(session.ControlAsync("off", "mic_off"));
            Need(!off.MicrophoneEnabled && microphone.Stops == 1 && microphone.Disposes == 1, "MicOff acknowledged before actual source boundary.");
            int reads = microphone.Reads; Thread.Sleep(20); Need(microphone.Reads == reads, "MicOff continued collecting PCM.");
            Need(Await(session.StopAsync()).StorageComplete, "Valid synthetic prepared recording did not save.");
            var capture = M1Recovery.Read(folder);
            Need(capture.IsCleanEnd && capture.Capture.Tracks.Where(t => t.SourceId == "remote").Sum(t => t.FrameCount) == 480 &&
                capture.Capture.Tracks.Where(t => t.SourceId == "mic").Sum(t => t.FrameCount) == 480,
                "Slow negotiation lost existing-source or authorized mic PCM.");
        }
        finally { factory.Release.Set(); Await(session.DisposeAsync().AsTask()); }
    }

    private static void Cancel(string folder, string operation)
    {
        using var factory = new Factory(); var clock = new Clock();
        var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        try
        {
            Await(session.StartAsync(Options(folder, false)));
            var on = session.ControlAsync("on", "mic_on", Mic);
            Need(factory.Entered.Wait(Timeout), "Cancellable preparation did not run.");
            factory.Remote.Push(Block(0)); clock.Set(Epoch + 100000);
            Wait(() => factory.Remote.Emitted == 1, "Cancelled prepare starved remote before its boundary.");
            var timer = Stopwatch.StartNew();
            if (operation == "stop") Await(session.StopAsync());
            else
            {
                var ack = Await(session.ControlAsync("cancel", operation));
                Need(!ack.MicrophoneEnabled && ack.Paused == (operation == "pause"), "Cancellation acknowledgement has the wrong actual state.");
            }
            Need(timer.Elapsed < TimeSpan.FromSeconds(1) && !factory.Returned.IsSet, "Control waited for blocked microphone negotiation.");
            Expect<OperationCanceledException>(() => Await(on));
            if (operation == "mic_off")
            {
                Expect<InvalidOperationException>(() => Await(session.ControlAsync("no-extra-worker", "mic_on", Mic)));
                Need(factory.PrepareCalls == 1, "Cancel spawned a second blocked preparation worker.");
            }
            factory.Release.Set(); Need(factory.Returned.Wait(Timeout), "Cancelled native preparation did not return.");
            Wait(() => factory.AbandonedDisposals == 1, "Late unstarted microphone client was not disposed by its preparation MTA.");
            Need(factory.Microphone is null && factory.AbandonThread == factory.PreparationThread,
                "Cancelled preparation acquired/started a capture service or released its client on another thread.");
            Await(session.StopAsync());
            var read = M1Recovery.Read(folder);
            Need(read.Capture.Tracks.All(t => t.SourceId != "mic") && read.Capture.Tracks.Sum(t => t.FrameCount) == 480,
                "Cancelled microphone persisted PCM or lost pre-stop remote PCM.");
        }
        finally { factory.Release.Set(); Await(session.DisposeAsync().AsTask()); }
    }

    private static void InitialStop(string folder)
    {
        using var factory = new Factory(); var clock = new Clock();
        var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        try
        {
            var start = session.StartAsync(Options(folder, true));
            Need(factory.Entered.Wait(Timeout), "Initial Stop fixture did not block.");
            var timer = Stopwatch.StartNew(); Await(session.StopAsync());
            Need(timer.Elapsed < TimeSpan.FromSeconds(1) && !factory.Returned.IsSet && factory.Remote.Starts == 0,
                "Initial Stop waited for native initialization or started remote.");
            Expect<OperationCanceledException>(() => Await(start));
            factory.Release.Set(); Wait(() => factory.AbandonedDisposals == 1, "Initial cancelled mic was not released.");
            Need(factory.Microphone is null && factory.Remote.Disposes == 1, "Initial cancellation leaked or activated a source.");
        }
        finally { factory.Release.Set(); Await(session.DisposeAsync().AsTask()); }
    }

    private static void SystemGap(string folder)
    {
        using var factory = new Factory(); var clock = new Clock();
        var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        var acknowledgements = new ConcurrentQueue<RecordingAck>();
        try
        {
            session.Acknowledged += acknowledgements.Enqueue;
            Await(session.StartAsync(Options(folder, false)));
            var on = session.ControlAsync("on", "mic_on", Mic);
            Need(factory.Entered.Wait(Timeout), "System-gap fixture did not block preparation.");
            clock.Set(Epoch + 30_000_000);
            Wait(() => acknowledgements.Any(ack => ack.RequestId == "system-gap" && ack.Paused), "Scheduling gap did not pause capture.");
            Expect<OperationCanceledException>(() => Await(on));
            factory.Release.Set(); Wait(() => factory.AbandonedDisposals == 1, "Auto-paused mic preparation was not disposed.");
            Need(factory.Microphone is null && factory.Remote.Stops == 1, "Microphone acquired or started a service while automatically paused.");
            Await(session.StopAsync());
            Need(M1Recovery.Read(folder).Capture.Tracks.All(t => t.SourceId != "mic"), "Automatic pause persisted pending mic PCM.");
        }
        finally { factory.Release.Set(); Await(session.DisposeAsync().AsTask()); }
    }

    private static void Failure(string folder)
    {
        using var factory = new Factory { FailFirst = true }; factory.Release.Set(); var clock = new Clock();
        var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
        try
        {
            Await(session.StartAsync(Options(folder, false)));
            Expect<IOException>(() => Await(session.ControlAsync("failure", "mic_on", Mic)));
            Need(factory.Remote.Stops == 0 && factory.Microphone is null, "Failed preparation disturbed remote or started mic.");
            Need(Await(session.ControlAsync("retry", "mic_on", Mic)).MicrophoneEnabled && factory.PrepareCalls == 2,
                "Explicit same-device retry after a finished failure did not succeed.");
            Await(session.StopAsync());
        }
        finally { factory.Release.Set(); Await(session.DisposeAsync().AsTask()); }
    }

    private static RecordingOptions Options(string directory, bool mic) => new(
        new(SourceMode.EndpointLoopback, null, "synthetic-render", true, mic, Mic, directory, "synthetic microphone preparation", 10),
        MaximumSeconds: 60, ExpectedMinutes: 1);
    private static RecordingBlock Block(long frame) => new(new byte[960], 480, 480, (ulong)frame,
        Epoch + (ulong)frame * 10_000_000 / 48000, 2, true, true, 0);
    private sealed class Clock
    {
        private long current = (long)Epoch;
        internal ulong Read() => (ulong)Interlocked.Read(ref current);
        internal void Set(ulong value) => Interlocked.Exchange(ref current, (long)value);
    }
    private sealed class Factory : IRecordingSourceFactory, IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new(), Release = new(), Returned = new();
        internal Source Remote = null!;
        internal Source? Microphone;
        internal int PrepareCalls, PreparationThread, AbandonedDisposals, AbandonThread;
        internal bool FailFirst;
        public void Preflight(RecordingOptions options) { }
        public void ValidateRemote(CaptureOptions options) { }
        public IRecordingAudioSource OpenRemote(CaptureOptions options) => Remote = new("remote");
        public IRecordingAudioSource OpenMicrophone(string deviceId) => throw new InvalidOperationException("The blocking one-stage path must not run.");
        public IPreparedRecordingSource PrepareMicrophone(string deviceId)
        {
            Need(deviceId == Mic, "Microphone selection changed.");
            int call = Interlocked.Increment(ref PrepareCalls); PreparationThread = Environment.CurrentManagedThreadId;
            Need(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA, "Preparation thread is not MTA.");
            Entered.Set(); Need(Release.Wait(Timeout), "Synthetic preparation gate timed out.");
            Returned.Set();
            if (FailFirst && call == 1) throw new IOException("Synthetic activation failure.");
            return new Prepared(this);
        }
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); Returned.Dispose(); }
        private sealed class Prepared(Factory factory) : IPreparedRecordingSource
        {
            private bool transferred;
            public IRecordingAudioSource CompleteOnCaptureThread()
            {
                Need(!transferred && Environment.CurrentManagedThreadId == factory.Remote.OwnerThread,
                    "Capture service was acquired off the owning capture thread.");
                transferred = true; return factory.Microphone = new("mic");
            }
            public void Dispose()
            {
                if (transferred) return;
                factory.AbandonThread = Environment.CurrentManagedThreadId;
                Interlocked.Increment(ref factory.AbandonedDisposals); transferred = true;
            }
        }
    }
    private sealed class Source(string id) : IRecordingAudioSource
    {
        private readonly ConcurrentQueue<RecordingBlock> packets = new();
        internal readonly int OwnerThread = Environment.CurrentManagedThreadId;
        internal int Starts, Stops, Reads, Disposes, Emitted;
        public string Id => id;
        public WaveFormat WaveFormat { get; } = new(48000, 16, 1);
        public PcmFormat Format { get; } = new(48000, 1, 16, false, 2);
        internal void Push(RecordingBlock packet) => packets.Enqueue(packet);
        private void SameThread() => Need(Environment.CurrentManagedThreadId == OwnerThread, "Capture service access/release changed threads.");
        public void Start() { SameThread(); Interlocked.Increment(ref Starts); }
        public void Stop() { SameThread(); Interlocked.Increment(ref Stops); }
        public RecordingBlock? Read(ulong epochQpc100ns, ulong? cutoffQpc100ns)
        {
            SameThread(); Need(Starts == 1 && Disposes == 0, "Read before Start or after disposal."); Interlocked.Increment(ref Reads);
            if (!packets.TryDequeue(out var packet)) return null;
            Interlocked.Increment(ref Emitted); return packet;
        }
        public void Dispose() { SameThread(); Interlocked.Increment(ref Disposes); }
    }
    private static void Await(Task task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();
    private static T Await<T>(Task<T> task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Wait(Func<bool> condition, string message)
    { var timer = Stopwatch.StartNew(); while (!condition()) { if (timer.Elapsed > Timeout) throw new InvalidDataException(message); Thread.Sleep(1); } }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidDataException("Expected " + typeof(T).Name + " was not raised."); }
}
