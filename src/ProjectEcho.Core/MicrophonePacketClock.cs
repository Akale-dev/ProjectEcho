namespace ProjectEcho.Core;

/// <summary>
/// Recognizes a small QPC interval deviation only when the microphone's actual
/// device frames independently prove that the two raw packets are contiguous.
/// The raw clocks, PCM and capture-thread privacy cutoffs are never rewritten.
/// This is a bounded packet interpolation rule, not a long-run drift estimate.
/// </summary>
public static class MicrophonePacketClock
{
    public const long MaximumJitter100ns = DevicePacketClock.MaximumJitter100ns;

    public static long BoundedJitter100ns(string sourceId, int sampleRate,
        CapturedPacket previous, CapturedPacket current)
        // Preserve the original microphone-only API for existing callers.
        => sourceId == "mic" ? DevicePacketClock.BoundedJitter100ns(sourceId, sampleRate, previous, current) : 0;
}
