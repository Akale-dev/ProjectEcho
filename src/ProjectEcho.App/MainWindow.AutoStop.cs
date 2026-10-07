using System.Windows.Controls;
using System.Windows;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private RecordingAutoStopOptions confirmedAutoStop = new();
    private bool updatingAutoStopSelection;

    private void SetAutoStopSelection(RecordingAutoStopOptions value)
    {
        updatingAutoStopSelection = true;
        try
        {
            AutoStopCheckBox.IsChecked = value.Enabled;
            AutoStopMinutesBox.SelectedIndex = value.SilenceMinutes switch { 1 => 0, 10 => 2, _ => 1 };
        }
        finally { updatingAutoStopSelection = false; }
    }

    private async void AutoStopSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized || updatingAutoStopSelection) return;
        if (SessionRules.CanStart(state)) { UpdateControls(); return; }
        var requested = AutoStopForCurrentSelection();
        SetAutoStopSelection(confirmedAutoStop); // A click is a request; only the Host receipt changes the active setting.
        if (requested != confirmedAutoStop && state is (SessionState.Recording or SessionState.Paused))
            await RequestControlAsync("auto_stop_settings", requested);
        UpdateControls();
    }
    private RecordingAutoStopOptions AutoStopForCurrentSelection() => new(AutoStopCheckBox.IsChecked == true,
        int.Parse((string)((ComboBoxItem)AutoStopMinutesBox.SelectedItem).Tag, System.Globalization.CultureInfo.InvariantCulture));

    private void UpdateAutoStopControls(bool selectionEditable)
    {
        bool liveEditable = !WindowCloseRequested && !isTrial && !starting && pendingControl is null &&
            state is (SessionState.Recording or SessionState.Paused);
        AutoStopCheckBox.IsEnabled = selectionEditable || liveEditable;
        AutoStopMinutesBox.IsEnabled = AutoStopSilenceLabel.IsEnabled = AutoStopCheckBox.IsEnabled && AutoStopCheckBox.IsChecked == true;
        AutoStopCheckBox.Content = UiText.T("自动结束会议录音");
        AutoStopSilenceLabel.Text = UiText.T("无声音");
        foreach (ComboBoxItem item in AutoStopMinutesBox.Items)
            item.Content = UiText.F($"{item.Tag}分钟");
        string hint = UiText.T(ModeBox.SelectedIndex == 0
            ? "勾选后，所选根进程退出，或录音来源连续无声音达到所选时长时，自动停止并保存。"
            : "勾选后，所选输出设备连续无声音达到所选时长时，自动停止并保存。");
        hint += UiText.T(" 已开启的麦克风声音也会重置计时；暂停不计时，恢复后重新计时。无音频包也计入无声音时长，但不会标为已证实的静音。");
        hint += UiText.T(" 麦克风的微弱底噪不再重置计时，原始音频不变。");
        hint += UiText.T(" 录音中可启用或取消；启用或修改时长后重新计时，等待保存时不可更改。");
        AutoStopCheckBox.ToolTip = AutoStopMinutesBox.ToolTip = hint;
        System.Windows.Automation.AutomationProperties.SetName(AutoStopMinutesBox, UiText.T("无声音自动结束时长"));
    }

    private static string DescribeAutomaticStop(RecordingAutoStopInfo info) =>
        (info.Reason == "source_process_exited" ? UiText.T("自动结束：所选应用进程已退出。")
            : UiText.F($"自动结束：连续{info.SilenceMinutes}分钟未检测到声音。")) +
        (info.RemotePacketMissing || info.MicrophonePacketMissing
            ? UiText.T(" 结束时部分来源未收到音频包，不等于已证实的静音；完整性以保存结果为准。") : "");
}
