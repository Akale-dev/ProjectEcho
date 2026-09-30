using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private bool useDarkTheme;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // WorkArea is in WPF device-independent units, including system scaling.
        // Constrain only the initial size; moving to another monitor remains free.
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(1, workArea.Width - 24);
        var availableHeight = Math.Max(1, workArea.Height - 24);
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
    }

    private bool libraryExpanded = true;
    private bool libraryRead;

    // Semantic destinations also keep deep links from export/transcription stable.
    private void NavigateTo(int page)
    {
        MainTabs.SelectedIndex = page == 0 ? 0 : page == 4 ? 2 : 1;
        if (page is 1 or 2 or 3 or 5)
        {
            MeetingTabs.SelectedIndex = page switch { 2 => 1, 3 => 3, 5 => 2, _ => 0 };
            libraryExpanded = true;
        }
        UpdateNavigation();
        EnsureTranscriptVersions();
    }

    private void UpdateNavigation()
    {
        RecordingBar.Visibility = MainTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryListPanel.Visibility = libraryExpanded ? Visibility.Visible : Visibility.Collapsed;
        LibraryNavigationButton.Content = libraryExpanded ? "02   会议库   ▾" : "02   会议库   ▸";
        PageContextText.Text = MainTabs.SelectedIndex switch { 0 => "新会议录音", 2 => "诊断与设置", _ => "会议库" };
        RecordNavigationButton.Background = (Brush)FindResource(MainTabs.SelectedIndex == 0 ? "SidebarSelectedBrush" : "SidebarBrush");
        LibraryNavigationButton.Background = (Brush)FindResource(MainTabs.SelectedIndex == 1 ? "SidebarSelectedBrush" : "SidebarBrush");
        SettingsNavigationButton.Background = (Brush)FindResource(MainTabs.SelectedIndex == 2 ? "SidebarSelectedBrush" : "SidebarBrush");
    }

    private void LibraryNavigation_Click(object sender, RoutedEventArgs e)
    {
        bool wasLibrary = MainTabs.SelectedIndex == 1;
        MainTabs.SelectedIndex = 1;
        libraryExpanded = !libraryRead || !wasLibrary || !libraryExpanded;
        if (!libraryRead) RefreshRecovery_Click(this, e);
        UpdateNavigation();
    }

    private void MeetingTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && ReferenceEquals(e.Source, MeetingTabs) && MeetingTabs.SelectedIndex is 2 or 3)
        {
            EnsureMeetingSubtitles();
            EnsureTranscriptVersions();
        }
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string index } && int.TryParse(index, out var page))
        {
            if (page == 0 && ReferenceEquals(sender, RecordNavigationButton)) ClearMeetingForNewRecording();
            NavigateTo(page);
        }
    }

    private void ClearMeetingForNewRecording()
    {
        if (!ProjectEcho.Core.SessionRules.CanStart(state) || WindowCloseRequested) return;
        savedMixCancellation?.Cancel();
        ResetTranscriptPlayback();
        ClearReplaySubtitles();
        synchronizingMeetingSelection = true;
        try { RecoveryBox.SelectedIndex = -1; }
        finally { synchronizingMeetingSelection = false; }
        selectedMeetingDirectory = null;
        selectedMeetingImported = false;
        currentMix = null; mixedPath = null; lastExportDirectory = null;
        SelectedMeetingText.Text = "选择一场会议";
        MeetingMetadataText.Text = "点击左侧会议选择历史记录。";
        FiledTitleBox.Clear(); FiledProjectBox.Text = "未分类";
        TitleBox.Text = ProjectEcho.Core.SessionDefaults.Title(DateTimeOffset.Now);
        UpdateControls();
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        useDarkTheme = !useDarkTheme;
        ApplyVisualTheme();
        UpdateNavigation();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        ApplyVisualTheme();
        SystemParameters.StaticPropertyChanged += OnSystemVisualSettingsChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemVisualSettingsChanged;
    }

    private void OnSystemVisualSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.Invoke(ApplyVisualTheme);
    }

    private void ApplyVisualTheme()
    {
        if (SystemParameters.HighContrast)
        {
            foreach (var key in new[] { "CanvasBrush", "SurfaceBrush", "SidebarBrush", "AccentSoftBrush" })
                Resources[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "TextBrush", "MutedBrush", "SidebarTextBrush", "LineBrush" })
                Resources[key] = SystemColors.WindowTextBrush;
            foreach (var key in new[] { "AccentBrush", "DangerBrush", "SidebarSelectedBrush" })
                Resources[key] = SystemColors.HighlightBrush;
            Resources["AccentTextBrush"] = SystemColors.HighlightTextBrush;
            return;
        }

        var colors = useDarkTheme
            ? new[] { "#152128", "#1E2E36", "#EBF2F5", "#B1C3CD", "#3C505B", "#237F80", "#203F43", "#B94759", "#101C23", "#D9E7EC", "#235660" }
            : new[] { "#F2F5F7", "#FFFFFF", "#192E3A", "#5C6F7B", "#D8E1E6", "#126F70", "#E9F4F2", "#B43748", "#152F3B", "#D9E7EC", "#235660" };
        var keys = new[] { "CanvasBrush", "SurfaceBrush", "TextBrush", "MutedBrush", "LineBrush", "AccentBrush", "AccentSoftBrush", "DangerBrush", "SidebarBrush", "SidebarTextBrush", "SidebarSelectedBrush" };
        for (var index = 0; index < keys.Length; index++)
            Resources[keys[index]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[index]));
        Resources["AccentTextBrush"] = Brushes.White;
    }
}
