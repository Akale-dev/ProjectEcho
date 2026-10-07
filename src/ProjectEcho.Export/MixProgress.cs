using System.Diagnostics;
namespace ProjectEcho.Export;

/// <summary>Measured work in one named phase; no elapsed-time or weighted overall percentage.</summary>
public sealed record MixProgress(string Stage, long Completed, long? Total)
{
    public double? Percent => Total is > 0 && Completed >= 0 && Completed <= Total.Value ? 100d * Completed / Total.Value : null;
}
public static class MixStages
{
    public const string Preparing="preparing", Validating="validating", ValidatingIndex="validating-index",
        ValidatingPcm="validating-pcm", ValidatingFiles="validating-files", RenderAnalysis="render-analysis",
        Rendering="rendering", TruePeakAnalysis="truepeak-analysis", TruePeakProtection="truepeak-protection",
        TruePeakVerification="truepeak-verification", Hashing="hashing", Completed="completed";
}
internal sealed class MixProgressReporter(IProgress<MixProgress>? target)
{
    private string? stage;
    private long lastTick;
    internal void Report(string value,long completed,long? total,bool force=false)
    {
        if(target is null)return;
        long tick=Stopwatch.GetTimestamp();
        if(!force && stage==value && completed!=total && tick-lastTick<Stopwatch.Frequency/5)return;
        stage=value;lastTick=tick;
        try {target.Report(new(value,completed,total));} catch(Exception) { /* Observers cannot change audio processing. */ }
    }
}
internal sealed class RecoveryMixProgress(MixProgressReporter reporter):IProgress<ProjectEcho.Capture.Windows.M1RecoveryProgress>
{
    public void Report(ProjectEcho.Capture.Windows.M1RecoveryProgress value) => reporter.Report(value.Stage switch {
        "index"=>MixStages.ValidatingIndex,"pcm"=>MixStages.ValidatingPcm,"files"=>MixStages.ValidatingFiles,_=>MixStages.Validating
    },value.Completed,value.Total);
}