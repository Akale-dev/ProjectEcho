using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NAudio.Wave;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Explicit large-model WPF test. Only the known public JFK input is read; no capture or playback.</summary>
    internal async Task<object> RealWhisperProgressSmokeAsync(string audioPath, string evidence, CancellationToken token)
    {
        const string publicJfkSha256 = "59dfb9a4acb36fe2a2affc14bacbee2920ff435cb13cc314a08c13f66ba7860e";
        var view = transcriptionView ?? throw new InvalidOperationException("转写界面未初始化。");
        var config = CurrentWhisperConfiguration();
        string configPath = File.Exists(WhisperConfigurationPath) ? WhisperConfigurationPath
            : Path.Combine(AppContext.BaseDirectory, PortableWhisperConfiguration.BundledFileName);
        if (!WhisperTranscriber.Inspect(config).Ready || !File.Exists(configPath) || config.Threads != 2)
            throw new InvalidDataException("大模型进度测试需要已保存的可信本地配置及2线程。");
        if (!Path.GetFileName(config.ModelPath).Equals("ggml-large-v3.bin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("本接口要求已有ggml-large-v3.bin；不会替换配置或下载模型。");
        if (host is not null || microphoneEnabled || player.Source is not null || transcriptBusy || exportBusy)
            throw new InvalidOperationException("大模型进度测试要求没有采集、播放或后台文件任务。");
        string source = Path.GetFullPath(audioPath);
        if (!Path.GetFileName(source).Equals("jfk.wav", StringComparison.OrdinalIgnoreCase) ||
            !CloseFixtureHash(source).Equals(publicJfkSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("本接口仅接受已知SHA-256的官方公开JFK样本，不读取私人会议作为识别夹具。");
        string configHash = CloseFixtureHash(configPath);
        string executableHash = CloseFixtureHash(config.ExecutablePath);
        var modelBefore = new FileInfo(config.ModelPath);
        long modelBytes = modelBefore.Length; DateTime modelWriteUtc = modelBefore.LastWriteTimeUtc;
        // A short path avoids the known native SQLite/media MAX_PATH fixture limitation.
        string fixture = ValidateLocalDataRoot(Path.Combine(Path.GetFullPath(evidence), "d")); Directory.CreateDirectory(fixture);
        string repeated = Path.Combine(fixture, "j47.wav");
        if (File.Exists(repeated)) throw new IOException("本次测试夹具已存在，请使用新的证据目录。");
        CreatePublicRepeatedWave(source, repeated);
        string repeatedHash = CloseFixtureHash(repeated);
        File.WriteAllText(Path.Combine(fixture, "PUBLIC_SYNTHETIC_FIXTURE.txt"),
            "Four unmodified copies of the public whisper.cpp JFK speech sample, each followed by 0.75 seconds of digital silence. Total 47 seconds. This is a constructed test fixture, not a natural meeting. No audio playback or capture.");

        dataRoot = fixture; DataRootText.Text = fixture;
        MeetingWhisperSettingsStore.SaveDefaults(WhisperConfigurationPath, config, AppContext.BaseDirectory);
        if (CurrentWhisperConfiguration() != config) throw new InvalidDataException("隔离测试目录改变了已加载模型配置。");
        var imported = await ImportAudioFileAsync(repeated, token);
        var originalHashes = Directory.EnumerateFiles(imported.MeetingDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != ProjectEcho.Core.Diagnostics.MeetingLogStore.FileName &&
                !path.EndsWith(MeetingWhisperSettingsStore.RelativePath.Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => path, CloseFixtureHash, StringComparer.OrdinalIgnoreCase);
        if (!imported.OriginalSha256.Equals(repeatedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("导入原件与构造夹具不同。");
        view.WorkspaceTabs.SelectedIndex = 0; NavigateTo(3); UpdateLayout();
        if (!view.RunWhisperButton.IsEnabled || currentMix?.WavePath != imported.Mix.WavePath)
            throw new InvalidDataException("47秒公开样本导入后识别入口未就绪。");

        var watch = Stopwatch.StartNew();
        var observations = new List<LargeWhisperUiObservation>();
        var displayedPercentages = new List<int>();
        var progressImages = new List<string>();
        var imagedStages = new HashSet<string>();
        bool captureObserved = false, microphoneObserved = false, playbackObserved = false;
        string observationsPath = Path.Combine(fixture, "ui-progress.jsonl");
        using var writer = new StreamWriter(observationsPath, append: false) { AutoFlush = true };
        var bindings = new List<(DependencyPropertyDescriptor Descriptor, object Target, EventHandler Handler)>();
        var sampler = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        sampler.Tick += Sample;
        ObserveProperty(view.ProgressText, TextBlock.TextProperty, "progress_text");
        ObserveProperty(view.ProgressElapsedText, TextBlock.TextProperty, "elapsed_text");
        ObserveProperty(view.RecognitionProgressBar, ProgressBar.IsIndeterminateProperty, "progress_mode");
        ObserveProperty(view.RecognitionActivityBar, VisibilityProperty, "activity_visibility");
        ObserveProperty(view.ProgressPanel, VisibilityProperty, "panel_visibility");
        view.RecognitionProgressBar.ValueChanged += PercentageChanged;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        using var registration = stop.Token.Register(() => Dispatcher.BeginInvoke(new Action(() => transcriptCancellation?.Cancel())));
        Task? ownedWork = null;
        try
        {
            Record("before_button"); sampler.Start();
            var previous = transcriptWork;
            view.RunWhisperButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ownedWork = transcriptWork;
            if (ReferenceEquals(previous, ownedWork)) throw new InvalidDataException("本地识别按钮没有启动新的文字任务。");
            Record("button_invoked");
            await ownedWork.WaitAsync(stop.Token);
            // Drain queued UI progress callbacks before comparing the observable sequence.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Record("work_completed");
            stop.Token.ThrowIfCancellationRequested();
        }
        finally
        {
            try
            {
                if (stop.IsCancellationRequested)
                {
                    transcriptCancellation?.Cancel();
                    if (ownedWork is not null) await ownedWork.WaitAsync(TimeSpan.FromSeconds(30));
                }
            }
            finally
            {
                sampler.Stop(); sampler.Tick -= Sample;
                view.RecognitionProgressBar.ValueChanged -= PercentageChanged;
                foreach (var binding in bindings) binding.Descriptor.RemoveValueChanged(binding.Target, binding.Handler);
                watch.Stop();
                File.WriteAllText(Path.Combine(fixture, "ui-observation-summary.json"), JsonSerializer.Serialize(new
                {
                    ObservedMilliseconds = watch.Elapsed.TotalMilliseconds, Cancelled = stop.IsCancellationRequested,
                    ProgressEventCount = observations.Count, UiPercentages = displayedPercentages,
                    StatusText = view.StatusText.Text, CaptureObserved = captureObserved, MicrophoneObserved = microphoneObserved,
                    PlaybackObserved = playbackObserved, Observations = observations,
                    ClockMeaning = "Monotonic Stopwatch timing of WPF property changes and 100ms UI sampling. These are UI observation times, not backend callback or raw stderr arrival timestamps."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        string attemptPath = Directory.EnumerateFiles(Path.Combine(imported.MeetingDirectory, "transcripts", "asr"),
            "attempt.json", SearchOption.AllDirectories).Single();
        using var attempt = JsonDocument.Parse(File.ReadAllText(attemptPath));
        string rawStderr = Path.Combine(Path.GetDirectoryName(attemptPath)!, "stderr.txt");
        string stderr = File.ReadAllText(rawStderr);
        var enginePercentages = new List<int>(); var engineStages = new List<string>();
        var parser = new WhisperProgressParser(enginePercentages.Add, stage => engineStages.Add(stage.ToString()));
        parser.Append(stderr); parser.Complete();
        var engineTiming = Regex.Matches(stderr, @"(?m)^whisper_print_timings:\s+([a-z_ ]+?)time\s*=\s*([0-9.]+)\s*ms")
            .Select(match => new { Name = match.Groups[1].Value.Trim(), Milliseconds = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) }).ToArray();
        var revision = visibleRevision;
        long durationMs = SelectedTranscriptMeeting().DurationMs;
        bool validOutput = attempt.RootElement.GetProperty("status").GetString() == "Succeeded" && revision is not null &&
            revision.RevisionId == attempt.RootElement.GetProperty("revisionId").GetString() && revision.Segments.Count > 0 &&
            revision.Segments.All(segment => !string.IsNullOrWhiteSpace(segment.Text) && segment.StartMs >= 0 && segment.EndMs > segment.StartMs &&
                segment.EndMs <= durationMs && segment.TimestampQuality == "approximate");
        bool percentMatch = displayedPercentages.SequenceEqual(enginePercentages.Where(value => value > 0));
        bool monotonicPercent = enginePercentages.Zip(enginePercentages.Skip(1)).All(pair => pair.First < pair.Second) && enginePercentages.All(value => value is >= 0 and <= 100);
        bool monotonicClock = observations.Zip(observations.Skip(1)).All(pair => pair.First.ElapsedMilliseconds <= pair.Second.ElapsedMilliseconds);
        bool loadingObserved = observations.Any(value => value.Stage == "LoadingModel" && value.Indeterminate);
        bool elapsedAdvanced = watch.Elapsed.TotalSeconds < 2 || observations.Where(value => value.Busy && !string.IsNullOrWhiteSpace(value.ElapsedText))
            .Select(value => value.ElapsedText).Distinct().Count() > 1;
        var numericSamples = observations.Where(value => value.Reason == "tick" && value.Busy && value.Stage == "Recognizing" && !value.Indeterminate && value.Value > 0).ToArray();
        bool activityCorrect = numericSamples.All(value => value.ActivityVisible);
        var plateauEvidence = numericSamples.GroupBy(value => (value.Stage, value.Value)).Select(group => new
        {
            group.Key.Stage, Percent = group.Key.Value, FirstObservedMs = group.First().ElapsedMilliseconds,
            LastObservedMs = group.Last().ElapsedMilliseconds, DistinctElapsedLabels = group.Select(value => value.ElapsedText).Distinct().Count(),
            AllSamplesShowActivity = group.All(value => value.ActivityVisible)
        }).ToArray();
        bool originalsUnchanged = originalHashes.All(pair => File.Exists(pair.Key) && CloseFixtureHash(pair.Key) == pair.Value) &&
            CloseFixtureHash(source).Equals(publicJfkSha256, StringComparison.OrdinalIgnoreCase) && CloseFixtureHash(repeated) == repeatedHash;
        var modelAfter = new FileInfo(config.ModelPath);
        bool modelMetadataUnchanged = modelAfter.Length == modelBytes && modelAfter.LastWriteTimeUtc == modelWriteUtc;
        bool configUnchanged = CloseFixtureHash(configPath) == configHash && CloseFixtureHash(config.ExecutablePath) == executableHash;
        bool cleanup = !transcriptBusy && !view.CancelButton.IsEnabled && view.ProgressPanel.Visibility == Visibility.Collapsed &&
            view.RecognitionActivityBar.Visibility == Visibility.Collapsed && host is null && !microphoneEnabled && player.Source is null &&
            !captureObserved && !microphoneObserved && !playbackObserved;
        var stageTransitions = observations.Where((value, index) => index == 0 || value.Stage != observations[index - 1].Stage)
            .Select(value => new { value.ElapsedMilliseconds, value.Stage, value.Text }).ToArray();
        bool pass = validOutput && percentMatch && monotonicPercent && monotonicClock && loadingObserved && elapsedAdvanced &&
            activityCorrect && originalsUnchanged && modelMetadataUnchanged && configUnchanged && cleanup;
        var report = new
        {
            Status = pass ? "PASS_LARGE_MODEL_WPF_PROGRESS" : "FAIL", Entry = "47-second constructed public fixture -> RunWhisperButton.Click -> production local ASR -> visible revision",
            Model = config.ModelPath, ModelBytes = modelBytes, ModelSha256FromSingleProductionHash = attempt.RootElement.GetProperty("modelSha256").GetString(),
            ModelFullHashRepeatedByProbe = false, ModelMetadataUnchanged = modelMetadataUnchanged,
            Executable = config.ExecutablePath, ExecutableSha256 = executableHash, config.Language, config.Threads,
            ConfigurationPath = configPath, ConfigurationUnchanged = configUnchanged,
            PublicSource = source, PublicSourceSha256 = publicJfkSha256, ConstructedFixture = repeated, ConstructedFixtureSha256 = repeatedHash,
            ConstructedAudio = "Four full public JFK copies, each followed by 0.75s silence; 47 seconds total; no natural-meeting accuracy claim",
            ImportedMeeting = imported.MeetingDirectory, DurationMs = durationMs, UiElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
            ActualAttempt = attemptPath, Attempt = attempt.RootElement.Clone(), RawStderr = rawStderr,
            EngineReportedPercentages = enginePercentages, UiDisplayedPercentages = displayedPercentages, EngineParsedStages = engineStages,
            EnginePrintedTimings = engineTiming, StageTransitions = stageTransitions, NumericPlateaus = plateauEvidence,
            EngineModelLines = stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("whisper_model_load:", StringComparison.Ordinal) &&
                (line.Contains("type", StringComparison.Ordinal) || line.Contains("model size", StringComparison.Ordinal))).ToArray(),
            Checks = new { ValidOutput = validOutput, PercentMatch = percentMatch, MonotonicPercent = monotonicPercent, MonotonicUiClock = monotonicClock,
                LoadingStageObserved = loadingObserved, ElapsedLabelsAdvanced = elapsedAdvanced, NumericActivityCorrect = activityCorrect,
                OriginalsUnchanged = originalsUnchanged, ConfigurationUnchanged = configUnchanged, ModelMetadataUnchanged = modelMetadataUnchanged, Cleanup = cleanup },
            SegmentCount = revision?.Segments.Count, Segments = revision?.Segments.Select(segment => new { segment.StartMs, segment.EndMs, segment.TimestampQuality }),
            UiObservationPath = observationsPath, Images = progressImages, NoCapture = !captureObserved, MicrophoneCollected = microphoneObserved, PlaybackPerformed = playbackObserved, Uploaded = false,
            Limitations = "UI observations have 100ms sampling in addition to property events; they do not timestamp raw stderr arrival. Engine percentages describe audio windows, not wall-clock completion. Model hash is reused from the production attempt, which held the model read lock; only size/write-time are rechecked afterwards. No exact sentence count or exact percentage such as 63 is required."
        };
        File.WriteAllText(Path.Combine(fixture, "progress-result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        if (!pass) throw new InvalidDataException("真实large模型WPF进度检查未全通过，详见 " + Path.Combine(fixture, "progress-result.json"));
        return report;

        void ObserveProperty(DependencyObject target, DependencyProperty property, string reason)
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(property, target.GetType())
                ?? throw new InvalidOperationException("WPF property descriptor unavailable.");
            EventHandler handler = (_, _) => Record(reason); descriptor.AddValueChanged(target, handler); bindings.Add((descriptor, target, handler));
        }
        void Sample(object? sender, EventArgs args) => Record("tick");
        void PercentageChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
        {
            if (!view.RecognitionProgressBar.IsIndeterminate && args.NewValue > 0) displayedPercentages.Add((int)args.NewValue);
            Record("percent_value");
        }
        void Record(string reason)
        {
            if (observations.Count >= 20000) throw new InvalidDataException("UI progress observation limit exceeded.");
            captureObserved |= host is not null; microphoneObserved |= microphoneEnabled; playbackObserved |= player.Source is not null;
            string text = view.ProgressText.Text;
            string stage = text == UiText.T("正在准备识别音频…") ? "Preparing"
                : text == UiText.T("正在校验本地文件…") ? "Validating"
                : text == UiText.T("正在加载模型并准备识别…") ? "LoadingModel"
                : text == UiText.T("识别运算已结束，正在校验并保存文字…") ? "Saving" : transcriptBusy ? "Recognizing" : "Idle";
            var item = new LargeWhisperUiObservation(observations.Count, DateTimeOffset.UtcNow, watch.Elapsed.TotalMilliseconds, reason,
                stage, text, view.ProgressElapsedText.Text, view.RecognitionProgressBar.IsIndeterminate, view.RecognitionProgressBar.Value,
                view.RecognitionActivityBar.Visibility == Visibility.Visible, view.ProgressPanel.Visibility == Visibility.Visible, transcriptBusy);
            observations.Add(item); writer.WriteLine(JsonSerializer.Serialize(item));
            if (reason == "tick" && transcriptBusy && view.ProgressPanel.IsVisible &&
                (stage == "LoadingModel" || stage == "Recognizing" && !item.Indeterminate && item.Value > 0) &&
                imagedStages.Add(stage))
                SaveLayoutImage(evidence, "progress-" + stage + ".png", 1, progressImages);
        }
    }

    private static void CreatePublicRepeatedWave(string source, string destination)
    {
        using var input = new WaveFileReader(source);
        if (input.WaveFormat.SampleRate != 16000 || input.WaveFormat.Channels != 1 || input.WaveFormat.BitsPerSample != 16 ||
            input.WaveFormat.Encoding != WaveFormatEncoding.Pcm || input.Length != 352000)
            throw new InvalidDataException("公开JFK WAV格式/长度不同，未创建夹具。");
        using var output = new WaveFileWriter(destination, new WaveFormat(16000, 16, 1));
        byte[] buffer = new byte[16384], silence = new byte[24000];
        for (int copy = 0; copy < 4; copy++)
        {
            input.Position = 0; int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);
            output.Write(silence, 0, silence.Length);
        }
    }

    private sealed record LargeWhisperUiObservation(int Sequence, DateTimeOffset AtUtc, double ElapsedMilliseconds, string Reason,
        string Stage, string Text, string ElapsedText, bool Indeterminate, double Value, bool ActivityVisible, bool PanelVisible, bool Busy);
}
