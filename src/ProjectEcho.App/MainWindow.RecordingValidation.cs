using System.Windows;
using ProjectEcho.Capture.Windows;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private FrameworkElement[] RecordingValidationFields => [ModeBox, SourceBox, EndpointBox, MicBox, SelectionPanel, DataRootButton];

    private void InitializeRecordingValidation()
    {
        TrackValidationCorrection(SourceBox, () => SourceBox.SelectedItem is ProcessSource);
        TrackValidationCorrection(EndpointBox, () => EndpointBox.SelectedItem is AudioDevice);
        TrackValidationCorrection(MicBox, () => MicBox.SelectedItem is AudioDevice);
        ModeBox.SelectionChanged += (_, _) =>
        {
            ClearValidationIssues(ModeBox, SourceBox, EndpointBox, SelectionPanel);
            if (initialized) RefreshRecordingValidationMessage();
        };
        foreach (var field in new[] { SourceBox, EndpointBox, MicBox })
            field.SelectionChanged += (_, _) => { ClearValidationIssue(SelectionPanel); RefreshRecordingValidationMessage(); };
    }

    private void RefreshRecordingValidationMessage()
    {
        if (RecordingValidationText is null) return;
        if (!RecordingValidationFields.Any(HasValidationIssue))
        { RecordingValidationText.Text = ""; RecordingValidationText.Visibility = Visibility.Collapsed; }
    }

    private void ShowRecordingValidation(string message)
    {
        RecordingValidationText.Text = message;
        RecordingValidationText.Visibility = Visibility.Visible;
        SetStatus(message);
        NavigateTo(0);
        FocusFirstValidationIssue(RecordingValidationFields);
    }

    private async Task<bool> ValidateRecordingBeforeStartAsync(CaptureOptions options, bool trial)
    {
        if (preflighting || WindowCloseRequested) return false;
        if (!DataRootAvailable) { ShowDataRootState(); return false; }
        ClearValidationIssues(RecordingValidationFields);
        RefreshRecordingValidationMessage();
        var errors = new List<string>();
        if (options.Mode == SourceMode.ProcessTree && options.ProcessSource is null)
        {
            string message = UiText.T("请选择本次录音的进程声音来源。");
            SetValidationIssue(SourceBox, message); errors.Add(message);
        }
        if (options.Mode == SourceMode.EndpointLoopback && string.IsNullOrWhiteSpace(options.RenderDeviceId))
        {
            string message = UiText.T("请选择要录制的输出设备。");
            SetValidationIssue(EndpointBox, message); errors.Add(message);
        }
        if (options.MicrophoneEnabled && string.IsNullOrWhiteSpace(options.MicrophoneDeviceId))
        {
            string message = UiText.T("已开启独立麦克风，请选择麦克风设备，或关闭麦克风。");
            SetValidationIssue(MicBox, message); errors.Add(message);
        }
        if (errors.Count > 0) { ShowRecordingValidation(string.Join("\n", errors)); return false; }
        preflighting = true;
        UpdateControls();
        SetStatus(UiText.T("正在检查所选来源与保存位置；尚未开始录音。"));
        try
        {
            var result = await Task.Run(() => recordingPreflight(options));
            if (WindowCloseRequested) return false;
            if (!result.CanStart)
            {
                HighlightRecordingErrors(result.Errors, options);
                ShowRecordingValidation(DescribePreflight(result, trial));
                return false;
            }
            SetStatus(DescribePreflight(result, trial));
            return true;
        }
        catch (Exception ex)
        {
            if (!WindowCloseRequested)
            {
                HighlightRecordingErrors([ex.Message], options);
                ShowRecordingValidation(UiText.T("预检失败（未采集）：") + ex.Message);
            }
            return false;
        }
        finally { preflighting = false; UpdateControls(); }
    }

    private void HighlightRecordingErrors(IEnumerable<string> errors, CaptureOptions options)
    {
        foreach (string error in errors)
        {
            FrameworkElement field = error.Contains("麦克风", StringComparison.Ordinal) || error.Contains("mic", StringComparison.OrdinalIgnoreCase) ? MicBox :
                error.Contains("磁盘", StringComparison.Ordinal) || error.Contains("目录", StringComparison.Ordinal) ? DataRootButton :
                error.Contains("Windows", StringComparison.Ordinal) || error.Contains("版本", StringComparison.Ordinal) ? ModeBox :
                error.Contains("来源", StringComparison.Ordinal) || error.Contains("进程", StringComparison.Ordinal) || error.Contains("PID", StringComparison.Ordinal) ? SourceBox :
                error.Contains("端点", StringComparison.Ordinal) || error.Contains("输出设备", StringComparison.Ordinal) ? EndpointBox : SelectionPanel;
            SetValidationIssue(field, UiText.T(error));
            if (field == DataRootButton) SetValidationIssue(SelectionPanel, UiText.T("保存位置或磁盘不可用，请到诊断与设置检查数据目录。"));
        }
    }
}
