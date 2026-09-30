using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ProjectEcho.Core;
using ProjectEcho.Core.Library;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private MeetingLibraryQuery libraryQuery = new();
    private bool deletingMeeting;
    private Action<string> openDataDirectory = path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private Func<string, bool>? deletionConfirmationOverride;
    private bool LibraryFilterActive => libraryQuery != new MeetingLibraryQuery();
    private bool CanDeleteMeeting => SessionRules.CanStart(state) && !WindowCloseRequested && !recoveryBusy &&
        !exportBusy && !transcriptBusy && pendingMixes.Count == 0;

    private void OpenDataRoot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string root = ValidateLocalDataRoot(dataRoot);
            Directory.CreateDirectory(root);
            RejectReparseAncestors(root);
            openDataDirectory(root);
        }
        catch (Exception ex) { SetStatus(UiText.T("未能打开数据目录：") + ex.Message); }
    }

    private void LibraryFilter_Click(object sender, RoutedEventArgs e)
    {
        if (!SessionRules.CanStart(state) || recoveryBusy || WindowCloseRequested) return;
        try
        {
            var index = new MeetingLibraryIndex(dataRoot);
            index.Refresh();
            var dialog = new LibraryFilterWindow(libraryQuery, index.Projects()) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                libraryQuery = dialog.Query;
                libraryExpanded = true;
                RefreshRecovery_Click(this, new RoutedEventArgs());
                MainTabs.SelectedIndex = 1;
                UpdateNavigation();
            }
        }
        catch (Exception ex) { RecoveryStatus.Text = UiText.T("筛选未完成：") + ex.Message; }
    }

    private void MeetingMenu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: RecordingDirectory entry } button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        var delete = new MenuItem { Header = UiText.T("删除会议…"), IsEnabled = CanDeleteMeeting };
        delete.Click += async (_, _) =>
        {
            if (!CanDeleteMeeting) return;
            bool confirmed = deletionConfirmationOverride?.Invoke(entry.Name) ?? MessageBox.Show(this,
                    UiText.F($"删除会议“{entry.Name}”？\n会议、录音与转写将移到数据目录内的 DeletedMeetings，可从该目录恢复。"),
                    UiText.T("确认删除会议"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!confirmed) return;
            recoveryWork = DeleteMeetingAsync(entry.Path);
            await recoveryWork;
        };
        menu.Items.Add(delete);
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    // The dialog is kept in the routed handler; this boundary is exercised by the WPF test interface.
    private async Task DeleteMeetingAsync(string directory)
    {
        if (!CanDeleteMeeting) return;
        string root = dataRoot;
        string outcome;
        deletingMeeting = true; recoveryBusy = true; UpdateControls();
        try
        {
            string source = ValidateRecordingDirectory(directory);
            savedMixCancellation?.Cancel();
            ResetTranscriptPlayback();
            ClearReplaySubtitles();
            await savedMixWork;
            await replayLoadWork;
            string deleted = await Task.Run(() => new MeetingLibraryIndex(root).MoveToTrash(source));
            renderedMeetings.Remove(source);
            if (string.Equals(selectedMeetingDirectory, source, StringComparison.OrdinalIgnoreCase)) ClearMeetingForNewRecording();
            outcome = UiText.T("会议已移入可恢复删除目录：") + deleted;
        }
        catch (Exception ex) { outcome = UiText.T("删除未完成，原件保留：") + ex.Message; }
        finally { deletingMeeting = false; recoveryBusy = false; }
        RefreshRecovery_Click(this, new RoutedEventArgs());
        RecoveryStatus.Text = outcome;
        UpdateControls(); EnsureMeetingSubtitles(); EnsureTranscriptVersions();
    }
}

internal sealed class LibraryFilterWindow : Window
{
    public MeetingLibraryQuery Query { get; private set; }
    internal readonly DatePicker From = new(), Through = new();
    internal readonly TextBox Minimum = new(), Maximum = new();
    internal readonly ComboBox Projects = new(), Sorting = new();
    internal readonly TextBlock Error = new() { TextWrapping = TextWrapping.Wrap };

    internal LibraryFilterWindow(MeetingLibraryQuery query, IReadOnlyList<string> projects)
    {
        Query = query; Title = UiText.T("筛选和排序会议"); Width = 450; Height = 560;
        MinWidth = 360; MinHeight = 470; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "CanvasBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        var content = new StackPanel { Margin = new Thickness(24) };
        void Field(string label, UIElement control)
        {
            content.Children.Add(new TextBlock { Text = UiText.T(label), Margin = new Thickness(0, 10, 0, 5) });
            content.Children.Add(control);
        }
        From.SelectedDate = query.DateFrom?.LocalDateTime.Date;
        Through.SelectedDate = query.DateToExclusive?.LocalDateTime.AddDays(-1).Date;
        Field(UiText.T("起始日期（含）"), From); Field(UiText.T("结束日期（含）"), Through);
        Projects.Items.Add(new ComboBoxItem { Content = UiText.T("所有项目"), Tag = null });
        foreach (string project in projects) Projects.Items.Add(new ComboBoxItem { Content = project, Tag = project });
        Projects.SelectedIndex = 0;
        for (int i = 1; i < Projects.Items.Count; i++)
            if (((ComboBoxItem)Projects.Items[i]).Tag as string == query.Project) Projects.SelectedIndex = i;
        Field(UiText.T("项目"), Projects);
        Minimum.Text = query.MinDurationMs is { } min ? (min / 60000d).ToString(CultureInfo.CurrentCulture) : "";
        Maximum.Text = query.MaxDurationMs is { } max ? (max / 60000d).ToString(CultureInfo.CurrentCulture) : "";
        var duration = new Grid(); duration.ColumnDefinitions.Add(new()); duration.ColumnDefinitions.Add(new());
        Minimum.Margin = new Thickness(0, 0, 8, 0); Grid.SetColumn(Maximum, 1);
        Minimum.ToolTip = UiText.T("最短分钟数，留空不限"); Maximum.ToolTip = UiText.T("最长分钟数，留空不限");
        System.Windows.Automation.AutomationProperties.SetName(Minimum, UiText.T("最短分钟数，留空不限"));
        System.Windows.Automation.AutomationProperties.SetName(Maximum, UiText.T("最长分钟数，留空不限"));
        duration.Children.Add(Minimum); duration.Children.Add(Maximum); Field(UiText.T("长度（分钟）：最短 / 最长"), duration);
        string[] names = [UiText.T("日期：最新优先"), UiText.T("日期：最早优先"), UiText.T("长度：最长优先"), UiText.T("长度：最短优先"), UiText.T("标题：升序"), UiText.T("项目：升序")];
        foreach (string name in names) Sorting.Items.Add(UiText.T(name));
        Sorting.SelectedIndex = (int)query.Sort; Field(UiText.T("排序"), Sorting);
        Error.Margin = new Thickness(0, 10, 0, 5); Error.SetResourceReference(ForegroundProperty, "DangerBrush"); content.Children.Add(Error);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var reset = new Button { Content = UiText.T("重置") };
        reset.Click += (_, _) => { Query = new(); DialogResult = true; };
        var cancel = new Button { Content = UiText.T("取消"), IsCancel = true };
        var apply = new Button { Content = UiText.T("应用"), IsDefault = true };
        apply.Click += (_, _) => { try { Query = ReadQuery(); DialogResult = true; } catch (Exception ex) { Error.Text = ex.Message; } };
        actions.Children.Add(reset); actions.Children.Add(cancel); actions.Children.Add(apply); content.Children.Add(actions);
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    internal MeetingLibraryQuery ReadQuery()
    {
        static long? Minutes(TextBox input)
        {
            if (string.IsNullOrWhiteSpace(input.Text)) return null;
            if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double minutes) ||
                !double.IsFinite(minutes) || minutes < 0 || minutes > 5256000)
                throw new InvalidDataException(UiText.T("长度必须是非负分钟数；留空表示不限。"));
            return checked((long)Math.Round(minutes * 60000));
        }
        DateTimeOffset? from = From.SelectedDate is { } start ? new DateTimeOffset(start.Date) : null;
        DateTimeOffset? to = Through.SelectedDate is { } end ? new DateTimeOffset(end.Date.AddDays(1)) : null;
        long? min = Minutes(Minimum), max = Minutes(Maximum);
        if (from >= to || min > max) throw new InvalidDataException(UiText.T("起始值不能大于结束值。"));
        return new(from, to, (Projects.SelectedItem as ComboBoxItem)?.Tag as string, min, max,
            (MeetingLibrarySort)Math.Clamp(Sorting.SelectedIndex, 0, 5));
    }
}
