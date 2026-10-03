using System.Diagnostics;
using System.Windows.Media;
using ProjectEcho.Core;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private readonly LevelMeterDisplay remoteLevelDisplay = new(), microphoneLevelDisplay = new();
    private double remoteLevelTargetRawPeak, microphoneLevelTargetRawPeak;
    private long meterLastRenderTicks;

    private void InitializeLevelMeterDisplay()
    {
        CompositionTarget.Rendering += RenderLevelMeters;
        Closed += (_, _) => CompositionTarget.Rendering -= RenderLevelMeters;
    }

    private void SetLevelMeterTargets(double remotePeak, double microphonePeak)
    {
        remoteLevelTargetRawPeak = double.IsFinite(remotePeak) ? Math.Clamp(remotePeak, 0, 1) : 0;
        microphoneLevelTargetRawPeak = double.IsFinite(microphonePeak) ? Math.Clamp(microphonePeak, 0, 1) : 0;
    }

    private void ResetLevelMeterDisplay(bool microphoneOnly = false)
    {
        microphoneLevelTargetRawPeak = 0; microphoneLevelDisplay.Reset(); MicLevel.Value = 0;
        if (!microphoneOnly)
        { remoteLevelTargetRawPeak = 0; remoteLevelDisplay.Reset(); RemoteLevel.Value = 0; }
        meterLastRenderTicks = Stopwatch.GetTimestamp();
    }

    private void RenderLevelMeters(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = meterLastRenderTicks == 0 ? 1d / 60 : (now - meterLastRenderTicks) / (double)Stopwatch.Frequency;
        meterLastRenderTicks = now;
        if (!initialized || WindowCloseRequested || MainTabs.SelectedIndex != 0) return;
        RemoteLevel.Value = remoteLevelDisplay.Advance(remoteLevelTargetRawPeak, elapsed);
        MicLevel.Value = microphoneLevelDisplay.Advance(microphoneLevelTargetRawPeak, elapsed);
    }
}
