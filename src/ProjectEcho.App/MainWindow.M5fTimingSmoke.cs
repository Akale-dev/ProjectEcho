using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    internal async Task<object> M5fTimingSmokeAsync(string evidence, CancellationToken token)
    {
        static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
        Need(host is null && !microphoneEnabled && !levelPreviewNativeEnabled, "M5F timing smoke must not capture audio.");
        dataRoot = Path.Combine(evidence, "synthetic-data"); Directory.CreateDirectory(dataRoot);
        string meeting = Path.Combine(RecordingsRoot, "speaker-timing"), other = Path.Combine(RecordingsRoot, "other-timing");
        WriteCloseFixture(meeting); WriteCloseFixture(other);
        currentMix = await Task.Run(() => M2ExportService.RenderMeeting(meeting, encoder: null), token);
        renderedMeetings[meeting] = currentMix;
        string input = Path.Combine(evidence, "speaker-source.srt");
        File.WriteAllText(input, "1\n00:00:00,123 --> 00:00:00,300\nspeaker-search-hit 第一人。\n\n2\n00:00:00,400 --> 00:00:00,800\n第一人待校订。\n\n3\n00:00:01,000 --> 00:00:01,200\n第二人。\n\n4\n00:00:01,300 --> 00:00:01,900\n第二人待校订。\n");
        var source = TranscriptService.Import(new(meeting, Path.GetFileName(meeting), 2000, input, "Synthetic M5F", IsSynthetic: true), token);
        var otherRevision = TranscriptService.Import(new(other, Path.GetFileName(other), 2000, input, "Synthetic other", IsSynthetic: true), token);
        var audio = AsrAudioPreparation.Prepare(currentMix.WavePath, currentMix.WaveSha256, Path.Combine(meeting, "transcripts", "asr-inputs"), token);
        string a = Guid.NewGuid().ToString("N"), b = Guid.NewGuid().ToString("N"), hash = new('a', 64);
        var analysis = SpeakerService.SaveAnalysis(meeting, new(1, Path.GetFileName(meeting), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            new(audio.Sha256, currentMix.WaveSha256, 16000, audio.Frames, "elapsed-v1"),
            new("synthetic-timing-only", "1", hash, hash, hash, 2, .9, "none", "synthetic-no-inference"),
            [new(a, "A"), new(b, "B")], [new(0, 1000, a), new(1000, 2000, b)], [], true), token);
        var initial = SpeakerService.CreateRevision(meeting, source.RevisionId, analysis.AnalysisId, token);
        initial = SpeakerService.Rename(meeting, initial.RevisionId, a, "本场甲", token);
        initial = SpeakerService.Rename(meeting, initial.RevisionId, b, "本场乙", token);
        var untimed = SpeakerDisplay.AsTranscript(initial, source).Segments.Select((s, i) => i is 1 or 3
            ? s with { StartMs = null, EndMs = null, TimestampQuality = "untimed" } : s).ToArray();
        var parent = SpeakerService.EditSubtitles(meeting, initial.RevisionId, untimed, token);
        var hashes = Directory.EnumerateFiles(meeting, "*", SearchOption.AllDirectories).ToDictionary(p => p, CloseFixtureHash);
        var prompts = new List<string>(); var images = new List<string>(); var view = transcriptionView!;
        reviewErrorForSmoke = prompts.Add;
        try
        {
            SelectMeeting(meeting); await savedMixWork.WaitAsync(token); await replayLoadWork.WaitAsync(token);
            NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 1; await transcriptWork.WaitAsync(token);
            await LoadTranscriptVersionsAsync(parent.RevisionId);
            Need(view.WorkspaceTabs.Items.Count == 4 && IsSpeakerPresentation(meeting, parent.RevisionId) &&
                view.IncludeSpeakerLabelsBox.IsChecked == true, "Pro speaker workspace/export option missing.");
            Need(view.SegmentsBox.Items.Cast<TranscriptRow>().Count(row => row.HasTimingIssue) == 2, "Speaker untimed rows not highlighted.");
            view.SegmentsBox.SelectedIndex = 3;
            view.ExportSubtitlesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(view.SegmentsBox.SelectedIndex == 1 && prompts.Count == 1 && visibleSegment!.DisplayText.StartsWith("[本场甲]"),
                "Speaker export did not select first error or lost display label.");
            foreach (int index in new[] { 3, 1 })
            {
                view.FindTimingErrorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
                Need(view.SegmentsBox.SelectedIndex == index, "Speaker timing navigation did not wrap.");
            }
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            var item = (ListBoxItem)view.SegmentsBox.ItemContainerGenerator.ContainerFromIndex(1);
            Need((Descendants(item).OfType<Border>().First(border => border.Name == "TimingHighlight").Background as SolidColorBrush)?.Color == Color.FromRgb(255, 225, 225),
                "Speaker selected error lacks pale red background.");
            SaveLayoutImage(evidence, "speaker-timing-error.png", 1, images);
            view.StartTimestampBox.Text = "00:00:00.400"; view.EndTimestampBox.Text = "00:00:00.800";
            Need(!visibleSegment!.HasTimingIssue, "Corrected speaker row remains highlighted.");
            view.FindTimingErrorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(view.SegmentsBox.SelectedIndex == 3, "Corrected speaker draft not skipped.");
            view.StartTimestampBox.Text = "00:00:01.300"; view.EndTimestampBox.Text = "00:00:01.900";
            view.ExportSubtitlesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(prompts[^1].Contains("尚未保存"), "Speaker export ignored unsaved correction.");
            view.SaveRevisionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            var saved = SpeakerService.Load(meeting, visibleRevision!.RevisionId, token);
            Need(saved.ParentRevisionId == parent.RevisionId && saved.SourceTranscriptRevisionId == source.RevisionId &&
                saved.Speakers.SequenceEqual(parent.Speakers) && saved.Segments.Select(s => s.SpeakerId).SequenceEqual(parent.Segments.Select(s => s.SpeakerId)) &&
                saved.Segments[0].StartMs == 123 && !view.SegmentsBox.Items.Cast<TranscriptRow>().Any(row => row.HasTimingIssue),
                "Correction lost speaker provenance/untouched precision or retained timing errors.");
            foreach (string extension in new[] { "srt", "vtt" })
            {
                string destination = Path.Combine(evidence, "speaker-corrected." + extension);
                StartTranscriptWork(() => ExportTranscriptSubtitlesAsync(destination)); await transcriptWork.WaitAsync(token);
                Need(File.ReadAllText(destination).Contains("本场甲") && File.ReadAllText(destination).Contains("本场乙"), "Corrected speaker export lost labels.");
            }
            view.IncludeSpeakerLabelsBox.IsChecked = false;
            string plain = Path.Combine(evidence, "speaker-corrected-no-label.srt");
            StartTranscriptWork(() => ExportTranscriptSubtitlesAsync(plain)); await transcriptWork.WaitAsync(token);
            Need(!File.ReadAllText(plain).Contains("本场甲") && File.ReadAllText(plain).Contains("第一人待校订。"), "Optional label export changed text or retained labels.");

            SelectMeeting(other, loadSavedMix: false); await transcriptWork.WaitAsync(token); await LoadTranscriptVersionsAsync(otherRevision.RevisionId);
            view.SegmentsBox.SelectedIndex = 0; view.EditBox.Text = "另一场未保存草稿";
            view.AllMeetingsBox.IsChecked = true; view.SearchBox.Text = "speaker-search-hit";
            StartTranscriptWork(SearchTranscriptsAsync); await transcriptWork.WaitAsync(token);
            view.SegmentsBox.SelectedItem = view.SegmentsBox.Items.Cast<TranscriptRow>().First(row => row.Revision.RevisionId == parent.RevisionId);
            view.FindTimingErrorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(selectedMeetingDirectory == meeting && visibleRevision?.RevisionId == parent.RevisionId && view.SegmentsBox.Items.Count == 4 &&
                view.SegmentsBox.SelectedIndex == 1 && visibleSegment!.DisplayText.StartsWith("[本场甲]") &&
                DraftFor(other, otherRevision).InputFor(otherRevision.Segments[0]).Text == "另一场未保存草稿", "Cross-meeting search lost speaker revision or another draft.");
            Need(hashes.All(pair => CloseFixtureHash(pair.Key) == pair.Value) && host is null && !microphoneEnabled && player.Source is null,
                "Timing navigation changed original files or started capture/playback.");
            return new { Status = "PASS", Version = "M5F Pro", SourceMode = "SYNTHETIC FILES / NO CAPTURE OR MODEL INFERENCE",
                SpeakerTimingHighlight = true, ExportPromptSelectsFirst = true, FindWraps = true, CorrectedSpeakerRevision = true,
                SpeakerIdentityAndProvenancePreserved = true, OptionalLabelsInSrtVtt = true, CrossMeetingSpeakerSearch = true,
                OtherDraftPreserved = true, OriginalHashesUnchanged = true, Images = images, DialogMessagesIntercepted = prompts,
                CaptureStarted = false, MicrophoneCollected = false };
        }
        finally { reviewErrorForSmoke = null; PrepareTranscriptDraftSmokeShutdown(); }
    }
}
