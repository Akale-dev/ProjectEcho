using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private string? asrConfigurationMeeting, asrConfigurationDataRoot, legacyWhisperDataRoot, legacyWhisperBaselineNotice;
    private WhisperConfiguration? legacyMeetingWhisperDefaults;
    private bool meetingWhisperSettingsReady, meetingWhisperPromptDirty;
    private DispatcherTimer? meetingWhisperPromptTimer;
    private readonly Dictionary<string, string> pendingWhisperPrompts = new(StringComparer.OrdinalIgnoreCase);

    private void InitializeMeetingWhisperPromptPersistence()
    {
        meetingWhisperPromptTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        meetingWhisperPromptTimer.Tick += (_, _) => FlushMeetingWhisperPrompt();
        transcriptionView!.InitialPromptBox.TextChanged += (_, _) =>
        {
            if (loadingWhisperConfiguration || asrConfigurationMeeting is null || !meetingWhisperSettingsReady) return;
            pendingWhisperPrompts[asrConfigurationMeeting] = transcriptionView.InitialPromptBox.Text;
            meetingWhisperPromptDirty = true;
            meetingWhisperPromptTimer.Stop(); meetingWhisperPromptTimer.Start();
        };
        transcriptionView.InitialPromptBox.LostKeyboardFocus += (_, _) => FlushMeetingWhisperPrompt();
        Closing += (_, _) => FlushMeetingWhisperPrompt();
        Closed += (_, _) => meetingWhisperPromptTimer.Stop();
    }

    private WhisperConfiguration? ReadDefaultWhisperConfiguration()
    {
        EnsureDataRootAvailable();
        var config = PortableWhisperConfiguration.Load(WhisperConfigurationPath, AppContext.BaseDirectory);
        return config is null ? null : config with { InitialPrompt = null };
    }

    private string? EnsureMeetingWhisperBaseline(bool recoverUnreadableDefaults = false)
    {
        if (string.Equals(legacyWhisperDataRoot, dataRoot, StringComparison.OrdinalIgnoreCase)) return legacyWhisperBaselineNotice;
        string baselinePath = Path.Combine(dataRoot, "settings", MeetingWhisperSettingsStore.LegacyDefaultsFileName);
        RejectReparseAncestors(baselinePath);
        // Once frozen, an unrelated malformed global file must not invalidate the old meeting baseline.
        WhisperConfiguration? defaults = null;
        string? recoveryNotice = null;
        if (!File.Exists(baselinePath))
        {
            try { defaults = ReadDefaultWhisperConfiguration(); }
            catch (Exception ex) when (recoverUnreadableDefaults &&
                (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException))
            {
                // Only an explicit valid Save-as-default may repair unreadable defaults.
                // First freeze a null baseline; unvisited old meetings must never inherit the replacement.
                recoveryNotice = "原默认识别配置无法读取；已为既有会议固定为空配置，新默认只用于新会议。";
            }
        }
        legacyMeetingWhisperDefaults = MeetingWhisperSettingsStore.EnsureLegacyDefaults(
            Path.Combine(dataRoot, "settings"), defaults, AppContext.BaseDirectory);
        legacyWhisperDataRoot = dataRoot;
        legacyWhisperBaselineNotice = recoveryNotice;
        return recoveryNotice;
    }

    // Capture before starting an import/recording. Executing the returned action cannot pick up a later default edit.
    private Action<string> PrepareNewMeetingWhisperDefaults()
    {
        WhisperConfiguration? snapshot;
        try
        {
            snapshot = ReadDefaultWhisperConfiguration(); EnsureMeetingWhisperBaseline();
        }
        catch (Exception ex)
        {
            string failure = ex.Message;
            return directory =>
            {
                try { MeetingWhisperSettingsStore.SnapshotDefaults(directory, null, AppContext.BaseDirectory); }
                catch (Exception snapshotError) { failure += "；" + snapshotError.Message; }
                LogMeeting(directory, "asr", "新会议默认识别配置未保存：" + failure, "warning");
            };
        }
        return directory =>
        {
            try { MeetingWhisperSettingsStore.SnapshotDefaults(directory, snapshot, AppContext.BaseDirectory); }
            catch (Exception ex) { LogMeeting(directory, "asr", "新会议默认识别配置未保存：" + ex.Message, "warning"); }
        };
    }

    private void SwitchMeetingWhisperConfiguration(string? directory)
    {
        if (string.Equals(directory, asrConfigurationMeeting, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(dataRoot, asrConfigurationDataRoot, StringComparison.OrdinalIgnoreCase)) return;
        FlushMeetingWhisperPrompt();
        asrConfigurationMeeting = directory;
        asrConfigurationDataRoot = dataRoot;
        meetingWhisperPromptDirty = false;
        transcriptionView!.StatusText.Text = "";
        transcriptionView.ProgressPanel.Visibility = transcriptBusy &&
            string.Equals(recognitionMeetingDirectory, directory, StringComparison.OrdinalIgnoreCase) && directory is not null
            ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            MeetingWhisperSettings settings;
            if (directory is null) settings = new(1, ReadDefaultWhisperConfiguration(), null);
            else if (MeetingWhisperSettingsStore.LoadExisting(directory, AppContext.BaseDirectory) is { } existing) settings = existing;
            else
            {
                EnsureMeetingWhisperBaseline();
                settings = MeetingWhisperSettingsStore.LoadOrCreate(directory, legacyMeetingWhisperDefaults, AppContext.BaseDirectory);
            }
            string? prompt = directory is not null && pendingWhisperPrompts.TryGetValue(directory, out var pending) ? pending : settings.PromptDraft;
            ApplyMeetingWhisperConfiguration(settings.Configuration, prompt);
            meetingWhisperSettingsReady = true;
            meetingWhisperPromptDirty = directory is not null && pendingWhisperPrompts.ContainsKey(directory);
            if (meetingWhisperPromptDirty) meetingWhisperPromptTimer?.Start();
            if (settings.MigrationNotice is { } notice) transcriptionView!.StatusText.Text = UiText.T(notice);
        }
        catch (Exception ex)
        {
            ApplyMeetingWhisperConfiguration(null, null); meetingWhisperSettingsReady = false;
            transcriptionView!.StatusText.Text = UiText.T("本地识别配置未加载：") + ex.Message;
            LogMeeting(directory, "asr", "本场识别配置未加载：" + ex.Message, "warning");
        }
    }

    private void ApplyMeetingWhisperConfiguration(WhisperConfiguration? configuration, string? prompt)
    {
        var view = transcriptionView!; loadingWhisperConfiguration = true;
        try
        {
            ClearValidationIssues(WhisperValidationFields());
            whisperExecutable = configuration?.ExecutablePath; whisperModel = configuration?.ModelPath;
            view.WhisperExeText.Text = string.IsNullOrEmpty(whisperExecutable) ? UiText.T("未配置程序") : whisperExecutable;
            view.WhisperModelText.Text = string.IsNullOrEmpty(whisperModel) ? UiText.T("未配置模型") : whisperModel;
            var hardware = configuration?.Hardware ?? LocalTranscriptionHardware.Cpu;
            view.HardwareBox.SelectedItem = view.HardwareBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == hardware.ToString());
            view.ThreadsBox.Text = (configuration?.Threads ?? 2).ToString(CultureInfo.InvariantCulture);
            view.GpuDeviceBox.Text = (configuration?.GpuDeviceIndex ?? 0).ToString(CultureInfo.InvariantCulture);
            view.InitialPromptBox.Text = prompt ?? "";
            view.ModelLicenseBox.IsChecked = configuration?.ModelLicenseAcknowledged == true;
            view.LanguageBox.SelectedIndex = configuration?.Language == "zh" ? 1 : configuration?.Language == "en" ? 2 : 0;
            if (hardware == LocalTranscriptionHardware.Cuda)
            {
                view.HardwareBox.SelectedIndex = -1;
                string message = UiText.T("CUDA 已不再提供，请选择 CPU、自动或 Vulkan。");
                SetValidationIssue(view.HardwareBox, message); view.StatusText.Text = message;
            }
        }
        finally { loadingWhisperConfiguration = false; }
    }

    private void FlushMeetingWhisperPrompt()
    {
        meetingWhisperPromptTimer?.Stop();
        if (!meetingWhisperPromptDirty || asrConfigurationMeeting is not { } directory || transcriptionView is not { } view ||
            !meetingWhisperSettingsReady) return;
        try
        {
            MeetingWhisperSettingsStore.SavePrompt(directory, view.InitialPromptBox.Text, AppContext.BaseDirectory);
            meetingWhisperPromptDirty = false;
            pendingWhisperPrompts.Remove(directory);
        }
        catch (Exception ex)
        {
            view.StatusText.Text = UiText.T("本场提示词未保存：") + ex.Message;
            LogMeeting(directory, "asr", "本场提示词未保存：" + ex.Message, "warning");
        }
    }
}
