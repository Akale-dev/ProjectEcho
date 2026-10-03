using System.Text.Json.Serialization;

namespace ProjectEcho.Core.Transcription;

public sealed record TranscriptSegment(string SegmentId, long? StartMs, long? EndMs,
    string? SpeakerLabel, string SourceKind, string Text, string TimestampQuality);

public sealed record TranscriptTimeSpan(long FileStartFrame, long SourceStartFrame, long FrameCount);

public sealed record TranscriptRevision(int SchemaVersion, bool IsSynthetic, string MeetingId,
    string RevisionId, string? ParentRevisionId, string Provider, string? ModelName,
    DateTimeOffset ImportedAt, long MeetingDurationMs, string? InputExportId, string? InputPartId,
    string? SourceMixSha256, int? MappingSampleRate, IReadOnlyList<TranscriptTimeSpan> SourceTimeMapping,
    string OriginalFile, string OriginalSha256, string? ReviewNote,
    IReadOnlyList<TranscriptSegment> Segments, IReadOnlyList<string> Warnings);

public sealed record TranscriptImportRequest(string MeetingDirectory, string MeetingId,
    long MeetingDurationMs, string InputPath, string ProviderName = "手动导入", string? ModelName = null,
    string? ExportManifestPath = null, string? InputPartId = null, bool IsSynthetic = false,
    string? ExpectedMixSha256 = null);

public sealed record TranscriptSearchHit(string RevisionId, string SegmentId, long? StartMs,
    long? EndMs, string Text, string? SpeakerLabel, bool IsReviewed);

public sealed record TranscriptOverlapCandidate(string LeftRevisionId, string LeftSegmentId,
    string RightRevisionId, string RightSegmentId, long StartMs, long EndMs, bool SameText);

/// <summary>Explicit GPU modes require the selected CLI to report that actual backend; Auto may use CPU.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LocalTranscriptionHardware>))]
public enum LocalTranscriptionHardware { Cpu, Auto, Vulkan, Cuda }

public sealed record WhisperConfiguration(string ExecutablePath, string ModelPath,
    string Language = "auto", int Threads = 2, string? ModelName = null,
    string? ExpectedModelSha256 = null, bool ModelLicenseAcknowledged = false,
    LocalTranscriptionHardware Hardware = LocalTranscriptionHardware.Cpu, int GpuDeviceIndex = 0,
    string? InitialPrompt = null);

public sealed record WhisperReadiness(bool Ready, string Message);

public sealed record WhisperRequest(string MeetingDirectory, string MeetingId, long MeetingDurationMs,
    string WavePath, string ExpectedWaveSha256, WhisperConfiguration Configuration, bool IsSynthetic = false);

public sealed record WhisperRunResult(string Status, string AttemptDirectory, long ElapsedMilliseconds,
    TranscriptRevision? Revision, string Message);
