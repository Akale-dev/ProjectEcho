using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
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
    /// <summary>
    /// Explicit real UI/model probe over three hash-allowlisted public/synthetic files only.
    /// No arbitrary meeting file, capture, microphone, network download, or audible playback.
    /// Caller must use enumerateSourcesOnLoad:false and run this separately from long-run resource measurement.
    /// </summary>
    internal async Task<object> M4RealQwenSmokeAsync(string audioPath, string executablePath, string modelPath,
        string alignerExecutablePath, string alignerModelPath, string evidence, CancellationToken token,
        LocalTranscriptionHardware hardware = LocalTranscriptionHardware.Vulkan,
        LocalTranscriptionHardware alignmentHardware = LocalTranscriptionHardware.Cpu,
        QwenPythonAlignmentConfiguration? pythonAlignment = null, bool disableCpuAcceleration = false)
    {
        RequireM4RealProbeIdle();
        if (hardware is not (LocalTranscriptionHardware.Cpu or LocalTranscriptionHardware.Vulkan))
            throw new InvalidDataException("The real Qwen UI probe requires an explicit CPU or Vulkan selection.");
        if (alignmentHardware is not (LocalTranscriptionHardware.Cpu or LocalTranscriptionHardware.Vulkan))
            throw new InvalidDataException("The real Qwen UI probe requires explicit CPU or Vulkan alignment.");
        if (pythonAlignment is not null && alignmentHardware != LocalTranscriptionHardware.Cpu)
            throw new InvalidDataException("Official Python alignment is an explicit CPU-only test option.");
        if (disableCpuAcceleration && (hardware != LocalTranscriptionHardware.Cpu || alignmentHardware != LocalTranscriptionHardware.Cpu))
            throw new InvalidDataException("The compatibility probe requires explicit CPU transcription and CPU alignment.");
        string source = Path.GetFullPath(audioPath); RejectReparseAncestors(source);
        if (new FileInfo(source).Length is <= 0 or > 2_000_000 || !source.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only a bounded allowlisted PCM WAV fixture may enter the real Qwen UI probe.");
        string sourceHash = CloseFixtureHash(source);
        (string Fixture, bool Synthetic) fixture = sourceHash switch
        {
            "e79d33c7a26e88f5ceb46478182c6ce1157c5c5a5d7f28ef2bb54af5e3fdf1e4" => ("FLEURS-zh-short-CC-BY-4.0", false),
            "c305fc1f06f088af52cfede7355d52ea77f6c94cc8cc728801b91fa2ec96f0b7" => ("Windows-SAPI-mandarin-fixed-M4", true),
            "281ca42aa548cd5db14a0316d6b6c4589e6734b4038acfcb7c76313d8ddb3ae7" => ("Windows-SAPI-mixed-fixed-M4", true),
            _ => throw new InvalidDataException("The real Qwen UI probe rejects every non-allowlisted audio hash, including user meetings.")
        };
        evidence = ValidateLocalDataRoot(Path.GetFullPath(evidence)); Directory.CreateDirectory(evidence);
        string owned = Path.Combine(evidence, "real-qwen-ui-data");
        if (Directory.Exists(owned)) throw new IOException("Real Qwen UI evidence already exists; refuse overwrite.");
        Directory.CreateDirectory(owned); dataRoot = owned; DataRootText.Text = dataRoot;
        recordingPreflight = _ => throw new InvalidOperationException("The real Qwen UI probe must never request recording.");
        var configuration = new LocalTranscriptionConfiguration(QwenTranscriber.BackendId,
            Path.GetFullPath(executablePath), Path.GetFullPath(modelPath), "auto", 4,
            ModelName: Path.GetFileNameWithoutExtension(modelPath), ModelLicenseAcknowledged: true,
            Hardware: hardware, GpuDeviceIndex: 0,
            QwenAlignment: new(Path.GetFullPath(alignerExecutablePath), Path.GetFullPath(alignerModelPath), alignmentHardware, 0,
                Engine: pythonAlignment is null ? QwenAlignmentEngine.Native : QwenAlignmentEngine.OfficialPython,
                Python: pythonAlignment, DisableCpuAcceleration: disableCpuAcceleration));
        var effectiveConfiguration = QwenAlignmentRuntime.EffectiveConfiguration(configuration.QwenAlignment!);
        var readiness = new QwenTranscriber().Inspect(configuration);
        if (!readiness.Ready) throw new InvalidDataException("The real UI probe requires explicit local ASR and alignment files: " + readiness.Message);
        QwenSettingsStore.SaveDefaults(Path.Combine(dataRoot, "settings"), new(1, QwenTranscriber.BackendId, configuration));
        var view = transcriptionView!; var images = new List<string>();
        var imported = await ImportAudioFileAsync(source, token);
        var second = await ImportAudioFileAsync(source, token);
        // ImportAudioFileAsync is the exact action behind the normal file picker; it calls
        // AudioImportService and keeps copied originals and the full decoded listening WAV.
        SelectMeeting(imported.MeetingDirectory); NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 0;
        await Task.WhenAll(savedMixWork, replayLoadWork, transcriptWork).WaitAsync(token);
        UpdateControls();
        CheckM4Real(QwenSelected && qwenSettingsReady && view.RunQwenButton.IsEnabled &&
            currentMix?.WavePath == imported.Mix.WavePath && imported.OriginalSha256 == sourceHash,
            "Imported audio/configuration did not make the actual Qwen button ready.");
        if (disableCpuAcceleration)
            CheckM4Real(view.QwenDisableCpuAccelerationBox.IsChecked == true &&
                view.QwenAlignerExeText.Text == effectiveConfiguration.ExecutablePath && !view.QwenAlignerExeButton.IsEnabled,
                "The real compatibility UI did not clearly show the forced generic CPU program.");
        var originalHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [source] = sourceHash,
            [imported.OriginalPath] = CloseFixtureHash(imported.OriginalPath),
            [imported.Mix.WavePath] = CloseFixtureHash(imported.Mix.WavePath),
            [Path.Combine(imported.MeetingDirectory,"meeting.json")] = CloseFixtureHash(Path.Combine(imported.MeetingDirectory,"meeting.json")),
            [second.OriginalPath] = CloseFixtureHash(second.OriginalPath),
            [second.Mix.WavePath] = CloseFixtureHash(second.Mix.WavePath)
        };
        Stage("ready", new { fixture.Fixture, fixture.Synthetic, sourceHash, configuration, imported.MeetingDirectory });
        using var cancellation = token.Register(() =>
        {
            if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(new Action(() => transcriptCancellation?.Cancel()));
        });
        var elapsed = Stopwatch.StartNew();
        Task before = transcriptWork;
        view.RunQwenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Task ownerWork = transcriptWork;
        CheckM4Real(!ReferenceEquals(before, ownerWork) && transcriptBusy && qwenRunning &&
            recognitionMeetingDirectory == imported.MeetingDirectory,
            "RunQwenButton did not enter the production owned transcription task.");
        Stage("actual_button_started", new { transcriptBusy, qwenRunning, recognitionMeetingDirectory });
        // Switch while the real model is still running. Its completion must retain A ownership.
        SelectMeeting(second.MeetingDirectory); view.WorkspaceTabs.SelectedIndex = 0;
        const string otherStatus = "B remains selected during A's real Qwen inference";
        view.StatusText.Text = otherStatus;
        try { await ownerWork.WaitAsync(token); }
        finally
        {
            if (token.IsCancellationRequested)
            {
                transcriptCancellation?.Cancel();
                await ownerWork.WaitAsync(TimeSpan.FromSeconds(20));
            }
        }
        elapsed.Stop(); token.ThrowIfCancellationRequested();
        CheckM4Real(selectedMeetingDirectory == second.MeetingDirectory && view.StatusText.Text == otherStatus &&
            visibleRevision is null && TranscriptService.ListRevisions(second.MeetingDirectory).Count == 0 &&
            !qwenRunning && !transcriptBusy && recognitionMeetingDirectory is null,
            "A's real result contaminated B or failed to release UI task ownership.");
        string attemptPath = Directory.EnumerateFiles(Path.Combine(imported.MeetingDirectory, "transcripts", "asr"),
            "attempt.json", SearchOption.AllDirectories).Single();
        using var attemptDocument = JsonDocument.Parse(File.ReadAllBytes(attemptPath));
        var attempt = attemptDocument.RootElement;
        string backend = hardware == LocalTranscriptionHardware.Cpu ? "CPU" : "Vulkan0";
        string alignmentBackend = alignmentHardware == LocalTranscriptionHardware.Cpu ? "CPU" : "Vulkan0";
        CheckM4Real(attempt.GetProperty("status").GetString() == "Succeeded" &&
            attempt.GetProperty("actualBackend").GetString() == backend &&
            attempt.GetProperty("alignmentHardware").GetString() == alignmentBackend &&
            attempt.GetProperty("alignmentEngine").GetString() == effectiveConfiguration.Engine.ToString() &&
            attempt.GetProperty("timelinePolicy").GetString() == "file-v1",
            "Real Qwen/alignment failed or actual hardware/timeline differs from requested values.");
        if (disableCpuAcceleration)
            CheckM4Real(attempt.GetProperty("disableCpuAcceleration").GetBoolean() &&
                attempt.GetProperty("hashes").GetProperty("alignerExecutable").GetString() == CloseFixtureHash(effectiveConfiguration.ExecutablePath),
                "Compatibility selected an optimized or custom executable instead of bundled generic CPU.");
        string revisionId = attempt.GetProperty("revisionId").GetString()!;
        var revision = TranscriptService.Load(imported.MeetingDirectory, revisionId);
        long durationMs = (long)Math.Round(imported.Mix.DurationUs / 1000d);
        CheckM4Real(revision.Alignment is { SampleRate: 16000, TimelinePolicy: "file-v1" } alignment &&
            alignment.SourceMixSha256 == imported.Mix.WaveSha256 && revision.Segments.Count > 0 &&
            revision.Segments.All(s => s.StartMs >= 0 && s.EndMs > s.StartMs && s.EndMs <= durationMs && s.TimestampQuality == "approximate"),
            "The visible candidate lacks real, complete, hash-bound sentence alignment.");
        if (fixture.Synthetic)
            CheckM4Real(revision.Segments.Count >= (fixture.Fixture.Contains("mixed", StringComparison.Ordinal) ? 5 : 3),
                "The fixed continuous multi-sentence sample was reduced to a single coarse audio-window cue.");
        string originalTranscript = Path.Combine(imported.MeetingDirectory, revision.OriginalFile);
        originalHashes[originalTranscript] = CloseFixtureHash(originalTranscript);
        originalHashes[Path.Combine(imported.MeetingDirectory, revision.Alignment!.EvidenceFile)] =
            CloseFixtureHash(Path.Combine(imported.MeetingDirectory, revision.Alignment.EvidenceFile));
        originalHashes[Path.Combine(Path.GetDirectoryName(originalTranscript)!, "revision.json")] =
            CloseFixtureHash(Path.Combine(Path.GetDirectoryName(originalTranscript)!, "revision.json"));
        Stage("real_asr_persisted_for_A", new { revisionId, revision.Alignment, revision.Segments, elapsedMilliseconds = elapsed.ElapsedMilliseconds });

        SelectMeeting(imported.MeetingDirectory); NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 1;
        await Task.WhenAll(savedMixWork, replayLoadWork, transcriptWork).WaitAsync(token);
        if (visibleRevision?.RevisionId != revisionId)
        {
            view.RefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await transcriptWork.WaitAsync(token);
        }
        CheckM4Real(visibleRevision?.RevisionId == revisionId && view.SegmentsBox.Items.Count == revision.Segments.Count,
            "Returning to A did not display the production Qwen revision.");
        int cueIndex = revision.Segments.Count > 1 ? 1 : 0;
        view.SegmentsBox.SelectedIndex = cueIndex;
        long requestedMs = revision.Segments[cueIndex].StartMs!.Value;
        double actualPositionMs;
        player.Volume = 0; player.IsMuted = true;
        try
        {
            view.SegmentsBox.ScrollIntoView(view.SegmentsBox.Items[cueIndex]); await Render();
            var row = view.SegmentsBox.ItemContainerGenerator.ContainerFromIndex(cueIndex) as ListBoxItem
                ?? throw new InvalidDataException("Real Qwen subtitle row was not materialized.");
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = row });
            var wait = Stopwatch.StartNew();
            while (!playbackOpened && wait.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25, token);
            CheckM4Real(playbackOpened && player.IsMuted && player.Volume == 0 && player.Source is not null,
                "Actual Qwen subtitle click did not open muted saved audio.");
            player.Pause(); playbackPaused = true; ResetDisplayPlaybackClock();
            actualPositionMs = player.Position.TotalMilliseconds;
            CheckM4Real(actualPositionMs >= requestedMs - 50 && actualPositionMs < requestedMs + 500,
                $"Native player did not seek to the selected Qwen cue: expected {requestedMs}, got {actualPositionMs}.");
            SaveLayoutImage(evidence, "m4-real-qwen-sentence.png", 1, images);
        }
        finally { ResetTranscriptPlayback(); }
        var subtitleExports = new List<object>();
        foreach (string extension in new[] { ".srt", ".vtt" })
        {
            string destination = Path.Combine(evidence, "real-qwen" + extension);
            StartTranscriptWork(() => ExportTranscriptSubtitlesAsync(destination), "export", imported.MeetingDirectory);
            await transcriptWork.WaitAsync(token);
            CheckM4Real(File.Exists(destination), "Real UI subtitle export did not produce " + extension);
            subtitleExports.Add(new { Path = destination, Sha256 = CloseFixtureHash(destination) });
        }
        var reopened = new MainWindow(enumerateSourcesOnLoad: false) { dataRoot = dataRoot };
        try
        {
            reopened.Show(); reopened.SelectMeeting(imported.MeetingDirectory); reopened.NavigateTo(3);
            reopened.transcriptionView!.WorkspaceTabs.SelectedIndex = 1;
            await Task.WhenAll(reopened.savedMixWork, reopened.replayLoadWork, reopened.transcriptWork).WaitAsync(token);
            CheckM4Real(reopened.visibleRevision?.RevisionId == revisionId && reopened.QwenSelected &&
                reopened.currentMix?.WaveSha256 == imported.Mix.WaveSha256 &&
                reopened.transcriptionView.SegmentsBox.Items.Count == revision.Segments.Count,
                "Reopening failed to restore the exact real Qwen revision and original listening mix.");
            SaveWindowImage(reopened, Path.Combine(evidence, "m4-real-qwen-reopened.png"));
            images.Add(Path.Combine(evidence, "m4-real-qwen-reopened.png"));
        }
        finally { reopened.Close(); }
        CheckM4Real(originalHashes.All(pair => File.Exists(pair.Key) && CloseFixtureHash(pair.Key) == pair.Value),
            "Real UI inference/replay/export/reopen modified an original audio/transcript/alignment file.");
        RequireM4RealProbeIdle();
        Stage("completed", new { requestedMs, actualPositionMs, subtitleExports, images });
        return new
        {
            Status = "PASS", SourceMode = "HASH-ALLOWLISTED PUBLIC/SAPI FILE / REAL QWEN BUTTON / REAL MODELS / MUTED WPF REPLAY",
            fixture.Fixture, FixtureIsSynthetic = fixture.Synthetic, InputSha256 = sourceHash,
            Entry = "ImportAudioFileAsync -> AudioImportService -> RunQwenButton.Click -> RunQwenAsync -> AsrAudioPreparation -> QwenTranscriber",
            ActualBackend = backend, AlignmentBackend = alignmentBackend, AlignmentEngine = effectiveConfiguration.Engine.ToString(),
            RequestedPython = pythonAlignment is not null, DisableCpuAcceleration = disableCpuAcceleration,
            Attempt = attemptPath, AttemptRecord = attempt.Clone(),
            ImportedMeeting = imported.MeetingDirectory, OtherMeeting = second.MeetingDirectory,
            revisionId, Segments = revision.Segments, revision.Alignment, UiElapsedMs = elapsed.ElapsedMilliseconds,
            ClickedCue = cueIndex, RequestedPositionMs = requestedMs, NativePlayerPositionMs = actualPositionMs,
            CrossMeetingOwnershipPreserved = true, Reopen = true, OriginalHashesUnchanged = true,
            SubtitleExports = subtitleExports, CaptureStarted = false, MicrophoneEnabled = false, AudioAudible = false, Images = images,
            Limitations = "Only the stated fixed sample. UI seek checks do not establish natural meeting acoustic accuracy. File picker is bypassed by its production import/save methods."
        };
        async Task Render() => await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
        void Stage(string name, object details) => File.AppendAllText(Path.Combine(evidence, "real-qwen-stages.jsonl"),
            JsonSerializer.Serialize(new { AtUtc = DateTimeOffset.UtcNow, Stage = name, Details = details }) + Environment.NewLine);
        static void SaveWindowImage(MainWindow window, string path)
        {
            window.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
                (int)Math.Ceiling(window.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = new FileStream(path, FileMode.CreateNew); encoder.Save(stream);
        }
    }

    /// <summary>Independent no-model export path. Generated PCM only, no inference or native capture.</summary>
    internal async Task<object> M4WithoutModelsSmokeAsync(string evidence, CancellationToken token)
    {
        RequireM4RealProbeIdle(); evidence = ValidateLocalDataRoot(Path.GetFullPath(evidence)); Directory.CreateDirectory(evidence);
        dataRoot = Path.Combine(evidence, "no-model-data");
        if (Directory.Exists(dataRoot)) throw new IOException("No-model smoke evidence already exists.");
        Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        recordingPreflight = _ => throw new InvalidOperationException("No-model export smoke never starts device capture.");
        string meeting = Path.Combine(RecordingsRoot, "synthetic-no-model-meeting"); WriteCloseFixture(meeting);
        var originalHashes = Directory.EnumerateFiles(meeting, "*", SearchOption.AllDirectories).ToDictionary(path => path, CloseFixtureHash);
        SelectMeeting(meeting, loadSavedMix: false); whisperExecutable = null; whisperModel = null;
        ApplyQwenConfiguration(null); UpdateControls();
        CheckM4Real(!WhisperTranscriber.Inspect(new("", "")).Ready &&
            !new QwenTranscriber().Inspect(CurrentQwenConfiguration()).Ready && StartButton.IsEnabled,
            "Missing models disabled recording readiness or were treated as configured inference.");
        QueueMix(meeting); await backgroundWork.WaitAsync(token);
        CheckM4Real(currentMix is not null && ExportButton.IsEnabled, "Missing models blocked complete WAV rendering/export UI.");
        var exports = new List<object>();
        foreach (var format in new[] { (Index: 1, Extension: ".wav"), (Index: 2, Extension: ".mp3") })
        {
            ExportFormatBox.SelectedIndex = format.Index; MaxBytesBox.Text = "1000000"; MaxSecondsBox.Text = "10"; OverlapBox.Text = "0";
            ExportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await backgroundWork.WaitAsync(token);
            CheckM4Real(lastExportDirectory is not null && Directory.EnumerateFiles(lastExportDirectory, "*" + format.Extension).Any(),
                "No-model production export failed for " + format.Extension + ": " + ExportStatus.Text);
            exports.Add(new { Format = format.Extension, Directory = lastExportDirectory,
                Files = Directory.EnumerateFiles(lastExportDirectory!, "*" + format.Extension).Select(path => new { Path = path, Sha256 = CloseFixtureHash(path), Bytes = new FileInfo(path).Length }).ToArray() });
        }
        CheckM4Real(originalHashes.All(pair => CloseFixtureHash(pair.Key) == pair.Value) && StartButton.IsEnabled &&
            !transcriptBusy && !exportBusy && host is null && !microphoneEnabled,
            "No-model export changed originals, blocked next recording, or activated capture/inference.");
        return new { Status = "PASS", SourceMode = "SYNTHETIC DISK PCM / PRODUCTION WPF WAV AND MP3 EXPORT / NO MODELS",
            Exports = exports, OriginalHashesUnchanged = true, RecordingButtonReady = true,
            CaptureStarted = false, MicrophoneEnabled = false, InferenceStarted = false,
            ActualDeviceRecording = "NOT_RUN; no device recording authorized by this probe" };
    }

    private void RequireM4RealProbeIdle() => CheckM4Real(host is null && !microphoneEnabled && !microphoneRequested &&
        !levelPreviewNativeEnabled && levelPreview is null && !transcriptBusy && !exportBusy && player.Source is null,
        "M4 real/no-model UI probe requires no-capture startup and no active audio/inference task.");
    private static void CheckM4Real(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
