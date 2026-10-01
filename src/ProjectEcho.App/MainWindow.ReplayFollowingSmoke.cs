using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Called by the existing synthetic/muted --m3c-transcript-smoke workflow.</summary>
    private async Task<object> ReplayFollowingSmokeAsync(string directory, TranscriptRevision original, CancellationToken token)
    {
        M3cNeed(playbackOpened && playbackPaused && player.IsMuted && player.Volume == 0 && host is null && !microphoneEnabled,
            "字幕跟随验证必须使用已暂停的静音合成文件。");
        var media = player;
        string? mediaPath = player.Source?.LocalPath;
        double originalPosition = player.Position.TotalSeconds;
        var rows = ReplaySegmentsBox.Items.Cast<TranscriptRow>().ToArray();
        M3cNeed(rows.Length == 100, "字幕跟随验证需要100行合成字幕。");
        var late = rows[70];
        double seekSeconds = (late.Segment.StartMs!.Value + 100) / 1000d;
        ReplayPositionSlider.Value = seekSeconds;
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        AssertSelected(late.Segment.SegmentId, original.RevisionId, seekSeconds);
        AssertVisible();

        var scroll = FindFollowingScrollViewer(ReplaySegmentsBox) ?? throw new InvalidDataException("回听字幕滚动视口不存在。");
        scroll.ScrollToTop(); await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        double manualOffset = scroll.VerticalOffset;
        for (int i = 0; i < 30; i++) UpdatePlaybackPosition();
        await Task.Delay(80, token);
        M3cNeed(Math.Abs(scroll.VerticalOffset - manualOffset) < .01, "暂停后每帧滚动抢走了用户查看位置。");

        // A deliberate seek within the same sentence should reveal it again,
        // despite the selected item and active cue remaining unchanged.
        ReplayPositionSlider.Value = seekSeconds + .01;
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        AssertSelected(late.Segment.SegmentId, original.RevisionId, seekSeconds + .01); AssertVisible();

        double gapSeconds = (late.Segment.EndMs!.Value + 25) / 1000d;
        ReplayPositionSlider.Value = gapSeconds;
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        M3cNeed(ReplaySegmentsBox.SelectedItem is null && !ReplayCopyButton.IsEnabled && playbackPaused &&
            Math.Abs(player.Position.TotalSeconds - gapSeconds) < .1, "字幕空隙保留错误高亮或自动重新播放。");

        ReplayPositionSlider.Value = seekSeconds;
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        var revised = await Task.Run(() => TranscriptService.CreateRevision(directory, original.RevisionId,
            original.Segments.Select(segment => segment with { Text = "校订版 · " + segment.Text }).ToArray(), "合成字幕跟随版本切换", token), token);
        SetReplayRevisions(directory, [original, revised], revised.RevisionId);
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        AssertSelected(late.Segment.SegmentId, revised.RevisionId, seekSeconds); AssertVisible();
        M3cNeed(CurrentSubtitleText.Text.StartsWith("校订版 · ", StringComparison.Ordinal), "切换版本后上方字幕仍为旧文字。");

        // Returning to the replay tab while paused must refresh the displayed
        // position without triggering the explicit click/Enter seek handlers.
        NavigateTo(3);
        transcriptionView!.PlaybackPositionSlider.Value = 5.1;
        NavigateTo(5);
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        AssertSelected(original.Segments[20].SegmentId, revised.RevisionId, 5.1); AssertVisible();

        string overlapPath = Path.Combine(directory, "synthetic-following-overlap.srt");
        File.WriteAllText(overlapPath, "1\n00:00:05,000 --> 00:00:07,000\n合成长句\n\n2\n00:00:05,500 --> 00:00:06,000\n合成重叠短句\n");
        var overlap = await Task.Run(() => TranscriptService.Import(new(directory, Path.GetFileName(directory), original.MeetingDurationMs,
            overlapPath, "synthetic-following-overlap", IsSynthetic: true), token), token);
        SetReplayRevisions(directory, [original, revised, overlap], overlap.RevisionId);
        ReplayPositionSlider.Value = 5.6;
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        AssertSelected(overlap.Segments[1].SegmentId, overlap.RevisionId, 5.6);
        M3cNeed(CurrentSubtitleText.Text.Contains("合成长句", StringComparison.Ordinal) && CurrentSubtitleText.Text.Contains("合成重叠短句", StringComparison.Ordinal),
            "重叠高亮删除了仍在播放的其他字幕。");
        ReplayPositionSlider.Value = 6.5;
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        AssertSelected(overlap.Segments[0].SegmentId, overlap.RevisionId, 6.5);

        SetReplayRevisions(directory, [], null);
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        M3cNeed(ReplaySegmentsBox.SelectedItem is null && playbackPaused && ReferenceEquals(media, player) &&
            player.Source?.LocalPath == mediaPath && pendingTranscriptSeek is null, "无字幕版本触发了播放或保留旧选中行。");

        SetReplayRevisions(directory, [original, revised], original.RevisionId);
        ReplayPositionSlider.Value = originalPosition;
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        M3cNeed(playbackPaused && ReferenceEquals(media, player) && player.Source?.LocalPath == mediaPath,
            "恢复字幕版本改变了播放器或暂停状态。");
        return new
        {
            Status = "PASS", PausedSeekSelectsAndReveals = true, SameCueSeekReveals = true,
            NoPerFrameScrollWhilePaused = true, GapsClearSelection = true, VersionChangeFollowsCurrentTime = true,
            ReturnToReplayFollowsPausedTime = true, EmptyVersionDoesNotPlay = true, AutomaticSelectionNeverSeeks = true,
            OverlapSelectsLatestStartWithoutDroppingText = true,
            SourceMode = "EXISTING SYNTHETIC FILE / MUTED WPF PLAYBACK / NO CAPTURE"
        };

        void AssertSelected(string segmentId, string revisionId, double expectedSeconds)
        {
            M3cNeed(ReplaySegmentsBox.SelectedItem is TranscriptRow selected && selected.Segment.SegmentId == segmentId &&
                selected.Revision.RevisionId == revisionId, "字幕没有选中当前媒体位置所属句子。");
            M3cNeed(playbackPaused && pendingTranscriptSeek is null && ReferenceEquals(media, player) && player.Source?.LocalPath == mediaPath &&
                Math.Abs(player.Position.TotalSeconds - expectedSeconds) < .1, "自动选中造成跳播、重新打开或取消暂停。");
        }

        void AssertVisible()
        {
            var selected = ReplaySegmentsBox.SelectedItem;
            var item = ReplaySegmentsBox.ItemContainerGenerator.ContainerFromItem(selected) as ListBoxItem
                ?? throw new InvalidDataException("自动选中的字幕未生成可见行。");
            M3cNeed(item.IsSelected && item.IsVisible, "字幕行没有保持选中或可见状态。");
            Point position = item.TranslatePoint(new Point(), ReplaySegmentsBox);
            M3cNeed(position.Y >= -2 && position.Y + item.ActualHeight <= ReplaySegmentsBox.ActualHeight + 2,
                "自动选中的字幕仍在滚动视口之外。");
        }
    }

    private static ScrollViewer? FindFollowingScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer found) return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindFollowingScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
}
