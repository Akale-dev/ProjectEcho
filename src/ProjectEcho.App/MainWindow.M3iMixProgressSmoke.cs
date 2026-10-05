using System.IO;
using System.Windows;
using System.Windows.Threading;
using ProjectEcho.Core.Diagnostics;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Real WPF dispatch and file persistence over synthetic PCM; no capture device is opened.</summary>
    internal async Task<object> M3iMixProgressSmokeAsync(string evidence, CancellationToken token)
    {
        string fixture = Path.Combine(evidence, "mix-progress-synthetic-data");
        Directory.CreateDirectory(fixture); dataRoot = fixture; DataRootText.Text = fixture;
        string first = Path.Combine(RecordingsRoot, "progress-a"), second = Path.Combine(RecordingsRoot, "progress-b");
        WriteCloseFixture(first); WriteCloseFixture(second);
        var originals = Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories).ToDictionary(path => path, CloseFixtureHash);
        var previousRender = renderOperation; var previousProgressRender = renderProgressOperation;
        string oldLanguage = UiText.Language;
        var gates = new List<MixProgressSmokeGate>();
        var images = new List<string>();
        try
        {
            UiText.SetLanguage("zh-CN"); renderOperation = DefaultRenderOperation;
            SelectMeeting(first, loadSavedMix: false); NavigateTo(2);
            SetMeetingExportStatus(first, "SYNTHETIC_PREVIOUS_RESULT", persist: true);
            string receipt = Path.Combine(first, ExportHistoryFileName), initialHash = CloseFixtureHash(receipt);
            var firstGate = NewGate();
            renderProgressOperation = firstGate.Run;
            QueueMix(first);
            var reporter = await firstGate.Entered.Task.WaitAsync(token);
            await Pump();
            int initialLogCount = MeetingLogStore.Read(first).Entries.Count;
            reporter.Report(new(MixStages.Rendering, 40, 100)); await Pump();
            Need(ExportStatus.Text.Contains("40.0%") && ExportStatus.Text.Contains("本阶段"), "Actual WPF did not display the reported 40%.");
            Need(CloseFixtureHash(receipt) == initialHash && MeetingLogStore.Read(first).Entries.Count == initialLogCount,
                "Intermediate progress wrote processing-result or Log.");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            Need(CurrentMixProgressText.IsVisible && CurrentMixProgressText.Text.Contains("40.0%") &&
                CurrentMixProgressText.TranslatePoint(new Point(), this).Y < ActualHeight - 40, "Progress is not visible in the top meeting card.");
            SaveLayoutImage(evidence, "mix-progress-40-percent.png", 1, images);

            SelectMeeting(second, loadSavedMix: false);
            string secondText = ExportStatus.Text;
            reporter.Report(new(MixStages.TruePeakAnalysis, 50, 100)); await Pump();
            Need(ExportStatus.Text == secondText && !ExportStatus.Text.Contains("50.0%"), "Background A progress contaminated selected meeting B.");
            SelectMeeting(first, loadSavedMix: false);
            Need(ExportStatus.Text.Contains("50.0%"), "Returning to A did not show A's own progress.");
            reporter.Report(new(MixStages.TruePeakProtection, 0, 0)); await Pump();
            Need(ExportStatus.Text.Contains("准备中") && !ExportStatus.Text.Contains("%"), "Zero-work/unknown total was fabricated as a percentage.");
            CancelExport_Click(this, new RoutedEventArgs());
            reporter.Report(new(MixStages.Completed, 1, 1)); await Pump();
            await backgroundWork.WaitAsync(token);
            string cancelled = ExportStatus.Text;
            Need(cancelled.Contains("混音已取消") && cancelled.Contains("总耗时") && !transientMixStatuses.ContainsKey(first),
                "Cancellation did not remove transient progress and show the saved final result.");
            reporter.Report(new(MixStages.Hashing, 99, 100)); await Pump();
            Need(ExportStatus.Text == cancelled, "Late cancelled-operation progress replaced the final result.");

            var successGate = NewGate(); renderProgressOperation = successGate.Run;
            QueueMix(first);
            var newReporter = await successGate.Entered.Task.WaitAsync(token); await Pump();
            string beforeOldEvent = ExportStatus.Text;
            reporter.Report(new(MixStages.Validating, 88, 100)); await Pump();
            Need(ExportStatus.Text == beforeOldEvent, "Old generation progress replaced the new operation.");
            newReporter.Report(new(MixStages.Hashing, 25, 100)); await Pump();
            Need(ExportStatus.Text.Contains("25.0%"), "New operation progress was blocked by the cancelled generation.");
            successGate.Release.Set();
            await backgroundWork.WaitAsync(token); await Pump();
            string succeeded = ExportStatus.Text;
            Need(succeeded.Contains("完整 WAV 已校验") && succeeded.Contains("混音总耗时") &&
                currentMix is not null && !transientMixStatuses.ContainsKey(first), "Real synthetic render did not finish with timing and playable output.");
            newReporter.Report(new(MixStages.Rendering, 76, 100)); await Pump();
            Need(ExportStatus.Text == succeeded, "A completed operation accepted late progress.");

            var failureGate = NewGate(failNoPcm: true); renderProgressOperation = failureGate.Run;
            QueueMix(first);
            var failureReporter = await failureGate.Entered.Task.WaitAsync(token); await Pump();
            failureReporter.Report(new(MixStages.Rendering, 0, null)); await Pump();
            Need(ExportStatus.Text.Contains("准备中") && !ExportStatus.Text.Contains("%"), "No audio work invented completion.");
            failureGate.Release.Set(); await backgroundWork.WaitAsync(token); await Pump();
            Need(ExportStatus.Text.Contains("SYNTHETIC_NO_PCM_FAILURE") && ExportStatus.Text.Contains("混音失败") &&
                !transientMixStatuses.ContainsKey(first), "No-PCM synthetic failure was hidden by progress.");
            var timingEntries = MeetingLogStore.Read(first).Entries.Where(entry => entry.Message.StartsWith("混音阶段耗时", StringComparison.Ordinal)).ToArray();
            Need(timingEntries.Length == 3 && timingEntries.All(entry => entry.Message.Contains("总计") && entry.Message.Contains("ms")),
                "Timing was not logged exactly once for success/cancel/failure.");
            foreach (var original in originals)
                Need(CloseFixtureHash(original.Key) == original.Value, "Progress changed original PCM/clock files.");
            Need(host is null && !microphoneEnabled && player.Source is null, "Progress smoke started capture/mic/playback.");
            return new
            {
                Status = "PASS", SyntheticOnly = true, CaptureStarted = false, MicrophoneCollected = false,
                ActualWpfPercent = true, CountsAreSyntheticUiStimuli = true, ActualSyntheticMixPipeline = true,
                MeetingIsolation = true, CancellationAndLateGenerationIsolation = true,
                ZeroTotalIndeterminate = true, NoPcmFailureSimulated = true, ProgressDoesNotPersist = true,
                TimingEntries = timingEntries.Length, OriginalsUnchanged = true, Images = images
            };
        }
        finally
        {
            exportCancellation?.Cancel();
            foreach (var gate in gates) gate.Release.Set();
            if (!backgroundWork.IsCompleted) await backgroundWork.WaitAsync(TimeSpan.FromSeconds(20));
            foreach (var gate in gates) gate.Dispose();
            renderOperation = previousRender; renderProgressOperation = previousProgressRender;
            UiText.SetLanguage(oldLanguage);
        }

        MixProgressSmokeGate NewGate(bool failNoPcm = false)
        {
            var value = new MixProgressSmokeGate(failNoPcm); gates.Add(value); return value;
        }
        async Task Pump() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background, token);
        static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    }

    private sealed class MixProgressSmokeGate(bool failNoPcm) : IDisposable
    {
        internal readonly ManualResetEventSlim Release = new(false);
        internal readonly TaskCompletionSource<IProgress<MixProgress>> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal FullMixResult Run(string directory, CancellationToken token, IProgress<MixProgress> progress)
        {
            progress.Report(new(MixStages.Preparing, 0, null)); Entered.TrySetResult(progress);
            Release.Wait(token); token.ThrowIfCancellationRequested();
            if (failNoPcm) throw new InvalidDataException("SYNTHETIC_NO_PCM_FAILURE");
            return M2ExportService.RenderMeeting(directory, encoder: null, token, progress);
        }
        public void Dispose() => Release.Dispose();
    }
}
