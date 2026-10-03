namespace ProjectEcho.Capture.Windows;

/// <summary>
/// One bounded preparation attempt. No source is started and no capture service
/// is acquired here. Cancellation never waits for a blocked native activation;
/// the same preparation MTA disposes a late initialized client without starting it.
/// </summary>
internal sealed class MicrophonePreparation
{
    private readonly object gate = new();
    private readonly Func<IPreparedRecordingSource> prepare;
    private bool cancelled, ready, taken, exited;
    private IPreparedRecordingSource? prepared;
    private Exception? error;

    internal MicrophonePreparation(Func<IPreparedRecordingSource> prepare)
    {
        this.prepare = prepare;
        var thread = new Thread(Run) { IsBackground = true, Name = "ProjectEcho microphone preparation" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    internal bool HasExited { get { lock (gate) return exited; } }

    internal bool TryTake(out IPreparedRecordingSource? result, out Exception? failure)
    {
        lock (gate)
        {
            result = null; failure = null;
            if (!ready || cancelled || taken) return false;
            result = prepared; failure = error; prepared = null; taken = true;
            Monitor.Pulse(gate); return true;
        }
    }

    internal void Cancel()
    {
        lock (gate) { cancelled = true; Monitor.Pulse(gate); }
    }

    private void Run()
    {
        IPreparedRecordingSource? owned = null;
        try
        {
            Exception? failure = null;
            try { owned = prepare(); }
            catch (Exception ex) { failure = ex; }
            lock (gate)
            {
                if (!cancelled)
                {
                    prepared = owned; error = failure; ready = true;
                    // A completed handle remains owned by this thread until the
                    // capture thread takes it or cancellation asks us to release it.
                    while (!cancelled && !taken) Monitor.Wait(gate);
                    if (taken) owned = null;
                    prepared = null;
                }
            }
        }
        finally
        {
            try { owned?.Dispose(); }
            catch { /* An abandoned unstarted client cannot block capture shutdown. */ }
            lock (gate) exited = true;
        }
    }
}
