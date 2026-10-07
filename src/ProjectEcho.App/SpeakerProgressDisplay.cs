using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace ProjectEcho.App;

/// <summary>Shows real embedding progress separately from the unmeasured global clustering phase.</summary>
internal sealed class SpeakerProgressDisplay : IDisposable
{
    private readonly TranscriptionView view;
    private readonly Func<bool> isCurrent;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private double? percent;
    internal SpeakerProgressDisplay(TranscriptionView view, Func<bool> isCurrent)
    {
        this.view = view; this.isCurrent = isCurrent;
        view.ProgressPanel.Visibility = Visibility.Visible;
        timer.Tick += Tick; Refresh(); timer.Start();
    }
    internal void Report(double value) { percent = Math.Clamp(value, 0, 100); Refresh(); }
    private void Tick(object? sender, EventArgs e) => Refresh();
    private void Refresh()
    {
        if (!isCurrent()) return;
        view.RecognitionProgressBar.IsIndeterminate = percent is null or >= 100;
        if (percent is { } value) view.RecognitionProgressBar.Value = value;
        view.RecognitionActivityBar.Visibility = Visibility.Visible;
        view.ProgressText.Text = percent is null ? UiText.T("正在加载说话人模型并分析完整音频…") : percent >= 100
            ? UiText.T("声音特征已计算，正在完成全场聚类与版本保存…")
            : UiText.T("声音特征阶段进度（不代表全任务）：") + percent.Value.ToString("F0", CultureInfo.InvariantCulture) + "%";
        view.ProgressElapsedText.Text = UiText.F($"已用时 {clock.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)}");
    }
    public void Dispose() { timer.Stop(); timer.Tick -= Tick; clock.Stop(); view.RecognitionActivityBar.Visibility = Visibility.Collapsed; }
}
