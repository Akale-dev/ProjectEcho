using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Actual WPF editing, muted generated-audio seeking, batching and close guard. No capture.</summary>
    internal async Task<object> M3jTranscriptEditingSmokeAsync(string evidence, CancellationToken token)
    {
        Need(host is null && !microphoneEnabled, "Draft smoke must not collect audio.");
        dataRoot = Path.Combine(evidence, "synthetic-draft-data"); Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        string directory = Path.Combine(RecordingsRoot, "draft-meeting");
        string other = Path.Combine(RecordingsRoot, "other-draft-meeting");
        await Task.Run(() => { WriteCloseFixture(directory); WriteCloseFixture(other); M2ExportService.RenderMeeting(directory, encoder: null); }, token);
        string input = Path.Combine(evidence, "draft-cues.srt");
        File.WriteAllText(input, "1\n00:00:00,100 --> 00:00:00,600\n第一句原文\n\n2\n00:00:01,100 --> 00:00:01,800\n第二句原文\n");
        var original = TranscriptService.Import(new(directory, "draft-meeting", 2000, input, "Synthetic drafts", IsSynthetic: true), token);
        var alternative = TranscriptService.Import(new(directory, "draft-meeting", 2000, input, "Synthetic alternate model", IsSynthetic: true), token);
        var otherRevision = TranscriptService.Import(new(other, "other-draft-meeting", 2000, input, "Synthetic other", IsSynthetic: true), token);
        var hashes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => path, CloseFixtureHash);
        var view = transcriptionView!; var images = new List<string>();
        player.Volume = 0; player.IsMuted = true;
        try
        {
            SelectMeeting(directory); await savedMixWork.WaitAsync(token); await replayLoadWork.WaitAsync(token);
            NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 1; await transcriptWork.WaitAsync(token);
            await LoadTranscriptVersionsAsync(original.RevisionId);
            view.SegmentsBox.SelectedIndex = 0;
            Need(view.StartTimestampBox.Text == "00:00:00.100" && view.EndTimestampBox.Text == "00:00:00.600", "Editor lost millisecond precision.");
            view.EditBox.Text = "第一句草稿"; view.StartTimestampBox.Text = "00:00:00.230"; view.EndTimestampBox.Text = "00:00:00.670";
            Need(view.StartTimestampBox.CentisecondsBox.Text == "23", "Selected-cue summary kept the original time.");
            view.SegmentsBox.SelectedIndex = 1; view.EditBox.Text = "第二句草稿";
            view.StartTimestampBox.Text = "00:00:"; // Deliberately unfinished input must survive selection and navigation.
            Need(HasValidationIssue(view.StartTimestampBox), "Invalid draft still displayed an apparently valid old timestamp.");
            view.SegmentsBox.SelectedIndex = 0;
            Need(view.EditBox.Text == "第一句草稿" && view.StartTimestampBox.Text == "00:00:00.230", "Selecting another cue lost text/time drafts.");
            await LoadTranscriptVersionsAsync(alternative.RevisionId); view.SegmentsBox.SelectedIndex = 0;
            Need(view.EditBox.Text == "第一句原文", "Draft leaked into another model version.");
            view.EditBox.Text = "另一版本草稿";
            SelectMeeting(other, loadSavedMix: false); await transcriptWork.WaitAsync(token); await LoadTranscriptVersionsAsync(otherRevision.RevisionId);
            view.SegmentsBox.SelectedIndex = 0; view.EditBox.Text = "另一场会议草稿";
            SelectMeeting(directory); await savedMixWork.WaitAsync(token); await replayLoadWork.WaitAsync(token); await transcriptWork.WaitAsync(token);
            await LoadTranscriptVersionsAsync(original.RevisionId);
            Need(view.EditBox.Text == "第一句草稿" && view.StartTimestampBox.Text == "00:00:00.230", "Returning to meeting/version lost draft selection.");
            view.SegmentsBox.SelectedIndex = 1;
            Need(view.StartTimestampBox.Text == "00:00:" && !view.SeekButton.IsEnabled, "Unfinished time was discarded or playable.");
            view.SaveRevisionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(TranscriptService.ListRevisions(directory).Count == 2 && CurrentTranscriptDraft?.ChangedSegmentCount == 2,
                "Invalid batch partially saved or lost drafts.");
            view.StartTimestampBox.Text = "00:00:01.120"; view.EndTimestampBox.Text = "00:00:02.001";
            Need(!view.SeekButton.IsEnabled && HasValidationIssue(view.EndTimestampBox), "Out-of-audio timestamp was accepted.");
            view.EndTimestampBox.Text = "00:00:01.870";
            view.SegmentsBox.SelectedIndex = 0;
            Need(view.SeekButton.IsEnabled && !view.ExportSubtitlesButton.IsEnabled && !view.ExportTextButton.IsEnabled,
                "Valid draft seek or save-before-export state is wrong.");

            // Call the real corrected-time seek handler, then pause generated audio at the requested position.
            SeekTranscript();
            Need(pendingTranscriptSeek?.Milliseconds == 230 || playbackOpened && player.Position.TotalMilliseconds >= 230 && player.Position.TotalMilliseconds < 400,
                "Review handler queued the original timestamp instead of the edited timestamp.");
            var clock = Stopwatch.StartNew();
            while (!playbackOpened && clock.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25, token);
            Need(playbackOpened && player.IsMuted && player.Volume == 0, "Muted synthetic playback did not open.");
            player.Pause(); playbackPaused = true; SeekPlaybackMilliseconds(230); ResetDisplayPlaybackClock();
            await Task.Delay(120, token);
            Need(Math.Abs(player.Position.TotalMilliseconds - 230) < 1 &&
                TryGetSelectedDraftSegment(out var corrected, out _) && corrected.StartMs == 230,
                "Corrected timestamp did not reach native WPF playback.");
            ResetTranscriptPlayback();

            int prompted = 0;
            confirmDiscardTranscriptDraftsForSmoke = count => { prompted = count; return false; };
            var close = new CancelEventArgs(); Window_Closing(this, close);
            Need(close.Cancel && prompted == 3 && !WindowCloseRequested && windowCloseWork is null,
                "Unsaved close did not protect all meeting/model drafts before shutdown.");
            confirmDiscardTranscriptDraftsForSmoke = count => count == 3;
            Need(ConfirmDiscardTranscriptDrafts(), "Explicit discard choice was ignored.");
            confirmDiscardTranscriptDraftsForSmoke = null;

            foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
            {
                ChangeLanguage(language, persist: false); Width = 1280; Height = 930;
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
                Need(view.StartTimestampLabel.Text == (language == "en" ? "Start" : language == "zh-Hant" ? "開始" : "开始"),
                    "Timestamp label did not localize.");
                Point left = view.StartTimestampBox.TranslatePoint(new Point(), view.SubtitleEditorGrid);
                Point right = view.EditBox.TranslatePoint(new Point(), view.SubtitleEditorGrid);
                Need(left.X + view.StartTimestampBox.ActualWidth <= right.X && view.StartTimestampBox.ActualWidth >= 100,
                    "Timestamp fields are not visibly left of the text editor.");
                SaveLayoutImage(evidence, "draft-editor-" + language + ".png", 1, images);
            }
            ChangeLanguage("zh-CN", persist: false);
            view.SaveRevisionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            var saved = visibleRevision!;
            Need(TranscriptService.ListRevisions(directory).Count == 3 && saved.ParentRevisionId == original.RevisionId &&
                saved.Segments[0] is { Text: "第一句草稿", StartMs: 230, EndMs: 670 } &&
                saved.Segments[1] is { Text: "第二句草稿", StartMs: 1120, EndMs: 1870 } && !CurrentTranscriptDraft!.HasChanges,
                "Save did not create one complete batch revision.");
            view.SaveRevisionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(TranscriptService.ListRevisions(directory).Count == 3, "Repeated save created an empty revision.");
            string exported = Path.Combine(evidence, "saved-draft.srt");
            StartTranscriptWork(() => ExportTranscriptSubtitlesAsync(exported)); await transcriptWork.WaitAsync(token);
            Need(File.ReadAllText(exported).Contains("00:00:00,230 --> 00:00:00,670"), "Saved export kept the old time.");
            await LoadTranscriptVersionsAsync(alternative.RevisionId); view.SegmentsBox.SelectedIndex = 0;
            Need(view.EditBox.Text == "另一版本草稿", "Saving one batch erased another version's draft.");

            // Search from A, edit two hits in B, then save twice. The second revision must
            // extend the first saved batch rather than silently return to B's original.
            view.SearchBox.Text = "原文"; view.AllMeetingsBox.IsChecked = true;
            view.SearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            var otherHits = view.SegmentsBox.Items.Cast<TranscriptRow>()
                .Where(row => row.Directory == other && row.Revision.RevisionId == otherRevision.RevisionId).ToArray();
            Need(otherHits.Length == 2 && selectedMeetingDirectory == directory && otherHits.All(row => row.Title == MeetingDisplayName(other)),
                "Cross-meeting search lost the source meeting or did not exercise external hits.");
            view.SegmentsBox.SelectedItem = otherHits[0]; view.EditBox.Text = "跨会检索第一句";
            view.StartTimestampBox.Text = "00:00:00.250"; view.EndTimestampBox.Text = "00:00:00.700";
            view.SegmentsBox.SelectedItem = otherHits[1]; view.EditBox.Text = "跨会检索第二句";
            view.StartTimestampBox.Text = "00:00:01.200"; view.EndTimestampBox.Text = "00:00:01.900";
            view.SaveRevisionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            var searchSaved = visibleRevision!;
            Need(selectedMeetingDirectory == other && searchSaved.ParentRevisionId == otherRevision.RevisionId &&
                searchSaved.Segments[0] is { Text: "跨会检索第一句", StartMs: 250, EndMs: 700 } &&
                searchSaved.Segments[1] is { Text: "跨会检索第二句", StartMs: 1200, EndMs: 1900 } &&
                visibleSegment?.Revision.RevisionId == searchSaved.RevisionId && !CurrentTranscriptDraft!.HasChanges,
                "Cross-meeting save left the old search draft or omitted part of the batch.");
            view.SegmentsBox.SelectedIndex = 0;
            Need(view.EditBox.Text == "跨会检索第一句" && view.StartTimestampBox.Text == "00:00:00.250" &&
                view.SegmentsBox.Items.Cast<TranscriptRow>().All(row => row.Revision.RevisionId == searchSaved.RevisionId),
                "Saved search rows and edit controls refer to different versions.");
            view.SegmentsBox.SelectedIndex = 1; view.EditBox.Text = "跨会检索第二句再修订";
            view.SaveRevisionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(visibleRevision!.ParentRevisionId == searchSaved.RevisionId && TranscriptService.ListRevisions(other).Count == 3 &&
                visibleRevision.Segments[0] is { Text: "跨会检索第一句", StartMs: 250, EndMs: 700 } &&
                visibleRevision.Segments[1] is { Text: "跨会检索第二句再修订", StartMs: 1200, EndMs: 1900 },
                "Continued search-result editing branched from the original and lost saved corrections.");
            string searchExport = Path.Combine(evidence, "saved-search-draft.srt");
            StartTranscriptWork(() => ExportTranscriptSubtitlesAsync(searchExport)); await transcriptWork.WaitAsync(token);
            Need(File.ReadAllText(searchExport).Contains("00:00:00,250 --> 00:00:00,700") &&
                File.ReadAllText(searchExport).Contains("跨会检索第二句再修订"), "Search-result revision export lost the saved corrections.");
            await savedMixWork.WaitAsync(token); await replayLoadWork.WaitAsync(token);
            SelectMeeting(directory); await savedMixWork.WaitAsync(token); await replayLoadWork.WaitAsync(token); await transcriptWork.WaitAsync(token);
            await LoadTranscriptVersionsAsync(alternative.RevisionId); view.SegmentsBox.SelectedIndex = 0;
            Need(view.EditBox.Text == "另一版本草稿", "Cross-meeting save erased the starting meeting's unrelated draft.");
            Need(hashes.All(pair => CloseFixtureHash(pair.Key) == pair.Value) && host is null && !microphoneEnabled,
                "Editing changed originals or started capture.");
            return new { Status = "PASS", SourceMode = "SYNTHETIC FILES / MUTED WPF / NO CAPTURE", BatchCues = 2,
                OneRevisionPerExplicitSave = true, InvalidBatchAtomic = true, MillisecondTimeEditing = true,
                NativeCorrectedSeekMs = 230, DraftsPreservedAcrossMeetingsVersionsAndRefresh = true,
                CrossMeetingSearchSavesContinueFromSavedRevision = true, DraftTimeSummaryTracksValidAndInvalidInput = true,
                CloseRejectsUnsavedDraftLoss = true, PromptedVersions = prompted, OriginalHashesUnchanged = true,
                Languages = new[] { "zh-CN", "zh-Hant", "en" }, Images = images, CaptureStarted = false, MicrophoneCollected = false };
        }
        finally { ResetTranscriptPlayback(); PrepareTranscriptDraftSmokeShutdown(); }
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
