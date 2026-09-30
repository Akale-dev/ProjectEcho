using System.Diagnostics;

namespace ProjectEcho.Capture.Windows;

internal sealed record CaptureValidationResult(string? RemoteSegmentId, long AvailableBytes,
    Exception? DiskError, Exception? RemoteError)
{
    internal TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Exactly one background thread and one request/result slot per CaptureHost recording.
/// No PCM/device ownership, capture-event callback, join, or disposable wait handle.
/// A blocked OS query cannot block Stop or spawn replacement validators.
/// </summary>
internal sealed class CaptureValidationWorker : IDisposable
{
    private readonly object gate = new();
    private readonly Func<string?, CaptureValidationResult> validate;
    private bool disposed, busy, requested;
    private string? segment;
    private long requestedAt;
    private CaptureValidationResult? completed;
    internal bool IsStopped { get { lock (gate) return disposed; } }

    internal CaptureValidationWorker(Func<string?, CaptureValidationResult> validate)
    {
        this.validate = validate;
        var thread = new Thread(Run) { IsBackground = true, Name = "ProjectEcho bounded source validation" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    internal bool TryRequest(string? remoteSegmentId)
    {
        lock (gate)
        {
            if (disposed || busy || completed is not null) return false;
            busy = requested = true; segment = remoteSegmentId; requestedAt = Stopwatch.GetTimestamp();
            Monitor.Pulse(gate); return true;
        }
    }

    internal bool TryTake(out CaptureValidationResult? result)
    {
        lock (gate) { result = completed; completed = null; return result is not null; }
    }

    internal bool HasTimedOut(TimeSpan timeout)
    {
        lock (gate) return busy && !disposed && Stopwatch.GetElapsedTime(requestedAt) >= timeout;
    }

    private void Run()
    {
        while (true)
        {
            string? target; long startedAt;
            lock (gate)
            {
                while (!requested && !disposed) Monitor.Wait(gate);
                if (disposed) return;
                target = segment; startedAt = requestedAt; requested = false;
            }
            CaptureValidationResult result;
            try { result = validate(target); }
            catch (Exception ex) { result = new(target, 0, ex, null); }
            result = result with { Elapsed = Stopwatch.GetElapsedTime(startedAt) };
            lock (gate)
            {
                if (disposed) return;
                completed = result; busy = false;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true; requested = false; completed = null;
            // No capture wake is retained. A late native return only sees this
            // managed disposed flag and exits; capture may already be released.
            Monitor.Pulse(gate);
        }
    }
}
