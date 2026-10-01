using System.IO;
using System.Globalization;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ProjectEcho.Core;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private Task replayLoadWork = Task.CompletedTask;
    private readonly DispatcherTimer replayTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch displayPlaybackClock = Stopwatch.StartNew();
    private double displayAnchorMs, displayAnchorAtMs, displayLastNativeMs, displayLastNativeAtMs, displayLastMs;
    private int replayLoadGeneration;
    private string? replayMeetingDirectory;
    private bool updatingReplay, updatingPlaybackPosition, playbackPaused;
    private long? playbackDurationMs;
    private long lastSubtitlePositionMs = -1;
    private object? lastSubtitleItems;
    private string? lastSubtitleMedia;
    private double nextSubtitleBoundaryMs;
    private bool revealReplaySubtitleOnNextUpdate;
    private bool updatingPlaybackTransport;
    private double playbackSpeed = 1;
    private static readonly Geometry ReplayPlayGeometry = Geometry.Parse("M 2 0 L 15 8 L 2 16 Z");
    private static readonly Geometry ReplayPauseGeometry = Geometry.Parse("M 1 0 H 6 V 16 H 1 Z M 10 0 H 15 V 16 H 10 Z");

    private void InitializeReplay()
    {
        // The Windows media position can update only a few times per second. This
        // UI-only clock drives the thumb; real media time remains the subtitle authority.
        replayTimer.Tick += ReplayRendering;
        replayTimer.Start();
        Closed += (_, _) => replayTimer.Stop();
        // Selection is display/copy state only. Seeking remains confined to the
        // explicit mouse/Enter handlers, so playback following cannot restart audio.
        ReplaySegmentsBox.SelectionChanged += (_, _) => ReplayCopyButton.IsEnabled = !WindowCloseRequested && ReplaySegmentsBox.SelectedItem is TranscriptRow;
        ReplaySegmentsBox.IsVisibleChanged += (_, _) =>
        {
            if (!ReplaySegmentsBox.IsVisible) return;
            InvalidateReplaySubtitleFollow();
            UpdatePlaybackPosition();
        };
    }

    private void ReplayRendering(object? sender, EventArgs e)
    {
        if (playbackOpened && MainTabs.SelectedIndex == 1 && MeetingTabs.SelectedIndex is 2 or 3)
            UpdatePlaybackPosition();
    }

    private void UpdateReplayControls()
    {
        bool canPlay = !WindowCloseRequested && !deletingMeeting && SessionRules.CanPlayback(state);
        PausePlaybackButton.IsEnabled = canPlay && playbackOpened;
        ReplayPositionSlider.IsEnabled = canPlay && playbackOpened;
        PlaybackSpeedBox.IsEnabled = canPlay;
        ReplaySegmentsBox.IsEnabled = canPlay;
        ReplayRevisionBox.IsEnabled = !WindowCloseRequested && !deletingMeeting && !transcriptBusy;
        ReplayExportSubtitlesButton.IsEnabled = !WindowCloseRequested && !deletingMeeting && !transcriptBusy
            && replayMeetingDirectory == selectedMeetingDirectory
            && ReplayRevisionBox.SelectedItem is RevisionRow { Revision.Segments.Count: > 0 } revision
            && revision.Revision.Segments.All(segment => segment.StartMs is not null && segment.EndMs is not null);
        ReplayExportTextButton.IsEnabled = !WindowCloseRequested && !deletingMeeting && !transcriptBusy
            && replayMeetingDirectory == selectedMeetingDirectory && ReplayRevisionBox.SelectedItem is RevisionRow { Revision.Segments.Count: > 0 };
        ReplayCopyButton.IsEnabled = !WindowCloseRequested && ReplaySegmentsBox.SelectedItem is TranscriptRow;
        if (transcriptionView is { } view)
        {
            view.PlayListeningButton.IsEnabled = canPlay && mixedPath is not null;
            view.PauseListeningButton.IsEnabled = canPlay && playbackOpened;
            view.PlaybackPositionSlider.IsEnabled = canPlay && playbackOpened;
            view.PlaybackSpeedBox.IsEnabled = canPlay;
        }
        UpdatePlaybackTransport();
        // All consumers share the one playback session, including full-file play.
        if (!canPlay && (player.Source is not null || pendingTranscriptSeek is not null)) ResetTranscriptPlayback();
    }

    private void UpdatePlaybackTransport()
    {
        PauseReplayIcon.Data = playbackPaused ? ReplayPlayGeometry : ReplayPauseGeometry;
        string action = playbackPaused ? UiText.T("继续回放") : UiText.T("暂停回放");
        PausePlaybackButton.ToolTip = action;
        AutomationProperties.SetName(PausePlaybackButton, action);
        if (transcriptionView is { } view)
        {
            view.PauseListeningIcon.Data = playbackPaused ? ReplayPlayGeometry : ReplayPauseGeometry;
            view.PauseListeningButton.ToolTip = action;
            AutomationProperties.SetName(view.PauseListeningButton, action);
        }
        // A new MediaPlayer is created for each listening file. Keep the explicit
        // rate choice across opens while all subtitle positions stay media time.
        if (player.SpeedRatio != playbackSpeed) player.SpeedRatio = playbackSpeed;
        updatingPlaybackTransport = true;
        try
        {
            foreach (var box in transcriptionView is { } current ? new[] { PlaybackSpeedBox, current.PlaybackSpeedBox } : new[] { PlaybackSpeedBox })
                box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag is string value &&
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed) && speed == playbackSpeed);
        }
        finally { updatingPlaybackTransport = false; }
    }

    private void PlaybackSpeed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || updatingPlaybackTransport || sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string value } }
            || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed)
            || speed is not (0.5 or 0.75 or 1 or 1.25 or 1.5 or 2)) return;
        playbackSpeed = speed;
        UpdatePlaybackTransport();
        ResetDisplayPlaybackClock();
    }

    private void ClearReplaySubtitles()
    {
        replayLoadGeneration++;
        replayMeetingDirectory = null;
        updatingReplay = true;
        ReplayRevisionBox.ItemsSource = null;
        ReplaySegmentsBox.ItemsSource = null;
        updatingReplay = false;
        CurrentSubtitleText.Text = UiText.T("播放时在这里显示当前字幕");
        ReplayStatusText.Text = UiText.T("尚无字幕也能回听；可在“转写与搜索”导入字幕或运行本地识别。");
    }

    private void EnsureMeetingSubtitles()
    {
        if (selectedMeetingDirectory is not { } directory || WindowCloseRequested || deletingMeeting || replayMeetingDirectory == directory) return;
        replayLoadWork = LoadReplaySubtitlesAsync(directory);
    }

    private void RefreshReplay_Click(object sender, RoutedEventArgs e)
    {
        if (selectedMeetingDirectory is { } directory && !WindowCloseRequested && !deletingMeeting)
            replayLoadWork = LoadReplaySubtitlesAsync(directory);
    }

    private void ExportReplaySubtitles_Click(object sender, RoutedEventArgs e)
    {
        if (!ReplayExportSubtitlesButton.IsEnabled || WindowCloseRequested || transcriptBusy) return;
        var picker = new SaveFileDialog
        {
            Title = UiText.T("保存当前回听版本的完整字幕"), Filter = UiText.T("SubRip 字幕|*.srt|WebVTT 字幕|*.vtt"),
            FileName = UiText.T("会议字幕.srt"), DefaultExt = ".srt", AddExtension = true, OverwritePrompt = true
        };
        if (picker.ShowDialog(this) == true) StartTranscriptWork(() => ExportReplaySubtitlesAsync(picker.FileName));
    }

    private async Task ExportReplaySubtitlesAsync(string destinationPath)
    {
        if (ReplayRevisionBox.SelectedItem is not RevisionRow row || replayMeetingDirectory is not { } directory
            || directory != selectedMeetingDirectory) throw new InvalidOperationException(UiText.T("先选择当前会议的带时间字幕版本。"));
        directory = ValidateRecordingDirectory(directory);
        string revisionId = row.Revision.RevisionId;
        var token = transcriptCancellation?.Token ?? CancellationToken.None;
        try
        {
            string result = await Task.Run(() => TranscriptSubtitleExporter.Export(directory, revisionId, destinationPath, token, overwrite: true), token);
            if (selectedMeetingDirectory == directory && ReplayRevisionBox.SelectedItem is RevisionRow selected && selected.Revision.RevisionId == revisionId)
                ReplayStatusText.Text = UiText.T("当前版本的完整字幕已保存：") + result;
        }
        catch (Exception ex)
        {
            if (selectedMeetingDirectory == directory) ReplayStatusText.Text = UiText.T("字幕未导出，可重试：") + ex.Message;
            throw;
        }
    }

    private void ExportReplayText_Click(object sender, RoutedEventArgs e)
    {
        if (!ReplayExportTextButton.IsEnabled || WindowCloseRequested || transcriptBusy) return;
        var picker = new SaveFileDialog
        {
            Title = UiText.T("保存当前回听版本全文（不带时间轴）"), Filter = UiText.T("UTF-8 全文|*.txt"), FileName = UiText.T("会议全文.txt"),
            DefaultExt = ".txt", AddExtension = true, OverwritePrompt = true
        };
        if (picker.ShowDialog(this) == true) StartTranscriptWork(() => ExportReplayTextAsync(picker.FileName));
    }

    private async Task ExportReplayTextAsync(string destinationPath)
    {
        if (ReplayRevisionBox.SelectedItem is not RevisionRow row || replayMeetingDirectory is not { } directory || directory != selectedMeetingDirectory)
            throw new InvalidOperationException(UiText.T("先选择当前会议的文字版本。"));
        directory = ValidateRecordingDirectory(directory);
        string revisionId = row.Revision.RevisionId;
        var token = transcriptCancellation?.Token ?? CancellationToken.None;
        string result = await Task.Run(() => TranscriptSubtitleExporter.ExportText(directory, revisionId, destinationPath, token, overwrite: true), token);
        if (selectedMeetingDirectory == directory) ReplayStatusText.Text = UiText.T("当前版本全文已保存（不带时间轴）：") + result;
    }

    private void CopyReplaySegment_Click(object sender, RoutedEventArgs e)
    {
        if (!WindowCloseRequested && ReplaySegmentsBox.SelectedItem is TranscriptRow row)
            CopySegmentText(row, message => ReplayStatusText.Text = message);
    }

    private async Task LoadReplaySubtitlesAsync(string directory)
    {
        if (deletingMeeting) return;
        int generation = ++replayLoadGeneration;
        try
        {
            ValidateRecordingDirectory(directory);
            var revisions = await Task.Run(() => TranscriptService.ListRevisions(directory));
            if (WindowCloseRequested || deletingMeeting || generation != replayLoadGeneration || directory != selectedMeetingDirectory) return;
            SetReplayRevisions(directory, revisions, null);
        }
        catch (Exception ex)
        {
            if (!WindowCloseRequested && generation == replayLoadGeneration)
                ReplayStatusText.Text = UiText.T("字幕未加载，可重试：") + ex.Message;
        }
    }

    private void SetReplayRevisions(string directory, IEnumerable<TranscriptRevision> revisions, string? preferred)
    {
        if (deletingMeeting || directory != selectedMeetingDirectory) return;
        // Reading versions in the transcription tab must not replace a version
        // the user explicitly chose for listening in this same meeting.
        preferred ??= replayMeetingDirectory == directory && ReplayRevisionBox.SelectedItem is RevisionRow selected
            ? selected.Revision.RevisionId : null;
        replayLoadGeneration++;
        replayMeetingDirectory = directory;
        var rows = revisions.OrderByDescending(revision => revision.ImportedAt).Select(revision => new RevisionRow(revision)).ToArray();
        updatingReplay = true;
        ReplayRevisionBox.ItemsSource = rows;
        // Prefer a timed version for listening if the latest import is untimed.
        ReplayRevisionBox.SelectedItem = rows.FirstOrDefault(row => row.Revision.RevisionId == preferred)
            ?? rows.FirstOrDefault(row => row.Revision.Segments.Any(segment => segment.StartMs is not null)) ?? rows.FirstOrDefault();
        updatingReplay = false;
        DisplayReplayRevision();
    }

    private void ReplayRevision_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingReplay && initialized) DisplayReplayRevision();
    }

    private void DisplayReplayRevision()
    {
        InvalidateReplaySubtitleFollow();
        if (ReplayRevisionBox.SelectedItem is not RevisionRow row || replayMeetingDirectory is not { } directory)
        {
            ReplaySegmentsBox.ItemsSource = null;
            ReplayStatusText.Text = UiText.T("尚无字幕。可直接播放，或前往“转写与搜索”生成 / 导入文字。");
        }
        else
        {
            var segments = row.Revision.Segments.Where(segment => segment.StartMs is not null && segment.EndMs is not null)
                .OrderBy(segment => segment.StartMs).Select(segment => new TranscriptRow(directory, row.Revision, segment, UiText.T("本场"))).ToArray();
            ReplaySegmentsBox.ItemsSource = segments;
            ReplayStatusText.Text = segments.Length == 0 ? UiText.T("本版文字没有时间戳，不会猜测字幕位置。可切换带时间的版本。")
                : UiText.F($"{segments.Length} 条字幕 · {row.Revision.Provider} · 点击字幕跳转，暂停和空白时段保持原始时间轴。");
        }
        UpdatePlaybackPosition();
        UpdateReplayControls();
    }

    private void ReplaySegment_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { IsEnabled: true, DataContext: TranscriptRow row }) return;
        ReplaySegmentsBox.SelectedItem = row;
        SeekTranscriptRow(row);
        e.Handled = true;
    }

    private void ReplaySegment_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && ReplaySegmentsBox.SelectedItem is TranscriptRow)
        { CopyReplaySegment_Click(sender, e); e.Handled = true; return; }
        if (e.Key != Key.Enter || ReplaySegmentsBox.SelectedItem is not TranscriptRow row) return;
        SeekTranscriptRow(row); e.Handled = true;
    }

    private void OpenListeningPosition(string path, long milliseconds, long? durationUs = null)
    {
        if (WindowCloseRequested || deletingMeeting || !SessionRules.CanPlayback(state)) return;
        InvalidateReplaySubtitleFollow();
        path = Path.GetFullPath(path);
        if (transcriptOwnsPlayer && string.Equals(player.Source?.LocalPath, path, StringComparison.OrdinalIgnoreCase))
        {
            if (playbackOpened) { pendingTranscriptSeek = null; PlayTranscriptPosition(milliseconds); return; }
            pendingTranscriptSeek = (path, milliseconds); return;
        }
        ReplaceTranscriptPlayer();
        playbackDurationMs = durationUs is { } duration ? duration / 1000 : currentMix?.DurationUs / 1000;
        pendingTranscriptSeek = (path, milliseconds);
        player.Open(new Uri(path));
    }

    private void PausePlayback_Click(object sender, RoutedEventArgs e)
    {
        if (WindowCloseRequested || deletingMeeting || !SessionRules.CanPlayback(state) || !playbackOpened) return;
        if (playbackPaused) player.Play(); else player.Pause();
        playbackPaused = !playbackPaused;
        UpdatePlaybackTransport();
        ResetDisplayPlaybackClock();
        UpdatePlaybackPosition();
    }

    private void ReplayPosition_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingPlaybackPosition || !initialized || !playbackOpened || !SessionRules.CanPlayback(state) || WindowCloseRequested || deletingMeeting) return;
        player.Position = TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(e.NewValue * 1000, Math.Max(0, (playbackDurationMs ?? 0) - 1))));
        InvalidateReplaySubtitleFollow();
        ResetDisplayPlaybackClock();
        UpdatePlaybackPosition();
    }

    private void ResetDisplayPlaybackClock()
    {
        displayAnchorMs = displayLastNativeMs = displayLastMs = playbackOpened ? player.Position.TotalMilliseconds : 0;
        displayAnchorAtMs = displayLastNativeAtMs = displayPlaybackClock.Elapsed.TotalMilliseconds;
    }

    private double DisplayPlaybackMilliseconds(double mediaMilliseconds)
    {
        if (!playbackOpened || playbackPaused) return mediaMilliseconds;
        double now = displayPlaybackClock.Elapsed.TotalMilliseconds;
        if (mediaMilliseconds != displayLastNativeMs)
        {
            double expected = displayAnchorMs + (now - displayAnchorAtMs) * playbackSpeed;
            if (mediaMilliseconds < displayLastNativeMs - 20 || Math.Abs(mediaMilliseconds - expected) > 500)
                displayLastMs = mediaMilliseconds; // Defensive resync after an external media-clock discontinuity.
            displayAnchorMs = mediaMilliseconds; displayAnchorAtMs = now;
            displayLastNativeMs = mediaMilliseconds; displayLastNativeAtMs = now;
        }
        // Bound interpolation if the decoder stops advancing; never run a fake
        // progress clock indefinitely during a stalled or ended media session.
        double extrapolation = Math.Min(now - displayAnchorAtMs, Math.Max(0, 350 - (displayAnchorAtMs - displayLastNativeAtMs)));
        double displayed = displayAnchorMs + extrapolation * playbackSpeed;
        displayLastMs = Math.Max(displayLastMs, displayed);
        return displayLastMs;
    }

    private void InvalidateReplaySubtitleFollow()
    {
        nextSubtitleBoundaryMs = 0;
        revealReplaySubtitleOnNextUpdate = true;
    }

    private void FollowReplaySubtitle(TranscriptRow? row)
    {
        bool changed = !ReferenceEquals(ReplaySegmentsBox.SelectedItem, row);
        if (changed) ReplaySegmentsBox.SelectedItem = row;
        // No Focus, Seek, list rebuild, or per-frame scrolling. A user can inspect
        // another row while paused; the next seek/active sentence resumes following.
        if (row is not null && (changed || revealReplaySubtitleOnNextUpdate)) ReplaySegmentsBox.ScrollIntoView(row);
        revealReplaySubtitleOnNextUpdate = false;
    }

    private void UpdatePlaybackPosition()
    {
        if (!initialized) return;
        double milliseconds = playbackOpened ? player.Position.TotalMilliseconds : 0;
        double displayMilliseconds = DisplayPlaybackMilliseconds(milliseconds);
        long duration = playbackDurationMs ?? (playbackOpened && player.NaturalDuration.HasTimeSpan ? (long)player.NaturalDuration.TimeSpan.TotalMilliseconds : currentMix?.DurationUs / 1000 ?? 0);
        updatingPlaybackPosition = true;
        try
        {
            foreach (var slider in transcriptionView is { } view ? new[] { ReplayPositionSlider, view.PlaybackPositionSlider } : new[] { ReplayPositionSlider })
            {
                slider.Maximum = Math.Max(0.001, duration / 1000d);
                // Do not pull a thumb away from a user's active drag.
                if (!slider.IsMouseCaptureWithin) slider.Value = Math.Clamp(displayMilliseconds / 1000d, 0, slider.Maximum);
            }
        }
        finally { updatingPlaybackPosition = false; }
        string time = $"{TimeSpan.FromMilliseconds(milliseconds):hh\\:mm\\:ss} / {TimeSpan.FromMilliseconds(duration):hh\\:mm\\:ss}";
        if (ReplayTimeText.Text != time) ReplayTimeText.Text = time;
        if (transcriptionView is { } transcript && transcript.PlaybackTimeText.Text != time) transcript.PlaybackTimeText.Text = time;
        if (MainTabs.SelectedIndex != 1 || MeetingTabs.SelectedIndex != 2) return;
        var media = playbackOpened ? player.Source?.LocalPath : null;
        long position = (long)milliseconds;
        if (position >= lastSubtitlePositionMs && position < nextSubtitleBoundaryMs &&
            ReferenceEquals(lastSubtitleItems, ReplaySegmentsBox.ItemsSource) && lastSubtitleMedia == media)
        { lastSubtitlePositionMs = position; return; }
        lastSubtitlePositionMs = position; lastSubtitleItems = ReplaySegmentsBox.ItemsSource; lastSubtitleMedia = media;
        var rows = ReplaySegmentsBox.Items.Cast<TranscriptRow>();
        nextSubtitleBoundaryMs = rows.SelectMany(row => new[] { row.Segment.StartMs ?? 0, row.Segment.EndMs ?? 0 })
            .Where(boundary => boundary > milliseconds).DefaultIfEmpty(long.MaxValue).Min();
        var active = playbackOpened && replayMeetingDirectory == selectedMeetingDirectory && mixedPath is not null
            && string.Equals(player.Source?.LocalPath, Path.GetFullPath(mixedPath), StringComparison.OrdinalIgnoreCase)
            ? rows.Where(row => row.Segment.StartMs <= milliseconds && row.Segment.EndMs > milliseconds).ToArray() : [];
        string subtitle = active.Length > 0 ? string.Join("\n", active.Select(row => row.Segment.Text))
            : playbackOpened ? UiText.T("此处没有字幕") : UiText.T("播放时在这里显示当前字幕");
        if (CurrentSubtitleText.Text != subtitle) CurrentSubtitleText.Text = subtitle;
        // Rows are ordered by start time. With overlapping cues, select the most
        // recently started one while retaining every active cue in the text display.
        FollowReplaySubtitle(active.LastOrDefault());
    }
}
