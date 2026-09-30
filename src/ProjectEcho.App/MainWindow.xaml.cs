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
    private string dataRoot;
    private MediaPlayer player = new();
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
    private bool starting;
    private bool launching;
    private bool stopRequested;
    private bool isTrial;
    private bool microphoneEnabled;
    private bool updatingMicrophoneCheck;
    private bool recoveryBusy;
    private bool synchronizingMeetingSelection;
    private string? fixedMicrophoneId;
    private string? fixedMicrophoneName;
    private AudioDevice? pendingMicrophone;
    private bool choosingMicrophone;
    private string? pendingControl;
    private string? pendingControlRequestId;
    private long elapsedUs;
    private SourceSnapshot? snapshot;

    public MainWindow() : this(enumerateSourcesOnLoad: true) { }

    internal MainWindow(bool enumerateSourcesOnLoad)
    {
        InitializeComponent();
        projectRoot = FindProjectRoot();
        dataRoot = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.json"))
            ? Path.Combine(AppContext.BaseDirectory, "Data") : Path.Combine(projectRoot, ".local");
        dataRoot = ValidateLocalDataRoot(dataRoot);
        InitializeTranscription();
        InitializeReplay();
        DataRootText.Text = dataRoot;
        TitleBox.Text = SessionDefaults.Title(DateTimeOffset.Now);
        EnvironmentText.Text = $"Windows build {Environment.OSVersion.Version.Build} · {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} · NAudio 3.1.0 · 不采集的预检";
        initialized = true;
        UpdateNavigation();
        UpdateControls();
        if (enumerateSourcesOnLoad) Loaded += async (_, _) => await RefreshSourcesAsync();
    }

    private static string FindProjectRoot()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.json"))) return AppContext.BaseDirectory;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) && File.Exists(Path.Combine(directory.FullName, "global.json")))
                return directory.FullName;
        throw new DirectoryNotFoundException("请从完整的 ProjectEcho 便携文件夹或开发工程内运行。");
    }

    private void SetStatus(string message) => StatusBox.Text = message;

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
            MicEnabled.IsChecked = false;
            EndpointConsent.IsChecked = false;
            SetStatus($"发现 {snapshot.ProcessSources.Count} 个音频会话/树候选、{snapshot.RenderDevices.Count} 个输出端点、{snapshot.Microphones.Count} 个麦克风。请手动选择本次来源。\n" +
                string.Join(Environment.NewLine, snapshot.Warnings));
        }
        catch (Exception ex) { SetStatus("来源枚举失败（未采集）：" + ex.Message); }
        finally { refreshing = false; RefreshButton.IsEnabled = true; UpdateControls(); }
    }

    private CaptureOptions OptionsForCurrentSelection(bool trial = false)
    {
        var directory = Path.Combine(dataRoot, trial ? "M0Recordings" : "M1Recordings",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        return new CaptureOptions(ModeBox.SelectedIndex == 0 ? SourceMode.ProcessTree : SourceMode.EndpointLoopback,
            SourceBox.SelectedItem as ProcessSource, (EndpointBox.SelectedItem as AudioDevice)?.Id,
            EndpointConsent.IsChecked == true, MicEnabled.IsChecked == true, (MicBox.SelectedItem as AudioDevice)?.Id,
            directory, string.IsNullOrWhiteSpace(TitleBox.Text) ? SessionDefaults.Title(DateTimeOffset.Now) : TitleBox.Text.Trim(), 10);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshSourcesAsync();

    private void Selection_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        if (sender == ModeBox || sender == EndpointBox) EndpointConsent.IsChecked = false;
        UpdateControls();
    }

    private void Mic_Checked(object sender, RoutedEventArgs e)
    {
        if (!initialized || updatingMicrophoneCheck) return;
        if (MessageBox.Show(this, "独立麦克风可能录下平台静音时的私下声音、其他人和扬声器回声。仅本次录音开启；与会议软件静音无联动。确认开启？",
                "独立麦克风同意", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            MicEnabled.IsChecked = false;
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (!initialized) return;
        var editable = SessionRules.CanStart(state) && !WindowCloseRequested;
        SelectionPanel.IsEnabled = editable && !refreshing;
        RecoveryPanel.IsEnabled = editable && !recoveryBusy;
        RecoveryBox.IsEnabled = editable && !recoveryBusy;
        InspectRecoveryButton.IsEnabled = editable && !recoveryBusy && selectedMeetingDirectory is not null;
        RecoverButton.IsEnabled = InspectRecoveryButton.IsEnabled && !selectedMeetingImported;
        TitleBox.IsEnabled = editable;
        PreflightButton.IsEnabled = editable && !refreshing;
        StartButton.IsEnabled = editable && !refreshing;
        TrialButton.IsEnabled = editable && !refreshing;
        StopButton.IsEnabled = state is SessionState.Recording or SessionState.Paused;
        var canControl = !isTrial && !starting && !choosingMicrophone && pendingControl is null && state is (SessionState.Recording or SessionState.Paused);
        PauseButton.IsEnabled = canControl;
        PauseButton.Content = pendingControl == "pause" ? "正在暂停…" : pendingControl == "resume" ? "正在恢复…" : state == SessionState.Paused ? "恢复录音" : "暂停";
        MicControlButton.IsEnabled = canControl && (microphoneEnabled || state == SessionState.Recording);
        MicControlButton.Content = pendingControl == "mic_off" ? "正在关闭 mic…" : pendingControl == "mic_on" ? "正在开启 mic…" : microphoneEnabled ? "关闭 mic" : "开启 mic";
        BookmarkButton.IsEnabled = canControl;
        PlaybackButton.IsEnabled = !WindowCloseRequested && SessionRules.CanPlayback(state) && mixedPath is not null;
        PlaybackButton.ToolTip = state == SessionState.SavedIncomplete || currentMix is { SourceStorageComplete: false }
            ? "试听混音（有警告）" : "播放完整录音";
        StopPlaybackButton.IsEnabled = !WindowCloseRequested && SessionRules.CanPlayback(state);
        TrialPlaybackPanel.Visibility = isTrial && mixedPath is not null && selectedMeetingDirectory is null ? Visibility.Visible : Visibility.Collapsed;
        TrialPlaybackButton.IsEnabled = PlaybackButton.IsEnabled;
        UpdateExportControls();
        UpdateTranscriptionControls();
        UpdateReplayControls();
        SourceBox.IsEnabled = ModeBox.SelectedIndex == 0;
        EndpointBox.IsEnabled = ModeBox.SelectedIndex == 1;
        EndpointConsent.IsEnabled = ModeBox.SelectedIndex == 1;
        MicBox.IsEnabled = editable;
        var scope = ModeBox.SelectedIndex == 0
            ? SourceBox.SelectedItem is ProcessSource source ? $"进程树：{source.DisplayName}\n包含根进程和子进程的输出，浏览器不是单标签页。" : "进程树：尚未选择本次音频来源。"
            : $"端点兼容：{(EndpointBox.SelectedItem as AudioDevice)?.Name ?? "尚未选择"}；该端点所有应用声音。范围确认：{(EndpointConsent.IsChecked == true ? "已明确确认" : "未确认")}";
        var micDescription = editable
            ? MicEnabled.IsChecked == true ? (MicBox.SelectedItem as AudioDevice)?.Name ?? "已请求开启，但尚未选设备" : "关闭，不采集"
            : pendingControl == "mic_off" ? "正在关闭，等待采集边界确认"
            : pendingControl == "mic_on" ? "正在开启，等待采集确认"
            : starting && microphoneEnabled ? "正在启动，等待采集确认"
            : state == SessionState.Stopping ? "正在停止并保存，尚未确认停采"
            : state == SessionState.Paused ? microphoneEnabled ? "暂停，不采集；恢复录音后开启" : "关闭，不采集"
            : microphoneEnabled ? "开启，与会议软件静音独立" : "关闭，不采集";
        ScopeText.Text = scope + $"\n独立 mic：{micDescription}" +
            (!editable && fixedMicrophoneName is not null ? $"；固定设备：{fixedMicrophoneName}" : "");
        SessionStateText.Text = StateDescription() + (elapsedUs > 0 ? $" · {FormatElapsed(elapsedUs)}" : "");
    }

    private string StateDescription() => state == SessionState.Stopping ? "正在停止并保存"
        : starting ? "正在启动（等待采集确认）"
        : pendingControl == "pause" ? "正在暂停（等待采集边界确认）"
        : pendingControl == "resume" ? "正在恢复（等待采集确认）"
        : state switch
        {
            SessionState.Recording => isTrial ? "正在进行 10 秒试录" : "正在录音",
            SessionState.Paused => "已暂停 · 时间轴继续，不采集",
            SessionState.Saved => "原轨已保存",
            SessionState.SavedIncomplete => "原件已保留 · 完整性有警告",
            SessionState.Faulted => "录音异常 · 保存状态请查看详情",
            _ => "尚未录音"
        };

    private static string FormatElapsed(long microseconds) => TimeSpan.FromMicroseconds(Math.Max(0, microseconds)).ToString(@"hh\:mm\:ss");

    private async void Preflight_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = OptionsForCurrentSelection();
            var result = await Task.Run(() => SourceDiscovery.Preflight(options));
            SetStatus(DescribePreflight(result, trial: false));
        }
        catch (Exception ex) { SetStatus("预检失败（未采集）：" + ex.Message); }
    }

    private static string DescribePreflight(PreflightResult result, bool trial) =>
        $"{(result.CanStart ? "静态预检通过；实际 API 激活与有声结果待主动录音验证" : "不能开始录音")}\n{result.ScopeDescription}\n" +
        $"预算 {result.EstimatedBytesPerSecond:N0} 字节/秒；可用 {result.AvailableBytes / 1_000_000:N0} MB。\n" +
        string.Join(Environment.NewLine, result.Errors.Concat(trial ? result.Warnings :
            result.Warnings.Where(warning => !warning.StartsWith("M0 仅限", StringComparison.Ordinal))
                .Append("普通录音的协商格式和预计 2 小时磁盘预算，还须通过 CaptureHost 启动预检；本页静态检查不表示可以无限录音。")));

    private async void Start_Click(object sender, RoutedEventArgs e) => await StartCaptureAsync(trial: false);

    private async void Trial_Click(object sender, RoutedEventArgs e) => await StartCaptureAsync(trial: true);

    private async Task StartCaptureAsync(bool trial)
    {
        if (!SessionRules.CanStart(state) || refreshing || WindowCloseRequested) return;
        var options = OptionsForCurrentSelection(trial);
        isTrial = trial;
        fixedMicrophoneId = options.MicrophoneDeviceId;
        fixedMicrophoneName = (MicBox.SelectedItem as AudioDevice)?.Name;
        microphoneEnabled = options.MicrophoneEnabled;
        elapsedUs = 0;
        pendingControl = null;
        pendingControlRequestId = null;
        pendingMicrophone = null;
        // Stop and close the owned media source before any capture client exists.
        ResetTranscriptPlayback();
        ClearReplaySubtitles();
        savedMixCancellation?.Cancel();
        state = SessionState.Recording;
        starting = true;
        launching = true;
        stopRequested = false;
        mixedPath = null;
        selectedMeetingDirectory = null;
        currentMix = null;
        SelectedMeetingText.Text = "本场录音保存后将自动生成完整混音。";
        ConnectionText.Text = "捕获连接：正在启动 CaptureHost";
        StorageText.Text = "保存状态：等待原轨 writer 就绪";
        UpdateControls();
        try
        {
            var preflight = await Task.Run(() => SourceDiscovery.Preflight(options));
            if (!preflight.CanStart) throw new InvalidOperationException(DescribePreflight(preflight, trial));
            if (stopRequested) throw new OperationCanceledException("启动已取消，没有开始采集。");
            SetStatus("正在建立单一 CaptureHost；不会回退到其他来源。\n" + DescribePreflight(preflight, trial));
            if (host is not null) await host.DisposeAsync();
            var createdHost = new CaptureHostClient();
            host = createdHost;
            host.EventReceived += value => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(host, createdHost)) HandleHostEvent(value);
            });
            if (trial) await host.StartAsync(options, projectRoot, () => stopRequested);
            else await host.StartRecordingAsync(new RecordingOptions(options), projectRoot, () => stopRequested);
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
            state = SessionState.Faulted;
            ConnectionText.Text = "捕获连接：已结束";
            StorageText.Text = "保存状态：未确认完整保存";
            SetStatus("未确认完整录音：" + ex.Message);
            ResetConsentAfterSession();
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
            ConnectionText.Text = "捕获连接：等待 CaptureHost 退出确认";
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
                    SetStatus("尚未确认停采：" + ex.Message);
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
                starting = false;
                ConnectionText.Text = "捕获连接：CaptureHost 已确认启动";
                StorageText.Text = isTrial ? "保存状态：短试录写入中，尚未收尾" : "保存状态：原轨写入中，等待检查点";
                if (!stopRequested) SetStatus(value.Message ?? (isTrial ? "正在试录" : "正在录音"));
                break;
            case "health" when value.Health is { } health:
                elapsedUs = health.ElapsedUs;
                RemoteLevel.Value = health.RemotePeak;
                MicLevel.Value = health.MicPeak;
                RemoteHealth.Text = $"{health.RemoteStatus} · {health.RemoteFrames:N0} 帧";
                MicHealth.Text = $"{health.MicStatus} · {health.MicFrames:N0} 帧";
                ConnectionText.Text = "捕获连接：CaptureHost 健康回报已收到";
                StorageText.Text = $"保存状态：写入中；最近持久检查点 {FormatElapsed(health.LastCheckpointUs)}；可用磁盘 {health.AvailableBytes / 1_000_000:N0} MB";
                break;
            case "ack" when value.Ack is { } ack:
                // Health reports never complete a requested privacy/state transition.
                // Only the capture thread's corresponding acknowledgement can do that.
                bool systemPause = !isTrial && ack.RequestId == "system-gap" && ack.Operation == "pause" && ack.Paused;
                if (state is not (SessionState.Recording or SessionState.Paused) ||
                    (!systemPause && (pendingControl != ack.Operation || pendingControlRequestId != ack.RequestId))) break;
                if (!systemPause) { pendingControl = null; pendingControlRequestId = null; }
                if (ack.Operation == "mic_on" && ack.MicrophoneEnabled && pendingMicrophone is { } enabledDevice)
                {
                    fixedMicrophoneId = enabledDevice.Id;
                    fixedMicrophoneName = enabledDevice.Name;
                    pendingMicrophone = null;
                }
                microphoneEnabled = ack.MicrophoneEnabled;
                SetMicrophoneCheckbox(microphoneEnabled);
                state = ack.Paused ? SessionState.Paused : SessionState.Recording;
                elapsedUs = Math.Max(elapsedUs, ack.EffectiveSessionUs);
                if (ack.Paused)
                {
                    RemoteLevel.Value = 0; MicLevel.Value = 0;
                    RemoteHealth.Text = "暂停 · 不采集";
                    MicHealth.Text = microphoneEnabled ? "暂停 · 不采集，恢复后开启" : "关闭 · 不采集";
                }
                if (!ack.MicrophoneEnabled) { MicLevel.Value = 0; MicHealth.Text = "关闭 · 不采集"; }
                SetStatus(systemPause ? "检测到系统/调度时间缺口；CaptureHost 已确认暂停，两路均已停采。可明确恢复录音，或停止并保存。" :
                    ack.Message ?? $"已确认{ControlDescription(ack.Operation)}，生效时间 {FormatElapsed(ack.EffectiveSessionUs)}。");
                break;
            case "control_error":
                if (pendingControlRequestId != value.RequestId || pendingControl is null ||
                    state is not (SessionState.Recording or SessionState.Paused)) break;
                var rejectedOperation = pendingControl;
                pendingControl = null;
                pendingControlRequestId = null;
                pendingMicrophone = null;
                SetStatus($"{ControlDescription(rejectedOperation)}未完成：{value.Message}\n当前开关和录音状态按上次实际回执显示；可重试或停止并保存。");
                break;
            case "levels" when value.Levels is { } levels:
                RemoteLevel.Value = levels.RemotePeak;
                MicLevel.Value = levels.MicrophonePeak;
                RemoteHealth.Text = levels.RemoteFrames == 0 ? "尚未收到音频包" : $"{levels.RemoteFrames:N0} 帧 · 峰值 {levels.RemotePeak:F3}";
                MicHealth.Text = microphoneEnabled ? $"{levels.MicrophoneFrames:N0} 帧 · 峰值 {levels.MicrophonePeak:F3}" : "关闭 · 不采集";
                if (!string.IsNullOrEmpty(levels.Status)) SetStatus(levels.Status);
                break;
            case "finalizing":
                state = SessionState.Stopping;
                starting = false;
                StorageText.Text = "保存状态：正在排空并收尾，尚未确认保存";
                SetStatus(value.Message ?? "正在保存");
                break;
            case "recording_saved":
            case "recording_incomplete":
                mixedPath = null;
                if (!exitConfirmed || value.RecordingResult is not { } recordingResult)
                {
                    state = SessionState.Faulted;
                    StorageText.Text = "保存状态：缺少持久结果或 Host 退出确认";
                    SetStatus("未确认完整保存；请保留原件。" + value.Message);
                }
                else
                {
                    elapsedUs = recordingResult.DurationUs;
                    bool saved = value.Kind == "recording_saved" && recordingResult.StorageComplete;
                    state = saved ? SessionState.Saved : SessionState.SavedIncomplete;
                    StorageText.Text = saved ? "保存状态：原轨及索引已收尾" : "保存状态：原件已保留，完整性有警告";
                    SetStatus($"{(saved ? "录音原轨已保存。" : "录音原件已保留，不能称作完整录音。")}" +
                        $"\n目录：{recordingResult.SessionDirectory}\n" +
                        string.Join(Environment.NewLine, recordingResult.Warnings) +
                        "\n原轨保存结束；后台混音不阻止开始下一场。");
                    SelectMeeting(recordingResult.SessionDirectory, loadSavedMix: false);
                    RefreshRecovery_Click(this, new RoutedEventArgs());
                    if (!closeAfterSave) QueueMix(recordingResult.SessionDirectory);
                }
                ConnectionText.Text = "捕获连接：CaptureHost 已退出";
                ResetConsentAfterSession();
                break;
            case "saved":
                state = SessionState.Saved;
                mixedPath = value.MixedPath;
                ConnectionText.Text = "捕获连接：CaptureHost 已退出";
                StorageText.Text = "保存状态：短试录原轨已收尾";
                SetStatus($"短试录原轨已收尾。{(mixedPath is null ? "本次无可回放混音。" : "混合 WAV 已生成，可回放。")}" +
                    $"\n目录：{value.Result?.SessionDirectory}\n{value.Message}\nM0 样机输出；末帧、恢复、平台兼容仍需验收。");
                ResetConsentAfterSession();
                break;
            case "incomplete":
                mixedPath = null;
                ConnectionText.Text = "捕获连接：CaptureHost 已退出";
                StorageText.Text = "保存状态：短试录原轨已收尾，完整性有警告";
                if (exitConfirmed && value.Result is { Complete: false } incomplete)
                {
                    try
                    {
                        SetStatus("CaptureHost 已退出；正在检查原轨并生成有警告的试听混音。完整性状态仍未通过。");
                        var preview = await Task.Run(() => RenderIncompletePreview(incomplete));
                        // An old asynchronous result must never enable playback in a later session.
                        if (!ReferenceEquals(host, eventHost) || state != SessionState.Stopping) return;
                        mixedPath = preview.Path;
                        state = SessionState.SavedIncomplete;
                        var warnings = incomplete.Warnings.Concat(preview.Warnings)
                            .Concat(string.IsNullOrWhiteSpace(value.Message) ? [] : new[] { value.Message! })
                            .Distinct();
                        SetStatus($"原轨已收尾；录音完整性未通过。可试听基于有效 QPC 的降级混音。\n目录：{incomplete.SessionDirectory}\n" +
                            string.Join(Environment.NewLine, warnings) + "\n试听不代表时钟、末帧或平台验收通过。");
                    }
                    catch (Exception ex)
                    {
                        if (!ReferenceEquals(host, eventHost) || state != SessionState.Stopping) return;
                        state = SessionState.Faulted;
                        SetStatus($"原轨保留；未确认完整录音，无法生成试听混音：{ex.Message}\n{value.Message}\n{incomplete.SessionDirectory}");
                    }
                }
                else
                {
                    state = SessionState.Faulted;
                    SetStatus($"未确认完整保存：{value.Message}\n原件保留，未自动更换音源。{value.Result?.SessionDirectory}");
                }
                ResetConsentAfterSession();
                break;
            case "error":
                state = SessionState.Faulted;
                ConnectionText.Text = "捕获连接：CaptureHost 已退出";
                StorageText.Text = "保存状态：未确认完整保存，原件保留";
                SetStatus($"未确认完整保存：{value.Message}\n原件保留，未自动更换音源。{value.Result?.SessionDirectory}");
                ResetConsentAfterSession();
                break;
        }
        UpdateControls();
        if (closeAfterSave && SessionRules.CanStart(state)) Close();
    }

    private static RenderResult RenderIncompletePreview(CaptureResult result)
    {
        if (!result.Tracks.Any(track => track.FrameCount > 0 && track.Packets.Any(packet => packet.FrameCount > 0)))
            throw new InvalidDataException("没有有效音频包，不能把无包静音称作试听结果。");
        foreach (var track in result.Tracks)
        {
            var expected = Path.GetFullPath(Path.Combine(result.SessionDirectory, "sources", track.SourceId + ".wav"));
            if (track.SourceId is not ("remote" or "mic") ||
                !string.Equals(Path.GetFullPath(track.WavePath), expected, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(expected) || File.Exists(expected + ".partial"))
                throw new InvalidDataException("原轨未确认收尾为本次 WAV 文件，不能生成试听混音。");
            if (track.Packets.Any(packet => !packet.TimestampValid || (packet.Flags & 4) != 0 || packet.QpcPosition100ns < result.EpochQpc100ns))
                throw new InvalidDataException("存在无效 QPC，不能生成按时间对齐的试听混音。");
        }
        var destination = Path.Combine(result.SessionDirectory, "mixed-preview-incomplete-" + Guid.NewGuid().ToString("N") + ".wav");
        // The renderer verifies finalized WAV lengths, packet storage offsets and nonoverlapping
        // QPC ranges. It preserves Complete=false and warnings and never overwrites originals.
        return TimelineWaveRenderer.Render(result, destination);
    }

    private void ResetConsentAfterSession()
    {
        starting = false;
        launching = false;
        pendingControl = null;
        pendingControlRequestId = null;
        microphoneEnabled = false;
        fixedMicrophoneId = null;
        fixedMicrophoneName = null;
        pendingMicrophone = null;
        SetMicrophoneCheckbox(false);
        EndpointConsent.IsChecked = false;
        SourceBox.SelectedIndex = -1; // A completed meeting never reuses an old PID implicitly.
        MicBox.SelectedIndex = -1;
        RemoteLevel.Value = 0;
        MicLevel.Value = 0;
        TitleBox.Text = SessionDefaults.Title(DateTimeOffset.Now);
    }

    private void SetMicrophoneCheckbox(bool enabled)
    {
        updatingMicrophoneCheck = true;
        try { MicEnabled.IsChecked = enabled; }
        finally { updatingMicrophoneCheck = false; }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e) =>
        await RequestControlAsync(state == SessionState.Paused ? "resume" : "pause");

    private async void Bookmark_Click(object sender, RoutedEventArgs e) => await RequestControlAsync("bookmark");

    private async void MicControl_Click(object sender, RoutedEventArgs e)
    {
        if (!MicControlButton.IsEnabled) return;
        if (microphoneEnabled) { await RequestControlAsync("mic_off"); return; }
        var currentHost = host;
        choosingMicrophone = true; UpdateControls();
        try
        {
            AudioDevice? chosen;
            if (fixedMicrophoneId is null)
            {
                // Metadata enumeration only. Selecting a row does not open an audio client.
                var devices = await Task.Run(() => SourceDiscovery.Enumerate().Microphones);
                if (WindowCloseRequested || state != SessionState.Recording || !ReferenceEquals(host, currentHost)) return;
                chosen = PickRecordingMicrophone(devices);
            }
            else
            {
                chosen = MessageBox.Show(this,
                    $"本场将开启固定麦克风：{fixedMicrophoneName}\n与 Zoom/Webex 静音独立，可能录下私下声音、其他人和回声。确认开启？",
                    "独立麦克风同意", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes
                    ? new AudioDevice(fixedMicrophoneId, fixedMicrophoneName ?? "本场固定麦克风") : null;
            }
            if (chosen is null || WindowCloseRequested || state != SessionState.Recording || !ReferenceEquals(host, currentHost)) return;
            pendingMicrophone = chosen;
            await RequestControlAsync("mic_on");
        }
        catch (Exception ex) { SetStatus("麦克风未开启：" + ex.Message); }
        finally { choosingMicrophone = false; UpdateControls(); }
    }

    private static string ControlDescription(string command) => command switch
    {
        "pause" => "暂停", "resume" => "恢复录音", "mic_off" => "关闭麦克风",
        "mic_on" => "开启麦克风", "bookmark" => "标记此刻", _ => command
    };

    private async Task RequestControlAsync(string command)
    {
        if (isTrial || starting || host is null || pendingControl is not null ||
            state is not (SessionState.Recording or SessionState.Paused)) return;
        pendingControl = command;
        pendingControlRequestId = Guid.NewGuid().ToString("N");
        UpdateControls();
        SetStatus($"正在请求{ControlDescription(command)}；等待实际采集边界确认。");
        try { await host.ControlAsync(command, command == "mic_on" ? pendingMicrophone?.Id ?? fixedMicrophoneId : null, pendingControlRequestId); }
        catch (Exception ex)
        {
            // Never turn a failed send into a successful privacy acknowledgement.
            SetStatus($"{ControlDescription(command)}请求未确认：{ex.Message}\n状态尚未改变；可停止整场录音。请等待 Host 停采/保存结果。");
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (state is not (SessionState.Recording or SessionState.Paused)) return;
        stopRequested = true;
        state = SessionState.Stopping;
        UpdateControls();
        StorageText.Text = "保存状态：正在停止并排空，尚未确认保存";
        SetStatus("正在停止并保存；收到 CaptureHost 收尾确认前不会显示已保存。");
        if (launching || host is null) return;
        try { await host.StopAsync(); }
        catch (Exception ex) { SetStatus("停止请求通信失败；Host 失联保护将停采，保存状态未知：" + ex.Message); }
    }

    private void Playback_Click(object sender, RoutedEventArgs e)
    {
        if (WindowCloseRequested || !SessionRules.CanPlayback(state) || mixedPath is null) return;
        try
        {
            RejectReparseAncestors(mixedPath);
            OpenListeningPosition(mixedPath, 0);
            if (currentMix?.CompressedPath == mixedPath)
                SetStatus("正在回听已校验的完整 M4A 副本；无损大 WAV 保留，可另行导出。");
        }
        catch (Exception ex) { player.Close(); SetStatus("回放未启动：" + ex.Message); }
    }

    private void StopPlayback_Click(object sender, RoutedEventArgs e) => ResetTranscriptPlayback();

    private string RecordingsRoot => Path.GetFullPath(Path.Combine(dataRoot, "M1Recordings"));

    private sealed record RecordingDirectory(string Path, string Name, DateTimeOffset? Date = null, bool Imported = false)
    {
        public string DateLabel => Date is { } value
            ? (Imported ? "导入于 " : "") + value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "日期待检查";
    }

    private static RecordingDirectory ReadMeetingListEntry(string directory)
    {
        try
        {
            var filing = ReadFiling(directory);
            string path = Path.Combine(directory, "meeting.json"); RejectReparseAncestors(path);
            if (new FileInfo(path).Length > 16_000_000) throw new InvalidDataException("会议信息超过读取上限。");
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            bool imported = root.TryGetProperty("importedAt", out var importedAt);
            DateTimeOffset? date = (imported ? importedAt : root.TryGetProperty("startedAtUtc", out var started) ? started : default)
                is { ValueKind: System.Text.Json.JsonValueKind.String } dateNode && dateNode.TryGetDateTimeOffset(out var value) ? value : null;
            return new(directory, string.IsNullOrWhiteSpace(filing.Title) ? "未命名会议" : filing.Title, date, imported);
        }
        catch { return new(directory, "信息待检查 · " + Path.GetFileName(directory)); }
    }

    private static void RejectReparseAncestors(string path)
    {
        for (string? ancestor = Path.GetFullPath(path); !string.IsNullOrEmpty(ancestor); ancestor = Path.GetDirectoryName(ancestor))
            if ((Directory.Exists(ancestor) || File.Exists(ancestor)) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("拒绝重解析点路径；仅操作所选数据目录中的实际文件。");
    }

    private string ValidateRecordingDirectory(string directory, bool mustExist = true)
    {
        string full = Path.GetFullPath(directory);
        string? parent = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.Equals(parent, RecordingsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("只能选择当前数据目录 M1Recordings 下的直接录音目录。");
        RejectReparseAncestors(full);
        if (mustExist && !Directory.Exists(full)) throw new DirectoryNotFoundException("所选录音目录已不存在。");
        return full;
    }

    private void OpenRecordingFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RejectReparseAncestors(RecordingsRoot);
            Directory.CreateDirectory(RecordingsRoot);
            RejectReparseAncestors(RecordingsRoot);
            Process.Start(new ProcessStartInfo(selectedMeetingDirectory is { } selected ? ValidateRecordingDirectory(selected) : RecordingsRoot) { UseShellExecute = true });
        }
        catch (Exception ex) { RecoveryStatus.Text = "未能打开录音目录：" + ex.Message; }
    }

    private void RefreshRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (!SessionRules.CanStart(state) || recoveryBusy) return;
        try
        {
            RejectReparseAncestors(RecordingsRoot);
            var directories = Directory.Exists(RecordingsRoot)
                ? Directory.EnumerateDirectories(RecordingsRoot, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
                        !Path.GetFileName(path).StartsWith(".pending-", StringComparison.Ordinal))
                    .Select(ReadMeetingListEntry).OrderByDescending(item => item.Date).ThenBy(item => item.Name, StringComparer.Ordinal).Take(500).ToArray()
                : [];
            var previouslySelected = selectedMeetingDirectory;
            RecoveryBox.ItemsSource = directories;
            RecoveryBox.SelectedItem = directories.FirstOrDefault(item => string.Equals(item.Path, previouslySelected, StringComparison.OrdinalIgnoreCase));
            libraryRead = true;
            MeetingCountText.Text = $"{directories.Length} 场会议 · 日期倒序";
            RecoveryStatus.Text = $"已列出 {directories.Length} 场（最多 500）；未读取音频或索引。请选择要检查的一场。";
        }
        catch (Exception ex) { RecoveryStatus.Text = "未能列出录音目录：" + ex.Message; }
        UpdateControls();
    }

    private void RecoverySelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && !synchronizingMeetingSelection && SessionRules.CanStart(state) && RecoveryBox.SelectedItem is RecordingDirectory selected)
        {
            SelectMeeting(selected.Path);
            NavigateTo(1);
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
        RecoveryStatus.Text = createCopy ? "正在校验所选原件并创建恢复副本…" : "正在只读校验所选录音的索引、检查点和原轨哈希…";
        try
        {
            string source = ValidateRecordingDirectory(selected);
            if (AudioImportService.IsImportedMeeting(source))
            {
                if (createCopy) throw new InvalidOperationException("外部导入录音没有采集 journal；不能执行采集恢复。原件副本已保留。");
                var imported = await Task.Run(() => AudioImportService.Load(source));
                RecoveryStatus.Text = $"外部录音原件副本、回放哈希与完整解码帧数通过：{imported.Mix.DurationUs / 1_000_000d:F3} 秒。\n外部录音采集时钟和原始缺口未知。";
                return;
            }
            if (createCopy)
            {
                string destination = ValidateRecordingDirectory(Path.Combine(RecordingsRoot,
                    "recovered-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")), mustExist: false);
                var result = await Task.Run(() => M1Recovery.RecoverTo(source, destination));
                RecoveryStatus.Text = $"已创建恢复副本：{result.SessionDirectory}\n原件未修改；只包含通过检查点和哈希校验的 PCM。未确认尾部不在副本中，恢复不等于录音完整。\n" +
                    string.Join(Environment.NewLine, result.Warnings);
            }
            else
            {
                var result = await Task.Run(() => M1Recovery.Read(source));
                RecoveryStatus.Text = $"{(result.IsCleanEnd ? "磁盘索引与保存结果校验通过。" : "保存结果存在缺口或警告。")}" +
                    $"\n已确认检查点：{(result.HasCheckpoint ? result.LastCheckpointSequence.ToString() : "无")}；可读取原轨块：{result.Capture.Tracks.Count}。" +
                    "\n这项检查不验证来源隔离、实际听感或双设备长期同步。\n" + string.Join(Environment.NewLine, result.Issues);
            }
        }
        catch (Exception ex) { RecoveryStatus.Text = "所选录音检查/恢复失败；原件保留：" + ex.Message; }
        finally { recoveryBusy = false; UpdateControls(); }
    }

    private void Browser_Click(object sender, RoutedEventArgs e)
    {
        try { SetStatus(DedicatedBrowserLauncher.LaunchChrome(projectRoot, dataRoot).Message); }
        catch (Exception ex) { SetStatus("专用浏览器未启动：" + ex.Message); }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closeApproved) return;
        // Cancel this close synchronously. The final Close is posted only after all
        // file work has unwound, never while this Closing event is on the stack.
        e.Cancel = true;
        if (windowCloseWork is not null) return;
        if (!SessionRules.CanStart(state))
        {
            if (state == SessionState.Stopping)
            {
                closeAfterSave = true;
                CancelBackgroundForClose();
                UpdateControls();
                return;
            }
            if (MessageBox.Show(this, "录音会话尚未保存。是否停止并保存后关闭窗口？\n选择“否”将保持窗口和当前录音状态。",
                    "关闭录音窗口", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
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
            player.Close();
            await backgroundWork;
            await transcriptWork;
            await savedMixWork;
            await replayLoadWork;
            // Recovery has no destructive cancellation API; let its current copy
            // finish safely rather than terminate it midway through an output file.
            await recoveryWork;
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
            SetStatus("关闭收尾未确认，窗口保持打开：" + ex.Message);
            UpdateControls();
        }
    }

    internal async Task<object> NonCaptureSmokeAsync()
    {
        while (refreshing) await Task.Delay(50);
        if (snapshot is null) await RefreshSourcesAsync();
        if (MicEnabled.IsChecked == true || state != SessionState.Idle || SourceBox.SelectedIndex != -1 || host is not null)
            throw new InvalidOperationException("默认隐私状态不符。");
        VerifyNonCaptureControlStates();
        return new { Test = "WPF startup + real enumeration, NO capture", OsBuild = Environment.OSVersion.Version.ToString(),
            MicDefaultOff = true, NoCaptureHostStarted = true, NoSourceAutomaticallySelected = true,
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
                throw new InvalidOperationException("初始未选麦克风时没有录音中开启入口，或误放开回听。");
            pendingMicrophone = new AudioDevice("synthetic-first-mic", "合成设备（未采集）");
            pendingControl = "mic_on"; pendingControlRequestId = "first-mic-on";
            HandleHostEvent(new(1, "synthetic-ui-only", "stale-mic-on", "ack",
                Ack: new("stale-mic-on", "mic_on", false, true, 0)));
            if (fixedMicrophoneId is not null || microphoneEnabled)
                throw new InvalidOperationException("过期开启回执提前锁定麦克风。");
            HandleHostEvent(new(1, "synthetic-ui-only", "first-mic-on", "control_error", "synthetic open failure"));
            if (fixedMicrophoneId is not null || pendingMicrophone is not null || microphoneEnabled)
                throw new InvalidOperationException("失败开启请求锁定设备或错误显示开启。");
            pendingMicrophone = new AudioDevice("synthetic-first-mic", "合成设备（未采集）");
            pendingControl = "mic_on"; pendingControlRequestId = "retry-mic-on";
            HandleHostEvent(new(1, "synthetic-ui-only", "retry-mic-on", "ack",
                Ack: new("retry-mic-on", "mic_on", false, true, 0)));
            if (fixedMicrophoneId != "synthetic-first-mic" || !microphoneEnabled || pendingMicrophone is not null)
                throw new InvalidOperationException("成功开启回执未固定明确选择的麦克风。");
            microphoneEnabled = true;
            fixedMicrophoneId = "synthetic-ui-only";
            pendingControl = "mic_off";
            pendingControlRequestId = "current-ui-request";
            UpdateControls();
            if (!microphoneEnabled || !ScopeText.Text.Contains("等待采集边界确认") || PlaybackButton.IsEnabled || SelectionPanel.IsEnabled)
                throw new InvalidOperationException("MicOff 未确认前 UI 隐私状态不符。");
            HandleHostEvent(new(1, "synthetic-ui-only", "old-ui-request", "ack",
                Ack: new("old-ui-request", "mic_off", false, false, 0)));
            if (!microphoneEnabled || pendingControl != "mic_off")
                throw new InvalidOperationException("过期 MicOff 回执改变了 UI 状态。");
            HandleHostEvent(new(1, "synthetic-ui-only", "current-ui-request", "ack",
                Ack: new("current-ui-request", "mic_off", false, false, 0)));
            if (microphoneEnabled || pendingControl is not null)
                throw new InvalidOperationException("匹配 MicOff 回执未确认 UI 状态。");
            microphoneEnabled = true;
            pendingControl = "mic_off";
            pendingControlRequestId = "rejected-ui-request";
            HandleHostEvent(new(1, "synthetic-ui-only", "old-ui-request", "control_error", "synthetic rejection"));
            if (pendingControl != "mic_off") throw new InvalidOperationException("旧控制失败回执清除了当前请求。");
            HandleHostEvent(new(1, "synthetic-ui-only", "rejected-ui-request", "control_error", "synthetic rejection"));
            if (!microphoneEnabled || pendingControl is not null || state != SessionState.Recording)
                throw new InvalidOperationException("控制失败回执改变了实际 mic/录音状态。");
            HandleHostEvent(new(1, "synthetic-ui-only", "system-gap", "ack",
                Ack: new("system-gap", "pause", true, true, 0)));
            if (state != SessionState.Paused || !microphoneEnabled || !PauseButton.IsEnabled ||
                !ScopeText.Text.Contains("暂停，不采集"))
                throw new InvalidOperationException("系统缺口暂停未正确显示停采与恢复入口。");
            state = SessionState.Recording;
            pendingControl = "pause";
            UpdateControls();
            if (state != SessionState.Recording || !SessionStateText.Text.Contains("等待采集边界确认") || PauseButton.IsEnabled)
                throw new InvalidOperationException("Pause 未确认前 UI 状态不符。");
            pendingControl = null;
            state = SessionState.Paused;
            UpdateControls();
            if (PlaybackButton.IsEnabled || StopPlaybackButton.IsEnabled || SelectionPanel.IsEnabled || !StopButton.IsEnabled)
                throw new InvalidOperationException("Paused 回放锁定或停止入口不符。");
            state = SessionState.Stopping;
            UpdateControls();
            if (PlaybackButton.IsEnabled || StartButton.IsEnabled || TrialButton.IsEnabled)
                throw new InvalidOperationException("Stopping 尚未保存时操作未锁定。");
            state = SessionState.Saved;
            selectedMeetingDirectory = Path.Combine(RecordingsRoot, "synthetic-new-visible-meeting");
            RecoveryBox.ItemsSource = new[] { new RecordingDirectory(Path.Combine(RecordingsRoot, "synthetic-old-picked-meeting"), "旧合成会议") };
            // Avoid SelectionChanged reading a fixture or any historical directory.
            initialized = false;
            RecoveryBox.SelectedIndex = 0;
            initialized = true;
            UpdateControls();
            if (!FileMeetingButton.IsEnabled)
                throw new InvalidOperationException("可见的新保存会议没有归档入口。");
            selectedMeetingDirectory = null;
            UpdateControls();
            if (FileMeetingButton.IsEnabled)
                throw new InvalidOperationException("旧历史选择在没有可见会议时启用了归档，可能错写旧会议。");
            exportBusy = true;
            UpdateControls();
            if (DataRootButton.IsEnabled || RetryMixButton.IsEnabled || ExportButton.IsEnabled || !CancelExportButton.IsEnabled || !StartButton.IsEnabled)
                throw new InvalidOperationException("后台任务互斥或新录音入口状态不符。");
            exportBusy = false;
            var nested = Path.Combine(RecordingsRoot, "one", "nested");
            var outside = Path.Combine(projectRoot, ".local", "not-a-recording");
            foreach (var forbidden in new[] { nested, outside })
            {
                bool rejected = false;
                try { ValidateRecordingDirectory(forbidden, mustExist: false); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("恢复入口接受了项目录音根目录以外的路径。");
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
            SetMicrophoneCheckbox(false);
            fixedMicrophoneId = null;
            fixedMicrophoneName = null;
            pendingMicrophone = null;
            pendingControl = null;
            pendingControlRequestId = null;
            UpdateControls();
        }
    }
}
