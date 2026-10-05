namespace ProjectEcho.Capture.Windows;

/// <summary>
/// An immutable clock snapshot shared with the writer. Original QPC values are
/// untouched; only explicit inactive intervals are excluded from recording time.
/// Mutations belong to the capture thread, and readers always see one coherent state.
/// </summary>
internal sealed class RecordingTimeClock(ulong epoch)
{
    private sealed record State(ulong Excluded, ulong? PausedAt);
    private State state = new(0, null);
    internal bool IsPaused => Volatile.Read(ref state).PausedAt.HasValue;
    internal ulong Excluded100ns => Volatile.Read(ref state).Excluded;

    internal long Elapsed100ns(ulong rawQpc)
    {
        var snapshot = Volatile.Read(ref state);
        ulong position = snapshot.PausedAt is { } pause ? Math.Min(rawQpc, pause) : rawQpc;
        Int128 elapsed = (Int128)position - epoch - snapshot.Excluded;
        return elapsed <= 0 ? 0 : checked((long)elapsed);
    }

    internal void Pause(ulong rawBoundary)
    {
        var snapshot = Volatile.Read(ref state);
        if (snapshot.PausedAt is not null) return;
        if ((Int128)rawBoundary < (Int128)epoch + snapshot.Excluded)
            throw new InvalidDataException("暂停时钟早于已确认录音时间轴。");
        Volatile.Write(ref state, snapshot with { PausedAt = rawBoundary });
    }

    internal ulong Resume(ulong rawBoundary)
    {
        var snapshot = Volatile.Read(ref state);
        if (snapshot.PausedAt is not { } pause) return snapshot.Excluded;
        if (rawBoundary < pause) throw new InvalidDataException("恢复时钟早于暂停边界。");
        ulong excluded = checked(snapshot.Excluded + rawBoundary - pause);
        Volatile.Write(ref state, new(excluded, null));
        return excluded;
    }

    // A cancelled/unstarted prepared segment can still have an auditable raw
    // close boundary. Map that boundary to the frozen time without adding silence.
    internal ulong ExcludedAtFrozenBoundary(ulong rawBoundary)
    {
        var snapshot = Volatile.Read(ref state);
        if (snapshot.PausedAt is not { } pause) return snapshot.Excluded;
        if (rawBoundary < pause) throw new InvalidDataException("未启动段的关闭边界早于暂停。");
        return checked(snapshot.Excluded + rawBoundary - pause);
    }
}
