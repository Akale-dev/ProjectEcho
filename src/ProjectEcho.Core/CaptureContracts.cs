namespace ProjectEcho.Core;

public sealed record PcmFormat(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    bool IsFloat,
    int BlockAlign,
    uint ChannelMask = 0)
{
    public void Validate()
    {
        if (SampleRate is < 8_000 or > 384_000) throw new ArgumentOutOfRangeException(nameof(SampleRate));
        if (Channels is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(Channels));
        if (BitsPerSample is not (16 or 24 or 32) || (IsFloat && BitsPerSample != 32))
            throw new NotSupportedException("样机支持 PCM16/24/32 或 Float32 原轨。");
        if (BlockAlign != Channels * (BitsPerSample / 8))
            throw new ArgumentException("音频 blockAlign 与协商格式不一致。");
    }
}

public sealed record CapturedPacket(
    long WaveFrameOffset,
    int FrameCount,
    int RawFrameCount,
    ulong DevicePosition,
    ulong QpcPosition100ns,
    int Flags,
    bool TimestampValid,
    bool DevicePositionValid = true,
    int RetainedFrameOffset = 0,
    long ClockJitter100ns = 0)
{
    // QPC can remain usable even when a process-loopback device counter is stuck.
    // Preserve both raw API values; never invent a replacement device position.
    public string GetTimestampQuality() => !TimestampValid ? "invalid"
        : DevicePositionValid ? "device_referenced" : "qpc_referenced";
}

public sealed record CapturedTrack(
    string SourceId,
    string WavePath,
    PcmFormat Format,
    long FrameCount,
    IReadOnlyList<CapturedPacket> Packets,
    string? SegmentId = null,
    string? ProcessingMode = null,
    ulong ExcludedPause100ns = 0);

public sealed record CaptureResult(
    string SessionDirectory,
    string IndexPath,
    DateTimeOffset StartedAtUtc,
    ulong EpochQpc100ns,
    long Duration100ns,
    IReadOnlyList<CapturedTrack> Tracks,
    bool Complete,
    IReadOnlyList<string> Warnings,
    string TimelinePolicy = RecordingTimeline.Elapsed);

/// <summary>Persisted mapping policy. Original device and QPC clocks are never rewritten.</summary>
public static class RecordingTimeline
{
    public const string Elapsed = "elapsed-v1";
    public const string ExcludePaused = "pause-excluded-v1";

    public static void Validate(string policy, ulong excludedPause100ns = 0)
    {
        if (policy is not (Elapsed or ExcludePaused) || (policy == Elapsed && excludedPause100ns != 0))
            throw new InvalidDataException("未知录音时间轴规则，或旧经过时间轴含暂停扣除。");
    }

    public static Int128 Position100ns(ulong qpc100ns, ulong epochQpc100ns, ulong excludedPause100ns)
    {
        Int128 position = RawPosition100ns(qpc100ns, epochQpc100ns, excludedPause100ns);
        if (position < 0) throw new InvalidDataException("原始 QPC 早于该段录音时间轴起点。");
        return position;
    }

    // A restarted device may return a raw packet beginning before native Start.
    // Keep its signed raw position; the retained frame offset must prove that no
    // pre-Start PCM is saved. Boundary positions themselves remain nonnegative.
    public static Int128 RawPosition100ns(ulong qpc100ns, ulong epochQpc100ns, ulong excludedPause100ns)
        => (Int128)qpc100ns - epochQpc100ns - excludedPause100ns;
}
