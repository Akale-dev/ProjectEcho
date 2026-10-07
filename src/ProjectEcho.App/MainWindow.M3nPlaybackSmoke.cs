using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ProjectEcho.Core;
using ProjectEcho.Core.Transcription;
using Path = System.IO.Path;

namespace ProjectEcho.App;

public partial class MainWindow
{
    internal async Task<object> M3nPlaybackSmokeAsync(string evidence, CancellationToken token)
    {
        static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
        Need(!levelPreviewNativeEnabled && host is null && !microphoneRequested, "WPF test must not collect audio.");
        dataRoot = Path.Combine(evidence, "synthetic-data"); Directory.CreateDirectory(dataRoot);
        string wave = Path.Combine(evidence, "synthetic-silence.wav");
        using (var writer = new BinaryWriter(File.Create(wave)))
        {
            const int frames = 8000 * 60;
            writer.Write("RIFF"u8); writer.Write(frames * 2 + 36); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
            writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(frames * 2); writer.Write(new byte[frames * 2]);
        }
        var imported = await ImportAudioFileAsync(wave, token);
        await savedMixWork.WaitAsync(token); await replayLoadWork.WaitAsync(token);
        string directory = imported.MeetingDirectory;
        string srt = Path.Combine(evidence, "synthetic.srt");
        File.WriteAllText(srt, "1\n00:00:05,000 --> 00:00:10,000\nfive\n\n2\n00:00:15,000 --> 00:00:20,000\nfifteen\n\n3\n00:00:30,000 --> 00:00:35,000\nthirty\n");
        var revision = TranscriptService.Import(new(directory, "Synthetic M3O", 60000, srt, "Synthetic", IsSynthetic: true), token);
        SetReplayRevisions(directory, [revision], revision.RevisionId);
        DisplayRevision(directory, revision);
        player.Volume = 0; player.IsMuted = true;
        NavigateTo(5); OpenListeningPosition(mixedPath!, 29000, currentMix!.DurationUs, directory);
        var waiting = Stopwatch.StartNew();
        while (!playbackOpened) { Need(waiting.Elapsed < TimeSpan.FromSeconds(8), "Silent media did not open."); await Task.Delay(25, token); }
        player.Pause(); playbackPaused = true; UpdateControls();
        var jumps = new List<object>(); var images = new List<string>();
        Button[] buttons = SkipPlaybackButtons;
        Need(buttons.Select(button => (string)button.Tag).SequenceEqual(new[] { "-10", "-5", "5", "10" }), "Skip order differs from confirmed user order.");
        foreach (var sample in new (int Index, long Start, long End)[] { (0, 29000L, 19000L), (1, 29000L, 24000L),
            (2, 29000L, 34000L), (3, 29000L, 39000L), (0, 3000L, 0L), (3, 58000L, 59999L) })
        {
            SeekPlaybackMilliseconds(sample.Start); ResetDisplayPlaybackClock();
            buttons[sample.Index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100, token); UpdatePlaybackPosition();
            Need(Math.Abs(player.Position.TotalMilliseconds - sample.End) < 1 && playbackPaused, "Skip changed pause state or target time.");
            Need(ReplayTimeText.Text == transcriptionView!.PlaybackTimeText.Text &&
                Math.Abs(ReplayPositionSlider.Value * 1000 - sample.End) < 1 && Math.Abs(transcriptionView.PlaybackPositionSlider.Value * 1000 - sample.End) < 1,
                "Shared transport labels or sliders did not follow the skip.");
            if (sample.End == 19000 || sample.End == 34000)
                Need(ReplaySegmentsBox.SelectedItem is TranscriptRow row && row.Segment.Text == (sample.End == 19000 ? "fifteen" : "thirty"), "Subtitle did not follow skip.");
            jumps.Add(new { sample.Index, sample.Start, sample.End, Actual = player.Position.TotalMilliseconds });
        }
        playbackSpeed = 1.5; UpdatePlaybackTransport(); SeekPlaybackMilliseconds(20000); player.Play(); playbackPaused = false;
        buttons[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Need(!playbackPaused && player.SpeedRatio == 1.5 && player.Position.TotalMilliseconds is >= 24999 and < 25500,
            "A skip stopped playback or reset speed.");
        player.Pause(); playbackPaused = true; UpdateControls(); SeekPlaybackMilliseconds(15000);
        SeekPlaybackMilliseconds(16000); InvalidateReplaySubtitleFollow(); UpdatePlaybackPosition();
        await PressArrow(Key.Left);
        Need(playbackPaused && Math.Abs(player.Position.TotalMilliseconds - 11000) < 1, "Paused Left key started playback or moved by the wrong amount.");
        await PressArrow(Key.Right);
        Need(playbackPaused && Math.Abs(player.Position.TotalMilliseconds - 16000) < 1, "Paused Right key did not move 5 seconds.");
        int pausedSelection = ReplaySegmentsBox.SelectedIndex;
        await PressArrow(Key.Down);
        Need(playbackPaused && ReplaySegmentsBox.SelectedIndex == pausedSelection + 1 && Math.Abs(player.Position.TotalMilliseconds - 16000) < 1,
            "Paused Up/Down changed audio time or lost the selected subtitle.");
        await PressArrow(Key.Up);
        Need(ReplaySegmentsBox.SelectedIndex == pausedSelection && playbackPaused, "Paused Up key failed.");
        player.Play(); playbackPaused = false;
        await PressArrow(Key.Down);
        Need(!playbackPaused && player.Position.TotalMilliseconds is >= 30000 and < 30800 && ReplaySegmentsBox.SelectedIndex == 2,
            "Playing Down key did not play the selected subtitle.");
        await PressArrow(Key.Up);
        Need(!playbackPaused && player.Position.TotalMilliseconds is >= 15000 and < 15800 && ReplaySegmentsBox.SelectedIndex == 1,
            "Playing Up key did not play the selected subtitle.");
        SeekPlaybackMilliseconds(20000); ResetDisplayPlaybackClock(); await PressArrow(Key.Left);
        Need(!playbackPaused && player.Position.TotalMilliseconds is >= 14999 and < 15800, "Playing Left key stopped playback or did not seek.");
        SeekPlaybackMilliseconds(20000); ResetDisplayPlaybackClock(); await PressArrow(Key.Right);
        Need(!playbackPaused && player.Position.TotalMilliseconds is >= 24999 and < 25800 && player.SpeedRatio == 1.5, "Playing Right key lost speed/state.");
        player.Pause(); playbackPaused = true; SeekPlaybackMilliseconds(15000); ResetDisplayPlaybackClock();
        var dropdownKey = KeyArgs(Key.Left); ReplayRevisionBox.RaiseEvent(dropdownKey);
        Need(Math.Abs(player.Position.TotalMilliseconds - 15000) < 1 && playbackPaused, "A dropdown's navigation caused an unintended seek.");
        var view = transcriptionView!;
        var restart = (System.Windows.Shapes.Path)PlaybackButton.Content;
        var reviewRestart = (System.Windows.Shapes.Path)view.PlayListeningButton.Content;
        Need(restart.Data == reviewRestart.Data && restart.Data.ToString() == ReplayPlayGeometry.ToString(),
            "Both Play buttons must use the original triangle.");
        var resumeChecks = new List<object>();
        foreach (var controls in new[] { (Play: PlaybackButton, Pause: PausePlaybackButton), (Play: view.PlayListeningButton, Pause: view.PauseListeningButton) })
        {
            SeekPlaybackMilliseconds(21000); player.Pause(); playbackPaused = true; UpdateControls();
            var activePlayer = player;
            controls.Play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(180, token);
            Need(ReferenceEquals(player, activePlayer) && !playbackPaused && player.Position.TotalMilliseconds is >= 21000 and < 22500 && player.SpeedRatio == 1.5,
                "Play restarted the file instead of resuming at the paused seek position.");
            double beforeRepeat = player.Position.TotalMilliseconds;
            controls.Play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Need(!playbackPaused && player.Position.TotalMilliseconds >= beforeRepeat - 1, "Repeated Play jumped backwards or paused.");
            controls.Pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            double paused = player.Position.TotalMilliseconds;
            await Task.Delay(160, token);
            Need(playbackPaused && Math.Abs(player.Position.TotalMilliseconds - paused) < 1 &&
                PauseReplayIcon.Data == ReplayPauseGeometry && view.PauseListeningIcon.Data == ReplayPauseGeometry,
                "Pause changed either icon to Play or failed to stop the clock.");
            controls.Pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(180, token);
            Need(!playbackPaused && player.Position.TotalMilliseconds > paused &&
                PauseReplayIcon.Data == ReplayPauseGeometry && view.PauseListeningIcon.Data == ReplayPauseGeometry,
                "Pause could not resume or changed its double-bar icon.");
            resumeChecks.Add(new { controls.Play.Name, PausedAt = paused, ResumedAt = player.Position.TotalMilliseconds });
        }
        player.Pause(); playbackPaused = true; UpdateControls();
        MethodInfo press = typeof(ButtonBase).GetMethod("SetIsPressed", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("WPF pressed-state hook is unavailable; pressed checks were not run.");
        foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
        foreach (int page in new[] { 5, 3 })
        {
            Width = 1280; Height = 930; ChangeLanguage(language, false); NavigateTo(page);
            if (page == 3) view.WorkspaceTabs.SelectedIndex = 1;
            await Render();
            var transports = page == 5 ? new[] { PlaybackButton, PausePlaybackButton, StopPlaybackButton }.Concat(buttons).ToArray()
                : new[] { view.PlayListeningButton, view.PauseListeningButton, view.StopListeningButton };
            SaveLayoutImage(evidence, $"m3n-{page}-{language}-before-state-check.png", 1, images);
            foreach (Button button in transports)
            {
                var content = (FrameworkElement)button.Content;
                Size normal = content.RenderSize;
                Need(button.IsEnabled && normal.Width > 0 && normal.Height > 0,
                    $"Transport {button.Name} page={page} language={language}: enabled={button.IsEnabled}, icon={normal}, button={button.RenderSize}, playback={playbackOpened}, state={state}.");
                Rect allocated = new(content.RenderSize); allocated.Inflate(.5, .5);
                Rect ink = content is Shape shape ? shape.RenderedGeometry.Bounds : VisualTreeHelper.GetDescendantBounds(content);
                Need(allocated.Contains(ink), $"Actual drawn icon bounds exceed available space: {button.Name}, ink={ink}, allocated={allocated}.");
                if (content is SeekPlaybackIcon icon)
                {
                    Rect digit = icon.NumberGlyph.Data.Bounds;
                    Point center = icon.NumberGlyph.TransformToAncestor(button).Transform(new Point(digit.Left + digit.Width / 2, digit.Top + digit.Height / 2));
                    Need(Math.Abs(center.X - button.ActualWidth / 2) <= .51 && Math.Abs(center.Y - button.ActualHeight / 2) <= .51,
                        $"Skip digit is not centered inside its button: {button.Name}, center={center}, size={button.RenderSize}.");
                    Need(Math.Abs(digit.Left + digit.Width / 2 - 14) < .001 && Math.Abs(digit.Top + digit.Height / 2 - 14) < .001,
                        "Skip digit ink is not centered on the arrow circle.");
                }
                button.Focus(); await Render();
                Need(content.RenderSize == normal, "Keyboard focus shrank the icon.");
                press.Invoke(button, [true]); await Render();
                try
                {
                    Need(button.IsPressed && content.RenderSize == normal, "Pressed state shrank the icon.");
                    Rect bounds = content.TransformToAncestor(button).TransformBounds(new Rect(content.RenderSize));
                    Need(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= button.ActualWidth && bounds.Bottom <= button.ActualHeight,
                        "Focused/pressed icon clips outside its button.");
                    if (button == transports[0]) SaveLayoutImage(evidence, $"m3n-{page}-{language}-pressed.png", 1, images);
                }
                finally { press.Invoke(button, [false]); }
            }
            SaveLayoutImage(evidence, $"m3n-{page}-{language}.png", 1, images);
            Width = 960; Height = 640; await Render();
            Need(transports.All(button => button.IsVisible), "Minimum window hid playback controls.");
            SaveLayoutImage(evidence, $"m3n-{page}-{language}-minimum.png", 1, images);
        }
        foreach (var locked in new[] { SessionState.Recording, SessionState.Paused, SessionState.Stopping })
        {
            state = locked; UpdateControls();
            Need(buttons.All(button => !button.IsEnabled) && !view.PlayListeningButton.IsEnabled && !view.PauseListeningButton.IsEnabled &&
                !view.StopListeningButton.IsEnabled, "Recording/paused/saving state did not lock both transport bars.");
        }
        state = SessionState.Idle; ResetTranscriptPlayback(); UpdateControls();
        Need(buttons.All(button => !button.IsEnabled), "Unopened media allows an invalid skip.");
        return new { Status = "PASS", Version = "M5E Pro", Jumps = jumps, PlayingSpeedPreserved = true, SubtitleFollows = true,
            BothPlayButtonsResume = resumeChecks, PauseIconsStayDoubleBar = true, SkipDigitInkCentered = true,
            NormalFocusPressedNoClipping = true, ThreeLanguagesTwoSizes = true, ArrowShortcuts = true,
            Images = images, AudioPlayback = "muted generated silence", PhysicalKeyboardMouse = "NOT_RUN", CaptureStarted = false, MicrophoneCollected = false };
        async Task Render() => await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
        KeyEventArgs KeyArgs(Key key) => new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        async Task PressArrow(Key key)
        {
            var args = KeyArgs(key); ReplaySegmentsBox.RaiseEvent(args); Need(args.Handled, "Replay arrow key was not handled.");
            await Task.Delay(100, token); UpdatePlaybackPosition();
        }
    }
}
