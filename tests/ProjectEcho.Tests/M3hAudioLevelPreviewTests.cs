using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

/// <summary>Only synthetic meter metadata. No device enumeration, PCM, or microphone activation.</summary>
internal static class M3hAudioLevelPreviewTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);
    internal static void Register(Action<string, Action> run)
    {
        run("T01/T08-M3H preview distinguishes no selection, no observed signal and unavailable meter", Readings);
        run("T01/T05-M3H preview process scope rejects unrelated, reused and reparented processes", Scope);
        run("T01/T05-M3H preview process scope rejects parent PID reuse, cycles and unreadable identity", InvalidAncestry);
        run("T08/T54-M3H preview owns reader and release on one MTA and stops synchronous callbacks", WorkerOwnership);
        run("T08/T54-M3H stopping preview discards an in-flight sample before delivery", StopInFlight);
        run("T08/T54-M3H meter and UI notification failure are isolated with no unhandled worker exception", FailureIsolation);
        run("T08-M3H slow explicitly enabled mic preview cancels before acquiring capture service or starting", CancelSlowMicrophone);
        run("T08-M3H mic preview acknowledges actual Start, clears transient PCM, and awaits real Stop release", MicrophoneBoundary);
        run("T08-M3H bad microphone preview samples are wiped and fail closed before reporting inactive", MicrophoneFailure);
        run("T01/T05-M3H shared-process and unknown-success session meters cannot claim selected tree scope", SessionProcessScope);
        run("T08-M3H cancellation while outer reader factory is blocked prevents late microphone preparation", CancelHeldReaderFactory);
        run("T08-M3H cancellation while microphone capture service completion is blocked prevents late Start", CancelHeldCompletion);
        run("T08-M3H native Start reserved before cancellation does not block UI cancellation or read PCM afterward", CancelHeldStart);
    }

    private static void Readings()
    {
        Need(AudioLevelPreviewReading.NotSelected.State == AudioLevelPreviewState.NotSelected, "Unselected source gained a signal status.");
        Need(AudioLevelPreviewReading.FromPeak(0).State == AudioLevelPreviewState.NoSignalObserved,
            "Zero was advertised as device success or proven silence.");
        Need(AudioLevelPreviewReading.FromPeak(.25f) is { Peak: .25f, State: AudioLevelPreviewState.SignalObserved }, "Real scalar peak changed.");
        foreach (float peak in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1, 1.01f })
            Need(AudioLevelPreviewReading.FromPeak(peak) is { Peak: 0, State: AudioLevelPreviewState.Unavailable }, "Invalid peak was accepted or fabricated.");
    }

    private static void Scope()
    {
        var root = Id(400, 100); var audio = Id(401, 110); var sibling = Id(402, 120); var outside = Id(900, 90);
        var identities = new Dictionary<int, ProcessIdentity> { [400] = root, [401] = audio, [402] = sibling, [900] = outside };
        var parents = new Dictionary<int, int> { [401] = 400, [402] = 400, [400] = 900 };
        var source = new ProcessSource(root, audio, "selected-device", "synthetic", "active", "synthetic");
        PreviewProcessTree Tree() => new(parents, p => identities.GetValueOrDefault(p));
        Need(Tree().SelectionMatches(source) && Tree().Contains(root, root) && Tree().Contains(root, sibling), "Root or real sibling was excluded.");
        Need(!Tree().Contains(root, outside), "An ancestor/unrelated application's meter escaped the selected tree.");
        identities[401] = audio with { StartTimeUtcTicks = 130 };
        Need(!Tree().SelectionMatches(source), "A reused selected audio PID passed.");
        identities[401] = audio;
        parents[401] = 900;
        Need(!Tree().SelectionMatches(source), "Reparented selected audio process passed.");
        parents[401] = 400;
        identities[400] = root with { ExecutablePath = "C:\\synthetic\\replacement.exe" };
        Need(!Tree().SelectionMatches(source), "Changed root executable identity passed.");
        identities[400] = root with { ExecutablePath = root.ExecutablePath.ToUpperInvariant() };
        Need(Tree().SelectionMatches(source), "Windows path case changed identity.");
    }

    private static void InvalidAncestry()
    {
        var root = Id(400, 100); var audio = Id(401, 110); var middle = Id(402, 120);
        var identities = new Dictionary<int, ProcessIdentity> { [400] = root, [401] = audio, [402] = middle };
        var parents = new Dictionary<int, int> { [401] = 402, [402] = 400 };
        PreviewProcessTree Tree() => new(parents, p => identities.GetValueOrDefault(p));
        Need(!Tree().Contains(root, audio), "A parent created after its child was trusted.");
        identities[402] = middle with { StartTimeUtcTicks = 105 };
        Need(Tree().Contains(root, audio), "A valid multilevel child was excluded.");
        parents[402] = 401;
        Need(!Tree().Contains(root, audio), "Parent cycle reached the root.");
        parents[402] = 400; identities.Remove(402);
        Need(!Tree().Contains(root, audio), "Unreadable parent identity was accepted.");
    }

    private static void WorkerOwnership()
    {
        var observed = new ConcurrentQueue<AudioLevelPreviewSnapshot>();
        using var delivered = new ManualResetEventSlim();
        SyntheticReader? reader = null;
        using var preview = new AudioLevelPreview(() => reader = new(), snapshot => { observed.Enqueue(snapshot); delivered.Set(); }, TimeSpan.FromMilliseconds(10));
        Need(delivered.Wait(Timeout), "No synthetic preview arrived.");
        preview.Dispose();
        int count = observed.Count;
        Thread.Sleep(30);
        Need(reader is { Reads: > 0, Disposals: 1 } && reader.OwnerApartment == ApartmentState.MTA &&
            reader.OwnerThread != Environment.CurrentManagedThreadId && observed.Count == count,
            "Preview did not have one MTA owner or callbacks continued after disposal.");
        preview.Dispose();
        Need(reader!.Disposals == 1, "Repeated Dispose released native owner twice.");
    }

    private static void StopInFlight()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        SyntheticReader? reader = null;
        int calls = 0;
        using var preview = new AudioLevelPreview(() => reader = new(entered, release), _ => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(10));
        Need(entered.Wait(Timeout), "Synthetic read did not enter.");
        using var stopping = new ManualResetEventSlim();
        var stop = Task.Run(() => { stopping.Set(); preview.Dispose(); });
        Need(stopping.Wait(Timeout), "Stop worker did not start.");
        // Dispose must wait for this native-equivalent in-flight read to return.
        Need(!stop.Wait(TimeSpan.FromMilliseconds(30)), "Dispose returned before the reader was released.");
        release.Set(); stop.WaitAsync(Timeout).GetAwaiter().GetResult();
        Need(calls == 0 && reader is { Reads: 1, Disposals: 1 }, "An in-flight stale sample was delivered after stop.");
    }

    private static void FailureIsolation()
    {
        using var failed = new ManualResetEventSlim(); AudioLevelPreviewSnapshot? result = null;
        using (var preview = new AudioLevelPreview(() => throw new IOException("synthetic private text"),
                   sample => { result = sample; failed.Set(); }, TimeSpan.FromMilliseconds(10)))
        {
            Need(failed.Wait(Timeout), "Failed reader initialization was not reported."); preview.Dispose();
            Need(result?.Remote.State == AudioLevelPreviewState.Unavailable &&
                result.Remote.Diagnostic?.Contains("synthetic private text", StringComparison.Ordinal) != true,
                "Worker failure was reported as zero signal or leaked arbitrary exception text.");
        }
        int callbacks = 0; using var second = new ManualResetEventSlim();
        using var continuing = new AudioLevelPreview(() => new SyntheticReader(), _ =>
        {
            if (Interlocked.Increment(ref callbacks) == 1) throw new InvalidOperationException("synthetic UI failure");
            second.Set();
        }, TimeSpan.FromMilliseconds(10));
        Need(second.Wait(Timeout), "A UI callback exception escaped the worker."); continuing.Dispose();
    }

    private static void CancelSlowMicrophone()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        PreparedMicrophone? prepared = null;
        var preview = new MicrophoneLevelPreview(() =>
        {
            entered.Set(); Need(release.Wait(Timeout), "Synthetic microphone preparation timed out.");
            return prepared = new();
        });
        try
        {
            Need(entered.Wait(Timeout) && preview.Current is { Started: false, Reading.State: AudioLevelPreviewState.Preparing },
                "Pending native preparation was reported as an active mic.");
            var stopped = preview.DisposeAsync().AsTask();
            Need(!stopped.IsCompleted, "MicOff claimed completion before its slow native handle was released.");
            release.Set(); stopped.WaitAsync(Timeout).GetAwaiter().GetResult();
            Need(prepared is { Completions: 0, Disposals: 1, Source: null } && !preview.Current.Started,
                "Cancelled preparation started PCM or transferred its capture service.");
        }
        finally { release.Set(); preview.DisposeAsync().AsTask().WaitAsync(Timeout).GetAwaiter().GetResult(); }
    }

    private static void MicrophoneBoundary()
    {
        PreparedMicrophone? prepared = null;
        using var stopEntered = new ManualResetEventSlim(); using var releaseStop = new ManualResetEventSlim();
        var preview = new MicrophoneLevelPreview(() => prepared = new(stopEntered, releaseStop));
        try
        {
            Wait(() => preview.Current is { Started: true, Reading.State: AudioLevelPreviewState.SignalObserved }, "Mic preview never acknowledged synthetic Start/data.");
            Need(preview.Current.Reading.Peak == .5f && prepared?.Source is { Starts: 1 }, "Mic preview peak was fabricated or acknowledged before Start.");
            Wait(() => prepared!.Source!.Packet.All(b => b == 0), "Temporary microphone PCM was retained after metering.");
            var stop = preview.DisposeAsync().AsTask();
            Need(stopEntered.Wait(Timeout) && !stop.IsCompleted && preview.Current.Started,
                "UI was allowed to claim mic-off before source Stop/release.");
            releaseStop.Set(); stop.WaitAsync(Timeout).GetAwaiter().GetResult();
            var source = prepared!.Source!; int reads = source.Reads;
            Thread.Sleep(20);
            Need(source is { Starts: 1, Stops: 1, Disposals: 1 } && source.Reads == reads && !preview.Current.Started,
                "MicOff left a source active or continued collecting PCM.");
        }
        finally { releaseStop.Set(); preview.DisposeAsync().AsTask().WaitAsync(Timeout).GetAwaiter().GetResult(); }
    }

    private static void MicrophoneFailure()
    {
        PreparedMicrophone? prepared = null;
        var preview = new MicrophoneLevelPreview(() => prepared = new(invalidSample: true));
        try
        {
            Wait(() => preview.Current.Reading.State == AudioLevelPreviewState.Unavailable, "Invalid mic sample was not reported.");
            preview.DisposeAsync().AsTask().WaitAsync(Timeout).GetAwaiter().GetResult();
            Need(!preview.Current.Started && prepared?.Source is { Starts: 1, Stops: 1, Disposals: 1 } &&
                prepared.Source.Packet.All(b => b == 0), "Failed mic preview retained PCM or reported off before release.");
        }
        finally { preview.DisposeAsync().AsTask().WaitAsync(Timeout).GetAwaiter().GetResult(); }
    }

    private static void SessionProcessScope()
    {
        Need(ProcessSessionMeters.HasSingleProcess(0), "S_OK single-process session was rejected.");
        foreach (int status in new[] { 0x0889000D, 1, 0x0889000E, unchecked((int)0x80004005) })
            Need(!ProcessSessionMeters.HasSingleProcess(status), "Ambiguous or failed process scope was treated as a single owner.");
    }

    private static void CancelHeldReaderFactory()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int prepareCalls = 0, callbacks = 0;
        using var preview = new AudioLevelPreview(token =>
        {
            entered.Set(); Need(release.Wait(Timeout), "Synthetic reader factory timed out.");
            return new TokenAwareReader(token, () => { Interlocked.Increment(ref prepareCalls); return new PreparedMicrophone(); });
        }, _ => Interlocked.Increment(ref callbacks), TimeSpan.FromMilliseconds(10));
        try
        {
            Need(entered.Wait(Timeout), "Reader factory did not block.");
            var stop = preview.DisposeAsync().AsTask();
            Need(!stop.IsCompleted, "Reader factory ownership ended before native-equivalent return.");
            release.Set(); stop.WaitAsync(Timeout).GetAwaiter().GetResult();
            Need(prepareCalls == 0 && callbacks == 0, "Late reader factory prepared/started a microphone after cancellation.");
        }
        finally { release.Set(); preview.DisposeAsync().AsTask().WaitAsync(Timeout).GetAwaiter().GetResult(); }
    }

    private static void CancelHeldCompletion()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        PreparedMicrophone? prepared = null;
        var preview = new MicrophoneLevelPreview(() => prepared = new(completeEntered: entered, releaseComplete: release));
        try
        {
            Need(entered.Wait(Timeout), "Capture service completion did not block.");
            var stop = preview.DisposeAsync().AsTask();
            Need(!stop.IsCompleted && !preview.Current.Started, "MicOff completed before pending service ownership returned.");
            release.Set(); stop.WaitAsync(Timeout).GetAwaiter().GetResult();
            Need(prepared?.Source is { Starts: 0, Stops: 0, Disposals: 1, Reads: 0 }, "Cancelled service completion started or read late mic PCM.");
        }
        finally { release.Set(); preview.DisposeAsync().AsTask().WaitAsync(Timeout).GetAwaiter().GetResult(); }
    }

    private static void CancelHeldStart()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        PreparedMicrophone? prepared = null;
        var preview = new MicrophoneLevelPreview(() => prepared = new(startEntered: entered, releaseStart: release));
        try
        {
            Need(entered.Wait(Timeout), "Synthetic native Start did not block.");
            var timer = Stopwatch.StartNew();
            var stop = preview.DisposeAsync().AsTask();
            Need(timer.Elapsed < TimeSpan.FromMilliseconds(500) && !stop.IsCompleted && !preview.Current.Started,
                "UI cancellation blocked on native Start or acknowledged started before Start returned.");
            release.Set(); stop.WaitAsync(Timeout).GetAwaiter().GetResult();
            Need(prepared?.Source is { Starts: 1, Stops: 1, Disposals: 1, Reads: 0 } && !preview.Current.Started,
                "Previously authorized slow Start collected PCM after cancellation or skipped actual Stop.");
        }
        finally { release.Set(); preview.DisposeAsync().AsTask().WaitAsync(Timeout).GetAwaiter().GetResult(); }
    }

    private sealed class TokenAwareReader(CancellationToken token, Func<IPreparedRecordingSource> prepare) : IAudioLevelPreviewReader
    {
        private readonly MicrophoneLevelPreview microphone = new(prepare, token);
        public AudioLevelPreviewSnapshot Read() => new(AudioLevelPreviewReading.NotSelected,
            microphone.Current.Reading, 42, microphone.Current.Started);
        public void RequestStop() => microphone.RequestStop();
        public void Dispose() => microphone.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class PreparedMicrophone(ManualResetEventSlim? stopEntered = null,
        ManualResetEventSlim? releaseStop = null, bool invalidSample = false,
        ManualResetEventSlim? completeEntered = null, ManualResetEventSlim? releaseComplete = null,
        ManualResetEventSlim? startEntered = null, ManualResetEventSlim? releaseStart = null) : IPreparedRecordingSource
    {
        private readonly int owner = Environment.CurrentManagedThreadId;
        internal MicrophoneSource? Source;
        internal int Completions, Disposals;
        public IRecordingAudioSource CompleteOnCaptureThread()
        {
            Need(Environment.CurrentManagedThreadId == owner && Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA,
                "Mic capture service was transferred to a different thread.");
            Completions++;
            completeEntered?.Set();
            if (releaseComplete is not null) Need(releaseComplete.Wait(Timeout), "Synthetic capture service completion timed out.");
            return Source = new(stopEntered, releaseStop, invalidSample, startEntered, releaseStart);
        }
        public void Dispose() { Need(Environment.CurrentManagedThreadId == owner, "Prepared mic release changed owner."); Disposals++; }
    }

    private sealed class MicrophoneSource(ManualResetEventSlim? stopEntered, ManualResetEventSlim? releaseStop,
        bool invalidSample, ManualResetEventSlim? startEntered, ManualResetEventSlim? releaseStart) : IRecordingAudioSource
    {
        private readonly int owner = Environment.CurrentManagedThreadId;
        internal readonly byte[] Packet = BitConverter.GetBytes(invalidSample ? float.NaN : .5f);
        internal int Starts, Stops, Disposals, Reads;
        public string Id => "mic";
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public PcmFormat Format { get; } = new(48000, 1, 32, true, 4);
        public void Start()
        {
            SameThread(); Starts++; startEntered?.Set();
            if (releaseStart is not null) Need(releaseStart.Wait(Timeout), "Synthetic native Start timed out.");
        }
        public void Stop()
        {
            SameThread(); Stops++; stopEntered?.Set();
            if (releaseStop is not null) Need(releaseStop.Wait(Timeout), "Synthetic mic Stop timed out.");
        }
        public RecordingBlock? Read(ulong epochQpc100ns, ulong? cutoffQpc100ns)
        {
            SameThread(); Need(Starts == 1 && Stops == 0 && Disposals == 0, "PCM read outside an active mic preview.");
            return ++Reads == 1 ? new(Packet, 1, 1, 0, epochQpc100ns, 0, true, true, 0) : null;
        }
        public void Dispose() { SameThread(); Disposals++; }
        private void SameThread() => Need(Environment.CurrentManagedThreadId == owner, "Mic PCM/capture service moved threads.");
    }

    private static void Wait(Func<bool> condition, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition()) { if (elapsed.Elapsed > Timeout) throw new InvalidDataException(message); Thread.Sleep(1); }
    }

    private sealed class SyntheticReader(ManualResetEventSlim? entered = null, ManualResetEventSlim? release = null) : IAudioLevelPreviewReader
    {
        internal readonly int OwnerThread = Environment.CurrentManagedThreadId;
        internal readonly ApartmentState OwnerApartment = Thread.CurrentThread.GetApartmentState();
        internal int Reads, Disposals;
        public AudioLevelPreviewSnapshot Read()
        {
            SameThread(); Interlocked.Increment(ref Reads); entered?.Set();
            if (release is not null) Need(release.Wait(Timeout), "Synthetic blocked read timed out.");
            return new(new(.125f, AudioLevelPreviewState.SignalObserved), AudioLevelPreviewReading.NotSelected, 42);
        }
        public void Dispose() { SameThread(); Interlocked.Increment(ref Disposals); }
        private void SameThread() => Need(Environment.CurrentManagedThreadId == OwnerThread, "Meter object crossed its owning MTA.");
    }

    private static ProcessIdentity Id(int pid, long started) => new(pid, started, "C:\\synthetic\\p" + pid + ".exe", "synthetic" + pid);
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
