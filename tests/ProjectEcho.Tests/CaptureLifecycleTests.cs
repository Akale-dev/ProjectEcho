using NAudio.Wave;
using ProjectEcho.Capture.Windows;

internal static class CaptureLifecycleTests
{
    internal static void WriterFailure(string output)
    {
        var path = Path.Combine(output, "writer-startup-existing.wav");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var ready = Task.Run(() =>
        {
            using var file = new CaptureWaveFile(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        });
        int starts = 0;
        try
        {
            CaptureLifecycle.StartAfterWriterReady(ready, [() => starts++], () => false);
            throw new InvalidOperationException("Expected writer startup failure.");
        }
        catch (IOException) { }
        Require(starts == 0, "A failed original-file open must never start an audio source.");
        Require(File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Original file was changed.");
    }

    internal static void CancelDuringWriterStartup()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var entering = new ManualResetEventSlim();
        int starts = 0, stopping = 0;
        var start = Task.Run(() =>
        {
            entering.Set();
            CaptureLifecycle.StartAfterWriterReady(ready.Task, [() => Interlocked.Increment(ref starts)],
                () => Volatile.Read(ref stopping) != 0);
        });
        Require(entering.Wait(TimeSpan.FromSeconds(5)), "Startup worker did not run.");
        Require(!start.IsCompleted && starts == 0, "Source started before writer readiness.");
        Volatile.Write(ref stopping, 1);
        ready.SetResult();
        try
        {
            start.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            throw new InvalidOperationException("Expected cancelled startup.");
        }
        catch (OperationCanceledException) { }
        Require(starts == 0, "Cancelled startup must not collect PCM.");
    }

    internal static void StopsPrecedeTailWork()
    {
        var events = new List<string>();
        bool remoteStopped = false, micStopped = false;
        var failures = new List<string>();
        var actions = new[]
        {
            new CaptureStopActions(
                () => { remoteStopped = true; events.Add("remote.stop"); },
                () =>
                {
                    Require(micStopped, "Mic was collecting while remote tail work began.");
                    events.Add("remote.tail");
                    throw new IOException("Injected tail failure");
                },
                () => { events.Add("remote.release"); throw new IOException("Injected release failure"); },
                (operation, _) => failures.Add("remote." + operation)),
            new CaptureStopActions(
                () => { micStopped = true; events.Add("mic.stop"); },
                () => { Require(remoteStopped, "Remote still collecting."); events.Add("mic.tail"); },
                () => events.Add("mic.release"),
                (operation, _) => failures.Add("mic." + operation))
        };
        CaptureLifecycle.StopAllBeforeDrain(actions);
        Require(events.SequenceEqual(new[] { "remote.stop", "mic.stop", "remote.tail", "mic.tail", "remote.release", "mic.release" }),
            "Tail failure skipped valid queued data or release, or stop ordering regressed.");
        Require(failures.Count == 2, "Tail/release failures must both be reported.");

        events.Clear();
        failures.Clear();
        var stopFailure = actions[0] with { Stop = () => { events.Add("remote.stop"); throw new IOException("Injected stop failure"); } };
        CaptureLifecycle.StopAllBeforeDrain([stopFailure, actions[1]]);
        Require(events.Contains("mic.stop") && events.Contains("mic.release") && failures.Count == 3,
            "One source's failed Stop must not skip stopping and releasing the other source.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
