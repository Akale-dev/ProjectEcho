using ProjectEcho.Capture.Windows;

internal static class ProbeSafetyTests
{
    internal static void Run(string output)
    {
        var owned = new ProcessIdentity(100, 1000, "owned.exe", "owned");
        var source = new ProcessSource(owned, owned, "endpoint", "Synthetic endpoint", "Active", "Synthetic fixture");
        Require(SyntheticAudioProbe.IsOwnedSelection(source, owned), "Correct producer identity rejected.");
        foreach (var changed in new[] { owned with { ProcessId = 101 }, owned with { StartTimeUtcTicks = 1001 }, owned with { ExecutablePath = "other.exe" } })
        {
            Require(!SyntheticAudioProbe.IsOwnedSelection(source with { Root = changed }, owned), "Foreign/reused root accepted.");
            Require(!SyntheticAudioProbe.IsOwnedSelection(source with { AudioProcess = changed }, owned), "Foreign audio process accepted.");
        }
        var options = SyntheticAudioProbe.CreateOptions(source, output);
        Require(options.Mode == SourceMode.ProcessTree && !options.MicrophoneEnabled && options.MicrophoneDeviceId is null
            && options.RenderDeviceId is null && !options.EndpointScopeConfirmed && options.MaximumSeconds == 10,
            "Probe capture scope was broadened.");
        Require(SyntheticAudioProbe.RunAsync(["--synthetic-audio-probe", "--pid", "101"]).GetAwaiter().GetResult() == 2,
            "Live entry accepted arbitrary PID/options.");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int goCount = 0;
        var handshake = SyntheticAudioProbe.BeginTonesAfterStartedAsync(started.Task, () => { goCount++; return Task.CompletedTask; }, CancellationToken.None);
        Require(goCount == 0 && !handshake.IsCompleted, "Audible samples began before capture started.");
        started.SetResult();
        handshake.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        Require(goCount == 1, "Started handshake did not begin exactly one signal.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            SyntheticAudioProbe.BeginTonesAfterStartedAsync(new TaskCompletionSource().Task,
                () => { goCount++; return Task.CompletedTask; }, canceled.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("Expected cancelled handshake.");
        }
        catch (OperationCanceledException) { }
        Require(goCount == 1, "Cancelled capture handshake emitted a signal.");
        try
        {
            SyntheticAudioProbe.BeginTonesAfterStartedAsync(Task.FromException(new IOException("Injected Host failure")),
                () => { goCount++; return Task.CompletedTask; }, CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("Expected failed handshake.");
        }
        catch (IOException) { }
        Require(goCount == 1, "Failed capture handshake emitted a signal.");
        try
        {
            SyntheticAudioProbe.SafeProjectPath(output, "../outside");
            throw new InvalidOperationException("Expected unsafe output rejection.");
        }
        catch (IOException) { }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
