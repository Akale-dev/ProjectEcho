using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

/// <summary>Elapsed time and activity are separate from the engine's coarse audio-window percentage.</summary>
internal sealed class RecognitionProgressDisplay : IDisposable
{
    private readonly TranscriptionView view;
    private readonly Func<bool> isCurrent;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly DispatcherTimer timer;
    private LocalTranscriptionProgress? latest;
    private bool disposed;

    internal RecognitionProgressDisplay(TranscriptionView view, Func<bool> isCurrent)
    {
        this.view = view; this.isCurrent = isCurrent;
        view.ProgressPanel.Visibility = Visibility.Visible;
        view.RecognitionProgressBar.IsIndeterminate = true;
        view.RecognitionProgressBar.Value = 0;
        timer = new DispatcherTimer(DispatcherPriority.Background, view.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += Tick;
        Refresh(); timer.Start();
    }

    internal void Report(LocalTranscriptionProgress value)
    {
        if (disposed || !isCurrent()) return;
        latest = value;
        Refresh();
    }

    private void Tick(object? sender, EventArgs args) => Refresh();

    private void Refresh()
    {
        if (disposed || !isCurrent()) return;
        bool hasPercent = latest is { Stage: LocalTranscriptionStage.Recognizing or LocalTranscriptionStage.Aligning, Percent: > 0 and <= 100 };
        view.RecognitionProgressBar.IsIndeterminate = !hasPercent;
        if (hasPercent) view.RecognitionProgressBar.Value = latest!.Percent!.Value;
        // Never advance a percentage using elapsed time. Keep visible activity even
        // while the engine spends a long time inside a window and reports no new value.
        view.RecognitionActivityBar.Visibility = hasPercent ? Visibility.Visible : Visibility.Collapsed;
        view.ProgressText.Text = latest?.Stage switch
        {
            null => UiText.T("正在准备识别音频…"),
            LocalTranscriptionStage.Validating => UiText.T("正在校验本地文件…"),
            LocalTranscriptionStage.LoadingModel => UiText.T("正在加载模型并准备识别…"),
            LocalTranscriptionStage.Aligning => UiText.F($"正在定位句子 {latest.Percent ?? 0}%（独立声学对齐）"),
            LocalTranscriptionStage.Saving => UiText.T("识别运算已结束，正在校验并保存文字…"),
            _ => hasPercent ? UiText.F($"本地识别 {latest!.Percent!.Value}%（引擎按音频窗口回报）")
                : UiText.T("正在识别，等待首个进度回报…")
        };
        string duration = elapsed.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        view.ProgressElapsedText.Text = UiText.F($"已用时 {duration}");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; timer.Stop(); timer.Tick -= Tick; elapsed.Stop();
        view.RecognitionActivityBar.Visibility = Visibility.Collapsed;
    }
}
