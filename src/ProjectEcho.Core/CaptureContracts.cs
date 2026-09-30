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
    int RetainedFrameOffset = 0)
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
    IReadOnlyList<CapturedPacket> Packets);

public sealed record CaptureResult(
    string SessionDirectory,
    string IndexPath,
    DateTimeOffset StartedAtUtc,
    ulong EpochQpc100ns,
    long Duration100ns,
    IReadOnlyList<CapturedTrack> Tracks,
    bool Complete,
    IReadOnlyList<string> Warnings);
