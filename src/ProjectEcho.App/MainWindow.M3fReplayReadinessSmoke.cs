using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ProjectEcho.Core;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    // Synthetic saved meetings and muted native playback. No capture client is opened.
    internal async Task<object> M3fReplayReadinessSmokeAsync(string evidence, CancellationToken token)
    {
        Need(host is null && !microphoneEnabled && !microphoneRequested, "Replay smoke requires no capture.");
        dataRoot = Path.Combine(evidence, "synthetic-replay-data"); Directory.CreateDirectory(dataRoot);
        DataRootText.Text = dataRoot;
        string first = Path.Combine(RecordingsRoot, "ready-a"), second = Path.Combine(RecordingsRoot, "ready-b");
        await Task.Run(() => { WriteCloseFixture(first); WriteCloseFixture(second);
            M2ExportService.RenderMeeting(first, encoder: null); M2ExportService.RenderMeeting(second, encoder: null); }, token);
        var originalHashes = Directory.EnumerateFiles(RecordingsRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, CloseFixtureHash);
        player.Volume = 0; player.IsMuted = true; state = SessionState.Idle;
        var defaultLoader = savedMixLoadOperation;
        int loads = 0;
        var results = new List<string>();
        try
        {
            RefreshRecovery_Click(this, new RoutedEventArgs());
            using (var gate = new ExportHistoryGate())
            {
                savedMixLoadOperation = (directory, cancellation) => { Interlocked.Increment(ref loads);
                    return gate.Run(() => M2SavedMixLoader.LoadLatestForListening(directory, cancellation), cancellation); };
                SelectMeeting(first); NavigateTo(5); await gate.Entered.Task.WaitAsync(token);
                Need(CanRequestSavedMixPlayback && PlaybackButton.IsEnabled && transcriptionView!.PlayListeningButton.IsEnabled &&
                    CurrentSubtitleText.Text.Contains("校验", StringComparison.Ordinal), "Waiting playback lacked a visible actionable state.");
                PlaybackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                SelectMeeting(first); PlaybackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Need(loads == 1 && savedMixPlaybackRequest is not null && player.Source is null,
                    "Repeated selection restarted validation or played unverified audio.");
                Need(TryQueueSavedMixPlayback(700, first), "Timed subtitle seek was not queued while validation ran.");
                gate.Release.Set(); await savedMixWork.WaitAsync(token);
                var watch = Stopwatch.StartNew();
                while (!playbackOpened && watch.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25, token);
                Need(playbackOpened && player.Source is not null && player.Position.TotalMilliseconds >= 680 &&
                    savedMixPlaybackRequest is null && !CurrentSubtitleText.Text.Contains("正在校验", StringComparison.Ordinal),
                    "Verified last seek did not reach native playback or readiness remained stale.");
                results.Add("one_validation_repeated_clicks_latest_seek_native_muted_playback");
            }
            ResetTranscriptPlayback();
            SelectMeeting(second, loadSavedMix: false); SelectMeeting(first);
            Need(loads == 1 && mixedPath is not null && player.Source is null, "Validated cache restarted work or navigation autoplayed.");
            results.Add("validated_cache_navigation_does_not_autoplay");

            foreach (string boundary in new[] { "stop", "meeting", "recording", "paused", "stopping", "faulted", "close_cancel" })
            {
                using var gate = new ExportHistoryGate();
                renderedMeetings.Remove(first); state = SessionState.Idle;
                savedMixLoadOperation = (directory, cancellation) => gate.Run(
                    () => M2SavedMixLoader.LoadLatestForListening(directory, cancellation), cancellation);
                SelectMeeting(first); await gate.Entered.Task.WaitAsync(token);
                PlaybackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Need(savedMixPlaybackRequest is not null, "Boundary case did not queue playback.");
                switch (boundary)
                {
                    case "stop": StopPlayback_Click(this, new RoutedEventArgs()); break;
                    case "meeting": SelectMeeting(second, loadSavedMix: false); break;
                    case "recording": state = SessionState.Recording; UpdateControls(); break;
                    case "paused": state = SessionState.Paused; UpdateControls(); break;
                    case "stopping": state = SessionState.Stopping; UpdateControls(); break;
                    case "faulted": state = SessionState.Faulted; UpdateControls(); break;
                    case "close_cancel": CancelBackgroundForClose(); break;
                }
                Need(savedMixPlaybackRequest is null, "Boundary retained pending playback: " + boundary);
                gate.Release.Set(); await savedMixWork.WaitAsync(token); await Task.Delay(50, token);
                Need(player.Source is null && !playbackOpened && pendingTranscriptSeek is null, "Late validation autoplayed after " + boundary);
                results.Add("no_late_playback_after_" + boundary);
            }
            state = SessionState.Idle; renderedMeetings.Remove(first);
            using (var gate = new ExportHistoryGate())
            {
                savedMixLoadOperation = (_, cancellation) => gate.Run<FullMixResult?>(() => throw new InvalidDataException("synthetic validation failure"), cancellation);
                SelectMeeting(first); await gate.Entered.Task.WaitAsync(token);
                PlaybackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                gate.Release.Set(); await savedMixWork.WaitAsync(token);
                Need(mixedPath is null && savedMixPlaybackRequest is null && player.Source is null &&
                    CurrentSubtitleText.Text.Contains("尚未就绪", StringComparison.Ordinal), "Validation failure did not cancel playback visibly.");
                results.Add("failed_validation_visible_and_never_played");
            }
            Need(originalHashes.All(pair => File.Exists(pair.Key) && CloseFixtureHash(pair.Key) == pair.Value), "Replay altered authoritative source/render files.");
            return new { Passed = true, Checks = results, NativePlaybackMuted = true, OriginalHashesUnchanged = true,
                CaptureStarted = false, MicrophoneCollected = false };
        }
        finally
        {
            savedMixCancellation?.Cancel(); await savedMixWork.WaitAsync(TimeSpan.FromSeconds(15));
            savedMixLoadOperation = defaultLoader; state = SessionState.Idle; ResetTranscriptPlayback(); UpdateControls();
        }
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
