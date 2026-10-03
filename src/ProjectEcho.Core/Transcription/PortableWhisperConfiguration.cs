using System.Text.Json;

namespace ProjectEcho.Core.Transcription;

/// <summary>
/// Resolves saved ASR file choices against the executable's directory, never the process working directory.
/// The optional bundled defaults apply only when the user's configuration file does not exist.
/// </summary>
public static class PortableWhisperConfiguration
{
    public const string BundledFileName = "bundled-whisper.json";
    public const string BundledCpuExecutable = "Tools/whisper-cpu/whisper-cli.exe";
    public const string BundledVulkanExecutable = "Tools/whisper-vulkan/whisper-cli.exe";
    private const int MaximumConfigurationBytes = 32000;

    public static WhisperConfiguration? Load(string settingsPath, string applicationDirectory)
    {
        string root = ApplicationDirectory(applicationDirectory);
        string userPath = TranscriptFiles.LocalPath(settingsPath);
        string path = Exists(userPath) ? userPath : Path.Combine(root, BundledFileName);
        if (!Exists(path)) return null;
        byte[] bytes = TranscriptFiles.ReadBounded(path, CancellationToken.None, MaximumConfigurationBytes);
        var configuration = JsonSerializer.Deserialize<WhisperConfiguration>(bytes)
            ?? throw new InvalidDataException("本地识别配置为空。");
        return ResolvePaths(configuration, root);
    }

    public static WhisperConfiguration ResolvePaths(WhisperConfiguration configuration, string applicationDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string root = ApplicationDirectory(applicationDirectory);
        return configuration with
        {
            ExecutablePath = SelectBundledExecutable(configuration.ExecutablePath, configuration.Hardware, root),
            ModelPath = ResolvePath(configuration.ModelPath, root),
            ExpectedModelSha256 = null
        };
    }

    /// <summary>
    /// CPU and Vulkan are separate executables in the complete portable distribution.
    /// An explicit hardware selection changes only these two exact paths in this application
    /// folder. External/custom engines and Auto/CUDA choices retain their selected executable.
    /// This chooses a file, not evidence of a working GPU; runtime verification is still required.
    /// </summary>
    public static string SelectBundledExecutable(string executablePath, LocalTranscriptionHardware hardware, string applicationDirectory)
    {
        string root = ApplicationDirectory(applicationDirectory);
        string selected = ResolvePath(executablePath, root);
        if (hardware is not (LocalTranscriptionHardware.Cpu or LocalTranscriptionHardware.Vulkan)) return selected;
        string cpu = Path.GetFullPath(BundledCpuExecutable, root), vulkan = Path.GetFullPath(BundledVulkanExecutable, root);
        if (!string.Equals(selected, cpu, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(selected, vulkan, StringComparison.OrdinalIgnoreCase)) return selected;
        string target = ResolvePath(hardware == LocalTranscriptionHardware.Vulkan ? vulkan : cpu, root);
        if (string.Equals(selected, target, StringComparison.OrdinalIgnoreCase)) return selected;
        // A missing saved file must not silently fall back to another file. Keep the user's
        // configuration intact and make the incomplete package visible before starting a job.
        if (!File.Exists(selected)) throw new FileNotFoundException("已选择的随包识别程序不存在；请检查完整解压的程序文件夹。", selected);
        if (!File.Exists(target)) throw new FileNotFoundException($"缺少随包 {hardware} 识别程序；请完整解压或手动选择对应的 whisper-cli.exe。", target);
        return target;
    }

    public static WhisperConfiguration ForStorage(WhisperConfiguration configuration, string applicationDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string root = ApplicationDirectory(applicationDirectory);
        var resolved = ResolvePaths(configuration, root);
        return resolved with
        {
            ExecutablePath = StoredPath(resolved.ExecutablePath, configuration.ExecutablePath, root),
            ModelPath = StoredPath(resolved.ModelPath, configuration.ModelPath, root)
        };
    }

    internal static string ApplicationDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new InvalidDataException("识别程序基目录必须是本地绝对路径。");
        return TranscriptFiles.LocalPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    internal static string ResolvePath(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("必须选择本地识别程序和模型文件。");
        if (Path.IsPathFullyQualified(path)) return TranscriptFiles.LocalPath(path);
        string[] parts = path.Replace('\\', '/').Split('/');
        if (Path.IsPathRooted(path) || path.Contains(':') || parts.Any(part => part is "" or "." or ".." ||
            part.TrimEnd(' ', '.') != part || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("本地识别配置中的相对路径必须位于程序文件夹内。");
        string full = TranscriptFiles.LocalPath(Path.GetFullPath(path, root));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("本地识别配置中的相对路径越过程序文件夹。");
        return full;
    }

    internal static string StoredPath(string resolvedPath, string originalPath, string root) =>
        resolvedPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(root, resolvedPath).Replace('\\', '/')
            : originalPath;

    // File.Exists hides access errors. Only actual absence permits using bundled defaults.
    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
