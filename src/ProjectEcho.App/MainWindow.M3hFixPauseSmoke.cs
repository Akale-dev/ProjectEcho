using System.IO;
using System.Windows.Threading;
using ProjectEcho.Capture.Windows;
using ProjectEcho.CaptureHost;
using ProjectEcho.Core;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Real WPF controls, synthetic Host messages only. No capture, microphone or media playback.</summary>
    internal async Task<object> M3hFixPauseSmokeAsync(string evidence, CancellationToken token)
    {
        Need(!levelPreviewNativeEnabled && levelPreview is null && host is null && state == SessionState.Idle,
            "Pause smoke requires the native-disabled constructor.");
        dataRoot = Path.Combine(evidence, "synthetic-pause-data"); Directory.CreateDirectory(dataRoot);
        DataRootText.Text = dataRoot; NavigateTo(0);
        var images = new List<string>(); var cases = new List<object>();
        const long boundaryUs = 1_234_560;
        try
        {
            foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
            {
                ChangeLanguage(language, persist: false);
                state = SessionState.Recording; elapsedUs = 0; pausedRecordingElapsedUs = null;
                microphoneEnabled = true; SetMicrophoneRequested(true);
                fixedMicrophoneId = "synthetic-mic-never-opened"; fixedMicrophoneName = "Synthetic microphone";
                starting = false; pendingControl = null; pendingControlRequestId = null; UpdateControls();
                Health(1_200_000, paused: false, mic: true);
                Need(elapsedUs == 1_200_000, "Recording time did not follow active Host time.");

                Request("pause");
                Ack("pause", paused: true, mic: true, boundaryUs, request: "unrelated-request");
                Need(state == SessionState.Recording && pendingControl == "pause",
                    "An unrelated pause ACK completed a transition.");
                Health(1_280_000, paused: false, mic: true);
                Need(state == SessionState.Recording && !PauseButton.IsEnabled && microphoneEnabled &&
                    elapsedUs == 1_280_000, "Pending pause claimed collection stopped before its ACK.");
                Ack("pause", paused: true, mic: true, boundaryUs);
                string pausedLabel = SessionStateText.Text;
                Need(state == SessionState.Paused && elapsedUs == boundaryUs && pausedRecordingElapsedUs == boundaryUs &&
                    pausedLabel.Contains(UiText.T("已暂停 · 计时停止，两路均不采集"), StringComparison.Ordinal) &&
                    pausedLabel.EndsWith("00:00:01.23", StringComparison.Ordinal),
                    "Pause did not adopt the precise capture-thread boundary or translated frozen-time text.");
                Need((string)PauseButton.Content == UiText.T("恢复录音") && microphoneEnabled &&
                    MicStateText.Text == UiText.T("暂停，不采集；恢复录音后开启"),
                    "Pause lost the resume command or microphone consent state.");
                for (int index = 0; index < 3; index++)
                {
                    Health(30_000_000 + index, paused: false, mic: true);
                    HandleHostEvent(new(1, "synthetic-pause", "levels", "levels", Levels:
                        new(.9f, .8f, 900_000, 800_000, "stale recording callback")));
                }
                Health(boundaryUs, paused: true, mic: true);
                await Task.Delay(160, token);
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
                Need(elapsedUs == boundaryUs && SessionStateText.Text == pausedLabel &&
                    RemoteHealth.Text == UiText.T("暂停 · 不采集") &&
                    MicHealth.Text == UiText.T("暂停 · 不采集，恢复后开启") &&
                    RemoteLevel.Value == 0 && MicLevel.Value == 0 && remoteLevelTargetRawPeak == 0 && microphoneLevelTargetRawPeak == 0,
                    "Delayed health/levels or wall time changed a confirmed pause boundary or no-capture meters.");
                Need(!PlaybackButton.IsEnabled && !PausePlaybackButton.IsEnabled && !ReplayPositionSlider.IsEnabled &&
                    DesiredLevelPreview() is null && levelPreview is null && host is null,
                    "Pause permitted playback, pre-recording preview or native capture.");
                SaveLayoutImage(evidence, $"pause-{language}.png", 1, images);

                Request("bookmark"); Ack("bookmark", paused: true, mic: true, boundaryUs);
                Need(elapsedUs == boundaryUs && state == SessionState.Paused, "A paused bookmark advanced recording time.");
                Request("mic_off"); Ack("mic_off", paused: true, mic: false, boundaryUs);
                Health(45_000_000, paused: false, mic: true);
                Need(elapsedUs == boundaryUs && !microphoneEnabled && !microphoneRequested &&
                    MicHealth.Text == UiText.T("关闭 · 不采集") && !MicControlButton.IsEnabled,
                    "Paused MicOff or a stale mic health report resumed collection/clock or restored consent.");

                Request("resume");
                Health(50_000_000, paused: false, mic: false);
                Need(state == SessionState.Paused && elapsedUs == boundaryUs && !PauseButton.IsEnabled,
                    "A health report completed resume before its capture-thread ACK.");
                Ack("resume", paused: false, mic: false, boundaryUs);
                Need(state == SessionState.Recording && elapsedUs == boundaryUs && pausedRecordingElapsedUs is null &&
                    !microphoneEnabled && (string)PauseButton.Content == UiText.T("暂停"),
                    "Resume did not continue from the frozen timeline or restored disabled mic consent.");
                Health(60_000_000, paused: true, mic: true);
                Need(elapsedUs == boundaryUs && state == SessionState.Recording && !microphoneEnabled,
                    "A late paused health report overwrote the acknowledged resume state/time.");
                Health(boundaryUs + 20_000, paused: false, mic: false);
                Need(elapsedUs == boundaryUs + 20_000 && SessionStateText.Text.EndsWith("00:00:01.25", StringComparison.Ordinal),
                    "Resumed time contains the pause gap or lost hundredth-second display.");
                Health(boundaryUs + 10_000, paused: false, mic: false);
                Need(elapsedUs == boundaryUs + 20_000, "Out-of-order active health moved time backwards.");

                Request("pause"); Ack("pause", paused: true, mic: false, boundaryUs + 30_000);
                state = SessionState.Stopping; UpdateControls();
                Health(70_000_000, paused: false, mic: true);
                Need(elapsedUs == boundaryUs + 30_000 && !PlaybackButton.IsEnabled && DesiredLevelPreview() is null,
                    "Stopping a paused recording added paused/drain wall time or enabled playback.");

                // System-gap ACKs use the same authoritative boundary and need no pending user request.
                state = SessionState.Recording; pausedRecordingElapsedUs = null; elapsedUs = 90_000_000;
                pendingControl = null; pendingControlRequestId = null;
                Ack("pause", paused: true, mic: false, boundaryUs + 40_000, request: "system-gap");
                Health(100_000_000, paused: false, mic: false);
                Need(state == SessionState.Paused && elapsedUs == boundaryUs + 40_000 &&
                    !PlaybackButton.IsEnabled && DesiredLevelPreview() is null,
                    "System pause retained an unacknowledged wall-clock gap.");
                cases.Add(new { Language = language, CaptureBoundaryUs = boundaryUs, PausedLabel = pausedLabel,
                    ResumedElapsedUs = boundaryUs + 20_000, StopDuringPauseUs = boundaryUs + 30_000,
                    SystemPauseUs = elapsedUs, DelayedHealthAndLevelsRejected = true,
                    BookmarkAndMicOffDoNotAdvanceTime = true, PlaybackAndPreviewBlocked = true });
            }
            Need(host is null && levelPreview is null && player.Source is null, "The smoke opened native capture or playback.");
            return new { Passed = true, SourceMode = "SYNTHETIC HOST MESSAGES / ACTUAL WPF / NO CAPTURE",
                CaptureStarted = false, MicrophoneCollected = false, Version = ApplicationVersionText.Text,
                Cases = cases, Images = images };
        }
        finally
        {
            state = SessionState.Idle; elapsedUs = 0; pausedRecordingElapsedUs = null;
            ResetConsentAfterSession(); UpdateControls();
        }

        void Request(string operation)
        {
            pendingControl = operation; pendingControlRequestId = "synthetic-" + operation; UpdateControls();
        }
        void Ack(string operation, bool paused, bool mic, long elapsed, string? request = null)
        {
            string id = request ?? pendingControlRequestId ?? throw new InvalidOperationException("Missing synthetic request.");
            HandleHostEvent(new HostEvent(1, "synthetic-pause", id, "ack", Ack:
                new RecordingAck(id, operation, paused, mic, elapsed)));
        }
        void Health(long elapsed, bool paused, bool mic) => HandleHostEvent(new(1, "synthetic-pause", "health", "health", Health:
            new(elapsed, paused, mic, .6f, .5f, 10_000, 5_000, "已观察到声音", "已观察到声音", 0, 1_000_000_000)));
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
