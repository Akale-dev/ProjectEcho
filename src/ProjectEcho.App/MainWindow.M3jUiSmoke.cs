using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProjectEcho.Core;
using ProjectEcho.Core.Diagnostics;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Actual WPF and synthetic local files only; no recording, microphone, or playback.</summary>
    internal async Task<object> M3jUiSmokeAsync(string evidence, CancellationToken token)
    {
        Need(!levelPreviewNativeEnabled && host is null && !microphoneEnabled && !microphoneRequested,
            "M3J smoke requires the native-disabled/no-capture window.");
        string fixture = Path.Combine(evidence, "m3j-synthetic-data");
        Directory.CreateDirectory(fixture); dataRoot = fixture; DataRootText.Text = fixture;
        string first = Path.Combine(RecordingsRoot, "history-a"), second = Path.Combine(RecordingsRoot, "recording-b");
        string third = Path.Combine(RecordingsRoot, "next-recording-c");
        WriteCloseFixture(first); WriteCloseFixture(second); WriteCloseFixture(third);
        var originals = Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories).ToDictionary(path => path, CloseFixtureHash);
        var images = new List<string>(); var layouts = new List<object>(); var durations = new List<object>();
        var gates = new List<MixProgressSmokeGate>(); var previousRenderer = renderProgressOperation;
        string languageBefore = UiText.Language; double widthBefore = Width, heightBefore = Height;
        try
        {
            ChangeLanguage("zh-CN", persist: false); state = SessionState.Idle; ClearRecordingProcessing();
            Need(RecordingProcessingStatus.Text.Contains("本场录音保存后") && recordingProcessingDirectory is null,
                "Fresh startup shows an unrelated history receipt.");

            // Deliberately different wall-clock bounds prove the label uses persisted duration only.
            foreach (var item in new[]
            {
                (Name: "pause-excluded", Duration: (long?)11000, Imported: false, Policy: RecordingTimeline.ExcludePaused),
                (Name: "legacy-elapsed", Duration: (long?)28345, Imported: false, Policy: RecordingTimeline.Elapsed),
                (Name: "imported", Duration: (long?)12567, Imported: true, Policy: "imported"),
                (Name: "unknown", Duration: (long?)null, Imported: false, Policy: RecordingTimeline.Elapsed)
            })
            {
                string directory = Path.Combine(RecordingsRoot, item.Name);
                WriteDurationFixture(directory, item.Duration, item.Imported, item.Policy);
                SelectMeeting(directory, loadSavedMix: false); NavigateTo(1); UpdateControls();
                Need(selectedDurationMs == item.Duration && (item.Duration is long value
                    ? MeetingDurationText.Text == UiText.F($"时长：{FormatElapsed(value * 1000)}")
                    : MeetingDurationText.Text == UiText.T("时长：未知（元数据不可用）")), "Meeting duration used wall time, another meeting, or invented unknown metadata.");
                durations.Add(new { item.Name, item.Duration, Display = MeetingDurationText.Text, MetadataOnlySyntheticFixture = true });
            }

            SetMeetingExportStatus(first, "SYNTHETIC_HISTORY_A_RESULT", persist: true);
            BeginRecordingProcessing(second, "Synthetic recording B");
            state = SessionState.Recording; UpdateControls();
            SelectMeeting(first, loadSavedMix: false);
            Need(RecordingProcessingMeetingText.Text.Contains("Synthetic recording B") &&
                !RecordingProcessingStatus.Text.Contains("HISTORY_A") && !PlaybackButton.IsEnabled,
                "Browsing A during recording B changed recording-card ownership or enabled playback.");
            state = SessionState.Paused; UpdateControls();
            Need(!PlaybackButton.IsEnabled && !PausePlaybackButton.IsEnabled && player.Source is null,
                "Paused recording enabled application playback.");
            state = SessionState.Stopping; UpdateControls(); Need(!PlaybackButton.IsEnabled, "Saving enabled playback.");
            state = SessionState.Saved; MarkRecordingProcessingSaved(first);
            Need(recordingProcessingDirectory == second && RecordingProcessingStatus.Text.Contains("录音结束并保存"),
                "An unrelated save receipt rebound this recording card.");
            MarkRecordingProcessingSaved(second); UpdateControls(); NavigateTo(0);

            var successGate = NewGate(); renderProgressOperation = successGate.Run;
            QueueMix(second); var reporter = await successGate.Entered.Task.WaitAsync(token); await Pump();
            string historyText = ExportStatus.Text;
            int logBefore = MeetingLogStore.Read(second).Entries.Count;
            reporter.Report(new(MixStages.Rendering, 40, 100)); await Pump();
            Need(RecordingProcessingStatus.Text.Contains("40.0%") && ExportStatus.Text == historyText &&
                !ExportStatus.Text.Contains("40.0%"), "Recording B progress failed to display independently of selected history A.");
            Need(MeetingLogStore.Read(second).Entries.Count == logBefore, "Progress refresh wrote an extra Log.");

            Width = MinWidth; Height = MinHeight;
            foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
            {
                ChangeLanguage(language, persist: false); NavigateTo(0);
                reporter.Report(new(language == "zh-CN" ? MixStages.Hashing : language == "zh-Hant" ? MixStages.Validating : MixStages.Rendering, 40, 100));
                await Pump(); RecordingProcessingCard.BringIntoView();
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
                var rootGrid = (Grid)Content;
                double versionRight = ApplicationVersionText.TranslatePoint(new Point(ApplicationVersionText.ActualWidth, 0), rootGrid).X;
                double logoLeft = BrandLogo.TranslatePoint(new Point(), rootGrid).X;
                Need(rootGrid.ColumnDefinitions[0].Width.Value == 246 && Math.Abs(rootGrid.ColumnDefinitions[0].ActualWidth - 246) < 0.1 &&
                    BrandHeaderPanel.Margin.Left == -9 && logoLeft >= 0 && logoLeft <= 6 && versionRight <= 245 &&
                    BrandNameText.FontSize == 19 && ApplicationVersionText.FontSize == 10 && ApplicationVersionText.Text == "Ver. M3J Lite",
                    "Brand group did not move left intact, or changed sidebar width/font size to hide clipping.");
                Need(ApplicationVersionText.ActualWidth > 0 && BrandHeaderPanel.IsVisible && RecordingProcessingCard.IsVisible &&
                    RecordingProcessingHeading.Text == UiText.T("录后处理"), "Version or localized recording card is absent.");
                // Both are actual WPF renders; 200% is a render scale, not a claim of OS DPI change.
                foreach (double scale in new[] { 1d, 2d })
                    SaveLayoutImage(evidence, $"m3j-recording-{language}-{scale * 100:0}.png", scale, images);
                SelectMeeting(second, loadSavedMix: false); NavigateTo(1); await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
                Need(MeetingDurationText.Text == UiText.F($"时长：{FormatElapsed(2_000_000)}"), "Duration label did not localize or follow selection.");
                SaveLayoutImage(evidence, $"m3j-metadata-{language}.png", 1, images);
                layouts.Add(new { Language = language, SidebarWidth = rootGrid.ColumnDefinitions[0].ActualWidth,
                    BrandLeft = logoLeft, VersionRight = versionRight, BrandNameText.FontSize, VersionFontSize = ApplicationVersionText.FontSize });
                SelectMeeting(first, loadSavedMix: false); NavigateTo(0);
            }
            ChangeLanguage("zh-CN", persist: false); successGate.Release.Set(); await backgroundWork.WaitAsync(token); await Pump();
            Need(RecordingProcessingStatus.Text.Contains("完整 WAV 已校验") && RecordingProcessingStatus.Text.Contains("总耗时") &&
                ExportStatus.Text.Contains("HISTORY_A"), "B success receipt was lost or replaced A's export status.");
            string success = RecordingProcessingStatus.Text;
            reporter.Report(new(MixStages.Rendering, 99, 100)); await Pump();
            Need(RecordingProcessingStatus.Text == success, "Late completed progress replaced the final receipt.");
            meetingExportStatuses.Remove(second);
            SelectMeeting(second, loadSavedMix: false); SelectMeeting(first, loadSavedMix: false);
            Need(RecordingProcessingStatus.Text.Contains("上次处理记录") && RecordingProcessingStatus.Text.Contains("完整 WAV 已校验"),
                "The recording card did not restore its own persisted receipt on history switching.");

            var failureGate = NewGate(fail: true); renderProgressOperation = failureGate.Run;
            QueueMix(second); await failureGate.Entered.Task.WaitAsync(token); failureGate.Release.Set();
            await backgroundWork.WaitAsync(token); await Pump();
            Need(RecordingProcessingStatus.Text.Contains("混音失败") && RecordingProcessingStatus.Text.Contains("SYNTHETIC_NO_PCM_FAILURE"),
                "The recording card hid a processing failure.");
            var cancelGate = NewGate(); renderProgressOperation = cancelGate.Run;
            QueueMix(second); var cancelledReporter = await cancelGate.Entered.Task.WaitAsync(token);
            CancelExport_Click(this, new RoutedEventArgs()); await backgroundWork.WaitAsync(token); await Pump();
            string cancelled = RecordingProcessingStatus.Text;
            Need(cancelled.Contains("混音已取消"), "The recording card did not show cancellation.");
            cancelledReporter.Report(new(MixStages.Completed, 1, 1)); await Pump();
            Need(RecordingProcessingStatus.Text == cancelled, "Late cancelled progress changed its receipt.");

            var oldGate = NewGate(); renderProgressOperation = oldGate.Run;
            QueueMix(second); var oldReporter = await oldGate.Entered.Task.WaitAsync(token); await Pump();
            BeginRecordingProcessing(third, "Synthetic next recording C"); state = SessionState.Recording; UpdateControls();
            string nextStatus = RecordingProcessingStatus.Text;
            oldReporter.Report(new(MixStages.TruePeakAnalysis, 80, 100)); await Pump();
            oldGate.Release.Set(); await backgroundWork.WaitAsync(token); await Pump();
            Need(recordingProcessingDirectory == third && RecordingProcessingStatus.Text == nextStatus &&
                RecordingProcessingMeetingText.Text.Contains("Synthetic next recording C") && !PlaybackButton.IsEnabled,
                "Finishing old B after starting C contaminated C's card or enabled playback.");
            foreach (var original in originals) Need(CloseFixtureHash(original.Key) == original.Value, "The UI changed original audio or clock metadata.");
            Need(host is null && !microphoneEnabled && player.Source is null, "The display smoke started audio collection or playback.");
            return new { Status = "PASS", Version = "M3J Lite", SyntheticOnly = true,
                CaptureStarted = false, MicrophoneCollected = false, PlaybackStarted = false,
                Layouts = layouts, Durations = durations, Images = images, RecordingRequestOwnerIndependentOfSelection = true,
                BeginApplyEndAndRestoredReceiptVisible = true, SuccessFailureCancelAndNextRecording = true,
                ProgressDoesNotWriteLog = true, OriginalAudioAndMetadataUnchanged = true,
                ScaleCheck = "Actual WPF render at 100%/200%; OS DPI switch NOT_RUN" };
        }
        finally
        {
            exportCancellation?.Cancel(); foreach (var gate in gates) gate.Release.Set();
            if (!backgroundWork.IsCompleted) await backgroundWork.WaitAsync(TimeSpan.FromSeconds(20));
            foreach (var gate in gates) gate.Dispose();
            renderProgressOperation = previousRenderer; state = SessionState.Idle;
            ClearRecordingProcessing(); Width = widthBefore; Height = heightBefore;
            ChangeLanguage(languageBefore, persist: false); UpdateControls();
        }

        MixProgressSmokeGate NewGate(bool fail = false)
        { var gate = new MixProgressSmokeGate(fail); gates.Add(gate); return gate; }
        async Task Pump() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background, token);
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        static void WriteDurationFixture(string directory, long? durationMs, bool imported, string policy)
        {
            Directory.CreateDirectory(directory);
            var values = new Dictionary<string, object>
            {
                ["schemaVersion"] = imported ? AudioImportService.SchemaVersion : "m1-recording-1",
                ["title"] = "Synthetic duration metadata", ["project"] = "未分类", ["isSynthetic"] = true,
                ["startedAtUtc"] = "2026-01-01T00:00:00Z", ["endedAtUtc"] = "2026-01-01T05:00:00Z",
                ["timelinePolicy"] = policy, ["storageComplete"] = true
            };
            if (durationMs is long value) values["duration100ns"] = value * 10000;
            if (imported) values["importedAt"] = "2026-02-01T00:00:00Z";
            File.WriteAllText(Path.Combine(directory, "meeting.json"), JsonSerializer.Serialize(values));
        }
    }
}
