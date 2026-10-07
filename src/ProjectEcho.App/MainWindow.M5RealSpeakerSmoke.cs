using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Explicit native CPU probe over the hash-locked public four-speaker sample; no capture or ASR-quality claim.</summary>
    internal async Task<object> M5RealSpeakerSmokeAsync(string audioPath, string executable, string segmentation, string embedding,
        string evidence, CancellationToken token, string? whisperCli = null, string? whisperModelPath = null)
    {
        Check(host is null && !microphoneEnabled && !levelPreviewNativeEnabled && !transcriptBusy, "Requires capture-disabled idle WPF.");
        string sourcePath = Path.GetFullPath(audioPath); RejectReparseAncestors(sourcePath);
        const string acceptedHash = "bedf036caed208386c67b4ef4b11f83d74dd0d420b102163a1c33cd09cde7010";
        Check(new FileInfo(sourcePath).Length < 4_000_000 && CloseFixtureHash(sourcePath) == acceptedHash, "Only the reviewed public four-speaker sample is authorized in this probe.");
        dataRoot = Path.Combine(evidence, "真实CPU-界面数据"); Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        recordingPreflight = _ => throw new InvalidOperationException("Real speaker UI probe never starts recording.");
        var first = await ImportAudioFileAsync(sourcePath, token); var second = await ImportAudioFileAsync(sourcePath, token);
        SelectMeeting(first.MeetingDirectory); await Task.WhenAll(savedMixWork, replayLoadWork).WaitAsync(token);
        var meeting = SelectedTranscriptMeeting();
        string input = Path.Combine(evidence, "speaker-pipeline-fixture.srt");
        TimeSpan duration = TimeSpan.FromMilliseconds(meeting.DurationMs);
        File.WriteAllText(input, "1\n00:00:00,000 --> " + duration.ToString(@"hh\:mm\:ss\,fff") + "\n仅用于真实说话人按钮流程的测试文字，非原音转写。\n");
        var source = TranscriptService.Import(new(meeting.Directory, meeting.Id, meeting.DurationMs, input, "UI pipeline fixture, NOT recognized speech"), token);
        await LoadTranscriptVersionsAsync(source.RevisionId); NavigateTo(3); var view = transcriptionView!; view.WorkspaceTabs.SelectedIndex = 3;
        speakerWorker = executable; speakerSegmentation = segmentation; speakerEmbedding = embedding;
        view.SpeakerLicenseBox.IsChecked = true; view.SpeakerCountBox.Clear(); view.SpeakerThreadsBox.Text = "2"; UpdateControls();
        var originalHashes = new[] { sourcePath, first.OriginalPath, first.Mix.WavePath, Path.Combine(first.MeetingDirectory, "meeting.json") }.ToDictionary(path => path, CloseFixtureHash);
        using var linked = token.Register(() => Dispatcher.BeginInvoke(new Action(() => transcriptCancellation?.Cancel())));
        var elapsed = Stopwatch.StartNew();
        view.RunSpeakersButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Task owned = transcriptWork;
        Check(transcriptBusy && speakerRunning && recognitionMeetingDirectory == meeting.Directory, "Actual button did not start an owned speaker task.");
        SelectMeeting(second.MeetingDirectory); view.WorkspaceTabs.SelectedIndex = 0;
        const string otherStatus = "B is selected while A's public CPU analysis runs"; view.StatusText.Text = otherStatus;
        await owned.WaitAsync(token); elapsed.Stop();
        Check(selectedMeetingDirectory == second.MeetingDirectory && view.StatusText.Text == otherStatus &&
            SpeakerService.ListRevisions(second.MeetingDirectory).Count == 0 && !transcriptBusy && !speakerRunning, "Native result or status leaked into the other selected meeting.");
        var automatic = SpeakerService.ListRevisions(meeting.Directory).Single();
        var autoAnalysis = SpeakerService.LoadAnalysis(meeting.Directory, automatic.AnalysisId);
        Check(autoAnalysis.Engine.KnownSpeakerCount is null && automatic.Speakers.Count > 0 && autoAnalysis.Engine.ActualDevice == "CPU", "Automatic mode was not executed by the native CPU model.");
        SelectMeeting(meeting.Directory); await Task.WhenAll(savedMixWork, replayLoadWork).WaitAsync(token); await LoadTranscriptVersionsAsync(automatic.RevisionId);
        view.WorkspaceTabs.SelectedIndex = 3; view.SpeakerIdentityBox.SelectedIndex = 0; view.SpeakerDisplayNameBox.Text = "本机测试名称";
        view.RenameSpeakerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        var manual = currentSpeakerRevision ?? throw new InvalidDataException(view.StatusText.Text);
        view.SpeakerCountBox.Text = "4"; view.RunSpeakersButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        var known = currentSpeakerRevision ?? throw new InvalidDataException(view.StatusText.Text);
        Check(known.RevisionId != manual.RevisionId && known.Speakers.Count == 4 &&
            SpeakerService.LoadAnalysis(meeting.Directory, known.AnalysisId).Engine.KnownSpeakerCount == 4 &&
            SpeakerService.Load(meeting.Directory, manual.RevisionId).Speakers.Any(item => item.DisplayName == "本机测试名称"), "Known-count rerun overwrote the manual version or did not persist its hint.");
        int beforeCancel = SpeakerService.ListRevisions(meeting.Directory).Count;
        view.RunSpeakersButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Task cancelling = transcriptWork;
        await Task.Delay(350, token); view.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await cancelling.WaitAsync(token);
        Check(!speakerRunning && !transcriptBusy && SpeakerService.ListRevisions(meeting.Directory).Count == beforeCancel,
            "Cancel published a speaker revision or left the task gate held.");
        // Exercise the production Whisper button, not a core request with manually supplied provenance.
        // This specifically guards passing the actual mix hash and file-v1 timeline into Whisper's alignment evidence.
        whisperCli ??= Path.Combine(AppContext.BaseDirectory, "Tools", "whisper-cpu", "whisper-cli.exe");
        whisperModelPath ??= Path.Combine(AppContext.BaseDirectory, "Models", "ggml-base.bin");
        ApplyMeetingWhisperConfiguration(new(whisperCli, whisperModelPath, "zh", 2, "base-m5-ui-probe", ModelLicenseAcknowledged: true), null);
        view.AsrBackendBox.SelectedIndex = 0; view.WorkspaceTabs.SelectedIndex = 0; UpdateControls();
        Check(TryValidateWhisperConfiguration(requireAudio: true, out _), "Real M5 UI probe requires local Whisper CPU/model files: " + view.StatusText.Text);
        view.RunWhisperButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        var whisper = visibleRevision ?? throw new InvalidDataException(view.StatusText.Text);
        Check(whisper.Provider.StartsWith("whisper.cpp", StringComparison.Ordinal) && whisper.Alignment is { } alignment &&
            alignment.SourceMixSha256 == first.Mix.WaveSha256 && alignment.TimelinePolicy == "file-v1" &&
            alignment.InputWaveSha256 == autoAnalysis.Audio.InputWaveSha256 && alignment.FrameCount == autoAnalysis.Audio.FrameCount,
            "Production Whisper button lost its actual audio hash or timeline policy.");
        view.WorkspaceTabs.SelectedIndex = 3;
        view.SpeakerAnalysisBox.SelectedItem = view.SpeakerAnalysisBox.Items.Cast<SpeakerAnalysisRow>().Single(row => row.Analysis.AnalysisId == automatic.AnalysisId);
        int beforeWhisperApply = SpeakerService.ListAnalyses(meeting.Directory).Count;
        view.ApplySpeakerAnalysisButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        var whisperSpeakers = currentSpeakerRevision ?? throw new InvalidDataException(view.StatusText.Text);
        Check(whisperSpeakers.SourceTranscriptRevisionId == whisper.RevisionId && whisperSpeakers.AnalysisId == automatic.AnalysisId &&
            string.Concat(whisperSpeakers.Segments.Select(item => item.Text)) == string.Concat(whisper.Segments.Select(item => item.Text)) &&
            SpeakerService.ListAnalyses(meeting.Directory).Count == beforeWhisperApply,
            "Real Whisper-button transcript could not reuse the actual saved CPU speaker analysis without dropping or duplicating words.");
        Check(originalHashes.All(pair => CloseFixtureHash(pair.Key) == pair.Value) && host is null && !microphoneEnabled, "Native UI probe modified an original or enabled capture.");
        var images = new List<string>(); view.SpeakerConfigurationExpander.IsExpanded = true;
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render); SaveLayoutImage(evidence, "m5-real-cpu.png", 1, images);
        return new { Status = autoAnalysis.Speakers.Count == 4 ? "PASS" : "PIPELINE_PASS_AUTO_COUNT_FAIL", Fixture = "public four-speakers-zh.wav; test subtitle is explicitly not ASR output", AudioSha256 = acceptedHash,
            PreparedInputSha256 = autoAnalysis.Audio.InputWaveSha256, MixedWaveSha256 = autoAnalysis.Audio.SourceMixSha256,
            AutomaticCountQuality = autoAnalysis.Speakers.Count == 4 ? "PASS" : "FAIL", ExpectedAutomaticCount = 4,
            AutomaticSpeakers = autoAnalysis.Speakers.Count, KnownSpeakers = known.Speakers.Count, NativeDevice = autoAnalysis.Engine.ActualDevice,
            AutoElapsedMs = elapsed.ElapsedMilliseconds, SwitchMeetingOwnership = true, ManualVersionPreserved = true, CancellationNoRevision = true,
            Versions = SpeakerService.ListRevisions(meeting.Directory).Count, TimelinePolicy = autoAnalysis.Audio.TimelinePolicy,
            RealWhisperButtonAudioProvenance = true, RealWhisperApplySavedAnalysis = true, WhisperSegments = whisper.Segments.Count, Images = images };
        static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
