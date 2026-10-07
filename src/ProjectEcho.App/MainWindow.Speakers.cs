using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ProjectEcho.Core;
using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private readonly ConcurrentDictionary<(string Directory, string Revision), SpeakerRevision> speakerPresentations = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<SpeakerAnalysis>> speakerAnalyses = new(StringComparer.OrdinalIgnoreCase);
    private string? speakerWorker, speakerSegmentation, speakerEmbedding, speakerSettingsMeeting, speakerSettingsRoot;
    private bool updatingSpeakerSelection, updatingSpeakerEmbeddingPreset, speakerRunning;
    private SpeakerRevision? currentSpeakerRevision;
    private (string Directory, string Revision, string Segment)? speakerCutSelection;
    private sealed record SpeakerChoice(string? Id, string Label);
    private sealed record SpeakerEmbeddingChoice(string Id, string? Path, string Label);
    private sealed record SpeakerAnalysisRow(SpeakerAnalysis Analysis)
    {
        public string Label => UiText.T("已保存声音分析") + $" · {Analysis.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · " +
            UiText.F($"{Analysis.Speakers.Count} 个声音分组") + $" · {Analysis.AnalysisId[..8]}";
    }
    private sealed record SpeakerCutChoice(long TimeMs, string? BeforeId, string? AfterId)
    {
        public string Label => TimelineTimestamp.FromMilliseconds(TimeMs);
    }

    private void InitializeSpeakers()
    {
        var view = transcriptionView!;
        view.OpenSpeakersButton.Click += (_, _) => view.WorkspaceTabs.SelectedIndex = 3;
        view.IncludeSpeakerLabelsBox.Checked += (_, _) => ReplaySpeakerLabelsBox.IsChecked = true;
        view.IncludeSpeakerLabelsBox.Unchecked += (_, _) => ReplaySpeakerLabelsBox.IsChecked = false;
        ReplaySpeakerLabelsBox.Checked += (_, _) => view.IncludeSpeakerLabelsBox.IsChecked = true;
        ReplaySpeakerLabelsBox.Unchecked += (_, _) => view.IncludeSpeakerLabelsBox.IsChecked = false;
        view.SpeakerBackToReviewButton.Click += (_, _) => view.WorkspaceTabs.SelectedIndex = 1;
        view.SpeakerWorkerButton.Click += (_, _) => PickSpeakerFile(0);
        view.SpeakerSegmentationButton.Click += (_, _) => PickSpeakerFile(1);
        view.SpeakerEmbeddingButton.Click += (_, _) => PickSpeakerFile(2);
        view.SpeakerEmbeddingPresetBox.SelectionChanged += (_, _) =>
        {
            if (updatingSpeakerEmbeddingPreset || transcriptBusy || view.SpeakerEmbeddingPresetBox.SelectedItem is not SpeakerEmbeddingChoice choice) return;
            speakerEmbedding = choice.Path;
            UpdateControls();
        };
        view.RunSpeakersButton.Click += (_, _) => StartTranscriptWork(RunSpeakersAsync, "asr");
        view.ApplySpeakerAnalysisButton.Click += (_, _) => StartTranscriptWork(ApplySpeakerAnalysisAsync, "edit");
        view.SpeakerRevisionBox.SelectionChanged += (_, _) =>
        {
            if (!updatingSpeakerSelection && view.SpeakerRevisionBox.SelectedItem is RevisionRow row)
                view.RevisionBox.SelectedItem = view.RevisionBox.Items.Cast<RevisionRow>().FirstOrDefault(value => value.Revision.RevisionId == row.Revision.RevisionId);
        };
        view.SpeakerSegmentsBox.SelectionChanged += (_, _) =>
        {
            if (!updatingSpeakerSelection)
                view.SegmentsBox.SelectedItem = view.SpeakerSegmentsBox.SelectedItem;
        };
        view.SpeakerSegmentInvoked += (_, _) =>
        {
            // Only an explicit click/Enter starts playback. Selection changes from
            // loading, following playback or synchronizing review stay silent.
            if (!transcriptBusy && view.SpeakerSegmentsBox.SelectedItem is TranscriptRow row)
                SeekTranscriptRow(row);
        };
        view.SpeakerIdentityBox.SelectionChanged += (_, _) =>
        {
            if (!updatingSpeakerSelection && view.SpeakerIdentityBox.SelectedItem is SpeakerChoice choice)
                view.SpeakerDisplayNameBox.Text = currentSpeakerRevision?.Speakers.FirstOrDefault(speaker => speaker.SpeakerId == choice.Id)?.Label ?? "";
        };
        view.SpeakerCutTimeBox.SelectionChanged += (_, _) =>
        {
            if (updatingSpeakerSelection || view.SpeakerCutTimeBox.SelectedItem is not SpeakerCutChoice cut) return;
            SelectSpeakerChoice(view.SpeakerCutLeftBox, cut.BeforeId);
            SelectSpeakerChoice(view.SpeakerCutRightBox, cut.AfterId);
        };
        view.SpeakerManualCutBox.Checked += (_, _) => RefreshSpeakerCutMode();
        view.SpeakerManualCutBox.Unchecked += (_, _) => RefreshSpeakerCutMode();
        view.RenameSpeakerButton.Click += (_, _) => StartSpeakerCorrection((directory, revision) => SpeakerService.Rename(directory, revision.RevisionId,
            RequiredSpeaker(view.SpeakerIdentityBox), view.SpeakerDisplayNameBox.Text.Trim()));
        view.AssignSpeakerButton.Click += (_, _) => StartSpeakerCorrection((directory, revision) => SpeakerService.Assign(directory, revision.RevisionId,
            RequiredSpeakerSegment(), (view.SpeakerAssigneeBox.SelectedItem as SpeakerChoice)?.Id));
        view.MergeSpeakerButton.Click += (_, _) => StartSpeakerCorrection((directory, revision) => SpeakerService.Merge(directory, revision.RevisionId,
            RequiredSpeaker(view.SpeakerIdentityBox), RequiredSpeaker(view.SpeakerAssigneeBox)));
        view.SplitOutSpeakerButton.Click += (_, _) => StartSpeakerCorrection((directory, revision) => SpeakerService.SplitSpeaker(directory, revision.RevisionId, [RequiredSpeakerSegment()]));
        view.SplitSpeakerTextButton.Click += (_, _) => StartSpeakerCorrection((directory, revision) =>
        {
            if (view.SpeakerManualCutBox.IsChecked == true)
            {
                if (!TryParseSpeakerManualCutTime(view.SpeakerManualCutTimeBox.Text, out long manualTime))
                    throw new InvalidOperationException(UiText.T("换人时刻格式须为 时:分:秒.毫秒，例如 00:00:27.500；请填写本录音时间。"));
                return SpeakerService.SplitSegmentAtManualTime(directory, revision.RevisionId, RequiredSpeakerSegment(), view.SpeakerCutTextBox.CaretIndex,
                    manualTime, (view.SpeakerCutLeftBox.SelectedItem as SpeakerChoice)?.Id, (view.SpeakerCutRightBox.SelectedItem as SpeakerChoice)?.Id);
            }
            var cut = view.SpeakerCutTimeBox.SelectedItem as SpeakerCutChoice ?? throw new InvalidOperationException(UiText.T("先选择模型给出的候选换人时刻。"));
            return SpeakerService.SplitSegment(directory, revision.RevisionId, RequiredSpeakerSegment(), view.SpeakerCutTextBox.CaretIndex,
                cut.TimeMs, (view.SpeakerCutLeftBox.SelectedItem as SpeakerChoice)?.Id, (view.SpeakerCutRightBox.SelectedItem as SpeakerChoice)?.Id);
        });
    }

    private bool IsSpeakerPresentation(string directory, string revisionId) => speakerPresentations.ContainsKey((directory, revisionId));

    private IReadOnlyList<TranscriptRevision> LoadAllTranscriptPresentations(string directory, CancellationToken token = default)
    {
        var ordinary = TranscriptService.ListRevisions(directory, token);
        speakerAnalyses[directory] = SpeakerService.ListAnalyses(directory, token);
        var result = ordinary.ToList();
        foreach (var revision in SpeakerService.ListRevisions(directory, token))
        {
            token.ThrowIfCancellationRequested();
            var source = ordinary.FirstOrDefault(item => item.RevisionId == revision.SourceTranscriptRevisionId)
                ?? TranscriptService.Load(directory, revision.SourceTranscriptRevisionId, token);
            speakerPresentations[(directory, revision.RevisionId)] = revision;
            result.Add(SpeakerDisplay.AsTranscript(revision, source));
        }
        return result;
    }

    private string ExportTranscriptPresentation(string directory, string revisionId, string destination, bool labels, CancellationToken token)
    {
        if (IsSpeakerPresentation(directory, revisionId))
            return SpeakerSubtitleExporter.Export(directory, revisionId, destination, labels, token, overwrite: true);
        return string.Equals(Path.GetExtension(destination), ".txt", StringComparison.OrdinalIgnoreCase)
            ? TranscriptSubtitleExporter.ExportText(directory, revisionId, destination, token, overwrite: true)
            : TranscriptSubtitleExporter.Export(directory, revisionId, destination, token, overwrite: true);
    }

    private void RefreshSpeakerRevision()
    {
        if (transcriptionView is not { } view) return;
        updatingSpeakerSelection = true;
        try
        {
            currentSpeakerRevision = (visibleSegment?.Revision ?? visibleRevision) is { } revision && (visibleSegment?.Directory ?? transcriptMeeting) is { } directory &&
                speakerPresentations.TryGetValue((directory, revision.RevisionId), out var speakers) ? speakers : null;
            view.SpeakerRevisionBox.ItemsSource = view.RevisionBox.ItemsSource;
            view.SpeakerRevisionBox.SelectedItem = view.RevisionBox.SelectedItem;
            string? analysisId = currentSpeakerRevision?.AnalysisId ?? (view.SpeakerAnalysisBox.SelectedItem as SpeakerAnalysisRow)?.Analysis.AnalysisId;
            var analyses = selectedMeetingDirectory is { } selected && speakerAnalyses.TryGetValue(selected, out var stored)
                ? stored.Select(analysis => new SpeakerAnalysisRow(analysis)).ToArray() : [];
            view.SpeakerAnalysisBox.ItemsSource = analyses;
            view.SpeakerAnalysisBox.SelectedItem = analyses.FirstOrDefault(row => row.Analysis.AnalysisId == analysisId) ?? analyses.FirstOrDefault();
            view.SpeakerSegmentsBox.ItemsSource = view.SegmentsBox.ItemsSource;
            var identities = currentSpeakerRevision?.Speakers.Select(speaker => new SpeakerChoice(speaker.SpeakerId, $"{speaker.Code} · {speaker.Label}")).ToArray() ?? [];
            var choices = new[] { new SpeakerChoice(null, UiText.T("待确认")) }.Concat(identities).ToArray();
            view.SpeakerIdentityBox.ItemsSource = identities;
            view.SpeakerIdentityBox.SelectedIndex = identities.Length > 0 ? 0 : -1;
            foreach (var box in new[] { view.SpeakerAssigneeBox, view.SpeakerCutLeftBox, view.SpeakerCutRightBox })
            { string? selectedId = (box.SelectedItem as SpeakerChoice)?.Id; box.ItemsSource = choices; SelectSpeakerChoice(box, selectedId); }
        }
        finally { updatingSpeakerSelection = false; }
        RefreshSpeakerSelection();
    }

    private void RefreshSpeakerSelection()
    {
        if (updatingSpeakerSelection || transcriptionView is not { } view) return;
        updatingSpeakerSelection = true;
        try
        {
            currentSpeakerRevision = visibleSegment is { } row && speakerPresentations.TryGetValue((row.Directory, row.Revision.RevisionId), out var selectedSpeakers)
                ? selectedSpeakers : visibleRevision is { } revision && transcriptMeeting is { } directory && speakerPresentations.TryGetValue((directory, revision.RevisionId), out var speakers) ? speakers : null;
            var identities = currentSpeakerRevision?.Speakers.Select(speaker => new SpeakerChoice(speaker.SpeakerId, $"{speaker.Code} · {speaker.Label}")).ToArray() ?? [];
            // A stable speakerId can have a different display name in another immutable version.
            // Search selections must refresh both identity and version-specific label.
            if (!view.SpeakerIdentityBox.Items.Cast<SpeakerChoice>().SequenceEqual(identities))
            {
                view.SpeakerIdentityBox.ItemsSource = identities;
                foreach (var box in new[] { view.SpeakerAssigneeBox, view.SpeakerCutLeftBox, view.SpeakerCutRightBox })
                {
                    string? selectedId = (box.SelectedItem as SpeakerChoice)?.Id;
                    box.ItemsSource = new[] { new SpeakerChoice(null, UiText.T("待确认")) }.Concat(identities).ToArray();
                    SelectSpeakerChoice(box, selectedId);
                }
            }
            view.SpeakerSegmentsBox.ItemsSource = view.SegmentsBox.ItemsSource;
            view.SpeakerSegmentsBox.SelectedItem = visibleSegment;
            var segment = currentSpeakerRevision?.Segments.FirstOrDefault(item => item.SegmentId == visibleSegment?.Segment.SegmentId);
            view.SpeakerSelectionText.Text = segment is null ? UiText.T("选择一个带说话人的版本和段落，再修正人物归属。")
                : $"[{(segment.SpeakerId is null ? UiText.T(SpeakerDisplay.Label(currentSpeakerRevision!, segment)) : SpeakerDisplay.Label(currentSpeakerRevision!, segment))}] · {UiText.T(segment.NeedsConfirmation ? "声音归属待确认；文字完整保留。" : "本场人物；声音边界为估计。")}";
            var cutKey = (visibleSegment?.Directory ?? transcriptMeeting ?? "", currentSpeakerRevision?.RevisionId ?? "", segment?.SegmentId ?? "");
            bool cutChanged = speakerCutSelection is not { } previousCut || previousCut != cutKey;
            if (cutChanged)
            {
                speakerCutSelection = cutKey;
                view.SpeakerCutTextBox.Text = segment?.Text ?? "";
                view.SpeakerCutTimeBox.ItemsSource = segment?.CandidateBoundaries.Select(cut => new SpeakerCutChoice(cut.TimeMs, cut.BeforeSpeakerId, cut.AfterSpeakerId)).ToArray();
                view.SpeakerCutTimeBox.SelectedIndex = view.SpeakerCutTimeBox.Items.Count > 0 ? 0 : -1;
                view.SpeakerManualCutBox.IsChecked = false;
                view.SpeakerManualCutTimeBox.Text = "";
            }
            SelectSpeakerChoice(view.SpeakerIdentityBox, segment?.SpeakerId);
            SelectSpeakerChoice(view.SpeakerAssigneeBox, segment?.SpeakerId);
            view.SpeakerDisplayNameBox.Text = currentSpeakerRevision?.Speakers.FirstOrDefault(speaker => speaker.SpeakerId == (view.SpeakerIdentityBox.SelectedItem as SpeakerChoice)?.Id)?.Label ?? "";
            if (cutChanged)
            {
                if (view.SpeakerCutTimeBox.SelectedItem is SpeakerCutChoice candidate)
                { SelectSpeakerChoice(view.SpeakerCutLeftBox, candidate.BeforeId); SelectSpeakerChoice(view.SpeakerCutRightBox, candidate.AfterId); }
                else
                { SelectSpeakerChoice(view.SpeakerCutLeftBox, segment?.SpeakerId); SelectSpeakerChoice(view.SpeakerCutRightBox, null); }
            }
        }
        finally { updatingSpeakerSelection = false; }
        RefreshSpeakerCutMode();
    }

    private void RefreshSpeakerCutMode()
    {
        if (transcriptionView is not { } view) return;
        bool manual = view.SpeakerManualCutBox.IsChecked == true;
        view.SpeakerCutTimeBox.IsEnabled = !manual && view.SpeakerCutTimeBox.Items.Count > 0;
        view.SpeakerManualCutTimeBox.IsEnabled = manual;
        view.SplitSpeakerTextButton.IsEnabled = manual || view.SpeakerCutTimeBox.Items.Count > 0;
    }

    private static bool TryParseSpeakerManualCutTime(string text, out long milliseconds)
    {
        milliseconds = 0;
        string[] formats = [@"hh\:mm\:ss", @"hh\:mm\:ss\.f", @"hh\:mm\:ss\.ff", @"hh\:mm\:ss\.fff"];
        if (!TimeSpan.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, out var value)) return false;
        milliseconds = value.Ticks / TimeSpan.TicksPerMillisecond; return true;
    }

    private static void SelectSpeakerChoice(ComboBox box, string? id) =>
        box.SelectedItem = box.Items.Cast<SpeakerChoice>().FirstOrDefault(choice => choice.Id == id) ?? box.Items.Cast<SpeakerChoice>().FirstOrDefault();
    private static string RequiredSpeaker(ComboBox box) => (box.SelectedItem as SpeakerChoice)?.Id ?? throw new InvalidOperationException(UiText.T("请选择一个已存在的本场人物。"));
    private string RequiredSpeakerSegment() => visibleSegment?.Segment.SegmentId ?? throw new InvalidOperationException(UiText.T("先选择文字段落。"));

    private void StartSpeakerCorrection(Func<string, SpeakerRevision, SpeakerRevision> action)
    {
        if (transcriptBusy || currentSpeakerRevision is null || transcriptMeeting is null || WindowCloseRequested) return;
        // Evaluate user selections on the dispatcher before yielding. Every action returns a new immutable version.
        string directory = visibleSegment?.Directory ?? transcriptMeeting; SpeakerRevision parent = currentSpeakerRevision;
        StartTranscriptWork(async () =>
        {
            var revision = action(directory, parent);
            if (directory == selectedMeetingDirectory) await LoadTranscriptVersionsAsync(revision.RevisionId);
            if (directory == selectedMeetingDirectory) transcriptionView!.StatusText.Text = UiText.T("说话人修正已另存新版本；原稿、父版本和内部人物 ID 保留。");
            LogMeeting(directory, "edit", "说话人修正已另存新版本。人物名称与全文不写入诊断。" );
        }, "edit", directory);
    }

    private void UpdateSpeakerControls(bool open)
    {
        var view = transcriptionView!;
        SwitchSpeakerSettings();
        if (currentSpeakerRevision is not null && (visibleSegment?.Revision ?? visibleRevision)?.RevisionId != currentSpeakerRevision.RevisionId)
            RefreshSpeakerRevision();
        view.SpeakersTab.Header = view.OpenSpeakersButton.Content = UiText.T("同场说话人");
        view.SpeakerConfigurationExpander.Header = UiText.T("本地 CPU 模型配置");
        view.SpeakerDescriptionText.Text = UiText.T("使用完整混音分析同场声音并生成独立版本。A/B/C 只代表本场声音分组，不推断姓名，也不将音轨当成人物。");
        view.SpeakerVersionText.Text = currentSpeakerRevision is null ? UiText.T("选择已有转写后主动运行；没有可靠句内文字边界时保留整句并提示待确认。")
            : UiText.T("当前为独立说话人版本。可在字幕回放与校订中修改正文并另存；单击下方字幕回听。再次分析不会覆盖本版人工修正。");
        view.SpeakerWorkerButton.Content = UiText.T("说话人程序"); view.SpeakerSegmentationButton.Content = UiText.T("分段模型"); view.SpeakerEmbeddingButton.Content = UiText.T("声音特征模型");
        view.SpeakerWorkerText.Text = speakerWorker ?? UiText.T("未配置程序");
        view.SpeakerSegmentationText.Text = speakerSegmentation ?? UiText.T("未配置模型"); view.SpeakerEmbeddingText.Text = speakerEmbedding ?? UiText.T("未配置模型");
        RefreshSpeakerEmbeddingPresets();
        view.SpeakerLicenseBox.Content = UiText.T("已核对本地说话人模型的来源与许可");
        view.SpeakerCountLabel.Text = UiText.T("已知人数（留空自动估计）"); view.SpeakerThreadsLabel.Text = UiText.T("CPU 线程");
        view.RunSpeakersButton.Content = UiText.T("按说话人分段");
        view.ApplySpeakerAnalysisButton.Content = UiText.T("应用已保存分析");
        view.SpeakerAnalysisBox.ToolTip = UiText.T("复用本场同一音频的声音分析，只为当前转写生成新人物版本，不再次运行模型。");
        System.Windows.Automation.AutomationProperties.SetName(view.SpeakerAnalysisBox, UiText.T("已保存声音分析"));
        view.RenameSpeakerButton.Content = UiText.T("保存显示名称"); view.AssignSpeakerButton.Content = UiText.T("改这段的说话人");
        view.MergeSpeakerButton.Content = UiText.T("将上方人物合并到此人"); view.SplitOutSpeakerButton.Content = UiText.T("将这段拆出为新人物");
        view.SpeakerCutExpander.Header = UiText.T("人工确认句内换人"); view.SplitSpeakerTextButton.Content = UiText.T("在光标处分成两段");
        view.SpeakerCutHelpText.Text = UiText.T("先回听，在完整文字中放置分界光标并选左右人物。可选模型候选时刻；模型漏检时，勾选手动输入本录音的准确换人时刻。缺失文字请先回校订页补入并保存，不会自动补写或按字数分配时间。");
        view.SpeakerManualCutBox.Content = UiText.T("手动输入换人时刻（本录音时间）");
        view.SpeakerManualCutFormatText.Text = UiText.T("时:分:秒.毫秒");
        view.SpeakerManualCutTimeBox.ToolTip = UiText.T("例如 00:00:27.500，必须位于当前字幕段内；这是人工时间，不是模型词界。");
        System.Windows.Automation.AutomationProperties.SetName(view.SpeakerManualCutTimeBox, UiText.T("人工换人时刻"));
        view.SpeakerCutLeftBox.ToolTip = UiText.T("切点左侧的说话人"); view.SpeakerCutRightBox.ToolTip = UiText.T("切点右侧的说话人");
        view.SpeakerDisplayNameBox.ToolTip = UiText.T("仅在本场显示的名称");
        System.Windows.Automation.AutomationProperties.SetName(view.SpeakerCutLeftBox, UiText.T("切点左侧的说话人"));
        System.Windows.Automation.AutomationProperties.SetName(view.SpeakerCutRightBox, UiText.T("切点右侧的说话人"));
        view.SpeakerCorrectionNoticeText.Text = UiText.T("每次纠正都保存新版本。改名只影响本场显示；合并或拆出不修改发言正文。导出标签可在校订页开关。");
        view.SpeakerBackToReviewButton.Content = UiText.T("回到校订与导出"); view.IncludeSpeakerLabelsBox.Content = ReplaySpeakerLabelsBox.Content = UiText.T("导出包含说话人标签");
        bool available = open && !transcriptBusy;
        view.RunSpeakersButton.IsEnabled = available && SessionRules.CanPlayback(state);
        view.ApplySpeakerAnalysisButton.IsEnabled = available && SessionRules.CanPlayback(state) && visibleRevision is not null && view.SpeakerAnalysisBox.SelectedItem is SpeakerAnalysisRow;
        view.SpeakerAnalysisBox.IsEnabled = available;
        view.SpeakerConfigurationExpander.IsEnabled = view.SpeakerRevisionBox.IsEnabled = view.SpeakerSegmentsBox.IsEnabled = available;
        bool correction = available && currentSpeakerRevision is not null;
        view.SpeakerIdentityBox.IsEnabled = view.SpeakerDisplayNameBox.IsEnabled = view.RenameSpeakerButton.IsEnabled = correction;
        view.SpeakerAssigneeBox.IsEnabled = view.MergeSpeakerButton.IsEnabled = correction;
        view.AssignSpeakerButton.IsEnabled = view.SplitOutSpeakerButton.IsEnabled = correction && visibleSegment is not null;
        var cutSegment = currentSpeakerRevision?.Segments.FirstOrDefault(segment => segment.SegmentId == visibleSegment?.Segment.SegmentId);
        view.SpeakerCutExpander.IsEnabled = correction && cutSegment?.StartMs is not null && cutSegment.EndMs > cutSegment.StartMs && cutSegment.Text.Length > 1;
        RefreshSpeakerCutMode();
    }

    private void PickSpeakerFile(int kind)
    {
        var picker = new OpenFileDialog { CheckFileExists = true, Filter = kind == 0 ? "Windows program|*.exe" : "ONNX model|*.onnx", Title = UiText.T("选择已下载并核对许可的本地说话人组件") };
        if (picker.ShowDialog(this) != true) return;
        if (kind == 0) speakerWorker = picker.FileName; else if (kind == 1) speakerSegmentation = picker.FileName; else speakerEmbedding = picker.FileName;
        UpdateControls();
    }

    private SherpaDiarizationConfiguration CurrentSpeakerConfiguration()
    {
        var view = transcriptionView!;
        int? count = string.IsNullOrWhiteSpace(view.SpeakerCountBox.Text) ? null :
            int.TryParse(view.SpeakerCountBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed is >= 1 and <= 64 ? parsed : throw new InvalidDataException(UiText.T("已知人数须为 1–64，留空自动估计。"));
        if (!int.TryParse(view.SpeakerThreadsBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int threads) || threads is < 1 or > 16)
            throw new InvalidDataException(UiText.T("说话人 CPU 线程须为 1–16。"));
        return new(speakerWorker ?? "", speakerSegmentation ?? "", speakerEmbedding ?? "", count, threads, ModelLicenseAcknowledged: view.SpeakerLicenseBox.IsChecked == true);
    }

    private void SwitchSpeakerSettings()
    {
        if (speakerSettingsRoot == dataRoot && speakerSettingsMeeting == selectedMeetingDirectory) return;
        speakerSettingsRoot = dataRoot; speakerSettingsMeeting = selectedMeetingDirectory;
        var view = transcriptionView!;
        try
        {
            string path = selectedMeetingDirectory is { } directory && File.Exists(Path.Combine(directory, "speaker-settings.json"))
                ? Path.Combine(directory, "speaker-settings.json") : Path.Combine(dataRoot, "settings", "speakers.json");
            RejectReparseAncestors(path);
            SherpaDiarizationConfiguration? config = File.Exists(path) && new FileInfo(path).Length <= 65536
                ? JsonSerializer.Deserialize<SherpaDiarizationConfiguration>(File.ReadAllText(path)) : null;
            if (config is not null) config = SpeakerUiSettingsPaths.Resolve(config, AppContext.BaseDirectory);
            string bundled = Path.Combine(AppContext.BaseDirectory, "Tools", "Sherpa", "sherpa-onnx-offline-speaker-diarization.exe");
            speakerWorker = config?.ExecutablePath ?? (File.Exists(bundled) ? bundled : null);
            string bundledSeg = Path.Combine(AppContext.BaseDirectory, "Models", "Sherpa", "pyannote-segmentation-3.0.onnx");
            speakerSegmentation = config?.SegmentationModelPath ?? (File.Exists(bundledSeg) ? bundledSeg : null);
            speakerEmbedding = config?.EmbeddingModelPath ?? SpeakerUiSettingsPaths.DefaultEmbedding(AppContext.BaseDirectory);
            view.SpeakerCountBox.Text = config?.KnownSpeakerCount?.ToString(CultureInfo.InvariantCulture) ?? "";
            view.SpeakerThreadsBox.Text = (config?.Threads ?? 2).ToString(CultureInfo.InvariantCulture);
            view.SpeakerLicenseBox.IsChecked = config?.ModelLicenseAcknowledged == true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { speakerWorker = speakerSegmentation = speakerEmbedding = null; view.SpeakerLicenseBox.IsChecked = false; view.StatusText.Text = ex.Message; }
    }

    private void RefreshSpeakerEmbeddingPresets()
    {
        var view = transcriptionView!;
        string titanet = Path.Combine(AppContext.BaseDirectory, "Models", "Sherpa", "nemo_en_titanet_large.onnx");
        string eres = Path.Combine(AppContext.BaseDirectory, "Models", "Sherpa", "3dspeaker-eres2net.onnx");
        string selected = string.Equals(speakerEmbedding, titanet, StringComparison.OrdinalIgnoreCase) ? "titanet" :
            string.Equals(speakerEmbedding, eres, StringComparison.OrdinalIgnoreCase) ? "eres" : "custom";
        var choices = new[] { new SpeakerEmbeddingChoice("titanet", titanet, UiText.T("TitaNet-Large（M5B）")),
            new SpeakerEmbeddingChoice("eres", eres, UiText.T("ERes2Net（M5）")),
            new SpeakerEmbeddingChoice("custom", selected == "custom" ? speakerEmbedding : null, UiText.T("自选文件")) };
        updatingSpeakerEmbeddingPreset = true;
        try
        {
            if (!view.SpeakerEmbeddingPresetBox.Items.Cast<SpeakerEmbeddingChoice>().SequenceEqual(choices))
                view.SpeakerEmbeddingPresetBox.ItemsSource = choices;
            view.SpeakerEmbeddingPresetBox.SelectedItem = view.SpeakerEmbeddingPresetBox.Items.Cast<SpeakerEmbeddingChoice>().First(choice => choice.Id == selected);
            view.SpeakerEmbeddingPresetBox.ToolTip = UiText.T("选择声音特征模型；自选文件请使用右侧按钮。切换后再次分析会另存新版本，不覆盖旧稿。");
            System.Windows.Automation.AutomationProperties.SetName(view.SpeakerEmbeddingPresetBox, UiText.T("声音特征模型"));
        }
        finally { updatingSpeakerEmbeddingPreset = false; }
    }

    private void SaveSpeakerSettings(string directory, SherpaDiarizationConfiguration configuration)
    {
        var stored = SpeakerUiSettingsPaths.ForStorage(configuration, AppContext.BaseDirectory);
        Save(Path.Combine(directory, "speaker-settings.json"), stored);
        Save(Path.Combine(dataRoot, "settings", "speakers.json"), stored with { KnownSpeakerCount = null });
        static void Save(string path, SherpaDiarizationConfiguration config)
        {
            RejectReparseAncestors(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(pending, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(pending, path, true);
        }
    }

    private async Task RunSpeakersAsync()
    {
        if (!SessionRules.CanPlayback(state)) throw new InvalidOperationException(UiText.T("录音、暂停及保存期间不能启动说话人分析。"));
        var meeting = SelectedTranscriptMeeting(); var mix = currentMix ?? throw new InvalidOperationException(UiText.T("请先生成完整混音。"));
        string sourceId = currentSpeakerRevision?.SourceTranscriptRevisionId ?? visibleRevision?.RevisionId ?? throw new InvalidOperationException(UiText.T("先选择本场转写版本。"));
        if (transcriptMeeting != meeting.Directory || visibleSegment is { } selectedRow && selectedRow.Directory != meeting.Directory)
            throw new InvalidOperationException(UiText.T("先选择本场转写版本。"));
        var configuration = CurrentSpeakerConfiguration(); var readiness = SherpaDiarizationRuntime.Inspect(configuration);
        if (!readiness.Ready) { transcriptionView!.SpeakerConfigurationExpander.IsExpanded = true; throw new InvalidOperationException(UiText.T(readiness.Message)); }
        SaveSpeakerSettings(meeting.Directory, configuration);
        var token = transcriptCancellation!.Token; var view = transcriptionView!;
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(meeting.Directory, "meeting.json")));
        string policy = metadata.RootElement.GetProperty("schemaVersion").GetString() == "m3-audio-import-1" ? "file-v1" :
            metadata.RootElement.TryGetProperty("timelinePolicy", out var value) ? value.GetString() ?? "elapsed-v1" : "elapsed-v1";
        speakerRunning = true; SetRecognitionMeeting(meeting.Directory); UpdateControls();
        try
        {
            view.StatusText.Text = UiText.T("正在校验完整混音并准备说话人分析副本…");
            var audio = await Task.Run(() => AsrAudioPreparation.Prepare(mix.WavePath, mix.WaveSha256,
                Path.Combine(meeting.Directory, "transcripts", "asr-inputs"), token), token);
            using var display = new SpeakerProgressDisplay(view, () => transcriptBusy && !token.IsCancellationRequested &&
                transcriptCancellation?.Token == token && selectedMeetingDirectory == meeting.Directory);
            var progress = new Progress<double>(percent =>
            {
                if (token.IsCancellationRequested || selectedMeetingDirectory != meeting.Directory || !speakerRunning) return;
                display.Report(percent);
                view.StatusText.Text = UiText.T("正在分析同场声音；可取消。声音特征阶段进度：") + percent.ToString("F0", CultureInfo.InvariantCulture) + "%";
            });
            var binding = new SpeakerAudioBinding(audio.Sha256, mix.WaveSha256, 16000, audio.Frames, policy);
            var result = await Task.Run(() => SherpaDiarizationRuntime.RunAsync(new(meeting.Directory, meeting.Id, audio.Path, binding, configuration, meeting.Synthetic), token, progress), token);
            if (result.Analysis is { } analysis)
            {
                token.ThrowIfCancellationRequested();
                var revision = await Task.Run(() => SpeakerService.CreateRevision(meeting.Directory, sourceId, analysis.AnalysisId, token), token);
                if (selectedMeetingDirectory == meeting.Directory && !WindowCloseRequested) await LoadTranscriptVersionsAsync(revision.RevisionId);
            }
            LogMeeting(meeting.Directory, "asr", "本地说话人分析：" + result.Status + "。原始音频和已有版本保留。\n" + result.Message, result.Analysis is null ? "warning" : "info");
            if (selectedMeetingDirectory == meeting.Directory && !WindowCloseRequested) view.StatusText.Text = UiText.T(result.Message) +
                (result.Analysis is null ? "" : "\n" + UiText.T("本次已另存新分析版本；人物分组和代号可能与旧稿不同，请切换版本对照。旧版人工修正保留。"));
        }
        finally { speakerRunning = false; SetRecognitionMeeting(null); }
    }

    private async Task ApplySpeakerAnalysisAsync()
    {
        if (!SessionRules.CanPlayback(state)) throw new InvalidOperationException(UiText.T("录音、暂停及保存期间不能启动说话人分析。"));
        var meeting = SelectedTranscriptMeeting(); var mix = currentMix ?? throw new InvalidOperationException(UiText.T("请先生成完整混音。"));
        var chosen = (transcriptionView!.SpeakerAnalysisBox.SelectedItem as SpeakerAnalysisRow)?.Analysis
            ?? throw new InvalidOperationException(UiText.T("先选择本场已保存的声音分析。"));
        string sourceId = currentSpeakerRevision?.SourceTranscriptRevisionId ?? visibleRevision?.RevisionId ?? throw new InvalidOperationException(UiText.T("先选择本场转写版本。"));
        if (transcriptMeeting != meeting.Directory || visibleSegment is { } row && row.Directory != meeting.Directory ||
            chosen.MeetingId != meeting.Id || chosen.Audio.SourceMixSha256 != mix.WaveSha256)
            throw new InvalidDataException(UiText.T("已保存分析与当前会议混音不一致，不能应用。"));
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(meeting.Directory, "meeting.json")));
        string policy = metadata.RootElement.GetProperty("schemaVersion").GetString() == "m3-audio-import-1" ? "file-v1" :
            metadata.RootElement.TryGetProperty("timelinePolicy", out var value) ? value.GetString() ?? "elapsed-v1" : "elapsed-v1";
        if (chosen.Audio.TimelinePolicy != policy) throw new InvalidDataException(UiText.T("已保存分析与当前录音时间轴不一致，不能应用。"));
        var token = transcriptCancellation!.Token;
        // Native inference is intentionally absent. Core revalidates the source text fingerprint and exact mapping evidence.
        speakerRunning = true; SetRecognitionMeeting(meeting.Directory);
        try
        {
            var revision = await Task.Run(() =>
            {
                RejectReparseAncestors(mix.WavePath);
                using var input = new FileStream(mix.WavePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                byte[] buffer = new byte[128 * 1024];
                while (true)
                {
                    token.ThrowIfCancellationRequested(); int read = input.Read(buffer);
                    if (read == 0) break; hash.AppendData(buffer, 0, read);
                }
                if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), chosen.Audio.SourceMixSha256, StringComparison.Ordinal))
                    throw new InvalidDataException(UiText.T("已保存分析与当前会议混音不一致，不能应用。"));
                return SpeakerService.CreateRevision(meeting.Directory, sourceId, chosen.AnalysisId, token);
            }, token);
            if (selectedMeetingDirectory == meeting.Directory && !WindowCloseRequested)
            {
                await LoadTranscriptVersionsAsync(revision.RevisionId);
                transcriptionView.StatusText.Text = UiText.T("已复用声音分析并另存人物版本；文字变化处仅使用仍有效的映射，缺乏边界时保留全文待确认。旧人工版本保留。");
            }
        }
        finally { speakerRunning = false; SetRecognitionMeeting(null); }
    }

    private void YieldSpeakersToRecording()
    {
        if (!speakerRunning) return;
        transcriptCancellation?.Cancel();
        LogMeeting(recognitionMeetingDirectory, "asr", "开始下一场录音，已取消后台说话人分析；原始音频与已有版本保留。", "warning");
    }
}
