using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProjectEcho.Core;
using ProjectEcho.Core.Library;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

public partial class MainWindow
{
    internal async Task<object> M3cLibrarySmokeAsync(string evidence, CancellationToken token)
    {
        string fixture = Path.Combine(evidence, "synthetic-data"); Directory.CreateDirectory(fixture);
        dataRoot = fixture; DataRootText.Text = fixture;
        string? opened = null;
        openDataDirectory = path => opened = path;
        OpenDataRootButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (opened != Path.GetFullPath(fixture)) throw new InvalidDataException("Open data folder did not use the selected root.");
        string first = Path.Combine(RecordingsRoot, "first"), second = Path.Combine(RecordingsRoot, "second");
        void Meeting(string directory, string title, string project, string date, long duration)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "meeting.json"), JsonSerializer.Serialize(new
            { meetingId = Guid.NewGuid().ToString("N"), title, project, startedAtUtc = date, duration100ns = duration * 10000, storageComplete = true }));
        }
        Meeting(first, "保留原文 Meeting 一", "项目 A", "2026-09-29T01:00:00Z", 60000);
        Meeting(second, "Meeting 二", "项目 B", "2026-09-30T01:00:00Z", 180000);
        RefreshRecovery_Click(this, new RoutedEventArgs());
        if (RecoveryBox.Items.Count != 2 || !File.Exists(Path.Combine(fixture, "library.sqlite3")))
            throw new InvalidDataException("Library UI did not populate from SQLite.");
        if (((RecordingDirectory)RecoveryBox.Items[0]).Path != second) throw new InvalidDataException("Default date order differs.");
        var filter = new LibraryFilterWindow(new(), ["项目 A", "项目 B"]) { Owner = this };
        filter.Minimum.Text = "2"; filter.Maximum.Text = "4"; filter.Sorting.SelectedIndex = 3;
        libraryQuery = filter.ReadQuery(); RefreshRecovery_Click(this, new RoutedEventArgs());
        if (RecoveryBox.Items.Count != 1 || ((RecordingDirectory)RecoveryBox.Items[0]).Path != second)
            throw new InvalidDataException("UI duration filter or sort was not applied.");
        filter.Minimum.Text = "5"; filter.Maximum.Text = "2";
        try { filter.ReadQuery(); throw new InvalidDataException("Invalid range accepted."); }
        catch (InvalidDataException ex) when (ex.Message != "Invalid range accepted.") { }
        libraryQuery = new(Project: "项目 A"); RefreshRecovery_Click(this, new RoutedEventArgs());
        if (RecoveryBox.Items.Count != 1 || ((RecordingDirectory)RecoveryBox.Items[0]).Path != first)
            throw new InvalidDataException("Project filter was not applied.");
        libraryQuery = new(); RefreshRecovery_Click(this, new RoutedEventArgs());
        SelectMeeting(first, loadSavedMix: false);
        FiledTitleBox.Text = "原文不随界面语言改变"; FiledProjectBox.Text = "Mixed 项目";
        FileMeeting_Click(this, new RoutedEventArgs());
        var stored = File.ReadAllBytes(Path.Combine(first, "filing.json"));
        FiledTitleBox.Text = "未保存编辑";
        RefreshRecovery_Click(this, new RoutedEventArgs());
        if (FiledTitleBox.Text != "未保存编辑") throw new InvalidDataException("Refreshing the library discarded unsaved filing edits.");
        FiledTitleBox.Text = "原文不随界面语言改变";
        var images = new List<string>();
        libraryQuery = new(Project: "Mixed 项目"); RefreshRecovery_Click(this, new RoutedEventArgs());
        Width = MinWidth; Height = MinHeight;
        foreach (string language in new[] { "en", "zh-Hant", "zh-CN" })
        {
            ChangeLanguage(language, persist: false);
            NavigateTo(4); await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            var sidebarLeftEdges = new List<double>();
            foreach (var navigationButton in new[] { RecordNavigationButton, LibraryNavigationButton, ImportAudioButton, SettingsNavigationButton, LanguageButton })
            {
                var presenter = FindContent(navigationButton) ?? throw new InvalidDataException("Missing sidebar content presenter.");
                if (presenter.HorizontalAlignment != HorizontalAlignment.Left)
                    throw new InvalidDataException("Sidebar template ignored left content alignment: " + navigationButton.Name);
                sidebarLeftEdges.Add(presenter.TranslatePoint(new Point(), this).X);
            }
            if (sidebarLeftEdges.Max() - sidebarLeftEdges.Min() > 2 || !Equals(LanguageButton.Content, "EN/中"))
                throw new InvalidDataException("Sidebar labels do not share a left edge or the invariant language label changed.");
            string expected = language switch { "en" => "Open data folder", "zh-Hant" => "打開數據目錄", _ => "打开数据目录" };
            if (!Equals(OpenDataRootButton.Content, expected))
                throw new InvalidDataException($"Static UI resource did not change: {language} / {OpenDataRootButton.Content}");
            string status = SessionStateText.Text;
            if (language == "en" && (status != UiText.T("尚未录音") || status.Any(c => c is >= '\u4e00' and <= '\u9fff')))
                throw new InvalidDataException("Dynamic recording state was not localized: " + status);
            if (language == "en" && LibraryFilterButton.ToolTip?.ToString() != "Filter and sort meetings (filtered)")
                throw new InvalidDataException("Active filter tooltip was not localized.");
            if (!stored.SequenceEqual(File.ReadAllBytes(Path.Combine(first, "filing.json"))) || FiledTitleBox.Text != "原文不随界面语言改变")
                throw new InvalidDataException("Language switching mutated meeting content.");
            SaveLayoutImage(evidence, "minimum-settings-" + language + ".png", 1, images);
            NavigateTo(3); transcriptionView!.WorkspaceTabs.SelectedIndex = 1;
            var view = transcriptionView;
            using (var display = new RecognitionProgressDisplay(view, () => true))
            {
                display.Report(new(LocalTranscriptionStage.LoadingModel));
                if (!view.RecognitionProgressBar.IsIndeterminate || view.ProgressText.Text != UiText.T("正在加载模型并准备识别…"))
                    throw new InvalidDataException("Loading model was displayed as numerical recognition progress.");
                display.Report(new(LocalTranscriptionStage.Recognizing, 0));
                if (!view.RecognitionProgressBar.IsIndeterminate)
                    throw new InvalidDataException("An engine's zero before its first window looks like stalled determinate progress.");
                display.Report(new(LocalTranscriptionStage.Recognizing, 63));
                string beforeElapsed = view.ProgressElapsedText.Text;
                var elapsedWait = System.Diagnostics.Stopwatch.StartNew();
                while (view.ProgressElapsedText.Text == beforeElapsed && elapsedWait.Elapsed < TimeSpan.FromSeconds(4))
                    await Task.Delay(50, token);
                if (view.RecognitionProgressBar.IsIndeterminate || view.RecognitionProgressBar.Value != 63 ||
                    view.RecognitionActivityBar.Visibility != Visibility.Visible || view.ProgressElapsedText.Text == beforeElapsed)
                    throw new InvalidDataException($"Sparse progress display: indeterminate={view.RecognitionProgressBar.IsIndeterminate}, value={view.RecognitionProgressBar.Value}, activity={view.RecognitionActivityBar.Visibility}, elapsed='{beforeElapsed}' -> '{view.ProgressElapsedText.Text}'.");
                display.Report(new(LocalTranscriptionStage.Saving));
                if (!view.RecognitionProgressBar.IsIndeterminate || view.ProgressText.Text != UiText.T("识别运算已结束，正在校验并保存文字…"))
                    throw new InvalidDataException("Saving was incorrectly shown as a finished recognition task.");
                display.Dispose();
                string priorText = view.ProgressText.Text;
                display.Report(new(LocalTranscriptionStage.Recognizing, 99));
                if (view.ProgressText.Text != priorText || view.RecognitionActivityBar.Visibility != Visibility.Collapsed)
                    throw new InvalidDataException("A disposed attempt accepted a stale progress callback.");
            }
            view.ProgressPanel.Visibility = Visibility.Collapsed;
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            SaveLayoutImage(evidence, "minimum-editor-" + language + ".png", 1, images);
            filter.Close();
            filter = new LibraryFilterWindow(new(), ["Mixed 项目", "项目 B"]) { Owner = this };
            filter.Show(); filter.UpdateLayout(); filter.Close();
        }
        NavigateTo(1);
        libraryQuery = new(); RefreshRecovery_Click(this, new RoutedEventArgs());
        await transcriptWork; await replayLoadWork; await savedMixWork;
        var entry = RecoveryBox.Items.Cast<RecordingDirectory>().Single(x => x.Path == first);
        var button = new Button { DataContext = entry };
        bool asked = false;
        deletionConfirmationOverride = _ => { asked = true; return false; };
        MeetingMenu_Click(button, new RoutedEventArgs(Button.ClickEvent));
        ((MenuItem)button.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        button.ContextMenu.IsOpen = false;
        if (!asked || !Directory.Exists(first)) throw new InvalidDataException("Delete cancellation did not retain meeting.");
        state = SessionState.Recording; UpdateControls();
        if (CanDeleteMeeting) throw new InvalidDataException("Deletion allowed during recording.");
        state = SessionState.Saved; UpdateControls();
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        savedMixWork = releaseDelete.Task;
        deletionConfirmationOverride = _ => true;
        MeetingMenu_Click(button, new RoutedEventArgs(Button.ClickEvent));
        ((MenuItem)button.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        button.ContextMenu.IsOpen = false;
        bool transcriptStarted = false;
        StartTranscriptWork(() => { transcriptStarted = true; return Task.CompletedTask; });
        SelectMeeting(second, loadSavedMix: false);
        if (!deletingMeeting || ExportButton.IsEnabled || RetryMixButton.IsEnabled || DataRootButton.IsEnabled ||
            StartButton.IsEnabled || transcriptionView!.RunWhisperButton.IsEnabled || transcriptStarted || selectedMeetingDirectory != first)
            throw new InvalidDataException("Deletion allowed a new overlapping file task or selection.");
        releaseDelete.SetResult(); await recoveryWork;
        deletionConfirmationOverride = null;
        if (Directory.Exists(first) || RecoveryBox.Items.Count != 1 || selectedMeetingDirectory is not null)
            throw new InvalidDataException("Confirmed deletion did not clear UI and SQLite index.");
        string deleted = Directory.GetDirectories(Directory.GetDirectories(Path.Combine(fixture, "DeletedMeetings")).Single()).Single();
        if (!stored.SequenceEqual(File.ReadAllBytes(Path.Combine(deleted, "filing.json"))))
            throw new InvalidDataException("Recoverable deletion changed originals.");
        RefreshRecovery_Click(this, new RoutedEventArgs());
        if (RecoveryBox.Items.Count != 1) throw new InvalidDataException("Deleted meeting resurrected.");
        return new { Status = "PASS", SqliteLibrary = true, FilterSort = true, InvalidRanges = true,
            ConfirmationAcceptedAndCancelled = true, RecordingDeletionGuard = true, RecoverableDeleteOriginalsUnchanged = true,
            Languages = new[] { "zh-CN", "zh-Hant", "en" }, UserContentUnchanged = true,
            SidebarLeftAligned = true, LanguageLabel = "EN/中", SparseProgressRemainsActiveWithoutFakePercentages = true,
            CaptureStarted = false, MicrophoneCollected = false, ComputerUse = false, Images = images };
    }

    private static ContentPresenter? FindContent(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ContentPresenter presenter) return presenter;
            if (FindContent(child) is { } found) return found;
        }
        return null;
    }
}
