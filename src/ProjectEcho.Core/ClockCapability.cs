namespace ProjectEcho.Core;

/// <summary>The caller must derive this from the recorded source selection, never its name or PCM.</summary>
public enum ClockSourceKind { Unknown, ProcessTree, EndpointLoopback, Microphone }

public enum ClockCapability { NoPackets, InsufficientEvidence, DeviceReferenced, QpcOnlyLimited, Invalid }

public sealed record ClockCapabilityIssue(int? PacketIndex, string Code);

/// <summary>
/// An offline observation of one uninterrupted segment's original packets, not a storage verdict.
/// It does not establish hardware accuracy, cross-source alignment or long-run drift bounds.
/// </summary>
public sealed record ClockCapabilityReport(
    ClockSourceKind SourceKind,
    ClockCapability Capability,
    int PacketCount,
    long RetainedFrameCount,
    bool AllDevicePositionsZero,
    IReadOnlyList<ClockCapabilityIssue> Issues)
{
    public bool IsPacketTimelineUsable => Capability is ClockCapability.DeviceReferenced or ClockCapability.QpcOnlyLimited;
    public bool DevicePositionAvailable => Capability == ClockCapability.DeviceReferenced;
    public bool CrossSourceSynchronizationValidated => false;
    public bool OriginalStorageValidated => false;
}

/// <summary>
/// Evaluates original (unsplit) API packets for one continuous capture segment only.
/// Pass a new segment after pause, device reset or format change; do not pass duplicated chunk slices.
/// Preserves all inputs. In particular it never replaces a missing device position with counted frames.
/// </summary>
public static class ClockCapabilityEvaluator
{
    // API timestamps are integral 100ns units. Rounding two ideal timestamps can differ by one unit.
    // This is not a configurable jitter/drift allowance: larger gaps or overlaps fail this classifier.
    public const int TimestampQuantizationTolerance100ns = 1;

    public static ClockCapabilityReport Evaluate(ClockSourceKind sourceKind, CapturedTrack track, ulong epochQpc100ns)
    {
        ArgumentNullException.ThrowIfNull(track);
        track.Format.Validate();
        var issues = new List<ClockCapabilityIssue>();
        void Add(int? index, string code) => issues.Add(new(index, code));

        if (track.Packets.Count == 0)
        {
            if (track.FrameCount != 0) Add(null, "track_frame_count_without_packets");
            return new(sourceKind, issues.Count == 0 ? ClockCapability.NoPackets : ClockCapability.Invalid,
                0, 0, false, issues.ToArray());
        }

        bool allZero = track.Packets.All(packet => packet.DevicePosition == 0);
        // Only this explicitly selected API mode may describe an all-zero sequence as a limitation.
        // A single initial zero is normal and gives insufficient evidence about subsequent progress.
        bool qpcOnlyCandidate = sourceKind == ClockSourceKind.ProcessTree && allZero && track.Packets.Count >= 2;
        Int128 retainedFrames = 0;
        CapturedPacket? previous = null;
        for (int index = 0; index < track.Packets.Count; index++)
        {
            var packet = track.Packets[index];
            if (packet.RetainedFrameOffset != 0) Add(index, "requires_original_unsplit_packets");
            if (packet.RawFrameCount <= 0 || packet.FrameCount <= 0 || packet.FrameCount > packet.RawFrameCount)
                Add(index, "invalid_frame_count");
            if (packet.WaveFrameOffset < 0 || packet.WaveFrameOffset != retainedFrames)
                Add(index, "wave_frame_offset_not_contiguous");
            retainedFrames += packet.FrameCount;

            if (!packet.TimestampValid || packet.QpcPosition100ns == 0 || packet.QpcPosition100ns < epochQpc100ns)
                Add(index, "qpc_invalid");
            if ((packet.Flags & 4) != 0) Add(index, "timestamp_error");
            if ((packet.Flags & 1) != 0) Add(index, "data_discontinuity");
            if ((packet.Flags & ~7) != 0) Add(index, "unknown_buffer_flags");
            if (!qpcOnlyCandidate && !packet.DevicePositionValid) Add(index, "device_position_marked_invalid");

            if (previous is not null)
            {
                if (packet.QpcPosition100ns <= previous.QpcPosition100ns)
                    Add(index, "qpc_not_increasing");
                // Compare rational durations without floating point, rounding or UInt64 wraparound.
                var qpcDeltaScaled = ((Int128)packet.QpcPosition100ns - previous.QpcPosition100ns) * track.Format.SampleRate;
                var expectedDeltaScaled = (Int128)previous.RawFrameCount * AudioClock.UnitsPerSecond;
                var difference = qpcDeltaScaled - expectedDeltaScaled;
                var toleranceScaled = (Int128)TimestampQuantizationTolerance100ns * track.Format.SampleRate;
                if (difference < -toleranceScaled) Add(index, "qpc_overlap");
                if (difference > toleranceScaled) Add(index, "qpc_gap");
                if (previous.FrameCount != previous.RawFrameCount)
                    Add(index - 1, "trimmed_packet_before_segment_end");

                if (!qpcOnlyCandidate)
                {
                    var nextDevice = (Int128)previous.DevicePosition + previous.RawFrameCount;
                    if (packet.DevicePosition < previous.DevicePosition) Add(index, "device_regression");
                    else if ((Int128)packet.DevicePosition < nextDevice) Add(index, "device_overlap_or_stall");
                    else if ((Int128)packet.DevicePosition > nextDevice) Add(index, "device_gap");
                }
            }
            previous = packet;
        }

        if (retainedFrames < 0 || retainedFrames > long.MaxValue)
            Add(null, "retained_frame_count_overflow");
        if (retainedFrames != track.FrameCount) Add(null, "track_frame_count_mismatch");
        var capability = issues.Count != 0 ? ClockCapability.Invalid
            : track.Packets.Count < 2 ? ClockCapability.InsufficientEvidence
            : qpcOnlyCandidate ? ClockCapability.QpcOnlyLimited : ClockCapability.DeviceReferenced;
        if (capability == ClockCapability.QpcOnlyLimited)
            Add(null, "process_loopback_device_position_unavailable");
        return new(sourceKind, capability, track.Packets.Count,
            retainedFrames >= 0 && retainedFrames <= long.MaxValue ? (long)retainedFrames : 0,
            allZero, issues.ToArray());
    }
}
