using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using ProjectEcho.Core;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private readonly Queue<string> pendingMixes = new();
    private readonly Dictionary<string, FullMixResult> renderedMeetings = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? exportCancellation;
    private Task backgroundWork = Task.CompletedTask;
    // These delegates keep the actual file pipelines injectable for no-device shutdown tests.
    private Func<string, CancellationToken, FullMixResult> renderOperation = (directory, token) =>
        AudioImportService.IsImportedMeeting(directory) ? AudioImportService.Load(directory, token).Mix : M2ExportService.RenderMeeting(directory, encoder: null, token);
    private Func<FullMixResult, ExportOptions, string, CancellationToken, ExportResult> exportOperation = M2ExportService.ExportParts;
    private bool exportBusy;
    private string activeProcessingLogCategory = "mix";
    private string? selectedMeetingDirectory;
    private FullMixResult? currentMix;
    private string? lastExportDirectory;
    private CancellationTokenSource? savedMixCancellation;
    private Task savedMixWork = Task.CompletedTask;
    private string? savedMixLoadingDirectory;
    private Func<string, CancellationToken, FullMixResult?> savedMixLoadOperation = (directory, token) =>
        AudioImportService.IsImportedMeeting(directory) ? AudioImportService.Load(directory, token).Mix : M2SavedMixLoader.LoadLatestForListening(directory, token);

    private void UpdateExportControls()
    {
        bool idle = SessionRules.CanStart(state) && !WindowCloseRequested && !deletingMeeting;
        ImportAudioButton.IsEnabled = idle && !exportBusy && !transcriptBusy && !recoveryBusy && pendingMixes.Count == 0;
        DataRootButton.IsEnabled = idle && !exportBusy && !transcriptBusy && pendingMixes.Count == 0 && !recoveryBusy && !refreshing;
        RetryMixButton.IsEnabled = idle && !exportBusy && selectedMeetingDirectory is not null;
        ExportButton.IsEnabled = !WindowCloseRequested && !deletingMeeting && !exportBusy && currentMix is not null;
        CancelExportButton.IsEnabled = !WindowCloseRequested && (exportBusy || savedMixCancellation is not null);
        OpenExportButton.IsEnabled = !WindowCloseRequested && lastExportDirectory is not null;
        FileMeetingButton.IsEnabled = idle && !recoveryBusy && selectedMeetingDirectory is not null;
    }

    private void DataRoot_Click(object sender, RoutedEventArgs e)
    {
        if (!DataRootButton.IsEnabled) return;
        var picker = new OpenFolderDialog { Title = UiText.T("选择 ProjectEcho 本地数据目录"), Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var chosen = ValidateLocalDataRoot(picker.FolderName);
            Directory.CreateDirectory(chosen);
            // Probe only a uniquely owned file; never modify existing user files.
            var probe = Path.Combine(chosen, ".projectecho-write-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       1, FileOptions.DeleteOnClose)) stream.WriteByte(0);
            ResetTranscriptPlayback();
            ClearSavedMixPlaybackRequest();
            savedMixCancellation?.Cancel();
            ClearReplaySubtitles();
            RememberFilingDraft();
            selectedFilingBaseline = null;
            FlushMeetingWhisperPrompt();
            dataRoot = chosen;
            libraryQuery = new();
            LoadWhisperConfiguration();
            DataRootText.Text = chosen;
            selectedMeetingDirectory = null; currentMix = null; mixedPath = null; lastExportDirectory = null;
            RefreshSelectedExportStatus();
            RefreshMeetingLog();
            RecoveryBox.ItemsSource = null;
            libraryRead = false; MeetingCountText.Text = UiText.T("点击会议库读取历史");
            SelectedMeetingText.Text = UiText.T("数据目录已更改；原有文件保留在原位置。");
            SetStatus(UiText.T("后续录音写入所选数据目录；没有移动或删除历史原件。"));
            ClearValidationIssues(DataRootButton, SelectionPanel);
            RefreshRecordingValidationMessage();
        }
        catch (Exception ex) { SetStatus(UiText.T("数据目录不可用：") + ex.Message); }
        UpdateControls();
    }

    private static string ValidateLocalDataRoot(string path)
    {
        var full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)!;
        if (root.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType == DriveType.Network ||
            full.AsSpan(root.Length).Contains(':'))
            throw new InvalidDataException(UiText.T("请选择本地磁盘目录；录音与浏览器资料不写入网络目录或备用数据流。"));
        RejectReparseAncestors(full);
        return full;
    }

    private void SelectMeeting(string directory, bool loadSavedMix = true)
    {
        if (deletingMeeting) return;
        // Navigation/clicks on the same row must not restart an in-flight hash
        // check or discard an explicit play request made while it is running.
        if (loadSavedMix && IsLoadingSavedMix(directory) &&
            string.Equals(selectedMeetingDirectory, directory, StringComparison.OrdinalIgnoreCase)) return;
        RememberFilingDraft();
        ClearSavedMixPlaybackRequest();
        savedMixCancellation?.Cancel();
        ResetTranscriptPlayback();
        ClearReplaySubtitles();
        try
        {
            selectedMeetingDirectory = ValidateRecordingDirectory(directory);
            synchronizingMeetingSelection = true;
            try { RecoveryBox.SelectedItem = RecoveryBox.Items.Cast<RecordingDirectory>().FirstOrDefault(item => string.Equals(item.Path, selectedMeetingDirectory, StringComparison.OrdinalIgnoreCase)); }
            finally { synchronizingMeetingSelection = false; }
            selectedMeetingImported = AudioImportService.IsImportedMeeting(selectedMeetingDirectory);
            renderedMeetings.TryGetValue(selectedMeetingDirectory, out currentMix);
            // Reopening historical meetings requires validation/rendering before playback.
            mixedPath = SelectListeningPath(currentMix);
            SelectedMeetingText.Text = MeetingDisplayName(selectedMeetingDirectory);
            var filing = ReadFiling(selectedMeetingDirectory);
            selectedFilingBaseline = filing;
            var draft = filingDrafts.GetValueOrDefault(selectedMeetingDirectory, filing);
            FiledTitleBox.Text = draft.Title;
            FiledProjectBox.Text = draft.Project;
            var entry = ReadMeetingListEntry(selectedMeetingDirectory);
            MeetingMetadataText.Text = entry.DateLabel + (entry.Imported ? UiText.T(" · 外部录音（实际开会日期未知）") : UiText.T(" · 本机录音")) + "\n" + selectedMeetingDirectory;
            RefreshSelectedExportStatus();
            MeetingLogExportStatus.Text = "";
            RefreshMeetingLog();
        }
        catch (Exception ex)
        {
            selectedMeetingDirectory = null; currentMix = null; mixedPath = null;
            selectedFilingBaseline = null;
            selectedMeetingImported = false;
            lastExportDirectory = null;
            SelectedMeetingText.Text = UiText.T("无法读取所选会议"); MeetingMetadataText.Text = UiText.T("请重新选择有效会议。");
            ExportStatus.Text = UiText.T("无法选择录音：") + ex.Message;
            RefreshMeetingLog();
        }
        UpdateControls();
        // Reopen only a validated existing render. Selection never silently creates another full mix.
        if (loadSavedMix && selectedMeetingDirectory is { } validDirectory && currentMix is null &&
            !string.Equals(activeExportMeetingDirectory, validDirectory, StringComparison.OrdinalIgnoreCase))
            savedMixWork = Task.WhenAll(savedMixWork, LoadSavedMixAsync(validDirectory));
        EnsureMeetingSubtitles();
        EnsureTranscriptVersions();
    }

    private async Task LoadSavedMixAsync(string directory)
    {
        using var cancellation = new CancellationTokenSource();
        savedMixCancellation = cancellation;
        savedMixLoadingDirectory = directory;
        SetSavedMixStatus(directory, UiText.T("正在校验已有回放文件；不会重新生成混音。"));
        UpdateControls();
        try
        {
            var result = await Task.Run(() => savedMixLoadOperation(directory, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (WindowCloseRequested || !string.Equals(selectedMeetingDirectory, directory, StringComparison.OrdinalIgnoreCase)) return;
            if (result is null)
                SetSavedMixStatus(directory, UiText.T("尚无完整混音；请点击“生成 / 重试完整混音”。原件保持不变。"));
            else
            {
                renderedMeetings[directory] = result; currentMix = result; mixedPath = SelectListeningPath(result);
                try { RestoreLegacyProcessingResult(directory, result); }
                catch (Exception ex)
                { SetMeetingExportStatus(directory, UiText.T("所选会议的处理记录无法读取；原件保持不变：") + ex.Message); }
                SetSavedMixStatus(directory, (mixedPath is null
                    ? UiText.T("完整 WAV 已校验；此超大文件暂不支持程序内回听，可按需导出 M4A。")
                    : UiText.T("已有完整回放通过来源、时间轴和文件校验；未重新生成音频。\n")) +
                    "\n" + string.Join("\n", result.Warnings.Select(UiText.T)));
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(savedMixCancellation, cancellation) && !string.Equals(activeExportMeetingDirectory, directory, StringComparison.OrdinalIgnoreCase))
                SetSavedMixStatus(directory, UiText.T("已有回放校验已取消；可重新选择会议后校验。"));
        }
        catch (Exception ex)
        {
            if (!WindowCloseRequested && !cancellation.IsCancellationRequested)
            {
                if (!meetingExportStatuses.ContainsKey(directory))
                {
                    try
                    {
                        var historical = await Task.Run(() => ReadHistoricalMixMetadata(directory, cancellation.Token), cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (historical is not null && !WindowCloseRequested && string.Equals(selectedMeetingDirectory, directory, StringComparison.OrdinalIgnoreCase))
                            RestoreLegacyProcessingResult(directory, historical);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception historyError)
                    { SetMeetingExportStatus(directory, UiText.T("所选会议的处理记录无法读取；原件保持不变：") + historyError.Message); }
                }
                if (!WindowCloseRequested && !cancellation.IsCancellationRequested)
                SetSavedMixStatus(directory, UiText.T("已有回放未能复用，请检查后点击“生成 / 重试完整混音”：") + ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(savedMixCancellation, cancellation))
            {
                savedMixCancellation = null;
                savedMixLoadingDirectory = null;
                CompleteSavedMixPlaybackRequest(directory);
            }
            UpdateControls();
        }
    }

    private void QueueMix(string directory)
    {
        if (WindowCloseRequested || deletingMeeting) return;
        if (!pendingMixes.Contains(directory, StringComparer.OrdinalIgnoreCase)) pendingMixes.Enqueue(directory);
        if (!exportBusy) backgroundWork = DrainMixQueueAsync();
    }

    private async Task DrainMixQueueAsync()
    {
        if (exportBusy || WindowCloseRequested || deletingMeeting) return;
        while (pendingMixes.Count > 0 && !WindowCloseRequested)
        {
            string directory = pendingMixes.Dequeue();
            exportBusy = true;
            activeExportMeetingDirectory = directory;
            activeProcessingLogCategory = "mix";
            if (string.Equals(selectedMeetingDirectory, directory, StringComparison.OrdinalIgnoreCase))
            {
                ClearSavedMixPlaybackRequest();
                savedMixCancellation?.Cancel();
            }
            savedMixStatuses.Remove(directory);
            using var cancellation = new CancellationTokenSource();
            exportCancellation = cancellation;
            SetMeetingExportStatus(directory, UiText.T("正在校验原件和完整回放文件…可取消，也可开始下一场录音。"));
            UpdateControls();
            try
            {
                var result = await Task.Run(() => renderOperation(directory, cancellation.Token));
                renderedMeetings[directory] = result;
                if (string.Equals(selectedMeetingDirectory, directory, StringComparison.OrdinalIgnoreCase))
                {
                    currentMix = result;
                    mixedPath = SelectListeningPath(result);
                }
                bool imported = AudioImportService.IsImportedMeeting(directory);
                savedMixStatuses.Remove(directory);
                SetMeetingExportStatus(directory, UiText.F($"{(result.SourceStorageComplete ? UiText.T("完整 WAV 已校验") : UiText.T("全时段 WAV 已生成（来源有完整性警告）"))}：{result.DurationUs / 1_000_000d:F3} 秒。") +
                    (imported ? UiText.T("外部录音可回听、导出或转写。") : result.CompressedPath is null ? UiText.T("已保留完整 WAV；需要 M4A 或 MP3 时可按需导出。") : UiText.T("M4A 已生成并完成解码检查。")) +
                    "\n" + string.Join(Environment.NewLine, result.Warnings.Select(UiText.T)), persist: true);
            }
            catch (OperationCanceledException) { SetMeetingExportStatus(directory, UiText.T("混音已取消；原轨保留，可重新生成。"), persist: true); }
            catch (Exception ex) { SetMeetingExportStatus(directory, UiText.T("混音失败；原轨保留，可重试：") + ex.Message, persist: true); }
            finally
            {
                exportCancellation = null; exportBusy = false; activeExportMeetingDirectory = null;
                UpdateControls();
            }
        }
    }

    private void RetryMix_Click(object sender, RoutedEventArgs e)
    {
        if (RetryMixButton.IsEnabled && selectedMeetingDirectory is { } directory)
        { savedMixCancellation?.Cancel(); QueueMix(directory); }
    }

    private static string? SelectListeningPath(FullMixResult? mix)
    {
        if (mix is null) return null;
        using var wave = new M2WaveReader(mix.WavePath);
        if (!wave.IsRf64) return mix.WavePath;
        // RF64 is the lossless large-file export. Its existence does not establish
        // WPF compatibility; use the already decoded/validated full M4A to listen.
        if (mix.CompressedPath is not { } compressed || mix.CompressedSha256?.Length != 64 || !File.Exists(compressed)) return null;
        RejectReparseAncestors(compressed);
        if (!string.Equals(Path.GetFullPath(Path.GetDirectoryName(compressed)!), Path.GetFullPath(mix.Directory), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(compressed), ".m4a", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(UiText.T("回听副本不在所选混音目录内。"));
        return compressed;
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e)
    {
        ClearSavedMixPlaybackRequest();
        exportCancellation?.Cancel();
        savedMixCancellation?.Cancel();
        if (activeExportMeetingDirectory is { } directory)
            SetMeetingExportStatus(directory, UiText.T("正在取消当前任务；等待文件收尾。"));
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!ExportButton.IsEnabled || currentMix is not { } mix || selectedMeetingDirectory is not { } directory) return;
        ExportOptions options;
        try
        {
            options = new ExportOptions(ExportFormatBox.SelectedIndex switch { 0 => M2ExportFormat.M4a, 1 => M2ExportFormat.Wave, 2 => M2ExportFormat.Mp3, _ => throw new InvalidOperationException(UiText.T("请选择导出格式。")) },
                long.Parse(MaxBytesBox.Text, NumberStyles.None, CultureInfo.InvariantCulture),
                double.Parse(MaxSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture),
                double.Parse(OverlapBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture), IncludeManifestBox.IsChecked == true);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            SetMeetingExportStatus(directory, UiText.T("请填写有效数字：大小为整数字节，时长和重叠为秒。"));
            return;
        }
        backgroundWork = ExportCurrentMixAsync(directory, mix, options, Path.Combine(dataRoot, "Exports"));
    }

    private async Task ExportCurrentMixAsync(string directory, FullMixResult mix, ExportOptions options, string destination)
    {
        exportBusy = true;
        activeExportMeetingDirectory = directory;
        activeProcessingLogCategory = "export";
        savedMixStatuses.Remove(directory);
        using var cancellation = new CancellationTokenSource();
        exportCancellation = cancellation;
        UpdateControls();
        SetMeetingExportStatus(directory, UiText.T("正在生成所选混音分段，并逐片检查实际字节与解码长度…"));
        try
        {
            var result = await Task.Run(() => exportOperation(mix, options, destination, cancellation.Token));
            SetMeetingExportStatus(directory, UiText.F($"本地导出完成：{result.Parts.Count} 片，共 {result.TotalBytes:N0} 字节。\n") +
                (mix.SourceStorageComplete ? "" : UiText.T("来源有完整性警告；分片保留经过时间和静音缺口，不代表声音全部录到。\n")) +
                string.Join(Environment.NewLine, result.Parts.Select(p => UiText.F($"{Path.GetFileName(p.Path)} · {p.ByteLength:N0} 字节 · {p.StartUs / 1_000_000d:F3}–{p.EndUs / 1_000_000d:F3} 秒"))) +
                (result.ManifestPath is null ? UiText.T("\n未附清单。") : UiText.T("\n已附所选时间映射清单。")) + UiText.T("\n可打开目录后自行上传。"),
                persist: true, exportDirectory: result.Directory, exportReceipt: result.LocalReceiptPath);
        }
        catch (OperationCanceledException) { SetMeetingExportStatus(directory, UiText.T("导出已取消；没有发布完整导出，原件保留。"), persist: true); }
        catch (Exception ex) { SetMeetingExportStatus(directory, UiText.T("导出失败；原件保留，可重试：") + ex.Message, persist: true); }
        finally
        {
            exportCancellation = null; exportBusy = false; activeExportMeetingDirectory = null;
            UpdateControls();
            // The one tracked task includes any queued mixes. Closing clears this
            // queue before cancellation, so a finally block cannot start more work.
            if (!WindowCloseRequested) await DrainMixQueueAsync();
        }
    }

    private void CancelBackgroundForClose()
    {
        ClearSavedMixPlaybackRequest();
        transcriptCancellation?.Cancel();
        savedMixCancellation?.Cancel();
        pendingMixes.Clear();
        exportCancellation?.Cancel();
        if (!backgroundWork.IsCompleted)
            ExportStatus.Text = UiText.T("正在取消后台任务并等待文件收尾；完成后关闭窗口。");
    }

    private void OpenExport_Click(object sender, RoutedEventArgs e)
    {
        if (lastExportDirectory is null) return;
        try
        {
            RejectReparseAncestors(lastExportDirectory);
            if (!Directory.Exists(lastExportDirectory)) throw new DirectoryNotFoundException();
            Process.Start(new ProcessStartInfo(lastExportDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            if (selectedMeetingDirectory is { } directory)
                SetMeetingExportStatus(directory, UiText.T("无法打开导出目录：") + ex.Message);
        }
    }

    private sealed record Filing(string Title, string Project);

    private static Filing ReadFiling(string directory)
    {
        foreach (string name in new[] { "filing.json", "meeting.json" })
        {
            string path = Path.Combine(directory, name);
            RejectReparseAncestors(path);
            if (!File.Exists(path)) continue;
            if (new FileInfo(path).Length > 2_000_000) throw new InvalidDataException("会议信息超过读取上限。");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var node = json.RootElement;
            string title = node.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string project = node.TryGetProperty("project", out var p) ? p.GetString() ?? "未分类" : "未分类";
            return new(title.Length > 200 ? title[..200] : title, project.Length > 100 ? project[..100] : project);
        }
        return new(Path.GetFileName(directory), "未分类");
    }

    private static string MeetingDisplayName(string directory)
    {
        try { var item = ReadFiling(directory); return $"{item.Title} · {item.Project}"; }
        catch { return Path.GetFileName(directory) + " · 信息待检查"; }
    }

    private void FileMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (!FileMeetingButton.IsEnabled || selectedMeetingDirectory is not { } selectedDirectory) return;
        try
        {
            // The visible meeting can be the newly saved session while the history
            // picker still highlights an older meeting. File exactly what is shown.
            string directory = ValidateRecordingDirectory(selectedDirectory);
            string title = FiledTitleBox.Text.Trim(), project = FiledProjectBox.Text.Trim();
            if (title.Length is < 1 or > 200 || project.Length > 100) throw new InvalidDataException(UiText.T("标题须为 1–200 字；项目最多 100 字。"));
            string path = Path.Combine(directory, "filing.json");
            RejectReparseAncestors(path);
            string temporary = Path.Combine(directory, "filing-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new { title, project = project.Length == 0 ? "未分类" : project });
                stream.Flush(true);
            }
            RejectReparseAncestors(path);
            File.Move(temporary, path, true);
            selectedFilingBaseline = new(title, project.Length == 0 ? "未分类" : project);
            filingDrafts.Remove(directory);
            SelectedMeetingText.Text = MeetingDisplayName(directory);
            RefreshRecovery_Click(this, new RoutedEventArgs());
            RecoveryStatus.Text = UiText.T("归档信息已保存；原始清单、音轨和时间索引未修改。");
            LogMeeting(directory, "edit", "会议标题/项目归档信息已保存；原始清单、音轨和时间索引未修改。");
        }
        catch (Exception ex) { RecoveryStatus.Text = UiText.T("归档失败：") + ex.Message; LogMeeting(selectedMeetingDirectory, "edit", "归档失败：" + ex.Message, "error"); }
    }
}
