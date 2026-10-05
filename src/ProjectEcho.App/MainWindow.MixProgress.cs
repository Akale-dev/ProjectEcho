using System.Diagnostics;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private readonly Dictionary<string, string> transientMixStatuses = new(StringComparer.OrdinalIgnoreCase);
    private long mixProgressGeneration;
    private MixUiOperation? activeMixUiOperation;
    // Explicit synthetic test seam; the old two-argument renderOperation override still works.
    private Func<string, CancellationToken, IProgress<MixProgress>, FullMixResult>? renderProgressOperation;

    private sealed class MixUiOperation(string directory, long generation, CancellationToken token)
    {
        internal string Directory { get; } = directory;
        internal long Generation { get; } = generation;
        internal CancellationToken Token { get; } = token;
        internal Stopwatch Clock { get; } = Stopwatch.StartNew();
        internal string Stage = MixStages.Preparing;
        internal TimeSpan StageStarted, LastObserved;
        internal readonly Dictionary<string, TimeSpan> StageTimes = new(StringComparer.Ordinal);
        internal required IProgress<MixProgress> Reporter;
        internal bool Finished;
        internal string Summary = "";
    }

    private sealed record MixUiObservation(MixProgress Value, TimeSpan ReportedAt);

    private sealed class ThrottledMixProgress(MixUiOperation operation, IProgress<MixUiObservation> dispatcher) : IProgress<MixProgress>
    {
        private readonly object gate = new();
        private string? lastStage;
        private long lastCompleted = -1;
        private long? lastTotal;
        private TimeSpan lastQueued = TimeSpan.MinValue;
        public void Report(MixProgress value)
        {
            if (operation.Token.IsCancellationRequested) return;
            lock (gate)
            {
                TimeSpan elapsed = operation.Clock.Elapsed;
                bool stageChanged = lastStage != value.Stage;
                bool completed = value.Total is > 0 && value.Completed == value.Total;
                bool unchanged = !stageChanged && lastCompleted == value.Completed && lastTotal == value.Total;
                if (unchanged || !stageChanged && !completed && elapsed - lastQueued < TimeSpan.FromMilliseconds(200)) return;
                lastStage = value.Stage; lastCompleted = value.Completed; lastTotal = value.Total; lastQueued = elapsed;
                dispatcher.Report(new(value, elapsed));
            }
        }
    }

    private MixUiOperation BeginMixProgress(string directory, CancellationToken token)
    {
        // Called on the WPF dispatcher: Progress<T> captures that context. Each callback also
        // checks operation identity and token, so queued events never resurrect completed work.
        Dispatcher.VerifyAccess();
        var operation = new MixUiOperation(directory, ++mixProgressGeneration, token) { Reporter = null! };
        operation.Reporter = new ThrottledMixProgress(operation, new Progress<MixUiObservation>(value => ApplyMixProgress(operation, value)));
        activeMixUiOperation = operation;
        transientMixStatuses[directory] = FormatMixProgress(operation, new(MixStages.Preparing, 0, null), TimeSpan.Zero);
        if (string.Equals(selectedMeetingDirectory, directory, StringComparison.OrdinalIgnoreCase)) RefreshSelectedExportStatus();
        return operation;
    }

    private void ApplyMixProgress(MixUiOperation operation, MixUiObservation observation)
    {
        Dispatcher.VerifyAccess();
        if (WindowCloseRequested || operation.Finished || operation.Token.IsCancellationRequested ||
            !ReferenceEquals(activeMixUiOperation, operation) || operation.Generation != mixProgressGeneration ||
            observation.ReportedAt < operation.LastObserved) return;
        MixProgress value = observation.Value;
        if (string.IsNullOrWhiteSpace(value.Stage)) return;
        operation.LastObserved = observation.ReportedAt;
        if (operation.Stage != value.Stage)
        {
            AddMixStageTime(operation, observation.ReportedAt);
            operation.Stage = value.Stage; operation.StageStarted = observation.ReportedAt;
        }
        transientMixStatuses[operation.Directory] = FormatMixProgress(operation, value, observation.ReportedAt);
        if (string.Equals(selectedMeetingDirectory, operation.Directory, StringComparison.OrdinalIgnoreCase))
            RefreshSelectedExportStatus();
    }

    private static string FormatMixProgress(MixUiOperation operation, MixProgress value, TimeSpan reportedAt)
    {
        string amount = value.Stage == MixStages.Completed ? UiText.T("已完成") : value.Total is > 0 && value.Completed >= 0 && value.Completed <= value.Total
            ? (100d * value.Completed / value.Total.Value).ToString("F1", System.Globalization.CultureInfo.CurrentCulture) + "%"
            : UiText.T("准备中");
        double stageSeconds = Math.Max(0, (reportedAt - operation.StageStarted).TotalSeconds);
        return UiText.F($"{MixStageLabel(value.Stage)}：{amount} · 本阶段 {stageSeconds:F1} 秒\n") +
            UiText.F($"混音已用 {reportedAt.TotalSeconds:F1} 秒。可取消，也可开始下一场录音。");
    }

    private string EndMixProgress(MixUiOperation operation, string outcome)
    {
        Dispatcher.VerifyAccess();
        if (operation.Finished) return operation.Summary;
        operation.Finished = true; operation.Clock.Stop();
        AddMixStageTime(operation, operation.Clock.Elapsed);
        if (ReferenceEquals(activeMixUiOperation, operation)) activeMixUiOperation = null;
        transientMixStatuses.Remove(operation.Directory);
        RefreshMixProgressHeader();
        operation.Summary = UiText.F($"混音总耗时：{operation.Clock.Elapsed.TotalSeconds:F1} 秒。");
        string details = UiText.F($"混音阶段耗时（{outcome}）：总计 {operation.Clock.Elapsed.TotalMilliseconds:F0} ms。\n") +
            string.Join("\n", operation.StageTimes.Select(pair => UiText.F($"{MixStageLabel(pair.Key)}：{pair.Value.TotalMilliseconds:F0} ms")));
        // One timing receipt per operation; intermediate percentages never write Log or files.
        LogMeeting(operation.Directory, "diagnostic", details);
        return operation.Summary;
    }

    private void RefreshMixProgressHeader()
    {
        CurrentMixProgressText.Text = selectedMeetingDirectory is { } directory &&
            transientMixStatuses.TryGetValue(directory, out string? text) ? text : "";
        CurrentMixProgressText.Visibility = CurrentMixProgressText.Text.Length == 0
            ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }
    private static void AddMixStageTime(MixUiOperation operation, TimeSpan until)
    {
        TimeSpan duration = until >= operation.StageStarted ? until - operation.StageStarted : TimeSpan.Zero;
        operation.StageTimes[operation.Stage] = operation.StageTimes.GetValueOrDefault(operation.Stage) + duration;
    }

    private static string MixStageLabel(string stage) => UiText.T(stage switch
    {
        MixStages.Preparing => "准备混音",
        MixStages.Validating => "核对原件文件",
        MixStages.ValidatingIndex => "校验录音时间索引",
        MixStages.ValidatingPcm => "校验原始音频数据",
        MixStages.ValidatingFiles => "校验原轨文件",
        MixStages.RenderAnalysis => "分析混音音量",
        MixStages.TruePeakVerification => "复查音频峰值",
        MixStages.Rendering => "重建完整混音",
        MixStages.TruePeakAnalysis => "检查音量峰值",
        MixStages.TruePeakProtection => "生成峰值保护副本",
        MixStages.Hashing => "校验完整回放文件",
        MixStages.Completed => "混音处理完成",
        _ => "正在处理混音"
    });
}
