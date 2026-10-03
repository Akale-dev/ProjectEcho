using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using ProjectEcho.Capture.Windows;

/// <summary>
/// Explicit opt-in playback/meter probe. Two self-owned synthetic render streams;
/// no recorder, CaptureHost, microphone device, or PCM capture is ever opened.
/// </summary>
internal static class M3hAudioLevelPreviewProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1 || args[0] != "--m3h-meter-preview-probe") return 2;
        var hosts = Process.GetProcessesByName("ProjectEcho.CaptureHost");
        try
        {
            if (hosts.Length != 0) { Console.Error.WriteLine("An existing recorder is active; synthetic playback probe refused."); return 2; }
        }
        finally { foreach (var host in hosts) host.Dispose(); }
        string root = SyntheticAudioProbe.FindProjectRoot();
        string folder = SyntheticAudioProbe.SafeProjectPath(root,
            ".local/M3H/meter-native-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = timeout.Token;
        var samples = new ConcurrentQueue<(int Phase, AudioLevelPreviewSnapshot Value)>();
        var controls = new ConcurrentQueue<AudioLevelPreviewSnapshot>();
        var endpoints = new ConcurrentQueue<AudioLevelPreviewSnapshot>();
        var stale = new ConcurrentQueue<AudioLevelPreviewSnapshot>();
        var missing = new ConcurrentQueue<AudioLevelPreviewSnapshot>();
        SyntheticAudioProbe.Producer? target = null, control = null;
        AudioLevelPreview? targetPreview = null, controlPreview = null, endpointPreview = null, stalePreview = null, missingPreview = null;
        int phase = 0; int? targetExit = null, controlExit = null;
        bool isolated = false, responds = false, endpointResponds = false, identitiesFailClosed = false, micAbsent = false;
        string? failure = null;
        try
        {
            target = await SyntheticAudioProbe.Producer.StartAsync(root, "target", token);
            control = await SyntheticAudioProbe.Producer.StartAsync(root, "control", token);
            var selected = await SyntheticAudioProbe.FindOwnedSourceAsync(target, token);
            var selectedControl = await SyntheticAudioProbe.FindOwnedSourceAsync(control, token);
            Need(SyntheticAudioProbe.IsOwnedSelection(selected, target.Identity), "Target selection escaped owned producer.");
            targetPreview = new(new(SourceMode.ProcessTree, selected, null, null), sample => samples.Enqueue((Volatile.Read(ref phase), sample)));
            controlPreview = new(new(SourceMode.ProcessTree, selectedControl, null, null), controls.Enqueue);
            endpointPreview = new(new(SourceMode.EndpointLoopback, null, selectedControl.EndpointId, null), endpoints.Enqueue);
            stalePreview = new(new(SourceMode.ProcessTree, selected with { Root = selected.Root with { StartTimeUtcTicks = selected.Root.StartTimeUtcTicks + 1 } }, null, null), stale.Enqueue);
            missingPreview = new(new(SourceMode.EndpointLoopback, null, "synthetic-nonexistent-device", null), missing.Enqueue);
            await Wait(() => samples.Count > 0 && controls.Count > 0 && stale.Count > 0 && missing.Count > 0, "First native meter samples did not arrive.");
            Volatile.Write(ref phase, 1);
            await control.GoAsync(token);
            await Wait(() => controls.Any(s => s.Remote.Peak > .005f), "Control synthetic output was not measured by its own session meter.");
            await Task.Delay(800, token);
            var silentTarget = samples.Where(s => s.Phase == 1).ToArray();
            isolated = silentTarget.Length > 0 && silentTarget.All(s => s.Value.Remote.Peak == 0 &&
                s.Value.Remote.State is AudioLevelPreviewState.NoSignalObserved or AudioLevelPreviewState.NoActiveSession);
            endpointResponds = endpoints.Any(s => s.Remote.Peak > 0 && s.Remote.State == AudioLevelPreviewState.SignalObserved);
            Need(isolated, "Unrelated control output contaminated target process-tree preview or target meter was unavailable.");
            Need(endpointResponds, "Explicit all-apps endpoint meter did not respond to controlled output.");
            await control.WaitDoneAsync(token);
            Volatile.Write(ref phase, 2);
            await target.GoAsync(token);
            await Wait(() => samples.Any(s => s.Phase == 2 && s.Value.Remote.Peak > .005f), "Selected synthetic target did not drive its meter.");
            responds = true;
            identitiesFailClosed = stale.All(s => s.Remote is { Peak: 0, State: AudioLevelPreviewState.StaleSelection }) &&
                missing.All(s => s.Remote is { Peak: 0, State: AudioLevelPreviewState.Unavailable });
            micAbsent = samples.All(s => s.Value.Microphone.State == AudioLevelPreviewState.NotSelected && !s.Value.MicrophonePreviewStarted) &&
                controls.Concat(endpoints).Concat(stale).Concat(missing).All(s => s.Microphone.State == AudioLevelPreviewState.NotSelected && !s.MicrophonePreviewStarted);
            Need(identitiesFailClosed && micAbsent, "Stale identity, missing device or microphone-off preview boundary failed.");
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            File.WriteAllText(Path.Combine(folder, "failure.txt"), ex.ToString());
        }
        finally
        {
            foreach (var preview in new[] { targetPreview, controlPreview, endpointPreview, stalePreview, missingPreview })
                if (preview is not null)
                    try { await preview.DisposeAsync(); } catch (Exception ex) { failure ??= "Preview stop: " + ex.GetType().Name; }
            if (target is not null)
                try { targetExit = await target.StopAsync(); } catch (Exception ex) { failure ??= "Target stop: " + ex.GetType().Name; }
            if (control is not null)
                try { controlExit = await control.StopAsync(); } catch (Exception ex) { failure ??= "Control stop: " + ex.GetType().Name; }
        }
        using var os = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        bool pass = failure is null && isolated && responds && endpointResponds && identitiesFailClosed && micAbsent && targetExit == 0 && controlExit == 0;
        var report = new
        {
            schemaVersion = 1, status = pass ? "PASS" : "FAIL", checkedAtUtc = DateTimeOffset.UtcNow,
            test = "Actual NAudio 3.1.0 Windows session/endpoint peak meters with self-owned synthetic playback",
            environment = new { build = os?.GetValue("CurrentBuildNumber"), ubr = os?.GetValue("UBR"), displayVersion = os?.GetValue("DisplayVersion"),
                architecture = RuntimeInformation.OSArchitecture.ToString(), runtime = Environment.Version.ToString(),
                naudio = typeof(NAudio.CoreAudioApi.AudioMeterInformation).Assembly.GetName().Version?.ToString() },
            sourceModes = new[] { "ProcessTree meter", "Explicit endpoint meter" }, microphoneEnabled = false,
            captureClientCreated = false, captureHostCreated = false, pcmStored = false,
            isolated, responds, endpointResponds, identitiesFailClosed, micAbsent, targetExit, controlExit, failure,
            targetSamples = samples.Count, targetSilentSamples = samples.Count(s => s.Phase == 1),
            targetMaximumPeak = samples.Select(s => s.Value.Remote.Peak).DefaultIfEmpty().Max(),
            controlMaximumPeak = controls.Select(s => s.Remote.Peak).DefaultIfEmpty().Max(),
            limitations = new[] { "No microphone opened or tested", "No live meeting recording", "No exclusive-mode meter coverage", "No physical listening judgment" }
        };
        string path = Path.Combine(folder, "report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(report.status + "; remote meters only; microphone OFF; " + path);
        return pass ? 0 : 1;

        async Task Wait(Func<bool> condition, string message)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(6)) throw new IOException(message);
                await Task.Delay(25, token);
            }
        }
    }

    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
