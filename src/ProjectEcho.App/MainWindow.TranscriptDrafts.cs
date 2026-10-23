using System.IO;
using System.Windows;
using System.Windows.Automation;
using ProjectEcho.Core;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private bool loadingTranscriptEditor;
    private Action<string>? reviewErrorForSmoke;

    private void ReportReviewError(string message)
    {
        if (WindowCloseRequested || MainTabs.SelectedIndex != 1 || MeetingTabs.SelectedIndex != 3 ||
            transcriptionView?.WorkspaceTabs.SelectedIndex != 1) return;
        if (reviewErrorForSmoke is { } report) report(message);
        else if (!isolatedTestWindow) MessageBox.Show(this, message, UiText.T("字幕操作未完成"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    // Only the synthetic WPF harness supplies this; production always shows the dialog.
    private Func<int, bool>? confirmDiscardTranscriptDraftsForSmoke;

    internal void PrepareTranscriptDraftSmokeShutdown() => confirmDiscardTranscriptDraftsForSmoke = _ => true;

    private TranscriptRevisionDraft DraftFor(string directory, TranscriptRevision revision)
    {
        var key = (directory, revision.RevisionId);
        if (!transcriptDrafts.TryGetValue(key, out var draft)) transcriptDrafts.Add(key, draft = new(revision));
        return draft;
    }

    private TranscriptRevisionDraft? CurrentTranscriptDraft =>
        (visibleSegment?.Directory ?? transcriptMeeting) is { } directory && (visibleSegment?.Revision ?? visibleRevision) is { } revision
            ? DraftFor(directory, revision) : null;

    private void PruneCleanTranscriptDrafts()
    {
        // Keep only edited revisions after leaving a list. Browsing many meetings must
        // not retain every previously loaded full transcript for the rest of the session.
        foreach (var key in transcriptDrafts.Where(pair => !pair.Value.HasChanges).Select(pair => pair.Key).ToArray())
            transcriptDrafts.Remove(key);
    }

    private void TranscriptDraftInputChanged()
    {
        if (loadingTranscriptEditor || visibleSegment is null) return;
        RememberTranscriptDraft();
        visibleSegment.RefreshDraftLabel();
        UpdateTranscriptionControls();
    }

    private void ClearTranscriptEditor()
    {
        if (transcriptionView is not { } view) return;
        loadingTranscriptEditor = true;
        try { view.EditBox.Clear(); view.StartTimestampBox.Clear(); view.EndTimestampBox.Clear(); }
        finally { loadingTranscriptEditor = false; }
        ClearValidationIssues(view.StartTimestampBox, view.EndTimestampBox);
    }

    private bool TryGetSelectedDraftSegment(out TranscriptSegment segment, out string? error)
    {
        segment = null!; error = null;
        if (visibleSegment is not { } row) return false;
        if (!DraftFor(row.Directory, row.Revision).TryGetSegment(row.Segment, out segment, out error)) return false;
        try { ValidateDraftAudioRange(row.Directory, [segment]); return true; }
        catch (InvalidDataException ex) { error = ex.Message; return false; }
    }

    private void ValidateDraftAudioRange(string directory, IReadOnlyList<TranscriptSegment> segments)
    {
        // Meeting duration is the persisted bound; a verified decoded mix gives a tighter bound.
        if (renderedMeetings.TryGetValue(directory, out var mix) &&
            segments.Any(segment => segment.EndMs is { } end && end > (mix.DurationUs + 999) / 1000))
            throw new InvalidDataException("字幕时间不能超过已校验的完整回听音频时长。");
    }

    private void UpdateTranscriptDraftStatus()
    {
        if (transcriptionView is not { } view) return;
        view.FindTimingErrorButton.Content = UiText.T("寻找错误");
        view.FindTimingErrorButton.ToolTip = UiText.T("选中当前版本的下一条时间戳异常字幕；到末尾后从头寻找。");
        AutomationProperties.SetName(view.FindTimingErrorButton, UiText.T("寻找错误"));
        view.StartTimestampLabel.Text = UiText.T("开始"); view.EndTimestampLabel.Text = UiText.T("结束");
        string hint = UiText.T("时:分:秒.百分秒（两位小数）；分隔符固定。× 清空此时间。");
        if (CurrentTranscriptDraft is { } draft)
            hint += "\n" + UiText.F($"本版本时长：{TimelineTimestamp.FromMilliseconds(draft.Parent.MeetingDurationMs)}");
        view.StartTimestampBox.RefreshLanguage(); view.EndTimestampBox.RefreshLanguage();
        view.StartTimestampBox.ToolTip = view.EndTimestampBox.ToolTip = hint;
        AutomationProperties.SetName(view.StartTimestampBox, UiText.T("选中字幕开始时间"));
        AutomationProperties.SetName(view.EndTimestampBox, UiText.T("选中字幕结束时间"));
        ClearValidationIssues(view.StartTimestampBox, view.EndTimestampBox);
        if (visibleSegment is not null && !TryGetSelectedDraftSegment(out _, out string? error))
        {
            string message = UiText.T(error ?? "时间无效。");
            SetValidationIssue(view.StartTimestampBox, message); SetValidationIssue(view.EndTimestampBox, message);
            view.StartTimestampBox.ToolTip = view.EndTimestampBox.ToolTip = message + "\n" + hint;
        }
        int currentCount = CurrentTranscriptDraft?.ChangedSegmentCount ?? 0;
        view.SaveRevisionButton.ToolTip = currentCount > 0
            ? UiText.F($"本版本 {currentCount} 段未保存；点击“保存修订”一次保存全部修改，之后可导出。")
            : UiText.T("文字和时间修改暂存在内存；点击“保存修订”才创建新版本。");
    }
    private bool ConfirmDiscardTranscriptDrafts()
    {
        RememberTranscriptDraft();
        int count = transcriptDrafts.Values.Count(draft => draft.HasChanges);
        if (count == 0) return true;
        if (confirmDiscardTranscriptDraftsForSmoke is { } confirm) return confirm(count);
        return MessageBox.Show(this,
            UiText.F($"还有 {count} 个字幕版本的修改未保存。是否放弃这些修改并关闭？\n选择“否”返回，点击“保存修订”后再关闭。"),
            UiText.T("字幕修改尚未保存"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
