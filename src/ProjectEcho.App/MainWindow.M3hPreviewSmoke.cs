using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

namespace ProjectEcho.App;

public partial class MainWindow
{
    // Actual UI lifecycle with injected IAsyncDisposable owners. Every identity,
    // peak and release boundary below is synthetic; the native factory is never called.
    internal async Task<object> M3hPreviewSmokeAsync(string evidence, CancellationToken token)
    {
        Need(!levelPreviewNativeEnabled && levelPreview is null && host is null && state == SessionState.Idle,
            "Preview smoke requires the native-disabled constructor.");
        dataRoot = Path.Combine(evidence, "synthetic-preview-data"); Directory.CreateDirectory(dataRoot);
        DataRootText.Text = dataRoot; ChangeLanguage("zh-CN", persist: false); NavigateTo(0);
        var workers = new List<M3hFakeLevelPreview>();
        int active = 0, maximumActive = 0, preflights = 0;
        bool preflightObservedReleasedOwner = false;
        var smoothSamples = new List<object>();
        var endpointA = new AudioDevice("synthetic-endpoint-a", "Synthetic output A");
        var endpointB = new AudioDevice("synthetic-endpoint-b", "Synthetic output B");
        var endpointC = new AudioDevice("synthetic-endpoint-c", "Synthetic output C");
        var microphone = new AudioDevice("synthetic-microphone", "Synthetic microphone");
        levelPreviewFactory = (selection, changed) =>
        {
            active++; maximumActive = Math.Max(maximumActive, active);
            Need(active == 1, "A replacement preview was opened before the previous owner released.");
            var worker = new M3hFakeLevelPreview(selection, changed, () => active--);
            workers.Add(worker); return worker;
        };
        recordingPreflight = _ =>
        {
            Interlocked.Increment(ref preflights);
            preflightObservedReleasedOwner = Volatile.Read(ref active) == 0 && levelPreview is null;
            return new(false, ["Synthetic preflight rejection; never open CaptureHost."], [], 0, 1_000_000, "synthetic");
        };
        ModeBox.SelectedIndex = 1; EndpointBox.ItemsSource = new[] { endpointA, endpointB, endpointC };
        MicBox.ItemsSource = new[] { microphone }; MicBox.SelectedItem = microphone; EndpointBox.SelectedItem = endpointA;
        UpdateControls(); await SettleAsync();
        Need(workers.Count == 0 && !microphoneRequested && levelPreview is null,
            "Device selection in a native-disabled smoke started preview or implied mic consent.");

        levelPreviewNativeEnabled = true; UpdateControls(); await SettleAsync();
        var remoteOnly = workers[^1];
        Need(remoteOnly.Selection.RenderDeviceId == endpointA.Id && remoteOnly.Selection.MicrophoneDeviceId is null,
            "Remote selection included an unconsented microphone.");
        ResetLevelMeterDisplay();
        var attack = await ObserveFramesAsync(() => remoteOnly.Publish(0.4f, 0.9f, microphoneStarted: false),
            () => Math.Abs(remoteLevelTargetRawPeak - 0.4) < 0.001);
        Need(Math.Abs(remoteLevelTargetRawPeak - 0.4) < 0.001 && microphoneLevelTargetRawPeak == 0 && MicLevel.Value == 0 &&
            RemoteHealth.Text.Contains("0 帧", StringComparison.Ordinal) && !microphoneRequested,
            "Remote preview failed to respond before recording, persisted a mic peak while off, or claimed recorded frames.");
        Need(attack.Remote.All(value => value is > 0 and < 1) && attack.Remote[^1] > attack.Remote[0] &&
            attack.Remote.Select(value => Math.Round(value, 4)).Distinct().Count() >= 3,
            "Actual WPF render frames did not show multiple continuous attack values.");
        var release = await ObserveFramesAsync(() => remoteOnly.Publish(0, 0, microphoneStarted: false),
            () => remoteLevelTargetRawPeak == 0);
        Need(release.Remote.All(value => value is > 0 and < 1) && release.Remote[^1] < release.Remote[0] &&
            release.Remote.Select(value => Math.Round(value, 4)).Distinct().Count() >= 3,
            "Actual WPF render frames did not show a smooth release after the raw peak dropped to zero.");
        smoothSamples.Add(new { Phase = "remote-attack", Values = attack.Remote });
        smoothSamples.Add(new { Phase = "remote-release", Values = release.Remote });
        foreach (var meter in new[] { RemoteLevel, MicLevel })
        {
            meter.ApplyTemplate();
            Need(ReferenceEquals(meter.Style, FindResource("ContinuousAudioMeter")) &&
                meter.Template.FindName("PART_Indicator", meter) is Border { Background: SolidColorBrush, Child: null },
                "Audio level display is not the dedicated continuous solid-fill ProgressBar template.");
        }

        MicControlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await SettleAsync();
        var micOwner = workers[^1];
        Need(micOwner.Selection.MicrophoneDeviceId == microphone.Id && remoteOnly.Released,
            "Explicit microphone enabling did not replace/release the remote preview safely.");
        var micAttack = await ObserveFramesAsync(() => micOwner.Publish(0.35f, 0.6f, microphoneStarted: true),
            () => Math.Abs(microphoneLevelTargetRawPeak - 0.6) < 0.001);
        Need(microphoneRequested && microphonePreviewActive && !microphoneEnabled && MicIconBody.IsVisible &&
            Math.Abs(microphoneLevelTargetRawPeak - 0.6) < 0.001 && MicLevel.Value is > 0 and < 1 &&
            MicHealth.Text.Contains("0 帧", StringComparison.Ordinal),
            "Explicit mic preview did not show a live peak without claiming a recording track.");
        Need(micAttack.Microphone[^1] > micAttack.Microphone[0] &&
            micAttack.Microphone.Select(value => Math.Round(value, 4)).Distinct().Count() >= 3,
            "The microphone display did not advance through intermediate render-frame levels.");
        smoothSamples.Add(new { Phase = "microphone-attack", Values = micAttack.Microphone });

        micOwner.BlockRelease();
        MicControlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await micOwner.ReleaseEntered.Task.WaitAsync(token); await PumpAsync();
        Need(microphonePreviewStopping && microphonePreviewActive && !microphoneRequested && !MicOffMark.IsVisible &&
            !MicControlButton.IsEnabled && !StartButton.IsEnabled &&
            MicStateText.Text.Contains("等待采集边界确认", StringComparison.Ordinal) &&
            ScopeText.Text.Contains("等待采集边界确认", StringComparison.Ordinal),
            "MicOff claims collection stopped, or permits capture, before the slow preview release boundary.");
        double pendingRemote = remoteLevelTargetRawPeak, pendingMic = microphoneLevelTargetRawPeak;
        micOwner.Publish(0.99f, 0.99f, microphoneStarted: true); await PumpAsync();
        Need(remoteLevelTargetRawPeak == pendingRemote && microphoneLevelTargetRawPeak == pendingMic,
            "A queued callback from the pre-MicOff generation overwrote the pending UI.");
        await StartCaptureAsync(trial: false);
        Need(preflights == 0 && host is null, "Start bypassed an outstanding microphone preview stop.");
        micOwner.AllowRelease(); await SettleAsync();
        Need(micOwner.Released && !microphonePreviewStopping && !microphonePreviewActive && MicOffMark.IsVisible &&
            workers[^1].Selection.MicrophoneDeviceId is null && microphoneLevelTargetRawPeak == 0 && MicLevel.Value == 0,
            "MicOff did not finish at release or left a microphone in the replacement preview.");

        // The native mic may have been created even before its first Started sample
        // reaches the Dispatcher. Quick On/Off must still wait for actual owner release.
        MicControlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await SettleAsync();
        var unreportedMicOwner = workers[^1];
        Need(unreportedMicOwner.Selection.MicrophoneDeviceId == microphone.Id && microphoneRequested &&
            !microphonePreviewActive && MicIconBody.IsVisible,
            "Quick-toggle fixture unexpectedly received a microphone-started callback.");
        unreportedMicOwner.BlockRelease();
        MicControlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await unreportedMicOwner.ReleaseEntered.Task.WaitAsync(token); await PumpAsync();
        Need(microphonePreviewStopping && !microphonePreviewActive && !microphoneRequested &&
            MicIconBody.IsVisible && !MicOffMark.IsVisible && !StartButton.IsEnabled &&
            MicStateText.Text.Contains("等待采集边界确认", StringComparison.Ordinal) &&
            ScopeText.Text.Contains("等待采集边界确认", StringComparison.Ordinal),
            "Quick On/Off before the first Started callback displayed OFF before release.");
        unreportedMicOwner.Publish(0.99f, 0.99f, microphoneStarted: true); await PumpAsync();
        Need(!microphonePreviewActive && microphoneLevelTargetRawPeak == 0,
            "A late Started callback reactivated a microphone whose quick shutdown is pending.");
        unreportedMicOwner.AllowRelease(); await SettleAsync();
        Need(unreportedMicOwner.Released && !microphonePreviewStopping && MicOffMark.IsVisible &&
            microphoneLevelTargetRawPeak == 0 && MicLevel.Value == 0,
            "Quick On/Off did not finish cleanly after the owner finally released.");

        var beforeRapidSelection = workers[^1]; beforeRapidSelection.BlockRelease();
        int beforeRapidCount = workers.Count;
        EndpointBox.SelectedItem = endpointB; EndpointBox.SelectedItem = endpointC;
        await beforeRapidSelection.ReleaseEntered.Task.WaitAsync(token); await PumpAsync();
        Need(workers.Count == beforeRapidCount && active == 1,
            "Fast source changes opened a second preview while the previous owner remained active.");
        beforeRapidSelection.Publish(0.99f, 0, microphoneStarted: false); await PumpAsync();
        Need(remoteLevelTargetRawPeak != 0.99f, "The old source callback survived a newer source selection.");
        beforeRapidSelection.AllowRelease(); await SettleAsync();
        Need(workers.Count == beforeRapidCount + 1 && workers[^1].Selection.RenderDeviceId == endpointC.Id && maximumActive == 1,
            "Fast source changes did not coalesce to the last source after one serialized release.");
        var selectedC = workers[^1]; selectedC.Publish(0.28f, 0, microphoneStarted: false); await PumpAsync();
        Need(Math.Abs(remoteLevelTargetRawPeak - 0.28) < 0.001, "The latest source no longer updates the preview's raw target.");

        selectedC.BlockRelease();
        Task startAttempt = StartCaptureAsync(trial: false);
        await selectedC.ReleaseEntered.Task.WaitAsync(token); await PumpAsync();
        Need(preparingCapturePreviewStop && !StartButton.IsEnabled && preflights == 0 && host is null &&
            state == SessionState.Idle && !startAttempt.IsCompleted,
            "Start reached preflight or a Host before the preview owner released.");
        selectedC.AllowRelease(); await startAttempt.WaitAsync(token); await SettleAsync();
        Need(preflights == 1 && preflightObservedReleasedOwner && host is null && state == SessionState.Idle,
            "Preflight did not follow the preview release, or synthetic rejection started recording.");

        // State changes must stop preview and late meter events cannot overwrite the
        // recording health messages. No actual Host is constructed for these states.
        var beforeRecording = workers[^1]; state = SessionState.Recording; UpdateControls(); await SettleAsync();
        Need(levelPreview is null && active == 0 && DesiredLevelPreview() is null, "Recording retained a pre-recording preview stream.");
        HandleHostEvent(new(1, "synthetic-m3h", "health", "health", Health: new(
            3_000_000, false, false, 0.32f, 0, 1_234, 0, "已观察到声音", "关闭", 0, 1_000_000_000)));
        string recordingHealth = RemoteHealth.Text; double recordingPeak = remoteLevelTargetRawPeak;
        beforeRecording.Publish(0.99f, 0.99f, microphoneStarted: true); await PumpAsync();
        Need(RemoteHealth.Text == recordingHealth && remoteLevelTargetRawPeak == recordingPeak && elapsedUs == 3_000_000,
            "A late preview callback replaced the recording health/counter state.");
        state = SessionState.Paused; UpdateControls(); await SettleAsync();
        Need(levelPreview is null && DesiredLevelPreview() is null && active == 0,
            "Paused recording opened a pre-recording preview stream.");
        int pausedCount = workers.Count; MicBox.SelectedIndex = -1; UpdateControls(); await PumpAsync();
        Need(workers.Count == pausedCount, "Changing metadata while paused started preview.");

        state = SessionState.Idle; NavigateTo(4); UpdateControls(); await SettleAsync();
        SetLevelMeterTargets(0.7, 0.8); RemoteLevel.Value = 0.7; MicLevel.Value = 0.8;
        RemoteHealth.Text = "已观察到声音 · 1234 帧"; MicHealth.Text = "已观察到声音 · 567 帧"; elapsedUs = 9_000_000;
        RecordNavigationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await SettleAsync();
        Need(RemoteLevel.Value == 0 && MicLevel.Value == 0 && remoteLevelTargetRawPeak == 0 &&
            microphoneLevelTargetRawPeak == 0 && elapsedUs == 0 &&
            RemoteHealth.Text.Contains("0 帧", StringComparison.Ordinal) && MicHealth.Text.Contains("0 帧", StringComparison.Ordinal),
            "Entering a new recording retained the previous meeting's observed-audio counters.");

        // Put the main window in a non-recording page while an isolated failure fixture
        // verifies that a failed release cannot claim off, start Host or finish closing.
        NavigateTo(4); UpdateControls(); await SettleAsync();
        object failedRelease = await CheckFailedReleaseAsync();
        NavigateTo(0); UpdateControls(); await SettleAsync();
        var closingOwner = workers[^1]; closingOwner.BlockRelease();
        var closeArgs = new CancelEventArgs(); Window_Closing(this, closeArgs);
        await closingOwner.ReleaseEntered.Task.WaitAsync(token); await PumpAsync();
        Need(closeArgs.Cancel && WindowCloseRequested && !closeApproved && windowCloseWork is { IsCompleted: false } &&
            DesiredLevelPreview() is null && active == 1,
            "Window close did not wait for the preview owner release.");
        closingOwner.Publish(0.99f, 0.99f, microphoneStarted: true); await PumpAsync();
        Need(remoteLevelTargetRawPeak != 0.99f && microphoneLevelTargetRawPeak != 0.99f,
            "A callback mutated the raw meter targets after close was requested.");
        closingOwner.AllowRelease(); await windowCloseWork!.WaitAsync(token); await PumpAsync();
        Need(closeApproved && !IsVisible && levelPreview is null && active == 0 && host is null && !microphoneEnabled,
            "The window did not finish closing after the fake preview release.");
        return new
        {
            Status = "PASS", ActualWpfControls = true, NativeDisabledConstructorDoesNotOpenPreview = true,
            RemotePeakBeforeRecording = true, MicPreviewRequiresExplicitClick = true, PreviewReportsZeroRecordedFrames = true,
            MicOffWaitsForReleaseAndKeepsPendingScope = true, StartWaitsForReleaseBeforePreflight = true,
            QuickOnOffBeforeStartedWaitsForRelease = true,
            FailedPreflightNeverOpensHost = true, RapidSelectionCoalesces = true, MaximumConcurrentPreviewOwners = maximumActive,
            PreviewOwnersCreated = workers.Count, LateCallbackRejected = true, RecordingAndPausedDisablePreview = true,
            NewRecordingResetsCounters = true, CloseWaitsForRelease = true, FailedRelease = failedRelease,
            RawPeakTargetsPreserved = true, ContinuousSolidMeterTemplate = true,
            ActualCompositionRenderingAttackAndRelease = true, DisplaySamples = smoothSamples,
            SourceMode = "INJECTED FAKE PREVIEW OWNERS / SYNTHETIC PEAKS AND HOST HEALTH / NO NATIVE DEVICE ACCESS",
            CaptureStarted = false, MicrophoneCollected = false, PhysicalDeviceAndRealMicRelease = "NOT_RUN"
        };

        async Task SettleAsync()
        {
            await levelPreviewTransition.WaitAsync(token); await PumpAsync();
        }
        async Task PumpAsync()
        {
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
        }
        async Task<(double[] Remote, double[] Microphone)> ObserveFramesAsync(Action change, Func<bool> targetApplied)
        {
            var remote = new List<double>(); var mic = new List<double>();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TimeSpan previousFrame = TimeSpan.MinValue;
            EventHandler observer = (_, args) =>
            {
                if (!targetApplied() || args is not RenderingEventArgs frame || frame.RenderingTime == previousFrame) return;
                previousFrame = frame.RenderingTime;
                remote.Add(RemoteLevel.Value); mic.Add(MicLevel.Value);
                if (remote.Count >= 8) done.TrySetResult();
            };
            CompositionTarget.Rendering += observer;
            try
            {
                change();
                await done.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                return (remote.ToArray(), mic.ToArray());
            }
            finally { CompositionTarget.Rendering -= observer; }
        }
        async Task<object> CheckFailedReleaseAsync()
        {
            var fixture = new MainWindow(enumerateSourcesOnLoad: false)
            {
                dataRoot = Path.Combine(evidence, "synthetic-release-failure-data"), Width = MinWidth, Height = MinHeight
            };
            Directory.CreateDirectory(fixture.dataRoot);
            M3hFakeLevelPreview? failedOwner = null;
            int failurePreflights = 0;
            fixture.levelPreviewFactory = (selection, changed) => failedOwner = new(selection, changed, () => { });
            fixture.recordingPreflight = _ =>
            {
                failurePreflights++;
                return new(false, ["Unexpected preflight after failed release."], [], 0, 1_000_000, "synthetic");
            };
            try
            {
                fixture.Show(); fixture.NavigateTo(0); fixture.ModeBox.SelectedIndex = 1;
                fixture.EndpointBox.ItemsSource = new[] { endpointA }; fixture.EndpointBox.SelectedItem = endpointA;
                fixture.MicBox.ItemsSource = new[] { microphone }; fixture.MicBox.SelectedItem = microphone;
                fixture.SetMicrophoneRequested(true); fixture.levelPreviewNativeEnabled = true; fixture.UpdateControls();
                await fixture.levelPreviewTransition.WaitAsync(token);
                Need(failedOwner is not null, "Failed-release fixture did not create its fake owner.");
                failedOwner!.Publish(0.2f, 0.5f, microphoneStarted: true); await PumpAsync();
                failedOwner.FailRelease = true;
                fixture.MicControlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                try { await fixture.levelPreviewTransition.WaitAsync(token); }
                catch (InvalidOperationException ex) when (ex.Message == M3hFakeLevelPreview.ReleaseFailure) { }
                await PumpAsync();
                Need(ReferenceEquals(fixture.levelPreview, failedOwner) && !failedOwner.Released && fixture.microphonePreviewActive &&
                    fixture.microphonePreviewStopping && fixture.MicIconBody.IsVisible && !fixture.MicOffMark.IsVisible &&
                    fixture.ScopeText.Text.Contains("等待采集边界确认", StringComparison.Ordinal),
                    "A failed release discarded ownership or falsely displayed microphone off.");
                await fixture.StartCaptureAsync(trial: false);
                Need(failurePreflights == 0 && fixture.host is null, "A failed preview release authorized a recording Host.");
                fixture.closeAfterSave = true; fixture.UpdateControls();
                await fixture.FinishWindowCloseAsync(); await PumpAsync();
                Need(!fixture.closeApproved && fixture.IsVisible && fixture.host is null && !failedOwner.Released,
                    "A failed preview release was incorrectly treated as successful window shutdown.");
                return new { Status = "PASS", OwnershipRetained = true, NoFalseOff = true, NoHost = true,
                    CloseNotAcknowledged = true, Cleanup = "TEST-ONLY direct release of injected fake after disabling its fault; not a product recovery claim" };
            }
            finally
            {
                // The disposable is a controlled fake. Explicit test cleanup prevents an
                // intentionally faulted transition from stranding the isolated test window.
                fixture.levelPreviewNativeEnabled = false;
                if (failedOwner is not null) { failedOwner.FailRelease = false; failedOwner.AllowRelease(); await failedOwner.DisposeAsync(); }
                fixture.levelPreview = null; fixture.levelPreviewSelection = null; fixture.levelPreviewTransition = Task.CompletedTask;
                fixture.microphonePreviewActive = false; fixture.microphonePreviewStopping = false;
                fixture.closeApproved = true; fixture.Close();
            }
        }
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }

    private sealed class M3hFakeLevelPreview(AudioLevelPreviewSelection selection,
        Action<AudioLevelPreviewSnapshot> changed, Action released) : IAsyncDisposable
    {
        internal const string ReleaseFailure = "Synthetic preview release was not acknowledged.";
        private TaskCompletionSource? releaseGate;
        internal AudioLevelPreviewSelection Selection { get; } = selection;
        internal TaskCompletionSource ReleaseEntered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Released { get; private set; }
        internal bool FailRelease { get; set; }
        internal void BlockRelease()
        {
            ReleaseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        internal void AllowRelease() => releaseGate?.TrySetResult();
        internal void Publish(float remote, float mic, bool microphoneStarted) => changed(new(
            new(remote, remote > 0 ? AudioLevelPreviewState.SignalObserved : AudioLevelPreviewState.NoSignalObserved),
            new(mic, Selection.MicrophoneDeviceId is null ? AudioLevelPreviewState.NotSelected :
                mic > 0 ? AudioLevelPreviewState.SignalObserved : AudioLevelPreviewState.NoSignalObserved),
            Stopwatch.GetTimestamp(), microphoneStarted));
        public async ValueTask DisposeAsync()
        {
            ReleaseEntered.TrySetResult();
            if (releaseGate is not null) await releaseGate.Task;
            if (FailRelease) throw new InvalidOperationException(ReleaseFailure);
            if (!Released) { Released = true; released(); }
        }
    }
}
