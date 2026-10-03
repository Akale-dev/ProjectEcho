using System.Text.Json;
using ProjectEcho.Core.Transcription;

internal static class M3dPortableWhisperTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        M3gBundledWhisperTests.Register(run, output);
        string root = Path.GetFullPath(Path.Combine(output, "m3d-portable-whisper")); Directory.CreateDirectory(root);
        run("T35/T47-SYNTH M3D bundled ASR paths survive relocation and unrelated working directory", () => Relocation(root));
        run("T35/T47-SYNTH M3D user ASR configuration wins and malformed files never use bundled fallback", () => Precedence(root));
        run("T35/T50-SYNTH M3D relative ASR paths reject escapes rooted drive-relative and alternate streams", () => PathBoundaries(root));
    }

    private static void Relocation(string root)
    {
        string oldRoot = Path.Combine(root, "original-location"), relocated = Path.Combine(root, "relocated folder");
        var original = Files(oldRoot);
        var stored = PortableWhisperConfiguration.ForStorage(original, oldRoot);
        Need(stored.ExecutablePath == "Tools/whisper/whisper-cli.exe" && stored.ModelPath == "Models/ggml-base.bin" &&
            stored.Hardware == LocalTranscriptionHardware.Cpu && stored.ExpectedModelSha256 is null,
            "Bundled paths were not relative or CPU/digest defaults changed.");
        var movedFiles = Files(relocated);
        string defaults = Path.Combine(relocated, PortableWhisperConfiguration.BundledFileName);
        File.WriteAllText(defaults, JsonSerializer.Serialize(stored));
        File.Delete(original.ExecutablePath); File.Delete(original.ModelPath);
        string settings = Path.Combine(relocated, "Data", "settings", "whisper.json");
        string cwd = Environment.CurrentDirectory;
        string unrelated = Path.Combine(root, "unrelated-cwd"); Directory.CreateDirectory(unrelated);
        try
        {
            Environment.CurrentDirectory = unrelated;
            var loaded = PortableWhisperConfiguration.Load(settings, relocated)!;
            Need(loaded == (movedFiles with { ExpectedModelSha256 = null }) && WhisperTranscriber.Inspect(loaded).Ready,
                "Relocated bundled defaults still require their original location or current directory.");
            Need(!Directory.Exists(Path.Combine(relocated, "Data")), "Loading bundled defaults wrote a user data directory.");
            Need(!File.Exists(original.ExecutablePath) && !File.Exists(original.ModelPath), "Original-location files were recreated.");
        }
        finally { Environment.CurrentDirectory = cwd; }
    }

    private static void Precedence(string root)
    {
        string app = Path.Combine(root, "precedence-app"), external = Path.Combine(root, "external-user-models");
        var defaults = Files(app);
        File.WriteAllText(Path.Combine(app, PortableWhisperConfiguration.BundledFileName),
            JsonSerializer.Serialize(PortableWhisperConfiguration.ForStorage(defaults, app)));
        var explicitConfig = Files(external) with
        {
            Hardware = LocalTranscriptionHardware.Vulkan, GpuDeviceIndex = 2, Threads = 6, Language = "zh",
            InitialPrompt = "公司 A, TICKER, EBITDA", ExpectedModelSha256 = "obsolete-user-hash"
        };
        var externalStored = PortableWhisperConfiguration.ForStorage(explicitConfig, app);
        Need(externalStored.ExecutablePath == explicitConfig.ExecutablePath && externalStored.ModelPath == explicitConfig.ModelPath,
            "External absolute file choices were rewritten.");
        string settings = Path.Combine(app, "Data", "settings", "whisper.json"); Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, JsonSerializer.Serialize(explicitConfig));
        var loaded = PortableWhisperConfiguration.Load(settings, app)!;
        Need(loaded == (explicitConfig with { ExpectedModelSha256 = null }), "Bundled CPU defaults replaced the user's explicit hardware/path/prompt choices.");
        string savedConfig = File.ReadAllText(settings);
        File.Delete(explicitConfig.ExecutablePath);
        loaded = PortableWhisperConfiguration.Load(settings, app)!;
        Need(loaded.Hardware == LocalTranscriptionHardware.Vulkan && loaded.ExecutablePath == explicitConfig.ExecutablePath &&
            !WhisperTranscriber.Inspect(loaded).Ready && File.ReadAllText(settings) == savedConfig,
            "Missing explicit user files triggered silent bundled fallback or rewrote configuration.");
        File.WriteAllText(settings, "{ damaged");
        Expect<JsonException>(() => PortableWhisperConfiguration.Load(settings, app), "Malformed user configuration used bundled fallback.");
        Need(File.ReadAllText(settings) == "{ damaged", "Malformed user configuration was overwritten.");
        File.WriteAllText(settings, new string(' ', 32001));
        Expect<InvalidDataException>(() => PortableWhisperConfiguration.Load(settings, app), "Oversize user configuration used bundled fallback.");
        File.Delete(settings);
        Need(PortableWhisperConfiguration.Load(settings, app)!.Hardware == LocalTranscriptionHardware.Cpu,
            "Absent user configuration did not use bundled CPU defaults.");
        File.Delete(Path.Combine(app, PortableWhisperConfiguration.BundledFileName));
        Need(PortableWhisperConfiguration.Load(settings, app) is null, "No configuration files should mean unconfigured.");
    }

    private static void PathBoundaries(string root)
    {
        string app = Path.Combine(root, "path-app"); var configuration = Files(app);
        var relative = PortableWhisperConfiguration.ForStorage(configuration, app);
        foreach (string invalid in new[] { "../escape.exe", "Tools/../escape.exe", "Tools//whisper-cli.exe", "./Tools/whisper-cli.exe",
            "C:drive-relative.exe", "Tools/whisper-cli.exe:stream", "Tools /whisper-cli.exe", "Tools./whisper-cli.exe", "https://invalid.example/model.bin" })
            Expect<InvalidDataException>(() => PortableWhisperConfiguration.ResolvePaths(relative with { ExecutablePath = invalid }, app),
                "Unsafe relative path was accepted: " + invalid);
        if (OperatingSystem.IsWindows())
        {
            Expect<InvalidDataException>(() => PortableWhisperConfiguration.ResolvePaths(relative with { ModelPath = @"\Models\ggml-base.bin" }, app),
                "Drive-rooted path depended on current drive.");
            Expect<InvalidDataException>(() => PortableWhisperConfiguration.ResolvePaths(relative with { ModelPath = @"\\invalid\share\model.bin" }, app),
                "UNC model path was accepted.");
        }
        string sibling = app + "-sibling"; var siblingFiles = Files(sibling);
        var siblingStored = PortableWhisperConfiguration.ForStorage(siblingFiles, app);
        Need(siblingStored.ExecutablePath == siblingFiles.ExecutablePath && siblingStored.ModelPath == siblingFiles.ModelPath,
            "A similarly prefixed sibling directory was treated as inside the portable folder.");
        Expect<InvalidDataException>(() => PortableWhisperConfiguration.ResolvePaths(relative, "relative-app-root"),
            "Relative application root was accepted.");
    }

    private static WhisperConfiguration Files(string root)
    {
        string exe = Path.Combine(root, "Tools", "whisper", "whisper-cli.exe"), model = Path.Combine(root, "Models", "ggml-base.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!); Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        File.WriteAllText(exe, "Synthetic path-only executable, never run.");
        File.WriteAllText(model, "Synthetic path-only model, never infer.");
        return new(exe, model, ModelLicenseAcknowledged: true, ExpectedModelSha256: "obsolete-digest");
    }

    private static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidDataException(message);
    }

    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
