using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

public partial class MainWindow
{
    internal object M3gBundledWhisperSmoke(string evidence)
    {
        if (host is not null || microphoneEnabled || transcriptBusy) throw new InvalidOperationException("No capture/inference permitted in bundled settings smoke.");
        string cpu = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, PortableWhisperConfiguration.BundledCpuExecutable));
        string vulkan = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, PortableWhisperConfiguration.BundledVulkanExecutable));
        if (!File.Exists(cpu) || !File.Exists(vulkan))
            return new { Status = "NOT_RUN", Reason = "Run this check from the complete portable package containing CPU and Vulkan engines." };
        dataRoot = Path.Combine(evidence, "bundled-settings"); Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        var view = transcriptionView!;
        LoadWhisperConfiguration();
        Need(CurrentWhisperConfiguration().Hardware == LocalTranscriptionHardware.Cpu && Same(whisperExecutable, cpu), "Fresh bundle did not default to CPU.");
        Select(LocalTranscriptionHardware.Vulkan);
        Need(Same(whisperExecutable, vulkan) && Same(view.WhisperExeText.Text, vulkan) &&
            Same(CurrentWhisperConfiguration().ExecutablePath, vulkan), "Vulkan selection did not update the actual and displayed executable.");
        Need(view.StatusText.Text.Contains(vulkan, StringComparison.OrdinalIgnoreCase), "The automatic file choice was not visible.");
        SaveWhisperConfiguration();
        var saved = JsonSerializer.Deserialize<WhisperConfiguration>(File.ReadAllText(WhisperConfigurationPath))!;
        Need(saved.ExecutablePath == PortableWhisperConfiguration.BundledVulkanExecutable && saved.Hardware == LocalTranscriptionHardware.Vulkan,
            "Saving Vulkan hardware did not persist the matching portable path.");
        LoadWhisperConfiguration();
        Need(Same(whisperExecutable, vulkan) && CurrentWhisperConfiguration().Hardware == LocalTranscriptionHardware.Vulkan, "Vulkan did not reload.");
        Select(LocalTranscriptionHardware.Cpu);
        Need(Same(whisperExecutable, cpu), "Switching back to CPU retained Vulkan.");
        string external = Path.Combine(dataRoot, "custom-never-executed.exe"); File.WriteAllText(external, "Synthetic UI path fixture, never executed.");
        whisperExecutable = external; view.WhisperExeText.Text = external;
        Select(LocalTranscriptionHardware.Vulkan);
        Need(Same(whisperExecutable, external) && Same(CurrentWhisperConfiguration().ExecutablePath, external), "Custom executable was silently replaced.");
        File.WriteAllText(WhisperConfigurationPath, JsonSerializer.Serialize(saved with { ExecutablePath = PortableWhisperConfiguration.BundledCpuExecutable }));
        string oldSettings = File.ReadAllText(WhisperConfigurationPath);
        LoadWhisperConfiguration();
        Need(Same(whisperExecutable, vulkan) && File.ReadAllText(WhisperConfigurationPath) == oldSettings,
            "Old bundled CPU/Vulkan mismatch was not corrected in memory or rewrote settings without save.");
        Need(host is null && !microphoneEnabled && !transcriptBusy, "Settings smoke started capture or inference.");
        return new { Status = "PASS", FreshCpuDefault = true, HardwareChangesActualAndDisplayedBundledEngine = true,
            ExplicitSaveAndReload = true, ExistingMismatchedBundledConfigurationResolvedInMemory = true, CustomExecutablePreserved = true,
            SourceMode = "REAL WINDOWS WPF CONTROLS / CONFIGURATION ONLY / NO CAPTURE OR INFERENCE" };
        void Select(LocalTranscriptionHardware hardware) => view.HardwareBox.SelectedItem = view.HardwareBox.Items.Cast<ComboBoxItem>().Single(item => item.Tag?.ToString() == hardware.ToString());
        static bool Same(string? first, string second) => string.Equals(first is null ? null : Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
