using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using ProjectEcho.Capture.Windows;
using ProjectEcho.CaptureHost;
using ProjectEcho.Core;
using ProjectEcho.Core.Diagnostics;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Generated PCM, imported subsecond cues and muted WPF seeking; no device capture.</summary>
    internal async Task<object> M3gTimeSmokeAsync(string evidence, CancellationToken token)
    {
        Need(host is null && !microphoneEnabled, "Time smoke must not collect audio.");
        dataRoot = Path.Combine(evidence, "synthetic-time-data"); Directory.CreateDirectory(dataRoot);
        DataRootText.Text = dataRoot;
        string directory = Path.Combine(RecordingsRoot, "hundredth-timeline");
        await Task.Run(() => { WriteCloseFixture(directory); M2ExportService.RenderMeeting(directory, encoder: null); }, token);
        string source = Path.Combine(evidence, "hundredths.srt");
        File.WriteAllText(source, "1\n00:00:01,230 --> 00:00:01,370\n百分秒第一句。\n\n2\n00:00:01,380 --> 00:00:01,490\n百分秒第二句。\n");
        var revision = TranscriptService.Import(new(directory, Path.GetFileName(directory), 2000, source, "Synthetic", IsSynthetic: true));
        var originals = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => path, CloseFixtureHash);
        player.Volume = 0; player.IsMuted = true;
        var seeks = new List<object>(); var images = new List<string>();
        try
        {
            SelectMeeting(directory); await savedMixWork.WaitAsync(token); await replayLoadWork.WaitAsync(token);
            NavigateTo(5); SetReplayRevisions(directory, [revision], revision.RevisionId);
            var rows = ReplaySegmentsBox.Items.Cast<TranscriptRow>().ToArray();
            Need(rows[0].TimeLabel == "00:00:01.23" && rows[1].TimeLabel == "00:00:01.38" &&
                rows[1].Label.Contains("00:00:01.38", StringComparison.Ordinal), "Replay/search labels hide hundredths.");
            var view = transcriptionView!;
            Need(ReplayPositionSlider.SmallChange == .01 && view.PlaybackPositionSlider.SmallChange == .01,
                "Keyboard seeking still steps in whole seconds.");
            OpenListeningPosition(mixedPath!, 0, currentMix!.DurationUs, directory);
            var ready = Stopwatch.StartNew();
            while (!playbackOpened && ready.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25, token);
            Need(playbackOpened && player.IsMuted && player.Volume == 0, "Muted WAV did not open.");
            player.Pause(); playbackPaused = true; ResetDisplayPlaybackClock();
            foreach (var sample in new[] { (Milliseconds: 1380L, Cue: 1), (Milliseconds: 1230L, Cue: 0),
                (Milliseconds: 1380L, Cue: 1), (Milliseconds: 1370L, Cue: -1), (Milliseconds: 1380L, Cue: 1) })
            {
                ReplayPositionSlider.Value = sample.Milliseconds / 1000d;
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
                File.AppendAllText(Path.Combine(evidence, "seek-diagnostics.txt"), $"requested={sample.Milliseconds}; immediate ticks={player.Position.Ticks}; slider={ReplayPositionSlider.Value:R}; native={player.Position.TotalMilliseconds:R}\n");
                await Task.Delay(150, token);
                UpdatePlaybackPosition();
                File.AppendAllText(Path.Combine(evidence, "seek-diagnostics.txt"), $"after150ms ticks={player.Position.Ticks}; slider={ReplayPositionSlider.Value:R}; native={player.Position.TotalMilliseconds:R}\n");
                double actual = player.Position.TotalMilliseconds;
                string expectedTime = TimelineTimestamp.FromMilliseconds(sample.Milliseconds) + " / 00:00:02.00";
                Need(Math.Abs(actual - sample.Milliseconds) < 1 && playbackPaused &&
                    ReplayTimeText.Text == expectedTime && view.PlaybackTimeText.Text == expectedTime,
                    $"Subsecond seek/labels were quantized: requested={sample.Milliseconds}, native={actual}, label={ReplayTimeText.Text}.");
                Need(sample.Cue < 0 ? ReplaySegmentsBox.SelectedItem is null :
                    ReplaySegmentsBox.SelectedItem is TranscriptRow selected && selected.Segment.SegmentId == rows[sample.Cue].Segment.SegmentId,
                    "Same-second cues or their subsecond gap were not followed.");
                seeks.Add(new { RequestedMs = sample.Milliseconds, NativePositionMs = actual, Display = ReplayTimeText.Text, ExpectedCue = sample.Cue });
            }
            NavigateTo(3); DisplayRevision(directory, revision); view.SegmentsBox.SelectedIndex = 1;
            Need(view.StartTimestampBox.CentisecondsBox.Text == "38", "Review cue detail still shows whole seconds.");
            view.PlaybackPositionSlider.Value = 1.39;
            await Task.Delay(150, token); UpdatePlaybackPosition();
            Need(Math.Abs(player.Position.TotalMilliseconds - 1390) < 1 && ReplayTimeText.Text.StartsWith("00:00:01.39", StringComparison.Ordinal),
                "Review seek lost 0.01-second precision.");
            NavigateTo(5); await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            SaveLayoutImage(evidence, "hundredth-replay.png", 1, images);
            ResetTranscriptPlayback();

            // Exercise the production acknowledgement display with a synthetic receipt.
            // No CaptureHost or capture thread exists in this smoke process.
            recordingLogDirectory = directory; state = SessionState.Recording;
            pendingControl = "bookmark"; pendingControlRequestId = "synthetic-time-bookmark";
            HandleHostEvent(new HostEvent(1, "synthetic-time", pendingControlRequestId, "ack", Ack:
                new RecordingAck(pendingControlRequestId, "bookmark", false, false, 1_239_999, "已标记此刻")));
            Need(StatusBox.Text.Contains("00:00:01.23", StringComparison.Ordinal) && elapsedUs == 1_239_999 &&
                MeetingLogStore.Read(directory).Entries.Any(entry => entry.Message.Contains("生效时间 00:00:01.23", StringComparison.Ordinal)),
                "Capture acknowledgement/log discarded hundredths or changed stored microseconds.");
            string acknowledgement = StatusBox.Text;
            Need(host is null && !microphoneEnabled && originals.All(pair => File.Exists(pair.Key) && CloseFixtureHash(pair.Key) == pair.Value),
                "Time smoke captured audio or altered original audio/transcript files.");
            return new { Passed = true, SourceMode = "SYNTHETIC PCM / MUTED WPF / SYNTHETIC ACK / NO CAPTURE",
                Seeks = seeks, Acknowledgement = acknowledgement, EffectiveSessionUs = elapsedUs,
                SourceTimesRetained = true, SubtitleQuality = "approximate; display precision is not acoustic alignment accuracy",
                NativeSeekLimitation = "On this Windows WPF runtime a consecutive seek differing by only 1 ms can remain at the prior position; no clock is fabricated. The required 10 ms steps are verified.",
                CaptureStarted = false, MicrophoneCollected = false, Images = images };
        }
        finally
        {
            recordingLogDirectory = null; pendingControl = null; pendingControlRequestId = null;
            state = SessionState.Idle; elapsedUs = 0; ResetTranscriptPlayback(); UpdateControls();
        }
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
