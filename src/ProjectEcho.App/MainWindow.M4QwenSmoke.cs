using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ProjectEcho.Core;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Actual WPF with generated files, muted playback and controlled task cancellation. No capture or model execution.</summary>
    internal async Task<object> M4QwenSmokeAsync(string evidence, CancellationToken token)
    {
        Need(host is null && !microphoneEnabled && !levelPreviewNativeEnabled && !transcriptBusy,
            "Qwen UI smoke requires the native-capture-disabled constructor.");
        dataRoot = Path.Combine(evidence, "synthetic-qwen-data"); Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        string first = Path.Combine(RecordingsRoot, "qwen-a"), second = Path.Combine(RecordingsRoot, "qwen-b"), older = Path.Combine(RecordingsRoot, "old-whisper");
        foreach (string directory in new[] { first, second, older }) WriteCloseFixture(directory);
        var originals = Directory.EnumerateFiles(RecordingsRoot, "*", SearchOption.AllDirectories).ToDictionary(path => path, CloseFixtureHash);
        string exe = Path.Combine(AppContext.BaseDirectory, "ProjectEcho.exe");
        string fakeModel = Path.Combine(evidence, "synthetic-not-a-model.gguf"); File.WriteAllText(fakeModel, "Never execute this synthetic UI model fixture.");
        var config = new LocalTranscriptionConfiguration(QwenTranscriber.BackendId, exe, fakeModel, Threads: 4,
            ModelName: "SYNTHETIC UI ONLY", ModelLicenseAcknowledged: true,
            QwenAlignment: new(exe, fakeModel));
        QwenSettingsStore.SaveDefaults(Path.Combine(dataRoot, "settings"), new(1, QwenTranscriber.BackendId, config));
        PrepareNewMeetingWhisperDefaults()(first); PrepareNewMeetingWhisperDefaults()(second);
        var view = transcriptionView!; var images = new List<string>();
        SelectMeeting(first, loadSavedMix: false); NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 0;
        Need(QwenSelected && qwenSettingsReady && view.QwenConfigurationPanel.Visibility == Visibility.Visible &&
            view.WhisperConfigurationPanel.Visibility == Visibility.Collapsed, "Qwen default selection/panel missing.");
        Need(view.QwenAlignmentHardwareBox.Items.Cast<ComboBoxItem>().Select(item => item.Tag?.ToString())
                .SequenceEqual(new[] { "Cpu", "OfficialPython", "Vulkan" }) && CurrentQwenConfiguration().QwenAlignment is { Hardware: LocalTranscriptionHardware.Cpu, Engine: QwenAlignmentEngine.Native },
            "Legacy/default alignment hardware changed from CPU, or Auto is offered for alignment.");
        view.QwenHardwareBox.SelectedIndex = 1;
        Need(CurrentQwenConfiguration().Hardware == LocalTranscriptionHardware.Vulkan &&
            CurrentQwenConfiguration().QwenAlignment?.Hardware == LocalTranscriptionHardware.Cpu,
            "Recognition hardware selection silently changed alignment hardware.");
        view.QwenHardwareBox.SelectedIndex = 0; view.QwenAlignmentHardwareBox.SelectedIndex = 2; view.QwenDeviceBox.Text = "2";
        var independentHardware = CurrentQwenConfiguration();
        Need(independentHardware.Hardware == LocalTranscriptionHardware.Cpu && independentHardware.GpuDeviceIndex == 2 &&
            independentHardware.QwenAlignment is { Hardware: LocalTranscriptionHardware.Vulkan, GpuDeviceIndex: 2 } &&
            independentHardware.QwenAlignment.ExecutablePath == exe,
            "Alignment hardware/shared GPU index was not independent, or a custom program path was replaced.");
        qwenPythonExecutable = exe;
        string expectedPythonModelDirectory = Path.Combine(evidence, "empty-synthetic-official-model");
        qwenPythonModelDirectory = expectedPythonModelDirectory; Directory.CreateDirectory(qwenPythonModelDirectory);
        qwenPythonBridgePath = Path.Combine(evidence, "synthetic-bridge-never-run.py"); File.WriteAllText(qwenPythonBridgePath, "# UI fixture only; never execute.");
        view.QwenAlignmentHardwareBox.SelectedIndex = 1;
        var selectedPython = CurrentQwenConfiguration().QwenAlignment!;
        Need(selectedPython is { Engine: QwenAlignmentEngine.OfficialPython, Hardware: LocalTranscriptionHardware.Cpu } &&
            selectedPython.ExecutablePath == exe && selectedPython.ModelPath == fakeModel && selectedPython.Python?.ExecutablePath == exe &&
            selectedPython.Python.ModelDirectory == qwenPythonModelDirectory && !new QwenTranscriber().Inspect(CurrentQwenConfiguration()).Ready,
            "Unconfigured official Python silently fell back or replaced the native configuration.");
        view.QwenDisableCpuAccelerationBox.IsChecked = true;
        var compatibleRequest = CurrentQwenConfiguration().QwenAlignment!;
        var compatibleActual = QwenAlignmentRuntime.EffectiveConfiguration(compatibleRequest);
        string generic = Path.Combine(AppContext.BaseDirectory, "Tools", "qwen-aligner", "audiocpp_cli.exe");
        Need(compatibleRequest is { Engine: QwenAlignmentEngine.OfficialPython, DisableCpuAcceleration: true } &&
            compatibleRequest.ExecutablePath == exe && compatibleRequest.Python?.ModelDirectory == qwenPythonModelDirectory &&
            compatibleActual is { Engine: QwenAlignmentEngine.Native, Hardware: LocalTranscriptionHardware.Cpu } &&
            compatibleActual.ExecutablePath == generic && view.QwenAlignerExeText.Text == generic &&
            !view.QwenAlignerExeButton.IsEnabled && view.QwenAlignerModelButton.Content?.ToString() == UiText.T("句子定位模型"),
            "Old-CPU compatibility did not visibly force bundled generic Native/GGUF while preserving requested settings.");
        view.QwenDisableCpuAccelerationBox.IsChecked = false;
        if (QwenAlignmentRuntime.SupportsOptimizedCpu)
            Need(QwenAlignmentRuntime.EffectiveConfiguration(CurrentQwenConfiguration().QwenAlignment!).Engine == QwenAlignmentEngine.OfficialPython &&
                view.QwenAlignerModelText.Text == qwenPythonModelDirectory, "Unchecking compatibility did not restore the selected official configuration.");
        SaveQwenMeeting(); SelectMeeting(second, loadSavedMix: false); SelectMeeting(first, loadSavedMix: false);
        Need(CurrentQwenConfiguration().QwenAlignment?.Python?.ModelDirectory == expectedPythonModelDirectory && QwenOfficialPythonSelected,
            "Python/native mode-specific settings did not survive a meeting switch.");
        view.QwenAlignmentHardwareBox.SelectedIndex = 2; view.QwenDisableCpuAccelerationBox.IsChecked = false;
        Need(CurrentQwenConfiguration().QwenAlignment is { Engine: QwenAlignmentEngine.Native, Hardware: LocalTranscriptionHardware.Vulkan } &&
            CurrentQwenConfiguration().QwenAlignment!.ModelPath == fakeModel, "Returning to native Vulkan lost its saved GGUF choice.");
        view.QwenPromptBox.Text = "A 的独立提示词"; view.QwenThreadsBox.Text = "6"; SaveQwenMeeting();
        view.SaveQwenDefaultsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var savedDefault = QwenSettingsStore.LoadDefaults(Path.Combine(dataRoot, "settings"));
        Need(savedDefault.Configuration?.Threads == 6 && savedDefault.Configuration.InitialPrompt is null,
            "New-meeting defaults included a private meeting prompt or missed thread count.");
        SelectMeeting(second, loadSavedMix: false);
        Need(QwenSelected && CurrentQwenConfiguration().Threads == 4 && view.QwenPromptBox.Text.Length == 0,
            "Updated defaults or A prompt leaked into B.");
        Need(CurrentQwenConfiguration().QwenAlignment?.Hardware == LocalTranscriptionHardware.Cpu &&
            CurrentQwenConfiguration().GpuDeviceIndex == 0, "A alignment hardware/device leaked into existing B.");
        view.QwenPromptBox.Text = "B 的独立提示词"; SaveQwenMeeting();
        SelectMeeting(older, loadSavedMix: false);
        Need(!QwenSelected && view.WhisperConfigurationPanel.Visibility == Visibility.Visible,
            "An old meeting's usable Whisper engine was silently replaced.");
        SelectMeeting(first, loadSavedMix: false);
        Need(QwenSelected && CurrentQwenConfiguration().Threads == 6 && view.QwenPromptBox.Text == "A 的独立提示词",
            "Meeting-specific Qwen settings did not round-trip.");
        Need(CurrentQwenConfiguration().Hardware == LocalTranscriptionHardware.Cpu &&
            CurrentQwenConfiguration().QwenAlignment is { Hardware: LocalTranscriptionHardware.Vulkan, GpuDeviceIndex: 2 },
            "Meeting-specific independent alignment hardware did not round-trip.");
        string next = Path.Combine(RecordingsRoot, "qwen-new"); WriteCloseFixture(next); PrepareNewMeetingWhisperDefaults()(next);
        SelectMeeting(next, loadSavedMix: false);
        Need(QwenSelected && CurrentQwenConfiguration().Threads == 6 && view.QwenPromptBox.Text.Length == 0,
            "New meeting did not inherit the explicit Qwen default without private prompt.");
        SelectMeeting(first, loadSavedMix: false);
        Width = 1280; Height = 930;
        foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
        {
            ChangeLanguage(language, persist: false); UpdateControls(); await Render();
            Need(view.QwenPromptBox.Text == "A 的独立提示词" && QwenSelected, "Language switch changed configured meeting.");
            view.AsrBackendBox.BringIntoView(); await Render();
            SaveLayoutImage(evidence, "m4-qwen-settings-" + language + ".png", 1, images);
        }
        Width = MinWidth; Height = MinHeight;
        view.RunQwenButton.BringIntoView(); await Render();
        SaveLayoutImage(evidence, "m4-qwen-minimum-en.png", 1, images);
        Width = 1280; Height = 930;

        currentMix = await Task.Run(() => M2ExportService.RenderMeeting(first, encoder: null, token), token); renderedMeetings[first] = currentMix;
        string subtitles = Path.Combine(evidence, "synthetic-cues.srt");
        File.WriteAllText(subtitles, "1\n00:00:00,100 --> 00:00:00,650\n合成第一句。\n\n2\n00:00:01,100 --> 00:00:01,800\nSynthetic second sentence.\n");
        var revision = TranscriptService.Import(new(first, Path.GetFileName(first), 2000, subtitles, "Synthetic UI only; not ASR validation", IsSynthetic: true));
        DisplayRevision(first, revision); view.WorkspaceTabs.SelectedIndex = 1; view.SegmentsBox.SelectedIndex = 1;
        foreach (var blocked in new[] { SessionState.Recording, SessionState.Paused, SessionState.Stopping })
        {
            state = blocked; UpdateControls(); SeekTranscript();
            Need(!view.SeekButton.IsEnabled && pendingTranscriptSeek is null && player.Source is null,
                "Recording/paused/saving permitted subtitle playback.");
        }
        state = SessionState.Saved; UpdateControls();
        player.Volume = 0; player.IsMuted = true;
        try
        {
            // Raise the actual WPF row mouse-up event, including an already selected row.
            await Render(); view.SegmentsBox.ScrollIntoView(view.SegmentsBox.Items[1]); await Render();
            var item = view.SegmentsBox.ItemContainerGenerator.ContainerFromIndex(1) as ListBoxItem
                ?? throw new InvalidDataException("Subtitle row was not materialized.");
            item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = item });
            var timer = Stopwatch.StartNew();
            while (!playbackOpened && timer.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25, token);
            File.WriteAllText(Path.Combine(evidence, "replay-click-diagnostics.json"), System.Text.Json.JsonSerializer.Serialize(new
            { playbackOpened, player.IsMuted, player.Volume, Source = player.Source?.ToString(), state,
                view.StatusText.Text, SelectedRow = view.SegmentsBox.SelectedItem?.ToString(),
                VisibleSegment = visibleSegment?.Segment.StartMs, transcriptBusy,
                Pending = pendingTranscriptSeek?.ToString(), mixedPath, MixPath = currentMix?.WavePath,
                ItemDataContext = item.DataContext?.ToString() }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Need(playbackOpened && player.IsMuted && player.Volume == 0 && player.Source is not null,
                "Clicking the selected sentence did not open muted saved audio.");
            player.Pause(); playbackPaused = true; ResetDisplayPlaybackClock();
            Need(player.Position.TotalMilliseconds >= 1090 && player.Position.TotalMilliseconds < 1500,
                "Sentence click did not seek to the selected millisecond boundary.");
            SaveLayoutImage(evidence, "m4-qwen-sentence-replay.png", 1, images);
        }
        finally { ResetTranscriptPlayback(); }

        // Exercise the production cancellation signal without starting a Host or inference process.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StartTranscriptWork(async () =>
        {
            qwenRunning = true; SetRecognitionMeeting(first); entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, transcriptCancellation!.Token); }
            catch (OperationCanceledException) { cancelled.SetResult(); await release.Task; throw; }
            finally { qwenRunning = false; SetRecognitionMeeting(null); }
        }, "asr", first);
        await entered.Task.WaitAsync(token);
        Need(StartButton.IsEnabled && view.CancelButton.IsEnabled && !view.QwenConfigurationPanel.IsEnabled,
            "Background Qwen blocked next recording or permitted concurrent reconfiguration.");
        SelectMeeting(second, loadSavedMix: false); view.StatusText.Text = "B status must remain";
        YieldQwenToRecording(); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
        Need(!transcriptWork.IsCompleted && StartButton.IsEnabled, "Recording handoff waited for asynchronous model cleanup.");
        release.SetResult(); await transcriptWork.WaitAsync(token);
        Need(view.StatusText.Text == "B status must remain" && view.QwenPromptBox.Text == "B 的独立提示词",
            "A cancelled result contaminated selected meeting B.");

        var reopened = new MainWindow(enumerateSourcesOnLoad: false) { dataRoot = dataRoot };
        try
        {
            reopened.Show(); reopened.SelectMeeting(first, loadSavedMix: false);
            Need(reopened.QwenSelected && reopened.CurrentQwenConfiguration().Threads == 6 &&
                reopened.transcriptionView!.QwenPromptBox.Text == "A 的独立提示词", "Reopened Qwen meeting lost settings.");
            Need(reopened.CurrentQwenConfiguration().QwenAlignment is { Hardware: LocalTranscriptionHardware.Vulkan, GpuDeviceIndex: 2 },
                "Reopening lost independent alignment hardware/device selection.");
            var loaded = TranscriptService.Load(first, revision.RevisionId);
            Need(loaded.Segments[1].StartMs == 1100 && loaded.Segments[1].Text == "Synthetic second sentence.",
                "Reopening changed original sentence/time relationship.");
        }
        finally { reopened.Close(); }
        // A broken preferences file is recoverable through the actual outside-panel button.
        string damaged = Path.Combine(RecordingsRoot, "damaged-qwen-settings"); WriteCloseFixture(damaged);
        PrepareNewMeetingWhisperDefaults()(damaged);
        var damagedOriginals = Directory.EnumerateFiles(damaged, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("qwen-settings.json", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => path, CloseFixtureHash);
        string damagedPath = Path.Combine(damaged, QwenSettingsStore.RelativePath);
        const string damagedText = "{ intentional synthetic invalid Qwen preferences";
        File.WriteAllText(damagedPath, damagedText);
        SelectMeeting(damaged, loadSavedMix: false); NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 0;
        await transcriptWork.WaitAsync(token); UpdateControls(); await Render();
        Need(!qwenSettingsReady && view.ResetQwenConfigurationButton.IsEnabled &&
            view.ResetQwenConfigurationButton.Visibility == Visibility.Visible, "Broken preferences lack a usable recovery button.");
        view.ResetQwenConfigurationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Render();
        string[] backups = Directory.GetFiles(Path.GetDirectoryName(damagedPath)!, "qwen-settings.json.invalid-*.bak");
        Need(qwenSettingsReady && view.AsrBackendBox.IsEnabled && backups.Length == 1 && File.ReadAllText(backups[0]) == damagedText &&
            damagedOriginals.All(pair => CloseFixtureHash(pair.Key) == pair.Value), "Reset lost the damaged settings backup or changed audio/Whisper originals.");
        Need(originals.All(pair => CloseFixtureHash(pair.Key) == pair.Value) && host is null && !microphoneEnabled,
            "Qwen UI smoke changed authoritative recording files or started capture.");
        return new { Status = "PASS", SourceMode = "SYNTHETIC FILES / ACTUAL WPF / MUTED REPLAY / CONTROLLED ASYNC CANCELLATION",
            ActualQwenInference = "NOT_RUN in this UI smoke; separate native evidence required", MeetingSettingsAndDefaults = true,
            AlignmentCpuVulkanIndependentAndLegacyCpuDefault = true, SharedGpuIndexPersistedToBothStages = true,
            CustomAlignmentExecutablePreserved = true,
            OfficialPythonExplicitUnconfiguredRejected = true, NativeAndPythonSettingsBothPreserved = true,
            OldCpuCompatibilityForcesGenericWithoutDestroyingChoice = true,
            ExistingWhisperPreserved = true, ChineseAndEnglishViews = true, ClickSentenceReplay = true,
            RecordingPauseSavingPlaybackGuards = true, RecordingCancellationDoesNotWait = true,
            CancelledResultKeepsMeetingOwnership = true, Reopen = true, CorruptSettingsRecoveryKeepsBackupAndOriginals = true,
            OriginalHashesUnchanged = true,
            CaptureStarted = false, MicrophoneEnabled = false, AudioAudible = false, Images = images };

        async Task Render() => await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
        static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    }
}
