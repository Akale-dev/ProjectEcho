using System.Windows;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

namespace ProjectEcho.App;

public partial class MainWindow
{
    // Real preview is enabled only in normal user windows. Smoke tests explicitly
    // inject fake workers before enabling this flag; merely constructing UI never opens a mic.
    private bool levelPreviewNativeEnabled;
    private bool preparingCapturePreviewStop, microphonePreviewStopping, microphonePreviewActive;
    private IAsyncDisposable? levelPreview;
    private AudioLevelPreviewSelection? levelPreviewSelection;
    private long levelPreviewGeneration;
    private Task levelPreviewTransition = Task.CompletedTask;
    private Func<AudioLevelPreviewSelection, Action<AudioLevelPreviewSnapshot>, IAsyncDisposable> levelPreviewFactory =
        (selection, changed) => new AudioLevelPreview(selection, changed);

    private AudioLevelPreviewSelection? DesiredLevelPreview()
    {
        if (!levelPreviewNativeEnabled || !initialized || WindowCloseRequested || !SessionRules.CanStart(state) ||
            MainTabs.SelectedIndex != 0 || refreshing || preparingCapturePreviewStop || deletingMeeting)
            return null;
        var process = ModeBox.SelectedIndex == 0 ? SourceBox.SelectedItem as ProcessSource : null;
        string? endpoint = ModeBox.SelectedIndex == 1 ? (EndpointBox.SelectedItem as AudioDevice)?.Id : null;
        string? mic = microphoneRequested ? (MicBox.SelectedItem as AudioDevice)?.Id : null;
        if (process is null && endpoint is null && mic is null) return null;
        return new(ModeBox.SelectedIndex == 0 ? SourceMode.ProcessTree : SourceMode.EndpointLoopback, process, endpoint, mic);
    }

    private void UpdateLevelPreview()
    {
        var selection = DesiredLevelPreview();
        if (selection == levelPreviewSelection) return;
        levelPreviewSelection = selection;
        long generation = ++levelPreviewGeneration;
        Task previous = levelPreviewTransition;
        // Assign before yielding: start/close/MicOff can await the entire release boundary.
        levelPreviewTransition = ChangeLevelPreviewAsync(previous, selection, generation);
    }

    private async Task ChangeLevelPreviewAsync(Task previous, AudioLevelPreviewSelection? selection, long generation)
    {
        await Task.Yield();
        try
        {
            await previous;
            if (levelPreview is { } old)
            {
                // Retain the owner reference on failure. A failed release cannot
                // authorize a new recording client or a false MicOff indication.
                await old.DisposeAsync();
                if (ReferenceEquals(levelPreview, old)) levelPreview = null;
            }
            microphonePreviewActive = false;
            if (generation != levelPreviewGeneration) return;
            if (SessionRules.CanStart(state)) ResetRecordingLevelCounters();
            if (selection is not null && selection == DesiredLevelPreview())
            {
                levelPreview = levelPreviewFactory(selection, snapshot =>
                {
                    if (Dispatcher.HasShutdownStarted) return;
                    _ = Dispatcher.BeginInvoke(() => ApplyLevelPreviewSnapshot(generation, snapshot));
                });
            }
            UpdateControls();
        }
        catch (Exception ex)
        {
            if (generation == levelPreviewGeneration && !Dispatcher.HasShutdownStarted)
            {
                SetStatus(UiText.T("声音预览未确认停止：") + ex.Message);
                MicHealth.Text = UiText.T("预览异常，请关闭麦克风或退出后重试");
            }
            throw;
        }
    }

    private void ApplyLevelPreviewSnapshot(long generation, AudioLevelPreviewSnapshot snapshot)
    {
        if (generation != levelPreviewGeneration || levelPreviewSelection is null ||
            levelPreviewSelection != DesiredLevelPreview() || WindowCloseRequested || !SessionRules.CanStart(state)) return;
        microphonePreviewActive = snapshot.MicrophonePreviewStarted;
        SetLevelMeterTargets(snapshot.Remote.Peak, microphoneRequested ? snapshot.Microphone.Peak : 0);
        RemoteHealth.Text = PreviewStatus(snapshot.Remote, microphone: false);
        MicHealth.Text = PreviewStatus(snapshot.Microphone, microphone: true);
        // A failed explicit opening is visible; it does not choose another mic.
        if (microphoneRequested && snapshot.Microphone.State is AudioLevelPreviewState.Unavailable or AudioLevelPreviewState.StaleSelection)
        {
            SetMicrophoneRequested(false);
            string message = UiText.T("所选麦克风声音预览未开启，请检查设备或权限后重试。");
            SetValidationIssue(MicBox, message); ShowRecordingValidation(message);
            UpdateControls();
            return;
        }
        UpdateMicrophoneIcon(editable: true);
    }

    private static string PreviewStatus(AudioLevelPreviewReading reading, bool microphone)
    {
        string stateText = reading.State switch
        {
            AudioLevelPreviewState.NotSelected => microphone ? UiText.T("关闭，不采集") : UiText.T("尚未选择来源"),
            AudioLevelPreviewState.Preparing => UiText.T("正在开启声音预览…"),
            AudioLevelPreviewState.SignalObserved => UiText.T("已观察到声音"),
            AudioLevelPreviewState.NoActiveSession => UiText.T("尚无活动音频会话"),
            AudioLevelPreviewState.NoSignalObserved => reading.Diagnostic == "waiting_for_pcm" ? UiText.T("尚未收到音频包") : UiText.T("尚未观察到声音"),
            AudioLevelPreviewState.StaleSelection => UiText.T("来源已变化，请重新选择"),
            _ => UiText.T("电平预览不可用")
        };
        return UiText.F($"预览：{stateText} · 录音 0 帧");
    }

    private void ResetRecordingLevelCounters()
    {
        ResetLevelMeterDisplay();
        RemoteHealth.Text = UiText.T("尚未观察到声音 · 0 帧");
        MicHealth.Text = UiText.T("尚未观察到声音 · 0 帧");
        if (!microphoneRequested && !microphoneEnabled) MicHealth.Text = UiText.T("关闭 · 0 帧");
    }
}
