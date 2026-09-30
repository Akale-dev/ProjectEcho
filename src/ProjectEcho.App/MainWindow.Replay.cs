using System.IO;
using System.Globalization;
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
    private readonly DispatcherTimer replayTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Task replayLoadWork = Task.CompletedTask;
    private int replayLoadGeneration;
    private string? replayMeetingDirectory;
    private bool updatingReplay, updatingPlaybackPosition, playbackPaused;
    private long? playbackDurationMs;
    private long lastSubtitlePositionMs = -1;
    private object? lastSubtitleItems;
    private string? lastSubtitleMedia;
    private double playbackSpeed = 1;
    private static readonly Geometry ReplayPlayGeometry = Geometry.Parse("M 2 0 L 15 8 L 2 16 Z");
    private static readonly Geometry ReplayPauseGeometry = Geometry.Parse("M 1 0 H 6 V 16 H 1 Z M 10 0 H 15 V 16 H 10 Z");

    private void InitializeReplay()
    {
        replayTimer.Tick += (_, _) => UpdatePlaybackPosition();
        replayTimer.Start();
        Closed += (_, _) => replayTimer.Stop();
    }

    private void UpdateReplayControls()
    {
        bool canPlay = !WindowCloseRequested && SessionRules.CanPlayback(state);
        PausePlaybackButton.IsEnabled = canPlay && playbackOpened;
        ReplayPositionSlider.IsEnabled = canPlay && playbackOpened;
        PlaybackSpeedBox.IsEnabled = canPlay;
        ReplaySegmentsBox.IsEnabled = canPlay;
        ReplayRevisionBox.IsEnabled = !WindowCloseRequested && !transcriptBusy;
        ReplayExportSubtitlesButton.IsEnabled = !WindowCloseRequested && !transcriptBusy
            && replayMeetingDirectory == selectedMeetingDirectory
            && ReplayRevisionBox.SelectedItem is RevisionRow { Revision.Segments.Count: > 0 } revision
            && revision.Revision.Segments.All(segment => segment.StartMs is not null && segment.EndMs is not null);
        UpdatePlaybackTransport();
        // All consumers share the one playback session, including full-file play.
        if (!canPlay && (player.Source is not null || pendingTranscriptSeek is not null)) ResetTranscriptPlayback();
    }

    private void UpdatePlaybackTransport()
    {
        PauseReplayIcon.Data = playbackPaused ? ReplayPlayGeometry : ReplayPauseGeometry;
        string action = playbackPaused ? "继续回放" : "暂停回放";
        PausePlaybackButton.ToolTip = action;
        AutomationProperties.SetName(PausePlaybackButton, action);
        // A new MediaPlayer is created for each listening file. Keep the explicit
        // rate choice across opens while all subtitle positions stay media time.
        if (player.SpeedRatio != playbackSpeed) player.SpeedRatio = playbackSpeed;
    }

    private void PlaybackSpeed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || PlaybackSpeedBox.SelectedItem is not ComboBoxItem { Tag: string value }
            || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed)
            || speed is not (0.5 or 0.75 or 1 or 1.25 or 1.5 or 2)) return;
        playbackSpeed = speed;
        UpdatePlaybackTransport();
    }

    private void ClearReplaySubtitles()
    {
        replayLoadGeneration++;
        replayMeetingDirectory = null;
        updatingReplay = true;
        ReplayRevisionBox.ItemsSource = null;
        ReplaySegmentsBox.ItemsSource = null;
        updatingReplay = false;
        CurrentSubtitleText.Text = "播放时在这里显示当前字幕";
        ReplayStatusText.Text = "尚无字幕也能回听；可在“转写与搜索”导入字幕或运行本地识别。";
    }

    private void EnsureMeetingSubtitles()
    {
        if (selectedMeetingDirectory is not { } directory || WindowCloseRequested || replayMeetingDirectory == directory) return;
        replayLoadWork = LoadReplaySubtitlesAsync(directory);
    }

    private void RefreshReplay_Click(object sender, RoutedEventArgs e)
    {
        if (selectedMeetingDirectory is { } directory && !WindowCloseRequested)
            replayLoadWork = LoadReplaySubtitlesAsync(directory);
    }

    private void ExportReplaySubtitles_Click(object sender, RoutedEventArgs e)
    {
        if (!ReplayExportSubtitlesButton.IsEnabled || WindowCloseRequested || transcriptBusy) return;
        var picker = new SaveFileDialog
        {
            Title = "保存当前回听版本的完整字幕", Filter = "SubRip 字幕|*.srt|WebVTT 字幕|*.vtt",
            FileName = "会议字幕.srt", DefaultExt = ".srt", AddExtension = true, OverwritePrompt = true
        };
        if (picker.ShowDialog(this) == true) StartTranscriptWork(() => ExportReplaySubtitlesAsync(picker.FileName));
    }

    private async Task ExportReplaySubtitlesAsync(string destinationPath)
    {
        if (ReplayRevisionBox.SelectedItem is not RevisionRow row || replayMeetingDirectory is not { } directory
            || directory != selectedMeetingDirectory) throw new InvalidOperationException("先选择当前会议的带时间字幕版本。");
        directory = ValidateRecordingDirectory(directory);
        string revisionId = row.Revision.RevisionId;
        var token = transcriptCancellation?.Token ?? CancellationToken.None;
        try
        {
            string result = await Task.Run(() => TranscriptSubtitleExporter.Export(directory, revisionId, destinationPath, token, overwrite: true), token);
            if (selectedMeetingDirectory == directory && ReplayRevisionBox.SelectedItem is RevisionRow selected && selected.Revision.RevisionId == revisionId)
                ReplayStatusText.Text = "当前版本的完整字幕已保存：" + result;
        }
        catch (Exception ex)
        {
            if (selectedMeetingDirectory == directory) ReplayStatusText.Text = "字幕未导出，可重试：" + ex.Message;
            throw;
        }
    }

    private async Task LoadReplaySubtitlesAsync(string directory)
    {
        int generation = ++replayLoadGeneration;
        try
        {
            ValidateRecordingDirectory(directory);
            var revisions = await Task.Run(() => TranscriptService.ListRevisions(directory));
            if (WindowCloseRequested || generation != replayLoadGeneration || directory != selectedMeetingDirectory) return;
            SetReplayRevisions(directory, revisions, null);
        }
        catch (Exception ex)
        {
            if (!WindowCloseRequested && generation == replayLoadGeneration)
                ReplayStatusText.Text = "字幕未加载，可重试：" + ex.Message;
        }
    }

    private void SetReplayRevisions(string directory, IEnumerable<TranscriptRevision> revisions, string? preferred)
    {
        if (directory != selectedMeetingDirectory) return;
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
        if (ReplayRevisionBox.SelectedItem is not RevisionRow row || replayMeetingDirectory is not { } directory)
        {
            ReplaySegmentsBox.ItemsSource = null;
            ReplayStatusText.Text = "尚无字幕。可直接播放，或前往“转写与搜索”生成 / 导入文字。";
        }
        else
        {
            var segments = row.Revision.Segments.Where(segment => segment.StartMs is not null && segment.EndMs is not null)
                .OrderBy(segment => segment.StartMs).Select(segment => new TranscriptRow(directory, row.Revision, segment, "本场")).ToArray();
            ReplaySegmentsBox.ItemsSource = segments;
            ReplayStatusText.Text = segments.Length == 0 ? "本版文字没有时间戳，不会猜测字幕位置。可切换带时间的版本。"
                : $"{segments.Length} 条字幕 · {row.Revision.Provider} · 点击字幕跳转，暂停和空白时段保持原始时间轴。";
        }
        UpdatePlaybackPosition();
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
        if (e.Key != Key.Enter || ReplaySegmentsBox.SelectedItem is not TranscriptRow row) return;
        SeekTranscriptRow(row); e.Handled = true;
    }

    private void OpenListeningPosition(string path, long milliseconds, long? durationUs = null)
    {
        if (WindowCloseRequested || !SessionRules.CanPlayback(state)) return;
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
        if (WindowCloseRequested || !SessionRules.CanPlayback(state) || !playbackOpened) return;
        if (playbackPaused) player.Play(); else player.Pause();
        playbackPaused = !playbackPaused;
        UpdatePlaybackTransport();
    }

    private void ReplayPosition_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingPlaybackPosition || !initialized || !playbackOpened || !SessionRules.CanPlayback(state) || WindowCloseRequested) return;
        player.Position = TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(e.NewValue * 1000, Math.Max(0, (playbackDurationMs ?? 0) - 1))));
        UpdatePlaybackPosition();
    }

    private void UpdatePlaybackPosition()
    {
        if (!initialized) return;
        double milliseconds = playbackOpened ? player.Position.TotalMilliseconds : 0;
        long duration = playbackDurationMs ?? (playbackOpened && player.NaturalDuration.HasTimeSpan ? (long)player.NaturalDuration.TimeSpan.TotalMilliseconds : currentMix?.DurationUs / 1000 ?? 0);
        updatingPlaybackPosition = true;
        ReplayPositionSlider.Maximum = Math.Max(0.001, duration / 1000d);
        ReplayPositionSlider.Value = Math.Clamp(milliseconds / 1000d, 0, ReplayPositionSlider.Maximum);
        updatingPlaybackPosition = false;
        ReplayTimeText.Text = $"{TimeSpan.FromMilliseconds(milliseconds):hh\\:mm\\:ss} / {TimeSpan.FromMilliseconds(duration):hh\\:mm\\:ss}";
        if (MainTabs.SelectedIndex != 1 || MeetingTabs.SelectedIndex != 2) { UpdateReplayControls(); return; }
        var media = playbackOpened ? player.Source?.LocalPath : null;
        long position = (long)milliseconds;
        if (position == lastSubtitlePositionMs && ReferenceEquals(lastSubtitleItems, ReplaySegmentsBox.ItemsSource) && lastSubtitleMedia == media)
        { UpdateReplayControls(); return; }
        lastSubtitlePositionMs = position; lastSubtitleItems = ReplaySegmentsBox.ItemsSource; lastSubtitleMedia = media;
        var active = playbackOpened && replayMeetingDirectory == selectedMeetingDirectory && mixedPath is not null
            && string.Equals(player.Source?.LocalPath, Path.GetFullPath(mixedPath), StringComparison.OrdinalIgnoreCase)
            ? ReplaySegmentsBox.Items.Cast<TranscriptRow>().Where(row => row.Segment.StartMs <= milliseconds && row.Segment.EndMs > milliseconds).ToArray() : [];
        CurrentSubtitleText.Text = active.Length > 0 ? string.Join("\n", active.Select(row => row.Segment.Text))
            : playbackOpened ? "此处没有字幕" : "播放时在这里显示当前字幕";
        UpdateReplayControls();
    }
}
