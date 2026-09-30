using System.IO;
using System.Windows;
using ProjectEcho.Core;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    internal async Task<object> TranscriptionSmokeAsync(string evidence)
    {
        string fixture = Path.Combine(evidence, "synthetic-transcription-ui");
        Directory.CreateDirectory(fixture); dataRoot = fixture;
        string meeting = Path.Combine(RecordingsRoot, "synthetic-meeting");
        await Task.Run(() => WriteCloseFixture(meeting));
        var originals = Directory.EnumerateFiles(meeting, "*", SearchOption.AllDirectories).ToDictionary(p => p, CloseFixtureHash);
        currentMix = await Task.Run(() => M2ExportService.RenderMeeting(meeting, encoder: null));
        renderedMeetings[meeting] = currentMix;
        SelectMeeting(meeting);
        var view = transcriptionView!;
        if (!StartButton.IsEnabled || view.RunWhisperButton.IsEnabled != true || host is not null || microphoneEnabled)
            throw new InvalidDataException("M3 初始化阻塞录音或产生采集。");
        importFile = Path.Combine(fixture, "test.srt");
        File.WriteAllText(importFile, "1\n00:00:00,100 --> 00:00:00,600\n合成会议待核实文本\n\n2\n00:00:01,100 --> 00:00:01,800\n保留完整时间轴\n");
        view.ProviderBox.Text = "合成界面测试";
        StartTranscriptWork(ImportTranscriptAsync); await transcriptWork;
        if (visibleRevision?.Segments.Count != 2) throw new InvalidDataException("M3 界面导入未显示结果：" + view.StatusText.Text);
        view.WorkspaceTabs.SelectedIndex = 1;
        view.SegmentsBox.SelectedIndex = 0;
        string originalRevision = visibleRevision.RevisionId;
        view.EditBox.Text = "用户校订后的合成文字";
        StartTranscriptWork(SaveTranscriptRevisionAsync); await transcriptWork;
        if (visibleRevision?.ParentRevisionId != originalRevision || visibleRevision.Segments[0].Text != "用户校订后的合成文字")
            throw new InvalidDataException("M3 界面修订没有保留父版本。");
        view.SearchBox.Text = "校订后的";
        StartTranscriptWork(SearchTranscriptsAsync); await transcriptWork;
        if (view.SegmentsBox.Items.Count != 1) throw new InvalidDataException("M3 界面搜索未返回修订结果。");
        view.SegmentsBox.SelectedIndex = 0;
        state = SessionState.Paused; UpdateControls();
        if (view.SeekButton.IsEnabled || view.StopListeningButton.IsEnabled) throw new InvalidDataException("暂停期间文字回放入口未锁定。");
        // Explicitly invoke the handler as well: it must not open any media while paused.
        SeekTranscript();
        if (pendingTranscriptSeek is not null) throw new InvalidDataException("暂停期间排队了回放。");
        state = SessionState.Saved; UpdateControls();
        if (!view.SeekButton.IsEnabled || !StartButton.IsEnabled) throw new InvalidDataException("保存后文字回放/新录音入口不符。");
        if (originals.Any(p => CloseFixtureHash(p.Key) != p.Value)) throw new InvalidDataException("文字操作改变了原件。");
        if (WhisperTranscriber.Inspect(new("", "")).Ready) throw new InvalidDataException("未配置模型被称为可识别。");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StartTranscriptWork(async () =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, transcriptCancellation!.Token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); await release.Task; throw; }
        });
        await entered.Task;
        if (!StartButton.IsEnabled || !view.CancelButton.IsEnabled) throw new InvalidDataException("文字任务阻塞新录音或缺取消入口。");
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Closed += (_, _) => closed.TrySetResult();
        try
        {
            Close(); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!IsVisible || transcriptWork.IsCompleted || closeApproved) throw new InvalidDataException("窗口未等文字任务清理就关闭。");
            release.TrySetResult();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.TrySetResult(); }
        return new { Status = "PASS", NoCapture = host is null, MicrophoneCollected = false,
            ImportRevisionSearch = "PASS", PausedPlaybackGuard = "PASS (no playback performed)",
            CloseWaitsForTextCleanup = "PASS (controlled asynchronous fixture)", OriginalHashesUnchanged = true,
            ActualWhisperInference = "NOT_RUN: no configured executable/model" };
    }
}
