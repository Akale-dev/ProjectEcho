using System.Text.Json;
using ProjectEcho.Core;

/// <summary>Pure metadata fixtures. These tests neither open devices nor prove recorded storage.</summary>
internal static class ClockCapabilityTests
{
    public static void Run()
    {
        const ulong epoch = 100_000;
        var format = new PcmFormat(48_000, 2, 32, true, 8);
        CapturedPacket[] zeroDevice =
        [
            new(0, 480, 480, 0, epoch, 0, true),
            new(480, 480, 480, 0, epoch + 100_000, 2, true, false),
            new(960, 480, 480, 0, epoch + 200_000, 0, true, false)
        ];
        CapturedTrack Track(CapturedPacket[] packets) => new("remote", "not-a-file", format,
            packets.Sum(packet => (long)packet.FrameCount), packets);
        ClockCapabilityReport Evaluate(CapturedPacket[] packets, ClockSourceKind kind = ClockSourceKind.ProcessTree)
            => ClockCapabilityEvaluator.Evaluate(kind, Track(packets), epoch);
        void Reject(CapturedPacket[] packets, string code, ClockSourceKind kind = ClockSourceKind.ProcessTree)
        {
            var report = Evaluate(packets, kind);
            Require(report.Capability == ClockCapability.Invalid && report.Issues.Any(issue => issue.Code == code),
                $"Expected invalid {code}, got {report.Capability}: {string.Join(',', report.Issues.Select(issue => issue.Code))}");
            Require(!report.IsPacketTimelineUsable, "Invalid metadata cannot supply a usable timeline.");
        }
        CapturedPacket[] Replace(int index, CapturedPacket packet)
        {
            var copy = zeroDevice.ToArray(); copy[index] = packet; return copy;
        }

        var before = JsonSerializer.Serialize(zeroDevice);
        var limited = Evaluate(zeroDevice);
        Require(limited.Capability == ClockCapability.QpcOnlyLimited && limited.IsPacketTimelineUsable
            && !limited.DevicePositionAvailable && limited.AllDevicePositionsZero
            && limited.PacketCount == 3 && limited.RetainedFrameCount == 1440,
            "Known process loopback with strictly continuous QPC must expose its limitation.");
        Require(!limited.CrossSourceSynchronizationValidated && !limited.OriginalStorageValidated,
            "Clock metadata must never assert storage or two-source synchronization.");
        Require(before == JsonSerializer.Serialize(zeroDevice), "Raw API values were changed.");
        foreach (var kind in new[] { ClockSourceKind.Unknown, ClockSourceKind.EndpointLoopback, ClockSourceKind.Microphone })
            Reject(zeroDevice, "device_overlap_or_stall", kind);

        CapturedPacket[] full = zeroDevice.Select((packet, index) => packet with
            { DevicePosition = (ulong)(index * 480), DevicePositionValid = true }).ToArray();
        Require(Evaluate(full).Capability == ClockCapability.DeviceReferenced, "Normal counters failed.");
        Require(Evaluate([zeroDevice[0]]).Capability == ClockCapability.InsufficientEvidence,
            "A single initial zero must not assert a missing or healthy counter.");
        Require(Evaluate([]).Capability == ClockCapability.NoPackets, "No callback must remain distinct from silence.");
        Require(Evaluate(zeroDevice.Select(packet => packet with { Flags = 2 }).ToArray()).Capability == ClockCapability.QpcOnlyLimited,
            "Actual silent packets still have valid timeline metadata.");
        Reject(Replace(1, zeroDevice[1] with { QpcPosition100ns = epoch }), "qpc_not_increasing");
        Reject(Replace(1, zeroDevice[1] with { QpcPosition100ns = epoch - 1 }), "qpc_invalid");
        Reject(Replace(1, zeroDevice[1] with { QpcPosition100ns = 0 }), "qpc_invalid");
        Reject(Replace(1, zeroDevice[1] with { QpcPosition100ns = epoch + 99_998 }), "qpc_overlap");
        Reject(Replace(1, zeroDevice[1] with { QpcPosition100ns = epoch + 100_002 }), "qpc_gap");
        Require(Evaluate(Replace(1, zeroDevice[1] with { QpcPosition100ns = epoch + 99_999 })).Capability == ClockCapability.QpcOnlyLimited,
            "Only one 100ns unit of timestamp quantization is permitted.");
        Reject(Replace(1, zeroDevice[1] with { TimestampValid = false }), "qpc_invalid");
        Reject(Replace(1, zeroDevice[1] with { Flags = 4 }), "timestamp_error");
        Reject(Replace(1, zeroDevice[1] with { Flags = 1 }), "data_discontinuity");
        Reject(Replace(1, zeroDevice[1] with { Flags = 8 }), "unknown_buffer_flags");
        Reject(Replace(1, zeroDevice[1] with { WaveFrameOffset = 479 }), "wave_frame_offset_not_contiguous");
        Reject(Replace(1, zeroDevice[1] with { FrameCount = 481 }), "invalid_frame_count");
        Reject(Replace(1, zeroDevice[1] with { RawFrameCount = 0 }), "invalid_frame_count");
        Reject(Replace(1, zeroDevice[1] with { DevicePosition = 480 }), "device_regression");
        var repeatedNonzero = full.Select(packet => packet with { DevicePosition = 480 }).ToArray();
        Reject(repeatedNonzero, "device_overlap_or_stall");
        var hiddenBadFlag = full.ToArray(); hiddenBadFlag[1] = full[1] with { DevicePositionValid = false };
        Reject(hiddenBadFlag, "device_position_marked_invalid");
        var missingFrames = full.ToArray(); missingFrames[2] = full[2] with { DevicePosition = 961 };
        Reject(missingFrames, "device_gap");
        var trimTail = Replace(2, zeroDevice[2] with { FrameCount = 17 });
        Require(Evaluate(trimTail).Capability == ClockCapability.QpcOnlyLimited, "A saved pre-cutoff prefix at segment end is valid.");
        Reject(Replace(1, zeroDevice[1] with { FrameCount = 17 }), "trimmed_packet_before_segment_end");
        var mismatched = ClockCapabilityEvaluator.Evaluate(ClockSourceKind.ProcessTree,
            Track(zeroDevice) with { FrameCount = 1441 }, epoch);
        Require(mismatched.Capability == ClockCapability.Invalid, "Track frame mismatch was accepted.");
        var emptyWithFrames = ClockCapabilityEvaluator.Evaluate(ClockSourceKind.ProcessTree,
            Track([]) with { FrameCount = 1 }, epoch);
        Require(emptyWithFrames.Capability == ClockCapability.Invalid, "Empty packet list with frames was accepted.");

        const ulong largeEpoch = 18_000_000_000_000_000_000;
        var largeQpc = zeroDevice.Select(packet => packet with { QpcPosition100ns = largeEpoch + packet.QpcPosition100ns - epoch }).ToArray();
        Require(ClockCapabilityEvaluator.Evaluate(ClockSourceKind.ProcessTree, Track(largeQpc), largeEpoch).Capability
            == ClockCapability.QpcOnlyLimited, "UInt64 QPC values must retain precision.");
        var overflowDevice = full.ToArray();
        overflowDevice[0] = full[0] with { DevicePosition = ulong.MaxValue - 1 };
        overflowDevice[1] = full[1] with { DevicePosition = ulong.MaxValue };
        Reject(overflowDevice, "device_overlap_or_stall");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
