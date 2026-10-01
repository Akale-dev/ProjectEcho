using System.Diagnostics;
using ProjectEcho.Core;

namespace ProjectEcho.Capture.Windows;

/// <summary>Fixed counters only; no per-packet records, formatting, locks, or allocations.</summary>
internal sealed class CaptureReadMetrics
{
    private long lastPollTicks;
    private bool polled;
    internal long PollCount, PacketCount, MaximumPollInterval100ns, MaximumBufferHold100ns;
    internal long PollIntervalsOver20Ms, BufferHoldsOver2Ms;

    internal void Poll(long ticks)
    {
        if (polled)
        {
            long interval = Elapsed100ns(lastPollTicks, ticks);
            if (interval > MaximumPollInterval100ns) MaximumPollInterval100ns = interval;
            if (interval > 200000) PollIntervalsOver20Ms++;
        }
        lastPollTicks = ticks; polled = true; PollCount++;
    }

    internal void Released(long heldAtTicks, long releasedAtTicks)
    {
        long interval = Elapsed100ns(heldAtTicks, releasedAtTicks);
        if (interval > MaximumBufferHold100ns) MaximumBufferHold100ns = interval;
        if (interval > 20000) BufferHoldsOver2Ms++;
        PacketCount++;
    }

    private static long Elapsed100ns(long before, long after) => after > before
        ? (long)(((Int128)after - before) * AudioClock.UnitsPerSecond / Stopwatch.Frequency) : 0;
}
