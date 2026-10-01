using ProjectEcho.Core;

namespace ProjectEcho.Capture.Windows;

public sealed record RecordingOptions(CaptureOptions Selection, int MaximumSeconds = 28800, int ExpectedMinutes = 120,
    bool EnableMultimediaScheduling = true);
public sealed record RecordingAck(string RequestId, string Operation, bool Paused, bool MicrophoneEnabled,
    long EffectiveSessionUs, string? Message = null);
public sealed record RecordingHealth(long ElapsedUs, bool Paused, bool MicrophoneEnabled,
    float RemotePeak, float MicPeak, long RemoteFrames, long MicFrames, string RemoteStatus, string MicStatus,
    long LastCheckpointUs, long AvailableBytes);
public sealed record RecordingResult(string SessionDirectory, string ManifestPath, bool StorageComplete,
    long DurationUs, IReadOnlyList<string> Warnings, RecordingDiagnostics? Diagnostics = null);

/// <summary>Service timings are diagnostics only, never substitutes for packet QPC/frame counts.</summary>
public sealed record RecordingSourceDiagnostics(int? BufferFrames, long? StreamLatency100ns, int ScratchCapacityBytes,
    long PollCount, long PacketCount, long MaximumPollInterval100ns, long MaximumBufferHold100ns,
    long PollIntervalsOver20Ms, long BufferHoldsOver2Ms);
public sealed record RecordedSourceDiagnostics(string SegmentId, string SourceId, string? ProcessingMode, RecordingSourceDiagnostics Timing);
public sealed record RecordingDiagnostics(CaptureSchedulingDiagnostic? Scheduling, long MaximumLoopInterval100ns,
    long CaptureThreadAllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections,
    double ProcessGcPauseMilliseconds, IReadOnlyList<RecordedSourceDiagnostics> Sources);

/// <summary>One actual WASAPI packet, with only authorized whole frames copied into PCM.</summary>
internal sealed record RecordingBlock(byte[] Pcm, int RawFrameCount, int FrameCount, ulong DevicePosition,
    ulong QpcPosition100ns, int Flags, bool TimestampValid, bool DevicePositionValid, int DiscardedFrames);

internal interface IRecordingAudioSource : IDisposable
{
    string Id { get; }
    NAudio.Wave.WaveFormat WaveFormat { get; }
    PcmFormat Format { get; }
    string? ProcessingMode => null;
    RecordingSourceDiagnostics? Diagnostics => null;
    WaitHandle? PacketReady => null;
    void Start();
    void Stop();
    RecordingBlock? Read(ulong epochQpc100ns, ulong? cutoffQpc100ns);
}

internal interface IRecordingSourceFactory
{
    void Preflight(RecordingOptions options);
    IRecordingAudioSource OpenRemote(CaptureOptions options);
    IRecordingAudioSource OpenMicrophone(string deviceId);
    void ValidateRemote(CaptureOptions options);
}
