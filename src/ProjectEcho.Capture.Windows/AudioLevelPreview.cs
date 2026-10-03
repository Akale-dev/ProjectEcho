using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace ProjectEcho.Capture.Windows;

/// <summary>
/// Explicit UI selections for preview. Remote uses read-only Windows meters.
/// A non-null microphone ID means the user explicitly enabled microphone preview:
/// its temporary PCM is reduced to a peak and immediately cleared, never saved.
/// </summary>
public sealed record AudioLevelPreviewSelection(SourceMode Mode, ProcessSource? ProcessSource,
    string? RenderDeviceId, string? MicrophoneDeviceId);

public enum AudioLevelPreviewState { NotSelected, Preparing, NoActiveSession, NoSignalObserved, SignalObserved, Unavailable, StaleSelection }

public sealed record AudioLevelPreviewReading(float Peak, AudioLevelPreviewState State, string? Diagnostic = null)
{
    internal static AudioLevelPreviewReading NotSelected { get; } = new(0, AudioLevelPreviewState.NotSelected);
    internal static AudioLevelPreviewReading FromPeak(float peak)
    {
        if (!float.IsFinite(peak) || peak < 0 || peak > 1)
            return new(0, AudioLevelPreviewState.Unavailable, "invalid_peak");
        return new(peak, peak > 0 ? AudioLevelPreviewState.SignalObserved : AudioLevelPreviewState.NoSignalObserved);
    }
}

/// <summary>Meter observation only: it has no recorded-frame or observed-audio counters.</summary>
public sealed record AudioLevelPreviewSnapshot(AudioLevelPreviewReading Remote,
    AudioLevelPreviewReading Microphone, long ObservedAtStopwatchTicks, bool MicrophonePreviewStarted = false);

/// <summary>
/// Owns all remote meter COM objects on one dedicated MTA, with a separate MTA
/// for explicitly enabled microphone preview so slow mic setup cannot stall the
/// selected remote meter. It never creates a CaptureHost, recorder or file.
/// A zero remote Windows meter is only 'no signal observed', not proven silence.
/// </summary>
public sealed class AudioLevelPreview : IDisposable, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Thread worker;
    private readonly Func<CancellationToken, IAudioLevelPreviewReader> createReader;
    private readonly Action<AudioLevelPreviewSnapshot> changed;
    private readonly TimeSpan interval;
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource cancellation = new();
    private IAudioLevelPreviewReader? activeReader;
    private bool stopped;

    public AudioLevelPreview(AudioLevelPreviewSelection selection, Action<AudioLevelPreviewSnapshot> changed)
        : this(token => new WindowsAudioLevelPreviewReader(selection, token), changed, TimeSpan.FromMilliseconds(100)) { }

    internal AudioLevelPreview(Func<IAudioLevelPreviewReader> createReader,
        Action<AudioLevelPreviewSnapshot> changed, TimeSpan interval)
        : this(_ => createReader(), changed, interval) { }

    internal AudioLevelPreview(Func<CancellationToken, IAudioLevelPreviewReader> createReader,
        Action<AudioLevelPreviewSnapshot> changed, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(createReader);
        ArgumentNullException.ThrowIfNull(changed);
        if (interval < TimeSpan.FromMilliseconds(10) || interval > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(interval));
        this.createReader = createReader; this.changed = changed; this.interval = interval;
        worker = new Thread(Run) { IsBackground = true, Name = "ProjectEcho read-only level preview" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    private void Run()
    {
        IAudioLevelPreviewReader? reader = null;
        Exception? failure = null;
        try
        {
            lock (gate) if (stopped) return;
            reader = createReader(cancellation.Token);
            Volatile.Write(ref activeReader, reader);
            lock (gate) if (stopped) reader.RequestStop();
            while (true)
            {
                lock (gate) if (stopped) return;
                var snapshot = reader.Read();
                // Dispose waits for this thread. Results already queued to a UI
                // Dispatcher still require the caller's selection-generation check.
                lock (gate) if (stopped) return;
                try { changed(snapshot); }
                catch { /* UI notification failure must not outlive or start capture. */ }
                lock (gate)
                {
                    if (stopped) return;
                    Monitor.Wait(gate, interval);
                }
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            Exception? cleanupFailure = null;
            try { reader?.Dispose(); }
            catch (Exception ex) { cleanupFailure = ex; }
            Volatile.Write(ref activeReader, null);
            // A worker error cannot advertise mic-off before the microphone's
            // real stop/release boundary. Failed release remains explicitly unsafe.
            if (failure is not null || cleanupFailure is not null)
            {
                bool notify;
                lock (gate) notify = !stopped;
                if (notify)
                {
                    var ex = cleanupFailure ?? failure!;
                    var error = new AudioLevelPreviewReading(0, AudioLevelPreviewState.Unavailable,
                        $"meter_worker:{ex.GetType().Name}:0x{ex.HResult:X8}");
                    try { changed(new(error, error, Stopwatch.GetTimestamp(), cleanupFailure is not null)); } catch { }
                }
            }
            if (cleanupFailure is null) finished.TrySetResult();
            else finished.TrySetException(cleanupFailure);
        }
    }

    /// <summary>
    /// Stops preview and joins the owner MTA, including native resource release.
    /// UI callbacks must enqueue asynchronously (never Dispatcher.Invoke), and
    /// discard queued results after selection changes/recording/pause/close.
    /// </summary>
    public void Dispose()
    {
        lock (gate) { stopped = true; Monitor.PulseAll(gate); }
        cancellation.Cancel(); // Only nonblocking managed flag callbacks are registered.
        Volatile.Read(ref activeReader)?.RequestStop();
        if (Thread.CurrentThread != worker) finished.Task.GetAwaiter().GetResult();
    }

    /// <summary>UI must await this actual mic stop/release boundary before showing MicOff or starting recording.</summary>
    public ValueTask DisposeAsync()
    {
        lock (gate) { stopped = true; Monitor.PulseAll(gate); }
        cancellation.Cancel();
        Volatile.Read(ref activeReader)?.RequestStop();
        return new ValueTask(finished.Task);
    }
}

internal interface IAudioLevelPreviewReader : IDisposable
{
    AudioLevelPreviewSnapshot Read();
    // Thread-safe cancellation flag only; native release stays on its owner MTA.
    void RequestStop() { }
}

internal sealed class WindowsAudioLevelPreviewReader(AudioLevelPreviewSelection selection, CancellationToken cancellation) : IAudioLevelPreviewReader
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly MicrophoneLevelPreview? microphone = string.IsNullOrWhiteSpace(selection.MicrophoneDeviceId)
        ? null : new MicrophoneLevelPreview(selection.MicrophoneDeviceId, cancellation);
    private ProcessSessionMeters? processMeters;

    public AudioLevelPreviewSnapshot Read()
    {
        var mic = microphone?.Current;
        return new(ReadRemote(), mic?.Reading ?? AudioLevelPreviewReading.NotSelected,
            Stopwatch.GetTimestamp(), mic?.Started ?? false);
    }

    private AudioLevelPreviewReading ReadRemote()
    {
        if (selection.Mode == SourceMode.EndpointLoopback)
            return ReadEndpoint(selection.RenderDeviceId, DataFlow.Render);
        if (selection.Mode != SourceMode.ProcessTree)
            return new(0, AudioLevelPreviewState.Unavailable, "unknown_source_mode");
        if (selection.ProcessSource is null) return AudioLevelPreviewReading.NotSelected;
        try { return ReadProcessTree(selection.ProcessSource); }
        catch (Exception ex) { return Failure(ex); }
    }

    private AudioLevelPreviewReading ReadEndpoint(string? id, DataFlow flow)
    {
        if (string.IsNullOrWhiteSpace(id)) return AudioLevelPreviewReading.NotSelected;
        try
        {
            using var device = SourceDiscovery.GetExactDevice(enumerator, id, flow);
            // The MMDevice owns its meter wrapper. No CreateAudioClient, capture
            // client, format negotiation, volume change or default-device fallback.
            return AudioLevelPreviewReading.FromPeak(device.AudioMeterInformation.MasterPeakValue);
        }
        catch (Exception ex) { return Failure(ex); }
    }

    private AudioLevelPreviewReading ReadProcessTree(ProcessSource selected)
    {
        if (SourceDiscovery.IsBroadRoot(selected.Root)) return Stale("broad_process_root");
        var before = PreviewProcessTree.Read();
        if (!before.SelectionMatches(selected)) return Stale("process_identity_or_tree_changed");
        var measured = new List<ProcessIdentity>();
        float peak = 0; int sessions = 0;
        processMeters ??= new ProcessSessionMeters();
        using (var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            for (int d = 0; d < devices.Count; d++)
            {
                using var device = devices[d];
                var result = processMeters.Read(device.ID, pid =>
                {
                    var identity = pid <= int.MaxValue ? before.Identity((int)pid) : null;
                    if (identity is null || !before.Contains(selected.Root, identity)) return false;
                    measured.Add(identity); return true;
                });
                if (result.AmbiguousScope) return new(0, AudioLevelPreviewState.Unavailable, "shared_process_session_scope_unproven");
                peak = Math.Max(peak, result.Peak); sessions += result.MatchingSessions;
            }
        }
        // Recheck before publishing: exiting/reused/reparented sources invalidate
        // the sample. Other applications' session peaks are never read or used.
        var after = PreviewProcessTree.Read();
        if (!after.SelectionMatches(selected) || measured.Any(id => !after.Contains(selected.Root, id)))
            return Stale("process_changed_during_meter_read");
        return sessions == 0 ? new(0, AudioLevelPreviewState.NoActiveSession)
            : AudioLevelPreviewReading.FromPeak(peak);
    }

    private static AudioLevelPreviewReading Stale(string reason) => new(0, AudioLevelPreviewState.StaleSelection, reason);
    private static AudioLevelPreviewReading Failure(Exception ex) => new(0, AudioLevelPreviewState.Unavailable,
        $"meter:{ex.GetType().Name}:0x{ex.HResult:X8}");
    public void RequestStop() => microphone?.RequestStop();
    public void Dispose()
    {
        try { microphone?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        finally { try { processMeters?.Dispose(); } finally { enumerator.Dispose(); } }
    }
}

/// <summary>Immutable parent snapshot plus lazily read identities; injectable for pure scope tests.</summary>
internal sealed class PreviewProcessTree(IReadOnlyDictionary<int, int> parents, Func<int, ProcessIdentity?> resolve)
{
    private readonly Dictionary<int, ProcessIdentity?> identities = new();
    internal static PreviewProcessTree Read() => new(SourceDiscovery.ReadParents(), SourceDiscovery.TryIdentity);
    internal ProcessIdentity? Identity(int pid)
    {
        if (!identities.TryGetValue(pid, out var identity)) identities[pid] = identity = resolve(pid);
        return identity;
    }
    internal bool SelectionMatches(ProcessSource source) => Contains(source.Root, source.AudioProcess);
    internal bool Contains(ProcessIdentity root, ProcessIdentity candidate)
    {
        if (!Same(root, Identity(root.ProcessId)) || !Same(candidate, Identity(candidate.ProcessId))) return false;
        var seen = new HashSet<int>();
        var current = candidate;
        for (int depth = 0; depth < 32 && seen.Add(current.ProcessId); depth++)
        {
            if (Same(root, current)) return true;
            if (!parents.TryGetValue(current.ProcessId, out int pid)) return false;
            var parent = Identity(pid);
            if (parent is null || parent.StartTimeUtcTicks > current.StartTimeUtcTicks) return false;
            current = parent;
        }
        return false;
    }
    private static bool Same(ProcessIdentity expected, ProcessIdentity? actual) => actual is not null &&
        expected.ProcessId == actual.ProcessId && expected.StartTimeUtcTicks == actual.StartTimeUtcTicks &&
        string.Equals(expected.ExecutablePath, actual.ExecutablePath, StringComparison.OrdinalIgnoreCase);
}
