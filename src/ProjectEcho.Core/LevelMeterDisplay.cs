namespace ProjectEcho.Core;

/// <summary>Display only. Original linear peaks, recording counters and audio clocks are never modified.</summary>
public sealed class LevelMeterDisplay
{
    public double Value { get; private set; }
    public static double Scale(double linearPeak) => !double.IsFinite(linearPeak) || linearPeak <= .001 ? 0
        : Math.Clamp((20 * Math.Log10(Math.Min(1, linearPeak)) + 60) / 60, 0, 1);

    public double Advance(double linearPeak, double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return Value;
        double target = Scale(linearPeak);
        double seconds = Math.Min(elapsedSeconds, .25);
        double timeConstant = target > Value ? .035 : .22;
        Value += (target - Value) * (1 - Math.Exp(-seconds / timeConstant));
        if (Math.Abs(target - Value) < .0001) Value = target;
        return Value;
    }

    public void Reset() => Value = 0;
}
