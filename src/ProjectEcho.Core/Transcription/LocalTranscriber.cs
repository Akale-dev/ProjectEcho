namespace ProjectEcho.Core.Transcription;

/// <summary>Properties of a verified adapter, not a promise of model accuracy or language coverage.</summary>
public sealed record LocalTranscriberCapabilities(int InputSampleRate, int InputChannels, int InputBitsPerSample,
    bool CanCancel, bool CanAutoDetectLanguage, bool CanSelectLanguage, bool CanIdentifySpeakers,
    string TimestampQuality, string TimestampGranularity);

public sealed record LocalTranscriberDescriptor(string Id, string DisplayName, LocalTranscriberCapabilities Capabilities);

/// <summary>Only explicit local files and bounded options. No URL, API key, command template or arbitrary arguments.</summary>
public sealed record LocalTranscriptionConfiguration(string BackendId, string ExecutablePath, string ModelPath,
    string Language = "auto", int Threads = 2, string? ModelName = null,
    string? ExpectedModelSha256 = null, bool ModelLicenseAcknowledged = false)
{
    // Keep the deployed whisper.json contract readable without migrating or replacing existing configuration.
    public static LocalTranscriptionConfiguration FromWhisper(WhisperConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new(WhisperCppLocalTranscriber.BackendId, configuration.ExecutablePath, configuration.ModelPath,
            configuration.Language, configuration.Threads, configuration.ModelName,
            configuration.ExpectedModelSha256, configuration.ModelLicenseAcknowledged);
    }
}

/// <summary>The entire saved meeting timeline, including pauses; audio preparation belongs outside the adapter.</summary>
public sealed record LocalTranscriptionRequest(string MeetingDirectory, string MeetingId, long MeetingDurationMs,
    string WavePath, string ExpectedWaveSha256, LocalTranscriptionConfiguration Configuration, bool IsSynthetic = false);

public sealed record LocalTranscriptionReadiness(bool Ready, string Message);

public enum LocalTranscriptionStatus { NotConfigured, Succeeded, NoSpeech, Cancelled, Failed }

public enum LocalTranscriptionStage { Validating, Recognizing, Saving }

/// <summary>Percent is engine-reported recognition progress, not an estimate of total task time.</summary>
public sealed record LocalTranscriptionProgress(LocalTranscriptionStage Stage, int? Percent = null);

/// <summary>
/// A successful result is an immutable TranscriptService revision with retained original model output.
/// Untimed output must remain untimed; approximate segment times must never be presented as word alignment.
/// Unsuccessful attempts return no revision; any revision already committed on disk is still retained. Retry creates a separate attempt.
/// </summary>
public sealed record LocalTranscriptionResult(LocalTranscriptionStatus Status, string AttemptDirectory,
    long ElapsedMilliseconds, TranscriptRevision? Revision, string Message);

/// <summary>
/// Optional post-meeting local ASR only. Implementations must not access capture, download files or call network services.
/// Inspect performs cheap local checks; callers run RunAsync on a background task because file validation may be synchronous.
/// Cancellation must stop owned inference work and preserve input audio, previous revisions and raw attempt outputs.
/// </summary>
public interface ILocalTranscriber
{
    LocalTranscriberDescriptor Descriptor { get; }
    LocalTranscriptionReadiness Inspect(LocalTranscriptionConfiguration? configuration);
    Task<LocalTranscriptionResult> RunAsync(LocalTranscriptionRequest request, CancellationToken cancellationToken = default,
        IProgress<LocalTranscriptionProgress>? progress = null);
}

/// <summary>The current verified adapter; the legacy static API remains compatible.</summary>
public sealed class WhisperCppLocalTranscriber : ILocalTranscriber
{
    public const string BackendId = "whisper.cpp";

    public LocalTranscriberDescriptor Descriptor { get; } = new(BackendId, "whisper.cpp（本地）",
        new(16000, 1, 16, CanCancel: true, CanAutoDetectLanguage: true, CanSelectLanguage: true,
            CanIdentifySpeakers: false, TimestampQuality: "approximate", TimestampGranularity: "segment"));

    public LocalTranscriptionReadiness Inspect(LocalTranscriptionConfiguration? configuration)
    {
        if (configuration is not null && configuration.BackendId != BackendId)
            return new(false, "所选配置不属于 whisper.cpp；不会用其他引擎配置启动程序或自动回退。");
        var readiness = WhisperTranscriber.Inspect(configuration is null ? null : ToWhisper(configuration));
        return new(readiness.Ready, readiness.Message);
    }

    public async Task<LocalTranscriptionResult> RunAsync(LocalTranscriptionRequest request, CancellationToken cancellationToken = default,
        IProgress<LocalTranscriptionProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var readiness = Inspect(request.Configuration);
        if (!readiness.Ready) return new(LocalTranscriptionStatus.NotConfigured, "", 0, null, readiness.Message);
        var result = await WhisperTranscriber.RunAsync(new(request.MeetingDirectory, request.MeetingId,
            request.MeetingDurationMs, request.WavePath, request.ExpectedWaveSha256, ToWhisper(request.Configuration),
            request.IsSynthetic), cancellationToken, progress).ConfigureAwait(false);
        var status = result.Status switch
        {
            "NotConfigured" => LocalTranscriptionStatus.NotConfigured,
            "Succeeded" => LocalTranscriptionStatus.Succeeded,
            "NoSpeech" => LocalTranscriptionStatus.NoSpeech,
            "Cancelled" => LocalTranscriptionStatus.Cancelled,
            "Failed" => LocalTranscriptionStatus.Failed,
            _ => throw new InvalidDataException("本地识别返回未知状态，不能将其标记为成功。")
        };
        if (status == LocalTranscriptionStatus.Succeeded && result.Revision is null)
            throw new InvalidDataException("本地识别未产生保存的版本，不能将其标记为成功。");
        return new(status, result.AttemptDirectory, result.ElapsedMilliseconds,
            status == LocalTranscriptionStatus.Succeeded ? result.Revision : null, result.Message);
    }

    private static WhisperConfiguration ToWhisper(LocalTranscriptionConfiguration configuration) =>
        new(configuration.ExecutablePath, configuration.ModelPath, configuration.Language, configuration.Threads,
            configuration.ModelName, configuration.ExpectedModelSha256, configuration.ModelLicenseAcknowledged);
}

/// <summary>A development note, deliberately not an ILocalTranscriber or selectable runnable backend.</summary>
public sealed record FutureLocalTranscriberPreparation(string Id, string DisplayName, string State,
    string RequiredValidation);

public static class LocalTranscriptionBackends
{
    public static ILocalTranscriber Default { get; } = new WhisperCppLocalTranscriber();

    // Do not infer Qwen runtime, input format, time alignment or language capabilities from Whisper's adapter.
    public static FutureLocalTranscriberPreparation Qwen3Asr { get; } = new("qwen3-asr", "Qwen3-ASR",
        "NotImplemented", "后续独立实现本地适配器，核验运行环境、模型及许可、完整时间轴、时间戳能力、取消与原稿保留后才能启用。当前未部署、不可运行。");
}
