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
    // Actual WPF controls with synthetic device metadata and injected Host messages only.
    // No CaptureHost, audio client, microphone collection or real boundary test is started here.
    internal async Task<object> M3gUiSmokeAsync(string evidence, CancellationToken token)
    {
        Need(host is null && !microphoneEnabled && !microphoneRequested && state == SessionState.Idle,
            "M3G UI smoke must start without capture or microphone consent.");
        dataRoot = Path.Combine(evidence, "synthetic-data");
        Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        int preflightCalls = 0;
        recordingPreflight = _ => { preflightCalls++; throw new InvalidOperationException("The icon must never start capture preflight."); };
        NavigateTo(0); Width = MinWidth; Height = MinHeight;
        var images = new List<string>();
        var microphone = new AudioDevice("synthetic-m3g-microphone-never-opened", "合成麦克风 · Synthetic microphone");
        MicBox.ItemsSource = new[] { microphone }; MicBox.SelectedIndex = -1;

        foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
        {
            ChangeLanguage(language, persist: false); ModeBox.SelectedIndex = 0; UpdateControls();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            Need(ApplicationVersionText.Text == "Ver. M3P Lite", "The current version is not M3G.");
            Need(Equals(((ComboBoxItem)ModeBox.Items[0]).Content, UiText.T("从特定应用录音")) &&
                Equals(((ComboBoxItem)ModeBox.Items[1]).Content, UiText.T("从所有应用录音")),
                "The recording mode names have not changed in every language.");
            Need(FindName("MicEnabled") is null && FindName("TrialButton") is null &&
                FindName("TrialPlaybackPanel") is null && FindName("TrialPlaybackButton") is null,
                "An old microphone checkbox or ten-second trial control remains in the WPF tree.");
            Need(MicControlButton.IsEnabled && MicOffMark.IsVisible && !microphoneRequested && !microphoneEnabled &&
                MicCaptionText.Text == UiText.T("本机Mic录音") && !string.IsNullOrWhiteSpace(MicStateText.Text),
                "The new microphone icon does not start visibly off with its caption.");
            Need(MicControlButton.Background is SolidColorBrush { Color.A: 0 } &&
                MicIconBody.Source is not null && MicOffMark.Source is not null,
                "The microphone is not a transparent vector control.");
            Point selectorBottom = MicBox.TranslatePoint(new Point(0, MicBox.ActualHeight), SelectionPanel);
            Point icon = MicControlButton.TranslatePoint(new Point(), SelectionPanel);
            Point caption = MicCaptionText.TranslatePoint(new Point(), SelectionPanel);
            Point iconBottom = MicControlButton.TranslatePoint(new Point(0, MicControlButton.ActualHeight), SelectionPanel);
            Need(icon.Y >= selectorBottom.Y && caption.Y >= iconBottom.Y && icon.X >= 0 &&
                icon.X + MicControlButton.ActualWidth <= SelectionPanel.ActualWidth + 1 &&
                caption.X + MicCaptionText.ActualWidth <= SelectionPanel.ActualWidth + 1,
                "The microphone icon/caption is not below the device dropdown or clips at minimum width.");
            SaveLayoutImage(evidence, "m3g-recording-off-" + language + ".png", 1, images);
            ModeBox.SelectedIndex = 1;
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            Need(EndpointScopeNotice.IsVisible && !string.IsNullOrWhiteSpace(EndpointScopeNotice.Text) &&
                !OptionsForCurrentSelection().EndpointScopeConfirmed,
                "The renamed all-app mode lost its explicit endpoint scope description or acquired confirmation.");
        }
        ChangeLanguage("zh-CN", persist: false); ModeBox.SelectedIndex = 0; UpdateControls();

        MicControlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
        Need(HasValidationIssue(MicBox) && RecordingValidationText.IsVisible && !microphoneRequested &&
            MicOffMark.IsVisible && host is null && preflightCalls == 0,
            "Clicking the microphone without a selected device was not rejected locally.");
        SaveLayoutImage(evidence, "m3g-missing-microphone.png", 1, images);
        MicBox.SelectedItem = microphone;
        Need(!HasValidationIssue(MicBox) && !microphoneRequested && !OptionsForCurrentSelection().MicrophoneEnabled,
            "Selecting a device enabled microphone collection or retained its validation error.");
        Need(OptionsForCurrentSelection().MicrophoneDeviceId is null,
            "A selected but disabled microphone was sent to the Host and fixed before first activation.");
        MicControlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
        var requested = OptionsForCurrentSelection();
        Need(microphoneRequested && !microphoneEnabled && host is null && preflightCalls == 0 &&
            requested.MicrophoneEnabled && requested.MicrophoneDeviceId == microphone.Id && !MicOffMark.IsVisible,
            "The pre-recording icon did not express an explicit request without opening capture.");
        SaveLayoutImage(evidence, "m3g-recording-microphone-requested.png", 1, images);
        MicControlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Need(!microphoneRequested && !microphoneEnabled && !OptionsForCurrentSelection().MicrophoneEnabled && MicOffMark.IsVisible,
            "Clicking the requested microphone again did not cancel the request.");
        MicControlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ResetConsentAfterSession(); UpdateControls();
        Need(!microphoneRequested && !microphoneEnabled && MicBox.SelectedIndex == -1 &&
            SourceBox.SelectedIndex == -1 && EndpointBox.SelectedIndex == -1 && MicOffMark.IsVisible,
            "A completed meeting retained microphone consent or source selection for the next meeting.");

        // Exercise recording-state UI and acknowledgement matching without constructing a Host.
        state = SessionState.Recording; isTrial = false; starting = false;
        mixedPath = Path.Combine(dataRoot, "synthetic-playback-never-opened.wav"); UpdateControls();
        Need(MicControlButton.IsEnabled && MicBox.IsEnabled && SelectionPanel.IsEnabled && !ModeBox.IsEnabled &&
            !SourceBox.IsEnabled && !EndpointBox.IsEnabled && !PlaybackButton.IsEnabled,
            "The new location disables the microphone control, unlocks source selection or allows playback during recording.");
        MicControlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Need(HasValidationIssue(MicBox) && !microphoneEnabled && pendingControl is null && host is null,
            "The recording-state microphone button did not reject a missing device locally.");
        MicBox.SelectedItem = microphone;
        pendingMicrophone = microphone; pendingControl = "mic_on"; pendingControlRequestId = "m3g-current-on"; UpdateControls();
        Need(!microphoneEnabled && MicOffMark.IsVisible && !MicControlButton.IsEnabled && !MicBox.IsEnabled,
            "Pending microphone startup is represented as completed or remains editable.");
        HandleHostEvent(new(1, "synthetic-m3g", "m3g-stale-on", "ack",
            Ack: new("m3g-stale-on", "mic_on", false, true, 120_000)));
        Need(!microphoneEnabled && fixedMicrophoneId is null && pendingControl == "mic_on" && MicOffMark.IsVisible,
            "A stale startup acknowledgement enabled the icon or fixed a microphone.");
        HandleHostEvent(new(1, "synthetic-m3g", "m3g-current-on", "ack",
            Ack: new("m3g-current-on", "mic_on", false, true, 230_000)));
        Need(microphoneEnabled && fixedMicrophoneId == microphone.Id && pendingControl is null &&
            !MicOffMark.IsVisible && !MicBox.IsEnabled && MicControlButton.IsEnabled,
            "A matched startup acknowledgement did not display actual enabled state and fix the device.");

        pendingControl = "mic_off"; pendingControlRequestId = "m3g-current-off"; UpdateControls();
        Need(microphoneEnabled && !MicOffMark.IsVisible && !MicControlButton.IsEnabled &&
            ScopeText.Text.Contains(UiText.T("等待采集边界确认")),
            "The icon claims the microphone is off before its capture-boundary acknowledgement.");
        string pendingOffText = MicStateText.Text;
        HandleHostEvent(new(1, "synthetic-m3g", "m3g-health", "health", Health: new(
            340_000, false, false, 0, 0, 0, 0, "Synthetic", "Synthetic", 0, 1_000_000_000)));
        HandleHostEvent(new(1, "synthetic-m3g", "m3g-stale-off", "ack",
            Ack: new("m3g-stale-off", "mic_off", false, false, 340_000)));
        Need(microphoneEnabled && pendingControl == "mic_off" && !MicOffMark.IsVisible && MicStateText.Text == pendingOffText,
            "A health message or stale acknowledgement completed microphone shutdown.");
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
        SaveLayoutImage(evidence, "m3g-microphone-shutdown-pending.png", 1, images);
        HandleHostEvent(new(1, "synthetic-m3g", "m3g-current-off", "ack",
            Ack: new("m3g-current-off", "mic_off", false, false, 450_000)));
        Need(!microphoneEnabled && !microphoneRequested && pendingControl is null && MicOffMark.IsVisible && !MicBox.IsEnabled,
            "A matched shutdown acknowledgement did not turn off the icon or retain the fixed device.");

        microphoneEnabled = true; SetMicrophoneRequested(true);
        pendingControl = "mic_off"; pendingControlRequestId = "m3g-rejected-off"; UpdateControls();
        HandleHostEvent(new(1, "synthetic-m3g", "m3g-rejected-off", "control_error", "Synthetic shutdown rejection"));
        Need(microphoneEnabled && !MicOffMark.IsVisible && pendingControl is null,
            "A rejected microphone shutdown is falsely displayed as disabled.");
        state = SessionState.Paused; UpdateControls();
        Need(!PlaybackButton.IsEnabled && !StopPlaybackButton.IsEnabled && MicControlButton.IsEnabled && !MicBox.IsEnabled,
            "Paused recording released playback, disabled microphone shutdown or changed its device.");
        microphoneEnabled = false; SetMicrophoneRequested(false); UpdateControls();
        Need(!MicControlButton.IsEnabled, "A paused recording permits starting microphone collection.");
        state = SessionState.Stopping; UpdateControls();
        Need(!MicControlButton.IsEnabled && !StartButton.IsEnabled && !MicBox.IsEnabled && !PlaybackButton.IsEnabled,
            "Stop-before-save leaves microphone/source/playback controls active.");

        state = SessionState.Idle; mixedPath = null;
        ResetConsentAfterSession(); UpdateControls();
        Need(host is null && !microphoneEnabled && !microphoneRequested && player.Source is null && preflightCalls == 0 &&
            !Directory.Exists(RecordingsRoot), "The UI smoke unexpectedly started capture, playback or persisted a meeting.");
        VerifyNonCaptureControlStates();
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
        SaveLayoutImage(evidence, "m3g-vector-200-percent.png", 2, images);
        object bundledAsr = M3gBundledWhisperSmoke(evidence);
        return new
        {
            Status = "PASS", Version = ApplicationVersionText.Text,
            ActualWpfControls = true, Languages = new[] { "zh-CN", "zh-Hant", "en" },
            RenamedSourceModes = true, MicrophoneDefaultOff = true, CheckboxAndTrialControlsRemoved = true,
            TransparentVectorBelowMicrophoneDropdown = true, MissingMicrophoneRejectedLocally = true,
            DeviceSelectionDoesNotEnableCollection = true, PreRecordingToggleOnlyChangesExplicitRequest = true,
            MeetingResetClearsConsent = true, SourceLockedAndMicrophoneControlReachableDuringRecording = true,
            DeviceFixedOnlyByMatchingStartupAck = true, HealthAndStaleMessagesNeverCompletePrivacyBoundary = true,
            ShutdownPendingNeverClaimsOff = true, RejectedShutdownRetainsActualState = true,
            PausedAndStoppingGuardsPreserved = true, ExistingNonCapturePrivacySmoke = "PASS",
            SourceMode = "SYNTHETIC DEVICE METADATA / INJECTED ACKS / NO CAPTURE", CaptureStarted = false,
            MicrophoneCollected = false, RealMicrophoneBoundary = "NOT_RUN", Images = images, BundledAsr = bundledAsr
        };
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
