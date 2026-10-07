using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

public partial class MainWindow
{
    internal async Task<object> M3kTimestampInputSmokeAsync(string evidence, CancellationToken token)
    {
        Need(host is null && !microphoneEnabled && !levelPreviewNativeEnabled, "Synthetic timestamp test cannot collect audio.");
        dataRoot = Path.Combine(evidence, "synthetic-data"); Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        string wave = Path.Combine(evidence, "synthetic-41min-silence.wav");
        const int frames = 39_605_202;
        using (var stream = new FileStream(wave, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(frames * 2 + 36);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(frames * 2);
            stream.SetLength(44L + frames * 2L); // Defined zero-filled PCM, no live source.
        }
        var imported = await ImportAudioFileAsync(wave, token);
        await transcriptWork.WaitAsync(token); await savedMixWork.WaitAsync(token); await replayLoadWork.WaitAsync(token);
        string plain = Path.Combine(evidence, "untimed.txt"); File.WriteAllText(plain, "Synthetic untimed final cue.");
        var original = TranscriptService.Import(new(imported.MeetingDirectory, Path.GetFileName(imported.MeetingDirectory),
            2_475_326, plain, "Synthetic time input", IsSynthetic: true), token);
        var view = transcriptionView!; var images = new List<string>();
        player.Volume = 0; player.IsMuted = true;
        try
        {
            NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 1; await transcriptWork.WaitAsync(token);
            await LoadTranscriptVersionsAsync(original.RevisionId); view.SegmentsBox.SelectedIndex = 0;
            // The old ambiguous input remains hours if imported programmatically.
            view.StartTimestampBox.Text = "41:09:01"; view.EndTimestampBox.Text = "41:11:00";
            Need(!view.SeekButton.IsEnabled && HasValidationIssue(view.StartTimestampBox) &&
                view.StartTimestampBox.ToolTip.ToString()!.Contains("00:41:15.32"), "Hour interpretation or real duration is not visible.");
            view.StartTimestampBox.Clear(); view.EndTimestampBox.Clear();
            // Edit actual WPF number fields; punctuation is outside these TextBoxes.
            view.StartTimestampBox.MinutesBox.Text = "41";
            view.StartTimestampBox.SecondsBox.Text = "09";
            view.StartTimestampBox.CentisecondsBox.Text = "01";
            view.EndTimestampBox.MinutesBox.Text = "41"; view.EndTimestampBox.SecondsBox.Text = "11";
            Need(view.StartTimestampBox.Text == "00:41:09.01" && view.EndTimestampBox.Text == "00:41:11.00" &&
                view.SeekButton.IsEnabled && !HasValidationIssue(view.StartTimestampBox), "Fixed fields did not produce in-range minute times.");
            foreach (var editor in new[] { view.StartTimestampBox, view.EndTimestampBox })
            {
                Need(editor.HoursSeparator.Text == ":" && editor.MinutesSeparator.Text == ":" && editor.FractionSeparator.Text == ".",
                    "Separators are not fixed text elements.");
                var composition = new TextComposition(InputManager.Current, editor.MinutesBox, ":");
                var input = new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice, composition)
                    { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
                editor.MinutesBox.RaiseEvent(input);
                Need(input.Handled && editor.MinutesBox.Text == "41", "A separator can be inserted into a digit field.");
                var paste = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, "41:09:01"), false, DataFormats.UnicodeText)
                    { RoutedEvent = DataObject.PastingEvent };
                editor.MinutesBox.RaiseEvent(paste);
                Need(paste.CommandCancelled, "Pasting a whole ambiguous time into one number field was not rejected.");
            }
            view.StartTimestampBox.SecondsBox.Clear();
            Need(!view.SeekButton.IsEnabled && HasValidationIssue(view.StartTimestampBox), "Partial number fields were silently converted to zero.");
            string partial = view.StartTimestampBox.Text;
            await LoadTranscriptVersionsAsync(original.RevisionId);
            Need(view.StartTimestampBox.Text == partial, "Refreshing repaired/lost an incomplete time draft.");
            view.StartTimestampBox.SecondsBox.Text = "09";
            view.EndTimestampBox.SecondsBox.Text = "15"; view.EndTimestampBox.CentisecondsBox.Text = "33";
            Need(!view.SeekButton.IsEnabled, "One millisecond beyond the saved EOF was accepted.");
            view.EndTimestampBox.SecondsBox.Text = "11"; view.EndTimestampBox.CentisecondsBox.Text = "00";

            SeekTranscript();
            var deadline = Stopwatch.StartNew();
            while (!playbackOpened && deadline.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25, token);
            Need(playbackOpened && player.IsMuted, "Muted generated-audio replay did not open.");
            player.Pause(); playbackPaused = true; SeekPlaybackMilliseconds(2_469_010); ResetDisplayPlaybackClock();
            await Task.Delay(100, token);
            Need(Math.Abs(player.Position.TotalMilliseconds - 2_469_010) < 1, "Fixed fields did not reach the real minute-scale replay position.");
            ResetTranscriptPlayback();
            foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
            {
                ChangeLanguage(language, persist: false); Width = 1280; Height = 930;
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
                view.ReviewBodyScroller.ScrollToEnd(); await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
                Need(!view.TranscriptStatusPanel.IsVisible && view.StartTimestampBox.ActualWidth == 118 &&
                    view.SubtitleEditorGrid.ColumnDefinitions[0].ActualWidth == 160 &&
                    view.StartTimestampBox.CentisecondsBox.ActualWidth >= 15, "Fixed fields widened the original editor or were clipped.");
                SaveLayoutImage(evidence, "fixed-time-" + language + ".png", 1, images);
            }
            ChangeLanguage("zh-CN", persist: false);
            view.SaveRevisionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(visibleRevision is { } saved && saved.ParentRevisionId == original.RevisionId &&
                saved.Segments[0] is { StartMs: 2_469_010, EndMs: 2_471_000 } && TranscriptService.ListRevisions(imported.MeetingDirectory).Count == 2,
                "Fixed input did not save exactly one timed revision.");
            string exported = Path.Combine(evidence, "fixed-time.srt");
            StartTranscriptWork(() => ExportTranscriptSubtitlesAsync(exported)); await transcriptWork.WaitAsync(token);
            Need(File.ReadAllText(exported).Contains("00:41:09,010 --> 00:41:11,000"), "Saved export has a different time.");
            view.StartTimestampBox.ClearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.EndTimestampBox.ClearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Need(view.StartTimestampBox.Text == "" && view.EndTimestampBox.Text == "" && !view.SeekButton.IsEnabled,
                "Clear buttons did not preserve the untimed state.");
            return new { Status = "PASS", FixedColonsAndDecimal = true, DigitsAndPasteGuard = true,
                PartialDraftPreserved = true, ActualDurationMs = 2_475_326, NativeCorrectedSeekMs = 2_469_010,
                SavedEndMs = 2_471_000, OneRevisionSaved = true, OriginalUntimedRetained = true,
                SourceMode = "GENERATED SILENCE FILE / MUTED WPF / NO CAPTURE", Images = images,
                PhysicalKeyboard = "NOT_RUN; actual WPF fields and routed input events tested" };
        }
        finally { ResetTranscriptPlayback(); PrepareTranscriptDraftSmokeShutdown(); }
        static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    }
}
