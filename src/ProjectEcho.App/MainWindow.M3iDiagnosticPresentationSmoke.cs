using System.IO;
using System.Text.Json;
using ProjectEcho.Core.Diagnostics;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Opt-in UI/Log checks with synthetic files only; never starts a capture device.</summary>
    internal Task<object> M3iDiagnosticPresentationSmokeAsync(string evidence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        const string device = "remote 设备帧计数不可依赖；保留原值，以实际API QPC标记时间。长时/双轨同步未验收。";
        const string legacy = "原件及索引已完成验证；旧式完整捕获/设备时钟条件未通过，Complete仍为false。";
        const string mix = "混音依据原始 API QPC 和设备帧重建时间；连续设备帧采用有界平稳映射，保留断续与真实缺口。真实两小时双轨同步尚待验收。";
        const string fault = "remote 存在未观察到音频包的时间区间；合成真实警告必须保留。";
        string raw = "录音原轨已保存。\n" + device + "\n" + legacy + "\n" + mix + "\n" + fault;
        string fixture = Path.Combine(evidence, "synthetic-data");
        Directory.CreateDirectory(fixture); dataRoot = fixture; DataRootText.Text = fixture;
        string meeting = Path.Combine(RecordingsRoot, "diagnostic-presentation");
        WriteCloseFixture(meeting);
        var originals = Directory.EnumerateFiles(meeting, "*", SearchOption.AllDirectories).ToDictionary(path => path, CloseFixtureHash);
        Need(MeetingLogStore.TryAppend(meeting, "recording", "warning", raw, eventId: "old-unmodified").Success, "Legacy log fixture failed.");
        string logPath = Path.Combine(meeting, MeetingLogStore.FileName);
        byte[] oldLog = File.ReadAllBytes(logPath);
        string oldLanguage = UiText.Language;
        try
        {
            UiText.SetLanguage("zh-CN"); SelectMeeting(meeting, loadSavedMix: false);
            recordingLogDirectory = meeting; SetStatus(raw); recordingLogDirectory = null;
            Need(!StatusBox.Text.Contains("未验收") && !StatusBox.Text.Contains("Complete仍为false") &&
                StatusBox.Text.Contains("系统 API 时间戳") && StatusBox.Text.Contains(fault), "Recording status projection is wrong.");
            SetMeetingExportStatus(meeting, raw, persist: true);
            Need(!ExportStatus.Text.Contains("未验收") && ExportStatus.Text.Contains(fault), "Mix status projection is wrong.");
            var log = MeetingLogStore.Read(meeting);
            Need(log.Entries.Single(entry => entry.EventId == "old-unmodified").Message == raw &&
                log.Entries.Any(entry => entry.EventId != "old-unmodified" && entry.Message.Contains(fault) &&
                    !entry.Message.Contains("Complete仍为false")), "New Log projection changed old history or lost a real warning.");
            RefreshMeetingLog(importHistory: false);
            Need(MeetingLogText.Text.Contains("Complete仍为false"), "Existing Log display was silently rewritten.");
            byte[] logBytes = File.ReadAllBytes(logPath);
            Need(logBytes.AsSpan(0, oldLog.Length).SequenceEqual(oldLog), "Original Log bytes changed.");
            string processing = Path.Combine(meeting, ExportHistoryFileName);
            File.WriteAllText(processing, JsonSerializer.Serialize(new SavedExportStatus("meeting-processing-result-1",
                MeetingManifestHash(meeting), DateTimeOffset.UtcNow, raw, null)));
            string processingHash = CloseFixtureHash(processing);
            meetingExportStatuses.Remove(meeting); RefreshSelectedExportStatus();
            Need(!ExportStatus.Text.Contains("Complete仍为false") && ExportStatus.Text.Contains(fault) &&
                CloseFixtureHash(processing) == processingHash, "Legacy processing receipt was rewritten or not projected.");
            foreach (string language in new[] { "en", "zh-Hant", "zh-CN" })
            {
                UiText.SetLanguage(language);
                string displayed = PresentRecordingWarnings([device, legacy, mix, fault]);
                Need(!displayed.Contains("Complete仍为false") && !displayed.Contains("未验收") && displayed.Contains(fault),
                    "Localized projection lost a fault or leaked a developer note.");
            }
            const string unknown = "UNKNOWN_CLOCK_DIAGNOSTIC warning with Complete=false";
            Need(PresentRecordingDiagnostic(unknown) == unknown, "Unknown diagnostic was changed.");
            foreach (var original in originals)
                Need(CloseFixtureHash(original.Key) == original.Value, "Display projection changed original audio/clock files.");
            return Task.FromResult<object>(new
            {
                Success = true, SyntheticOnly = true, CaptureStarted = false, MicrophoneEnabled = false,
                ActualWpfStatus = true, RealWarningRetained = true, UnknownDiagnosticRetained = true,
                OldLogUnchanged = true, OldProcessingReceiptUnchanged = true, OriginalAudioAndClockFilesUnchanged = true
            });
        }
        finally { recordingLogDirectory = null; UiText.SetLanguage(oldLanguage); }
        static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    }
}
