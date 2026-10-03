using System.Globalization;

namespace ProjectEcho.Core;

/// <summary>
/// Human-readable elapsed timeline at 0.01 s resolution. This is a display only:
/// persisted capture clocks and transcript milliseconds retain their original precision.
/// </summary>
public static class TimelineTimestamp
{
    public static string FromMilliseconds(long milliseconds) => Format(Math.Max(0, milliseconds) / 10);

    public static string FromMicroseconds(long microseconds) => Format(Math.Max(0, microseconds) / 10_000);

    private static string Format(long hundredths) => string.Create(CultureInfo.InvariantCulture,
        $"{hundredths / 360_000:D2}:{hundredths / 6_000 % 60:D2}:{hundredths / 100 % 60:D2}.{hundredths % 100:D2}");
}
