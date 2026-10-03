using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Opt-in WPF/file integration test. No enumeration, Host, audio device, playback or historical data.</summary>
    internal async Task<object> ExportCloseSmokeAsync(bool closeDuringMix)
    {
        // Even when exercising a published App, keep generated evidence outside
        // the immutable portable folder and inside the actual developer workspace.
        DirectoryInfo? workspace = new(projectRoot);
        while (workspace is not null && !(File.Exists(Path.Combine(workspace.FullName, "AGENTS.md")) && File.Exists(Path.Combine(workspace.FullName, "global.json"))))
            workspace = workspace.Parent;
        if (workspace is null) throw new InvalidOperationException("Run this synthetic shutdown test inside the ProjectEcho development workspace.");
        string fixture = Path.Combine(workspace.FullName, ".local", "UiCloseTests",
            (closeDuringMix ? "mix-" : "export-") + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        RejectReparseAncestors(fixture);
        Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "SYNTHETIC_FIXTURE"), "Generated PCM and WPF close test only. No device, microphone, Host or playback.");
        dataRoot = fixture;
        string source = Path.Combine(RecordingsRoot, "synthetic-source");
        var encoder = new CloseGateEncoder();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int closedCount = 0;
        Closed += (_, _) => { closedCount++; closed.TrySetResult(); };
        try
        {
            await Task.Run(() => WriteCloseFixture(source));
            var originalHashes = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, CloseFixtureHash, StringComparer.OrdinalIgnoreCase);
            if (closeDuringMix)
            {
                renderOperation = (directory, token) => M2ExportService.RenderMeeting(directory, encoder, token);
                QueueMix(source);
            }
            else
            {
                currentMix = await Task.Run(() => M2ExportService.RenderMeeting(source, encoder: null));
                mixedPath = currentMix.WavePath;
                selectedMeetingDirectory = source;
                exportOperation = (mix, options, destination, token) => M2ExportService.ExportParts(mix, options, destination, encoder, token);
                ExportFormatBox.SelectedIndex = 0;
                MaxBytesBox.Text = "1000000"; MaxSecondsBox.Text = "1.5"; OverlapBox.Text = "0.1";
                UpdateControls();
                Export_Click(this, new RoutedEventArgs());
            }
            await encoder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (!exportBusy || backgroundWork.IsCompleted || encoder.PendingPath is null || !File.Exists(encoder.PendingPath))
                throw new InvalidDataException("The real file pipeline did not reach the held encoder stage.");
            var heldWork = backgroundWork;
            pendingMixes.Enqueue(Path.Combine(fixture, "must-not-start-after-close"));
            Close();
            var firstClose = windowCloseWork ?? throw new InvalidDataException("Close did not register its cleanup task.");
            await encoder.CancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bool remainedOpen = IsVisible && closedCount == 0 && !closeApproved && !heldWork.IsCompleted;
            if (!remainedOpen || pendingMixes.Count != 0 || !File.Exists(encoder.PendingPath))
                throw new InvalidDataException("Window closed or claimed cleanup before pending file work finished.");
            if (StartButton.IsEnabled || ExportButton.IsEnabled || PlaybackButton.IsEnabled || RecoveryPanel.IsEnabled)
                throw new InvalidDataException("Closing allowed new recording, playback or file work.");
            QueueMix(Path.Combine(fixture, "must-not-queue-during-close"));
            if (pendingMixes.Count != 0) throw new InvalidDataException("Closing accepted another mix job.");
            Close();
            if (!ReferenceEquals(firstClose, windowCloseWork) || closedCount != 0)
                throw new InvalidDataException("Repeated Close reentered cleanup or closed the window prematurely.");
            bool dispatcherResponsive = await Dispatcher.InvokeAsync(() => IsVisible && !heldWork.IsCompleted, DispatcherPriority.Background);
            if (!dispatcherResponsive) throw new InvalidDataException("Dispatcher did not remain responsive during file cleanup.");
            encoder.ReleaseCleanup.Set();
            await heldWork.WaitAsync(TimeSpan.FromSeconds(15));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await firstClose;
            if (closedCount != 1 || !closeApproved || exportBusy || pendingMixes.Count != 0 || File.Exists(encoder.PendingPath))
                throw new InvalidDataException("Window did not close exactly once after final cleanup.");
            var leftovers = Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".pending.m4a", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).StartsWith(".encoding-", StringComparison.Ordinal) || Path.GetFileName(path).StartsWith("work-", StringComparison.Ordinal)).ToArray();
            if (leftovers.Length != 0) throw new InvalidDataException("Cancelled file pipeline retained registered temporary files.");
            if (originalHashes.Any(pair => !File.Exists(pair.Key) || CloseFixtureHash(pair.Key) != pair.Value))
                throw new InvalidDataException("Cancellation/close changed a synthetic original.");
            foreach (string path in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
            if (host is not null || microphoneEnabled) throw new InvalidDataException("The close test unexpectedly created a capture session.");
            return new
            {
                Scenario = closeDuringMix ? "close_during_mix" : "close_during_export", Status = "PASS", Fixture = fixture,
                Encoder = "Synthetic delayed-cancellation gate in the production render/export file pipeline; not a native codec test",
                OriginalFiles = originalHashes.Count, OriginalsUnchanged = true, WindowWaitedForFileCleanup = remainedOpen,
                DispatcherResponsive = dispatcherResponsive, ClosedCount = closedCount, PendingJobsDiscarded = true, RegisteredTemporaryFilesRemaining = leftovers.Length,
                FilesExclusivelyReadableAfterClose = true,
                AudioDeviceAccess = "NOT_RUN", CaptureHostStarted = false, MicrophoneCollected = false, Playback = "NOT_RUN", HistoricalDataRead = false
            };
        }
        finally
        {
            // Even a failed assertion must release the synthetic worker before the
            // test application exits. Never strand a file writer to pass a UI test.
            encoder.ReleaseCleanup.Set();
            exportCancellation?.Cancel();
            await backgroundWork.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private static void WriteCloseFixture(string directory)
    {
        const int rate = 48000, frames = rate * 2;
        const ulong epoch = 1000000;
        var pcm = new byte[frames * 2];
        for (int frame = 0; frame < frames; frame++)
            BitConverter.TryWriteBytes(pcm.AsSpan(frame * 2, 2), (short)(300 * Math.Sin(2 * Math.PI * 1000 * frame / rate)));
        using var storage = new M1Storage(directory, epoch, DateTimeOffset.UtcNow, "UI关闭合成测试", isSynthetic: true);
        storage.AddSegment("remote-1", "remote", new WaveFormat(rate, 16, 1));
        storage.WritePacket("remote-1", pcm, new(0, frames, frames, 0, epoch, 0, true));
        storage.WriteBoundary("remote-1", "synthetic-stop", "stop", epoch + 20000000, frames, 0, null);
        storage.Finish(20000000, true, []);
    }

    private static string CloseFixtureHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private sealed class CloseGateEncoder : IM2AudioEncoder
    {
        public string Extension => ".m4a";
        public int BitsPerSecond => 96000;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseCleanup { get; } = new();
        public string? PendingPath { get; private set; }

        public AudioEncodingResult Encode(string pcmWavePath, string destinationPath, CancellationToken cancellationToken)
        {
            using (var pending = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                pending.Write("SYNTHETIC_PENDING_ENCODING"u8);
            PendingPath = destinationPath;
            Entered.TrySetResult();
            if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(15))) throw new TimeoutException("Close did not request cancellation.");
            CancellationSeen.TrySetResult();
            if (!ReleaseCleanup.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Synthetic cleanup gate was not released.");
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The shutdown gate may only complete by cancellation.");
        }
    }
}
