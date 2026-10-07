using ProjectEcho.Core.Transcription;
using System.Text.Json.Serialization;

namespace ProjectEcho.Core.Diarization;

/// <summary>Meeting-local identities. IDs never contain inferred names or audio-source identities.</summary>
public sealed record MeetingSpeaker(string SpeakerId, string Code, string? DisplayName = null)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Code : DisplayName;
}

public sealed record SpeakerAudioBinding(string InputWaveSha256, string? SourceMixSha256,
    int SampleRate, long FrameCount, string TimelinePolicy);

/// <summary>Times use the selected saved audio clock, with no second pause offset.</summary>
public sealed record SpeakerInterval(long StartMs, long EndMs, string? SpeakerId,
    string Quality = "estimated", bool Overlap = false);

public sealed record SpeakerEngineEvidence(string Engine, string Version, string ExecutableSha256,
    string SegmentationModelSha256, string EmbeddingModelSha256, int? KnownSpeakerCount,
    double ClusteringThreshold, string ActualDevice, string ClusteringPolicy,
    long ElapsedMilliseconds = 0, long PeakWorkingSetBytes = 0);

public sealed record SpeakerAnalysis(int SchemaVersion, string MeetingId, string AnalysisId,
    DateTimeOffset CreatedAt, SpeakerAudioBinding Audio, SpeakerEngineEvidence Engine,
    IReadOnlyList<MeetingSpeaker> Speakers, IReadOnlyList<SpeakerInterval> Intervals,
    IReadOnlyList<string> Warnings, bool IsSynthetic = false);

public sealed record SpeakerCandidateBoundary(long TimeMs, string? BeforeSpeakerId, string? AfterSpeakerId);

/// <summary>UTF-16 source ranges partition the source. An edited group shares one coarse source range;
/// it does not claim a character/time mapping for replacement text. Text never includes labels.</summary>
public sealed record SpeakerSegment(string SegmentId, string SourceSegmentId, int TextStart, int TextLength,
    long? StartMs, long? EndMs, string Text, string? SpeakerId, string Status,
    IReadOnlyList<SpeakerCandidateBoundary> CandidateBoundaries, string TimingSource = "source-subtitle",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TextEditGroupId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ManualTiming = false)
{
    public bool NeedsConfirmation => Status != "assigned" && Status != "manual";
}

public sealed record SpeakerCorrection(string Operation, DateTimeOffset CreatedAt,
    IReadOnlyList<string> SegmentIds, string? FromSpeakerId = null, string? ToSpeakerId = null,
    string? DisplayName = null, int? TextOffset = null, long? TimeMs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreviousText = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReplacementText = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SpeakerSubtitleEdit>? SubtitleEdits = null);

public sealed record SpeakerSubtitleEdit(string SegmentId, string PreviousText, string ReplacementText,
    long? PreviousStartMs, long? PreviousEndMs, long? StartMs, long? EndMs);

public sealed record SpeakerRevision(int SchemaVersion, string MeetingId, string RevisionId,
    string? ParentRevisionId, DateTimeOffset CreatedAt, string SourceTranscriptRevisionId,
    string SourceTranscriptSha256, string AnalysisId, string AnalysisSha256, SpeakerAudioBinding Audio,
    long MeetingDurationMs, IReadOnlyList<MeetingSpeaker> Speakers, IReadOnlyList<SpeakerSegment> Segments,
    IReadOnlyList<SpeakerCorrection> Corrections, IReadOnlyList<string> Warnings, bool IsSynthetic = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParentRevisionSha256 = null);

/// <summary>An internal boundary copied from exact-text validated acoustic evidence, never interpolation.</summary>
public sealed record SpeakerTextBoundary(string SegmentId, int TextOffset, long LeftEndMs, long RightStartMs,
    string Source);

public sealed record SpeakerMappingEvidence(string TranscriptRevisionId, string TranscriptSha256,
    SpeakerAudioBinding Audio, IReadOnlyList<SpeakerTextBoundary> Boundaries, IReadOnlyList<string> Warnings);

public static class SpeakerDisplay
{
    public static string Label(SpeakerRevision revision, SpeakerSegment segment) => segment.SpeakerId is { } id
        ? revision.Speakers.Single(speaker => speaker.SpeakerId == id).Label
        : segment.Status == "multiple-speakers" ? "多人/待确认" : "待确认";

    public static TranscriptRevision AsTranscript(SpeakerRevision revision, TranscriptRevision source) => source with
    {
        RevisionId = revision.RevisionId, ParentRevisionId = revision.SourceTranscriptRevisionId,
        ImportedAt = revision.CreatedAt, Provider = source.Provider + " · 同场说话人",
        ReviewNote = revision.Corrections.Count > 0 ? "说话人人工修订" : "同场说话人分析",
        // Display projection only: source tokens cannot describe changed text or speaker-split IDs.
        Alignment = null,
        Segments = revision.Segments.Select(segment => new TranscriptSegment(segment.SegmentId,
            segment.StartMs, segment.EndMs, Label(revision, segment), "mixed", segment.Text,
            segment.StartMs is null ? "untimed" : "approximate")).ToArray(),
        Warnings = source.Warnings.Concat(revision.Warnings).ToArray()
    };
}
