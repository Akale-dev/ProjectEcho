using System.Windows;
using System.IO;
using System.Text.Json;

namespace ProjectEcho.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool exportCloseSmoke = e.Args.Contains("--export-close-smoke");
        bool playbackSmoke = e.Args.Contains("--playback-smoke");
        bool layoutSmoke = e.Args.Contains("--ui-layout-smoke");
        bool transcriptionSmoke = e.Args.Contains("--transcription-smoke");
        bool m3ReplaySmoke = e.Args.Contains("--m3-replay-smoke");
        bool realWhisperSmoke = e.Args.Contains("--m3-whisper-smoke");
        bool whisperProgressSmoke = e.Args.Contains("--m3-whisper-progress-smoke");
        bool m3cTranscriptSmoke = e.Args.Contains("--m3c-transcript-smoke");
        bool m3cLibrarySmoke = e.Args.Contains("--m3c-library-smoke");
        try
        {
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
            window.Show();
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
            if (exportCloseSmoke || playbackSmoke || layoutSmoke || transcriptionSmoke || m3ReplaySmoke || realWhisperSmoke || whisperProgressSmoke || m3cTranscriptSmoke || m3cLibrarySmoke)
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
