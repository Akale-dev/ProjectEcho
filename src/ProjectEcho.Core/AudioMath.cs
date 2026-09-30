namespace ProjectEcho.Core;

/// <summary>Conversions for WASAPI's 100 ns QPC values. These are not raw Stopwatch ticks.</summary>
public static class AudioClock
{
    public const long UnitsPerSecond = 10_000_000;

    public static long CounterTo100ns(long counter, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(counter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        return checked((long)((Int128)counter * UnitsPerSecond / frequency));
    }

    public static long SessionMicroseconds(ulong packetQpc100ns, ulong epochQpc100ns)
        => checked((long)(((Int128)packetQpc100ns - epochQpc100ns) / 10));

    /// <summary>Nearest output frame; keeps arithmetic integral even for large boot times.</summary>
    public static long FrameAt(ulong qpc100ns, ulong epochQpc100ns, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var scaled = ((Int128)qpc100ns - epochQpc100ns) * sampleRate;
        var rounded = scaled >= 0 ? scaled + UnitsPerSecond / 2 : scaled - UnitsPerSecond / 2;
        return checked((long)(rounded / UnitsPerSecond));
    }

    /// <summary>Counts only whole frames ending at or before an authorized cutoff.</summary>
    public static int CompleteFramesBefore(ulong packetQpc100ns, ulong cutoffQpc100ns, int sampleRate, int rawFrameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegative(rawFrameCount);
        if (cutoffQpc100ns <= packetQpc100ns) return 0;
        var count = (UInt128)(cutoffQpc100ns - packetQpc100ns) * (uint)sampleRate / UnitsPerSecond;
        return (int)UInt128.Min(count, (uint)rawFrameCount);
    }
}

public static class AudioLevels
{
    public static float Peak(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample)) continue;
            peak = Math.Max(peak, Math.Abs(sample));
        }
        return Math.Min(1, peak);
    }
}

public static class DiskBudget
{
    public static long PcmBytes(int sampleRate, int blockAlign, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockAlign);
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        var frames = ((Int128)duration.Ticks * sampleRate + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond;
        return checked((long)(frames * blockAlign));
    }
}
