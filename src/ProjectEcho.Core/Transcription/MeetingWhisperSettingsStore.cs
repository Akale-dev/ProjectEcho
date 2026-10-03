using System.Text.Json;

namespace ProjectEcho.Core.Transcription;

/// <summary>Mutable meeting preferences; never edits authoritative ASR attempts or transcript revisions.</summary>
public sealed record MeetingWhisperSettings(int SchemaVersion, WhisperConfiguration? Configuration,
    string? PromptDraft, string? MigrationNotice = null);

public static class MeetingWhisperSettingsStore
{
    public const string RelativePath = "transcripts/local-asr-settings.json";
    public const string LegacyDefaultsFileName = "legacy-whisper-defaults.json";
    private const int MaximumBytes = 64000;
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNameCaseInsensitive = true, MaxDepth = 16, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };

    public static WhisperConfiguration? EnsureLegacyDefaults(string settingsDirectory, WhisperConfiguration? defaults,
        string applicationDirectory)
    {
        string path = TranscriptFiles.Inside(settingsDirectory, LegacyDefaultsFileName);
        if (Exists(path)) return Resolve(Read(path), applicationDirectory).Configuration;
        // Freeze the migration baseline before defaults can change. An absent configuration is also a snapshot.
        var baseline = new MeetingWhisperSettings(1, defaults is null ? null : defaults with { InitialPrompt = null }, null);
        Write(path, Store(baseline, applicationDirectory), overwrite: false);
        return baseline.Configuration;
    }

    public static MeetingWhisperSettings LoadOrCreate(string meetingDirectory, WhisperConfiguration? legacyDefaults,
        string applicationDirectory)
    {
        string meeting = TranscriptFiles.Meeting(meetingDirectory), path = TranscriptFiles.Inside(meeting, RelativePath);
        if (Exists(path)) return Resolve(Read(path), applicationDirectory);
        var settings = RecoverLatestAttempt(meeting, legacyDefaults);
        Write(path, Store(settings, applicationDirectory), overwrite: false);
        return Resolve(settings, applicationDirectory);
    }

    public static MeetingWhisperSettings? LoadExisting(string meetingDirectory, string applicationDirectory)
    {
        string path = TranscriptFiles.Inside(TranscriptFiles.Meeting(meetingDirectory), RelativePath);
        return Exists(path) ? Resolve(Read(path), applicationDirectory) : null;
    }

    public static void SnapshotDefaults(string meetingDirectory, WhisperConfiguration? defaults, string applicationDirectory)
    {
        string path = TranscriptFiles.Inside(TranscriptFiles.Meeting(meetingDirectory), RelativePath);
        if (Exists(path)) return;
        var settings = new MeetingWhisperSettings(1, defaults is null ? null : defaults with { InitialPrompt = null }, null);
        Write(path, Store(settings, applicationDirectory), overwrite: false);
    }

    public static void SavePrompt(string meetingDirectory, string? prompt, string applicationDirectory)
    {
        ValidatePrompt(prompt);
        string path = TranscriptFiles.Inside(TranscriptFiles.Meeting(meetingDirectory), RelativePath);
        // Require an existing snapshot so a draft cannot accidentally capture newer global defaults.
        var settings = Read(path) with { PromptDraft = prompt };
        Write(path, settings, overwrite: true);
    }

    public static void SaveAttemptConfiguration(string meetingDirectory, WhisperConfiguration configuration,
        string applicationDirectory)
    {
        ValidateConfiguration(configuration);
        string path = TranscriptFiles.Inside(TranscriptFiles.Meeting(meetingDirectory), RelativePath);
        // Called before preparation/launch, hence failed and cancelled attempts retain the chosen options too.
        Write(path, Store(new(1, configuration, configuration.InitialPrompt), applicationDirectory), overwrite: true);
    }

    public static void SaveDefaults(string path, WhisperConfiguration configuration, string applicationDirectory)
    {
        ValidateConfiguration(configuration);
        if (configuration.Hardware == LocalTranscriptionHardware.Cuda)
            throw new InvalidDataException("CUDA 已不再提供，请选择 CPU、自动或 Vulkan。");
        var stored = PortableWhisperConfiguration.ForStorage(configuration with { InitialPrompt = null }, applicationDirectory);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(stored);
        if (bytes.Length > 32000) throw new InvalidDataException("默认识别配置超过大小限制。");
        WriteBytes(TranscriptFiles.LocalPath(path), bytes, overwrite: true);
    }

    private static MeetingWhisperSettings RecoverLatestAttempt(string meeting, WhisperConfiguration? baseline)
    {
        baseline = baseline is null ? null : baseline with { InitialPrompt = null };
        string root = TranscriptFiles.Inside(meeting, "transcripts/asr");
        if (!Directory.Exists(root)) return new(1, baseline, null);
        JsonElement? latest = null; DateTimeOffset newest = DateTimeOffset.MinValue; string newestPath = "";
        var directories = Directory.EnumerateDirectories(root).Take(2001).ToArray();
        if (directories.Length > 2000) throw new InvalidDataException("旧识别记录超过迁移上限，请保留原件并手动选择本场配置。");
        bool unreadable = false;
        foreach (string directory in directories)
        {
            string path = TranscriptFiles.Inside(directory, "attempt.json");
            if (!Exists(path)) { unreadable = true; continue; }
            try
            {
                using var document = TranscriptFiles.ParseJson(TranscriptFiles.ReadBounded(path, CancellationToken.None, MaximumBytes));
                var attempt = document.RootElement;
                if (!attempt.TryGetProperty("importedAt", out var date) || !date.TryGetDateTimeOffset(out var time))
                { unreadable = true; continue; }
                if (time > newest || time == newest && StringComparer.Ordinal.Compare(path, newestPath) > 0)
                { latest = attempt.Clone(); newest = time; newestPath = path; }
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or InvalidOperationException or FormatException)
            { unreadable = true; }
        }
        if (latest is not { } saved) return new(1, baseline, null,
            unreadable ? "部分旧识别记录无法读取；本场使用升级时的默认基线，请核对后识别。" : null);
        var recovered = baseline ?? new WhisperConfiguration("", "");
        string? name = Text(saved, "modelName");
        bool sameModel = name is null || string.Equals(name, recovered.ModelName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, Path.GetFileName(recovered.ModelPath), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, Path.GetFileNameWithoutExtension(recovered.ModelPath), StringComparison.OrdinalIgnoreCase);
        var hardware = Text(saved, "requestedHardware") is { } requested
            ? Enum.TryParse<LocalTranscriptionHardware>(requested, true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed : throw new InvalidDataException("旧识别记录的硬件模式无效；没有回退到 CPU。")
            : saved.TryGetProperty("cpuOnly", out var cpu) && cpu.ValueKind == JsonValueKind.False ? LocalTranscriptionHardware.Auto : LocalTranscriptionHardware.Cpu;
        recovered = recovered with
        {
            Language = Text(saved, "language") ?? recovered.Language,
            Threads = Number(saved, "threads") ?? recovered.Threads,
            GpuDeviceIndex = Number(saved, "gpuDeviceIndex") ?? recovered.GpuDeviceIndex,
            InitialPrompt = Text(saved, "initialPrompt"), Hardware = hardware, ModelName = name ?? recovered.ModelName,
            ModelPath = sameModel ? recovered.ModelPath : "",
            ModelLicenseAcknowledged = sameModel && saved.TryGetProperty("modelLicenseAcknowledged", out var license) && license.ValueKind == JsonValueKind.True
        };
        ValidateConfiguration(recovered);
        return new(1, recovered, recovered.InitialPrompt,
            unreadable
                ? "部分旧识别记录无法读取；仅恢复最近可读记录的选项，可能不是最后一次识别。旧记录未保存程序和模型路径，请核对文件。"
                : "已恢复上次识别选项。旧记录未保存程序和模型路径，请核对文件；不同模型需要重新选择。");
    }

    private static string? Text(JsonElement element, string property) => element.TryGetProperty(property, out var value)
        ? value.ValueKind == JsonValueKind.Null ? null : value.GetString() : null;
    private static int? Number(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.GetInt32() : null;

    private static MeetingWhisperSettings Read(string path)
    {
        using var document = TranscriptFiles.ParseJson(TranscriptFiles.ReadBounded(path, CancellationToken.None, MaximumBytes));
        var result = document.RootElement.Deserialize<MeetingWhisperSettings>(Json) ?? throw new InvalidDataException("本场识别配置为空。");
        if (result.SchemaVersion != 1) throw new InvalidDataException("本场识别配置版本不受支持。");
        ValidatePrompt(result.PromptDraft);
        if (result.Configuration is { } configuration) ValidateConfiguration(configuration);
        if (result.MigrationNotice?.Length > 1000) throw new InvalidDataException("识别配置说明过长。");
        return result;
    }

    private static void ValidatePrompt(string? prompt)
    {
        if (prompt?.Length > 4000 || prompt?.Contains('\0') == true)
            throw new InvalidDataException("初始提示词不能超过 4000 字符或包含空字符；模型会按其上下文 token 上限截取。");
    }

    private static void ValidateConfiguration(WhisperConfiguration configuration)
    {
        ValidatePrompt(configuration.InitialPrompt);
        if (configuration.Threads is < 1 or > 64 || configuration.GpuDeviceIndex is < 0 or > 63 ||
            !Enum.IsDefined(configuration.Hardware) || configuration.Language is not ("auto" or "zh" or "en") ||
            configuration.ExecutablePath is null || configuration.ModelPath is null || configuration.ModelName?.Length > 200)
            throw new InvalidDataException("本场识别配置字段无效。");
    }

    private static MeetingWhisperSettings Resolve(MeetingWhisperSettings settings, string applicationDirectory)
    {
        if (settings.Configuration is not { } configuration) return settings;
        string root = PortableWhisperConfiguration.ApplicationDirectory(applicationDirectory);
        return settings with { Configuration = configuration with
        {
            ExecutablePath = configuration.ExecutablePath.Length == 0 ? "" : PortableWhisperConfiguration.SelectBundledExecutable(configuration.ExecutablePath, configuration.Hardware, root),
            ModelPath = configuration.ModelPath.Length == 0 ? "" : PortableWhisperConfiguration.ResolvePath(configuration.ModelPath, root),
            ExpectedModelSha256 = null
        } };
    }

    private static MeetingWhisperSettings Store(MeetingWhisperSettings settings, string applicationDirectory)
    {
        ValidatePrompt(settings.PromptDraft);
        if (settings.Configuration is not { } original) return settings;
        ValidateConfiguration(original);
        var resolved = Resolve(settings, applicationDirectory); var configuration = resolved.Configuration!;
        string root = PortableWhisperConfiguration.ApplicationDirectory(applicationDirectory);
        return resolved with { Configuration = configuration with
        {
            ExecutablePath = configuration.ExecutablePath.Length == 0 ? "" : PortableWhisperConfiguration.StoredPath(configuration.ExecutablePath, original.ExecutablePath, root),
            ModelPath = configuration.ModelPath.Length == 0 ? "" : PortableWhisperConfiguration.StoredPath(configuration.ModelPath, original.ModelPath, root)
        } };
    }

    private static void Write(string path, MeetingWhisperSettings settings, bool overwrite) =>
        WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(settings, Json), overwrite);

    private static void WriteBytes(string path, byte[] bytes, bool overwrite)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("本场识别配置超过大小限制。");
        TranscriptFiles.NoReparse(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            TranscriptFiles.WriteNew(temporary, bytes); TranscriptFiles.NoReparse(path);
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
