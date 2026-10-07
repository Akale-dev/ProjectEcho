using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProjectEcho.Core;
using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Actual WPF corrections/export/reopen with synthetic acoustic assignments, never native capture.</summary>
    internal async Task<object> M5SpeakerSmokeAsync(string evidence, CancellationToken token)
    {
        Need(host is null && !microphoneEnabled && !levelPreviewNativeEnabled, "M5 smoke requires capture-disabled construction.");
        string portableA = Path.Combine(evidence, "portable-a"), portableB = Path.Combine(evidence, "便携-移动后"), external = Path.Combine(evidence, "external-model.onnx");
        var portableConfig = new SherpaDiarizationConfiguration(Path.Combine(portableA, "Tools", "Sherpa", "worker.exe"),
            Path.Combine(portableA, "Models", "segment.onnx"), external);
        var savedPaths = SpeakerUiSettingsPaths.ForStorage(portableConfig, portableA);
        var movedPaths = SpeakerUiSettingsPaths.Resolve(savedPaths, portableB);
        Need(!Path.IsPathRooted(savedPaths.ExecutablePath) && movedPaths.ExecutablePath == Path.Combine(portableB, "Tools", "Sherpa", "worker.exe") &&
            movedPaths.SegmentationModelPath == Path.Combine(portableB, "Models", "segment.onnx") && movedPaths.EmbeddingModelPath == external, "Portable speaker settings failed to relocate bundled files or changed an external choice.");
        bool rejectedEscape = false;
        try { SpeakerUiSettingsPaths.Resolve(savedPaths with { ExecutablePath = "../worker.exe" }, portableB); } catch (InvalidDataException) { rejectedEscape = true; }
        Need(rejectedEscape, "Speaker settings accepted a relative directory escape.");
        dataRoot = Path.Combine(evidence, "synthetic-speaker-data"); Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        string meeting = Path.Combine(RecordingsRoot, "synthetic-speakers"), other = Path.Combine(RecordingsRoot, "synthetic-other");
        WriteCloseFixture(meeting); WriteCloseFixture(other);
        var originals = Directory.EnumerateFiles(meeting, "*", SearchOption.AllDirectories).ToDictionary(path => path, CloseFixtureHash);
        currentMix = await Task.Run(() => M2ExportService.RenderMeeting(meeting, encoder: null), token); renderedMeetings[meeting] = currentMix;
        SelectMeeting(meeting); await savedMixWork.WaitAsync(token); await replayLoadWork.WaitAsync(token);
        string input = Path.Combine(evidence, "synthetic-m5.srt");
        File.WriteAllText(input, "1\n00:00:00,000 --> 00:00:00,600\n第一人先发言。\n\n2\n00:00:00,600 --> 00:00:01,200\n我们保留全部文字，不捏造发言。\n\n3\n00:00:01,200 --> 00:00:01,900\n第一人再次返回。\n");
        var source = TranscriptService.Import(new(meeting, Path.GetFileName(meeting), 2000, input, "synthetic UI fixture", IsSynthetic: true), token);
        var audio = AsrAudioPreparation.Prepare(currentMix!.WavePath, currentMix.WaveSha256, Path.Combine(meeting, "transcripts", "asr-inputs"), token);
        string a = Guid.NewGuid().ToString("N"), b = Guid.NewGuid().ToString("N"), hash = new('a', 64);
        var analysis = SpeakerService.SaveAnalysis(meeting, new(1, Path.GetFileName(meeting), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            new(audio.Sha256, currentMix.WaveSha256, 16000, audio.Frames, "elapsed-v1"),
            new("synthetic-ui-only", "1", hash, hash, hash, null, .9, "none", "synthetic-assignments-not-model-inference"),
            [new(a, "A"), new(b, "B")], [new(0, 800, a), new(800, 1200, b), new(1200, 2000, a)], [], true), token);
        var initial = SpeakerService.CreateRevision(meeting, source.RevisionId, analysis.AnalysisId, token);
        await LoadTranscriptVersionsAsync(initial.RevisionId); NavigateTo(3); var view = transcriptionView!; view.WorkspaceTabs.SelectedIndex = 3;
        var images = new List<string>(); string fullText = string.Concat(source.Segments.Select(segment => segment.Text));
        Need(currentSpeakerRevision?.RevisionId == initial.RevisionId && view.SpeakerRevisionBox.Items.Count == 2 &&
            view.RevisionBox.Items.Count == ReplayRevisionBox.Items.Count, "Speaker version was not integrated with review/replay selection.");
        Select(0);
        Need(view.SaveRevisionButton.IsEnabled && view.EditBox.IsEnabled, "Speaker wording cannot be edited through the version-aware review writer.");
        Select(1);
        Need(view.SpeakerCutTimeBox.Items.Count == 1 && view.SpeakerCutTextBox.Text == source.Segments[1].Text, "Missing unaltered text and candidate cut.");
        view.SpeakerCutTextBox.CaretIndex = 6;
        view.SplitSpeakerTextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        var cutRevision = RequiredRevision();
        Need(cutRevision.ParentRevisionId == initial.RevisionId && cutRevision.Segments.Count == 4 &&
            string.Concat(cutRevision.Segments.Select(segment => segment.Text)) == fullText, "Manual cut lost or duplicated source text.");
        Select(0); SelectSpeakerChoice(view.SpeakerIdentityBox, a); view.SpeakerDisplayNameBox.Text = "测试主持人";
        view.RenameSpeakerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        var renamed = RequiredRevision();
        Need(renamed.Speakers.Single(item => item.SpeakerId == a).DisplayName == "测试主持人" &&
            renamed.Segments[0].SpeakerId == a && ((TranscriptRow)view.SpeakerSegmentsBox.Items[0]).Label.Contains("测试主持人"), "Display rename changed stable identity or was not shown.");
        Select(0); SelectSpeakerChoice(view.SpeakerAssigneeBox, b);
        view.AssignSpeakerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        Need(RequiredRevision().Segments[0].SpeakerId == b, "Single-segment reassignment failed.");
        Select(0); view.SplitOutSpeakerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        string splitId = RequiredRevision().Segments[0].SpeakerId!;
        Need(splitId != a && splitId != b && RequiredRevision().Speakers.Count == 3, "Split-out failed to create a new meeting-local identity.");
        Select(0); SelectSpeakerChoice(view.SpeakerIdentityBox, splitId); SelectSpeakerChoice(view.SpeakerAssigneeBox, a);
        view.MergeSpeakerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        var final = RequiredRevision();
        Need(final.Segments[0].SpeakerId == a && string.Concat(final.Segments.Select(segment => segment.Text)) == fullText, "Merge changed text or failed to remap identity.");
        Need(SpeakerService.Load(meeting, initial.RevisionId).Speakers.Single(item => item.SpeakerId == a).DisplayName is null &&
            TranscriptService.Load(meeting, source.RevisionId).Segments.SequenceEqual(source.Segments), "Correction overwrote the parent or source transcript.");
        foreach (string extension in new[] { "txt", "srt", "vtt" })
        {
            string labeled = Path.Combine(evidence, "with-speakers." + extension), plain = Path.Combine(evidence, "without-speakers." + extension);
            view.IncludeSpeakerLabelsBox.IsChecked = true;
            StartTranscriptWork(() => extension == "txt" ? ExportTranscriptTextAsync(labeled) : ExportTranscriptSubtitlesAsync(labeled)); await transcriptWork.WaitAsync(token);
            view.IncludeSpeakerLabelsBox.IsChecked = false;
            StartTranscriptWork(() => extension == "txt" ? ExportTranscriptTextAsync(plain) : ExportTranscriptSubtitlesAsync(plain)); await transcriptWork.WaitAsync(token);
            Need(File.ReadAllText(labeled).Contains("测试主持人") && !File.ReadAllText(plain).Contains("测试主持人"), "Speaker export label toggle failed for " + extension);
        }
        view.IncludeSpeakerLabelsBox.IsChecked = true;
        foreach (string language in new[] { "zh-CN", "en", "zh-Hant" })
        {
            ChangeLanguage(language, persist: false); view.WorkspaceTabs.SelectedIndex = 3;
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            Need(view.SpeakersTab.Header?.ToString() == UiText.T("同场说话人"), "Speaker tab localization missing.");
            SaveLayoutImage(evidence, "m5-speakers-" + language + ".png", 1, images);
        }
        ChangeLanguage("zh-CN", persist: false);
        view.WorkspaceTabs.SelectedIndex = 1; view.SearchBox.Text = "测试主持人";
        view.SearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        Need(view.SegmentsBox.Items.Count > 0 && view.SegmentsBox.Items.Cast<TranscriptRow>().All(row => row.Revision.Provider.EndsWith(" · 同场说话人")), "Name search failed or produced non-speaker hits.");
        view.SegmentsBox.SelectedIndex = 0;
        Need(currentSpeakerRevision is not null && view.RenameSpeakerButton.IsEnabled, "Searched speaker version could not be corrected.");
        speakerPresentations.Clear(); SelectMeeting(other, loadSavedMix: false); SelectMeeting(meeting); await savedMixWork.WaitAsync(token); await replayLoadWork.WaitAsync(token);
        await LoadTranscriptVersionsAsync(final.RevisionId);
        Need(RequiredRevision().Speakers.Single(item => item.SpeakerId == a).DisplayName == "测试主持人" &&
            view.RevisionBox.Items.Count == 7, "Disk reopen lost names or immutable versions.");
        view.RevisionBox.SelectedItem = view.RevisionBox.Items.Cast<RevisionRow>().Single(item => item.Revision.RevisionId == source.RevisionId);
        view.SegmentsBox.SelectedIndex = 1; view.EditBox.Text = "人工校订后的完整文字，不捏造发言。";
        view.SaveRevisionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        var edited = visibleRevision ?? throw new InvalidDataException(view.StatusText.Text);
        Need(edited.ParentRevisionId == source.RevisionId && currentSpeakerRevision is null, "Editing the source did not retain a normal transcript version.");
        // An invalid native path proves this action only reuses the saved analysis; it cannot successfully launch inference.
        speakerWorker = Path.Combine(evidence, "this-native-program-does-not-exist.exe");
        int analysesBefore = SpeakerService.ListAnalyses(meeting).Count;
        view.WorkspaceTabs.SelectedIndex = 3;
        view.ApplySpeakerAnalysisButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        var reapplied = RequiredRevision();
        Need(reapplied.SourceTranscriptRevisionId == edited.RevisionId && reapplied.AnalysisId == analysis.AnalysisId &&
            SpeakerService.ListAnalyses(meeting).Count == analysesBefore &&
            string.Concat(reapplied.Segments.Select(item => item.Text)) == string.Concat(edited.Segments.Select(item => item.Text)) &&
            reapplied.Segments.Any(item => item.Text == edited.Segments[1].Text && item.NeedsConfirmation) &&
            SpeakerService.Load(meeting, final.RevisionId).Speakers.Any(item => item.DisplayName == "测试主持人"),
            "Reusing analysis lost edited text, invented its internal boundary, ran a model, or overwrote a manual version.");
        Select(0); SelectSpeakerChoice(view.SpeakerIdentityBox, a); view.SpeakerDisplayNameBox.Text = "主持甲";
        view.RenameSpeakerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        var nameA = RequiredRevision();
        Select(0); SelectSpeakerChoice(view.SpeakerIdentityBox, a); view.SpeakerDisplayNameBox.Text = "主持乙";
        view.RenameSpeakerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        var nameB = RequiredRevision();
        view.SearchBox.Text = "第一人先发言"; view.SearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        foreach (var named in new[] { (Revision: nameA, Name: "主持甲"), (Revision: nameB, Name: "主持乙"), (Revision: nameA, Name: "主持甲") })
        {
            view.SegmentsBox.SelectedItem = view.SegmentsBox.Items.Cast<TranscriptRow>().Single(item => item.Revision.RevisionId == named.Revision.RevisionId);
            Need(currentSpeakerRevision?.RevisionId == named.Revision.RevisionId &&
                (view.SpeakerIdentityBox.SelectedItem as SpeakerChoice)?.Id == a &&
                (view.SpeakerIdentityBox.SelectedItem as SpeakerChoice)?.Label == "A · " + named.Name && view.SpeakerDisplayNameBox.Text == named.Name,
                "Search result retained a different version's display name for the same stable identity.");
        }
        view.SpeakerDisplayNameBox.Text = "主持甲校正";
        view.RenameSpeakerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        Need(RequiredRevision().ParentRevisionId == nameA.RevisionId && RequiredRevision().Speakers.Single(item => item.SpeakerId == a).DisplayName == "主持甲校正" &&
            SpeakerService.Load(meeting, nameB.RevisionId).Speakers.Single(item => item.SpeakerId == a).DisplayName == "主持乙",
            "Search-result rename targeted the wrong immutable version.");
        // Model-missed turn: no candidate is present. Only explicit manual mode may supply a time.
        view.SearchBox.Text = "";
        await LoadTranscriptVersionsAsync(initial.RevisionId); Select(0); UpdateControls();
        Need(view.SpeakerCutTimeBox.Items.Count == 0 && view.SpeakerCutExpander.IsEnabled &&
            view.SpeakerManualCutBox.IsChecked == false && !view.SplitSpeakerTextButton.IsEnabled,
            "A missing model candidate still blocks manual correction or implicitly supplies a cut.");
        foreach (string invalid in new[] { "", "27.5", "-00:00:01.000", "00:60:00.000", "00:00:60.000", "00:00:01.0000" })
            Need(!TryParseSpeakerManualCutTime(invalid, out _), "Invalid or ambiguous manual time accepted: " + invalid);
        Need(TryParseSpeakerManualCutTime("00:00:00.321", out long preciseCut) && preciseCut == 321 &&
            TryParseSpeakerManualCutTime("02:03:04.5", out long longCut) && longCut == 7384500,
            "Manual time parser rounded or changed the recording timeline.");
        view.SpeakerCutExpander.IsExpanded = true; view.SpeakerManualCutBox.IsChecked = true;
        view.SpeakerManualCutTimeBox.Text = "00:00:00.321"; view.SpeakerCutTextBox.CaretIndex = 3;
        SelectSpeakerChoice(view.SpeakerCutLeftBox, a); SelectSpeakerChoice(view.SpeakerCutRightBox, b);
        Need(view.SpeakerManualCutTimeBox.IsEnabled && !view.SpeakerCutTimeBox.IsEnabled && view.SplitSpeakerTextButton.IsEnabled,
            "Manual input and model candidate modes were not separated.");
        foreach (string language in new[] { "zh-CN", "en", "zh-Hant" })
        {
            ChangeLanguage(language, persist: false); view.WorkspaceTabs.SelectedIndex = 3;
            Need(view.SpeakerManualCutBox.Content?.ToString() == UiText.T("手动输入换人时刻（本录音时间）") &&
                (language == "zh-CN" || view.SpeakerManualCutBox.Content?.ToString() != "手动输入换人时刻（本录音时间）"), "Manual turn input localization missing.");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            view.SpeakerCutExpander.BringIntoView();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            SaveLayoutImage(evidence, "m5c-manual-time-" + language + ".png", 1, images);
        }
        ChangeLanguage("zh-CN", persist: false);
        view.SplitSpeakerTextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        var manualTimeRevision = RequiredRevision();
        Need(manualTimeRevision.ParentRevisionId == initial.RevisionId && manualTimeRevision.SchemaVersion == 2 &&
            manualTimeRevision.Segments.Count == initial.Segments.Count + 1 && manualTimeRevision.Segments[0].EndMs == 321 &&
            manualTimeRevision.Segments[1].StartMs == 321 && manualTimeRevision.Segments[1].SpeakerId == b &&
            manualTimeRevision.Corrections[^1].Operation == "split-segment-manual-time" &&
            string.Concat(manualTimeRevision.Segments.Select(segment => segment.Text)) == fullText &&
            SpeakerService.Load(meeting, manualTimeRevision.RevisionId).Segments[0].TimingSource == "human-entered-speaker-boundary" &&
            SpeakerService.Load(meeting, initial.RevisionId).Segments.Count == initial.Segments.Count,
            "Explicit manual turn did not save a separate verifiable complete-text version.");
        Select(0);
        Need(view.SpeakerManualCutBox.IsChecked == false && view.SpeakerManualCutTimeBox.Text.Length == 0,
            "Manual time input leaked into a different segment/version.");
        var cancellationSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        speakerRunning = true;
        StartTranscriptWork(async () => { try { await Task.Delay(Timeout.Infinite, transcriptCancellation!.Token); } catch (OperationCanceledException) { cancellationSeen.TrySetResult(); throw; } });
        YieldQwenToRecording(); await cancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(3), token); await transcriptWork.WaitAsync(token); speakerRunning = false;
        foreach (var active in new[] { SessionState.Recording, SessionState.Paused, SessionState.Stopping })
        {
            state = active; UpdateControls();
            Need(!view.RunSpeakersButton.IsEnabled && !view.ApplySpeakerAnalysisButton.IsEnabled && !view.PlayListeningButton.IsEnabled && !PlaybackButton.IsEnabled, "Active recording did not block speaker start/apply/playback.");
        }
        state = SessionState.Idle; UpdateControls();
        Need(originals.All(pair => CloseFixtureHash(pair.Key) == pair.Value) && host is null && !microphoneEnabled && !levelPreviewNativeEnabled,
            "Smoke changed an original or enabled native capture.");
        return new { Status = "PASS", Kind = "actual WPF; synthetic acoustic assignments, no model inference or capture", Versions = view.RevisionBox.Items.Count,
            StableSpeakerId = a, FullTextPreserved = true, DisplayRename = true, Assign = true, Merge = true, SplitOut = true, ManualCut = true,
            ExplicitManualTimeWithoutModelCandidate = true, ManualTimeMillisecondsPreserved = true, ManualTimeThreeLanguages = true,
            ExportFormats = new[] { "txt", "srt", "vtt" }, SearchByDisplayName = true, SearchVersionSpecificNames = true, Reopen = true, ReuseSavedAnalysisForEditedText = true, PortablePaths = true, RecordingPriorityCancellation = true, Images = images };

        SpeakerRevision RequiredRevision() => currentSpeakerRevision ?? throw new InvalidDataException(view.StatusText.Text);
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        void Select(int index) { view.SpeakerSegmentsBox.SelectedIndex = index; Need(visibleSegment is not null, "Speaker segment selection did not synchronize."); }
    }
}
