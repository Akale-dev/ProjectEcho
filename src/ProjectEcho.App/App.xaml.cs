using System.Windows;
using System.IO;
using System.Text.Json;

namespace ProjectEcho.App;

public partial class App : Application
{
    private SingleInstanceCoordinator? singleInstance;
    private bool activationPending;

    private void ActivateExistingWindow()
    {
        if (singleInstance is null || Dispatcher.HasShutdownStarted) return;
        if (MainWindow is null) { activationPending = true; return; }
        activationPending = false;
        WindowActivation.Restore(MainWindow);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        singleInstance?.Dispose();
        singleInstance = null;
        base.OnExit(e);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // On this Windows host the default WPF drawing path repeatedly crashed
        // during the mix/UI regression; identical SoftwareOnly runs passed.
        // This affects this UI process only, never the external Whisper GPU backend.
        // Keep an explicit isolated diagnostic path for reproducing the old failure.
        System.Windows.Media.RenderOptions.ProcessRenderMode =
            e.Args.Contains("--hardware-render-smoke") && e.Args.Contains("--m3f-recording-validation-smoke")
                ? System.Windows.Interop.RenderMode.Default : System.Windows.Interop.RenderMode.SoftwareOnly;
        bool exportCloseSmoke = e.Args.Contains("--export-close-smoke");
        bool exportHistorySmoke = e.Args.Contains("--export-history-smoke");
        bool playbackSmoke = e.Args.Contains("--playback-smoke");
        bool layoutSmoke = e.Args.Contains("--ui-layout-smoke");
        bool transcriptionSmoke = e.Args.Contains("--transcription-smoke");
        bool m3ReplaySmoke = e.Args.Contains("--m3-replay-smoke");
        bool realWhisperSmoke = e.Args.Contains("--m3-whisper-smoke");
        bool whisperProgressSmoke = e.Args.Contains("--m3-whisper-progress-smoke");
        bool m3cTranscriptSmoke = e.Args.Contains("--m3c-transcript-smoke");
        bool m3cLibrarySmoke = e.Args.Contains("--m3c-library-smoke");
        bool m3dVersionSmoke = e.Args.Contains("--m3d-transcript-version-smoke");
        bool m3dNavigationSmoke = e.Args.Contains("--m3d-navigation-smoke");
        bool m3dSettingsSmoke = e.Args.Contains("--m3d-settings-smoke");
        bool m3fUiSmoke = e.Args.Contains("--m3f-ui-smoke");
        bool m3fRecordingSmoke = e.Args.Contains("--m3f-recording-validation-smoke");
        bool m3fReplaySmoke = e.Args.Contains("--m3f-replay-readiness-smoke");
        bool m3gUiSmoke = e.Args.Contains("--m3g-ui-smoke");
        bool m3gTimeSmoke = e.Args.Contains("--m3g-time-smoke");
        bool m3hUiSmoke = e.Args.Contains("--m3h-ui-smoke");
        bool m3hAsrSmoke = e.Args.Contains("--m3h-meeting-asr-smoke");
        bool m3hPreviewSmoke = e.Args.Contains("--m3h-preview-smoke");
        bool m3hFixPauseSmoke = e.Args.Contains("--m3h-fix-pause-smoke");
        bool m3jUiSmoke = e.Args.Contains("--m3j-ui-smoke");
        try
        {
            if (e.Args.Contains("--single-instance-probe"))
            {
                await SingleInstanceProbe.RunAsync(this, e.Args);
                return;
            }
            // Existing explicit smoke tests are independent processes and must not
            // activate or manipulate a user's already-open recording window.
            bool isolatedSmoke = exportCloseSmoke || exportHistorySmoke || playbackSmoke || layoutSmoke
                || transcriptionSmoke || m3ReplaySmoke || realWhisperSmoke || whisperProgressSmoke
                || m3cTranscriptSmoke || m3cLibrarySmoke || m3dVersionSmoke || m3dNavigationSmoke || m3dSettingsSmoke
                || m3fUiSmoke || m3fRecordingSmoke || m3fReplaySmoke || m3gUiSmoke || m3gTimeSmoke || m3hUiSmoke || m3hAsrSmoke || m3hPreviewSmoke || m3hFixPauseSmoke || m3jUiSmoke || e.Args.Contains("--smoke-test")
                || e.Args.Contains("--m3e-ui-smoke") || e.Args.Contains("--m3e-log-smoke");
            if (!isolatedSmoke)
            {
                singleInstance = await SingleInstanceCoordinator.AcquireOrActivateAsync(
                    () => Dispatcher.BeginInvoke(new Action(ActivateExistingWindow)));
                if (singleInstance is null) { Shutdown(0); return; }
            }
            if (m3jUiSmoke)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                string folder = Path.Combine(Directory.GetCurrentDirectory(), ".local", "M3J", "ui-" + DateTime.UtcNow.ToString("HHmmssfff"));
                Directory.CreateDirectory(folder);
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var testWindow = new MainWindow(enumerateSourcesOnLoad: false);
                MainWindow = testWindow; testWindow.Show();
                var result = await testWindow.M3jUiSmokeAsync(folder, deadline.Token);
                File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new
                { Os = Environment.OSVersion.Version.ToString(), Result = result }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0); return;
            }
            if (m3hUiSmoke || m3hAsrSmoke || m3hPreviewSmoke || m3hFixPauseSmoke)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                string folder = Path.Combine(Directory.GetCurrentDirectory(), ".local", m3hFixPauseSmoke ? "M3Hfix" : "M3H",
                    (m3hUiSmoke ? "ui-" : m3hAsrSmoke ? "asr-" : "preview-") + DateTime.UtcNow.ToString("HHmmssfff"));
                Directory.CreateDirectory(folder);
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var testWindow = new MainWindow(enumerateSourcesOnLoad: false);
                MainWindow = testWindow; testWindow.Show();
                var result = m3hFixPauseSmoke ? await testWindow.M3hFixPauseSmokeAsync(folder, deadline.Token)
                    : m3hUiSmoke ? await testWindow.M3hUiSmokeAsync(folder, deadline.Token)
                    : m3hAsrSmoke ? await testWindow.M3hMeetingAsrSmokeAsync(folder, deadline.Token)
                    : await testWindow.M3hPreviewSmokeAsync(folder, deadline.Token);
                File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new
                { Os = Environment.OSVersion.Version.ToString(), Result = result }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0); return;
            }
            if (m3gUiSmoke || m3gTimeSmoke)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                string folder = Path.Combine(Directory.GetCurrentDirectory(), ".local", "M3G",
                    (m3gUiSmoke ? "ui-" : "time-") + DateTime.UtcNow.ToString("HHmmssfff"));
                Directory.CreateDirectory(folder);
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var testWindow = new MainWindow(enumerateSourcesOnLoad: false);
                MainWindow = testWindow; testWindow.Show();
                var result = m3gUiSmoke ? await testWindow.M3gUiSmokeAsync(folder, deadline.Token)
                    : await testWindow.M3gTimeSmokeAsync(folder, deadline.Token);
                File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new
                { Os = Environment.OSVersion.Version.ToString(), Result = result }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0); return;
            }
            if (m3fUiSmoke || m3fRecordingSmoke || m3fReplaySmoke)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                string folder = Path.Combine(Directory.GetCurrentDirectory(), ".local", "M3F",
                    (m3fUiSmoke ? "ui-" : m3fRecordingSmoke ? "recording-" : "replay-") + DateTime.UtcNow.ToString("HHmmssfff"));
                Directory.CreateDirectory(folder);
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var testWindow = new MainWindow(enumerateSourcesOnLoad: false);
                MainWindow = testWindow; testWindow.Show();
                var result = m3fUiSmoke ? await testWindow.M3fUiSmokeAsync(folder, deadline.Token)
                    : m3fRecordingSmoke ? await testWindow.M3fRecordingValidationSmokeAsync(folder, deadline.Token)
                    : await testWindow.M3fReplayReadinessSmokeAsync(folder, deadline.Token);
                File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new
                { Os = Environment.OSVersion.Version.ToString(), Result = result }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0); return;
            }
            if (e.Args.Contains("--m3e-ui-smoke") || e.Args.Contains("--m3e-log-smoke"))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                string folder = Path.Combine(Directory.GetCurrentDirectory(), ".local", "M3E",
                    (e.Args.Contains("--m3e-log-smoke") ? "log-" : "ui-") + DateTime.UtcNow.ToString("HHmmssfff"));
                Directory.CreateDirectory(folder);
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var testWindow = new MainWindow(enumerateSourcesOnLoad: false);
                MainWindow = testWindow; testWindow.Show();
                var result = e.Args.Contains("--m3e-log-smoke") ? await testWindow.M3eLogSmokeAsync(folder, deadline.Token)
                    : await testWindow.M3eUiSmokeAsync(folder, deadline.Token);
                File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new
                { Os = Environment.OSVersion.Version.ToString(), Result = result }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0); return;
            }
            if (exportHistorySmoke)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                string folder = Path.Combine(Directory.GetCurrentDirectory(), ".local", "M3D-EndpointFollowup",
                    "ui-" + DateTime.UtcNow.ToString("HHmmssfff"));
                Directory.CreateDirectory(folder);
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var testWindow = new MainWindow(enumerateSourcesOnLoad: false);
                MainWindow = testWindow; testWindow.Show();
                var result = await testWindow.ExportHistorySmokeAsync(folder, deadline.Token);
                File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new
                { Os = Environment.OSVersion.Version.ToString(), Result = result }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0); return;
            }
            if (m3dVersionSmoke || m3dNavigationSmoke || m3dSettingsSmoke)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                string folder = Path.Combine(Directory.GetCurrentDirectory(), ".local", "M3D",
                    (m3dVersionSmoke ? "v-" : m3dSettingsSmoke ? "s-" : "n-") + DateTime.UtcNow.ToString("HHmmssfff"));
                Directory.CreateDirectory(folder);
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var testWindow = new MainWindow(enumerateSourcesOnLoad: false);
                MainWindow = testWindow; testWindow.Show();
                var result = m3dVersionSmoke ? await testWindow.M3dTranscriptVersionSmokeAsync(folder, deadline.Token)
                    : m3dSettingsSmoke ? await testWindow.M3dSettingsSmokeAsync(folder, deadline.Token)
                    : await testWindow.M3dNavigationSmokeAsync(folder, deadline.Token);
                File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new
                { Os = Environment.OSVersion.Version.ToString(), Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), Result = result },
                    new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0); return;
            }
            if (m3cTranscriptSmoke || m3cLibrarySmoke)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                string folder = Path.Combine(Directory.GetCurrentDirectory(), ".local", "diagnostics",
                    (m3cTranscriptSmoke ? "m3c-transcript-" : "m3c-library-") + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
                Directory.CreateDirectory(folder);
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var testWindow = new MainWindow(enumerateSourcesOnLoad: false);
                MainWindow = testWindow; testWindow.Show();
                var result = m3cTranscriptSmoke
                    ? await testWindow.M3cTranscriptSmokeAsync(folder, deadline.Token)
                    : await testWindow.M3cLibrarySmokeAsync(folder, deadline.Token);
                File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new
                { Os = Environment.OSVersion.Version.ToString(), Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), Result = result },
                    new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0); return;
            }
            if (m3ReplaySmoke || realWhisperSmoke || whisperProgressSmoke)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                string Option(string name)
                {
                    int index = Array.IndexOf(e.Args, name);
                    return index >= 0 && index + 1 < e.Args.Length ? Path.GetFullPath(e.Args[index + 1])
                        : throw new ArgumentException("Missing replay test option: " + name);
                }
                string folder = Path.Combine(Directory.GetCurrentDirectory(), ".local", "diagnostics",
                    (whisperProgressSmoke ? "whisper-progress-" : realWhisperSmoke ? "m3-whisper-ui-" : "m3-replay-") + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
                Directory.CreateDirectory(folder);
                using var deadline = new CancellationTokenSource(whisperProgressSmoke ? TimeSpan.FromMinutes(20) : TimeSpan.FromSeconds(90));
                var testWindow = new MainWindow(enumerateSourcesOnLoad: false);
                MainWindow = testWindow; testWindow.Show();
                var result = whisperProgressSmoke
                    ? await testWindow.RealWhisperProgressSmokeAsync(Option("--audio"), folder, deadline.Token)
                    : realWhisperSmoke
                    ? await testWindow.RealWhisperSmokeAsync(Option("--audio"), folder, deadline.Token)
                    : await testWindow.M3ReplaySmokeAsync(Option("--audio"), Option("--subtitles"), folder, deadline.Token);
                File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new
                { Os = Environment.OSVersion.Version.ToString(), Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), Result = result },
                    new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0); return;
            }
            if (layoutSmoke || transcriptionSmoke)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                string folder = Path.Combine(Directory.GetCurrentDirectory(), ".local", "diagnostics",
                    (layoutSmoke ? "m3-layout-" : "m3-transcription-") + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
                Directory.CreateDirectory(folder);
                var testWindow = new MainWindow(enumerateSourcesOnLoad: false);
                MainWindow = testWindow; testWindow.Show();
                var result = layoutSmoke ? await testWindow.UiLayoutSmokeAsync(folder) : await testWindow.TranscriptionSmokeAsync(folder);
                File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new
                { Os = Environment.OSVersion.Version.ToString(), Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), Result = result },
                    new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0); return;
            }
            if (playbackSmoke)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
                string folder = Path.Combine(Directory.GetCurrentDirectory(), ".local", "diagnostics");
                Directory.CreateDirectory(folder);
                string progress = Path.Combine(folder, "m2-wpf-playback-stage-" + stamp + ".jsonl");
                ProjectEcho.App.MainWindow.AppendPlaybackSmokeStage(progress, "app_playback_smoke_started");
                using var hardDeadline = new Timer(_ =>
                {
                    try { File.WriteAllText(Path.Combine(folder, "m2-wpf-playback-timeout-" + stamp + ".json"),
                        JsonSerializer.Serialize(new { Status = "TIMEOUT", LimitSeconds = 90, MicrophoneCollected = false, PlayerVolume = 0 })); }
                    finally { Environment.Exit(124); }
                }, null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                ProjectEcho.App.MainWindow.AppendPlaybackSmokeStage(progress, "before_window_construction");
                var testWindow = new MainWindow(enumerateSourcesOnLoad: false);
                ProjectEcho.App.MainWindow.AppendPlaybackSmokeStage(progress, "window_constructed_before_show");
                MainWindow = testWindow; testWindow.Show();
                ProjectEcho.App.MainWindow.AppendPlaybackSmokeStage(progress, "window_shown_before_test");
                var result = await testWindow.PlaybackSmokeAsync(deadline.Token, progress);
                ProjectEcho.App.MainWindow.AppendPlaybackSmokeStage(progress, "before_final_report", new { result.Passed });
                File.WriteAllText(Path.Combine(folder, "m2-wpf-playback-" + stamp + ".json"), JsonSerializer.Serialize(result.Report, new JsonSerializerOptions { WriteIndented = true }));
                ProjectEcho.App.MainWindow.AppendPlaybackSmokeStage(progress, "report_saved_before_shutdown", new { result.Passed });
                Shutdown(result.Passed ? 0 : 1);
                return;
            }
            if (exportCloseSmoke)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var results = new List<object>();
                foreach (bool duringMix in new[] { false, true })
                {
                    var testWindow = new MainWindow(enumerateSourcesOnLoad: false);
                    MainWindow = testWindow;
                    testWindow.Show();
                    results.Add(await testWindow.ExportCloseSmokeAsync(duringMix));
                }
                var evidence = Path.Combine(Directory.GetCurrentDirectory(), ".local", "diagnostics", "m2-wpf-export-close-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
                File.WriteAllText(evidence, JsonSerializer.Serialize(new { Status = "PASS", Os = Environment.OSVersion.Version.ToString(), Results = results }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
                return;
            }
            var window = new MainWindow();
            MainWindow = window;
            window.Closed += (_, _) => { singleInstance?.Dispose(); singleInstance = null; };
            window.Show();
            if (activationPending) ActivateExistingWindow();
            if (e.Args.Contains("--smoke-test"))
            {
                var report = await window.NonCaptureSmokeAsync();
                var output = Path.Combine(Directory.GetCurrentDirectory(), ".local", "diagnostics", "m0-wpf-smoke.json");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
        }
        catch (Exception ex)
        {
            if (m3jUiSmoke)
            {
                string failure = Path.Combine(Directory.GetCurrentDirectory(), ".local", "M3J", "failure-" + DateTime.UtcNow.ToString("HHmmssfff") + ".txt");
                Directory.CreateDirectory(Path.GetDirectoryName(failure)!); File.WriteAllText(failure, ex.ToString());
                Shutdown(1); return;
            }
            if (e.Args.Any(argument => argument.StartsWith("--m3h-", StringComparison.Ordinal)))
            {
                string failure = Path.Combine(Directory.GetCurrentDirectory(), ".local", m3hFixPauseSmoke ? "M3Hfix" : "M3H", "failure-" + DateTime.UtcNow.ToString("HHmmssfff") + ".txt");
                Directory.CreateDirectory(Path.GetDirectoryName(failure)!); File.WriteAllText(failure, ex.ToString());
                Shutdown(1); return;
            }
            if (e.Args.Any(argument => argument.StartsWith("--m3g-", StringComparison.Ordinal)))
            {
                string failure = Path.Combine(Directory.GetCurrentDirectory(), ".local", "M3G", "failure-" + DateTime.UtcNow.ToString("HHmmssfff") + ".txt");
                Directory.CreateDirectory(Path.GetDirectoryName(failure)!); File.WriteAllText(failure, ex.ToString());
                Shutdown(1); return;
            }
            if (e.Args.Any(argument => argument.StartsWith("--m3f-", StringComparison.Ordinal)))
            {
                string failure = Path.Combine(Directory.GetCurrentDirectory(), ".local", "M3F", "failure-" + DateTime.UtcNow.ToString("HHmmssfff") + ".txt");
                Directory.CreateDirectory(Path.GetDirectoryName(failure)!); File.WriteAllText(failure, ex.ToString());
                Shutdown(1); return;
            }
            if (e.Args.Contains("--single-instance-probe")) { Shutdown(1); return; }
            if (e.Args.Any(argument => argument.StartsWith("--m3e-", StringComparison.Ordinal)))
            {
                string failure = Path.Combine(Directory.GetCurrentDirectory(), ".local", "M3E", "failure-" + DateTime.UtcNow.ToString("HHmmssfff") + ".txt");
                Directory.CreateDirectory(Path.GetDirectoryName(failure)!); File.WriteAllText(failure, ex.ToString());
                Shutdown(1); return;
            }
            if (exportHistorySmoke || exportCloseSmoke || playbackSmoke || layoutSmoke || transcriptionSmoke || m3ReplaySmoke || realWhisperSmoke || whisperProgressSmoke || m3cTranscriptSmoke || m3cLibrarySmoke || m3dVersionSmoke || m3dNavigationSmoke || m3dSettingsSmoke)
            {
                var evidence = Path.Combine(Directory.GetCurrentDirectory(), ".local", "diagnostics", (whisperProgressSmoke ? "whisper-progress-failure-" : m3cTranscriptSmoke || m3cLibrarySmoke ? "m3c-ui-failure-" : realWhisperSmoke ? "m3-whisper-ui-failure-" : m3ReplaySmoke ? "m3-replay-failure-" : layoutSmoke || transcriptionSmoke ? "m3-ui-failure-" : playbackSmoke ? "m2-wpf-playback-failure-" : "m2-wpf-export-close-failure-") + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".txt");
                Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
                File.WriteAllText(evidence, ex.ToString());
            }
            else if (!e.Args.Contains("--smoke-test")) MessageBox.Show(ex.Message, UiText.T("ProjectEcho 启动失败"));
            Shutdown(1);
        }
    }
}
