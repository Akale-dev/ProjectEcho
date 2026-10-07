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
    private bool keepPausedReplaySelection;
    private double playbackSpeed = 1;
    private (string Directory, long Milliseconds)? savedMixPlaybackRequest;
    private string? lastSavedMixReadinessText;
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
        if (!canPlay) ClearSavedMixPlaybackRequest();
        PausePlaybackButton.IsEnabled = canPlay && playbackOpened;
        ReplayPositionSlider.IsEnabled = canPlay && playbackOpened;
        PlaybackSpeedBox.IsEnabled = canPlay;
        foreach (var button in SkipPlaybackButtons)
        {
            button.IsEnabled = canPlay && playbackOpened;
            int seconds = int.Parse((string)button.Tag, CultureInfo.InvariantCulture);
            string label = seconds < 0 ? UiText.F($"回退{Math.Abs(seconds)}秒") : UiText.F($"前进{seconds}秒");
            button.ToolTip = label; AutomationProperties.SetName(button, label);
        }
        ReplaySegmentsBox.IsEnabled = canPlay;
        ReplaySegmentsBox.ToolTip = UiText.T("↑/↓选择字幕；播放中会从新选中字幕开始。←/→跳转5秒并保持播放或暂停状态。");
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
            view.PlayListeningButton.IsEnabled = canPlay && (mixedPath is not null || CanRequestSavedMixPlayback);
            view.PauseListeningButton.IsEnabled = canPlay && playbackOpened;
            view.StopListeningButton.IsEnabled = canPlay;
            view.PlaybackPositionSlider.IsEnabled = canPlay && playbackOpened;
            view.PlaybackSpeedBox.IsEnabled = canPlay;
        }
        UpdatePlaybackTransport();
        // All consumers share the one playback session, including full-file play.
        if (!canPlay && (player.Source is not null || pendingTranscriptSeek is not null)) ResetTranscriptPlayback();
        UpdateSavedMixReadinessDisplay();
    }

    private bool IsLoadingSavedMix(string directory) => savedMixCancellation is { IsCancellationRequested: false } &&
        string.Equals(savedMixLoadingDirectory, directory, StringComparison.OrdinalIgnoreCase);

    private bool CanRequestSavedMixPlayback => !WindowCloseRequested && !deletingMeeting && SessionRules.CanPlayback(state) &&
        mixedPath is null && selectedMeetingDirectory is { } directory && IsLoadingSavedMix(directory);

    // Remember only the latest explicit play/seek request. A pending validation is
    // never itself permission to play, and every capture/navigation boundary clears it.
    private bool TryQueueSavedMixPlayback(long milliseconds, string? directory = null)
    {
        directory ??= selectedMeetingDirectory;
        if (!CanRequestSavedMixPlayback || directory is null ||
            !string.Equals(directory, selectedMeetingDirectory, StringComparison.OrdinalIgnoreCase)) return false;
        savedMixPlaybackRequest = (directory, milliseconds);
        UpdateSavedMixReadinessDisplay();
        if (transcriptionView is { } view) view.StatusText.Text = UiText.T("正在校验回听文件；完成后将播放，无需重复点击。");
        return true;
    }

    private void ClearSavedMixPlaybackRequest() => savedMixPlaybackRequest = null;

    private void CompleteSavedMixPlaybackRequest(string directory)
    {
        if (savedMixPlaybackRequest is not { } request ||
            !string.Equals(request.Directory, directory, StringComparison.OrdinalIgnoreCase)) return;
        savedMixPlaybackRequest = null;
        if (WindowCloseRequested || deletingMeeting || !SessionRules.CanPlayback(state) ||
            !string.Equals(selectedMeetingDirectory, directory, StringComparison.OrdinalIgnoreCase) ||
            mixedPath is null || currentMix is null) return;
        try
        {
            if (request.Milliseconds < 0 || request.Milliseconds >= currentMix.DurationUs / 1000)
                throw new InvalidDataException(UiText.T("这条文字时间不在可回听范围内。"));
            RejectReparseAncestors(mixedPath);
            OpenListeningPosition(mixedPath, request.Milliseconds, currentMix.DurationUs, directory);
        }
        catch (Exception ex)
        {
            ResetTranscriptPlayback();
            string message = UiText.T("回放未启动：") + ex.Message;
            ReplayStatusText.Text = message;
            if (transcriptionView is { } view) view.StatusText.Text = message;
            LogMeeting(directory, "diagnostic", message, "error");
        }
    }

    private string? SavedMixReadinessText => selectedMeetingDirectory is not { } directory || mixedPath is not null ? null :
        IsLoadingSavedMix(directory)
            ? UiText.T(savedMixPlaybackRequest is null
                ? "正在校验回听文件；可点击播放，校验完成后开始。"
                : "正在校验回听文件；完成后将播放，无需重复点击。")
            : currentMix is not null ? UiText.T("完整 WAV 已校验；此超大文件暂不支持程序内回听，可按需导出 M4A。")
            : savedMixStatuses.ContainsKey(directory) ? UiText.T("回听文件尚未就绪；请查看本会议 Log 或重新生成混音。") : null;

    private void UpdateSavedMixReadinessDisplay()
    {
        string? status = SavedMixReadinessText;
        if (!playbackOpened && status is not null) CurrentSubtitleText.Text = status;
        else if (!playbackOpened && lastSavedMixReadinessText is { } previous && CurrentSubtitleText.Text == previous)
            CurrentSubtitleText.Text = UiText.T("播放时在这里显示当前字幕");
        lastSavedMixReadinessText = status;
        string help = status ?? "";
        AutomationProperties.SetHelpText(PlaybackButton, help);
        if (transcriptionView is { } view) AutomationProperties.SetHelpText(view.PlayListeningButton, help);
    }

    private void UpdatePlaybackTransport()
    {
        PauseReplayIcon.Data = ReplayPauseGeometry;
        string action = playbackPaused ? UiText.T("继续回放") : UiText.T("暂停回放");
        PausePlaybackButton.ToolTip = action;
        AutomationProperties.SetName(PausePlaybackButton, action);
        if (transcriptionView is { } view)
        {
            view.PauseListeningIcon.Data = ReplayPauseGeometry;
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
        ReplayStatusText.Text = UiText.T("尚无字幕也能回听；可在“转写与字幕”导入字幕或运行本地识别。");
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
            ReplayStatusText.Text = UiText.T("尚无字幕。可直接播放，或前往“转写与字幕”生成 / 导入文字。");
        }
        else
        {
            var segments = row.Revision.Segments.Where(segment => segment.StartMs is not null && segment.EndMs is not null)
                .OrderBy(segment => segment.StartMs).Select(segment => new TranscriptRow(directory, row.Revision, segment, UiText.T("本场"))).ToArray();
            ReplaySegmentsBox.ItemsSource = segments;
            ReplayStatusText.Text = segments.Length == 0 ? UiText.T("本版文字没有时间戳，不会猜测字幕位置。可切换带时间的版本。")
                : UiText.F($"{segments.Length} 条字幕 · {row.Revision.Provider} · 点击字幕跳转，字幕时间以本场录音为准。");
            int untimed = row.Revision.Segments.Count(segment => segment.StartMs is null);
            if (untimed > 0) ReplayStatusText.Text += UiText.F($" 另有 {untimed} 段无时间文字，已保留在转写全文中。");
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

    private void OpenListeningPosition(string path, long milliseconds, long? durationUs = null, string? meetingDirectory = null)
    {
        if (WindowCloseRequested || deletingMeeting || !SessionRules.CanPlayback(state)) return;
        InvalidateReplaySubtitleFollow();
        path = Path.GetFullPath(path);
        if (transcriptOwnsPlayer && string.Equals(player.Source?.LocalPath, path, StringComparison.OrdinalIgnoreCase))
        {
            if (playbackOpened) { pendingTranscriptSeek = null; PlayTranscriptPosition(milliseconds); return; }
            pendingTranscriptSeek = (path, milliseconds); return;
        }
        ReplaceTranscriptPlayer(meetingDirectory);
        playbackDurationMs = durationUs is { } duration ? duration / 1000 : currentMix?.DurationUs / 1000;
        pendingTranscriptSeek = (path, milliseconds);
        player.Open(new Uri(path));
    }

    private void PausePlayback_Click(object sender, RoutedEventArgs e)
    {
        if (WindowCloseRequested || deletingMeeting || !SessionRules.CanPlayback(state) || !playbackOpened) return;
        if (playbackPaused) player.Play(); else player.Pause();
        playbackPaused = !playbackPaused;
        if (!playbackPaused) InvalidateReplaySubtitleFollow();
        UpdatePlaybackTransport();
        ResetDisplayPlaybackClock();
        UpdatePlaybackPosition();
    }

    private Button[] SkipPlaybackButtons => [SkipBack10Button, SkipBack5Button, SkipForward5Button, SkipForward10Button];

    private void SkipPlayback_Click(object sender, RoutedEventArgs e)
    {
        if (WindowCloseRequested || deletingMeeting || !SessionRules.CanPlayback(state) || !playbackOpened ||
            sender is not Button { IsEnabled: true, Tag: string tag } ||
            !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) || seconds is not (-10 or -5 or 5 or 10)) return;
        SkipPlaybackBy(seconds);
    }

    private void SkipPlaybackBy(int seconds)
    {
        if (WindowCloseRequested || deletingMeeting || !SessionRules.CanPlayback(state) || !playbackOpened) return;
        long current = (long)Math.Round(player.Position.TotalMilliseconds, MidpointRounding.AwayFromZero);
        long seek = Math.Clamp(current + seconds * 1000L, 0, Math.Max(0, (playbackDurationMs ?? 0) - 1));
        SeekPlaybackMilliseconds(seek);
        InvalidateReplaySubtitleFollow();
        ResetDisplayPlaybackClock();
        UpdatePlaybackPosition(); // Preserve playing/paused state and current speed.
    }

    private void ReplayShortcut_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None || !ReplayWorkspace.IsVisible ||
            WindowCloseRequested || deletingMeeting || !SessionRules.CanPlayback(state)) return;
        for (DependencyObject? target = e.OriginalSource as DependencyObject; target is not null;)
        {
            if (target is System.Windows.Controls.Primitives.TextBoxBase or ComboBox or ComboBoxItem or PasswordBox) return;
            if (ReferenceEquals(target, ReplayWorkspace)) break;
            target = target is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(target) : LogicalTreeHelper.GetParent(target);
        }
        if (e.Key is Key.Left or Key.Right)
        {
            if (!playbackOpened) return;
            SkipPlaybackBy(e.Key == Key.Left ? -5 : 5); e.Handled = true; return;
        }
        if (e.Key is not (Key.Up or Key.Down) || ReplaySegmentsBox.Items.Count == 0) return;
        int current = ReplaySegmentsBox.SelectedIndex;
        int next = current < 0 ? 0 : Math.Clamp(current + (e.Key == Key.Up ? -1 : 1), 0, ReplaySegmentsBox.Items.Count - 1);
        e.Handled = true;
        if (next == current) return;
        ReplaySegmentsBox.SelectedIndex = next;
        ReplaySegmentsBox.ScrollIntoView(ReplaySegmentsBox.SelectedItem);
        if (playbackOpened && !playbackPaused && ReplaySegmentsBox.SelectedItem is TranscriptRow row)
        {
            if (row.Segment.StartMs is not null) SeekTranscriptRow(row);
            else ReplayStatusText.Text = UiText.T("这条字幕没有可用时间，无法定位回听。");
        }
        else keepPausedReplaySelection = true;
    }

    private void ReplayPosition_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingPlaybackPosition || !initialized || !playbackOpened || !SessionRules.CanPlayback(state) || WindowCloseRequested || deletingMeeting) return;
        // A decimal slider value such as 1.38 may multiply to 1379.9999999999998.
        // Quantize to the existing transcript millisecond clock before passing it
        // to MediaPlayer, which otherwise truncates and selects the preceding cue.
        long requestedMs = (long)Math.Round(e.NewValue * 1000, MidpointRounding.AwayFromZero);
        long seekMs = Math.Clamp(requestedMs, 0, Math.Max(0, (playbackDurationMs ?? 0) - 1));
        SeekPlaybackMilliseconds(seekMs);
        InvalidateReplaySubtitleFollow();
        ResetDisplayPlaybackClock();
        UpdatePlaybackPosition();
    }

    private void SeekPlaybackMilliseconds(long milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        player.Position = TimeSpan.FromTicks(checked(milliseconds * TimeSpan.TicksPerMillisecond));
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
        keepPausedReplaySelection = false;
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
        string time = $"{TimelineTimestamp.FromMilliseconds((long)milliseconds)} / {TimelineTimestamp.FromMilliseconds(duration)}";
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
            : playbackOpened ? UiText.T("此处没有字幕") : SavedMixReadinessText ?? UiText.T("播放时在这里显示当前字幕");
        if (CurrentSubtitleText.Text != subtitle) CurrentSubtitleText.Text = subtitle;
        // Rows are ordered by start time. With overlapping cues, select the most
        // recently started one while retaining every active cue in the text display.
        if (!keepPausedReplaySelection || (playbackOpened && !playbackPaused)) FollowReplaySubtitle(active.LastOrDefault());
    }
}
