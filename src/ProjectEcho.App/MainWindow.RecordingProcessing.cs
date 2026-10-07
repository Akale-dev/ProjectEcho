using System.IO;

namespace ProjectEcho.App;

public partial class MainWindow
{
    // This owner is captured from the recording request, never from the selected history row.
    // Resetting source/mic consent after Save must leave this receipt visible until the next request.
    private string? recordingProcessingDirectory;
    private string recordingProcessingTitle = "";
    private string recordingProcessingState = "本场录音保存后将在此显示处理进度与结果。";
    private string? selectedDurationDirectory;
    private long? selectedDurationMs;

    private void BeginRecordingProcessing(string directory, string title)
    {
        recordingProcessingDirectory = Path.GetFullPath(directory);
        recordingProcessingTitle = title.Trim();
        recordingProcessingState = "录音结束并保存原轨后，开始录后处理。";
        RefreshRecordingProcessingCard();
    }

    private void ClearRecordingProcessing()
    {
        recordingProcessingDirectory = null;
        recordingProcessingTitle = "";
        recordingProcessingState = "本场录音保存后将在此显示处理进度与结果。";
        RefreshRecordingProcessingCard();
    }

    private void MarkRecordingProcessingSaved(string directory)
    {
        if (!string.Equals(recordingProcessingDirectory, directory, StringComparison.OrdinalIgnoreCase)) return;
        recordingProcessingState = "原轨已保存；等待录后处理。可在会议库查看处理记录。";
        EnsureMeetingExportStatus(directory);
        RefreshRecordingProcessingCard();
    }

    private void MarkRecordingProcessingUnconfirmed()
    {
        if (recordingProcessingDirectory is null) return;
        recordingProcessingState = "录音未确认完整保存；尚未开始录后处理，请查看录音状态。";
        RefreshRecordingProcessingCard();
    }

    private void RefreshRecordingProcessingCard()
    {
        if (RecordingProcessingStatus is null) return;
        RecordingProcessingHeading.Text = UiText.T("录后处理");
        RecordingProcessingMeetingText.Text = recordingProcessingDirectory is null ? UiText.T("尚无本次录音") :
            UiText.F($"本次录音：{(recordingProcessingTitle.Length == 0 ? UiText.T("未命名会议") : recordingProcessingTitle)}");
        // Display-only and memory-only: high-rate progress must not read a receipt or write a Log.
        RecordingProcessingStatus.Text = recordingProcessingDirectory is { } directory &&
            transientMixStatuses.TryGetValue(directory, out string? progress) ? progress :
            recordingProcessingDirectory is { } saved && meetingExportStatuses.TryGetValue(saved, out var result)
                ? result.Text : UiText.T(recordingProcessingState);
    }

    private void RefreshMeetingDuration()
    {
        if (MeetingDurationText is null) return;
        long? duration = string.Equals(selectedDurationDirectory, selectedMeetingDirectory, StringComparison.OrdinalIgnoreCase)
            ? selectedDurationMs : null;
        MeetingDurationText.Text = duration is long ms
            ? UiText.F($"时长：{FormatElapsed(checked(ms * 1000))}") : UiText.T("时长：未知（元数据不可用）");
    }
}
