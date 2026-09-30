using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ProjectEcho.Core;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private TranscriptionView? transcriptionView;
    private string? transcriptMeeting, importFile, mappingFile, whisperExecutable, whisperModel;
    private bool transcriptBusy, updatingRevisions;
    private Task transcriptWork = Task.CompletedTask;
    private CancellationTokenSource? transcriptCancellation;
    private TranscriptRevision? visibleRevision;
    private TranscriptRow? visibleSegment;
    private (string Path, long Milliseconds)? pendingTranscriptSeek;
    private sealed record RevisionRow(TranscriptRevision Revision)
    {
        public string Label => $"{Revision.ImportedAt.ToLocalTime():MM-dd HH:mm:ss} · {(Revision.ParentRevisionId is null ? "原稿" : "修订")} · {Revision.Provider} · {Revision.RevisionId}";
    }
    private sealed record TranscriptRow(string Directory, TranscriptRevision Revision, TranscriptSegment Segment, string Title)
    {
        public string Label => $"{Title} · {(Segment.StartMs is { } ms ? TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss") : "无时间")} · {Segment.Text}";
    }

    private void InitializeTranscription()
    {
        var view = transcriptionView = new TranscriptionView();
        TranscriptionContent.Content = view;
        view.ImportFileButton.Click += (_, _) => PickTranscriptFile();
        view.MappingFileButton.Click += (_, _) => PickMappingFile();
        view.ClearMappingButton.Click += (_, _) => { mappingFile = null; view.PartBox.Clear(); view.MappingFileText.Text = "未使用分片映射：字幕按完整会议时间导入。"; };
        view.ImportButton.Click += (_, _) => StartTranscriptWork(ImportTranscriptAsync);
        view.RefreshButton.Click += (_, _) => StartTranscriptWork(() => LoadTranscriptVersionsAsync());
        view.RevisionBox.SelectionChanged += (_, _) =>
        {
            if (!updatingRevisions && view.RevisionBox.SelectedItem is RevisionRow row && transcriptMeeting is { } directory)
                DisplayRevision(directory, row.Revision);
        };
        view.SegmentsBox.SelectionChanged += (_, _) => SelectTranscriptSegment();
        view.SearchButton.Click += (_, _) => StartTranscriptWork(SearchTranscriptsAsync);
        view.SaveRevisionButton.Click += (_, _) => StartTranscriptWork(SaveTranscriptRevisionAsync);
        view.SeekButton.Click += (_, _) => SeekTranscript();
        view.StopListeningButton.Click += (_, _) => { pendingTranscriptSeek = null; StopPlayback_Click(this, new RoutedEventArgs()); };
        view.WhisperExeButton.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Title = "选择可信的本地 whisper.cpp CLI", Filter = "Windows 程序|*.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) == true) { whisperExecutable = picker.FileName; view.WhisperExeText.Text = whisperExecutable; }
        };
        view.WhisperModelButton.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Title = "选择已下载并核对许可的 whisper.cpp 模型", Filter = "模型文件|*.bin;*.ggml;*.gguf|所有文件|*.*", CheckFileExists = true };
            if (picker.ShowDialog(this) == true) { whisperModel = picker.FileName; view.WhisperModelText.Text = whisperModel; }
        };
        view.SaveConfigurationButton.Click += (_, _) => SaveWhisperConfiguration();
        view.RunWhisperButton.Click += (_, _) => StartTranscriptWork(RunWhisperAsync);
        view.CancelButton.Click += (_, _) => { transcriptCancellation?.Cancel(); view.StatusText.Text = "正在取消并等待文字任务收尾…"; };
        player.MediaOpened += (_, _) =>
        {
            var pending = pendingTranscriptSeek; pendingTranscriptSeek = null;
            if (pending is null || WindowCloseRequested || !SessionRules.CanPlayback(state) || player.Source?.LocalPath != pending.Value.Path) return;
            player.Position = TimeSpan.FromMilliseconds(pending.Value.Milliseconds);
            player.Play();
        };
        LoadWhisperConfiguration();
    }

    private void UpdateTranscriptionControls()
    {
        if (transcriptionView is not { } view) return;
        bool open = !WindowCloseRequested, selected = selectedMeetingDirectory is not null;
        if (!string.Equals(transcriptMeeting, selectedMeetingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            transcriptMeeting = selectedMeetingDirectory;
            visibleRevision = null; visibleSegment = null;
            view.RevisionBox.ItemsSource = null; view.SegmentsBox.ItemsSource = null; view.EditBox.Clear();
            mappingFile = null; view.PartBox.Clear();
            view.MappingFileText.Text = "未使用分片映射：字幕按完整会议时间导入。";
        }
        view.MeetingText.Text = selected ? "当前会议：" + SelectedMeetingText.Text : "先在会议库选择一场已保存录音；可随后导入或识别。";
        view.ImportButton.IsEnabled = open && !transcriptBusy && selected && importFile is not null;
        view.RefreshButton.IsEnabled = open && !transcriptBusy && selected;
        view.SearchButton.IsEnabled = open && !transcriptBusy;
        view.RevisionBox.IsEnabled = open && !transcriptBusy;
        view.SaveRevisionButton.IsEnabled = open && !transcriptBusy && visibleSegment is not null;
        view.SeekButton.IsEnabled = open && !transcriptBusy && SessionRules.CanPlayback(state) && visibleSegment?.Segment.StartMs is not null;
        view.StopListeningButton.IsEnabled = open && SessionRules.CanPlayback(state);
        view.EditBox.IsEnabled = open && !transcriptBusy && visibleSegment is not null;
        view.RunWhisperButton.IsEnabled = open && !transcriptBusy && selected && currentMix is not null;
        view.CancelButton.IsEnabled = open && transcriptBusy;
        view.ImportFileButton.IsEnabled = view.MappingFileButton.IsEnabled = view.ClearMappingButton.IsEnabled = open && !transcriptBusy;
        view.SaveConfigurationButton.IsEnabled = view.WhisperExeButton.IsEnabled = view.WhisperModelButton.IsEnabled = open && !transcriptBusy;
    }

    private void PickTranscriptFile()
    {
        var picker = new OpenFileDialog { Title = "选择本地转写结果", Filter = "转写文字|*.txt;*.md;*.srt;*.vtt;*.json", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        importFile = picker.FileName; transcriptionView!.ImportFileText.Text = importFile; UpdateControls();
    }

    private void PickMappingFile()
    {
        var picker = new OpenFileDialog { Title = "选择这场混音的导出映射清单", Filter = "映射清单|*.json", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        mappingFile = picker.FileName; transcriptionView!.MappingFileText.Text = mappingFile;
    }

    private void StartTranscriptWork(Func<Task> operation)
    {
        if (WindowCloseRequested || transcriptBusy) return;
        transcriptWork = RunTranscriptWorkAsync(operation);
    }

    private async Task RunTranscriptWorkAsync(Func<Task> operation)
    {
        transcriptBusy = true;
        using var cancellation = new CancellationTokenSource(); transcriptCancellation = cancellation;
        UpdateControls();
        try { await operation(); }
        catch (OperationCanceledException) { transcriptionView!.StatusText.Text = "文字任务已取消；原始音频和已有修订保留。"; }
        catch (Exception ex) { transcriptionView!.StatusText.Text = "文字任务未完成，可修正后重试：" + ex.Message; }
        finally { transcriptCancellation = null; transcriptBusy = false; UpdateControls(); }
    }

    private (string Directory, string Id, long DurationMs, bool Synthetic) SelectedTranscriptMeeting()
    {
        string directory = ValidateRecordingDirectory(selectedMeetingDirectory ?? throw new InvalidOperationException("先选择一场已保存录音。"));
        string path = Path.Combine(directory, "meeting.json"); RejectReparseAncestors(path);
        if (new FileInfo(path).Length > 16_000_000) throw new InvalidDataException("会议信息超过读取上限。");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        if (root.GetProperty("schemaVersion").GetString() != "m1-recording-1") throw new InvalidDataException("这场录音的格式尚不支持文字归档。");
        long duration = root.GetProperty("duration100ns").GetInt64();
        if (duration <= 0 || duration > 86400L * 10_000_000) throw new InvalidDataException("会议时长无效。");
        return (directory, Path.GetFileName(directory), (duration + 9999) / 10000,
            root.TryGetProperty("isSynthetic", out var synthetic) && synthetic.GetBoolean());
    }

    private async Task ImportTranscriptAsync()
    {
        var view = transcriptionView!; var meeting = SelectedTranscriptMeeting();
        if (mappingFile is not null && currentMix is null) throw new InvalidOperationException("导入分片前请先为这场录音生成完整混音，以核对映射所属会议。");
        var request = new TranscriptImportRequest(meeting.Directory, meeting.Id, meeting.DurationMs,
            importFile ?? throw new InvalidOperationException("先选择文字文件。"), view.ProviderBox.Text.Trim(),
            ExportManifestPath: mappingFile, InputPartId: mappingFile is null ? null : view.PartBox.Text.Trim(),
            IsSynthetic: meeting.Synthetic, ExpectedMixSha256: currentMix?.WaveSha256);
        view.StatusText.Text = "正在解析、检查时间并保留原稿…";
        var revision = await Task.Run(() => TranscriptService.Import(request, transcriptCancellation!.Token));
        if (selectedMeetingDirectory == meeting.Directory) await LoadTranscriptVersionsAsync(revision.RevisionId);
        view.StatusText.Text = $"已为 {meeting.Id} 导入 {revision.Segments.Count} 段；原稿已保留。\n" + string.Join("\n", revision.Warnings);
    }

    private async Task LoadTranscriptVersionsAsync(string? preferredRevision = null)
    {
        string directory = ValidateRecordingDirectory(selectedMeetingDirectory ?? throw new InvalidOperationException("先选择会议。"));
        var token = transcriptCancellation?.Token ?? CancellationToken.None;
        var revisions = await Task.Run(() => TranscriptService.ListRevisions(directory, token));
        if (selectedMeetingDirectory != directory || WindowCloseRequested) return;
        var view = transcriptionView!;
        updatingRevisions = true;
        try
        {
            var rows = revisions.OrderByDescending(r => r.ImportedAt).Select(r => new RevisionRow(r)).ToArray();
            view.RevisionBox.ItemsSource = rows;
            view.RevisionBox.SelectedItem = rows.FirstOrDefault(r => r.Revision.RevisionId == preferredRevision) ?? rows.FirstOrDefault();
            if (view.RevisionBox.SelectedItem is RevisionRow row) DisplayRevision(directory, row.Revision);
            else { view.SegmentsBox.ItemsSource = null; visibleRevision = null; visibleSegment = null; }
            view.StatusText.Text = $"已读取 {rows.Length} 个独立版本；选择一条文字进行校订。";
        }
        finally { updatingRevisions = false; }
    }

    private void DisplayRevision(string directory, TranscriptRevision revision)
    {
        visibleRevision = revision; visibleSegment = null;
        var view = transcriptionView!;
        view.SegmentsBox.ItemsSource = revision.Segments.Select(s => new TranscriptRow(directory, revision, s, "本场")).ToArray();
        view.EditBox.Clear();
        view.StatusText.Text = $"{revision.Provider} · {revision.Segments.Count} 段 · {revision.ImportedAt.ToLocalTime():g}\n" + string.Join("\n", revision.Warnings);
        UpdateTranscriptionControls();
    }

    private void SelectTranscriptSegment()
    {
        var view = transcriptionView!;
        visibleSegment = view.SegmentsBox.SelectedItem as TranscriptRow;
        view.EditBox.Text = visibleSegment?.Segment.Text ?? "";
        view.SegmentTimeText.Text = visibleSegment is { Segment: var segment }
            ? $"{(segment.StartMs is { } start ? TimeSpan.FromMilliseconds(start).ToString(@"hh\:mm\:ss\.fff") : "无时间戳")} · {segment.TimestampQuality} · {segment.SpeakerLabel ?? "未标说话人"}"
            : "选择一条文字可校订；有时间戳时可定位回放。";
        UpdateTranscriptionControls();
    }

    private async Task SaveTranscriptRevisionAsync()
    {
        var row = visibleSegment ?? throw new InvalidOperationException("先选择文字段落。");
        var view = transcriptionView!;
        string directory = ValidateRecordingDirectory(row.Directory);
        string revisedText = view.EditBox.Text;
        var segments = row.Revision.Segments.Select(s => s.SegmentId == row.Segment.SegmentId ? s with { Text = revisedText } : s).ToArray();
        var revision = await Task.Run(() => TranscriptService.CreateRevision(directory, row.Revision.RevisionId, segments, "用户校订文字", transcriptCancellation!.Token));
        if (selectedMeetingDirectory == directory) await LoadTranscriptVersionsAsync(revision.RevisionId);
        view.StatusText.Text = $"已为 {row.Title}（{Path.GetFileName(directory)}）保存新修订；父版本、原稿和原始音频均保留。";
    }

    private async Task SearchTranscriptsAsync()
    {
        var view = transcriptionView!; string query = view.SearchBox.Text.Trim();
        if (query.Length is < 1 or > 200) throw new InvalidDataException("搜索词须为 1–200 字。");
        bool all = view.AllMeetingsBox.IsChecked == true;
        string? selected = selectedMeetingDirectory; string root = RecordingsRoot;
        var token = transcriptCancellation!.Token;
        view.StatusText.Text = "正在本地搜索转写版本…";
        var result = await Task.Run(() =>
        {
            RejectReparseAncestors(root);
            string[] directories = all && Directory.Exists(root) ? Directory.EnumerateDirectories(root).Take(500).ToArray()
                : selected is not null ? [selected] : [];
            var rows = new List<TranscriptRow>(); int skipped = 0;
            foreach (string directory in directories)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    RejectReparseAncestors(directory);
                    var cache = new Dictionary<string, TranscriptRevision>();
                    foreach (var hit in TranscriptService.Search(directory, query, token))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!cache.TryGetValue(hit.RevisionId, out var revision)) cache.Add(hit.RevisionId, revision = TranscriptService.Load(directory, hit.RevisionId));
                        rows.Add(new(directory, revision, revision.Segments.First(s => s.SegmentId == hit.SegmentId), MeetingDisplayName(directory)));
                        if (rows.Count >= 2000) break;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException) { skipped++; }
                if (rows.Count >= 2000) break;
            }
            return (Rows: rows, Skipped: skipped);
        }, token);
        if (selectedMeetingDirectory != selected || RecordingsRoot != root || WindowCloseRequested) return;
        view.SegmentsBox.ItemsSource = result.Rows; visibleSegment = null; view.EditBox.Clear();
        view.StatusText.Text = $"找到 {result.Rows.Count} 条（最多 2000 条、500 场）；{result.Skipped} 场无法读取。搜索保留版本信息，不合并或删除重复发言。";
    }

    private void SeekTranscript()
    {
        if (WindowCloseRequested || !SessionRules.CanPlayback(state) || visibleSegment is not { Segment.StartMs: { } milliseconds } row) return;
        try
        {
            ValidateRecordingDirectory(row.Directory);
            if (!renderedMeetings.TryGetValue(row.Directory, out var mix))
                throw new InvalidOperationException("请先在会议库选中这场录音，并生成完整混音，再从文字定位回听。");
            string path = SelectListeningPath(mix) ?? throw new InvalidOperationException("完整回听文件尚未就绪。");
            if (milliseconds < 0 || milliseconds * 1000L >= mix.DurationUs) throw new InvalidDataException("这条文字时间不在可回听范围内。");
            player.Stop(); player.Close();
            pendingTranscriptSeek = (path, milliseconds);
            player.Open(new Uri(path));
            transcriptionView!.StatusText.Text = "正在定位回听；外部转写时间为近似值，请结合声音核对。";
        }
        catch (Exception ex) { pendingTranscriptSeek = null; transcriptionView!.StatusText.Text = ex.Message; }
    }

    private WhisperConfiguration CurrentWhisperConfiguration() => new(whisperExecutable ?? "", whisperModel ?? "",
        (transcriptionView!.LanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto", 2,
        ExpectedModelSha256: string.IsNullOrWhiteSpace(transcriptionView.ModelHashBox.Text) ? null : transcriptionView.ModelHashBox.Text.Trim(),
        ModelLicenseAcknowledged: transcriptionView.ModelLicenseBox.IsChecked == true);

    private string WhisperConfigurationPath => Path.Combine(dataRoot, "settings", "whisper.json");

    private void LoadWhisperConfiguration()
    {
        if (transcriptionView is not { } view) return;
        whisperExecutable = whisperModel = null;
        view.WhisperExeText.Text = "未配置程序"; view.WhisperModelText.Text = "未配置模型";
        view.ModelHashBox.Clear(); view.ModelLicenseBox.IsChecked = false;
        try
        {
            string path = WhisperConfigurationPath; RejectReparseAncestors(path);
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 32000) throw new InvalidDataException("配置文件过大。");
            var config = JsonSerializer.Deserialize<WhisperConfiguration>(File.ReadAllText(path)) ?? throw new InvalidDataException("配置为空。");
            whisperExecutable = config.ExecutablePath; whisperModel = config.ModelPath;
            view.WhisperExeText.Text = whisperExecutable; view.WhisperModelText.Text = whisperModel;
            view.ModelHashBox.Text = config.ExpectedModelSha256 ?? "";
            view.ModelLicenseBox.IsChecked = config.ModelLicenseAcknowledged;
            view.LanguageBox.SelectedIndex = config.Language == "zh" ? 1 : config.Language == "en" ? 2 : 0;
        }
        catch (Exception ex) { view.StatusText.Text = "本地识别配置未加载：" + ex.Message; }
    }

    private void SaveWhisperConfiguration()
    {
        try
        {
            var config = CurrentWhisperConfiguration(); var readiness = WhisperTranscriber.Inspect(config);
            if (!readiness.Ready) throw new InvalidOperationException(readiness.Message);
            string path = WhisperConfigurationPath; RejectReparseAncestors(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, config); stream.Flush(true); }
                RejectReparseAncestors(path); File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            transcriptionView!.StatusText.Text = "本机配置已保存；开始识别时校验文件哈希。";
        }
        catch (Exception ex) { transcriptionView!.StatusText.Text = "配置未保存：" + ex.Message; }
    }

    private async Task RunWhisperAsync()
    {
        var meeting = SelectedTranscriptMeeting(); var mix = currentMix ?? throw new InvalidOperationException("请先生成完整混音。");
        var config = CurrentWhisperConfiguration(); var readiness = WhisperTranscriber.Inspect(config);
        if (!readiness.Ready) throw new InvalidOperationException(readiness.Message);
        var view = transcriptionView!; var token = transcriptCancellation!.Token;
        view.StatusText.Text = "正在校验混音并准备 16 kHz 会后识别副本…";
        var audio = await Task.Run(() => AsrAudioPreparation.Prepare(mix.WavePath, mix.WaveSha256,
            Path.Combine(meeting.Directory, "transcripts", "asr-inputs"), token), token);
        view.StatusText.Text = "本地识别正在运行；可取消，也可开始下一场录音。";
        var result = await Task.Run(() => WhisperTranscriber.RunAsync(new(meeting.Directory, meeting.Id, meeting.DurationMs,
            audio.Path, audio.Sha256, config, meeting.Synthetic), token), token);
        if (result.Revision is { } revision && selectedMeetingDirectory == meeting.Directory)
            await LoadTranscriptVersionsAsync(revision.RevisionId);
        view.StatusText.Text = $"{meeting.Id} · {result.Status} · {result.ElapsedMilliseconds / 1000d:F1} 秒\n{result.Message}";
    }
}
