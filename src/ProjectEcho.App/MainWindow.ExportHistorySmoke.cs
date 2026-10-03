using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Opt-in production WPF/file flows over generated PCM only; never opens a capture device.</summary>
    internal async Task<object> ExportHistorySmokeAsync(string evidence, CancellationToken token)
    {
        string fixture = Path.Combine(evidence, "synthetic-data");
        Directory.CreateDirectory(fixture);
        dataRoot = fixture; DataRootText.Text = fixture;
        string first = Path.Combine(RecordingsRoot, "meeting-a"), second = Path.Combine(RecordingsRoot, "meeting-b");
        string empty = Path.Combine(RecordingsRoot, "meeting-empty"), invalid = Path.Combine(RecordingsRoot, "meeting-invalid");
        foreach (string directory in new[] { first, second, empty, invalid }) WriteCloseFixture(directory);
        // Reuse the actual previous-release synthetic v4 render, never a user recording.
        DirectoryInfo? workspace = new(projectRoot);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        if (workspace is null) throw new InvalidOperationException("Run history smoke inside the ProjectEcho workspace.");
        string oldFixture = Path.Combine(workspace.FullName, ".local", "TestResults", "M3D-EndpointFix", "default-final", "m2-export", "wave-parts");
        using (var manifest = ReadProcessingJson(Path.Combine(oldFixture, "meeting.json")))
            if (!manifest.RootElement.GetProperty("isSynthetic").GetBoolean()) throw new InvalidDataException("Legacy fixture must be synthetic.");
        string legacy = Path.Combine(RecordingsRoot, "meeting-actual-v4");
        foreach (string file in Directory.EnumerateFiles(oldFixture, "*", SearchOption.AllDirectories))
        {
            RejectReparseAncestors(file);
            string destination = Path.Combine(legacy, Path.GetRelativePath(oldFixture, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
        }
        var originals = Directory.EnumerateFiles(RecordingsRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, CloseFixtureHash, StringComparer.OrdinalIgnoreCase);
        RefreshRecovery_Click(this, new RoutedEventArgs());
        var images = new List<string>();

        // A mix finishes after the user has selected B. B must remain empty.
        using (var gate = new ExportHistoryGate())
        {
            renderOperation = (directory, cancellation) => gate.Run(() => M2ExportService.RenderMeeting(directory, encoder: null, cancellation), cancellation);
            SelectMeeting(first, loadSavedMix: false); QueueMix(first);
            await gate.Entered.Task.WaitAsync(token);
            SelectMeeting(second, loadSavedMix: false);
            string before = ExportStatus.Text;
            gate.Release.Set(); await backgroundWork.WaitAsync(token);
            Require(ExportStatus.Text == before && currentMix is null && lastExportDirectory is null, "Late A mix contaminated B.");
        }
        SelectMeeting(first, loadSavedMix: false);
        Require(currentMix is not null && ExportStatus.Text.Contains("完整 WAV 已校验", StringComparison.Ordinal), "A mix result did not follow selection.");
        string mixText = ExportStatus.Text;

        // Use the real WAV export pipeline, with a gate only to make navigation deterministic.
        ExportFormatBox.SelectedIndex = 1;
        MaxBytesBox.Text = "1000000"; MaxSecondsBox.Text = "1.5"; OverlapBox.Text = "0.1";
        using (var gate = new ExportHistoryGate())
        {
            exportOperation = (mix, options, destination, cancellation) => gate.Run(
                () => M2ExportService.ExportParts(mix, options, destination, cancellation), cancellation);
            Export_Click(this, new RoutedEventArgs());
            await gate.Entered.Task.WaitAsync(token);
            SelectMeeting(second, loadSavedMix: false);
            string before = ExportStatus.Text;
            gate.Release.Set(); await backgroundWork.WaitAsync(token);
            Require(ExportStatus.Text == before && lastExportDirectory is null && !OpenExportButton.IsEnabled, "Late A export changed B output.");
        }
        SelectMeeting(first, loadSavedMix: false);
        string exportText = ExportStatus.Text;
        string exported = lastExportDirectory ?? throw new InvalidDataException("A export directory was not restored.");
        Require(exportText.Contains("本地导出完成", StringComparison.Ordinal) && OpenExportButton.IsEnabled && exportText != mixText,
            "A result did not change from mix to export.");

        renderOperation = (directory, cancellation) => M2ExportService.RenderMeeting(directory, encoder: null, cancellation);
        SelectMeeting(second, loadSavedMix: false); QueueMix(second); await backgroundWork.WaitAsync(token);
        using (var gate = new ExportHistoryGate())
        {
            exportOperation = (_, _, _, cancellation) => gate.Run<ExportResult>(() => throw new IOException("SYNTHETIC_B_EXPORT_FAILURE"), cancellation);
            Export_Click(this, new RoutedEventArgs());
            await gate.Entered.Task.WaitAsync(token);
            SelectMeeting(first, loadSavedMix: false);
            gate.Release.Set(); await backgroundWork.WaitAsync(token);
            Require(ExportStatus.Text == exportText && lastExportDirectory == exported, "B export failure replaced A success.");
        }
        SelectMeeting(second, loadSavedMix: false);
        Require(ExportStatus.Text.Contains("SYNTHETIC_B_EXPORT_FAILURE", StringComparison.Ordinal) && lastExportDirectory is null,
            "B export failure was not retained on B.");

        using (var gate = new ExportHistoryGate())
        {
            renderOperation = (_, cancellation) => gate.Run<FullMixResult>(() => throw new IOException("SYNTHETIC_B_MIX_FAILURE"), cancellation);
            QueueMix(second); await gate.Entered.Task.WaitAsync(token);
            SelectMeeting(first, loadSavedMix: false);
            gate.Release.Set(); await backgroundWork.WaitAsync(token);
            Require(ExportStatus.Text == exportText, "B mix failure replaced A success.");
        }
        SelectMeeting(second, loadSavedMix: false);
        Require(ExportStatus.Text.Contains("SYNTHETIC_B_MIX_FAILURE", StringComparison.Ordinal), "B mix failure was not retained.");

        // A fresh WPF instance has no result dictionaries. It must reconstruct results from disk,
        // while the independent audio validation must not overwrite the last processing outcome.
        var reopened = new MainWindow(enumerateSourcesOnLoad: false) { dataRoot = fixture };
        try
        {
            reopened.Show(); reopened.DataRootText.Text = fixture;
            reopened.RefreshRecovery_Click(this, new RoutedEventArgs());
            reopened.SelectMeeting(first); await reopened.savedMixWork.WaitAsync(token);
            Require(reopened.ExportStatus.Text.Contains(exportText, StringComparison.Ordinal) && reopened.lastExportDirectory == exported &&
                reopened.currentMix is not null && reopened.OpenExportButton.IsEnabled, "Disk reload did not retain A export and independently load audio.");
            string firstHistory = Path.Combine(first, ExportHistoryFileName), heldHistory = firstHistory + ".smoke-held";
            File.Move(firstHistory, heldHistory);
            try
            {
                reopened.meetingExportStatuses.Remove(first); reopened.renderedMeetings.Remove(first);
                reopened.SelectMeeting(first); await reopened.savedMixWork.WaitAsync(token);
                Require(reopened.ExportStatus.Text.Contains("历史导出记录", StringComparison.Ordinal) && reopened.lastExportDirectory == exported,
                    "A meeting from an older release did not reconstruct its own export receipt.");
            }
            finally { File.Move(heldHistory, firstHistory); }
            reopened.SelectMeeting(second); await reopened.savedMixWork.WaitAsync(token);
            Require(reopened.ExportStatus.Text.Contains("SYNTHETIC_B_MIX_FAILURE", StringComparison.Ordinal) && reopened.lastExportDirectory is null,
                "Disk reload/validation lost B failure or exposed A output.");
            reopened.SelectMeeting(legacy); await reopened.savedMixWork.WaitAsync(token);
            Require(reopened.ExportStatus.Text.Contains("历史处理记录；旧版音频尚未重新校验", StringComparison.Ordinal) &&
                reopened.ExportStatus.Text.Contains("历史导出记录", StringComparison.Ordinal) && reopened.currentMix is null &&
                !reopened.PlaybackButton.IsEnabled && !reopened.ExportButton.IsEnabled,
                "Actual v4 history was not visible, or enabled unvalidated legacy audio.");
            string legacyRender = Directory.EnumerateFiles(Path.Combine(legacy, "derived"), "render.json", SearchOption.AllDirectories).Single();
            string legacyIndex = Path.Combine(legacy, "journal", "capture-index.jsonl");
            byte[] legacyRenderBytes = File.ReadAllBytes(legacyRender), legacyIndexBytes = File.ReadAllBytes(legacyIndex);
            try
            {
                File.AppendAllText(legacyIndex, " ");
                reopened.meetingExportStatuses.Remove(legacy); reopened.SelectMeeting(legacy); await reopened.savedMixWork.WaitAsync(token);
                Require(reopened.ExportStatus.Text.Contains("处理记录无法读取", StringComparison.Ordinal) && reopened.currentMix is null,
                    "Legacy history accepted a different source index.");
                File.WriteAllBytes(legacyIndex, legacyIndexBytes);
                foreach (var change in new Action<System.Text.Json.Nodes.JsonNode>[]
                {
                    node => node["schemaVersion"] = "unknown-schema",
                    node => node["wave"]!["file"] = "../../foreign.wav"
                })
                {
                    var modified = System.Text.Json.Nodes.JsonNode.Parse(legacyRenderBytes)!; change(modified);
                    File.WriteAllText(legacyRender, modified.ToJsonString());
                    reopened.meetingExportStatuses.Remove(legacy); reopened.SelectMeeting(legacy); await reopened.savedMixWork.WaitAsync(token);
                    Require(reopened.ExportStatus.Text.Contains("处理记录无法读取", StringComparison.Ordinal) && reopened.currentMix is null,
                        "Legacy history accepted an unknown schema or arbitrary audio path.");
                }
            }
            finally { File.WriteAllBytes(legacyRender, legacyRenderBytes); File.WriteAllBytes(legacyIndex, legacyIndexBytes); }
            reopened.SelectMeeting(empty); await reopened.savedMixWork.WaitAsync(token);
            Require(reopened.ExportStatus.Text.Contains("所选会议暂无处理结果", StringComparison.Ordinal) &&
                !reopened.ExportStatus.Text.Contains("SYNTHETIC_B", StringComparison.Ordinal) && reopened.lastExportDirectory is null,
                "An unprocessed meeting reused another meeting's result.");

            // A copied receipt from another source must not be accepted as this meeting's history.
            File.Copy(Path.Combine(first, ExportHistoryFileName), Path.Combine(invalid, ExportHistoryFileName));
            reopened.SelectMeeting(invalid, loadSavedMix: false);
            Require(reopened.ExportStatus.Text.Contains("处理记录无法读取", StringComparison.Ordinal) && !reopened.OpenExportButton.IsEnabled,
                "A receipt for another source was accepted.");
            var poisoned = new SavedExportStatus("meeting-processing-result-1", MeetingManifestHash(invalid), DateTimeOffset.UtcNow,
                "UNTRUSTED_RESULT", "../outside/export-poison");
            File.WriteAllText(Path.Combine(invalid, ExportHistoryFileName), JsonSerializer.Serialize(poisoned));
            reopened.meetingExportStatuses.Remove(invalid); reopened.SelectMeeting(invalid, loadSavedMix: false);
            Require(reopened.ExportStatus.Text.Contains("处理记录无法读取", StringComparison.Ordinal) && reopened.lastExportDirectory is null,
                "A path-traversal receipt enabled an output directory.");
            reopened.ClearMeetingForNewRecording();
            Require(reopened.selectedMeetingDirectory is null && reopened.lastExportDirectory is null &&
                reopened.ExportStatus.Text.Contains("请先在会议库选择会议", StringComparison.Ordinal), "Clearing selection retained a result.");
        }
        finally
        {
            reopened.Close();
            if (reopened.windowCloseWork is { } closing) await closing.WaitAsync(token);
        }

        // Cancel B from A: the shared cancel button controls the job, but the result stays on B.
        SelectMeeting(second, loadSavedMix: false);
        using (var gate = new ExportHistoryGate())
        {
            exportOperation = (_, _, _, cancellation) => gate.Run<ExportResult>(() => throw new InvalidOperationException("Gate should cancel."), cancellation);
            Export_Click(this, new RoutedEventArgs()); await gate.Entered.Task.WaitAsync(token);
            SelectMeeting(first, loadSavedMix: false); CancelExport_Click(this, new RoutedEventArgs());
            await backgroundWork.WaitAsync(token);
            Require(ExportStatus.Text == exportText && lastExportDirectory == exported, "B cancellation replaced A result.");
        }
        SelectMeeting(second, loadSavedMix: false);
        Require(ExportStatus.Text.Contains("导出已取消", StringComparison.Ordinal), "B cancellation was not assigned to B.");
        SelectMeeting(first, loadSavedMix: false); NavigateTo(2);
        ExportStatus.BringIntoView();
        for (DependencyObject? ancestor = System.Windows.Media.VisualTreeHelper.GetParent(ExportStatus); ancestor is not null;
             ancestor = System.Windows.Media.VisualTreeHelper.GetParent(ancestor))
            if (ancestor is System.Windows.Controls.ScrollViewer scroll) { scroll.ScrollToEnd(); break; }
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
        SaveLayoutImage(evidence, "selected-meeting-export-result.png", 1, images);
        foreach (var original in originals)
            Require(CloseFixtureHash(original.Key) == original.Value, "Processing history changed authoritative originals.");
        Require(host is null && !microphoneEnabled, "History smoke unexpectedly activated capture.");
        return new { Status = "PASS", AAndBSelection = true, EmptyMeeting = true, LateMixAndExportCompletion = true,
            MixAndExportFailureOwnership = true, CancellationOwnership = true, FreshWindowDiskReload = true,
            IndependentSavedAudioValidation = true, LegacyExportReceiptReconstructed = true, ActualV4SourceBoundHistory = true,
            LegacyAudioPlaybackAndExportRemainDisabled = true, LegacyChangedIndexUnknownSchemaAndForeignPathRejected = true,
            ForeignReceiptRejected = true, TraversalRejected = true,
            OpenExportBelongsToSelection = true, ClearSelection = true, OriginalsUnchanged = true,
            CaptureStarted = false, MicrophoneCollected = false, ComputerUse = false, Images = images };

        static void Require(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
    }

    private sealed class ExportHistoryGate : IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        internal T Run<T>(Func<T> operation, CancellationToken token)
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(30), token)) throw new TimeoutException("Synthetic history gate timed out.");
            token.ThrowIfCancellationRequested(); return operation();
        }
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }
}
