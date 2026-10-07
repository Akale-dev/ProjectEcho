using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProjectEcho.Core;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    internal async Task<object> M3lUiSmokeAsync(string evidence, CancellationToken token)
    {
        if (host is not null || microphoneEnabled || levelPreviewNativeEnabled) throw new InvalidOperationException("No-capture test only.");
        dataRoot = Path.Combine(evidence, "synthetic-data"); Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        string meeting = Path.Combine(RecordingsRoot, "subtitles");
        WriteCloseFixture(meeting); M2ExportService.RenderMeeting(meeting, encoder: null);
        string srt = Path.Combine(evidence, "original.srt");
        File.WriteAllText(srt, "1\n00:00:00,123 --> 00:00:00,567\nSynthetic first cue\n\n2\n00:00:01,103 --> 00:00:01,569\nSynthetic second cue\n");
        var original = TranscriptService.Import(new(meeting, "subtitles", 2000, srt, "Synthetic M3L", IsSynthetic: true), token);
        var images = new List<string>(); var sizes = new List<object>(); var windows = new List<MainWindow>();
        try
        {
            SelectMeeting(meeting); await savedMixWork.WaitAsync(token); await replayLoadWork.WaitAsync(token); await transcriptWork.WaitAsync(token);
            NavigateTo(3); var view = transcriptionView!; view.WorkspaceTabs.SelectedIndex = 1;
            await LoadTranscriptVersionsAsync(original.RevisionId); view.SegmentsBox.SelectedIndex = 0;
            Need(view.StartTimestampBox.CentisecondsBox.Text == "12" && view.StartTimestampBox.Text == "00:00:00.123" &&
                !CurrentTranscriptDraft!.HasChanges, "Displaying two decimals changed the stored millisecond timestamp.");
            view.EditBox.Text = "Text-only correction";
            view.SaveRevisionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            var textOnly = visibleRevision!;
            Need(textOnly.Segments[0].StartMs == 123 && textOnly.Segments[0].EndMs == 567, "Text-only save rounded untouched times.");
            view.StartTimestampBox.CentisecondsBox.Text = "25";
            Need(TryGetSelectedDraftSegment(out var edited, out _) && edited.StartMs == 250 && edited.EndMs == 567,
                "A hundredths edit changed the other boundary or has the wrong scale.");
            view.SaveRevisionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(visibleRevision!.Segments[0].StartMs == 250 && visibleRevision.Segments[0].EndMs == 567,
                "Hundredths edit did not persist with unchanged opposite boundary.");
            var frame = view.StartTimestampBox;
            frame.Text = "00:00:01.999";
            Need(frame.SecondsBox.Text == "01" && frame.CentisecondsBox.Text == "99" && frame.Text.EndsWith(".999"),
                "Display rounded an original timestamp past EOF or erased precision.");
            await LoadTranscriptVersionsAsync(textOnly.RevisionId); view.SegmentsBox.SelectedIndex = 0;
            string? surfaced = null; reviewErrorForSmoke = message => surfaced = message;
            view.EndTimestampBox.SecondsBox.Text = "59";
            view.SaveRevisionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(surfaced is not null && HasValidationIssue(view.EndTimestampBox) && !view.TranscriptStatusPanel.IsVisible,
                "Removing the status panel swallowed an invalid-save error.");
            view.EndTimestampBox.Text = "00:00:00.567";
            surfaced = null; view.SearchBox.Text = "";
            view.SearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(surfaced is not null && !view.TranscriptStatusPanel.IsVisible, "An explicit search failure lost its error feedback.");
            foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
            {
                ChangeLanguage(language, persist: false); Width = 1280; Height = 930;
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
                await Task.Delay(60, token);
                Need(new[] { "SegmentTimeText", "TimestampFormatHint", "TimestampInputContextText", "DraftStatusText" }
                    .All(name => view.FindName(name) is null), "A removed explanatory row remains in the review view.");
                Need(!view.TranscriptStatusPanel.IsVisible && !view.CancelButton.IsVisible && !view.StatusText.IsVisible &&
                    view.ReviewSearchRow.ActualHeight == 32 && view.SearchBox.ActualHeight == 32 &&
                    !view.SearchBox.AcceptsReturn && view.SegmentsBox.ActualHeight >= 240 && frame.ActualWidth == 118 && frame.CentisecondsBox.MaxLength == 2,
                    $"Review space was not reclaimed: list={view.SegmentsBox.ActualHeight}, search={view.SearchBox.ActualHeight}.");
                sizes.Add(new { language, WindowWidth = ActualWidth, WindowHeight = ActualHeight,
                    ListHeight = view.SegmentsBox.ActualHeight, SearchHeight = view.SearchBox.ActualHeight, TimeWidth = frame.ActualWidth });
                SaveLayoutImage(evidence, "m3l-review-" + language + ".png", 1, images);
                view.WorkspaceTabs.SelectedIndex = 0; await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
                Need(view.TranscriptStatusPanel.IsVisible, "ASR progress/cancellation was removed with the review footer.");
                view.WorkspaceTabs.SelectedIndex = 1;
            }
            ChangeLanguage("zh-CN", persist: false);
            string pref = Path.Combine(evidence, "startup-settings", "data-root.json");
            PortableDataRootPreferences.Save(pref, projectRoot, dataRoot);
            var startup = new MainWindow(false, pref, refreshLibraryOnLoad: true); windows.Add(startup); startup.Show();
            await startup.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
            await startup.startupLibraryWork.WaitAsync(token);
            Need(startup.libraryRead && startup.RecoveryBox.Items.Count == 1 && startup.dataRoot == dataRoot &&
                startup.selectedMeetingDirectory is null && startup.player.Source is null && startup.host is null,
                "Startup did not automatically populate the remembered library without opening a meeting/audio.");
            string missing = Path.Combine(evidence, "missing-data"); Directory.CreateDirectory(missing);
            PortableDataRootPreferences.Save(pref, projectRoot, missing); Directory.Delete(missing);
            var unavailable = new MainWindow(false, pref, refreshLibraryOnLoad: true); windows.Add(unavailable); unavailable.Show();
            await unavailable.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
            await unavailable.startupLibraryWork.WaitAsync(token);
            Need(!unavailable.libraryRead && !Directory.Exists(missing) && unavailable.dataRoot == missing,
                "Startup refresh replaced/recreated an unavailable remembered directory.");
            return new { Status = "PASS", ReviewRowsRemoved = true, ReviewFooterRemoved = true, SearchSingleLine = true,
                HundredthsEditor = true, UntouchedMillisecondsPreserved = true, TextOnlySavePreservesTime = true,
                ActionErrorsSurfaced = true, StartupLibraryRefreshed = true, NoAutomaticPlayback = true,
                UnavailableRootUnchanged = true, Measurements = sizes, Images = images, CaptureStarted = false, MicrophoneCollected = false };
        }
        finally
        {
            reviewErrorForSmoke = null; PrepareTranscriptDraftSmokeShutdown();
            foreach (var window in windows) { window.PrepareTranscriptDraftSmokeShutdown(); window.Close(); }
        }
        static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    }
}
