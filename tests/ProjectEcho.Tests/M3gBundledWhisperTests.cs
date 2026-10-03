using System.Text.Json;
using ProjectEcho.Core.Transcription;

internal static class M3gBundledWhisperTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3g-bundled-hardware");
        run("T35/T47-SYNTH M3G bundled CPU and Vulkan selection follows hardware and survives save/load relocation", () => HardwareAndRelocation(root));
        run("T35/T50-SYNTH M3G hardware selection preserves custom external Auto CUDA and incomplete-package boundaries", () => Boundaries(root));
    }

    private static void HardwareAndRelocation(string root)
    {
        string app = Path.Combine(root, "app"), moved = Path.Combine(root, "新位置");
        var paths = Files(app); var movedPaths = Files(moved);
        Need(PortableWhisperConfiguration.SelectBundledExecutable(paths.Cpu, LocalTranscriptionHardware.Vulkan, app) == paths.Vulkan,
            "Selecting Vulkan left the bundled CPU executable selected.");
        Need(PortableWhisperConfiguration.SelectBundledExecutable(paths.Vulkan, LocalTranscriptionHardware.Cpu, app) == paths.Cpu,
            "Selecting CPU did not switch back to its bundled executable.");
        var configuration = new WhisperConfiguration(paths.Cpu, paths.Model, Threads: 7, Language: "zh",
            ModelLicenseAcknowledged: true, Hardware: LocalTranscriptionHardware.Vulkan, GpuDeviceIndex: 3, InitialPrompt: "Company TEST.A");
        var saved = PortableWhisperConfiguration.ForStorage(configuration, app);
        Need(saved.ExecutablePath == PortableWhisperConfiguration.BundledVulkanExecutable && saved.ModelPath == "Models/ggml-base.bin" &&
            saved.GpuDeviceIndex == 3 && saved.Threads == 7 && saved.InitialPrompt == "Company TEST.A", "Save lost settings or stored the wrong engine.");
        string settings = Path.Combine(moved, "settings", "whisper.json"); Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        // M3F could persist Vulkan hardware with the default CPU path. Loading fixes this exact bundled pair in memory only.
        var old = saved with { ExecutablePath = PortableWhisperConfiguration.BundledCpuExecutable };
        string original = JsonSerializer.Serialize(old); File.WriteAllText(settings, original);
        var loaded = PortableWhisperConfiguration.Load(settings, moved)!;
        Need(loaded.ExecutablePath == movedPaths.Vulkan && loaded.ModelPath == movedPaths.Model && loaded.Hardware == LocalTranscriptionHardware.Vulkan,
            "Relocated old hardware selection was not resolved to this package's Vulkan runtime.");
        Need(File.ReadAllText(settings) == original, "Reading the old configuration rewrote the user's settings.");
        Need(PortableWhisperConfiguration.ForStorage(loaded, moved).ExecutablePath == PortableWhisperConfiguration.BundledVulkanExecutable,
            "Corrected runtime did not remain portable on explicit save.");
    }

    private static void Boundaries(string root)
    {
        string app = Path.Combine(root, "boundaries"), external = Path.Combine(root, "external-package");
        var paths = Files(app); var outside = Files(external);
        string custom = Path.Combine(app, "custom-cli.exe"); File.WriteAllText(custom, "Synthetic path fixture; never execute.");
        foreach (var hardware in Enum.GetValues<LocalTranscriptionHardware>())
        {
            Need(PortableWhisperConfiguration.SelectBundledExecutable(outside.Cpu, hardware, app) == outside.Cpu,
                "External user executable was replaced.");
            Need(PortableWhisperConfiguration.SelectBundledExecutable(custom, hardware, app) == custom,
                "Custom user executable was replaced.");
        }
        foreach (var hardware in new[] { LocalTranscriptionHardware.Auto, LocalTranscriptionHardware.Cuda })
            Need(PortableWhisperConfiguration.SelectBundledExecutable(paths.Cpu, hardware, app) == paths.Cpu,
                "Auto/CUDA silently chose a different backend.");
        File.Delete(paths.Vulkan);
        Expect<FileNotFoundException>(() => PortableWhisperConfiguration.SelectBundledExecutable(paths.Cpu, LocalTranscriptionHardware.Vulkan, app));
        File.WriteAllText(paths.Vulkan, "Synthetic Vulkan path fixture."); File.Delete(paths.Cpu);
        Expect<FileNotFoundException>(() => PortableWhisperConfiguration.SelectBundledExecutable(paths.Cpu, LocalTranscriptionHardware.Vulkan, app));
        Expect<InvalidDataException>(() => PortableWhisperConfiguration.SelectBundledExecutable("Tools/../escape.exe", LocalTranscriptionHardware.Vulkan, app));
    }

    private static (string Cpu, string Vulkan, string Model) Files(string root)
    {
        string cpu = Path.GetFullPath(Path.Combine(root, PortableWhisperConfiguration.BundledCpuExecutable));
        string vulkan = Path.GetFullPath(Path.Combine(root, PortableWhisperConfiguration.BundledVulkanExecutable));
        string model = Path.GetFullPath(Path.Combine(root, "Models", "ggml-base.bin"));
        foreach (string path in new[] { cpu, vulkan, model })
        { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "Synthetic path fixture; never execute or infer."); }
        return (cpu, vulkan, model);
    }

    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidDataException("Expected " + typeof(T).Name); }
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
