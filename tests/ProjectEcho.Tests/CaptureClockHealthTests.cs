using ProjectEcho.Core;

/// <summary>Raw-position checks only; does not enumerate, activate or read an audio device.</summary>
internal static class CaptureClockHealthTests
{
    public static void Run()
    {
        const ulong epoch = 100_000;
        var normal = new CaptureClockHealth();
        Check(normal.Observe(0, 100_000, 480, false, epoch), true, true, "first zero position");
        Check(normal.Observe(480, 200_000, 480, false, epoch), true, true, "next complete packet");
        Check(normal.Observe(960, 300_000, 480, false, epoch), true, true, "third complete packet");
        Require(normal.IsHealthy && !normal.HasInvalidQpc && !normal.HasInvalidDevicePosition,
            "Normal consecutive packets must remain healthy.");

        var zeroDevice = new CaptureClockHealth();
        zeroDevice.Observe(0, 100_000, 480, false, epoch);
        Check(zeroDevice.Observe(0, 200_000, 480, false, epoch), true, false, "stalled device position");
        Check(zeroDevice.Observe(0, 300_000, 480, false, epoch), true, false, "repeated zero device position");
        Require(zeroDevice.HasInvalidDevicePosition && !zeroDevice.HasInvalidQpc && !zeroDevice.IsHealthy,
            "A stalled device position must not invalidate otherwise valid QPC.");
        Check(zeroDevice.Observe(480, 400_000, 480, false, epoch), true, true, "device position resumes");
        Require(zeroDevice.HasInvalidDevicePosition && !zeroDevice.IsHealthy,
            "Later valid positions must not clear the session failure.");

        var deviceRegression = new CaptureClockHealth();
        deviceRegression.Observe(960, 100_000, 480, false, epoch);
        Check(deviceRegression.Observe(480, 200_000, 480, false, epoch), true, false, "device regression");

        var qpcRegression = new CaptureClockHealth();
        qpcRegression.Observe(0, 200_000, 480, false, epoch);
        Check(qpcRegression.Observe(480, 199_999, 480, false, epoch), false, true, "QPC regression");
        Check(qpcRegression.Observe(960, 300_000, 480, false, epoch), true, true, "QPC resumes");
        Require(qpcRegression.HasInvalidQpc && !qpcRegression.HasInvalidDevicePosition,
            "QPC failure must latch independently from device position quality.");

        var repeatedQpc = new CaptureClockHealth();
        repeatedQpc.Observe(0, 100_000, 480, false, epoch);
        Check(repeatedQpc.Observe(480, 100_000, 480, false, epoch), false, true, "QPC did not advance");
        Check(repeatedQpc.Observe(960, 300_000, 480, false, epoch), true, true, "QPC advances again");
        Require(repeatedQpc.HasInvalidQpc && !repeatedQpc.HasInvalidDevicePosition && !repeatedQpc.IsHealthy,
            "Repeated QPC must fail clock health even when device positions advance, and remain latched.");

        var timestampError = new CaptureClockHealth();
        Check(timestampError.Observe(0, 100_000, 480, true, epoch), false, false, "timestamp error flag");
        Check(timestampError.Observe(480, 200_000, 480, false, epoch), true, true, "valid packet after flag");
        Require(timestampError.HasInvalidQpc && timestampError.HasInvalidDevicePosition,
            "The timestamp-error flag must remain latched for both positions.");

        Check(new CaptureClockHealth().Observe(-1, 100_000, 480, false, epoch), true, false, "negative device position");
        Check(new CaptureClockHealth().Observe(0, -1, 480, false, epoch), false, true, "negative QPC");
        Check(new CaptureClockHealth().Observe(0, 99_999, 480, false, epoch), false, true, "QPC before epoch");
        Check(new CaptureClockHealth().Observe(0, long.MaxValue, 480, false, ulong.MaxValue),
            false, true, "epoch must not wrap through a signed cast");

        var nearLimit = new CaptureClockHealth();
        Check(nearLimit.Observe(long.MaxValue - 1, 100_000, int.MaxValue, false, epoch),
            true, true, "first high device position");
        Check(nearLimit.Observe(long.MaxValue, 200_000, 1, false, epoch),
            true, false, "device end above Int64 maximum must not overflow");
        Require(nearLimit.HasInvalidDevicePosition && !nearLimit.HasInvalidQpc,
            "Overflow-safe overlap check must preserve QPC quality.");

        var invalidFrameCount = new CaptureClockHealth();
        foreach (int frames in new[] { 0, -1 })
        {
            bool threw = false;
            try { invalidFrameCount.Observe(0, 100_000, frames, false, epoch); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            Require(threw, "Nonpositive raw frame count must be rejected.");
        }
        Require(invalidFrameCount.IsHealthy, "Rejected arguments must not mutate capture health.");
        Check(invalidFrameCount.Observe(0, 100_000, 480, false, epoch),
            true, true, "first valid packet after rejected arguments");
    }

    private static void Check(CaptureClockObservation result, bool qpc, bool device, string scenario)
    {
        Require(result.QpcValid == qpc && result.DevicePositionValid == device,
            $"{scenario}: expected QPC={qpc}, device={device}; got QPC={result.QpcValid}, device={result.DevicePositionValid}.");
    }

    private static void Require(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }
}
