using ProjectEcho.Capture.Windows;

/// <summary>Pure clock-order regression; no devices, recording, playback, or wall-clock waits.</summary>
internal static class RecordingHealthTests
{
    public static void Run()
    {
        const ulong snapshot = 10_269_960;
        Require(!RecordingSession.PacketHealthTimedOut(snapshot, snapshot + 1),
            "A packet observed after the loop snapshot must not underflow into a no-data timeout.");
        Require(!RecordingSession.PacketHealthTimedOut(snapshot, snapshot),
            "A packet observed at the snapshot must be fresh.");
        Require(!RecordingSession.PacketHealthTimedOut(snapshot + 19_999_999, snapshot),
            "A packet younger than two seconds must not time out.");
        Require(!RecordingSession.PacketHealthTimedOut(snapshot + 20_000_000, snapshot),
            "Exactly two seconds must preserve the existing strictly-greater timeout boundary.");
        Require(RecordingSession.PacketHealthTimedOut(snapshot + 20_000_001, snapshot),
            "A genuine no-packet interval beyond two seconds must remain visible.");

        // Keep the ordering comparison unsigned and exact even outside signed-long range.
        Require(!RecordingSession.PacketHealthTimedOut(ulong.MaxValue - 1, ulong.MaxValue),
            "A newer packet near the unsigned limit must remain fresh.");
        Require(RecordingSession.PacketHealthTimedOut(ulong.MaxValue, ulong.MaxValue - 20_000_001),
            "A genuine timeout near the unsigned limit must remain visible.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
