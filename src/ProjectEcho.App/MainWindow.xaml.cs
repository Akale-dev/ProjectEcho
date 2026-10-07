using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProjectEcho.Capture.Windows;
using ProjectEcho.CaptureHost;
using ProjectEcho.Core;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow : Window
{
    private readonly string projectRoot;
    private readonly bool isolatedTestWindow;
    private string dataRoot;
    private MediaPlayer player = new() { Volume = 1 };
    private SessionState state = SessionState.Idle;
    private CaptureHostClient? host;
    private string? mixedPath;
    private bool initialized;
    private bool closeAfterSave;
    private bool closeApproved;
    private Task? windowCloseWork;
    private Task recoveryWork = Task.CompletedTask;
    private bool WindowCloseRequested => closeAfterSave || windowCloseWork is not null || closeApproved;
    private bool refreshing;
    private bool preflighting;
    private Func<CaptureOptions, PreflightResult> recordingPreflight = SourceDiscovery.Preflight;
    private bool starting;
    private bool launching;
    private bool stopRequested;
    private bool isTrial;
    private bool microphoneEnabled;
    private bool microphoneRequested;
    private bool recoveryBusy;
    private bool synchronizingMeetingSelection;
    private string? fixedMicrophoneId;
    private string? fixedMicrophoneName;
    private AudioDevice? pendingMicrophone;
    private string? pendingControl;
    private string? pendingControlRequestId;
    private long elapsedUs;
    // Capture-thread ACKs own the frozen active timeline, including while Stop
    // drains a paused session. Delayed health events cannot advance this boundary.
    private long? pausedRecordingElapsedUs;
    private Action<string>? pendingMeetingWhisperDefaults;
    private SourceSnapshot? snapshot;

    public MainWindow() : this(enumerateSourcesOnLoad: true) { }

    internal MainWindow(bool enumerateSourcesOnLoad, string? dataRootPreferencePathOverride = null, bool? refreshLibraryOnLoad = null)
    {
        projectRoot = FindProjectRoot();
        isolatedTestWindow = !enumerateSourcesOnLoad;
        levelPreviewNativeEnabled = enumerateSourcesOnLoad;
        UiText.SetLanguage(enumerateSourcesOnLoad ? ReadInterfaceLanguage() : "zh-CN");
        InitializeComponent();
        dataRoot = InitializeDataRoot(enumerateSourcesOnLoad, dataRootPreferencePathOverride);
        InitializeTranscription();
        InitializeReplay();
        InitializeRecordingValidation();
        InitializeLevelMeterDisplay();
        ResetRecordingLevelCounters();
        ShowDataRootState();
        TitleBox.Text = SessionDefaults.Title(DateTimeOffset.Now);
        EnvironmentText.Text = UiText.F($"Windows build {Environment.OSVersion.Version.Build} · {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} · NAudio 3.1.0 · 不采集的预检");
        initialized = true;
        UpdateLanguageLabel();
        UpdateNavigation();
        UpdateControls();
        if (refreshLibraryOnLoad ?? enumerateSourcesOnLoad)
            Loaded += (_, _) => { if (!libraryRead && startupLibraryWork.IsCompleted) startupLibraryWork = RefreshStartupLibraryAsync(); };
        if (enumerateSourcesOnLoad) Loaded += async (_, _) => await RefreshSourcesAsync();
    }

    private static string FindProjectRoot()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.json"))) return AppContext.BaseDirectory;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) && File.Exists(Path.Combine(directory.FullName, "global.json")))
                return directory.FullName;
        throw new DirectoryNotFoundException(UiText.T("请从完整的 ProjectEcho 便携文件夹或开发工程内运行。"));
    }

    private void SetStatus(string message)
    {
        message = PresentRecordingDiagnostic(message);
        StatusBox.Text = message;
        LogRecordingStatus(message);
    }

    private async Task RefreshSourcesAsync()
    {
        if (!SessionRules.CanStart(state) || refreshing || WindowCloseRequested) return;
        refreshing = true;
        UpdateControls();
        try
        {
            snapshot = await Task.Run(SourceDiscovery.Enumerate);
            SourceBox.ItemsSource = snapshot.ProcessSources;
            SourceBox.DisplayMemberPath = "DisplayName";
            SourceBox.SelectedIndex = -1;
            EndpointBox.ItemsSource = snapshot.RenderDevices;
            EndpointBox.DisplayMemberPath = "Name";
            EndpointBox.SelectedIndex = -1;
            MicBox.ItemsSource = snapshot.Microphones;
            MicBox.DisplayMemberPath = "Name";
            MicBox.SelectedIndex = -1;
            SetMicrophoneRequested(false);
            SetStatus(UiText.F($"发现 {snapshot.ProcessSources.Count} 个音频会话/树候选、{snapshot.RenderDevices.Count} 个输出端点、{snapshot.Microphones.Count} 个麦克风。请手动选择本次来源。\n") +
                string.Join(Environment.NewLine, snapshot.Warnings));
        }
        catch (Exception ex) { SetStatus(UiText.T("来源枚举失败（未采集）：") + ex.Message); }
        finally { refreshing = false; RefreshButton.IsEnabled = true; UpdateControls(); }
    }

    private CaptureOptions OptionsForCurrentSelection(bool trial = false)
    {
        EnsureDataRootAvailable();
        var directory = Path.Combine(dataRoot, trial ? "M0Recordings" : "M1Recordings",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        return new CaptureOptions(ModeBox.SelectedIndex == 0 ? SourceMode.ProcessTree : SourceMode.EndpointLoopback,
            SourceBox.SelectedItem as ProcessSource, (EndpointBox.SelectedItem as AudioDevice)?.Id,
            ModeBox.SelectedIndex == 1 && EndpointBox.SelectedItem is AudioDevice,
            microphoneRequested, microphoneRequested ? (MicBox.SelectedItem as AudioDevice)?.Id : null,
            directory, string.IsNullOrWhiteSpace(TitleBox.Text) ? SessionDefaults.Title(DateTimeOffset.Now) : TitleBox.Text.Trim(), 10);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshSourcesAsync();

    private void Selection_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        if (ReferenceEquals(sender, MicBox) && SessionRules.CanStart(state)) SetMicrophoneRequested(false);
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (!initialized) return;
        var editable = SessionRules.CanStart(state) && !WindowCloseRequested && !deletingMeeting;
        // The mic control stays reachable during a meeting. Lock source fields individually.
        SelectionPanel.IsEnabled = !WindowCloseRequested && !deletingMeeting;
        var selectionEditable = editable && !refreshing && !preflighting && !preparingCapturePreviewStop && !microphonePreviewStopping;
        ModeBox.IsEnabled = RefreshButton.IsEnabled = selectionEditable;
        RecoveryPanel.IsEnabled = editable && !recoveryBusy;
        RecoveryBox.IsEnabled = CanNavigateMeetings;
        InspectRecoveryButton.IsEnabled = editable && !recoveryBusy && selectedMeetingDirectory is not null;
        RecoverButton.IsEnabled = InspectRecoveryButton.IsEnabled && !selectedMeetingImported;
        TitleBox.IsEnabled = selectionEditable;
        UpdateAutoStopControls(selectionEditable);
        StartButton.IsEnabled = selectionEditable && DataRootAvailable;
        StopButton.IsEnabled = state is SessionState.Recording or SessionState.Paused;
        var canControl = !WindowCloseRequested && !isTrial && !starting && pendingControl is null && state is (SessionState.Recording or SessionState.Paused);
        PauseButton.IsEnabled = canControl;
        PauseButton.Content = pendingControl == "pause" ? UiText.T("正在暂停…") : pendingControl == "resume" ? UiText.T("正在恢复…") : state == SessionState.Paused ? UiText.T("恢复录音") : UiText.T("暂停");
        MicControlButton.IsEnabled = selectionEditable || (canControl && (microphoneEnabled || state == SessionState.Recording));
        UpdateMicrophoneIcon(editable);
        BookmarkButton.IsEnabled = canControl;
        PlaybackButton.IsEnabled = !WindowCloseRequested && !deletingMeeting && SessionRules.CanPlayback(state) && (mixedPath is not null || CanRequestSavedMixPlayback);
        PlaybackButton.ToolTip = state == SessionState.SavedIncomplete || currentMix is { SourceStorageComplete: false }
            ? UiText.T("试听混音（有警告）") : UiText.T("播放完整录音");
        StopPlaybackButton.IsEnabled = !WindowCloseRequested && SessionRules.CanPlayback(state);
        UpdateExportControls();
        RefreshRecordingProcessingCard();
        RefreshMeetingDuration();
        if (preflighting || preparingCapturePreviewStop || microphonePreviewStopping) DataRootButton.IsEnabled = ImportAudioButton.IsEnabled = false;
        UpdateTranscriptionControls();
        UpdateReplayControls();
        SourceBox.IsEnabled = selectionEditable && ModeBox.SelectedIndex == 0;
        EndpointBox.IsEnabled = selectionEditable && ModeBox.SelectedIndex == 1;
        MicBox.IsEnabled = selectionEditable || (canControl && state == SessionState.Recording && fixedMicrophoneId is null);
        var scope = ModeBox.SelectedIndex == 0
            ? SourceBox.SelectedItem is ProcessSource source ? UiText.F($"进程树：{source.DisplayName}\n包含根进程和子进程的输出，浏览器不是单标签页。") : UiText.T("进程树：尚未选择本次音频来源。")
            : UiText.F($"从所有应用录音：{(EndpointBox.SelectedItem as AudioDevice)?.Name ?? UiText.T("尚未选择")}；该输出设备上所有应用的声音。点击开始录音即确认此范围。");
        var micDescription = editable
            ? microphonePreviewStopping || (!microphoneRequested && microphonePreviewActive) ? UiText.T("正在关闭，等待采集边界确认")
                : microphoneRequested ? (MicBox.SelectedItem as AudioDevice)?.Name ?? UiText.T("已请求开启，但尚未选设备") : UiText.T("关闭，不采集")
            : pendingControl == "mic_off" ? UiText.T("正在关闭，等待采集边界确认")
            : pendingControl == "mic_on" ? UiText.T("正在开启，等待采集确认")
            : starting && microphoneEnabled ? UiText.T("正在启动，等待采集确认")
            : state == SessionState.Stopping ? UiText.T("正在停止并保存，尚未确认停采")
            : state == SessionState.Paused ? microphoneEnabled ? UiText.T("暂停，不采集；恢复录音后开启") : UiText.T("关闭，不采集")
            : microphoneEnabled ? UiText.T("开启，与会议软件静音独立") : UiText.T("关闭，不采集");
        ScopeText.Text = scope + UiText.F($"\n独立 mic：{micDescription}") +
            (!editable && fixedMicrophoneName is not null ? UiText.F($"；固定设备：{fixedMicrophoneName}") : "");
        SessionStateText.Text = StateDescription() + (elapsedUs > 0 ? $" · {FormatElapsed(elapsedUs)}" : "");
        UpdateRecordingIndicator();
        UpdateLevelPreview();
    }

    private string StateDescription() => state == SessionState.Stopping ? UiText.T("正在停止并保存")
        : starting ? UiText.T("正在启动（等待采集确认）")
        : pendingControl == "pause" ? UiText.T("正在暂停（等待采集边界确认）")
        : pendingControl == "resume" ? UiText.T("正在恢复（等待采集确认）")
        : state switch
        {
            SessionState.Recording => isTrial ? UiText.T("正在进行 10 秒试录") : UiText.T("正在录音"),
            SessionState.Paused => UiText.T("已暂停 · 计时停止，两路均不采集"),
            SessionState.Saved => UiText.T("原轨已保存"),
            SessionState.SavedIncomplete => UiText.T("原件已保留 · 完整性有警告"),
            SessionState.Faulted or SessionState.StoppedWithError => UiText.T("录音异常 · 保存状态请查看详情"),
            _ => UiText.T("尚未录音")
        };

    private static string FormatElapsed(long microseconds) => TimelineTimestamp.FromMicroseconds(microseconds);

    private static string DescribePreflight(PreflightResult result, bool trial) =>
        $"{(result.CanStart ? UiText.T("静态预检通过；实际 API 激活与有声结果待主动录音验证") : UiText.T("不能开始录音"))}\n{result.ScopeDescription}\n" +
        UiText.F($"预算 {result.EstimatedBytesPerSecond:N0} 字节/秒；可用 {result.AvailableBytes / 1_000_000:N0} MB。\n") +
        string.Join(Environment.NewLine, result.Errors.Concat(trial ? result.Warnings :
            result.Warnings.Where(warning => !warning.StartsWith("M0 仅限", StringComparison.Ordinal))
                .Append(UiText.T("普通录音的协商格式和预计 2 小时磁盘预算，还须通过 CaptureHost 启动预检；本页静态检查不表示可以无限录音。"))));

    private async void Start_Click(object sender, RoutedEventArgs e) => await StartCaptureAsync(trial: false);

    private async Task StartCaptureAsync(bool trial)
    {
        if (!SessionRules.CanStart(state) || refreshing || preflighting || preparingCapturePreviewStop || microphonePreviewStopping || WindowCloseRequested || deletingMeeting) return;
        if (!DataRootAvailable) { ShowDataRootState(); return; }
        var options = OptionsForCurrentSelection(trial);
        var autoStop = AutoStopForCurrentSelection();
        confirmedAutoStop = autoStop;
        string titleBeforeStart = TitleBox.Text;
        // The user-enabled microphone preview must relinquish the device before
        // preflight/recording owns a capture client. Preview PCM is never written.
        preparingCapturePreviewStop = true; UpdateControls();
        try { await levelPreviewTransition; }
        catch (Exception ex) { SetStatus(UiText.T("声音预览未确认停止：") + ex.Message); preparingCapturePreviewStop = false; UpdateControls(); return; }
        // Validation has no capture ownership. A missing/stale selection must not
        // clear a usable historical mix or strand every meeting in Faulted.
        if (!await ValidateRecordingBeforeStartAsync(options, trial)) { preparingCapturePreviewStop = false; UpdateControls(); return; }
        if (WindowCloseRequested || deletingMeeting || !SessionRules.CanStart(state)) { preparingCapturePreviewStop = false; UpdateControls(); return; }
        YieldQwenToRecording();
        if (!trial) BeginRecordingProcessing(options.SessionDirectory, options.Title);
        else ClearRecordingProcessing();
        pendingMeetingWhisperDefaults = trial ? null : PrepareNewMeetingWhisperDefaults();
        FlushMeetingWhisperPrompt();
        recordingLogDirectory = trial ? null : options.SessionDirectory;
        pendingRecordingLog.Clear();
        LogRecordingStatus($"Ver. M5E Pro · Windows {Environment.OSVersion.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} · NAudio 3.1.0\n录音请求：{options.Mode}；MicEnabled={options.MicrophoneEnabled}。");
        isTrial = trial;
        fixedMicrophoneId = options.MicrophoneDeviceId;
        fixedMicrophoneName = options.MicrophoneEnabled ? (MicBox.SelectedItem as AudioDevice)?.Name : null;
        microphoneEnabled = options.MicrophoneEnabled;
        elapsedUs = 0;
        pausedRecordingElapsedUs = null;
        pendingControl = null;
        pendingControlRequestId = null;
        pendingMicrophone = null;
        // Stop and close the owned media source before any capture client exists.
        RememberFilingDraft();
        selectedFilingBaseline = null;
        ResetTranscriptPlayback();
        ClearReplaySubtitles();
        savedMixCancellation?.Cancel();
        state = SessionState.Recording;
        preparingCapturePreviewStop = false;
        ResetRecordingLevelCounters();
        starting = true;
        launching = true;
        stopRequested = false;
        mixedPath = null;
        selectedMeetingDirectory = null;
        currentMix = null;
        SelectedMeetingText.Text = UiText.T("本场录音保存后将自动生成完整混音。");
        ConnectionText.Text = UiText.T("捕获连接：正在启动 CaptureHost");
        StorageText.Text = UiText.T("保存状态：等待原轨 writer 就绪");
        UpdateControls();
        try
        {
            if (stopRequested) throw new OperationCanceledException(UiText.T("启动已取消，没有开始采集。"));
            SetStatus(UiText.T("正在建立单一 CaptureHost；不会回退到其他来源。\n"));
            if (host is not null) await host.DisposeAsync();
            var createdHost = new CaptureHostClient();
            host = createdHost;
            host.EventReceived += value => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(host, createdHost)) HandleHostEvent(value);
            });
            if (trial) await host.StartAsync(options, projectRoot, () => stopRequested);
            else await host.StartRecordingAsync(new RecordingOptions(options, AutoStop: autoStop), projectRoot, () => stopRequested);
            launching = false;
            if (stopRequested) await host.StopAsync();
        }
        catch (Exception ex)
        {
            starting = false;
            launching = false;
            state = SessionState.Stopping;
            if (host is not null)
            {
                try { await host.DisposeAsync(); host = null; }
                catch (Exception shutdownError) { SetStatus(shutdownError.Message); UpdateControls(); return; }
            }
            state = SessionState.StoppedWithError;
            ConnectionText.Text = UiText.T("捕获连接：已结束");
            StorageText.Text = UiText.T("保存状态：未确认完整保存");
            MarkRecordingProcessingUnconfirmed();
            SetStatus(UiText.T("未确认完整录音：") + ex.Message);
            recordingLogDirectory = null;
            ResetConsentAfterSession();
            TitleBox.Text = titleBeforeStart;
            HighlightRecordingErrors([ex.Message], options);
            ShowRecordingValidation(UiText.T("未确认完整录音：") + ex.Message);
            UpdateControls();
            if (closeAfterSave) Close();
        }
    }

    private async void HandleHostEvent(HostEvent value)
    {
        var eventHost = host;
        var exitConfirmed = false;
        if (value.Kind is "saved" or "incomplete" or "recording_saved" or "recording_incomplete" or "error")
        {
            state = SessionState.Stopping;
            starting = false;
            pendingControl = null;
            pendingControlRequestId = null;
            ConnectionText.Text = UiText.T("捕获连接：等待 CaptureHost 退出确认");
            UpdateControls();
            if (eventHost is not null)
            {
                try
                {
                    if (value.Kind == "error") await eventHost.DisposeAsync();
                    else await eventHost.WaitForExitAsync();
                }
                catch (Exception ex)
                {
                    if (!ReferenceEquals(host, eventHost) || state != SessionState.Stopping) return;
                    SetStatus(UiText.T("尚未确认停采：") + ex.Message);
                    return;
                }
                if (!ReferenceEquals(host, eventHost) || state != SessionState.Stopping) return;
                exitConfirmed = true;
                if (value.Kind == "error") host = null;
            }
        }
        switch (value.Kind)
        {
            case "started":
                if (!isTrial && recordingLogDirectory is { } newMeeting) pendingMeetingWhisperDefaults?.Invoke(newMeeting);
                pendingMeetingWhisperDefaults = null;
                starting = false;
                ConnectionText.Text = UiText.T("捕获连接：CaptureHost 已确认启动");
                StorageText.Text = isTrial ? UiText.T("保存状态：短试录写入中，尚未收尾") : UiText.T("保存状态：原轨写入中，等待检查点");
                if (!stopRequested) SetStatus(value.Message ?? (isTrial ? UiText.T("正在试录") : UiText.T("正在录音")));
                break;
            case "health" when value.Health is { } health && state is (SessionState.Recording or SessionState.Paused or SessionState.Stopping):
                bool clockPaused = pausedRecordingElapsedUs is not null || state == SessionState.Paused;
                if (!clockPaused && !health.Paused)
                {
                    elapsedUs = Math.Max(elapsedUs, health.ElapsedUs);
                    SetLevelMeterTargets(health.RemotePeak, microphoneEnabled && health.MicrophoneEnabled ? health.MicPeak : 0);
                    RemoteHealth.Text = UiText.F($"{health.RemoteStatus} · {health.RemoteFrames:N0} 帧");
                    if (!microphoneEnabled) MicHealth.Text = UiText.T("关闭 · 不采集");
                    else if (health.MicrophoneEnabled) MicHealth.Text = UiText.F($"{health.MicStatus} · {health.MicFrames:N0} 帧");
                }
                else if (clockPaused) ShowPausedCaptureHealth();
                ConnectionText.Text = UiText.T("捕获连接：CaptureHost 健康回报已收到");
                if (state != SessionState.Stopping)
                    StorageText.Text = UiText.F($"保存状态：写入中；最近持久检查点 {FormatElapsed(health.LastCheckpointUs)}；可用磁盘 {health.AvailableBytes / 1_000_000:N0} MB");
                break;
            case "ack" when value.Ack is { } ack:
                // Health reports never complete a requested privacy/state transition.
                // Only the capture thread's corresponding acknowledgement can do that.
                bool systemPause = !isTrial && ack.RequestId == "system-gap" && ack.Operation == "pause" && ack.Paused;
                if (state is not (SessionState.Recording or SessionState.Paused) ||
                    (!systemPause && (pendingControl != ack.Operation || pendingControlRequestId != ack.RequestId))) break;
                if (!systemPause) { pendingControl = null; pendingControlRequestId = null; }
                if (ack.Operation == "auto_stop_settings" && ack.AutoStopSettings is { } automaticSettings)
                { confirmedAutoStop = automaticSettings; SetAutoStopSelection(confirmedAutoStop); }
                if (ack.Operation == "mic_on" && ack.MicrophoneEnabled && pendingMicrophone is { } enabledDevice)
                {
                    fixedMicrophoneId = enabledDevice.Id;
                    fixedMicrophoneName = enabledDevice.Name;
                    pendingMicrophone = null;
                }
                microphoneEnabled = ack.MicrophoneEnabled;
                SetMicrophoneRequested(microphoneEnabled);
                state = ack.Paused ? SessionState.Paused : SessionState.Recording;
                if (ack.Paused)
                {
                    // Pause's capture boundary is authoritative even if a health
                    // report arrived first. Other paused controls cannot add time.
                    pausedRecordingElapsedUs = ack.Operation == "pause" ? ack.EffectiveSessionUs
                        : pausedRecordingElapsedUs ?? elapsedUs;
                    elapsedUs = pausedRecordingElapsedUs.Value;
                    ShowPausedCaptureHealth();
                }
                else if (ack.Operation == "resume")
                {
                    pausedRecordingElapsedUs = null;
                    elapsedUs = ack.EffectiveSessionUs;
                }
                else elapsedUs = Math.Max(elapsedUs, ack.EffectiveSessionUs);
                if (!ack.MicrophoneEnabled) { ResetLevelMeterDisplay(microphoneOnly: true); MicHealth.Text = UiText.T("关闭 · 不采集"); }
                SetStatus((systemPause ? UiText.T("检测到系统/调度时间缺口；CaptureHost 已确认暂停，两路均已停采。可明确恢复录音，或停止并保存。") :
                    ack.Message ?? UiText.F($"已确认{ControlDescription(ack.Operation)}")) +
                    UiText.F($" 生效时间 {FormatElapsed(ack.EffectiveSessionUs)}。"));
                break;
            case "control_error":
                if (pendingControlRequestId != value.RequestId || pendingControl is null ||
                    state is not (SessionState.Recording or SessionState.Paused)) break;
                var rejectedOperation = pendingControl;
                if (rejectedOperation == "auto_stop_settings") SetAutoStopSelection(confirmedAutoStop);
                pendingControl = null;
                pendingControlRequestId = null;
                pendingMicrophone = null;
                SetStatus(UiText.F($"{ControlDescription(rejectedOperation)}未完成：{value.Message}\n当前开关和录音状态按上次实际回执显示；可重试或停止并保存。"));
                break;
            case "levels" when value.Levels is { } levels && state is (SessionState.Recording or SessionState.Paused or SessionState.Stopping):
                if (pausedRecordingElapsedUs is not null || state == SessionState.Paused)
                {
                    ShowPausedCaptureHealth();
                    break;
                }
                SetLevelMeterTargets(levels.RemotePeak, !microphoneEnabled ? 0 : levels.MicrophonePeak);
                RemoteHealth.Text = levels.RemoteFrames == 0 ? UiText.T("尚未收到音频包") : UiText.F($"{levels.RemoteFrames:N0} 帧 · 峰值 {levels.RemotePeak:F3}");
                MicHealth.Text = microphoneEnabled ? UiText.F($"{levels.MicrophoneFrames:N0} 帧 · 峰值 {levels.MicrophonePeak:F3}") : UiText.T("关闭 · 不采集");
                if (!string.IsNullOrEmpty(levels.Status)) SetStatus(levels.Status);
                break;
            case "finalizing":
                state = SessionState.Stopping;
                starting = false;
                StorageText.Text = UiText.T("保存状态：正在排空并收尾，尚未确认保存");
                SetStatus(value.AutoStop is { } automatic ? DescribeAutomaticStop(automatic) + UiText.T(" 正在停止并保存。")
                    : value.Message ?? UiText.T("正在保存"));
                break;
            case "recording_saved":
            case "recording_incomplete":
                mixedPath = null;
                if (!exitConfirmed || value.RecordingResult is not { } recordingResult)
                {
                    state = exitConfirmed ? SessionState.StoppedWithError : SessionState.Faulted;
                    StorageText.Text = UiText.T("保存状态：缺少持久结果或 Host 退出确认");
                    MarkRecordingProcessingUnconfirmed();
                    SetStatus(UiText.T("未确认完整保存；请保留原件。") + value.Message);
                }
                else
                {
                    elapsedUs = recordingResult.DurationUs;
                    bool saved = value.Kind == "recording_saved" && recordingResult.StorageComplete;
                    state = saved ? SessionState.Saved : SessionState.SavedIncomplete;
                    StorageText.Text = saved ? UiText.T("保存状态：原轨及索引已收尾") : UiText.T("保存状态：原件已保留，完整性有警告");
                    SetStatus($"{(saved ? UiText.T("录音原轨已保存。") : UiText.T("录音原件已保留，不能称作完整录音。"))}" +
                        (recordingResult.AutoStop is { } automaticResult ? "\n" + DescribeAutomaticStop(automaticResult) : "") +
                        UiText.F($"\n目录：{recordingResult.SessionDirectory}\n") +
                        string.Join(Environment.NewLine, recordingResult.Warnings) +
                        UiText.T("\n原轨保存结束；后台混音不阻止开始下一场。"));
                    MarkRecordingProcessingSaved(recordingResult.SessionDirectory);
                    SelectMeeting(recordingResult.SessionDirectory, loadSavedMix: false);
                    RefreshRecovery_Click(this, new RoutedEventArgs());
                    if (!closeAfterSave) QueueMix(recordingResult.SessionDirectory);
                }
                ConnectionText.Text = UiText.T("捕获连接：CaptureHost 已退出");
                recordingLogDirectory = null;
                ResetConsentAfterSession();
                break;
            case "saved":
                state = SessionState.Saved;
                mixedPath = value.MixedPath;
                ConnectionText.Text = UiText.T("捕获连接：CaptureHost 已退出");
                StorageText.Text = UiText.T("保存状态：短试录原轨已收尾");
                SetStatus(UiText.F($"短试录原轨已收尾。{(mixedPath is null ? UiText.T("本次无可回放混音。") : UiText.T("混合 WAV 已生成，可回放。"))}") +
                    UiText.F($"\n目录：{value.Result?.SessionDirectory}\n{value.Message}\nM0 样机输出；末帧、恢复、平台兼容仍需验收。"));
                ResetConsentAfterSession();
                break;
            case "incomplete":
                mixedPath = null;
                ConnectionText.Text = UiText.T("捕获连接：CaptureHost 已退出");
                StorageText.Text = UiText.T("保存状态：短试录原轨已收尾，完整性有警告");
                if (exitConfirmed && value.Result is { Complete: false } incomplete)
                {
                    try
                    {
                        SetStatus(UiText.T("CaptureHost 已退出；正在检查原轨并生成有警告的试听混音。完整性状态仍未通过。"));
                        var preview = await Task.Run(() => RenderIncompletePreview(incomplete));
                        // An old asynchronous result must never enable playback in a later session.
                        if (!ReferenceEquals(host, eventHost) || state != SessionState.Stopping) return;
                        mixedPath = preview.Path;
                        state = SessionState.SavedIncomplete;
                        var warnings = incomplete.Warnings.Concat(preview.Warnings)
                            .Concat(string.IsNullOrWhiteSpace(value.Message) ? [] : new[] { value.Message! })
                            .Distinct();
                        SetStatus(UiText.F($"原轨已收尾；录音完整性未通过。可试听基于有效 QPC 的降级混音。\n目录：{incomplete.SessionDirectory}\n") +
                            string.Join(Environment.NewLine, warnings) + UiText.T("\n试听不代表时钟、末帧或平台验收通过。"));
                    }
                    catch (Exception ex)
                    {
                        if (!ReferenceEquals(host, eventHost) || state != SessionState.Stopping) return;
                        state = SessionState.StoppedWithError;
                        SetStatus(UiText.F($"原轨保留；未确认完整录音，无法生成试听混音：{ex.Message}\n{value.Message}\n{incomplete.SessionDirectory}"));
                    }
                }
                else
                {
                    state = exitConfirmed ? SessionState.StoppedWithError : SessionState.Faulted;
                    SetStatus(UiText.F($"未确认完整保存：{value.Message}\n原件保留，未自动更换音源。{value.Result?.SessionDirectory}"));
                }
                ResetConsentAfterSession();
                break;
            case "error":
                MarkRecordingProcessingUnconfirmed();
                state = exitConfirmed ? SessionState.StoppedWithError : SessionState.Faulted;
                ConnectionText.Text = exitConfirmed ? UiText.T("捕获连接：CaptureHost 已退出") : UiText.T("捕获连接：未确认退出");
                StorageText.Text = UiText.T("保存状态：未确认完整保存，原件保留");
                SetStatus(UiText.F($"未确认完整保存：{value.Message}\n原件保留，未自动更换音源。{value.Result?.SessionDirectory}"));
                ResetConsentAfterSession();
                break;
        }
        UpdateControls();
        if (closeAfterSave && SessionRules.CanStart(state)) Close();
    }

    private static RenderResult RenderIncompletePreview(CaptureResult result)
    {
        if (!result.Tracks.Any(track => track.FrameCount > 0 && track.Packets.Any(packet => packet.FrameCount > 0)))
            throw new InvalidDataException(UiText.T("没有有效音频包，不能把无包静音称作试听结果。"));
        foreach (var track in result.Tracks)
        {
            var expected = Path.GetFullPath(Path.Combine(result.SessionDirectory, "sources", track.SourceId + ".wav"));
            if (track.SourceId is not ("remote" or "mic") ||
                !string.Equals(Path.GetFullPath(track.WavePath), expected, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(expected) || File.Exists(expected + ".partial"))
                throw new InvalidDataException(UiText.T("原轨未确认收尾为本次 WAV 文件，不能生成试听混音。"));
            if (track.Packets.Any(packet => !packet.TimestampValid || (packet.Flags & 4) != 0 || packet.QpcPosition100ns < result.EpochQpc100ns))
                throw new InvalidDataException(UiText.T("存在无效 QPC，不能生成按时间对齐的试听混音。"));
        }
        var destination = Path.Combine(result.SessionDirectory, "mixed-preview-incomplete-" + Guid.NewGuid().ToString("N") + ".wav");
        // The renderer verifies finalized WAV lengths, packet storage offsets and nonoverlapping
        // QPC ranges. It preserves Complete=false and warnings and never overwrites originals.
        return TimelineWaveRenderer.Render(result, destination);
    }

    private void ResetConsentAfterSession()
    {
        recordingLogDirectory = null;
        pendingRecordingLog.Clear();
        pendingMeetingWhisperDefaults = null;
        starting = false;
        launching = false;
        pendingControl = null;
        pendingControlRequestId = null;
        pausedRecordingElapsedUs = null;
        microphoneEnabled = false;
        fixedMicrophoneId = null;
        fixedMicrophoneName = null;
        pendingMicrophone = null;
        SetMicrophoneRequested(false);
        EndpointBox.SelectedIndex = -1; // A new meeting requires an explicit endpoint selection.
        SourceBox.SelectedIndex = -1; // A completed meeting never reuses an old PID implicitly.
        MicBox.SelectedIndex = -1;
        ResetRecordingLevelCounters();
        TitleBox.Text = SessionDefaults.Title(DateTimeOffset.Now);
    }

    private void ShowPausedCaptureHealth()
    {
        ResetLevelMeterDisplay();
        RemoteHealth.Text = UiText.T("暂停 · 不采集");
        MicHealth.Text = microphoneEnabled ? UiText.T("暂停 · 不采集，恢复后开启") : UiText.T("关闭 · 不采集");
    }

    private void SetMicrophoneRequested(bool enabled)
    {
        microphoneRequested = enabled;
        if (!enabled) { ClearValidationIssue(MicBox); RefreshRecordingValidationMessage(); }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e) =>
        await RequestControlAsync(state == SessionState.Paused ? "resume" : "pause");

    private async void Bookmark_Click(object sender, RoutedEventArgs e) => await RequestControlAsync("bookmark");

    private async void MicControl_Click(object sender, RoutedEventArgs e)
    {
        if (!MicControlButton.IsEnabled) return;
        if (SessionRules.CanStart(state))
        {
            if (microphoneRequested)
            {
                microphonePreviewStopping = true;
                SetMicrophoneRequested(false); UpdateControls();
                try { await levelPreviewTransition; }
                catch (Exception ex) { SetStatus(UiText.T("声音预览未确认停止：") + ex.Message); UpdateControls(); return; }
                microphonePreviewStopping = false;
            }
            else if (MicBox.SelectedItem is AudioDevice) SetMicrophoneRequested(true);
            else { ShowMissingMicrophone(); return; }
            UpdateControls();
            return;
        }
        if (microphoneEnabled) { await RequestControlAsync("mic_off"); return; }
        var chosen = fixedMicrophoneId is not null
            ? new AudioDevice(fixedMicrophoneId, fixedMicrophoneName ?? UiText.T("本场固定麦克风"))
            : MicBox.SelectedItem as AudioDevice;
        if (chosen is null) { ShowMissingMicrophone(); return; }
        pendingMicrophone = chosen;
        await RequestControlAsync("mic_on");
    }

    private void ShowMissingMicrophone()
    {
        string message = UiText.T("请先选择麦克风，再点击图标开启本机Mic录音。");
        SetValidationIssue(MicBox, message);
        ShowRecordingValidation(message);
    }

    private void UpdateMicrophoneIcon(bool editable)
    {
        bool enabled = editable ? microphoneRequested || microphonePreviewActive || microphonePreviewStopping : microphoneEnabled;
        // Pending off retains the last acknowledged on icon; no premature privacy claim.
        MicOffMark.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        MicIconBody.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        string status = microphonePreviewStopping || (editable && !microphoneRequested && microphonePreviewActive) ? UiText.T("正在关闭，等待采集边界确认")
            : editable && microphoneRequested && levelPreviewNativeEnabled && !microphonePreviewActive ? UiText.T("正在开启声音预览…")
            : pendingControl == "mic_off" ? UiText.T("正在关闭，等待采集边界确认")
            : pendingControl == "mic_on" ? UiText.T("正在开启，等待采集确认")
            : starting && enabled ? UiText.T("正在启动，等待采集确认")
            : state == SessionState.Stopping ? UiText.T("正在停止并保存，尚未确认停采")
            : state == SessionState.Paused ? enabled ? UiText.T("暂停，不采集；恢复录音后开启") : UiText.T("关闭，不采集")
            : editable && enabled ? UiText.T("已开启 · 声音预览，不保存")
            : enabled ? UiText.T("开启，与会议软件静音独立") : UiText.T("关闭，不采集");
        MicCaptionText.Text = UiText.T("本机Mic录音");
        MicStateText.Text = status;
        string action = enabled ? UiText.T("关闭麦克风") : UiText.T("开启麦克风");
        MicControlButton.ToolTip = action + " · " + status;
        System.Windows.Automation.AutomationProperties.SetName(MicControlButton, UiText.T("本机Mic录音") + " · " + status + " · " + action);
    }

    private static string ControlDescription(string command) => command switch
    {
        "pause" => UiText.T("暂停"), "resume" => UiText.T("恢复录音"), "mic_off" => UiText.T("关闭麦克风"),
        "mic_on" => UiText.T("开启麦克风"), "bookmark" => UiText.T("标记此刻"),
        "auto_stop_settings" => UiText.T("自动结束设置"), _ => command
    };

    private async Task RequestControlAsync(string command, RecordingAutoStopOptions? automatic = null)
    {
        if (isTrial || starting || host is null || pendingControl is not null ||
            state is not (SessionState.Recording or SessionState.Paused)) return;
        pendingControl = command;
        pendingControlRequestId = Guid.NewGuid().ToString("N");
        UpdateControls();
        SetStatus(UiText.F($"正在请求{ControlDescription(command)}；等待实际采集边界确认。"));
        try { await host.ControlAsync(command, command == "mic_on" ? pendingMicrophone?.Id ?? fixedMicrophoneId : null, pendingControlRequestId, automatic); }
        catch (Exception ex)
        {
            // Never turn a failed send into a successful privacy acknowledgement.
            SetStatus(UiText.F($"{ControlDescription(command)}请求未确认：{ex.Message}\n状态尚未改变；可停止整场录音。请等待 Host 停采/保存结果。"));
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (state is not (SessionState.Recording or SessionState.Paused)) return;
        stopRequested = true;
        state = SessionState.Stopping;
        UpdateControls();
        StorageText.Text = UiText.T("保存状态：正在停止并排空，尚未确认保存");
        SetStatus(UiText.T("正在停止并保存；收到 CaptureHost 收尾确认前不会显示已保存。"));
        if (launching || host is null) return;
        try { await host.StopAsync(); }
        catch (Exception ex) { SetStatus(UiText.T("停止请求通信失败；Host 失联保护将停采，保存状态未知：") + ex.Message); }
    }

    private void Playback_Click(object sender, RoutedEventArgs e)
    {
        if (WindowCloseRequested || deletingMeeting || !SessionRules.CanPlayback(state)) return;
        if (mixedPath is null && TryQueueSavedMixPlayback(0)) return;
        if (mixedPath is null) return;
        string? directory = selectedMeetingDirectory;
        try
        {
            RejectReparseAncestors(mixedPath);
            // Play is idempotent for the current file: resume at the actual
            // media position (including a paused seek), without reopening it.
            if (playbackOpened && string.Equals(player.Source?.LocalPath, Path.GetFullPath(mixedPath), StringComparison.OrdinalIgnoreCase))
            {
                player.Play();
                playbackPaused = false;
                InvalidateReplaySubtitleFollow();
                UpdatePlaybackTransport();
                ResetDisplayPlaybackClock();
                UpdatePlaybackPosition();
                return;
            }
            // An already queued subtitle seek owns the pending position.
            if (pendingTranscriptSeek is { } pending && string.Equals(pending.Path, Path.GetFullPath(mixedPath), StringComparison.OrdinalIgnoreCase)) return;
            OpenListeningPosition(mixedPath, 0, meetingDirectory: directory);
            if (currentMix?.CompressedPath == mixedPath)
            {
                StatusBox.Text = UiText.T("正在回听已校验的完整 M4A 副本；无损大 WAV 保留，可另行导出。");
                LogMeeting(directory, "diagnostic", StatusBox.Text);
            }
        }
        catch (Exception ex)
        {
            ResetTranscriptPlayback(); StatusBox.Text = UiText.T("回放未启动：") + ex.Message;
            LogMeeting(directory, "diagnostic", StatusBox.Text, "error");
        }
    }

    private void StopPlayback_Click(object sender, RoutedEventArgs e) => ResetTranscriptPlayback();

    private string RecordingsRoot
    {
        get { EnsureDataRootAvailable(); return Path.GetFullPath(Path.Combine(dataRoot, "M1Recordings")); }
    }

    // Identity must remain stable as bindings subscribe or recognition changes.
    // Mutable notification fields in a record would change its generated hash and break WPF selection lookup.
    private sealed class RecordingDirectory(string path, string name, DateTimeOffset? date = null, bool imported = false,
        string project = "未分类", long? durationMs = null, string status = "saved") : INotifyPropertyChanged
    {
        public string Path { get; } = path;
        public string Name { get; } = name;
        public DateTimeOffset? Date { get; } = date;
        public bool Imported { get; } = imported;
        public string Project { get; } = project;
        public long? DurationMs { get; } = durationMs;
        public string Status { get; } = status;
        private bool isRecognizing;
        public bool IsRecognizing
        {
            get => isRecognizing;
            set
            {
                if (isRecognizing == value) return;
                isRecognizing = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRecognizing)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public string DateLabel => Date is { } value
            ? (Imported ? UiText.T("导入于 ") : "") + value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : UiText.T("日期待检查");
        public string DetailLabel => (Project == "未分类" ? UiText.T("未分类") : Project)
            + (DurationMs is { } ms ? " · " + TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss") : "")
            + (Status == "saved" ? "" : UiText.T(Status == "metadata_error" ? " · 信息待检查" : " · 未完成"));
    }

    private static RecordingDirectory ReadMeetingListEntry(string directory)
    {
        try
        {
            var filing = ReadFiling(directory);
            string path = Path.Combine(directory, "meeting.json"); RejectReparseAncestors(path);
            if (new FileInfo(path).Length > 16_000_000) throw new InvalidDataException(UiText.T("会议信息超过读取上限。"));
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            bool imported = root.TryGetProperty("importedAt", out var importedAt);
            DateTimeOffset? date = (imported ? importedAt : root.TryGetProperty("startedAtUtc", out var started) ? started : default)
                is { ValueKind: System.Text.Json.JsonValueKind.String } dateNode && dateNode.TryGetDateTimeOffset(out var value) ? value : null;
            // Honor each manifest's own pause-excluded, legacy elapsed, or imported timeline.
            long? durationMs = root.TryGetProperty("duration100ns", out var span) && span.TryGetInt64(out long duration100ns) &&
                duration100ns >= 0 && duration100ns <= 7L * 24 * 60 * 60 * 10_000_000 ? duration100ns / 10000 : null;
            return new(directory, string.IsNullOrWhiteSpace(filing.Title) ? UiText.T("未命名会议") : filing.Title,
                date, imported, durationMs: durationMs);
        }
        catch { return new(directory, UiText.T("信息待检查 · ") + Path.GetFileName(directory)); }
    }

    private static void RejectReparseAncestors(string path)
    {
        for (string? ancestor = Path.GetFullPath(path); !string.IsNullOrEmpty(ancestor); ancestor = Path.GetDirectoryName(ancestor))
            if ((Directory.Exists(ancestor) || File.Exists(ancestor)) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(UiText.T("拒绝重解析点路径；仅操作所选数据目录中的实际文件。"));
    }

    private string ValidateRecordingDirectory(string directory, bool mustExist = true)
    {
        string full = Path.GetFullPath(directory);
        string? parent = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.Equals(parent, RecordingsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(UiText.T("只能选择当前数据目录 M1Recordings 下的直接录音目录。"));
        RejectReparseAncestors(full);
        if (mustExist && !Directory.Exists(full)) throw new DirectoryNotFoundException(UiText.T("所选录音目录已不存在。"));
        return full;
    }

    private void OpenRecordingFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureDataRootAvailable();
            RejectReparseAncestors(RecordingsRoot);
            Directory.CreateDirectory(RecordingsRoot);
            RejectReparseAncestors(RecordingsRoot);
            Process.Start(new ProcessStartInfo(selectedMeetingDirectory is { } selected ? ValidateRecordingDirectory(selected) : RecordingsRoot) { UseShellExecute = true });
        }
        catch (Exception ex) { RecoveryStatus.Text = UiText.T("未能打开录音目录：") + ex.Message; }
    }

    private void RefreshRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (!SessionRules.CanStart(state) || recoveryBusy) return;
        CancelStartupLibraryRefresh();
        try
        {
            EnsureDataRootAvailable();
            var index = new ProjectEcho.Core.Library.MeetingLibraryIndex(dataRoot);
            index.Refresh();
            ApplyLibraryEntries(index.Query(libraryQuery));
        }
        catch (Exception ex) { RecoveryStatus.Text = UiText.T("未能列出录音目录：") + ex.Message; }
        UpdateControls();
    }

    private void RecoverySelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && !synchronizingMeetingSelection && CanNavigateMeetings && RecoveryBox.SelectedItem is RecordingDirectory selected)
        {
            NavigateToMeeting(selected, recognition: false);
        }
        UpdateControls();
    }

    private void InspectRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (!recoveryBusy && !WindowCloseRequested) recoveryWork = InspectOrRecoverAsync(createCopy: false);
    }

    private void Recover_Click(object sender, RoutedEventArgs e)
    {
        if (!recoveryBusy && !WindowCloseRequested) recoveryWork = InspectOrRecoverAsync(createCopy: true);
    }

    private async Task InspectOrRecoverAsync(bool createCopy)
    {
        if (!SessionRules.CanStart(state) || recoveryBusy || WindowCloseRequested || selectedMeetingDirectory is not { } selected) return;
        recoveryBusy = true;
        UpdateControls();
        RecoveryStatus.Text = createCopy ? UiText.T("正在校验所选原件并创建恢复副本…") : UiText.T("正在只读校验所选录音的索引、检查点和原轨哈希…");
        LogMeeting(selected, "diagnostic", RecoveryStatus.Text);
        try
        {
            string source = ValidateRecordingDirectory(selected);
            if (AudioImportService.IsImportedMeeting(source))
            {
                if (createCopy) throw new InvalidOperationException(UiText.T("外部导入录音没有采集 journal；不能执行采集恢复。原件副本已保留。"));
                var imported = await Task.Run(() => AudioImportService.Load(source));
                RecoveryStatus.Text = UiText.F($"外部录音原件副本、回放哈希与完整解码帧数通过：{imported.Mix.DurationUs / 1_000_000d:F3} 秒。\n外部录音采集时钟和原始缺口未知。");
                return;
            }
            if (createCopy)
            {
                string destination = ValidateRecordingDirectory(Path.Combine(RecordingsRoot,
                    "recovered-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")), mustExist: false);
                var result = await Task.Run(() => M1Recovery.RecoverTo(source, destination));
                RecoveryStatus.Text = UiText.F($"已创建恢复副本：{result.SessionDirectory}\n原件未修改；只包含通过检查点和哈希校验的 PCM。未确认尾部不在副本中，恢复不等于录音完整。\n") +
                    string.Join(Environment.NewLine, result.Warnings);
            }
            else
            {
                var result = await Task.Run(() => M1Recovery.Read(source));
                RecoveryStatus.Text = $"{(result.IsCleanEnd ? UiText.T("磁盘索引与保存结果校验通过。") : UiText.T("保存结果存在缺口或警告。"))}" +
                    UiText.F($"\n已确认检查点：{(result.HasCheckpoint ? result.LastCheckpointSequence.ToString() : UiText.T("无"))}；可读取原轨块：{result.Capture.Tracks.Count}。") +
                    UiText.T("\n这项检查不验证来源隔离、实际听感或双设备长期同步。\n") + string.Join(Environment.NewLine, result.Issues);
            }
        }
        catch (Exception ex) { RecoveryStatus.Text = UiText.T("所选录音检查/恢复失败；原件保留：") + ex.Message; }
        finally { LogMeeting(selected, "diagnostic", RecoveryStatus.Text, DiagnosticLevel(RecoveryStatus.Text)); recoveryBusy = false; UpdateControls(); }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closeApproved) return;
        // Cancel this close synchronously. The final Close is posted only after all
        // file work has unwound, never while this Closing event is on the stack.
        e.Cancel = true;
        if (windowCloseWork is not null) return;
        if (!ConfirmDiscardTranscriptDrafts()) return;
        if (!SessionRules.CanStart(state))
        {
            if (state == SessionState.Stopping)
            {
                closeAfterSave = true;
                CancelBackgroundForClose();
                UpdateControls();
                return;
            }
            if (MessageBox.Show(this, UiText.T("录音会话尚未保存。是否停止并保存后关闭窗口？\n选择“否”将保持窗口和当前录音状态。"),
                    UiText.T("关闭录音窗口"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            closeAfterSave = true;
            CancelBackgroundForClose();
            Stop_Click(this, new RoutedEventArgs());
            return;
        }
        closeAfterSave = true;
        CancelBackgroundForClose();
        UpdateControls();
        windowCloseWork = FinishWindowCloseAsync();
    }

    private async Task FinishWindowCloseAsync()
    {
        // Ensure Window_Closing assigns the task before this can finish or fail.
        await Task.Yield();
        try
        {
            UpdateLevelPreview();
            await levelPreviewTransition;
            FlushMeetingWhisperPrompt();
            player.Close();
            await backgroundWork;
            await transcriptWork;
            await savedMixWork;
            await replayLoadWork;
            // Recovery has no destructive cancellation API; let its current copy
            // finish safely rather than terminate it midway through an output file.
            await recoveryWork;
            CancelStartupLibraryRefresh();
            await startupLibraryWork;
            if (host is not null)
            {
                var closingHost = host;
                await closingHost.DisposeAsync();
                if (ReferenceEquals(host, closingHost)) host = null;
            }
            closeApproved = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        catch (Exception ex)
        {
            windowCloseWork = null;
            closeAfterSave = false;
            SetStatus(UiText.T("关闭收尾未确认，窗口保持打开：") + ex.Message);
            UpdateControls();
        }
    }

    internal async Task<object> NonCaptureSmokeAsync()
    {
        while (refreshing) await Task.Delay(50);
        if (snapshot is null) await RefreshSourcesAsync();
        if (microphoneRequested || state != SessionState.Idle || SourceBox.SelectedIndex != -1 || host is not null)
            throw new InvalidOperationException(UiText.T("默认隐私状态不符。"));
        if (player.Volume != 1) throw new InvalidOperationException("默认回听音量应保留文件电平。");
        VerifyNonCaptureControlStates();
        return new { Test = "WPF startup + real enumeration, NO capture", OsBuild = Environment.OSVersion.Version.ToString(),
            MicDefaultOff = true, NoCaptureHostStarted = true, NoSourceAutomaticallySelected = true, PlaybackVolume = player.Volume,
            RenderDeviceCount = snapshot?.RenderDevices.Count, MicrophoneCount = snapshot?.Microphones.Count,
            ProcessSourceCount = snapshot?.ProcessSources.Count, SyntheticUiControlStates = "PASS (no audio clients or Host)",
            RecoveryPathScopeChecks = "PASS (lexical rejection only; no historical recordings read)",
            ApiActivation = "NOT_RUN", Recording = "NOT_RUN" };
    }

    private void VerifyNonCaptureControlStates()
    {
        // State-only smoke checks. These never send IPC, create a capture client,
        // request microphone consent or claim to exercise a real microphone boundary.
        try
        {
            // Give playback a candidate so disabled assertions exercise the session
            // guard rather than passing only because no file is selected. No file is opened.
            mixedPath = Path.Combine(dataRoot, "synthetic-ui-only.wav");
            isTrial = false;
            state = SessionState.Recording;
            microphoneEnabled = false;
            fixedMicrophoneId = null;
            UpdateControls();
            if (!MicControlButton.IsEnabled || PlaybackButton.IsEnabled)
                throw new InvalidOperationException(UiText.T("初始未选麦克风时没有录音中开启入口，或误放开回听。"));
            pendingMicrophone = new AudioDevice("synthetic-first-mic", UiText.T("合成设备（未采集）"));
            pendingControl = "mic_on"; pendingControlRequestId = "first-mic-on";
            HandleHostEvent(new(1, "synthetic-ui-only", "stale-mic-on", "ack",
                Ack: new("stale-mic-on", "mic_on", false, true, 0)));
            if (fixedMicrophoneId is not null || microphoneEnabled)
                throw new InvalidOperationException(UiText.T("过期开启回执提前锁定麦克风。"));
            HandleHostEvent(new(1, "synthetic-ui-only", "first-mic-on", "control_error", "synthetic open failure"));
            if (fixedMicrophoneId is not null || pendingMicrophone is not null || microphoneEnabled)
                throw new InvalidOperationException(UiText.T("失败开启请求锁定设备或错误显示开启。"));
            pendingMicrophone = new AudioDevice("synthetic-first-mic", UiText.T("合成设备（未采集）"));
            pendingControl = "mic_on"; pendingControlRequestId = "retry-mic-on";
            HandleHostEvent(new(1, "synthetic-ui-only", "retry-mic-on", "ack",
                Ack: new("retry-mic-on", "mic_on", false, true, 0)));
            if (fixedMicrophoneId != "synthetic-first-mic" || !microphoneEnabled || pendingMicrophone is not null)
                throw new InvalidOperationException(UiText.T("成功开启回执未固定明确选择的麦克风。"));
            microphoneEnabled = true;
            fixedMicrophoneId = "synthetic-ui-only";
            pendingControl = "mic_off";
            pendingControlRequestId = "current-ui-request";
            UpdateControls();
            if (!microphoneEnabled || !ScopeText.Text.Contains(UiText.T("等待采集边界确认")) || PlaybackButton.IsEnabled || ModeBox.IsEnabled || SourceBox.IsEnabled || EndpointBox.IsEnabled)
                throw new InvalidOperationException(UiText.T("MicOff 未确认前 UI 隐私状态不符。"));
            HandleHostEvent(new(1, "synthetic-ui-only", "old-ui-request", "ack",
                Ack: new("old-ui-request", "mic_off", false, false, 0)));
            if (!microphoneEnabled || pendingControl != "mic_off")
                throw new InvalidOperationException(UiText.T("过期 MicOff 回执改变了 UI 状态。"));
            HandleHostEvent(new(1, "synthetic-ui-only", "current-ui-request", "ack",
                Ack: new("current-ui-request", "mic_off", false, false, 0)));
            if (microphoneEnabled || pendingControl is not null)
                throw new InvalidOperationException(UiText.T("匹配 MicOff 回执未确认 UI 状态。"));
            microphoneEnabled = true;
            pendingControl = "mic_off";
            pendingControlRequestId = "rejected-ui-request";
            HandleHostEvent(new(1, "synthetic-ui-only", "old-ui-request", "control_error", "synthetic rejection"));
            if (pendingControl != "mic_off") throw new InvalidOperationException(UiText.T("旧控制失败回执清除了当前请求。"));
            HandleHostEvent(new(1, "synthetic-ui-only", "rejected-ui-request", "control_error", "synthetic rejection"));
            if (!microphoneEnabled || pendingControl is not null || state != SessionState.Recording)
                throw new InvalidOperationException(UiText.T("控制失败回执改变了实际 mic/录音状态。"));
            HandleHostEvent(new(1, "synthetic-ui-only", "system-gap", "ack",
                Ack: new("system-gap", "pause", true, true, 0)));
            if (state != SessionState.Paused || !microphoneEnabled || !PauseButton.IsEnabled ||
                !ScopeText.Text.Contains(UiText.T("暂停，不采集")))
                throw new InvalidOperationException(UiText.T("系统缺口暂停未正确显示停采与恢复入口。"));
            state = SessionState.Recording;
            pendingControl = "pause";
            UpdateControls();
            if (state != SessionState.Recording || !SessionStateText.Text.Contains(UiText.T("等待采集边界确认")) || PauseButton.IsEnabled)
                throw new InvalidOperationException(UiText.T("Pause 未确认前 UI 状态不符。"));
            pendingControl = null;
            state = SessionState.Paused;
            UpdateControls();
            if (PlaybackButton.IsEnabled || StopPlaybackButton.IsEnabled || ModeBox.IsEnabled || SourceBox.IsEnabled || EndpointBox.IsEnabled || !StopButton.IsEnabled)
                throw new InvalidOperationException(UiText.T("Paused 回放锁定或停止入口不符。"));
            state = SessionState.Stopping;
            UpdateControls();
            if (PlaybackButton.IsEnabled || StartButton.IsEnabled)
                throw new InvalidOperationException(UiText.T("Stopping 尚未保存时操作未锁定。"));
            state = SessionState.Saved;
            selectedMeetingDirectory = Path.Combine(RecordingsRoot, "synthetic-new-visible-meeting");
            RecoveryBox.ItemsSource = new[] { new RecordingDirectory(Path.Combine(RecordingsRoot, "synthetic-old-picked-meeting"), UiText.T("旧合成会议")) };
            // Avoid SelectionChanged reading a fixture or any historical directory.
            initialized = false;
            RecoveryBox.SelectedIndex = 0;
            initialized = true;
            UpdateControls();
            if (!FileMeetingButton.IsEnabled)
                throw new InvalidOperationException(UiText.T("可见的新保存会议没有归档入口。"));
            selectedMeetingDirectory = null;
            UpdateControls();
            if (FileMeetingButton.IsEnabled)
                throw new InvalidOperationException(UiText.T("旧历史选择在没有可见会议时启用了归档，可能错写旧会议。"));
            exportBusy = true;
            UpdateControls();
            if (DataRootButton.IsEnabled || RetryMixButton.IsEnabled || ExportButton.IsEnabled || !CancelExportButton.IsEnabled || !StartButton.IsEnabled)
                throw new InvalidOperationException(UiText.T("后台任务互斥或新录音入口状态不符。"));
            exportBusy = false;
            var nested = Path.Combine(RecordingsRoot, "one", "nested");
            var outside = Path.Combine(projectRoot, ".local", "not-a-recording");
            foreach (var forbidden in new[] { nested, outside })
            {
                bool rejected = false;
                try { ValidateRecordingDirectory(forbidden, mustExist: false); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException(UiText.T("恢复入口接受了项目录音根目录以外的路径。"));
            }
        }
        finally
        {
            initialized = true;
            state = SessionState.Idle;
            mixedPath = null;
            selectedMeetingDirectory = null;
            currentMix = null;
            exportBusy = false;
            RecoveryBox.ItemsSource = null;
            microphoneEnabled = false;
            SetMicrophoneRequested(false);
            fixedMicrophoneId = null;
            fixedMicrophoneName = null;
            pendingMicrophone = null;
            pendingControl = null;
            pendingControlRequestId = null;
            UpdateControls();
        }
    }
}
