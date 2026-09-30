// DESIGN CONTRACTS ONLY. Not a complete capture implementation; no real device API
// names are assumed. Adapt and compile in M0 after validating current NAudio APIs.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace ProjectEcho.Design;

public enum CaptureScope { ProcessTree, DedicatedBrowser, ExistingBrowser, Endpoint, BrowserTab }
public enum SourceKind { Remote, Microphone }
public enum TimestampQuality { DeviceReferenced, Estimated, Invalid }
public sealed record CaptureCapabilities(bool BuildEligible, bool ActivationTested,
    bool CanAccessDeviceTimestamps, string? FailureReason);
public sealed record SourceIdentity(string SourceId, SourceKind Kind, CaptureScope Scope,
    int? ProcessId, long? ProcessStartUtcTicks, string? EndpointId);
public sealed record SourceFormat(int SampleRate, int Channels, string Encoding,
    int BlockAlign, uint? ChannelMask);
public sealed record SessionEpoch(ulong QpcPosition100ns, DateTimeOffset StartedAtUtc,
    string DisplayTimeZone);
public sealed record ClockAnchor(ulong? DeviceFramePosition, ulong? QpcPosition100ns,
    long SessionStartUs, TimestampQuality TimestampQuality);
public sealed record CaptureSegment(string SegmentId, string SourceId,
    SourceFormat Format, ClockAnchor Anchor);
[Flags]
public enum PacketFlags { None = 0, DataDiscontinuity = 1, Silent = 2, TimestampError = 4 }

// Each block owns its bytes. A production implementation may pool memory, but must
// define ownership/disposal and never reuse storage while the writer still reads it.
// Raw positions/count survive trimming or splitting. OwnedBytes contains only the
// retained complete frames; SILENT may be materialized as zero PCM, never dereference
// an absent API buffer. JSON uint64 positions are decimal strings, not doubles.
public sealed record CaptureBlock(string SourceId, string SegmentId,
    byte[] OwnedBytes, int RawFrameCount, int RetainedFrameOffset, int FrameCount,
    ulong? DeviceFramePosition, ulong? QpcPosition100ns, long SessionStartUs,
    TimestampQuality TimestampQuality, PacketFlags Flags);
public enum BoundaryOperation { Stop, Pause, MicOff }
public sealed record BoundaryRequest(string RequestId, BoundaryOperation Operation);
// Emitted by the capture thread after capture stops/releases and final callbacks
// settle. Each source has its own effective boundary. It is not a Saved receipt.
public sealed record BoundaryAck(string RequestId, BoundaryOperation Operation,
    string SegmentId, long EffectiveSessionUs, ulong? FirstForbiddenDeviceFrame,
    long DiscardedFrameCount, string? DiscardReason);
// Index metadata must never serialize CaptureBlock.OwnedBytes into JSON/logs.
public sealed record StoredPacket(long Sequence, string SegmentId, string ChunkId,
    string RelativePath, long ChunkFrameOffset, int RawFrameCount,
    int RetainedFrameOffset, int FrameCount, ulong? DeviceFramePosition,
    ulong? QpcPosition100ns, long SessionStartUs,
    TimestampQuality TimestampQuality, PacketFlags Flags);
public sealed record ChunkWatermark(string ChunkId, string RelativePath,
    long CommittedFrameCount, long CommittedPcmBytes, long DataOffsetBytes);
public sealed record Checkpoint(long CoveredThroughSequence,
    IReadOnlyList<ChunkWatermark> Chunks);
public sealed record TimelineSpan(long FileStartMs, long FileEndMs,
    long SessionStartMs, long SessionEndMs);
public sealed record Gap(long StartUs, long EndUs, string? SourceId, string Reason);
public sealed record SplitOptions(long MaxFileBytes, int MaxDurationSeconds, int OverlapSeconds);

public interface IAudioSource : IAsyncDisposable
{
    SourceIdentity Identity { get; }
    // Start only after explicit action. No mic client is started while mic is off.
    // A normal Stop is a capture-thread boundary, not cancellation of this reader
    // or the writer; drain all verifiable authorized complete frames first.
    IAsyncEnumerable<CaptureBlock> ReadAsync(CancellationToken cancellationToken);
    Task<BoundaryAck> ApplyBoundaryAsync(BoundaryRequest request,
        CancellationToken cancellationToken);
}
public interface ISessionStore
{
    Task AppendSegmentAsync(CaptureSegment segment, CancellationToken cancellationToken);
    Task AppendBlockAsync(CaptureBlock block, CancellationToken cancellationToken);
    // Writer drains/records all authorized packets for this boundary before adding
    // the journal boundary row. It supplies acceptedThroughSequence from its own
    // assigned index order; the earlier capture/UI ack does not guess that sequence.
    Task AppendBoundaryAsync(BoundaryAck boundary, CancellationToken cancellationToken);
    Task AppendGapAsync(Gap gap, CancellationToken cancellationToken);
    // Flush PCM and index, append checkpoint, then flush the checkpoint. Recovery
    // needs the disk only, and accepts no more than this verified frame watermark.
    Task<Checkpoint> FlushCheckpointAsync(CancellationToken cancellationToken);
    // Return only after accepted queues drain, final checkpoint and WAV/header/hash
    // finalization succeed and the manifest is replaced. Failure must not show Saved.
    Task FinalizeAsync(CancellationToken cancellationToken);
}
public interface ITimelineRenderer
{
    // Keep elapsed-time gaps by default. Render must retain the longest valid tail.
    Task<string> RenderMonoMixAsync(string meetingManifestPath, CancellationToken cancellationToken);
}
public interface IUploadExport
{
    // Returns a manifest for local files. Never transmits anything to the network.
    Task<string> ExportAsync(string renderPath, SplitOptions options, CancellationToken cancellationToken);
}
