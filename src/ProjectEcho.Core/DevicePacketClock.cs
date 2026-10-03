namespace ProjectEcho.Core;

/// <summary>
/// Recognizes a bounded deviation between adjacent raw API QPC anchors only
/// when independent device positions prove contiguous frames. Endpoint loopback
/// and microphone streams can provide this evidence; QPC-only streams cannot.
/// No raw clock, PCM, capture cutoff or long-run drift estimate is rewritten.
/// </summary>
public static class DevicePacketClock
{
    public const long MaximumJitter100ns = 20_000;

    public static long BoundedJitter100ns(string sourceId, int sampleRate,
        CapturedPacket previous, CapturedPacket current)
    {
        if (sourceId is not ("mic" or "remote") || sampleRate <= 0 ||
            !previous.TimestampValid || !current.TimestampValid ||
            !previous.DevicePositionValid || !current.DevicePositionValid ||
            (current.Flags & 5) != 0 || current.QpcPosition100ns <= previous.QpcPosition100ns ||
            previous.RetainedFrameOffset + (long)previous.FrameCount != previous.RawFrameCount ||
            (UInt128)previous.DevicePosition + (uint)previous.RawFrameCount != current.DevicePosition)
            return 0;

        Int128 deviation = ((Int128)current.QpcPosition100ns - previous.QpcPosition100ns) * sampleRate
            - (Int128)previous.RawFrameCount * AudioClock.UnitsPerSecond;
        Int128 absolute = Int128.Abs(deviation);
        if (absolute <= AudioClock.UnitsPerSecond ||
            absolute > (Int128)MaximumJitter100ns * sampleRate ||
            absolute * 4 > (Int128)previous.RawFrameCount * AudioClock.UnitsPerSecond)
            return 0;
        return checked((long)(deviation / sampleRate));
    }
}
