using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using ProjectEcho.Core;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private string? qwenMeeting, qwenDataRoot, qwenExecutable, qwenModel, qwenAlignerExecutable, qwenAlignerModel;
    private string? qwenPythonExecutable, qwenPythonModelDirectory, qwenPythonBridgePath;
    private bool loadingQwen, qwenSettingsReady, qwenInitialized, qwenRunning;
    private DispatcherTimer? qwenSaveTimer;
    private bool QwenSelected => (transcriptionView?.AsrBackendBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == QwenTranscriber.BackendId;
    private bool QwenOfficialPythonSelected => (transcriptionView?.QwenAlignmentHardwareBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "OfficialPython";
    private bool QwenPythonControlsActive => QwenOfficialPythonSelected &&
        transcriptionView?.QwenDisableCpuAccelerationBox.IsChecked != true && QwenAlignmentRuntime.SupportsOptimizedCpu;
    private static string BundledQwenPythonBridge => Path.Combine(AppContext.BaseDirectory, "Tools", "qwen-python", "qwen_aligner_bridge.py");

    private void InitializeQwenTranscription()
    {
        var view = transcriptionView!;
        qwenSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        qwenSaveTimer.Tick += (_, _) => SaveQwenMeeting();
        view.AsrBackendBox.SelectionChanged += (_, _) =>
        {
            if (loadingQwen || !qwenInitialized) return;
            if (QwenSelected && qwenModel is null)
            {
                try { ApplyQwenConfiguration(QwenSettingsStore.LoadDefaults(Path.Combine(dataRoot, "settings")).Configuration); }
                catch (Exception ex) { view.StatusText.Text = ex.Message; }
            }
            SaveQwenMeeting(); UpdateControls();
        };
        void Pick(Button button, bool program, Action<string> assign)
        {
            button.Click += (_, _) =>
            {
                var picker = new OpenFileDialog { Title = UiText.T(program ? "选择可信的本地原生程序" : "选择已下载并核对许可的 GGUF 模型"),
                    Filter = program ? "Windows 程序|*.exe" : "GGUF 模型|*.gguf", CheckFileExists = true };
                if (picker.ShowDialog(this) != true) return;
                assign(picker.FileName); SaveQwenMeeting(); UpdateControls();
            };
        }
        Pick(view.QwenExeButton, true, path => qwenExecutable = path);
        Pick(view.QwenModelButton, false, path => qwenModel = path);
        view.QwenAlignerExeButton.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Title = UiText.T(QwenPythonControlsActive ? "选择官方 CPU 定位的 Python 程序" : "选择可信的本地原生程序"),
                Filter = QwenPythonControlsActive ? "Python 程序|python.exe|Windows 程序|*.exe" : "Windows 程序|*.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            if (QwenPythonControlsActive) qwenPythonExecutable = picker.FileName; else qwenAlignerExecutable = picker.FileName;
            SaveQwenMeeting(); UpdateControls();
        };
        view.QwenAlignerModelButton.Click += (_, _) =>
        {
            if (QwenPythonControlsActive)
            {
                var picker = new OpenFolderDialog { Title = UiText.T("选择官方 Qwen3-ForcedAligner 模型文件夹") };
                if (picker.ShowDialog(this) != true) return;
                qwenPythonModelDirectory = picker.FolderName;
            }
            else
            {
                var picker = new OpenFileDialog { Title = UiText.T("选择已下载并核对许可的 GGUF 模型"), Filter = "GGUF 模型|*.gguf", CheckFileExists = true };
                if (picker.ShowDialog(this) != true) return;
                qwenAlignerModel = picker.FileName;
            }
            SaveQwenMeeting(); UpdateControls();
        };
        void SaveLater()
        {
            if (loadingQwen || !qwenInitialized || !qwenSettingsReady) return;
            qwenSaveTimer.Stop(); qwenSaveTimer.Start();
        }
        view.QwenPromptBox.TextChanged += (_, _) => SaveLater();
        view.QwenThreadsBox.LostKeyboardFocus += (_, _) => SaveQwenMeeting();
        view.QwenDeviceBox.LostKeyboardFocus += (_, _) => SaveQwenMeeting();
        view.QwenHardwareBox.SelectionChanged += (_, _) =>
        {
            if (loadingQwen || !qwenInitialized) return;
            if (Enum.TryParse<LocalTranscriptionHardware>((view.QwenHardwareBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var hardware))
                qwenExecutable = ResolveQwenExecutable(qwenExecutable ?? "", hardware);
            SaveLater(); UpdateControls();
        };
        view.QwenAlignmentHardwareBox.SelectionChanged += (_, _) =>
        {
            if (loadingQwen || !qwenInitialized) return;
            if (!QwenOfficialPythonSelected && Enum.TryParse<LocalTranscriptionHardware>((view.QwenAlignmentHardwareBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var hardware))
                qwenAlignerExecutable = QwenAlignmentRuntime.ResolveBundledExecutable(qwenAlignerExecutable ?? "", hardware);
            if (!QwenAlignmentRuntime.SupportsOptimizedCpu &&
                (view.QwenAlignmentHardwareBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "Vulkan")
                view.QwenDisableCpuAccelerationBox.IsChecked = true;
            SaveLater(); UpdateControls();
        };
        view.QwenDisableCpuAccelerationBox.Checked += (_, _) =>
        { if (loadingQwen || !qwenInitialized) return; SaveLater(); UpdateControls(); };
        view.QwenDisableCpuAccelerationBox.Unchecked += (_, _) =>
        {
            if (loadingQwen || !qwenInitialized) return;
            if (!loadingQwen && !QwenAlignmentRuntime.SupportsOptimizedCpu &&
                (view.QwenAlignmentHardwareBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "Vulkan")
                view.QwenDisableCpuAccelerationBox.IsChecked = true;
            SaveLater(); UpdateControls();
        };
        view.QwenLanguageBox.SelectionChanged += (_, _) => SaveLater();
        view.QwenLicenseBox.Checked += (_, _) => SaveLater();
        view.QwenLicenseBox.Unchecked += (_, _) => SaveLater();
        view.ResetQwenConfigurationButton.Click += (_, _) => ResetQwenConfigurationWithBackup();
        view.RunQwenButton.Click += (_, _) =>
        {
            try
            {
                if (!qwenSettingsReady) throw new InvalidDataException(UiText.T("本场 Qwen 配置无法读取，请先处理文件错误。"));
                _ = SelectedTranscriptMeeting();
                if (currentMix is null) throw new InvalidOperationException(UiText.T("请先生成完整混音。"));
                if (!SessionRules.CanPlayback(state)) throw new InvalidOperationException(UiText.T("请在录音保存后运行本地识别。"));
                var readiness = new QwenTranscriber().Inspect(CurrentQwenConfiguration());
                if (!readiness.Ready) throw new InvalidOperationException(UiText.T(readiness.Message));
                SaveQwenMeeting(); StartTranscriptWork(RunQwenAsync, "asr");
            }
            catch (Exception ex) { view.StatusText.Text = ex.Message; }
        };
        view.SaveQwenDefaultsButton.Click += (_, _) =>
        {
            try
            {
                var configuration = CurrentQwenConfiguration();
                var readiness = new QwenTranscriber().Inspect(configuration);
                if (!readiness.Ready) throw new InvalidOperationException(UiText.T(readiness.Message));
                QwenSettingsStore.SaveDefaults(Path.Combine(dataRoot, "settings"), new(1, QwenTranscriber.BackendId, configuration));
                view.StatusText.Text = UiText.T("Qwen 默认配置已保存，仅应用于新会议；已有 Whisper 配置和本场提示词保留。");
            }
            catch (Exception ex) { view.StatusText.Text = ex.Message; }
        };
        view.QwenHelpButton.Click += (_, _) => MessageBox.Show(this, UiText.T(
            "Qwen 模型单独下载和保存，不包含在软件包内。\n\n1. 选择 transcribe.cpp 的 ProjectEcho CPU/Vulkan 程序及 Qwen3-ASR GGUF 模型。0.6B 已测；1.7B 需另验。\n2. 句子定位可选原生 CPU、官方 Python CPU 或原生 Vulkan，独立于识别硬件。\n3. 原生定位选择程序和 ForcedAligner GGUF；官方 CPU 选择 python.exe 和官方 ForcedAligner 模型文件夹。官方环境需要另行配置，不会自动下载。\n4. 兼容旧 CPU 选项关闭 AVX2/FMA 等加速，实际使用随包通用 CPU 程序与原生 GGUF。原生与官方配置分别保留，取消勾选后恢复所选方案。\n\n未满足优化指令要求的电脑自动使用兼容 CPU 路径；Vulkan 仍须兼容驱动。没有模型或 Python 仍可录音、回听和导出；程序不自动上传。\n具体速度、许可、版本和已测范围见随包说明。"), "Qwen3-ASR", MessageBoxButton.OK, MessageBoxImage.Information);
        Closing += (_, _) => SaveQwenMeeting();
        Closed += (_, _) => qwenSaveTimer.Stop();
        qwenInitialized = true;
        SwitchQwenMeeting();
    }

    private LocalTranscriptionConfiguration CurrentQwenConfiguration()
    {
        var view = transcriptionView!;
        if (!int.TryParse(view.QwenThreadsBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int threads) || threads is < 1 or > 64 ||
            !int.TryParse(view.QwenDeviceBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int device) || device is < 0 or > 63)
            throw new InvalidDataException(UiText.T("CPU 线程须为 1–64，GPU 编号须为 0–63。"));
        if (!Enum.TryParse<LocalTranscriptionHardware>((view.QwenHardwareBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var hardware))
            throw new InvalidDataException(UiText.T("请选择识别硬件。"));
        var alignmentEngine = QwenOfficialPythonSelected ? QwenAlignmentEngine.OfficialPython : QwenAlignmentEngine.Native;
        var alignmentHardware = LocalTranscriptionHardware.Cpu;
        if (!QwenOfficialPythonSelected && (!Enum.TryParse((view.QwenAlignmentHardwareBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out alignmentHardware) ||
            alignmentHardware is not (LocalTranscriptionHardware.Cpu or LocalTranscriptionHardware.Vulkan)))
            throw new InvalidDataException(UiText.T("请选择句子定位方式。"));
        string executable = ResolveQwenExecutable(qwenExecutable ?? "", hardware);
        string alignmentExecutable = QwenOfficialPythonSelected ? qwenAlignerExecutable ?? "" :
            QwenAlignmentRuntime.ResolveBundledExecutable(qwenAlignerExecutable ?? "", alignmentHardware);
        QwenPythonAlignmentConfiguration? python = QwenOfficialPythonSelected || qwenPythonExecutable is not null || qwenPythonModelDirectory is not null
            ? new(qwenPythonExecutable ?? "", qwenPythonModelDirectory ?? "", qwenPythonBridgePath ?? BundledQwenPythonBridge) : null;
        return new(QwenTranscriber.BackendId, executable, qwenModel ?? "",
            (view.QwenLanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto", threads,
            ModelName: string.IsNullOrEmpty(qwenModel) ? null : Path.GetFileNameWithoutExtension(qwenModel),
            ModelLicenseAcknowledged: view.QwenLicenseBox.IsChecked == true, Hardware: hardware, GpuDeviceIndex: device,
            InitialPrompt: string.IsNullOrWhiteSpace(view.QwenPromptBox.Text) ? null : view.QwenPromptBox.Text,
            QwenAlignment: new(alignmentExecutable, qwenAlignerModel ?? "", Hardware: alignmentHardware, GpuDeviceIndex: device,
                Engine: alignmentEngine, Python: python, DisableCpuAcceleration: view.QwenDisableCpuAccelerationBox.IsChecked == true));
    }

    private static string ResolveQwenExecutable(string path, LocalTranscriptionHardware hardware)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        string directory = Path.GetDirectoryName(path)!;
        if (Path.GetFileName(path).Equals("transcribe-cli.exe", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(directory) is "cpu" or "vulkan" or "qwen-cpu" or "qwen-vulkan")
        {
            string prefix = Path.GetFileName(directory).StartsWith("qwen-", StringComparison.Ordinal) ? "qwen-" : "";
            string candidate = Path.Combine(Path.GetDirectoryName(directory)!, prefix + (hardware == LocalTranscriptionHardware.Cpu ? "cpu" : "vulkan"), "transcribe-cli.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return path;
    }

    private void SaveQwenMeeting()
    {
        qwenSaveTimer?.Stop();
        if (!qwenInitialized || loadingQwen || !qwenSettingsReady || qwenMeeting is null) return;
        try
        {
            QwenSettingsStore.SaveMeeting(qwenMeeting, new(1, QwenSelected ? QwenTranscriber.BackendId : WhisperCppLocalTranscriber.BackendId,
                CurrentQwenConfiguration()));
        }
        catch (Exception ex) { transcriptionView!.StatusText.Text = UiText.T("本场 Qwen 配置未保存：") + ex.Message; }
    }

    private void SwitchQwenMeeting()
    {
        if (!qwenInitialized || string.Equals(qwenMeeting, selectedMeetingDirectory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(qwenDataRoot, dataRoot, StringComparison.OrdinalIgnoreCase)) return;
        SaveQwenMeeting(); qwenMeeting = selectedMeetingDirectory; qwenDataRoot = dataRoot;
        loadingQwen = true;
        try
        {
            var settings = qwenMeeting is null ? QwenSettingsStore.LoadDefaults(Path.Combine(dataRoot, "settings")) : QwenSettingsStore.LoadMeeting(qwenMeeting);
            transcriptionView!.AsrBackendBox.SelectedIndex = settings.BackendId == QwenTranscriber.BackendId ? 1 : 0;
            ApplyQwenConfiguration(settings.Configuration); qwenSettingsReady = true;
        }
        catch (Exception ex)
        {
            qwenSettingsReady = false; ApplyQwenConfiguration(null);
            transcriptionView!.StatusText.Text = UiText.T("本场 Qwen 配置无法读取：") + ex.Message;
        }
        finally { loadingQwen = false; }
    }

    private void ApplyQwenConfiguration(LocalTranscriptionConfiguration? configuration)
    {
        bool previous = loadingQwen; loadingQwen = true;
        try
        {
            var view = transcriptionView!;
            string cpu = Path.Combine(AppContext.BaseDirectory, "Tools", "qwen-cpu", "transcribe-cli.exe");
            string aligner = Path.Combine(AppContext.BaseDirectory, "Tools", "qwen-aligner", "audiocpp_cli.exe");
            var alignmentHardware = configuration?.QwenAlignment?.Hardware ?? LocalTranscriptionHardware.Cpu;
            var alignmentEngine = configuration?.QwenAlignment?.Engine ?? QwenAlignmentEngine.Native;
            aligner = QwenAlignmentRuntime.ResolveBundledExecutable(aligner, alignmentHardware);
            qwenExecutable = configuration?.ExecutablePath ?? (File.Exists(cpu) ? cpu : null);
            qwenModel = configuration?.ModelPath;
            qwenAlignerExecutable = configuration?.QwenAlignment?.ExecutablePath is { } configuredAligner
                ? alignmentEngine == QwenAlignmentEngine.OfficialPython ? configuredAligner : QwenAlignmentRuntime.ResolveBundledExecutable(configuredAligner, alignmentHardware)
                : File.Exists(aligner) ? aligner : null;
            qwenAlignerModel = configuration?.QwenAlignment?.ModelPath;
            qwenPythonExecutable = configuration?.QwenAlignment?.Python?.ExecutablePath;
            qwenPythonModelDirectory = configuration?.QwenAlignment?.Python?.ModelDirectory;
            qwenPythonBridgePath = configuration?.QwenAlignment?.Python?.BridgePath ?? BundledQwenPythonBridge;
            view.QwenHardwareBox.SelectedItem = view.QwenHardwareBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == (configuration?.Hardware ?? LocalTranscriptionHardware.Cpu).ToString());
            string alignmentTag = alignmentEngine == QwenAlignmentEngine.OfficialPython ? "OfficialPython" : alignmentHardware.ToString();
            view.QwenAlignmentHardwareBox.SelectedItem = view.QwenAlignmentHardwareBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == alignmentTag);
            view.QwenDisableCpuAccelerationBox.IsChecked = configuration?.QwenAlignment?.DisableCpuAcceleration == true ||
                alignmentHardware == LocalTranscriptionHardware.Cpu && !QwenAlignmentRuntime.SupportsOptimizedCpu;
            view.QwenLanguageBox.SelectedIndex = configuration?.Language == "zh" ? 1 : configuration?.Language == "en" ? 2 : 0;
            view.QwenThreadsBox.Text = (configuration?.Threads ?? 4).ToString(CultureInfo.InvariantCulture);
            view.QwenDeviceBox.Text = (configuration?.GpuDeviceIndex ?? 0).ToString(CultureInfo.InvariantCulture);
            view.QwenPromptBox.Text = configuration?.InitialPrompt ?? "";
            view.QwenLicenseBox.IsChecked = configuration?.ModelLicenseAcknowledged == true;
        }
        finally { loadingQwen = previous; }
    }

    private void UpdateQwenControls(bool open, bool selected)
    {
        if (!qwenInitialized) return;
        SwitchQwenMeeting(); var view = transcriptionView!;
        view.WhisperConfigurationPanel.Visibility = QwenSelected ? Visibility.Collapsed : Visibility.Visible;
        view.QwenConfigurationPanel.Visibility = QwenSelected ? Visibility.Visible : Visibility.Collapsed;
        view.AsrBackendBox.IsEnabled = open && !transcriptBusy && qwenSettingsReady;
        view.ResetQwenConfigurationButton.Visibility = qwenSettingsReady ? Visibility.Collapsed : Visibility.Visible;
        view.ResetQwenConfigurationButton.IsEnabled = open && !transcriptBusy;
        view.ResetQwenConfigurationButton.Content = UiText.T("重新配置引擎（保留错误设置备份）");
        view.QwenConfigurationPanel.IsEnabled = open && !transcriptBusy && qwenSettingsReady;
        view.RunQwenButton.IsEnabled = open && !transcriptBusy;
        view.QwenPromptBox.IsEnabled = open && !transcriptBusy && selected;
        view.QwenExeText.Text = string.IsNullOrEmpty(qwenExecutable) ? UiText.T("未配置程序") : qwenExecutable;
        view.QwenModelText.Text = string.IsNullOrEmpty(qwenModel) ? UiText.T("未配置模型") : qwenModel;
        QwenAlignmentConfiguration? effectiveAlignment = null;
        try { effectiveAlignment = QwenAlignmentRuntime.EffectiveConfiguration(CurrentQwenConfiguration().QwenAlignment!); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { }
        bool compatibleCpu = effectiveAlignment?.Hardware == LocalTranscriptionHardware.Cpu &&
            (view.QwenDisableCpuAccelerationBox.IsChecked == true || !QwenAlignmentRuntime.SupportsOptimizedCpu);
        string? alignmentExe = compatibleCpu ? effectiveAlignment!.ExecutablePath : QwenPythonControlsActive ? qwenPythonExecutable : qwenAlignerExecutable;
        string? alignmentModel = QwenPythonControlsActive ? qwenPythonModelDirectory : qwenAlignerModel;
        view.QwenAlignerExeText.Text = string.IsNullOrEmpty(alignmentExe) ? UiText.T("未配置程序") : alignmentExe;
        view.QwenAlignerModelText.Text = string.IsNullOrEmpty(alignmentModel) ? UiText.T("未配置模型") : alignmentModel;
        view.QwenDescriptionText.Text = UiText.T("中文与中英识别；0.6B / 1.7B 模型独立配置。识别后定位句子，点击字幕回听。模型不随软件分发。");
        view.QwenExeButton.Content = UiText.T("Qwen 程序"); view.QwenModelButton.Content = UiText.T("Qwen 模型");
        view.QwenAlignerExeButton.Content = UiText.T(QwenPythonControlsActive ? "官方 CPU 的 Python 程序" : "句子定位程序");
        view.QwenAlignerModelButton.Content = UiText.T(QwenPythonControlsActive ? "官方定位模型文件夹" : "句子定位模型");
        view.QwenAlignerExeButton.IsEnabled = open && !transcriptBusy && qwenSettingsReady && !compatibleCpu;
        view.QwenLicenseBox.Content = UiText.T("已核对 Qwen 和句子定位模型的来源与许可");
        view.QwenPromptLabel.Text = UiText.T("本场提示词（可选）"); view.QwenThreadsLabel.Text = UiText.T("CPU 线程"); view.QwenDeviceLabel.Text = UiText.T("GPU 编号");
        view.QwenRecognitionHardwareLabel.Text = UiText.T("识别硬件"); view.QwenAlignmentHardwareLabel.Text = UiText.T("句子定位方式");
        ((ComboBoxItem)view.QwenAlignmentHardwareBox.Items[0]).Content = UiText.T("CPU（原生）");
        ((ComboBoxItem)view.QwenAlignmentHardwareBox.Items[1]).Content = UiText.T("CPU（官方 Python，可选）");
        ((ComboBoxItem)view.QwenAlignmentHardwareBox.Items[2]).Content = UiText.T("Vulkan（原生）");
        view.QwenDisableCpuAccelerationBox.Content = UiText.T("兼容旧 CPU（关闭 AVX2/FMA 等加速）");
        view.QwenDisableCpuAccelerationBox.IsEnabled = open && !transcriptBusy && qwenSettingsReady &&
            (view.QwenAlignmentHardwareBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "Vulkan";
        view.QwenDisableCpuAccelerationBox.ToolTip = UiText.T("仅影响 CPU 句子定位。兼容模式使用随包通用 CPU 程序与 GGUF 模型，不运行官方 Python 或 AVX2 程序；原选择保留，取消勾选后恢复。");
        view.QwenAlignmentEffectiveText.Text = compatibleCpu
            ? UiText.T("实际定位：通用 CPU（兼容模式）；需要原生 GGUF 模型。原选择保留。") + "\n" + effectiveAlignment!.ExecutablePath
            : QwenPythonControlsActive ? UiText.T("实际定位：官方 Python CPU；需配置解释器与官方模型文件夹。")
            : effectiveAlignment is null ? UiText.T("请检查定位配置。") : UiText.T("实际定位程序：") + effectiveAlignment.ExecutablePath;
        if (compatibleCpu && !QwenAlignmentRuntime.SupportsOptimizedCpu)
            view.QwenAlignmentEffectiveText.Text += "\n" + UiText.T("当前 CPU 不满足优化指令要求，已启用兼容模式。");
        view.QwenDeviceBox.ToolTip = UiText.T("识别与句子定位共用 GPU 编号；CPU 阶段不使用 GPU。");
        view.RunQwenButton.Content = UiText.T("开始识别和句子定位"); view.SaveQwenDefaultsButton.Content = UiText.T("保存为新会默认"); view.QwenHelpButton.Content = UiText.T("如何配置 Qwen");
        ((ComboBoxItem)view.AsrBackendBox.Items[1]).Content = UiText.T("Qwen3-ASR + 句子定位");
        ((ComboBoxItem)view.QwenLanguageBox.Items[0]).Content = UiText.T("自动语言");
        ((ComboBoxItem)view.QwenLanguageBox.Items[1]).Content = UiText.T("中文");
        ((ComboBoxItem)view.QwenHardwareBox.Items[2]).Content = UiText.T("自动硬件");
    }

    private Action<string> PrepareNewMeetingWhisperDefaults()
    {
        var whisper = PrepareLegacyMeetingWhisperDefaults();
        QwenMeetingSettings defaults;
        try { defaults = QwenSettingsStore.LoadDefaults(Path.Combine(dataRoot, "settings")); }
        catch { defaults = new(); }
        return directory =>
        {
            whisper(directory);
            try { QwenSettingsStore.SnapshotDefaults(directory, defaults); }
            catch (Exception ex) { LogMeeting(directory, "asr", "Qwen 默认设置快照未保存：" + ex.Message, "warning"); }
        };
    }

    private async Task RunQwenAsync()
    {
        var meeting = SelectedTranscriptMeeting(); var mix = currentMix ?? throw new InvalidOperationException(UiText.T("请先生成完整混音。"));
        var configuration = CurrentQwenConfiguration(); var token = transcriptCancellation!.Token; var view = transcriptionView!;
        QwenSettingsStore.SaveMeeting(meeting.Directory, new(1, QwenTranscriber.BackendId, configuration));
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(meeting.Directory, "meeting.json")));
        string policy = metadata.RootElement.GetProperty("schemaVersion").GetString() == "m3-audio-import-1" ? "file-v1" :
            metadata.RootElement.TryGetProperty("timelinePolicy", out var value) ? value.GetString() ?? "elapsed-v1" : "elapsed-v1";
        qwenRunning = true; SetRecognitionMeeting(meeting.Directory); UpdateControls();
        try
        {
            using var display = new RecognitionProgressDisplay(view, () => transcriptBusy && !token.IsCancellationRequested &&
                transcriptCancellation?.Token == token && string.Equals(selectedMeetingDirectory, meeting.Directory, StringComparison.OrdinalIgnoreCase));
            var audio = await Task.Run(() => AsrAudioPreparation.Prepare(mix.WavePath, mix.WaveSha256,
                Path.Combine(meeting.Directory, "transcripts", "asr-inputs"), token), token);
            var progress = new Progress<LocalTranscriptionProgress>(display.Report);
            var result = await Task.Run(() => new QwenTranscriber().RunAsync(new(meeting.Directory, meeting.Id, meeting.DurationMs,
                audio.Path, audio.Sha256, configuration, meeting.Synthetic, mix.WaveSha256, policy), token, progress), token);
            await PresentQwenResultAsync(meeting.Directory, result);
        }
        finally { qwenRunning = false; SetRecognitionMeeting(null); }
    }

    private static string QwenPendingAlignmentNotice(TranscriptRevision? revision)
    {
        if (revision is null || !revision.Provider.StartsWith("Qwen3-ASR", StringComparison.Ordinal)) return "";
        int untimed = revision.Segments.Count(segment => segment.StartMs is null || segment.EndMs is null);
        return untimed == 0 ? "" : UiText.F($"文字已保存，有 {untimed} 段待定位。") + "\n" +
            UiText.T("待定位文字不能定位回听；TXT 保留全文，完成定位前不能导出 SRT/VTT。");
    }

    // Saving the words and verifying their acoustic locations are separate outcomes.
    // This also keeps late completion owned by its original meeting after navigation.
    private async Task PresentQwenResultAsync(string directory, LocalTranscriptionResult result)
    {
        string pending = result.Status == LocalTranscriptionStatus.Succeeded
            ? QwenPendingAlignmentNotice(result.Revision) : "";
        string message = pending.Length == 0 ? result.Message : pending + "\n" + result.Message;
        LogMeeting(directory, "asr", $"Qwen {result.Status}；{result.ElapsedMilliseconds} ms。\n{message}",
            result.Status == LocalTranscriptionStatus.Failed ? "error" :
            result.Status == LocalTranscriptionStatus.Cancelled || pending.Length > 0 ? "warning" : "info");
        if (result.Revision is { } revision && selectedMeetingDirectory == directory)
        {
            await LoadTranscriptVersionsAsync(revision.RevisionId);
            if (selectedMeetingDirectory == directory && !WindowCloseRequested) transcriptionView!.WorkspaceTabs.SelectedIndex = 1;
        }
        if (selectedMeetingDirectory == directory && !WindowCloseRequested)
            transcriptionView!.StatusText.Text = pending.Length == 0
                ? $"{result.Status} · {result.ElapsedMilliseconds / 1000d:F1} s\n{UiText.T(result.Message)}"
                : pending + "\n" + UiText.F($"已用时 {result.ElapsedMilliseconds / 1000d:F1} 秒。") + "\n" + UiText.T(result.Message);
    }

    private void YieldQwenToRecording()
    {
        YieldSpeakersToRecording();
        if (!qwenRunning) return;
        // Cancellation signals only owned inference processes. Recording does not await model unloading.
        transcriptCancellation?.Cancel();
        LogMeeting(recognitionMeetingDirectory, "asr", "开始下一场录音，已取消后台 Qwen 识别/定位；本次输出和已有原稿保留，可在录音保存后重试。", "warning");
    }

    private void ResetQwenConfigurationWithBackup()
    {
        if (qwenSettingsReady || transcriptBusy || WindowCloseRequested) return;
        try
        {
            string path = qwenMeeting is null ? Path.Combine(dataRoot, "settings", QwenSettingsStore.DefaultsName) :
                Path.Combine(qwenMeeting, QwenSettingsStore.RelativePath);
            RejectReparseAncestors(path);
            if (File.Exists(path)) File.Copy(path, path + ".invalid-" + Guid.NewGuid().ToString("N") + ".bak", false);
            if (qwenMeeting is null) QwenSettingsStore.SaveDefaults(Path.Combine(dataRoot, "settings"), new());
            else QwenSettingsStore.SaveMeeting(qwenMeeting, new());
            qwenSettingsReady = false; qwenDataRoot = null;
            SwitchQwenMeeting(); UpdateControls();
            transcriptionView!.StatusText.Text = UiText.T("已保留错误设置备份，可重新选择本地引擎和模型；音频与字幕未改动。");
        }
        catch (Exception ex) { transcriptionView!.StatusText.Text = ex.Message; }
    }
}
