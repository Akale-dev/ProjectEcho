namespace ProjectEcho.Core;

/// <summary>
/// Checks one source's consecutive raw WASAPI positions. Use only on its capture thread.
/// This class never substitutes positions or estimates missing device frames from QPC.
/// </summary>
public sealed class CaptureClockHealth
{
    private bool _hasPrevious;
    private long _previousDevicePosition;
    private long _previousQpcPosition100ns;
    private int _previousRawFrameCount;

    public bool HasInvalidQpc { get; private set; }
    public bool HasInvalidDevicePosition { get; private set; }
    public bool IsHealthy => !HasInvalidQpc && !HasInvalidDevicePosition;

    /// <summary>
    /// Returns this packet's position quality and latches any failure for the session.
    /// A first device position of zero is valid. Later device positions must not overlap
    /// the previous raw packet; QPC must advance for every positive-length packet.
    /// WASAPI's timestamp-error flag makes both reported positions untrustworthy.
    /// </summary>
    public CaptureClockObservation Observe(long devicePosition, long qpcPosition100ns,
        int rawFrameCount, bool timestampError, ulong epochQpc100ns)
    {
        if (rawFrameCount <= 0) throw new ArgumentOutOfRangeException(nameof(rawFrameCount));

        bool qpcValid = !timestampError && qpcPosition100ns >= 0
            && (ulong)qpcPosition100ns >= epochQpc100ns
            && (!_hasPrevious || qpcPosition100ns > _previousQpcPosition100ns);
        bool deviceValid = !timestampError && devicePosition >= 0
            && (!_hasPrevious || (Int128)devicePosition >= (Int128)_previousDevicePosition + _previousRawFrameCount);

        HasInvalidQpc |= !qpcValid;
        HasInvalidDevicePosition |= !deviceValid;
        _hasPrevious = true;
        _previousDevicePosition = devicePosition;
        _previousQpcPosition100ns = qpcPosition100ns;
        _previousRawFrameCount = rawFrameCount;
        return new(qpcValid, deviceValid);
    }
}

public readonly record struct CaptureClockObservation(bool QpcValid, bool DevicePositionValid);
