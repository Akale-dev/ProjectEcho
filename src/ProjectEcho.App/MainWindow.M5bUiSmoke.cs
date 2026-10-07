using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Actual WPF labels, speaker wording, click playback and guards; generated silent PCM only.</summary>
    internal async Task<object> M5bUiSmokeAsync(string evidence, CancellationToken token)
    {
        Need(host is null && !microphoneEnabled && !levelPreviewNativeEnabled, "M5B smoke requires capture-disabled construction.");
        dataRoot = Path.Combine(evidence, "synthetic-data"); Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        string meeting = Path.Combine(RecordingsRoot, "synthetic-m5b"), other = Path.Combine(RecordingsRoot, "synthetic-other");
        WriteSilence(meeting); WriteSilence(other);
        var originals = Directory.EnumerateFiles(meeting, "*", SearchOption.AllDirectories).ToDictionary(path => path, CloseFixtureHash);
        currentMix = await Task.Run(() => M2ExportService.RenderMeeting(meeting, encoder: null), token); renderedMeetings[meeting] = currentMix;
        SelectMeeting(meeting); await savedMixWork.WaitAsync(token); await replayLoadWork.WaitAsync(token);
        string input = Path.Combine(evidence, "synthetic.srt");
        File.WriteAllText(input, "1\n00:00:00,100 --> 00:00:00,800\n第一人发言。\n\n2\n00:00:00,800 --> 00:00:01,600\n左边文字右边文字。\n\n3\n00:00:01,600 --> 00:00:02,400\n第二人回应。\n\n4\n00:00:02,400 --> 00:00:05,500\n第一人返回。\n");
        var source = TranscriptService.Import(new(meeting, Path.GetFileName(meeting), 6000, input, "Synthetic M5B UI", IsSynthetic: true), token);
        var audio = AsrAudioPreparation.Prepare(currentMix!.WavePath, currentMix.WaveSha256, Path.Combine(meeting, "transcripts", "asr-inputs"), token);
        string a = Guid.NewGuid().ToString("N"), b = Guid.NewGuid().ToString("N"), hash = new('a', 64);
        var analysis = SpeakerService.SaveAnalysis(meeting, new(1, Path.GetFileName(meeting), Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            new(audio.Sha256, currentMix.WaveSha256, 16000, audio.Frames, "elapsed-v1"),
            new("synthetic-ui-only", "1", hash, hash, hash, 2, .9, "none", "synthetic-assignments-not-model-inference"),
            [new(a, "A"), new(b, "B")], [new(0, 1200, a), new(1200, 2400, b), new(2400, 6000, a)], [], true), token);
        var initial = SpeakerService.CreateRevision(meeting, source.RevisionId, analysis.AnalysisId, token);
        var named = SpeakerService.Rename(meeting, initial.RevisionId, a, "本场主持人", token);
        var parent = SpeakerService.SplitSegment(meeting, named.RevisionId, named.Segments[1].SegmentId, 4, 1200, a, b, token);
        string parentHash = CloseFixtureHash(Path.Combine(meeting, "diarization", "revisions", parent.RevisionId, "revision.json"));
        await LoadTranscriptVersionsAsync(parent.RevisionId);
        var view = transcriptionView!; var images = new List<string>(); var seeks = new List<object>();
        string defaultFixture = Path.Combine(evidence, "bundled-default-fixture");
        string fixtureModels = Path.Combine(defaultFixture, "Models", "Sherpa"); Directory.CreateDirectory(fixtureModels);
        Need(SpeakerUiSettingsPaths.DefaultEmbedding(defaultFixture) is null, "Missing model was treated as available.");
        string fixtureEres = Path.Combine(fixtureModels, "3dspeaker-eres2net.onnx"), fixtureTitanet = Path.Combine(fixtureModels, "nemo_en_titanet_large.onnx");
        File.WriteAllText(fixtureEres, "synthetic-path-fixture-not-a-model");
        Need(SpeakerUiSettingsPaths.DefaultEmbedding(defaultFixture) == fixtureEres, "Existing ERes-only bundle lost its default.");
        File.WriteAllText(fixtureTitanet, "synthetic-path-fixture-not-a-model");
        Need(SpeakerUiSettingsPaths.DefaultEmbedding(defaultFixture) == fixtureTitanet, "New model default does not prefer the bundled TitaNet file.");
        string bundledEres = Path.Combine(AppContext.BaseDirectory, "Models", "Sherpa", "3dspeaker-eres2net.onnx");
        string bundledTitanet = Path.Combine(AppContext.BaseDirectory, "Models", "Sherpa", "nemo_en_titanet_large.onnx");
        var oldConfiguration = new SherpaDiarizationConfiguration(Path.Combine(evidence, "fake-worker.exe"), Path.Combine(evidence, "fake-segmentation.onnx"), bundledEres,
            KnownSpeakerCount: 2, ModelLicenseAcknowledged: true);
        SaveSpeakerSettings(meeting, oldConfiguration); speakerSettingsRoot = null; UpdateControls();
        Need(speakerEmbedding == bundledEres && (view.SpeakerEmbeddingPresetBox.SelectedItem as SpeakerEmbeddingChoice)?.Id == "eres",
            "Opening an existing ERes configuration silently selected TitaNet.");
        string configPath = Path.Combine(meeting, "speaker-settings.json"), settingsBefore = CloseFixtureHash(configPath);
        view.SpeakerEmbeddingPresetBox.SelectedItem = view.SpeakerEmbeddingPresetBox.Items.Cast<SpeakerEmbeddingChoice>().Single(choice => choice.Id == "titanet");
        Need(speakerEmbedding == bundledTitanet && CloseFixtureHash(configPath) == settingsBefore, "Preset selection did not use TitaNet or prematurely rewrote saved settings.");
        speakerSettingsRoot = null; UpdateControls();
        Need(speakerEmbedding == bundledEres, "Unsaved preset choice overwrote the saved meeting configuration.");
        string customModel = Path.Combine(evidence, "custom-model.onnx");
        SaveSpeakerSettings(meeting, oldConfiguration with { EmbeddingModelPath = customModel }); speakerSettingsRoot = null; UpdateControls();
        Need(speakerEmbedding == customModel && (view.SpeakerEmbeddingPresetBox.SelectedItem as SpeakerEmbeddingChoice)?.Id == "custom",
            "Custom embedding model selection was replaced.");
        player.Volume = 0; player.IsMuted = true;
        try
        {
            foreach (string language in new[] { "zh-CN", "en", "zh-Hant" })
            {
                ChangeLanguage(language, persist: false); NavigateTo(5);
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
                var replayRow = (TranscriptRow)ReplaySegmentsBox.Items[0];
                ReplaySegmentsBox.ScrollIntoView(replayRow); UpdateLayout();
                var item = ReplaySegmentsBox.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem ?? throw new InvalidDataException("Replay container missing.");
                Need(replayRow.DisplayText == "[本场主持人] 第一人发言。" && Descendants(item).OfType<TextBlock>().Any(block => block.Text == replayRow.DisplayText),
                    "Rendered replay subtitle omits the speaker label.");
                Need(replayRow.Segment.Text == source.Segments[0].Text, "A displayed speaker prefix contaminated transcript text.");
                SaveLayoutImage(evidence, "m5b-replay-" + language + ".png", 1, images);
                NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 3;
                view.SpeakerSegmentsBox.SelectedIndex = 0;
                Need(view.SaveRevisionButton.IsEnabled && view.EditBox.IsEnabled && view.EditBox.Text == source.Segments[0].Text,
                    "Speaker transcript editor disabled or editing a prefixed label.");
                SaveLayoutImage(evidence, "m5b-speakers-" + language + ".png", 1, images);
                view.SpeakerConfigurationExpander.IsExpanded = true;
                Need(view.SpeakerEmbeddingPresetBox.Items.Cast<SpeakerEmbeddingChoice>().First().Label == UiText.T("TitaNet-Large（M5B）"), "Model presets were not localized.");
                SaveLayoutImage(evidence, "m5b-model-presets-" + language + ".png", 1, images);
                view.SpeakerConfigurationExpander.IsExpanded = false;
            }
            ChangeLanguage("zh-CN", persist: false);

            ResetTranscriptPlayback(); view.SpeakerSegmentsBox.SelectedIndex = 1;
            await Task.Delay(120, token);
            Need(player.Source is null && pendingTranscriptSeek is null, "Speaker selection alone started playback.");
            InvokeSpeakerRow(1); await ObserveSeek(800, "first-click");
            SeekPlaybackMilliseconds(2000); InvokeSpeakerRow(1); await ObserveSeek(800, "repeat-click-same-row");
            view.SpeakerSegmentsBox.SelectedIndex = 2;
            view.SpeakerSegmentsBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, Environment.TickCount, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = view.SpeakerSegmentsBox });
            await ObserveSeek(1200, "keyboard-enter");
            ResetTranscriptPlayback();
            foreach (var active in new[] { SessionState.Recording, SessionState.Paused, SessionState.Stopping })
            {
                state = active; UpdateControls(); InvokeSpeakerRow(0);
                await Task.Delay(50, token);
                Need(player.Source is null && pendingTranscriptSeek is null && !transcriptOwnsPlayer, "Speaker click bypassed recording playback guard: " + active);
            }
            state = SessionState.Idle; UpdateControls();

            view.WorkspaceTabs.SelectedIndex = 1; view.SegmentsBox.SelectedIndex = 1;
            string draftText = "人工校订左边文字😀";
            view.EditBox.Text = draftText;
            view.SegmentsBox.SelectedIndex = 2; view.EditBox.Text = "另一段批量草稿";
            view.StartTimestampBox.Text = "00:00:01.250"; view.EndTimestampBox.Text = "00:00:02.350";
            SelectMeeting(other, loadSavedMix: false); SelectMeeting(meeting);
            await savedMixWork.WaitAsync(token); await replayLoadWork.WaitAsync(token); await LoadTranscriptVersionsAsync(parent.RevisionId);
            view.SegmentsBox.SelectedIndex = 1;
            Need(view.EditBox.Text == draftText, "Switching meetings lost unsaved speaker text.");
            view.SaveRevisionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            var edited = currentSpeakerRevision ?? throw new InvalidDataException(view.StatusText.Text);
            Need(edited.ParentRevisionId == parent.RevisionId && edited.Segments[1].Text == draftText &&
                edited.Segments[1].SpeakerId == parent.Segments[1].SpeakerId && edited.Segments[1].StartMs == 800 && edited.Segments[1].EndMs == 1200 &&
                edited.Speakers.SequenceEqual(parent.Speakers) && edited.Segments.Count == parent.Segments.Count,
                "Saving speaker wording lost names, stable identities, manual cut, timing or version lineage.");
            Need(edited.Segments[2].Text == "另一段批量草稿" && edited.Segments[2].StartMs == 1250 && edited.Segments[2].EndMs == 2350 &&
                edited.Segments[2].ManualTiming && edited.Corrections[^1].SubtitleEdits?.Count == 2,
                "Saving the version did not atomically apply both drafts and explicit manual time.");
            await LoadTranscriptVersionsAsync(parent.RevisionId); view.SegmentsBox.SelectedIndex = 2;
            Need(view.EditBox.Text == parent.Segments[2].Text, "Saved draft leaked back into the immutable parent.");
            Need(CloseFixtureHash(Path.Combine(meeting, "diarization", "revisions", parent.RevisionId, "revision.json")) == parentHash &&
                TranscriptService.Load(meeting, source.RevisionId).Segments.SequenceEqual(source.Segments), "Saving wording overwrote a parent or source.");

            await LoadTranscriptVersionsAsync(edited.RevisionId); view.SegmentsBox.SelectedIndex = 1;
            Need(view.EditBox.Text == draftText && ((TranscriptRow)view.SegmentsBox.SelectedItem).DisplayText.StartsWith("[本场主持人] ", StringComparison.Ordinal),
                "New version lost the separate label and editable wording.");
            foreach (string extension in new[] { "txt", "srt", "vtt" })
            {
                string labeled = Path.Combine(evidence, "edited-with-speakers." + extension), plain = Path.Combine(evidence, "edited-plain." + extension);
                view.IncludeSpeakerLabelsBox.IsChecked = true;
                StartTranscriptWork(() => extension == "txt" ? ExportTranscriptTextAsync(labeled) : ExportTranscriptSubtitlesAsync(labeled)); await transcriptWork.WaitAsync(token);
                view.IncludeSpeakerLabelsBox.IsChecked = false;
                StartTranscriptWork(() => extension == "txt" ? ExportTranscriptTextAsync(plain) : ExportTranscriptSubtitlesAsync(plain)); await transcriptWork.WaitAsync(token);
                Need(File.ReadAllText(labeled).Contains(draftText) && File.ReadAllText(labeled).Contains("本场主持人") &&
                    File.ReadAllText(plain).Contains(draftText) && !File.ReadAllText(plain).Contains("本场主持人"), "Edited speaker export failed: " + extension);
            }
            view.IncludeSpeakerLabelsBox.IsChecked = true;
            view.SearchBox.Text = draftText; view.SearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            Need(view.SegmentsBox.Items.Cast<TranscriptRow>().Any(row => row.Revision.RevisionId == edited.RevisionId && row.Segment.Text == draftText),
                "Speaker text edit did not reach the rebuilt search index.");

            // The source meeting of a search result owns its edits even if another meeting is selected.
            var searched = view.SegmentsBox.Items.Cast<TranscriptRow>().Single(row => row.Revision.RevisionId == edited.RevisionId);
            SelectMeeting(other, loadSavedMix: false);
            await transcriptWork.WaitAsync(token);
            view.SegmentsBox.ItemsSource = new[] { searched }; view.SegmentsBox.SelectedIndex = 0;
            view.EditBox.Text = "跨会搜索校订，归属原会议。";
            view.SaveRevisionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
            var crossMeeting = SpeakerService.ListRevisions(meeting).Single(revision => revision.ParentRevisionId == edited.RevisionId);
            Need(crossMeeting.Segments[1].Text == "跨会搜索校订，归属原会议。" && selectedMeetingDirectory == meeting &&
                SpeakerService.ListRevisions(other).Count == 0, "Search-result edit saved to or selected a different meeting.");

            speakerPresentations.Clear(); SelectMeeting(meeting); await savedMixWork.WaitAsync(token); await replayLoadWork.WaitAsync(token);
            await LoadTranscriptVersionsAsync(crossMeeting.RevisionId); view.SegmentsBox.SelectedIndex = 1;
            Need(currentSpeakerRevision?.RevisionId == crossMeeting.RevisionId && view.EditBox.Text == crossMeeting.Segments[1].Text &&
                currentSpeakerRevision.Speakers.SequenceEqual(parent.Speakers), "Disk reopen lost edited speaker text or names.");
            ReplayRevisionBox.SelectedItem = ReplayRevisionBox.Items.Cast<RevisionRow>().Single(row => row.Revision.RevisionId == source.RevisionId);
            Need(ReplaySegmentsBox.Items.Cast<TranscriptRow>().All(row => row.DisplayText == row.Segment.Text), "Plain transcript unexpectedly acquired speaker labels.");
            Need(originals.All(pair => CloseFixtureHash(pair.Key) == pair.Value) && host is null && !microphoneEnabled && !levelPreviewNativeEnabled,
                "M5B UI smoke changed audio originals or enabled capture.");
            return new { Status = "PASS", Kind = "actual WPF; generated silent PCM, synthetic assignments; muted playback; no capture or native model",
                ReplayLabels = true, SpeakerTextEditing = true, ImmutableParents = true, UnsavedDraftsRetained = true, CrossMeetingEditOwnership = true,
                ManualCutsNamesAndSpeakerIdsRetained = true, ExportFormats = new[] { "txt", "srt", "vtt" }, SearchAndReopen = true,
                EmbeddingPresets = true, ExistingAndCustomModelsPreserved = true, NewDefaultOnlyWhenBundled = true,
                SelectionDoesNotAutoplay = true, RecordingPausedStoppingBlockPlayback = true, Languages = new[] { "zh-CN", "en", "zh-Hant" }, Seeks = seeks, Images = images };
        }
        finally { state = SessionState.Idle; ResetTranscriptPlayback(); UpdateControls(); }

        void InvokeSpeakerRow(int index)
        {
            view.WorkspaceTabs.SelectedIndex = 3;
            view.SpeakerSegmentsBox.ScrollIntoView(view.SpeakerSegmentsBox.Items[index]); UpdateLayout();
            var item = view.SpeakerSegmentsBox.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem ?? throw new InvalidDataException("Speaker item missing.");
            item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = item });
        }
        async Task ObserveSeek(long expected, string action)
        {
            var watch = Stopwatch.StartNew();
            while (!playbackOpened && watch.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(10, token);
            player.Pause(); playbackPaused = true; ResetDisplayPlaybackClock();
            double actual = player.Position.TotalMilliseconds;
            Need(playbackOpened && player.IsMuted && player.Volume == 0 && Math.Abs(actual - expected) < 200,
                $"Speaker {action} did not seek: expected={expected}; observed={actual}; {view.StatusText.Text}");
            seeks.Add(new { Action = action, ExpectedMs = expected, ActualMs = actual });
        }
        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index); yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        static void WriteSilence(string directory)
        {
            const int rate = 48000, frames = rate * 6;
            const ulong epoch = 1000000;
            using var storage = new M1Storage(directory, epoch, DateTimeOffset.UtcNow, "M5B合成静音测试", isSynthetic: true);
            storage.AddSegment("remote-1", "remote", new WaveFormat(rate, 16, 1));
            storage.WritePacket("remote-1", new byte[frames * 2], new(0, frames, frames, 0, epoch, 0, true));
            storage.WriteBoundary("remote-1", "synthetic-stop", "stop", epoch + 60000000, frames, 0, null);
            storage.Finish(60000000, true, []);
        }
    }
}
