using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProjectEcho.Capture.Windows;
using ProjectEcho.CaptureHost;
using ProjectEcho.Core;

namespace ProjectEcho.App;

public partial class MainWindow
{
    internal async Task<object> M3mAutoStopSmokeAsync(string evidence, CancellationToken token)
    {
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        Need(!levelPreviewNativeEnabled && host is null && !microphoneRequested, "Native capture must remain disabled.");
        dataRoot = Path.Combine(evidence, "synthetic-data"); Directory.CreateDirectory(dataRoot);
        Need(FindName("PreflightButton") is null && !AutoStopForCurrentSelection().Enabled &&
            AutoStopForCurrentSelection().SilenceMinutes == 5 && !AutoStopMinutesBox.IsEnabled,
            "Manual preflight remains or automatic stop defaults changed.");
        AutoStopCheckBox.IsChecked = true;
        var images = new List<string>();
        foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
        foreach (double width in new[] { 1280d, 960d })
        {
            Width = width; Height = width == 960 ? 640 : 930;
            ChangeLanguage(language, persist: false); NavigateTo(0); UpdateControls();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            Need(AutoStopCheckBox.IsVisible && AutoStopMinutesBox.IsVisible && AutoStopMinutesBox.IsEnabled,
                "Automatic stop controls are hidden or disabled.");
            var bounds = AutoStopMinutesBox.TransformToAncestor(RecordingBar).TransformBounds(
                new Rect(0, 0, AutoStopMinutesBox.ActualWidth, AutoStopMinutesBox.ActualHeight));
            Need(bounds.Left >= 0 && bounds.Right <= RecordingBar.ActualWidth && bounds.Bottom <= RecordingBar.ActualHeight,
                "The threshold selector clips outside the recording bar.");
            foreach (int index in new[] { 0, 1, 2 })
            {
                AutoStopMinutesBox.SelectedIndex = index;
                Need(AutoStopForCurrentSelection() == new RecordingAutoStopOptions(true, new[] { 1, 5, 10 }[index]),
                    "The selected interval is not the recording option.");
            }
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            Need(AutoStopMinutesBox.ActualHeight <= 40, "The selected duration wraps onto a second line.");
            SaveLayoutImage(evidence, $"m3m-auto-stop-{language}-{width}.png", 1, images);
        }
        int preflightCalls = 0;
        recordingPreflight = _ => { preflightCalls++; return new(false, ["Synthetic unavailable output"], [], 0, 0, "Synthetic"); };
        ModeBox.SelectedIndex = 1;
        EndpointBox.ItemsSource = new[] { new AudioDevice("synthetic-only", "Synthetic output; no device opened") };
        EndpointBox.SelectedIndex = 0;
        await StartCaptureAsync(trial: false);
        Need(preflightCalls == 1 && host is null && state == SessionState.Idle && RecordingValidationText.IsVisible,
            "Start skipped automatic validation or opened a host after validation failed.");
        foreach (var active in new[] { SessionState.Recording, SessionState.Paused, SessionState.Stopping })
        {
            state = active; UpdateControls();
            bool editablePolicy = active is SessionState.Recording or SessionState.Paused;
            Need(AutoStopCheckBox.IsEnabled == editablePolicy && AutoStopMinutesBox.IsEnabled == editablePolicy && !PlaybackButton.IsEnabled,
                "Live automatic settings or saving/playback locks are incorrect.");
        }
        state = SessionState.Recording;
        pendingControl = "auto_stop_settings"; pendingControlRequestId = "synthetic-auto-settings";
        HandleHostEvent(new(1, "synthetic", pendingControlRequestId, "ack", Ack:
            new(pendingControlRequestId, pendingControl, false, false, 0, AutoStopSettings: new(false, 5))));
        Need(AutoStopCheckBox.IsChecked == false && confirmedAutoStop == new RecordingAutoStopOptions(false, 5) && pendingControl is null,
            "Host receipt did not commit the automatic end setting.");
        var info = new RecordingAutoStopInfo("no_audio_activity", 5, 300_000_000, true, false);
        HandleHostEvent(new(1, "synthetic", "automatic", "finalizing", AutoStop: info));
        string saving = StorageText.Text;
        Need(state == SessionState.Stopping && StatusBox.Text.Contains("5", StringComparison.Ordinal), "Automatic ending did not enter saving.");
        HandleHostEvent(new(1, "synthetic", "health", "health", Health: new(300_000_000, false, false, 0, 0, 0, 0, "synthetic", "off", 0, 1L << 40)));
        Need(state == SessionState.Stopping && StorageText.Text == saving, "Late health reported writing or Saved during finalization.");
        Need(DescribeAutomaticStop(info).Contains(UiText.T(" 结束时部分来源未收到音频包，不等于已证实的静音；完整性以保存结果为准。"), StringComparison.Ordinal),
            "No-packet evidence was mislabeled silence.");
        state = SessionState.Idle; UpdateControls();
        return new { Status = "PASS", Version = "M3P Lite", ManualPreflightRemoved = true, StartStillValidates = true,
            DefaultUncheckedFiveMinutes = true, Intervals = new[] { 1, 5, 10 }, LivePolicyChangesAllowed = true, SavingPolicyLocked = true,
            FinalizingWaitsForSave = true, Languages = 3, Images = images, CaptureStarted = false, MicrophoneCollected = false };
    }
}
