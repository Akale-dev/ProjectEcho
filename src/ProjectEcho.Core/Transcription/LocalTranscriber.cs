namespace ProjectEcho.Core.Transcription;

/// <summary>Properties of a verified adapter, not a promise of model accuracy or language coverage.</summary>
public sealed record LocalTranscriberCapabilities(int InputSampleRate, int InputChannels, int InputBitsPerSample,
    bool CanCancel, bool CanAutoDetectLanguage, bool CanSelectLanguage, bool CanIdentifySpeakers,
    string TimestampQuality, string TimestampGranularity);

public sealed record LocalTranscriberDescriptor(string Id, string DisplayName, LocalTranscriberCapabilities Capabilities);

/// <summary>Only explicit local files and bounded options. No URL, API key, command template or arbitrary arguments.</summary>
public sealed record LocalTranscriptionConfiguration(string BackendId, string ExecutablePath, string ModelPath,
    string Language = "auto", int Threads = 2, string? ModelName = null,
    string? ExpectedModelSha256 = null, bool ModelLicenseAcknowledged = false,
    LocalTranscriptionHardware Hardware = LocalTranscriptionHardware.Cpu, int GpuDeviceIndex = 0,
    string? InitialPrompt = null, QwenAlignmentConfiguration? QwenAlignment = null)
{
    // Keep the deployed whisper.json contract readable without migrating or replacing existing configuration.
    public static LocalTranscriptionConfiguration FromWhisper(WhisperConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new(WhisperCppLocalTranscriber.BackendId, configuration.ExecutablePath, configuration.ModelPath,
            configuration.Language, configuration.Threads, configuration.ModelName,
            configuration.ExpectedModelSha256, configuration.ModelLicenseAcknowledged,
            configuration.Hardware, configuration.GpuDeviceIndex, configuration.InitialPrompt);
    }
}

/// <summary>The entire saved audio timeline; pause policy is preserved by preparation, never reapplied by ASR.</summary>
public sealed record LocalTranscriptionRequest(string MeetingDirectory, string MeetingId, long MeetingDurationMs,
    string WavePath, string ExpectedWaveSha256, LocalTranscriptionConfiguration Configuration, bool IsSynthetic = false,
    string? SourceMixSha256 = null, string TimelinePolicy = "elapsed-v1");

public sealed record LocalTranscriptionReadiness(bool Ready, string Message);

public enum LocalTranscriptionStatus { NotConfigured, Succeeded, NoSpeech, Cancelled, Failed }

// Append new stages so existing values retain their numeric identity.
public enum LocalTranscriptionStage { Validating, Recognizing, Saving, LoadingModel, Aligning }

/// <summary>
/// Percent is engine-reported seek/window recognition progress, not elapsed work or total task time.
/// Validation, model loading, recognition before a callback, and saving have no invented percentage.
/// </summary>
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
            request.IsSynthetic, request.SourceMixSha256, request.TimelinePolicy), cancellationToken, progress).ConfigureAwait(false);
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
            configuration.ModelName, configuration.ExpectedModelSha256, configuration.ModelLicenseAcknowledged,
            configuration.Hardware, configuration.GpuDeviceIndex, configuration.InitialPrompt);
}

/// <summary>A development note, deliberately not an ILocalTranscriber or selectable runnable backend.</summary>
public sealed record FutureLocalTranscriberPreparation(string Id, string DisplayName, string State,
    string RequiredValidation);

public static class LocalTranscriptionBackends
{
    public static ILocalTranscriber Default { get; } = new WhisperCppLocalTranscriber();

    // Independent verified native ASR + raw acoustic alignment; never replaces the existing default.
    public static ILocalTranscriber Qwen3Asr { get; } = new QwenTranscriber();
}
