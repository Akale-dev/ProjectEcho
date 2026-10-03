using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ProjectEcho.Core;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private TranscriptionView? transcriptionView;
    private AsrSetupHelpWindow? asrSetupHelp;
    private string? transcriptMeeting, importFile, mappingFile, whisperExecutable, whisperModel;
    private bool transcriptBusy, updatingRevisions, loadingWhisperConfiguration;
    private string? transcriptAutoLoadMeeting;
    private Task transcriptWork = Task.CompletedTask;
    private CancellationTokenSource? transcriptCancellation;
    private TranscriptRevision? visibleRevision;
    private TranscriptRow? visibleSegment;
    private readonly Dictionary<(string Directory, string Revision, string Segment), string> transcriptDrafts = new();
    private readonly Dictionary<string, string> reviewSelections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Directory, string Revision), string> segmentSelections = new();
    private (string Path, long Milliseconds)? pendingTranscriptSeek;
    private bool playbackOpened, transcriptOwnsPlayer;
    private sealed record RevisionRow(TranscriptRevision Revision)
    {
        public string Label => $"{Revision.ModelName ?? Revision.Provider} · {Revision.ImportedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} · {(Revision.ParentRevisionId is null ? UiText.T("原稿") : UiText.T("修订"))} · {Revision.RevisionId[..Math.Min(8, Revision.RevisionId.Length)]}";
    }
    private sealed record TranscriptRow(string Directory, TranscriptRevision Revision, TranscriptSegment Segment, string Title)
    {
        public string TimeLabel => Segment.StartMs is { } ms ? TimelineTimestamp.FromMilliseconds(ms) : UiText.T("无时间");
        public string Label => $"{Title} · {TimeLabel} · {Segment.Text}";
    }

    private void InitializeTranscription()
    {
        var view = transcriptionView = new TranscriptionView();
        TranscriptionContent.Content = view;
        view.ImportFileButton.Click += (_, _) => PickTranscriptFile();
        view.MappingFileButton.Click += (_, _) => PickMappingFile();
        view.ClearMappingButton.Click += (_, _) => { mappingFile = null; view.PartBox.Clear(); view.MappingFileText.Text = UiText.T("未使用分片映射：字幕按完整会议时间导入。"); };
        view.ImportButton.Click += (_, _) => StartTranscriptWork(ImportTranscriptAsync, "edit");
        view.RefreshButton.Click += (_, _) => StartTranscriptWork(() => LoadTranscriptVersionsAsync());
        view.WorkspaceTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, view.WorkspaceTabs)) EnsureTranscriptVersions();
        };
        view.RevisionBox.SelectionChanged += (_, _) =>
        {
            if (!updatingRevisions && view.RevisionBox.SelectedItem is RevisionRow row && transcriptMeeting is { } directory)
                DisplayRevision(directory, row.Revision);
        };
        view.SegmentsBox.SelectionChanged += (_, _) => SelectTranscriptSegment();
        view.SegmentInvoked += (_, _) => SeekTranscript();
        view.CopySegmentRequested += (_, _) => CopyTranscriptSegment();
        view.CopySegmentButton.Click += (_, _) => CopyTranscriptSegment();
        view.SearchButton.Click += (_, _) => StartTranscriptWork(SearchTranscriptsAsync);
        view.SaveRevisionButton.Click += (_, _) => StartTranscriptWork(SaveTranscriptRevisionAsync, "edit", visibleSegment?.Directory);
        view.ExportSubtitlesButton.Click += (_, _) => PickTranscriptExport();
        view.ExportTextButton.Click += (_, _) => PickTranscriptTextExport();
        view.SeekButton.Click += (_, _) => SeekTranscript();
        view.PlayListeningButton.Click += Playback_Click;
        view.PauseListeningButton.Click += PausePlayback_Click;
        view.PlaybackPositionSlider.ValueChanged += ReplayPosition_Changed;
        view.PlaybackSpeedBox.SelectionChanged += PlaybackSpeed_Changed;
        view.StopListeningButton.Click += (_, _) => { pendingTranscriptSeek = null; StopPlayback_Click(this, new RoutedEventArgs()); };
        view.WhisperExeButton.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Title = UiText.T("选择可信的本地 whisper.cpp CLI"), Filter = UiText.T("Windows 程序|*.exe"), CheckFileExists = true };
            if (picker.ShowDialog(this) == true) { whisperExecutable = picker.FileName; view.WhisperExeText.Text = whisperExecutable; if (WhisperFileIssue(whisperExecutable, true) is null) ClearValidationIssue(view.WhisperExeButton); SelectBundledWhisperForHardware(); }
        };
        view.WhisperModelButton.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Title = UiText.T("选择已下载并核对许可的 whisper.cpp 模型"), Filter = UiText.T("模型文件|*.bin|所有文件|*.*"), CheckFileExists = true };
            if (picker.ShowDialog(this) == true) { whisperModel = picker.FileName; view.WhisperModelText.Text = whisperModel; if (WhisperFileIssue(whisperModel, false) is null) ClearValidationIssue(view.WhisperModelButton); }
        };
        view.SaveConfigurationButton.Click += (_, _) => SaveWhisperConfiguration();
        view.WhisperExeHelpButton.Click += (_, _) => ShowAsrSetupHelp(0);
        view.WhisperModelHelpButton.Click += (_, _) => ShowAsrSetupHelp(1);
        view.RunWhisperButton.Click += (_, _) =>
        {
            if (TryValidateWhisperConfiguration(requireAudio: true, out _)) StartTranscriptWork(RunWhisperAsync, "asr");
        };
        view.CancelButton.Click += (_, _) => { transcriptCancellation?.Cancel(); view.StatusText.Text = UiText.T("正在取消并等待文字任务收尾…"); };
        AttachPlaybackPlayer(player);
        TrackValidationCorrection(view.ThreadsBox, () => ValidNumber(view.ThreadsBox.Text, 1, 64));
        TrackValidationCorrection(view.GpuDeviceBox, () => ValidNumber(view.GpuDeviceBox.Text, 0, 63));
        TrackValidationCorrection(view.HardwareBox, () => Enum.TryParse<LocalTranscriptionHardware>((view.HardwareBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var hardware) && Enum.IsDefined(hardware));
        view.HardwareBox.SelectionChanged += (_, _) => SelectBundledWhisperForHardware();
        TrackValidationCorrection(view.LanguageBox, () => view.LanguageBox.SelectedItem is ComboBoxItem);
        TrackValidationCorrection(view.ModelLicenseBox, () => view.ModelLicenseBox.IsChecked == true);
        TrackValidationCorrection(view.InitialPromptBox, () => view.InitialPromptBox.Text.Length <= 4000 && !view.InitialPromptBox.Text.Contains('\0'));
        LoadWhisperConfiguration();
        InitializeMeetingWhisperPromptPersistence();
    }

    private void ShowAsrSetupHelp(int topic)
    {
        if (asrSetupHelp is null)
        {
            var window = new AsrSetupHelpWindow(topic) { Owner = this };
            foreach (object key in Resources.Keys) window.Resources[key] = Resources[key];
            asrSetupHelp = window;
            window.Closed += (_, _) => { if (ReferenceEquals(asrSetupHelp, window)) asrSetupHelp = null; };
            window.Show();
        }
        else { asrSetupHelp.TopicTabs.SelectedIndex = topic; asrSetupHelp.Activate(); }
    }

    private void AttachPlaybackPlayer(MediaPlayer ownedPlayer, string? meetingDirectory = null)
    {
        ownedPlayer.MediaOpened += (_, _) =>
        {
            // MediaOpened is asynchronous. A closed/replaced player's late event
            // must never consume a newer request, including one for the same URI.
            if (!ReferenceEquals(ownedPlayer, player) || !transcriptOwnsPlayer || player.Source is null) return;
            playbackOpened = true;
            ResetDisplayPlaybackClock();
            playbackDurationMs ??= player.NaturalDuration.HasTimeSpan ? (long)player.NaturalDuration.TimeSpan.TotalMilliseconds : null;
            UpdateReplayControls();
            var pending = pendingTranscriptSeek;
            if (pending is null) return;
            pendingTranscriptSeek = null;
            if (WindowCloseRequested || deletingMeeting || !SessionRules.CanPlayback(state)) { ResetTranscriptPlayback(); return; }
            if (!string.Equals(player.Source?.LocalPath, pending.Value.Path, StringComparison.OrdinalIgnoreCase)) return;
            try { PlayTranscriptPosition(pending.Value.Milliseconds); }
            catch (Exception ex)
            {
                ResetTranscriptPlayback();
                transcriptionView!.StatusText.Text = UiText.T("回听未启动：") + ex.Message;
                LogMeeting(meetingDirectory, "diagnostic", transcriptionView.StatusText.Text, "error");
            }
        };
        ownedPlayer.MediaFailed += (_, e) =>
        {
            HandlePlaybackFailure(ownedPlayer, meetingDirectory, e.ErrorException);
        };
        ownedPlayer.MediaEnded += (_, _) =>
        {
            if (ReferenceEquals(ownedPlayer, player) && transcriptOwnsPlayer)
            {
                playbackPaused = true; UpdatePlaybackTransport();
                ResetDisplayPlaybackClock(); UpdatePlaybackPosition();
                transcriptionView!.StatusText.Text = UiText.T("本场录音已播放到结尾；可单击任意带时间的字幕重新回听。");
            }
        };
    }

    private void ResetTranscriptPlayback()
    {
        ClearSavedMixPlaybackRequest();
        double volume = player.Volume; bool muted = player.IsMuted;
        pendingTranscriptSeek = null; playbackOpened = false; transcriptOwnsPlayer = false;
        player.Stop(); player.Close();
        player.Volume = volume; player.IsMuted = muted;
        playbackPaused = false; playbackDurationMs = null;
        ResetDisplayPlaybackClock();
        UpdatePlaybackTransport();
        UpdatePlaybackPosition();
        UpdateReplayControls();
    }

    private void HandlePlaybackFailure(MediaPlayer ownedPlayer, string? meetingDirectory, Exception error)
    {
        // The source meeting is captured with this player. A late event from an
        // old/closed player must not be assigned to the currently selected meeting.
        if (!ReferenceEquals(ownedPlayer, player) || !transcriptOwnsPlayer || WindowCloseRequested) return;
        ResetTranscriptPlayback();
        string message = UiText.T("回放失败：") + error.Message;
        StatusBox.Text = message;
        LogMeeting(meetingDirectory, "diagnostic", message, "error");
        transcriptionView!.StatusText.Text = UiText.T("回听失败，可重新单击字幕重试：") + error.Message;
    }

    private void ReplaceTranscriptPlayer(string? meetingDirectory = null)
    {
        // Preserve the user's volume/mute settings across a new media session.
        double volume = player.Volume; bool muted = player.IsMuted;
        ResetTranscriptPlayback();
        player = new MediaPlayer { Volume = volume, IsMuted = muted };
        AttachPlaybackPlayer(player, meetingDirectory);
        transcriptOwnsPlayer = true;
    }

    private void PlayTranscriptPosition(long milliseconds)
    {
        // The decoded mix timeline is the authority (validated by SeekTranscript).
        // WPF/Windows can round M4A NaturalDuration down to a whole second, so it
        // must not reject a valid subtitle near the end of that listening copy.
        if (!player.HasAudio) throw new InvalidDataException(UiText.T("回听文件未包含可播放音频。"));
        SeekPlaybackMilliseconds(milliseconds);
        player.Play();
        playbackPaused = false; UpdatePlaybackTransport();
        ResetDisplayPlaybackClock();
        transcriptionView!.StatusText.Text = UiText.F($"正在从 {TimelineTimestamp.FromMilliseconds(milliseconds)} 回听；字幕时间为近似值，请结合声音核对。");
    }

    private void UpdateTranscriptionControls()
    {
        if (transcriptionView is not { } view) return;
        SwitchMeetingWhisperConfiguration(selectedMeetingDirectory);
        if (string.IsNullOrEmpty(whisperExecutable)) view.WhisperExeText.Text = UiText.T("未配置程序");
        if (string.IsNullOrEmpty(whisperModel)) view.WhisperModelText.Text = UiText.T("未配置模型");
        bool open = !WindowCloseRequested && !deletingMeeting, selected = selectedMeetingDirectory is not null;
        if ((!open || !SessionRules.CanPlayback(state)) && (pendingTranscriptSeek is not null || transcriptOwnsPlayer))
            ResetTranscriptPlayback();
        if (!string.Equals(transcriptMeeting, selectedMeetingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            RememberTranscriptDraft();
            transcriptMeeting = selectedMeetingDirectory;
            transcriptAutoLoadMeeting = null;
            visibleRevision = null; visibleSegment = null;
            view.RevisionBox.ItemsSource = null; view.SegmentsBox.ItemsSource = null; view.EditBox.Clear();
            mappingFile = null; view.PartBox.Clear();
            view.MappingFileText.Text = UiText.T("未使用分片映射：字幕按完整会议时间导入。");
        }
        view.MeetingText.Text = selected ? UiText.T("当前会议：") + SelectedMeetingText.Text : UiText.T("先在会议库选择一场已保存录音；可随后导入或识别。");
        if (recognitionMeetingDirectory is { } runningDirectory)
            view.MeetingText.Text += " · " + UiText.T("识别中…") + " " + MeetingDisplayName(runningDirectory);
        view.ImportButton.IsEnabled = open && !transcriptBusy && selected && importFile is not null;
        view.RefreshButton.IsEnabled = open && !transcriptBusy && selected;
        view.SearchButton.IsEnabled = open && !transcriptBusy;
        view.RevisionBox.IsEnabled = open && !transcriptBusy;
        view.SaveRevisionButton.IsEnabled = open && !transcriptBusy && visibleSegment is not null;
        var exportRevision = visibleSegment?.Revision ?? visibleRevision;
        view.ExportSubtitlesButton.IsEnabled = open && !transcriptBusy && exportRevision is { Segments.Count: > 0 }
            && exportRevision.Segments.All(segment => segment.StartMs is not null && segment.EndMs is not null);
        view.ExportTextButton.IsEnabled = open && !transcriptBusy && exportRevision is { Segments.Count: > 0 };
        view.CopySegmentButton.IsEnabled = open && visibleSegment is not null;
        view.SeekButton.IsEnabled = open && !transcriptBusy && SessionRules.CanPlayback(state) && visibleSegment?.Segment.StartMs is not null;
        view.StopListeningButton.IsEnabled = open && SessionRules.CanPlayback(state);
        view.EditBox.IsEnabled = open && !transcriptBusy && visibleSegment is not null;
        // Leave validation reachable: a missing meeting/mix gets an actionable field outline.
        view.RunWhisperButton.IsEnabled = open && !transcriptBusy;
        view.AudioReadinessText.Visibility = currentMix is null ? Visibility.Visible : Visibility.Collapsed;
        view.AudioReadinessText.Text = UiText.T(selectedMeetingDirectory is { } loadingDirectory && IsLoadingSavedMix(loadingDirectory)
            ? "正在校验已有回放文件；不会重新生成混音。" : "请先生成完整混音。");
        if (selected) ClearValidationIssue(view.MeetingText);
        view.MeetingText.Visibility = !selected && HasValidationIssue(view.MeetingText) ? Visibility.Visible : Visibility.Collapsed;
        if (currentMix is not null) ClearValidationIssue(view.AudioReadinessText);
        view.CancelButton.IsEnabled = open && transcriptBusy;
        view.ImportFileButton.IsEnabled = view.MappingFileButton.IsEnabled = view.ClearMappingButton.IsEnabled = open && !transcriptBusy;
        view.SaveConfigurationButton.IsEnabled = view.WhisperExeButton.IsEnabled = view.WhisperModelButton.IsEnabled = open && !transcriptBusy;
        view.HardwareBox.IsEnabled = view.ThreadsBox.IsEnabled = view.GpuDeviceBox.IsEnabled =
            view.LanguageBox.IsEnabled = view.ModelLicenseBox.IsEnabled = open && !transcriptBusy;
        view.InitialPromptBox.IsEnabled = open && !transcriptBusy && selected && meetingWhisperSettingsReady;
    }

    private void PickTranscriptFile()
    {
        var picker = new OpenFileDialog { Title = UiText.T("选择本地转写结果"), Filter = UiText.T("转写文字|*.txt;*.md;*.srt;*.vtt;*.json"), CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        importFile = picker.FileName; transcriptionView!.ImportFileText.Text = importFile; UpdateControls();
    }

    private void PickMappingFile()
    {
        var picker = new OpenFileDialog { Title = UiText.T("选择这场混音的导出映射清单"), Filter = UiText.T("映射清单|*.json"), CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        mappingFile = picker.FileName; transcriptionView!.MappingFileText.Text = mappingFile;
    }

    private void StartTranscriptWork(Func<Task> operation, string? logCategory = null, string? logDirectory = null)
    {
        if (WindowCloseRequested || deletingMeeting || transcriptBusy) return;
        transcriptWork = RunTranscriptWorkAsync(operation, logCategory, logDirectory ?? selectedMeetingDirectory);
    }

    private void EnsureTranscriptVersions()
    {
        if (!initialized || WindowCloseRequested || deletingMeeting || transcriptBusy || transcriptionView is not { } view ||
            MainTabs.SelectedIndex != 1 || MeetingTabs.SelectedIndex != 3 || view.WorkspaceTabs.SelectedIndex != 1 ||
            selectedMeetingDirectory is not { } directory || transcriptAutoLoadMeeting == directory) return;
        // One automatic attempt per meeting selection. A failed read remains visible and
        // can be retried explicitly; returning to the tab preserves unsaved edits.
        transcriptAutoLoadMeeting = directory;
        StartTranscriptWork(() => LoadTranscriptVersionsAsync());
    }

    private async Task RunTranscriptWorkAsync(Func<Task> operation, string? logCategory = null, string? logDirectory = null)
    {
        transcriptBusy = true;
        using var cancellation = new CancellationTokenSource(); transcriptCancellation = cancellation;
        UpdateControls();
        if (logCategory is not null) LogMeeting(logDirectory, logCategory, "文字任务已开始。Ver. M3H");
        try { await operation(); }
        catch (OperationCanceledException)
        {
            if (string.Equals(selectedMeetingDirectory, logDirectory, StringComparison.OrdinalIgnoreCase))
                transcriptionView!.StatusText.Text = UiText.T("文字任务已取消；原始音频和已有修订保留。");
            if (logCategory is not null) LogMeeting(logDirectory, logCategory, "文字任务已取消；原件保留。", "warning");
        }
        catch (Exception ex)
        {
            if (string.Equals(selectedMeetingDirectory, logDirectory, StringComparison.OrdinalIgnoreCase))
                transcriptionView!.StatusText.Text = UiText.T("文字任务未完成，可修正后重试：") + ex.Message;
            LogMeeting(logDirectory, logCategory ?? "diagnostic", "文字任务失败：" + ex.Message, "error");
        }
        finally
        {
            transcriptCancellation = null; transcriptBusy = false;
            transcriptionView!.ProgressPanel.Visibility = Visibility.Collapsed;
            UpdateControls(); EnsureTranscriptVersions();
        }
    }

    private (string Directory, string Id, long DurationMs, bool Synthetic) SelectedTranscriptMeeting()
    {
        string directory = ValidateRecordingDirectory(selectedMeetingDirectory ?? throw new InvalidOperationException(UiText.T("先选择一场已保存录音。")));
        string path = Path.Combine(directory, "meeting.json"); RejectReparseAncestors(path);
        if (new FileInfo(path).Length > 16_000_000) throw new InvalidDataException(UiText.T("会议信息超过读取上限。"));
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        if (root.GetProperty("schemaVersion").GetString() is not ("m1-recording-1" or "m3-audio-import-1")) throw new InvalidDataException(UiText.T("这场录音的格式尚不支持文字归档。"));
        long duration = root.GetProperty("duration100ns").GetInt64();
        if (duration <= 0 || duration > 86400L * 10_000_000) throw new InvalidDataException(UiText.T("会议时长无效。"));
        return (directory, Path.GetFileName(directory), (duration + 9999) / 10000,
            root.TryGetProperty("isSynthetic", out var synthetic) && synthetic.GetBoolean());
    }

    private async Task ImportTranscriptAsync()
    {
        var view = transcriptionView!; var meeting = SelectedTranscriptMeeting();
        if (mappingFile is not null && currentMix is null) throw new InvalidOperationException(UiText.T("导入分片前请先为这场录音生成完整混音，以核对映射所属会议。"));
        var request = new TranscriptImportRequest(meeting.Directory, meeting.Id, meeting.DurationMs,
            importFile ?? throw new InvalidOperationException(UiText.T("先选择文字文件。")), view.ProviderBox.Text.Trim(),
            ExportManifestPath: mappingFile, InputPartId: mappingFile is null ? null : view.PartBox.Text.Trim(),
            IsSynthetic: meeting.Synthetic, ExpectedMixSha256: currentMix?.WaveSha256);
        view.StatusText.Text = UiText.T("正在解析、检查时间并保留原稿…");
        var revision = await Task.Run(() => TranscriptService.Import(request, transcriptCancellation!.Token));
        LogMeeting(meeting.Directory, "edit", $"已导入 {revision.Segments.Count} 段转写；原稿已保留。\n" + string.Join("\n", revision.Warnings), eventId: "legacy-original-" + revision.RevisionId, timestamp: revision.ImportedAt);
        if (selectedMeetingDirectory == meeting.Directory)
        {
            await LoadTranscriptVersionsAsync(revision.RevisionId);
            if (selectedMeetingDirectory == meeting.Directory && !WindowCloseRequested) view.WorkspaceTabs.SelectedIndex = 1;
        }
        view.StatusText.Text = UiText.F($"已为 {meeting.Id} 导入 {revision.Segments.Count} 段；原稿已保留。\n") + string.Join("\n", revision.Warnings);
    }

    private async Task LoadTranscriptVersionsAsync(string? preferredRevision = null)
    {
        if (deletingMeeting) return;
        string directory = ValidateRecordingDirectory(selectedMeetingDirectory ?? throw new InvalidOperationException(UiText.T("先选择会议。")));
        var token = transcriptCancellation?.Token ?? CancellationToken.None;
        var revisions = await Task.Run(() => TranscriptService.ListRevisions(directory, token));
        if (selectedMeetingDirectory != directory || WindowCloseRequested || deletingMeeting) return;
        transcriptAutoLoadMeeting = directory;
        var view = transcriptionView!;
        updatingRevisions = true;
        try
        {
            var rows = revisions.OrderByDescending(r => r.ImportedAt).Select(r => new RevisionRow(r)).ToArray();
            string? replayPreference = preferredRevision ?? (replayMeetingDirectory == directory && ReplayRevisionBox.SelectedItem is RevisionRow replayRow
                ? replayRow.Revision.RevisionId : null);
            SetReplayRevisions(directory, revisions, replayPreference);
            string? reviewPreference = preferredRevision ?? (view.RevisionBox.SelectedItem as RevisionRow)?.Revision.RevisionId
                ?? reviewSelections.GetValueOrDefault(directory);
            view.RevisionBox.ItemsSource = rows;
            view.RevisionBox.SelectedItem = rows.FirstOrDefault(r => r.Revision.RevisionId == reviewPreference) ?? rows.FirstOrDefault();
            if (view.RevisionBox.SelectedItem is RevisionRow row) DisplayRevision(directory, row.Revision);
            else { view.SegmentsBox.ItemsSource = null; visibleRevision = null; visibleSegment = null; }
            view.StatusText.Text = UiText.F($"已读取 {rows.Length} 个独立版本；选择一条文字进行校订。");
        }
        finally { updatingRevisions = false; }
    }

    private void DisplayRevision(string directory, TranscriptRevision revision)
    {
        RememberTranscriptDraft();
        visibleRevision = revision; visibleSegment = null;
        reviewSelections[directory] = revision.RevisionId;
        var view = transcriptionView!;
        view.SegmentsBox.ItemsSource = revision.Segments.Select(s => new TranscriptRow(directory, revision, s, UiText.T("本场"))).ToArray();
        view.EditBox.Clear();
        if (segmentSelections.TryGetValue((directory, revision.RevisionId), out string? segmentId))
            view.SegmentsBox.SelectedItem = view.SegmentsBox.Items.Cast<TranscriptRow>().FirstOrDefault(row => row.Segment.SegmentId == segmentId);
        view.StatusText.Text = UiText.F($"{revision.ModelName ?? revision.Provider} · {revision.Segments.Count} 段 · {revision.ImportedAt.ToLocalTime():g}\n") + string.Join("\n", revision.Warnings);
        UpdateTranscriptionControls();
    }

    private void SelectTranscriptSegment()
    {
        RememberTranscriptDraft();
        var view = transcriptionView!;
        visibleSegment = view.SegmentsBox.SelectedItem as TranscriptRow;
        if (visibleSegment is { } row)
        {
            segmentSelections[(row.Directory, row.Revision.RevisionId)] = row.Segment.SegmentId;
            view.EditBox.Text = transcriptDrafts.GetValueOrDefault((row.Directory, row.Revision.RevisionId, row.Segment.SegmentId), row.Segment.Text);
        }
        else view.EditBox.Clear();
        view.SegmentTimeText.Text = visibleSegment is { Segment: var segment }
            ? $"{(segment.StartMs is { } start ? TimelineTimestamp.FromMilliseconds(start) : UiText.T("无时间戳"))} · {segment.TimestampQuality} · {segment.SpeakerLabel ?? UiText.T("未标说话人")}"
            : UiText.T("单击带时间的字幕可立即回听；纯文字可选择校订。");
        UpdateTranscriptionControls();
    }

    private void RememberTranscriptDraft()
    {
        if (visibleSegment is not { } row || transcriptionView is not { } view) return;
        var key = (row.Directory, row.Revision.RevisionId, row.Segment.SegmentId);
        if (view.EditBox.Text != row.Segment.Text) transcriptDrafts[key] = view.EditBox.Text;
        else transcriptDrafts.Remove(key);
    }

    private async Task SaveTranscriptRevisionAsync()
    {
        var row = visibleSegment ?? throw new InvalidOperationException(UiText.T("先选择文字段落。"));
        var view = transcriptionView!;
        string directory = ValidateRecordingDirectory(row.Directory);
        string revisedText = view.EditBox.Text;
        var segments = row.Revision.Segments.Select(s => s.SegmentId == row.Segment.SegmentId ? s with { Text = revisedText } : s).ToArray();
        var revision = await Task.Run(() => TranscriptService.CreateRevision(directory, row.Revision.RevisionId, segments, UiText.T("用户校订文字"), transcriptCancellation!.Token));
        LogMeeting(directory, "edit", "文字修订已保存；父版本、原稿和原始音频保留。", eventId: "legacy-reviewed-" + revision.RevisionId, timestamp: revision.ImportedAt);
        // A saved edit now belongs to its new immutable revision; the parent stays untouched.
        if (visibleSegment == row) { view.EditBox.Text = row.Segment.Text; }
        transcriptDrafts.Remove((row.Directory, row.Revision.RevisionId, row.Segment.SegmentId));
        if (selectedMeetingDirectory == directory) await LoadTranscriptVersionsAsync(revision.RevisionId);
        view.StatusText.Text = UiText.F($"已为 {row.Title}（{Path.GetFileName(directory)}）保存新修订；父版本、原稿和原始音频均保留。");
    }

    private void PickTranscriptExport()
    {
        if (WindowCloseRequested || transcriptBusy || !transcriptionView!.ExportSubtitlesButton.IsEnabled) return;
        var picker = new SaveFileDialog
        {
            Title = UiText.T("保存完整会议时间轴字幕"), Filter = UiText.T("SubRip 字幕|*.srt|WebVTT 字幕|*.vtt"),
            FileName = UiText.T("会议字幕.srt"), DefaultExt = ".srt", AddExtension = true, OverwritePrompt = true
        };
        if (picker.ShowDialog(this) == true) StartTranscriptWork(() => ExportTranscriptSubtitlesAsync(picker.FileName), "export", visibleSegment?.Directory ?? transcriptMeeting);
    }

    private async Task ExportTranscriptSubtitlesAsync(string destinationPath)
    {
        var revision = visibleSegment?.Revision ?? visibleRevision ?? throw new InvalidOperationException(UiText.T("先读取带时间的字幕版本。"));
        string directory = ValidateRecordingDirectory(visibleSegment?.Directory ?? transcriptMeeting ?? throw new InvalidOperationException(UiText.T("先选择字幕所属会议。")));
        string result = await Task.Run(() => TranscriptSubtitleExporter.Export(directory, revision.RevisionId, destinationPath, transcriptCancellation!.Token, overwrite: true));
        LogMeeting(directory, "export", "字幕已导出；保留完整会议时间轴和毫秒时间。");
        transcriptionView!.StatusText.Text = UiText.T("字幕已保存：") + result + UiText.T("\n保留完整会议时间轴和毫秒时间；模型/导入字幕的时间为近似值，需结合原录音校对。");
    }

    private void PickTranscriptTextExport()
    {
        if (WindowCloseRequested || transcriptBusy || !transcriptionView!.ExportTextButton.IsEnabled) return;
        var picker = new SaveFileDialog
        {
            Title = UiText.T("保存当前版本全文（不带时间轴）"), Filter = UiText.T("UTF-8 全文|*.txt"), FileName = UiText.T("会议全文.txt"),
            DefaultExt = ".txt", AddExtension = true, OverwritePrompt = true
        };
        if (picker.ShowDialog(this) == true) StartTranscriptWork(() => ExportTranscriptTextAsync(picker.FileName), "export", visibleSegment?.Directory ?? transcriptMeeting);
    }

    private async Task ExportTranscriptTextAsync(string destinationPath)
    {
        var revision = visibleSegment?.Revision ?? visibleRevision ?? throw new InvalidOperationException(UiText.T("先读取文字版本。"));
        string directory = ValidateRecordingDirectory(visibleSegment?.Directory ?? transcriptMeeting ?? throw new InvalidOperationException(UiText.T("先选择文字所属会议。")));
        string result = await Task.Run(() => TranscriptSubtitleExporter.ExportText(directory, revision.RevisionId, destinationPath, transcriptCancellation!.Token, overwrite: true));
        LogMeeting(directory, "export", "当前版本全文已导出（不带时间轴）。");
        transcriptionView!.StatusText.Text = UiText.T("全文已保存（不带时间轴）：") + result;
    }

    private void CopyTranscriptSegment()
    {
        if (!WindowCloseRequested && transcriptionView?.SegmentsBox.SelectedItem is TranscriptRow row)
            CopySegmentText(row, message => transcriptionView.StatusText.Text = message);
    }

    private static void CopySegmentText(TranscriptRow row, Action<string> setStatus)
    {
        try { Clipboard.SetText(row.Segment.Text); setStatus(UiText.T("已复制所选句子，不含时间戳。")); }
        catch (System.Runtime.InteropServices.ExternalException) { setStatus(UiText.T("剪贴板正被其他程序使用，请重试复制。")); }
    }

    private async Task SearchTranscriptsAsync()
    {
        var view = transcriptionView!; string query = view.SearchBox.Text.Trim();
        if (query.Length is < 1 or > 200) throw new InvalidDataException(UiText.T("搜索词须为 1–200 字。"));
        bool all = view.AllMeetingsBox.IsChecked == true;
        string? selected = selectedMeetingDirectory; string root = RecordingsRoot;
        var token = transcriptCancellation!.Token;
        view.StatusText.Text = UiText.T("正在本地搜索转写版本…");
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
        view.StatusText.Text = UiText.F($"找到 {result.Rows.Count} 条（最多 2000 条、500 场）；{result.Skipped} 场无法读取。搜索保留版本信息，不合并或删除重复发言。");
    }

    private void SeekTranscript()
    {
        if (WindowCloseRequested || deletingMeeting || transcriptBusy || !SessionRules.CanPlayback(state) || visibleSegment is not { Segment.StartMs: { } milliseconds } row) return;
        SeekTranscriptRow(row);
    }

    private void SeekTranscriptRow(TranscriptRow row)
    {
        if (WindowCloseRequested || deletingMeeting || !SessionRules.CanPlayback(state) || row.Segment.StartMs is not { } milliseconds) return;
        try
        {
            if (TryQueueSavedMixPlayback(milliseconds, row.Directory)) return;
            ValidateRecordingDirectory(row.Directory);
            if (!renderedMeetings.TryGetValue(row.Directory, out var mix))
                throw new InvalidOperationException(UiText.T("请先在会议库选中这场录音，并生成完整混音，再从文字定位回听。"));
            // Import manifests use portable "/" separators. Canonicalize before
            // comparing with MediaPlayer.Source.LocalPath, which uses Windows "\".
            string path = Path.GetFullPath(SelectListeningPath(mix) ?? throw new InvalidOperationException(UiText.T("完整回听文件尚未就绪。")));
            if (milliseconds < 0 || milliseconds * 1000L >= mix.DurationUs) throw new InvalidDataException(UiText.T("这条文字时间不在可回听范围内。"));
            OpenListeningPosition(path, milliseconds, mix.DurationUs, row.Directory);
            transcriptionView!.StatusText.Text = UiText.T("正在定位回听；外部转写时间为近似值，请结合声音核对。");
        }
        catch (Exception ex)
        {
            ResetTranscriptPlayback(); transcriptionView!.StatusText.Text = ex.Message;
            LogMeeting(row.Directory, "diagnostic", UiText.T("回听未启动：") + ex.Message, "error");
        }
    }

    private FrameworkElement[] WhisperValidationFields() =>
    [
        transcriptionView!.MeetingText, transcriptionView.AudioReadinessText,
        transcriptionView.WhisperExeButton, transcriptionView.WhisperModelButton,
        transcriptionView.ModelLicenseBox, transcriptionView.LanguageBox, transcriptionView.HardwareBox,
        transcriptionView.ThreadsBox, transcriptionView.GpuDeviceBox, transcriptionView.InitialPromptBox
    ];

    private bool TryValidateWhisperConfiguration(bool requireAudio, out WhisperConfiguration configuration)
    {
        configuration = null!;
        var view = transcriptionView!;
        var fields = WhisperValidationFields();
        ClearValidationIssues(fields);
        var messages = new List<string>();
        void Issue(FrameworkElement field, string message)
        {
            if (ReferenceEquals(field, view.MeetingText)) view.MeetingText.Visibility = Visibility.Visible;
            string localized = UiText.T(message); SetValidationIssue(field, localized); messages.Add(localized);
        }
        if (requireAudio && selectedMeetingDirectory is null) Issue(view.MeetingText, "先选择一场已保存录音。");
        else if (requireAudio && currentMix is null) Issue(view.AudioReadinessText,
            selectedMeetingDirectory is { } loadingDirectory && IsLoadingSavedMix(loadingDirectory)
                ? "正在校验已有回放文件；不会重新生成混音。" : "请先生成完整混音。");
        if (WhisperFileIssue(whisperExecutable, true) is { } executableIssue) Issue(view.WhisperExeButton, executableIssue);
        if (WhisperFileIssue(whisperModel, false) is { } modelIssue) Issue(view.WhisperModelButton, modelIssue);
        if (view.ModelLicenseBox.IsChecked != true) Issue(view.ModelLicenseBox, "请先确认所选本地模型的来源与许可。");
        if (view.LanguageBox.SelectedItem is not ComboBoxItem) Issue(view.LanguageBox, "语言或 CPU 线程设置无效。");
        if (!Enum.TryParse<LocalTranscriptionHardware>((view.HardwareBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var hardware) || !Enum.IsDefined(hardware))
            Issue(view.HardwareBox, "请选择识别硬件。");
        else if (hardware == LocalTranscriptionHardware.Cuda)
            Issue(view.HardwareBox, "CUDA 已不再提供，请选择 CPU、自动或 Vulkan。");
        if (requireAudio && selectedMeetingDirectory is not null && !meetingWhisperSettingsReady)
            Issue(view.InitialPromptBox, "本场识别配置无法读取；请先处理配置文件错误，避免覆盖原有选项。");
        if (!ValidNumber(view.ThreadsBox.Text, 1, 64)) Issue(view.ThreadsBox, "CPU 线程须为 1–64。");
        if (!ValidNumber(view.GpuDeviceBox.Text, 0, 63)) Issue(view.GpuDeviceBox, "GPU 设备编号须为 0–63。");
        if (view.InitialPromptBox.Text.Length > 4000 || view.InitialPromptBox.Text.Contains('\0'))
            Issue(view.InitialPromptBox, "初始提示词不能超过 4000 字符或包含空字符；模型会按其上下文 token 上限截取。");
        if (messages.Count == 0)
        {
            try
            {
                configuration = CurrentWhisperConfiguration();
                var readiness = WhisperTranscriber.Inspect(configuration);
                if (!readiness.Ready) Issue(view.WhisperModelButton, readiness.Message);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { Issue(view.WhisperExeButton, ex.Message); }
        }
        if (messages.Count == 0) return true;
        string message = string.Join("\n", messages.Distinct());
        view.StatusText.Text = message;
        if (requireAudio) LogMeeting(selectedMeetingDirectory, "asr", message, "error");
        FocusFirstValidationIssue(fields);
        return false;
    }

    private static bool ValidNumber(string text, int minimum, int maximum) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= minimum && number <= maximum;

    private static string? WhisperFileIssue(string? path, bool executable)
    {
        if (string.IsNullOrWhiteSpace(path)) return "尚未配置本地 whisper.cpp 或模型；可正常录音、导出及导入转写。";
        try
        {
            string full = Path.GetFullPath(path), root = Path.GetPathRoot(full)!;
            if (full.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType == DriveType.Network || full.AsSpan(root.Length).Contains(':'))
                return "转写仅使用本地磁盘，不接受网络路径或备用数据流。";
            RejectReparseAncestors(full);
            if (!File.Exists(full)) return "本地程序或模型文件不存在；不会自动下载。";
            if (executable && !string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase))
                return "请选择已信任的 whisper-cli.exe，不能运行脚本或命令行包装器。";
            if (new FileInfo(full).Length == 0) return "程序或模型文件为空。";
            if (!executable && Path.GetFileNameWithoutExtension(full).Length > 200) return "模型显示名称不能超过 200 字符。";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return ex.Message; }
    }

    private WhisperConfiguration CurrentWhisperConfiguration()
    {
        var view = transcriptionView!;
        if (!int.TryParse(view.ThreadsBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int threads) || threads is < 1 or > 64)
            throw new InvalidDataException(UiText.T("CPU 线程须为 1–64。"));
        if (!int.TryParse(view.GpuDeviceBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int device) || device is < 0 or > 63)
            throw new InvalidDataException(UiText.T("GPU 设备编号须为 0–63。"));
        if (!Enum.TryParse<LocalTranscriptionHardware>((view.HardwareBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var hardware) || !Enum.IsDefined(hardware))
            throw new InvalidDataException(UiText.T("请选择识别硬件。"));
        if (hardware == LocalTranscriptionHardware.Cuda)
            throw new InvalidDataException(UiText.T("CUDA 已不再提供，请选择 CPU、自动或 Vulkan。"));
        string executable = string.IsNullOrWhiteSpace(whisperExecutable) ? "" :
            PortableWhisperConfiguration.SelectBundledExecutable(whisperExecutable, hardware, AppContext.BaseDirectory);
        return new(executable, whisperModel ?? "",
            (view.LanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto", threads,
            ModelName: string.IsNullOrEmpty(whisperModel) ? null : Path.GetFileNameWithoutExtension(whisperModel),
            ModelLicenseAcknowledged: view.ModelLicenseBox.IsChecked == true,
            Hardware: hardware, GpuDeviceIndex: device,
            InitialPrompt: string.IsNullOrWhiteSpace(view.InitialPromptBox.Text) ? null : view.InitialPromptBox.Text);
    }

    private string WhisperConfigurationPath => Path.Combine(dataRoot, "settings", "whisper.json");

    private void SelectBundledWhisperForHardware()
    {
        if (loadingWhisperConfiguration || transcriptBusy || transcriptionView is not { } view || string.IsNullOrWhiteSpace(whisperExecutable) ||
            !Enum.TryParse<LocalTranscriptionHardware>((view.HardwareBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var hardware)) return;
        try
        {
            string selected = PortableWhisperConfiguration.SelectBundledExecutable(whisperExecutable, hardware, AppContext.BaseDirectory);
            if (string.Equals(selected, whisperExecutable, StringComparison.OrdinalIgnoreCase)) return;
            whisperExecutable = selected; view.WhisperExeText.Text = selected;
            ClearValidationIssue(view.WhisperExeButton);
            string message = UiText.T("已按所选硬件切换随包识别程序：") + selected;
            view.StatusText.Text = message;
            LogMeeting(selectedMeetingDirectory, "asr", message,
                exportSummary: UiText.T("已按所选硬件切换随包识别程序：") + hardware);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { view.StatusText.Text = ex.Message; SetValidationIssue(view.WhisperExeButton, ex.Message); }
    }

    private void LoadWhisperConfiguration()
    {
        if (transcriptionView is not { } view) return;
        FlushMeetingWhisperPrompt();
        asrConfigurationMeeting = null;
        asrConfigurationDataRoot = null;
        try
        {
            var config = ReadDefaultWhisperConfiguration();
            ApplyMeetingWhisperConfiguration(config, null);
            asrConfigurationDataRoot = dataRoot;
            meetingWhisperSettingsReady = true;
        }
        catch (Exception ex)
        {
            ApplyMeetingWhisperConfiguration(null, null); meetingWhisperSettingsReady = false;
            view.StatusText.Text = UiText.T("本地识别配置未加载：") + ex.Message;
        }
    }

    private void SaveWhisperConfiguration()
    {
        try
        {
            if (!TryValidateWhisperConfiguration(requireAudio: false, out var config)) return;
            string? recoveryNotice = EnsureMeetingWhisperBaseline(recoverUnreadableDefaults: true);
            FlushMeetingWhisperPrompt();
            MeetingWhisperSettingsStore.SaveDefaults(WhisperConfigurationPath, config, AppContext.BaseDirectory);
            transcriptionView!.StatusText.Text = UiText.T("默认识别配置已保存，仅应用于新会议；提示词只保存在当前会议。") +
                (recoveryNotice is null ? "" : "\n" + UiText.T(recoveryNotice));
        }
        catch (Exception ex) { transcriptionView!.StatusText.Text = UiText.T("配置未保存：") + ex.Message; }
    }

    private async Task RunWhisperAsync()
    {
        var meeting = SelectedTranscriptMeeting(); var mix = currentMix ?? throw new InvalidOperationException(UiText.T("请先生成完整混音。"));
        ILocalTranscriber transcriber = LocalTranscriptionBackends.Default;
        var whisperConfig = CurrentWhisperConfiguration();
        MeetingWhisperSettingsStore.SaveAttemptConfiguration(meeting.Directory, whisperConfig, AppContext.BaseDirectory);
        meetingWhisperPromptDirty = false;
        pendingWhisperPrompts.Remove(meeting.Directory);
        var config = LocalTranscriptionConfiguration.FromWhisper(whisperConfig); var readiness = transcriber.Inspect(config);
        if (!readiness.Ready) throw new InvalidOperationException(UiText.T(readiness.Message));
        var view = transcriptionView!; var token = transcriptCancellation!.Token;
        SetRecognitionMeeting(meeting.Directory);
        UpdateTranscriptionControls();
        try
        {
            using var display = new RecognitionProgressDisplay(view,
                () => transcriptBusy && !token.IsCancellationRequested && transcriptCancellation?.Token == token &&
                    string.Equals(selectedMeetingDirectory, meeting.Directory, StringComparison.OrdinalIgnoreCase));
            view.StatusText.Text = UiText.T("正在校验混音并准备 16 kHz 会后识别副本…");
            var audio = await Task.Run(() => AsrAudioPreparation.Prepare(mix.WavePath, mix.WaveSha256,
                Path.Combine(meeting.Directory, "transcripts", "asr-inputs"), token), token);
            if (string.Equals(selectedMeetingDirectory, meeting.Directory, StringComparison.OrdinalIgnoreCase))
                view.StatusText.Text = UiText.T("本地识别正在运行；可取消，也可开始下一场录音。");
            // Progress<T> marshals engine callbacks to this UI thread. The display also
            // rejects callbacks after this attempt has ended, been cancelled or replaced.
            var progress = new Progress<LocalTranscriptionProgress>(display.Report);
            var result = await Task.Run(() => transcriber.RunAsync(new(meeting.Directory, meeting.Id, meeting.DurationMs,
                audio.Path, audio.Sha256, config, meeting.Synthetic), token, progress), token);
            LogMeeting(meeting.Directory, "asr", $"本地识别 {result.Status}；耗时 {result.ElapsedMilliseconds} ms。\n{result.Message}" +
                (result.Revision is { } loggedRevision ? "\n" + string.Join("\n", loggedRevision.Warnings) : ""),
                result.Status == LocalTranscriptionStatus.Failed ? "error" : result.Status == LocalTranscriptionStatus.Cancelled ? "warning" : "info",
                eventId: "legacy-asr-" + Path.GetFileName(result.AttemptDirectory));
            if (result.Revision is { } revision && selectedMeetingDirectory == meeting.Directory)
            {
                await LoadTranscriptVersionsAsync(revision.RevisionId);
                if (selectedMeetingDirectory == meeting.Directory && !WindowCloseRequested) view.WorkspaceTabs.SelectedIndex = 1;
            }
            if (string.Equals(selectedMeetingDirectory, meeting.Directory, StringComparison.OrdinalIgnoreCase) && !WindowCloseRequested)
                view.StatusText.Text = UiText.F($"{meeting.Id} · {result.Status} · {result.ElapsedMilliseconds / 1000d:F1} 秒\n{UiText.T(result.Message)}");
        }
        finally { SetRecognitionMeeting(null); }
    }
}
