using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ProjectEcho.Core.Diagnostics;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    internal async Task<object> M3eLogSmokeAsync(string evidence, CancellationToken token)
    {
        dataRoot = Path.Combine(evidence, "data"); Directory.CreateDirectory(dataRoot);
        string first = Path.Combine(RecordingsRoot, "meeting-a"), second = Path.Combine(RecordingsRoot, "meeting-b");
        WriteCloseFixture(first); WriteCloseFixture(second);
        var originals = Directory.EnumerateFiles(RecordingsRoot, "*", SearchOption.AllDirectories).ToDictionary(p => p, CloseFixtureHash);
        SelectMeeting(first, loadSavedMix: false); NavigateTo(1); MeetingTabs.SelectedIndex = 4;
        Need(MeetingTabs.Items.Count == 5 && RecoveryStatus.Visibility == Visibility.Collapsed, "Log tab or removal of stale basic status failed.");
        foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
        {
            ChangeLanguage(language, persist: false);
            Need(ExportMeetingLogButton.Content?.ToString() == UiText.T("导出 Log") &&
                RefreshMeetingLogButton.Content?.ToString() == UiText.T("刷新 Log"), "Log actions missing or wrongly localized.");
        }
        ChangeLanguage("zh-CN", persist: false);
        recordingLogDirectory = first;
        SetStatus("SYNTHETIC_CAPTURE_DIAGNOSTIC");
        SelectMeeting(second, loadSavedMix: false);
        SetStatus("SYNTHETIC_CAPTURE_BOUNDARY_A");
        recordingLogDirectory = null;
        Need(!MeetingLogText.Text.Contains("SYNTHETIC_CAPTURE"), "Active capture diagnostic leaked into B.");
        using (var gate = new ExportHistoryGate())
        {
            renderOperation = (directory, cancellation) => gate.Run(() => M2ExportService.RenderMeeting(directory, encoder: null, cancellation), cancellation);
            SelectMeeting(first, loadSavedMix: false); QueueMix(first);
            await gate.Entered.Task.WaitAsync(token);
            SelectMeeting(second, loadSavedMix: false); gate.Release.Set(); await backgroundWork.WaitAsync(token);
            Need(!MeetingLogText.Text.Contains("完整 WAV 已校验"), "Late mix contaminated selected B Log.");
        }
        SelectMeeting(first, loadSavedMix: false);
        Need(MeetingLogText.Text.Contains("完整 WAV 已校验") && MeetingLogText.Text.Contains("SYNTHETIC_CAPTURE_BOUNDARY_A"), "A lost capture or mix history.");
        FiledTitleBox.Text = "PRIVATE_SYNTHETIC_TITLE"; FiledProjectBox.Text = "PRIVATE_SYNTHETIC_PROJECT";
        FileMeeting_Click(this, new RoutedEventArgs());
        Need(MeetingLogText.Text.Contains("会议标题/项目归档信息已保存") && !MeetingLogText.Text.Contains("PRIVATE_SYNTHETIC_TITLE"), "Filing event missing or title included.");
        await InspectOrRecoverAsync(false);
        Need(MeetingLogText.Text.Contains("磁盘索引与保存结果校验通过"), "Recovery diagnostic missing.");
        exportOperation = (_, _, _, _) => throw new IOException("SYNTHETIC_EXPORT_ERROR HRESULT=0x80070005");
        await ExportCurrentMixAsync(first, currentMix!, new(M2ExportFormat.Wave, 1000000, 2, 0, false), Path.Combine(dataRoot, "Exports"));
        Need(MeetingLogText.Text.Contains("SYNTHETIC_EXPORT_ERROR"), "Export failure was not logged.");
        await RunTranscriptWorkAsync(() => throw new InvalidDataException("SYNTHETIC_ASR_ERROR duration=32831 end=33100"), "asr", first);
        Need(MeetingLogText.Text.Contains("SYNTHETIC_ASR_ERROR"), "ASR failure was not logged.");
        string attempt = Path.Combine(second, "transcripts", "asr", "legacy-attempt"); Directory.CreateDirectory(attempt);
        File.WriteAllText(Path.Combine(attempt, "attempt.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "m3-whisper-attempt-2", importedAt = DateTimeOffset.Parse("2026-10-02T08:15:56Z"),
            status = "Failed", elapsedMilliseconds = 42800, requestedHardware = "Cpu",
            message = "转写起止时间倒置、越界或质量标记无效。 PRIVATE_UNLABELLED_SENTENCE", initialPrompt = "PRIVATE_SYNTHETIC_PROMPT"
        }));
        SelectMeeting(second, loadSavedMix: false); int count = MeetingLogStore.Read(second).Entries.Count;
        RefreshMeetingLog(); Need(MeetingLogStore.Read(second).Entries.Count == count, "Legacy import is not idempotent.");
        Need(MeetingLogText.Text.Contains("42800") && MeetingLogText.Text.Contains("时间倒置") && !MeetingLogText.Text.Contains("PRIVATE_SYNTHETIC_PROMPT"), "Legacy ASR whitelist import failed.");
        string legacyExport = Path.Combine(evidence, "legacy-shared.txt"); MeetingLogStore.ExportText(second, legacyExport);
        Need(!File.ReadAllText(legacyExport).Contains("PRIVATE_UNLABELLED_SENTENCE") && File.ReadAllText(legacyExport).Contains("时间倒置"), "Legacy free text was shared or known error omitted.");
        ReplaceTranscriptPlayer(first); selectedMeetingDirectory = second;
        HandlePlaybackFailure(player, first, new IOException("SYNTHETIC_PLAYBACK_A_ERROR"));
        Need(MeetingLogStore.Read(first).Entries.Any(entry => entry.Message.Contains("SYNTHETIC_PLAYBACK_A_ERROR")) &&
            !MeetingLogStore.Read(second).Entries.Any(entry => entry.Message.Contains("SYNTHETIC_PLAYBACK_A_ERROR")), "Player failed against wrong meeting.");
        Need(DiagnosticLevel("混音失敗") == "error", "Traditional Chinese failure was labelled info.");
        SelectMeeting(first, loadSavedMix: false);
        string destination = Path.Combine(evidence, "shared-log.txt"); MeetingLogStore.ExportText(first, destination);
        string exported = File.ReadAllText(destination);
        Need(exported.Contains("SYNTHETIC_EXPORT_ERROR") && exported.Contains("SYNTHETIC_ASR_ERROR") &&
            !exported.Contains(dataRoot) && !exported.Contains("PRIVATE_SYNTHETIC_TITLE"), "Export lost diagnostic or leaked metadata.");
        var reopened = new MainWindow(enumerateSourcesOnLoad: false) { dataRoot = dataRoot };
        try
        {
            reopened.Show(); reopened.SelectMeeting(first, loadSavedMix: false);
            Need(reopened.MeetingLogText.Text.Contains("SYNTHETIC_EXPORT_ERROR"), "Log did not survive new window.");
        }
        finally { reopened.closeApproved = true; reopened.Close(); }
        foreach (var original in originals) Need(CloseFixtureHash(original.Key) == original.Value, "Original changed by Log workflow.");
        MeetingTabs.SelectedIndex = 4; await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
        var images = new List<string>(); SaveLayoutImage(evidence, "meeting-log.png", 1, images);
        Need(host is null && !microphoneEnabled && player.Source is null, "Log smoke opened capture or playback.");
        return new { Status = "PASS", SourceMode = "SYNTHETIC GENERATED FILES ONLY", CaptureStarted = false,
            MicrophoneCollected = false, MeetingIsolation = true, PersistedAcrossWindows = true,
            LegacyAttemptImportedWithoutPrompt = true, OriginalsUnchanged = true, Export = destination, Images = images };
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
