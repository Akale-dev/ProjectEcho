using System.IO;
using System.Windows;
using System.Windows.Threading;
using ProjectEcho.Core;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

public partial class MainWindow
{
    internal object M3kDataRootProcessProbe(string? chosen)
    {
        if (levelPreviewNativeEnabled || host is not null || microphoneEnabled)
            throw new InvalidOperationException("Data-root process probe must not capture.");
        if (chosen is not null) ApplySelectedDataRoot(chosen);
        return new { ProcessId = Environment.ProcessId, DataRoot = dataRoot, Available = DataRootAvailable,
            LibraryRead = libraryRead, MeetingCount = RecoveryBox.Items.Count, SelectedMeeting = selectedMeetingDirectory,
            Message = DataRootText.Text, CanStart = StartButton.IsEnabled, CanImport = ImportAudioButton.IsEnabled,
            CaptureStarted = false, MicrophoneCollected = false };
    }

    internal async Task<object> M3kDataRootSmokeAsync(string evidence, CancellationToken token)
    {
        if (host is not null || microphoneEnabled) throw new InvalidOperationException("Data-root smoke must not capture.");
        string fixture = Path.Combine(evidence, "preference-fixture"); Directory.CreateDirectory(fixture);
        string preference = Path.Combine(fixture, "app-settings", "data-root.json");
        string first = Path.Combine(fixture, "first-data"), second = Path.Combine(fixture, "second-data");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        static WhisperConfiguration Configuration(string root, int threads) => new(Path.Combine(root, "synthetic.exe"),
            Path.Combine(root, "synthetic.bin"), Threads: threads);
        MeetingWhisperSettingsStore.SaveDefaults(Path.Combine(first, "settings", "whisper.json"), Configuration(first, 4), AppContext.BaseDirectory);
        MeetingWhisperSettingsStore.SaveDefaults(Path.Combine(second, "settings", "whisper.json"), Configuration(second, 9), AppContext.BaseDirectory);
        string firstDefaults = File.ReadAllText(Path.Combine(first, "settings", "whisper.json"));
        string secondDefaults = File.ReadAllText(Path.Combine(second, "settings", "whisper.json"));
        var windows = new List<MainWindow>();
        MainWindow Window()
        {
            var window = new MainWindow(enumerateSourcesOnLoad: false, dataRootPreferencePathOverride: preference);
            windows.Add(window); return window;
        }
        try
        {
            var session = Window();
            Need(!File.Exists(preference), "初次启动写入了默认路径。");
            session.ApplySelectedDataRoot(first);
            Need(session.dataRoot == first && session.CurrentWhisperConfiguration().Threads == 4, "选择目录未加载对应设置。");
            string meeting = Path.Combine(first, "M1Recordings", "synthetic-settings"); WriteCloseFixture(meeting);
            session.PrepareNewMeetingWhisperDefaults()(meeting); session.SelectMeeting(meeting, loadSavedMix: false);
            session.transcriptionView!.InitialPromptBox.Text = "private synthetic draft in first meeting";
            session.ApplySelectedDataRoot(second);
            Need(MeetingWhisperSettingsStore.LoadExisting(meeting, AppContext.BaseDirectory)?.PromptDraft == "private synthetic draft in first meeting" &&
                session.CurrentWhisperConfiguration().Threads == 9 && session.transcriptionView.InitialPromptBox.Text == "",
                "切换目录丢失旧会议提示词或让其流入新目录。");
            string savedPreference = File.ReadAllText(preference);
            var reopened = Window();
            Need(reopened.dataRoot == second && reopened.DataRootText.Text == second && reopened.CurrentWhisperConfiguration().Threads == 9 &&
                reopened.transcriptionView!.InitialPromptBox.Text == "" && File.ReadAllText(preference) == savedPreference,
                "独立窗口未恢复上次目录及其设置，或改写了偏好。");
            Directory.Move(second, second + "-offline");
            var unavailable = Window(); unavailable.Show();
            await unavailable.Dispatcher.InvokeAsync(unavailable.UpdateLayout, DispatcherPriority.Render, token);
            Need(unavailable.dataRoot == second && !unavailable.DataRootAvailable && unavailable.DataRootText.Text.Contains(second, StringComparison.Ordinal) &&
                unavailable.RecordingValidationText.Visibility == Visibility.Visible && !unavailable.StartButton.IsEnabled &&
                !unavailable.ImportAudioButton.IsEnabled && unavailable.DataRootButton.IsEnabled &&
                !unavailable.transcriptionView!.SaveConfigurationButton.IsEnabled && !unavailable.transcriptionView.SearchButton.IsEnabled,
                "不可用目录未保留/提示，或允许开始写入。");
            bool opened = false; unavailable.openDataDirectory = _ => opened = true;
            unavailable.RefreshRecovery_Click(this, new RoutedEventArgs()); unavailable.OpenDataRoot_Click(this, new RoutedEventArgs());
            unavailable.SaveWhisperConfiguration();
            Need(!opened && !Directory.Exists(second) && File.ReadAllText(preference) == savedPreference,
                "读取/打开/保存设置创建了不可用目录或更换了记忆。");
            var images = new List<string>(); unavailable.SaveLayoutImage(evidence, "unavailable-data-root.png", 1, images);
            unavailable.ApplySelectedDataRoot(first);
            Need(unavailable.DataRootAvailable && unavailable.StartButton.IsEnabled && unavailable.dataRoot == first,
                "明确重选有效目录后没有解除阻止。");
            var recovered = Window(); Need(recovered.dataRoot == first && recovered.CurrentWhisperConfiguration().Threads == 4,
                "恢复后重启仍使用旧或默认目录。");
            File.WriteAllText(preference, "malformed synthetic preference");
            var damaged = Window();
            Need(!damaged.DataRootAvailable && damaged.dataRoot == "" && !damaged.StartButton.IsEnabled &&
                File.ReadAllText(preference) == "malformed synthetic preference", "损坏配置静默切到默认或被覆盖。");
            bool relativeBlocked = false;
            try { _ = damaged.RecordingsRoot; }
            catch (InvalidOperationException) { relativeBlocked = true; }
            Need(relativeBlocked, "空路径解析成了工作目录。");
            damaged.ApplySelectedDataRoot(first);
            Need(File.ReadAllText(Path.Combine(first, "settings", "whisper.json")) == firstDefaults &&
                File.ReadAllText(Path.Combine(second + "-offline", "settings", "whisper.json")) == secondDefaults &&
                !File.ReadAllText(preference).Contains("prompt", StringComparison.OrdinalIgnoreCase), "目录记忆改写默认配置或包含提示词。");
            Need(windows.All(window => window.host is null && !window.microphoneEnabled && window.player.Source is null),
                "目录测试启动了采集或回放。");
            return new { Status = "PASS", SeparateWindowRestore = true, LastExplicitChoiceWins = true,
                MissingRootRetainedAndVisible = true, MissingRootNotCreated = true, MalformedPreferenceBlocksFallback = true,
                ExplicitRecoveryWorks = true, MeetingPromptAndDefaultsIsolated = true, Images = images,
                SourceMode = "SYNTHETIC DATA-ROOT SETTINGS ONLY / NO CAPTURE / NO PLAYBACK" };
        }
        finally
        {
            foreach (var window in windows) { window.PrepareTranscriptDraftSmokeShutdown(); window.Close(); }
        }
        static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    }
}
