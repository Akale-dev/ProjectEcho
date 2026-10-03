using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Opt-in Windows UI smoke using only the configured local model and an existing public speech file.</summary>
    internal async Task<object> RealWhisperSmokeAsync(string audioPath, string evidence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var view = transcriptionView ?? throw new InvalidOperationException("转写界面未初始化。");
        // Exercise the literal prompt control without changing the user's saved settings.
        view.InitialPromptBox.Text = "John F. Kennedy, United States, country";
        string configurationPath = File.Exists(WhisperConfigurationPath) ? WhisperConfigurationPath
            : Path.Combine(AppContext.BaseDirectory, PortableWhisperConfiguration.BundledFileName);
        var configuration = CurrentWhisperConfiguration();
        var readiness = WhisperTranscriber.Inspect(configuration);
        if (!readiness.Ready) throw new InvalidDataException("真实 UI 识别需要先加载已配置的 CLI/model：" + readiness.Message);
        if (host is not null || microphoneEnabled || player.Source is not null || transcriptBusy || exportBusy)
            throw new InvalidOperationException("真实 UI 识别测试要求没有采集、播放或正在运行的文件任务。");
        if (!File.Exists(configurationPath)) throw new InvalidDataException("未找到启动时应加载的持久配置文件。");
        string configurationHash = CloseFixtureHash(configurationPath);
        string source = Path.GetFullPath(audioPath), sourceHash = CloseFixtureHash(source);
        string fixture = ValidateLocalDataRoot(Path.Combine(Path.GetFullPath(evidence), "real-whisper-ui"));
        Directory.CreateDirectory(fixture);
        string stages = Path.Combine(fixture, "stages.jsonl");
        Stage("loaded_configuration_ready", new { configurationPath, configuration.ExecutablePath, configuration.ModelPath,
            configuration.Language, configuration.Threads, configuration.ExpectedModelSha256 });

        // Keep the loaded controls and CLI/model selection while isolating all newly imported recordings.
        dataRoot = fixture; DataRootText.Text = dataRoot;
        MeetingWhisperSettingsStore.SaveDefaults(WhisperConfigurationPath, configuration, AppContext.BaseDirectory);
        if (CurrentWhisperConfiguration() != configuration) throw new InvalidDataException("隔离测试数据改变了已加载的识别配置。");
        var imported = await ImportAudioFileAsync(source, token);
        view.InitialPromptBox.Text = configuration.InitialPrompt ?? ""; FlushMeetingWhisperPrompt();
        Stage("production_audio_import_completed", new { imported.MeetingDirectory, imported.Mix.FrameCount, imported.Mix.SampleRate });
        var originalHashes = Directory.EnumerateFiles(imported.MeetingDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != ProjectEcho.Core.Diagnostics.MeetingLogStore.FileName &&
                !path.EndsWith(MeetingWhisperSettingsStore.RelativePath.Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => path, CloseFixtureHash, StringComparer.OrdinalIgnoreCase);
        if (sourceHash != imported.OriginalSha256 || CloseFixtureHash(imported.OriginalPath) != sourceHash)
            throw new InvalidDataException("UI 导入没有保存与源文件完全相同的原件副本。");
        if (!view.RunWhisperButton.IsEnabled || currentMix?.WavePath != imported.Mix.WavePath)
            throw new InvalidDataException("UI 导入后本地识别按钮或当前完整混音未就绪。");

        using var registration = token.Register(() => Dispatcher.BeginInvoke(new Action(() => transcriptCancellation?.Cancel())));
        var elapsed = Stopwatch.StartNew();
        var displayedPercentages = new List<int>();
        void ProgressChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
        {
            if (!view.RecognitionProgressBar.IsIndeterminate) displayedPercentages.Add((int)args.NewValue);
        }
        view.RecognitionProgressBar.ValueChanged += ProgressChanged;
        var previousWork = transcriptWork;
        view.RunWhisperButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (ReferenceEquals(previousWork, transcriptWork)) throw new InvalidDataException("本地识别按钮没有进入文字任务。");
        if (recognitionMeetingDirectory != imported.MeetingDirectory ||
            !RecoveryBox.Items.Cast<RecordingDirectory>().Any(row => row.Path == imported.MeetingDirectory && row.IsRecognizing))
            throw new InvalidDataException("实际识别任务没有标记正确的侧边栏会议。");
        Stage("run_whisper_button_invoked", new { Busy = transcriptBusy, CancelEnabled = view.CancelButton.IsEnabled });
        try { await transcriptWork.WaitAsync(token); }
        finally
        {
            view.RecognitionProgressBar.ValueChanged -= ProgressChanged;
            if (token.IsCancellationRequested)
            {
                transcriptCancellation?.Cancel();
                await transcriptWork.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        elapsed.Stop(); token.ThrowIfCancellationRequested();
        string recognitionStatusText = view.StatusText.Text;
        var asrRevision = visibleRevision ?? throw new InvalidDataException("UI 真实识别没有显示转写版本：" + recognitionStatusText);
        long durationMs = SelectedTranscriptMeeting().DurationMs;
        if (!recognitionStatusText.Contains("Succeeded", StringComparison.Ordinal) ||
            !asrRevision.Provider.StartsWith("whisper.cpp", StringComparison.Ordinal) || asrRevision.IsSynthetic ||
            asrRevision.Segments.Count != 1 || view.SegmentsBox.Items.Count != 1 ||
            asrRevision.Segments.Any(segment => segment.StartMs is null || segment.EndMs is null ||
                segment.StartMs < 0 || segment.EndMs <= segment.StartMs || segment.EndMs > durationMs ||
                segment.TimestampQuality != "approximate" || segment.SpeakerLabel is not null))
            throw new InvalidDataException("UI 真实 JFK 识别结果/来源/完整一句 approximate 时间范围不符：" + recognitionStatusText);
        string recognizedText = Regex.Replace(string.Join(" ", asrRevision.Segments.Select(s => s.Text)).ToLowerInvariant(), "[^a-z]+", " ").Trim();
        if (!recognizedText.Contains("ask not what your country can do for you", StringComparison.Ordinal) ||
            !recognizedText.Contains("what you can do for your country", StringComparison.Ordinal))
            throw new InvalidDataException("UI 真实识别未包含 JFK 样本的基本短语。");
        if (!view.ExportSubtitlesButton.IsEnabled || !view.RunWhisperButton.IsEnabled || view.CancelButton.IsEnabled || transcriptBusy ||
            recognitionMeetingDirectory is not null || RecoveryBox.Items.Cast<RecordingDirectory>().Any(row => row.IsRecognizing))
            throw new InvalidDataException("UI 真实识别结束后按钮状态没有恢复。");
        string attemptPath = Directory.EnumerateFiles(Path.Combine(imported.MeetingDirectory, "transcripts", "asr"),
            "attempt.json", SearchOption.AllDirectories).Single();
        var enginePercentages = new List<int>(); var progressParser = new WhisperProgressParser(enginePercentages.Add);
        progressParser.Append(File.ReadAllText(Path.Combine(Path.GetDirectoryName(attemptPath)!, "stderr.txt"))); progressParser.Complete();
        if (!displayedPercentages.SequenceEqual(enginePercentages.Where(value => value != 0)) || view.ProgressPanel.Visibility != Visibility.Collapsed)
            throw new InvalidDataException("UI 百分比与本次引擎 stderr 不符，或完成后未清理进度条。");
        using var attempt = JsonDocument.Parse(File.ReadAllText(attemptPath));
        if (attempt.RootElement.GetProperty("status").GetString() != "Succeeded" ||
            attempt.RootElement.GetProperty("initialPrompt").GetString() != configuration.InitialPrompt ||
            attempt.RootElement.GetProperty("revisionId").GetString() != asrRevision.RevisionId ||
            !attempt.RootElement.GetProperty("fullTokenJson").GetBoolean() ||
            attempt.RootElement.GetProperty("maxSegmentLength").GetInt32() != 0 ||
            attempt.RootElement.GetProperty("subtitleSegmentation").GetString() != "sentence-punctuation-token-boundaries-v1")
            throw new InvalidDataException("UI 可见识别版本与实际 whisper.cpp 尝试不一致。");
        string asrOriginal = Path.Combine(imported.MeetingDirectory, asrRevision.OriginalFile);
        originalHashes.Add(asrOriginal, CloseFixtureHash(asrOriginal));
        string asrRevisionPath = Path.Combine(Path.GetDirectoryName(asrOriginal)!, "revision.json");
        originalHashes.Add(asrRevisionPath, CloseFixtureHash(asrRevisionPath));
        Stage("real_asr_displayed", new { asrRevision.RevisionId, elapsedMilliseconds = elapsed.ElapsedMilliseconds,
            segments = asrRevision.Segments, recognitionStatusText });

        var roundTrips = new List<object>();
        foreach (string extension in new[] { ".srt", ".vtt" })
        {
            token.ThrowIfCancellationRequested();
            DisplayRevision(imported.MeetingDirectory, asrRevision);
            string path = Path.Combine(fixture, "jfk-ui-real" + extension);
            // Use the same save method called after the user's SaveFileDialog confirmation.
            // The platform file picker itself is deliberately outside this automated smoke.
            StartTranscriptWork(() => ExportTranscriptSubtitlesAsync(path));
            await transcriptWork.WaitAsync(token);
            if (!File.Exists(path)) throw new InvalidDataException("UI 字幕保存没有生成文件：" + view.StatusText.Text);
            string exportedHash = CloseFixtureHash(path);
            importFile = path; view.ImportFileText.Text = path;
            view.ProviderBox.Text = "真实 UI 识别字幕导出往返校验";
            UpdateControls();
            if (!view.ImportButton.IsEnabled) throw new InvalidDataException("字幕保存后 UI 导入按钮不可用。");
            view.ImportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await transcriptWork.WaitAsync(token);
            var reimported = visibleRevision ?? throw new InvalidDataException("UI 字幕再导入没有显示版本。");
            var expected = asrRevision.Segments.OrderBy(s => s.StartMs).Select(s => (s.StartMs, s.EndMs, s.Text));
            if (reimported.RevisionId == asrRevision.RevisionId ||
                !expected.SequenceEqual(reimported.Segments.Select(s => (s.StartMs, s.EndMs, s.Text))))
                throw new InvalidDataException("UI 字幕导出再导入改变文字或毫秒时间：" + extension);
            if (CloseFixtureHash(path) != exportedHash) throw new InvalidDataException("字幕再导入修改了用户的导出文件。");
            roundTrips.Add(new { format = extension[1..], path, sha256 = exportedHash,
                reimportedRevisionId = reimported.RevisionId, allTextAndTimestampsIdentical = true });
        }
        DisplayRevision(imported.MeetingDirectory, asrRevision);
        if (host is not null || microphoneEnabled || player.Source is not null || pendingTranscriptSeek is not null || transcriptOwnsPlayer)
            throw new InvalidDataException("纯文件识别 UI 测试触发了采集或播放。");
        if (originalHashes.Any(pair => !File.Exists(pair.Key) || CloseFixtureHash(pair.Key) != pair.Value) ||
            CloseFixtureHash(source) != sourceHash || CloseFixtureHash(configurationPath) != configurationHash)
            throw new InvalidDataException("UI 识别/导出/导入改变了原音频、原稿或持久配置。");
        Stage("subtitle_roundtrips_and_original_hashes_verified", new { roundTrips });
        return new
        {
            Status = "PASS", Entry = "ImportAudioFileAsync -> RunWhisperButton.Click -> production local ASR -> visible revision",
            ConfigurationLoadedAtStartup = configurationPath, ConfigurationSha256 = configurationHash,
            ConfiguredExecutable = configuration.ExecutablePath, ConfiguredModel = configuration.ModelPath,
            configuration.Language, configuration.Threads, configuration.ExpectedModelSha256,
            Input = source, InputSha256 = sourceHash, ImportedMeeting = imported.MeetingDirectory,
            SourceMode = "Public file import; no capture", durationMs,
            UiRecognitionElapsedMilliseconds = elapsed.ElapsedMilliseconds,
            RealTimeFactor = (double)elapsed.ElapsedMilliseconds / durationMs,
            ActualAttempt = attemptPath, ActualAttemptRecord = attempt.RootElement.Clone(),
            EngineReportedPercentages = enginePercentages, ActualUiPercentages = displayedPercentages,
            ProgressMatchesEngine = true, NoSyntheticPercentages = true,
            asrRevision.RevisionId, asrRevision.Provider, Segments = asrRevision.Segments,
            SubtitleRoundTrips = roundTrips, ExportEntry = "Production save method after file-picker confirmation; picker not automated",
            OriginalHashesUnchanged = true, ConfigurationUnchanged = true, NoCapture = true, MicrophoneCollected = false,
            PlaybackPerformed = false, RecognitionStatusText = recognitionStatusText,
            Limitations = "One public 11-second English sample; subtitle times remain approximate. No general accuracy, mixed-language, file-picker or playback claim."
        };

        void Stage(string name, object details) => File.AppendAllText(stages,
            JsonSerializer.Serialize(new { At = DateTimeOffset.UtcNow, Stage = name, Details = details }) + Environment.NewLine);
    }
}
