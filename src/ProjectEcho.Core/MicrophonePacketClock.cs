namespace ProjectEcho.Core;

/// <summary>
/// Recognizes a small QPC interval deviation only when the microphone's actual
/// device frames independently prove that the two raw packets are contiguous.
/// The raw clocks, PCM and capture-thread privacy cutoffs are never rewritten.
/// This is a bounded packet interpolation rule, not a long-run drift estimate.
/// </summary>
public static class MicrophonePacketClock
{
    public const long MaximumJitter100ns = 20_000; // 2 ms between adjacent API anchors.

    public static long BoundedJitter100ns(string sourceId, int sampleRate,
        CapturedPacket previous, CapturedPacket current)
    {
        if (sourceId != "mic" || sampleRate <= 0 ||
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
