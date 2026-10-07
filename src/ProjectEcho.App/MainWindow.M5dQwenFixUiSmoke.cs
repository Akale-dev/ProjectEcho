using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using ProjectEcho.Core;
using ProjectEcho.Core.Diagnostics;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Saved-result UI contract only: generated text/files, no model, capture, microphone or playback.</summary>
    internal async Task<object> M5dQwenFixUiSmokeAsync(string evidence, CancellationToken token)
    {
        Need(host is null && !microphoneEnabled && !levelPreviewNativeEnabled && !transcriptBusy,
            "Qwen partial UI smoke requires the native-disabled idle window.");
        string fixture = Path.Combine(evidence, "qwen-partial-synthetic-data");
        Directory.CreateDirectory(fixture); dataRoot = fixture; DataRootText.Text = fixture;
        string first = Path.Combine(RecordingsRoot, "qwen-partial-a"), second = Path.Combine(RecordingsRoot, "qwen-partial-b");
        WriteCloseFixture(first); WriteCloseFixture(second);
        var originals = Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories).ToDictionary(path => path, CloseFixtureHash);
        var segments = new[]
        {
            new TranscriptSegment("first", 0, 400, null, "mixed", "合成有时间段一。", "approximate"),
            new TranscriptSegment("middle", null, null, null, "mixed", "合成待定位正文必须完整保存。", "untimed"),
            new TranscriptSegment("last", 1500, 1900, null, "mixed", "合成有时间段三。", "approximate")
        };
        var mixed = Import("mixed", segments);
        var all = Import("all", segments.Select(segment => segment with
            { StartMs = null, EndMs = null, TimestampQuality = "untimed" }).ToArray());
        var immutableRevisions = new Dictionary<string, string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(first, "transcripts", "original"), "*", SearchOption.AllDirectories))
            immutableRevisions.Add(file, CloseFixtureHash(file));
        string languageBefore = UiText.Language;
        var images = new List<string>();
        try
        {
            ChangeLanguage("zh-CN", persist: false); state = SessionState.Idle;
            SelectMeeting(first, loadSavedMix: false); NavigateTo(3);
            await transcriptWork.WaitAsync(token);
            var result = new LocalTranscriptionResult(LocalTranscriptionStatus.Succeeded, "", 1234, mixed,
                "SYNTHETIC_QWEN_TEXT_SAVED_ALIGNMENT_INCOMPLETE");
            await PresentQwenResultAsync(first, result);
            var view = transcriptionView!;
            Need(view.StatusText.Text.Contains("文字已保存，有 1 段待定位") && !view.StatusText.Text.StartsWith("Succeeded", StringComparison.Ordinal),
                "Qwen partial save was presented as unqualified alignment success.");
            Need(MeetingLogStore.Read(first).Entries.Any(entry => entry.Category == "asr" && entry.Level == "warning" &&
                entry.Message.Contains("SYNTHETIC_QWEN_TEXT_SAVED_ALIGNMENT_INCOMPLETE")), "Partial Qwen save was not logged as a warning.");
            Need(view.WorkspaceTabs.Items.Count == 4 && view.SegmentsBox.Items.Count == 3,
                "Partial text lost rows or the Pro speaker workspace.");
            view.SegmentsBox.SelectedIndex = 1; SelectTranscriptSegment(); UpdateControls();
            Need(view.ReviewAlignmentNotice.IsVisible && view.ReviewAlignmentNotice.Text.Contains("文字已保存，有 1 段待定位"),
                "Compact review hid the Qwen pending-alignment notice.");
            Need(!view.SeekButton.IsEnabled && !view.ExportSubtitlesButton.IsEnabled && view.ExportTextButton.IsEnabled,
                "Untimed Qwen actions either invented seek/subtitle time or blocked full text.");
            SeekTranscript(); Need(player.Source is null && pendingTranscriptSeek is null, "Untimed Qwen text started playback.");
            string txt = Path.Combine(evidence, "qwen-partial-full-text.txt");
            TranscriptSubtitleExporter.ExportText(first, mixed.RevisionId, txt, token);
            Need(File.ReadAllText(txt) == string.Join('\n', segments.Select(segment => segment.Text)) + "\n", "Qwen TXT dropped or reordered text.");
            foreach (string extension in new[] { ".srt", ".vtt" })
            {
                string destination = Path.Combine(evidence, "qwen-must-not-drop-text" + extension);
                bool rejected = false;
                try { TranscriptSubtitleExporter.Export(first, mixed.RevisionId, destination, token); }
                catch (InvalidDataException) { rejected = true; }
                Need(rejected && !File.Exists(destination), "Qwen subtitle export silently discarded untimed words.");
            }
            string json = Path.Combine(evidence, "qwen-saved-revision-roundtrip.json");
            File.Copy(Path.Combine(first, "transcripts", "original", mixed.RevisionId, "revision.json"), json, overwrite: false);
            var roundTrip = TranscriptService.Import(new(first, Path.GetFileName(first), 2000, json, IsSynthetic: true), token);
            Need(roundTrip.Segments.SequenceEqual(mixed.Segments), "Stored Qwen revision JSON did not preserve timed and untimed text on reimport.");
            await LoadTranscriptVersionsAsync(mixed.RevisionId);
            Need(view.StatusText.Text.Contains("文字已保存，有 1 段待定位") && mixed.Warnings.All(warning => view.StatusText.Text.Contains(UiText.T(warning))),
                "Reload overwrote the pending-alignment summary or original warnings.");
            foreach (string language in new[] { "en", "zh-Hant", "zh-CN" })
            {
                ChangeLanguage(language, persist: false);
                await LoadTranscriptVersionsAsync(mixed.RevisionId);
                string notice = QwenPendingAlignmentNotice(mixed);
                Need(view.StatusText.Text.Contains(notice) && (language switch
                {
                    "en" => notice.Contains("Text saved; 1 segments still need timing."),
                    "zh-Hant" => notice.Contains("文字已儲存，有 1 段待定位。"),
                    _ => notice.Contains("文字已保存，有 1 段待定位。")
                }), "Qwen pending-alignment notice was missing or not localized after reload.");
            }
            var allResult = result with { Revision = all };
            await PresentQwenResultAsync(first, allResult);
            Need(view.StatusText.Text.Contains("文字已保存，有 3 段待定位") && view.SegmentsBox.Items.Count == 3 &&
                view.SegmentsBox.Items.Cast<TranscriptRow>().All(row => row.Segment.StartMs is null),
                "All pending windows were called aligned sentences or lost text.");

            SelectMeeting(second, loadSavedMix: false); await transcriptWork.WaitAsync(token);
            string unrelatedStatus = view.StatusText.Text;
            await PresentQwenResultAsync(first, result);
            Need(selectedMeetingDirectory == second && view.StatusText.Text == unrelatedStatus &&
                !MeetingLogStore.Read(second).Entries.Any(entry => entry.Message.Contains("SYNTHETIC_QWEN_TEXT_SAVED_ALIGNMENT_INCOMPLETE")),
                "Late Qwen completion changed another meeting's UI or Log.");

            var reopened = new MainWindow(enumerateSourcesOnLoad: false) { dataRoot = fixture };
            try
            {
                reopened.Show(); reopened.SelectMeeting(first, loadSavedMix: false); reopened.NavigateTo(3);
                await reopened.transcriptWork.WaitAsync(token);
                await reopened.LoadTranscriptVersionsAsync(all.RevisionId);
                Need(reopened.transcriptionView!.StatusText.Text.Contains("文字已保存，有 3 段待定位") &&
                    all.Warnings.All(warning => reopened.transcriptionView.StatusText.Text.Contains(UiText.T(warning))),
                    "Reopening the application lost the Qwen pending-alignment result.");
                await reopened.Dispatcher.InvokeAsync(reopened.UpdateLayout, DispatcherPriority.Render, token);
                reopened.SaveLayoutImage(evidence, "qwen-pending-reopened.png", 1, images);
                Need(reopened.host is null && !reopened.microphoneEnabled && reopened.player.Source is null,
                    "Reopen validation started a media device.");
            }
            finally { reopened.closeApproved = true; reopened.Close(); }
            foreach (var original in originals.Concat(immutableRevisions))
                Need(CloseFixtureHash(original.Key) == original.Value, "Qwen UI changed original audio, metadata or a saved revision.");
            Need(host is null && !microphoneEnabled && player.Source is null, "Qwen UI validation started capture or playback.");
            return new { Status = "PASS", SyntheticOnly = true, ModelExecuted = false, CaptureStarted = false,
                MicrophoneCollected = false, AudioPlayed = false, PartialSavedWarning = true, AllUntimedCount = 3,
                ReloadAndReopenWarningRetained = true, OriginalWarningsPreserved = true, MeetingIsolation = true,
                ThreeLanguagePendingNotice = true,
                UntimedSeekDisabled = true, StrictSubtitleExport = true, TxtComplete = true,
                SavedRevisionJsonRoundTrip = true, ProWorkspacePreserved = true, OriginalsUnchanged = true, Images = images };
        }
        finally { ChangeLanguage(languageBefore, persist: false); }

        TranscriptRevision Import(string suffix, IReadOnlyList<TranscriptSegment> values)
        {
            string input = Path.Combine(evidence, "qwen-partial-" + suffix + ".json");
            File.WriteAllText(input, JsonSerializer.Serialize(new { schemaVersion = 1, isSynthetic = true,
                meetingId = Path.GetFileName(first), revisionId = "fixture", parentRevisionId = (string?)null,
                provider = "Synthetic UI fixture", inputExportId = (string?)null, segments = values },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return TranscriptService.Import(new(first, Path.GetFileName(first), 2000, input,
                "Qwen3-ASR / transcribe.cpp（本地，待人工校对）", "Synthetic UI contract; no model executed", IsSynthetic: true), token);
        }
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
