using ProjectEcho.Core;

internal static class M3hMeterDisplayTests
{
    internal static void Register(Action<string, Action> run)
    {
        run("M3H meter fine logarithmic scale keeps quiet changes visible and rejects invalid peaks", () =>
        {
            Need(LevelMeterDisplay.Scale(0) == 0 && LevelMeterDisplay.Scale(double.NaN) == 0 && LevelMeterDisplay.Scale(1) == 1);
            Need(Math.Abs(LevelMeterDisplay.Scale(.01) - 1d / 3) < 1e-9);
            Need(LevelMeterDisplay.Scale(.0101) > LevelMeterDisplay.Scale(.01) && LevelMeterDisplay.Scale(10) == 1);
        });
        run("M3H meter attack-release follows elapsed time with intermediate values and no overshoot", () =>
        {
            var first = new LevelMeterDisplay(); var second = new LevelMeterDisplay();
            double initial = first.Advance(1, 1d / 60);
            Need(initial > 0 && initial < 1);
            first.Advance(1, 1d / 60); second.Advance(1, 1d / 30);
            Need(Math.Abs(first.Value - second.Value) < 1e-10);
            double high = first.Value;
            first.Advance(0, 1d / 60); Need(first.Value < high && first.Value > 0);
            first.Reset(); Need(first.Value == 0);
        });
        static void Need(bool condition) { if (!condition) throw new InvalidDataException("Meter display contract failed."); }
    }
}
