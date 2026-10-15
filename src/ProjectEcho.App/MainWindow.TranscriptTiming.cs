using System.Windows;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private (object? Rows, string? Directory, long? Duration, string? Language) transcriptTimingBounds;

    private long? VerifiedTranscriptDurationMs(string? directory) => directory is not null && renderedMeetings.TryGetValue(directory, out var mix)
        ? (mix.DurationUs + 999) / 1000 : null;

    private void RefreshTranscriptTimingBounds()
    {
        var view = transcriptionView!;
        var current = (view.SegmentsBox.ItemsSource, selectedMeetingDirectory, VerifiedTranscriptDurationMs(selectedMeetingDirectory), UiText.Language);
        if (transcriptTimingBounds == current) return;
        transcriptTimingBounds = current;
        foreach (var row in view.SegmentsBox.Items.Cast<TranscriptRow>()) row.RefreshDraftLabel();
    }

    private async Task FindNextTranscriptTimingErrorAsync()
    {
        RememberTranscriptDraft();
        if (CurrentTranscriptDraft is not { } draft) return;
        string directory = visibleSegment?.Directory ?? transcriptMeeting!;
        var error = draft.FindTimingError(visibleSegment?.Segment.SegmentId, VerifiedTranscriptDurationMs(directory));
        if (error is null)
        {
            ReportReviewError(UiText.T("当前字幕版本没有发现时间戳错误。"));
            return;
        }
        await SelectTranscriptTimingErrorAsync(directory, draft.Parent, error);
    }

    private async Task SelectTranscriptTimingErrorAsync(string directory, TranscriptRevision revision, TranscriptSegment segment)
    {
        var view = transcriptionView!;
        // Search can hide the broken cue or point to another meeting/version. Expand
        // that complete revision before selecting; drafts stay attached to their parent.
        bool sameMeeting = string.Equals(directory, selectedMeetingDirectory, StringComparison.OrdinalIgnoreCase);
        if (!sameMeeting) SelectMeeting(directory);
        if (!sameMeeting || visibleRevision?.RevisionId != revision.RevisionId ||
            view.SegmentsBox.Items.Count != revision.Segments.Count ||
            view.SegmentsBox.Items.Cast<TranscriptRow>().Any(row => row.Directory != directory || row.Revision.RevisionId != revision.RevisionId))
            await LoadTranscriptVersionsAsync(revision.RevisionId);
        if (WindowCloseRequested || selectedMeetingDirectory != directory) return;
        var target = view.SegmentsBox.Items.Cast<TranscriptRow>().FirstOrDefault(row => row.Segment.SegmentId == segment.SegmentId);
        if (target is null) return;
        view.SegmentsBox.SelectedItem = target;
        view.SegmentsBox.ScrollIntoView(target);
        view.SegmentsBox.UpdateLayout();
        (view.SegmentsBox.ItemContainerGenerator.ContainerFromItem(target) as FrameworkElement)?.BringIntoView();
        // Selection is for correction only; it must never invoke subtitle playback.
    }
}
