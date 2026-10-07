using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProjectEcho.Core;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Actual WPF revision selection and file exports, with generated model-result fixtures and no capture.</summary>
    internal async Task<object> M3dTranscriptVersionSmokeAsync(string evidence, CancellationToken token)
    {
        if (host is not null || microphoneEnabled) throw new InvalidOperationException("版本测试必须在无采集状态运行。");
        string folder = Path.GetFullPath(evidence); Directory.CreateDirectory(folder);
        dataRoot = Path.Combine(folder, "isolated-version-data"); Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        string meeting = Path.Combine(RecordingsRoot, "synthetic-model-versions");
        await Task.Run(() => WriteCloseFixture(meeting), token);
        var sourceHashes = Directory.EnumerateFiles(meeting, "*", SearchOption.AllDirectories).ToDictionary(path => path, CloseFixtureHash);
        currentMix = await Task.Run(() => M2ExportService.RenderMeeting(meeting, encoder: null), token);
        renderedMeetings[meeting] = currentMix;
        SelectMeeting(meeting); await savedMixWork.WaitAsync(token); await replayLoadWork.WaitAsync(token);
        var view = transcriptionView!; var images = new List<string>();
        double initialWidth = Width, initialHeight = Height;
        try
        {
            var modelA = await ImportSyntheticModel("synthetic-model-A", "合成模型 A 原稿 · ticker TEST");
            await LoadTranscriptVersionsAsync(modelA.RevisionId);
            NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 1;
            view.SegmentsBox.SelectedIndex = 0; view.EditBox.Text = "合成模型 A 人工校订 · ticker TEST-A";
            view.SaveRevisionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            var reviewA = visibleRevision ?? throw new InvalidDataException(view.StatusText.Text);
            M3dVersionNeed(reviewA.ParentRevisionId == modelA.RevisionId, "修订未创建独立版本。");
            var oldHashes = SnapshotVersions();

            var modelB = await ImportSyntheticModel("synthetic-model-B", "合成模型 B 原稿 · ticker TEST-B");
            await LoadTranscriptVersionsAsync(modelB.RevisionId);
            M3dVersionNeed(view.RevisionBox.Items.Count == 3 && ReplayRevisionBox.Items.Count == 3 &&
                oldHashes.All(pair => CloseFixtureHash(pair.Key) == pair.Value), "新模型覆盖旧原稿/修订或两处选版列表不同步。");
            SelectReplay(modelA.RevisionId); SelectReview(reviewA.RevisionId);
            view.RefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            M3dVersionNeed(ReplayId() == modelA.RevisionId && visibleRevision?.RevisionId == reviewA.RevisionId,
                "刷新丢失回听/校订各自选中的旧版本。");
            M3dVersionNeed(((TranscriptRow)ReplaySegmentsBox.Items[0]).Segment.Text == modelA.Segments[0].Text &&
                ((TranscriptRow)view.SegmentsBox.Items[0]).Segment.Text == reviewA.Segments[0].Text,
                "两处选择不同版本后仍显示同一套文字。");

            // Loading an additional completed version without requesting a new
            // selection must refresh available versions while preserving choices.
            var repeatedA = await ImportSyntheticModel("synthetic-model-A", "合成模型 A 再次识别 · ticker TEST-A2");
            await LoadTranscriptVersionsAsync();
            M3dVersionNeed(view.RevisionBox.Items.Count == 4 && ReplayRevisionBox.Items.Count == 4 &&
                ReplayId() == modelA.RevisionId && visibleRevision?.RevisionId == reviewA.RevisionId,
                "同模型重跑没有独立可选版本，或无指定刷新覆盖已选版本。");
            foreach (var row in view.RevisionBox.Items.Cast<RevisionRow>())
                M3dVersionNeed(row.Label.Contains(row.Revision.ModelName!, StringComparison.Ordinal) &&
                    row.Label.Contains(row.Revision.ImportedAt.ToLocalTime().ToString("yyyy-MM-dd"), StringComparison.Ordinal) &&
                    row.Label.Contains(UiText.T(row.Revision.ParentRevisionId is null ? "原稿" : "修订"), StringComparison.Ordinal),
                    "选版标签未区分模型、完整日期与原稿/修订。");
            M3dVersionNeed(view.RevisionBox.Items.Cast<RevisionRow>().Select(row => row.Label).Distinct().Count() == 4,
                "不同版本标签不可区分。");

            foreach (int page in new[] { 5, 4, 3 })
            { NavigateTo(page); await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render); }
            view.WorkspaceTabs.SelectedIndex = 1; await transcriptWork.WaitAsync(token);
            M3dVersionNeed(ReplayId() == modelA.RevisionId && visibleRevision?.RevisionId == reviewA.RevisionId,
                "跨页面导航丢失独立版本选择。");
            string reviewExport = Path.Combine(folder, "old-reviewed-a.txt"), replayExport = Path.Combine(folder, "old-original-a.txt");
            StartTranscriptWork(() => ExportTranscriptTextAsync(reviewExport)); await transcriptWork.WaitAsync(token);
            StartTranscriptWork(() => ExportReplayTextAsync(replayExport)); await transcriptWork.WaitAsync(token);
            M3dVersionNeed(File.ReadAllText(reviewExport).Contains(reviewA.Segments[0].Text, StringComparison.Ordinal) &&
                !File.ReadAllText(reviewExport).Contains(modelB.Segments[0].Text, StringComparison.Ordinal) &&
                File.ReadAllText(replayExport).Contains(modelA.Segments[0].Text, StringComparison.Ordinal) &&
                !File.ReadAllText(replayExport).Contains(reviewA.Segments[0].Text, StringComparison.Ordinal),
                "两处导出没有分别使用所选旧版。");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            SaveLayoutImage(folder, "m3d-model-versions-review.png", 1, images);
            NavigateTo(5); await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            SaveLayoutImage(folder, "m3d-model-versions-replay.png", 1, images);

            var finalHashes = SnapshotVersions();
            // Remove UI selections and rendered-media cache. Reload from the
            // finalized meeting directory, with no revision objects reused.
            RecordNavigationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            renderedMeetings.Clear(); SelectMeeting(meeting); await savedMixWork.WaitAsync(token);
            NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 1; await transcriptWork.WaitAsync(token); await replayLoadWork.WaitAsync(token);
            await LoadTranscriptVersionsAsync();
            M3dVersionNeed(view.RevisionBox.Items.Count == 4 && ReplayRevisionBox.Items.Count == 4, "清除缓存从磁盘重开丢失历史版本。");
            SelectReplay(reviewA.RevisionId); SelectReview(modelA.RevisionId);
            string subtitleExport = Path.Combine(folder, "reopened-old-review.srt");
            StartTranscriptWork(() => ExportReplaySubtitlesAsync(subtitleExport)); await transcriptWork.WaitAsync(token);
            string subtitle = File.ReadAllText(subtitleExport);
            M3dVersionNeed(subtitle.Contains(reviewA.Segments[0].Text, StringComparison.Ordinal) && subtitle.Contains("00:00:00,100 --> 00:00:00,600", StringComparison.Ordinal),
                "磁盘重开后旧修订字幕导出使用了错误内容或改变时间。");
            M3dVersionNeed(sourceHashes.All(pair => CloseFixtureHash(pair.Key) == pair.Value) &&
                finalHashes.All(pair => CloseFixtureHash(pair.Key) == pair.Value) && host is null && !microphoneEnabled && player.Source is null,
                "版本选择/导出更改原件或触发采集/播放。");

            // Preserve real unsaved editor text independently of immutable files.
            // The earlier assertions above already established all saved versions.
            const string draftA = "模型 A 尚未保存的人工校订 · TEST-DRAFT-A";
            const string draftB = "模型 B 尚未保存的人工校订 · TEST-DRAFT-B";
            SelectReview(modelA.RevisionId); view.SegmentsBox.SelectedIndex = 0; view.EditBox.Text = draftA;
            SelectReview(modelB.RevisionId); view.SegmentsBox.SelectedIndex = 0;
            M3dVersionNeed(view.EditBox.Text == modelB.Segments[0].Text, "模型 A 草稿串到了模型 B。");
            view.EditBox.Text = draftB;
            SelectReview(modelA.RevisionId);
            M3dVersionNeed(visibleSegment?.Revision.RevisionId == modelA.RevisionId && view.EditBox.Text == draftA,
                "切换版本返回后未恢复模型 A 的已选段落和未保存校订。");
            view.RefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            M3dVersionNeed(visibleSegment?.Revision.RevisionId == modelA.RevisionId && view.EditBox.Text == draftA,
                "刷新版本丢失未保存校订或已选字幕段落。");
            SelectReview(modelB.RevisionId);
            M3dVersionNeed(visibleSegment?.Revision.RevisionId == modelB.RevisionId && view.EditBox.Text == draftB,
                "模型 B 草稿被模型 A 草稿或刷新覆盖。");
            SelectReview(modelA.RevisionId);

            string otherMeeting = Path.Combine(RecordingsRoot, "synthetic-draft-other-meeting");
            await Task.Run(() => WriteCloseFixture(otherMeeting), token);
            string otherSubtitle = Path.Combine(folder, "other-meeting-draft.srt");
            File.WriteAllText(otherSubtitle, "1\n00:00:00,100 --> 00:00:00,600\n另一场会议的原稿 · SAME-MODEL-NAME\n");
            var otherRevision = await Task.Run(() => TranscriptService.Import(new(otherMeeting, Path.GetFileName(otherMeeting), 2000,
                otherSubtitle, "synthetic local model result, not inference", modelA.ModelName, IsSynthetic: true), token), token);
            var otherHashes = Directory.EnumerateFiles(otherMeeting, "*", SearchOption.AllDirectories).ToDictionary(path => path, CloseFixtureHash);
            SelectMeeting(otherMeeting, loadSavedMix: false); await transcriptWork.WaitAsync(token); await replayLoadWork.WaitAsync(token);
            await LoadTranscriptVersionsAsync(otherRevision.RevisionId); view.SegmentsBox.SelectedIndex = 0;
            M3dVersionNeed(view.EditBox.Text == otherRevision.Segments[0].Text, "不同会议的同名模型串入旧会议草稿。");
            const string otherDraft = "另一场会议未保存的校订 · TEST-DRAFT-OTHER";
            view.EditBox.Text = otherDraft;
            SelectMeeting(meeting); await savedMixWork.WaitAsync(token); await transcriptWork.WaitAsync(token); await replayLoadWork.WaitAsync(token);
            M3dVersionNeed(visibleSegment?.Revision.RevisionId == modelA.RevisionId && view.EditBox.Text == draftA,
                "跨会议返回未恢复原会议的版本、段落和未保存校订。");
            SelectMeeting(otherMeeting, loadSavedMix: false); await transcriptWork.WaitAsync(token); await replayLoadWork.WaitAsync(token);
            M3dVersionNeed(visibleSegment?.Revision.RevisionId == otherRevision.RevisionId && view.EditBox.Text == otherDraft,
                "另一场会议的未保存草稿未独立恢复。");
            SelectMeeting(meeting); await savedMixWork.WaitAsync(token); await transcriptWork.WaitAsync(token); await replayLoadWork.WaitAsync(token);
            M3dVersionNeed(visibleSegment?.Revision.RevisionId == modelA.RevisionId && view.EditBox.Text == draftA &&
                TranscriptService.ListRevisions(meeting).Count == 4 &&
                finalHashes.All(pair => CloseFixtureHash(pair.Key) == pair.Value) && otherHashes.All(pair => CloseFixtureHash(pair.Key) == pair.Value),
                "草稿恢复写入了伪保存版本、覆盖原稿，或未回到模型 A 草稿。");

            // Render both version selectors and the review editor at the minimum
            // supported window. Scrollable content is checked at both ends.
            Width = MinWidth; Height = MinHeight;
            NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 1;
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render); await Task.Delay(80, token);
            view.ReviewBodyScroller.ScrollToTop(); await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            M3dVersionNeed(VisibleInside(view.RevisionBox, view.ReviewBodyScroller), "最小窗口的校订版本选择器超出可见区域。");
            SaveLayoutImage(folder, "m3d-model-versions-review-minimum-top.png", 1, images);
            view.ReviewBodyScroller.ScrollToEnd(); await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            M3dVersionNeed(VisibleInside(view.SaveRevisionButton, view.ReviewBodyScroller) && VisibleInside(view.ExportTextButton, view.ReviewBodyScroller) &&
                VisibleInside(view.PlaybackPositionSlider, view) && !view.TranscriptStatusPanel.IsVisible && view.EditBox.Text == draftA,
                "最小窗口滚动后无法操作校订/导出，播放器被裁切，已删除的状态栏复现，或草稿丢失。");
            SaveLayoutImage(folder, "m3d-model-versions-review-minimum-bottom.png", 1, images);
            NavigateTo(5); await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            M3dVersionNeed(VisibleInside(ReplayRevisionBox, this) && VisibleInside(ReplayPlayerBar, this), "最小窗口回听版本选择器或播放器超出窗口。");
            SaveLayoutImage(folder, "m3d-model-versions-replay-minimum.png", 1, images);
            var minimumWindow = new { Width = ActualWidth, Height = ActualHeight };
            return new
            {
                Status = "PASS", SourceMode = "GENERATED AUDIO AND SYNTHETIC MODEL-RESULT FILES / NO CAPTURE / NO PLAYBACK",
                Versions = new[] { modelA, reviewA, modelB, repeatedA }.Select(r => new { r.RevisionId, r.ParentRevisionId, r.ModelName }).ToArray(),
                SeparateReplayAndReviewSelection = true, RefreshPreservesSelection = true, NewAndRepeatedModelKeepAllVersions = true,
                LabelsIncludeModelDateAndRevisionKind = true, ReopenedFromDisk = true, SelectedOldVersionExports = true,
                UnsavedDraftAcrossVersions = true, UnsavedDraftSurvivesRefresh = true, UnsavedDraftAcrossMeetings = true,
                DraftsDoNotCreateOrMutateSavedVersions = true, MinimumWindowControlsReachable = true, MinimumWindow = minimumWindow,
                OriginalsUnchanged = true, Images = images,
                ActualModelInference = "NOT_RUN; model-result fixtures exercise production persistence and WPF controls"
            };
        }
        finally { Width = initialWidth; Height = initialHeight; ResetTranscriptPlayback(); state = SessionState.Idle; UpdateControls(); }

        async Task<TranscriptRevision> ImportSyntheticModel(string modelName, string content)
        {
            string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".srt");
            File.WriteAllText(path, "1\n00:00:00,100 --> 00:00:00,600\n" + content + "\n");
            return await Task.Run(() => TranscriptService.Import(new(meeting, Path.GetFileName(meeting), 2000, path,
                "synthetic local model result, not inference", modelName, IsSynthetic: true), token), token);
        }
        void SelectReplay(string id) => ReplayRevisionBox.SelectedItem = ReplayRevisionBox.Items.Cast<RevisionRow>().Single(row => row.Revision.RevisionId == id);
        void SelectReview(string id) => view.RevisionBox.SelectedItem = view.RevisionBox.Items.Cast<RevisionRow>().Single(row => row.Revision.RevisionId == id);
        string? ReplayId() => (ReplayRevisionBox.SelectedItem as RevisionRow)?.Revision.RevisionId;
        Dictionary<string, string> SnapshotVersions() => Directory.EnumerateFiles(Path.Combine(meeting, "transcripts"), "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, CloseFixtureHash);
        static bool VisibleInside(FrameworkElement element, FrameworkElement viewport)
        {
            Point position = element.TranslatePoint(new Point(), viewport);
            return element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0 && position.X >= -1 && position.Y >= -1 &&
                position.X + element.ActualWidth <= viewport.ActualWidth + 1 && position.Y + element.ActualHeight <= viewport.ActualHeight + 1;
        }
    }

    private static void M3dVersionNeed(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
