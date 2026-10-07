using System.Text.Json;

namespace ProjectEcho.Core.Transcription;

public enum QwenAlignmentEngine { Native, OfficialPython }

public sealed record QwenPythonAlignmentConfiguration(string ExecutablePath, string ModelDirectory, string BridgePath);

/// <summary>Separate offline forced aligner; Python is optional and isolated from recording/export.</summary>
public sealed record QwenAlignmentConfiguration(string ExecutablePath, string ModelPath,
    LocalTranscriptionHardware Hardware = LocalTranscriptionHardware.Cpu, int GpuDeviceIndex = 0,
    QwenAlignmentEngine Engine = QwenAlignmentEngine.Native, QwenPythonAlignmentConfiguration? Python = null,
    bool DisableCpuAcceleration = false);

public sealed record QwenMeetingSettings(int SchemaVersion = 1, string BackendId = "whisper.cpp",
    LocalTranscriptionConfiguration? Configuration = null);

/// <summary>Qwen preferences are separate from legacy Whisper preferences and immutable attempts.</summary>
public static class QwenSettingsStore
{
    public const string RelativePath = "transcripts/qwen-settings.json";
    public const string DefaultsName = "qwen-defaults.json";
    private const int MaximumBytes = 64000;

    public static QwenMeetingSettings LoadMeeting(string directory) =>
        Read(TranscriptFiles.Inside(TranscriptFiles.Meeting(directory), RelativePath)) ?? new();

    public static QwenMeetingSettings LoadDefaults(string settingsDirectory) =>
        Read(TranscriptFiles.Inside(settingsDirectory, DefaultsName)) ?? new();

    public static void SaveMeeting(string directory, QwenMeetingSettings settings) =>
        Write(TranscriptFiles.Inside(TranscriptFiles.Meeting(directory), RelativePath), settings, true);

    public static void SaveDefaults(string settingsDirectory, QwenMeetingSettings settings) =>
        Write(TranscriptFiles.Inside(settingsDirectory, DefaultsName),
            settings with { Configuration = settings.Configuration is { } configuration ? configuration with { InitialPrompt = null } : null }, true);

    public static void SnapshotDefaults(string directory, QwenMeetingSettings defaults)
    {
        string path = TranscriptFiles.Inside(TranscriptFiles.Meeting(directory), RelativePath);
        if (File.Exists(path)) return;
        Write(path, defaults with { Configuration = defaults.Configuration is { } configuration ? configuration with { InitialPrompt = null } : null }, false);
    }

    private static QwenMeetingSettings? Read(string path)
    {
        if (!File.Exists(path)) return null;
        using var json = TranscriptFiles.ParseJson(TranscriptFiles.ReadBounded(path, default, MaximumBytes));
        var settings = JsonSerializer.Deserialize<QwenMeetingSettings>(json.RootElement.GetRawText(), TranscriptFiles.Json)
            ?? throw new InvalidDataException("Qwen 配置为空。");
        settings = TransformPaths(settings, ResolvePath);
        Validate(settings); return settings;
    }

    private static void Validate(QwenMeetingSettings settings)
    {
        if (settings.SchemaVersion is not (1 or 2) || settings.BackendId is not ("whisper.cpp" or "qwen3-asr"))
            throw new InvalidDataException("本场本地识别引擎配置无效；不会自动换引擎。");
        if (settings.Configuration is not { } configuration) return;
        if (configuration.BackendId != "qwen3-asr" || configuration.ExecutablePath is null || configuration.ModelPath is null ||
            configuration.Threads is < 1 or > 64 || configuration.GpuDeviceIndex is < 0 or > 63 ||
            configuration.Hardware is not (LocalTranscriptionHardware.Cpu or LocalTranscriptionHardware.Auto or LocalTranscriptionHardware.Vulkan) ||
            configuration.Language is not ("auto" or "zh" or "en") || configuration.ModelName?.Length > 200 ||
            configuration.InitialPrompt?.Length > 4000 || configuration.InitialPrompt?.Contains('\0') == true)
            throw new InvalidDataException("Qwen 配置字段无效。");
        if (configuration.QwenAlignment is { } alignment &&
            (alignment.Hardware is not (LocalTranscriptionHardware.Cpu or LocalTranscriptionHardware.Vulkan) || alignment.GpuDeviceIndex is < 0 or > 63 ||
             alignment.Engine is not (QwenAlignmentEngine.Native or QwenAlignmentEngine.OfficialPython) ||
             alignment.Engine == QwenAlignmentEngine.OfficialPython && alignment.Hardware != LocalTranscriptionHardware.Cpu))
            throw new InvalidDataException("句子定位硬件设置无效。");
        foreach (string path in new[] { configuration.ExecutablePath, configuration.ModelPath,
            configuration.QwenAlignment?.ExecutablePath ?? "", configuration.QwenAlignment?.ModelPath ?? "",
            configuration.QwenAlignment?.Python?.ExecutablePath ?? "", configuration.QwenAlignment?.Python?.ModelDirectory ?? "",
            configuration.QwenAlignment?.Python?.BridgePath ?? "" })
            if (!string.IsNullOrEmpty(path)) _ = TranscriptFiles.LocalPath(path);
    }

    private static void Write(string path, QwenMeetingSettings settings, bool overwrite)
    {
        Validate(settings);
        // Older applications must reject new engine/ISA controls rather than silently ignore them.
        settings = settings with { SchemaVersion = 2 };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(TransformPaths(settings, StorePath), TranscriptFiles.Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Qwen 配置超过大小限制。");
        TranscriptFiles.NoReparse(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            TranscriptFiles.WriteNew(temporary, bytes); TranscriptFiles.NoReparse(path);
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Only bundled executable locations are relative. User-selected models remain outside the package.
    private static QwenMeetingSettings TransformPaths(QwenMeetingSettings settings, Func<string, string> transform)
    {
        if (settings.Configuration is not { } configuration) return settings;
        return settings with { Configuration = configuration with
        {
            ExecutablePath = transform(configuration.ExecutablePath),
            QwenAlignment = configuration.QwenAlignment is { } alignment ? alignment with
            {
                ExecutablePath = transform(alignment.ExecutablePath),
                Python = alignment.Python is { } python ? python with { BridgePath = transform(python.BridgePath) } : null
            } : null
        } };
    }

    private static string StorePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        foreach (string relative in BundledPaths)
            if (Path.GetFullPath(path).Equals(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative)), StringComparison.OrdinalIgnoreCase))
                return "@app/" + relative;
        return path;
    }

    private static string ResolvePath(string path)
    {
        if (!path.StartsWith("@app/", StringComparison.Ordinal)) return path;
        string relative = path[5..];
        if (!BundledPaths.Contains(relative, StringComparer.Ordinal)) throw new InvalidDataException("随包 Qwen 程序引用无效。");
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative));
    }

    private static readonly string[] BundledPaths = ["Tools/qwen-cpu/transcribe-cli.exe", "Tools/qwen-vulkan/transcribe-cli.exe",
        "Tools/qwen-aligner/audiocpp_cli.exe", "Tools/qwen-aligner-avx2/audiocpp_cli.exe", "Tools/qwen-aligner-vulkan/audiocpp_cli.exe",
        "Tools/qwen-python/qwen_aligner_bridge.py"];
}
