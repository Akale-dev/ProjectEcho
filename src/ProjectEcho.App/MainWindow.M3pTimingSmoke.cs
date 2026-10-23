using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    internal async Task<object> M3pTimingSmokeAsync(string evidence, CancellationToken token)
    {
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        Need(host is null && !microphoneEnabled && !levelPreviewNativeEnabled, "Timing test must not collect audio.");
        dataRoot = Path.Combine(evidence, "synthetic-data"); Directory.CreateDirectory(dataRoot);
        string directory = Path.Combine(RecordingsRoot, "timing-errors");
        WriteCloseFixture(directory);
        await Task.Run(() => M2ExportService.RenderMeeting(directory, encoder: null), token);
        var segments = new[] {
            new TranscriptSegment("good", 123, 300, null, "unknown", "正常字幕 / valid cue", "approximate"),
            new TranscriptSegment("missing-one", null, null, null, "unknown", "第一条待定位字幕 / first timing error", "untimed"),
            new TranscriptSegment("search-hit", 1000, 1400, null, "unknown", "search-visible 正常字幕", "approximate"),
            new TranscriptSegment("missing-two", null, null, null, "unknown", "第二条待定位字幕 / second timing error", "untimed")
        };
        TranscriptRevision Import(string meeting, string name, long duration, TranscriptSegment[] cues)
        {
            string file = Path.Combine(evidence, name + ".json");
            File.WriteAllText(file, JsonSerializer.Serialize(new { schemaVersion = 1, isSynthetic = true,
                meetingId = Path.GetFileName(meeting), revisionId = "fixture", parentRevisionId = (string?)null,
                provider = "Synthetic M3P", inputExportId = (string?)null, segments = cues },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return TranscriptService.Import(new(meeting, Path.GetFileName(meeting), duration, file, "Synthetic M3P", IsSynthetic: true), token);
        }
        var parent = Import(directory, "mixed-cues", 2000, segments);
        var hashes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(p => p, CloseFixtureHash);
        var images = new List<string>(); var prompts = new List<string>(); var view = transcriptionView!;
        reviewErrorForSmoke = prompts.Add;
        try
        {
            SelectMeeting(directory); await savedMixWork.WaitAsync(token); await replayLoadWork.WaitAsync(token);
            NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 1; await transcriptWork.WaitAsync(token);
            await LoadTranscriptVersionsAsync(parent.RevisionId);
            Need(view.ExportSubtitlesButton.IsEnabled && view.FindTimingErrorButton.IsEnabled &&
                view.SegmentsBox.Items.Cast<TranscriptRow>().Count(r => r.HasTimingIssue) == 2, "Untimed export disabled or highlight count wrong.");
            view.SegmentsBox.SelectedIndex = 3;
            view.ExportSubtitlesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(visibleSegment?.Segment.SegmentId == "missing-one" && prompts.Count == 1 && prompts[0].Contains("第一条"), "Export did not prompt and select first error.");
            foreach (int expected in new[] { 3, 1 })
            {
                view.FindTimingErrorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
                Need(view.SegmentsBox.SelectedIndex == expected, "Next error did not advance/wrap.");
            }
            foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
            {
                ChangeLanguage(language, persist: false);
                foreach (var size in new[] { (1280d, 930d), (MinWidth, MinHeight) })
                {
                    Width = size.Item1; Height = size.Item2;
                    await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
                    Need(view.FindTimingErrorButton.Content?.ToString() == (language == "en" ? "Find error" : language == "zh-Hant" ? "尋找錯誤" : "寻找错误"), "Find button localization failed.");
                    var item = (ListBoxItem)view.SegmentsBox.ItemContainerGenerator.ContainerFromItem(view.SegmentsBox.SelectedItem);
                    var border = Descendants(item).OfType<Border>().First(b => b.Name == "TimingHighlight");
                    Need((border.Background as SolidColorBrush)?.Color == Color.FromRgb(255, 225, 225), "Selected error lost its pale red highlight.");
                    Need(view.FindTimingErrorButton.IsVisible && view.ExportSubtitlesButton.IsVisible &&
                        ((Panel)view.FindTimingErrorButton.Parent).Children.IndexOf(view.FindTimingErrorButton) + 1 ==
                        ((Panel)view.ExportSubtitlesButton.Parent).Children.IndexOf(view.ExportSubtitlesButton), "Find button is not immediately before export.");
                    SaveLayoutImage(evidence, $"timing-{language}-{size.Item1}.png", 1, images);
                    if (size.Item1 == MinWidth)
                    {
                        view.ReviewEditorPanel.BringIntoView();
                        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
                        Point find = view.FindTimingErrorButton.TranslatePoint(new Point(), view.ReviewBodyScroller);
                        Point export = view.ExportSubtitlesButton.TranslatePoint(new Point(), view.ReviewBodyScroller);
                        Need(Math.Abs(find.Y - export.Y) < 1 && find.X + view.FindTimingErrorButton.ActualWidth <= export.X &&
                            export.Y + view.ExportSubtitlesButton.ActualHeight <= view.ReviewBodyScroller.ActualHeight + 1,
                            "Small-window error/export buttons clipped or not adjacent.");
                        SaveLayoutImage(evidence, $"timing-actions-{language}-{size.Item1}.png", 1, images);
                        view.ReviewBodyScroller.ScrollToTop();
                    }
                }
            }
            ChangeLanguage("zh-CN", persist: false); Width = 1280; Height = 930;
            view.StartTimestampBox.Text = "00:00:00.400"; view.EndTimestampBox.Text = "00:00:00.800";
            Need(!visibleSegment!.HasTimingIssue, "Corrected row remained red.");
            view.SegmentsBox.SelectedIndex = 0; view.EditBox.Text = "保留这条文字草稿";
            view.EndTimestampBox.Text = "00:00:00.123";
            Need(visibleSegment!.HasTimingIssue, "Zero-duration draft not highlighted.");
            view.ExportSubtitlesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(view.SegmentsBox.SelectedIndex == 0 && view.EditBox.Text == "保留这条文字草稿", "Export skipped malformed draft or lost text.");
            view.EndTimestampBox.Text = "00:00:00.300";
            view.FindTimingErrorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(view.SegmentsBox.SelectedIndex == 3, "Find did not skip corrected drafts.");
            view.StartTimestampBox.Text = "00:00:01.500"; view.EndTimestampBox.Text = "00:00:01.900";
            int promptCount = prompts.Count;
            view.ExportSubtitlesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(prompts.Count == promptCount + 1 && prompts[^1].Contains("尚未保存") && CurrentTranscriptDraft!.ChangedSegmentCount == 3,
                "Dirty valid export did not require saving or lost edits.");
            view.SaveRevisionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            var saved = visibleRevision!;
            Need(saved.ParentRevisionId == parent.RevisionId && saved.Segments[0].StartMs == 123 &&
                !view.SegmentsBox.Items.Cast<TranscriptRow>().Any(r => r.HasTimingIssue), "Saved correction changed untouched precision or retained errors.");
            foreach (string extension in new[] { "srt", "vtt" })
            {
                string export = Path.Combine(evidence, "corrected." + extension);
                StartTranscriptWork(() => ExportTranscriptSubtitlesAsync(export)); await transcriptWork.WaitAsync(token);
                Need(File.Exists(export) && File.ReadAllText(export).Contains("保留这条文字草稿"), "Corrected UI export lost text.");
            }
            promptCount = prompts.Count;
            view.FindTimingErrorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(prompts.Count == promptCount + 1 && prompts[^1].Contains("没有发现"), "No-error response missing.");

            // Search must not conceal an error from navigation or export validation.
            await LoadTranscriptVersionsAsync(parent.RevisionId);
            view.SearchBox.Text = "search-visible";
            StartTranscriptWork(SearchTranscriptsAsync); await transcriptWork.WaitAsync(token);
            view.SegmentsBox.SelectedItem = view.SegmentsBox.Items.Cast<TranscriptRow>().First(r => r.Revision.RevisionId == parent.RevisionId);
            view.FindTimingErrorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(view.SegmentsBox.Items.Count == 4 && visibleSegment?.Segment.SegmentId == "missing-two", "Search hid the next error.");

            // A separate synthetic three-hour transcript exercises virtualization and cross-meeting search.
            string longDirectory = Path.Combine(RecordingsRoot, "long-timing-errors"); WriteCloseFixture(longDirectory);
            var many = Enumerable.Range(0, 5000).Select(i => new TranscriptSegment("long-" + i, i * 2000L, i * 2000L + 1000,
                null, "unknown", "synthetic-long-cue-" + i, "approximate")).ToArray();
            many[4800] = many[4800] with { StartMs = null, EndMs = null, TimestampQuality = "untimed" };
            var longRevision = Import(longDirectory, "long-cues", 10800000, many);
            view.AllMeetingsBox.IsChecked = true; view.SearchBox.Text = "synthetic-long-cue-4999";
            StartTranscriptWork(SearchTranscriptsAsync); await transcriptWork.WaitAsync(token); view.SegmentsBox.SelectedIndex = 0;
            view.ExportSubtitlesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(selectedMeetingDirectory == longDirectory && visibleRevision?.RevisionId == longRevision.RevisionId &&
                view.SegmentsBox.SelectedIndex == 4800 && view.SegmentsBox.Items.Count == 5000, "Cross-meeting export did not reveal the late error.");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            var lateItem = view.SegmentsBox.ItemContainerGenerator.ContainerFromIndex(4800) as ListBoxItem;
            Need(lateItem is { IsVisible: true } && Descendants(view.SegmentsBox).OfType<ListBoxItem>().Count() < 100,
                "Late error not visible or list lost virtualization.");
            SaveLayoutImage(evidence, "long-error-selected.png", 1, images);
            Need(hashes.All(p => CloseFixtureHash(p.Key) == p.Value) && host is null && !microphoneEnabled &&
                player.Source is null && pendingTranscriptSeek is null, "Navigation changed originals or started audio.");
            return new { Status = "PASS", Version = "M5F Pro", SourceMode = "SYNTHETIC FILES / NO CAPTURE",
                Highlight = "#FFFFE1E1", ErrorNavigationWraps = true, ExportPromptSelectsFirst = true, DraftsPreserved = true,
                CorrectedSrtAndVtt = true, SearchRevealsHiddenErrors = true, CrossMeetingSearch = true, LongTranscriptCues = 5000,
                LateErrorIndex = 4800, Virtualized = true, OriginalHashesUnchanged = true, DialogMessagesIntercepted = prompts,
                Images = images, CaptureStarted = false, MicrophoneCollected = false, PhysicalInputAndNativeDialog = "NOT_RUN" };
        }
        finally { reviewErrorForSmoke = null; PrepareTranscriptDraftSmokeShutdown(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
