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

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabs is not null && NavigationList.SelectedIndex >= 0)
            MainTabs.SelectedIndex = NavigationList.SelectedIndex;
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string index } && int.TryParse(index, out var page))
            NavigationList.SelectedIndex = page;
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        useDarkTheme = !useDarkTheme;
        ApplyVisualTheme();
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
