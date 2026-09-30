using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ProjectEcho.App;

public partial class MainWindow
{
    // Renders only this window. Never starts audio, enumerates historical files,
    // opens a browser, changes a system DPI setting or takes a desktop screenshot.
    internal async Task<object> UiLayoutSmokeAsync(string evidenceDirectory)
    {
        if (host is not null || MicEnabled.IsChecked == true || SourceBox.SelectedIndex != -1)
            throw new InvalidOperationException("布局检查必须从无采集的默认状态开始。");
        string directory = Path.GetFullPath(evidenceDirectory);
        string localRoot = Path.GetFullPath(Path.Combine(projectRoot, ".local")) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(localRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("布局证据必须保存到当前项目的 .local 内。");
        RejectReparseAncestors(directory);
        Directory.CreateDirectory(directory);
        VerifyNonCaptureControlStates();
        var originalWidth = Width;
        var originalHeight = Height;
        var originalTitle = TitleBox.Text;
        var originalDataRoot = DataRootText.Text;
        var originalStatus = StatusBox.Text;
        var originalScope = ScopeText.Text;
        var originalState = SessionStateText.Text;
        var files = new List<string>();
        try
        {
            TitleBox.Text = "会议 · 布局检查（合成界面）";
            DataRootText.Text = @"…\ProjectEcho\Data（布局检查已隐去实际路径）";
            ScopeText.Text = "进程树：未选择音频来源。\n独立 mic：关闭，不采集";
            SessionStateText.Text = "尚未录音";
            RemoteHealth.Text = "尚未采集";
            MicHealth.Text = "关闭 · 不采集";
            RemoteLevel.Value = MicLevel.Value = 0;
            ConnectionText.Text = "捕获连接：未启动";
            StorageText.Text = "保存状态：尚未创建本次录音";
            StatusBox.Text = "布局检查：没有启动捕获进程，没有打开音频设备，没有读取历史录音。";
            foreach (var size in new[] { (Width: 1280d, Height: 900d, Label: "standard"), (Width: MinWidth, Height: MinHeight, Label: "minimum") })
            {
                Width = size.Width;
                Height = size.Height;
                for (int page = 0; page < MainTabs.Items.Count; page++)
                {
                    NavigationList.SelectedIndex = page;
                    await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
                    if (MainTabs.SelectedIndex != page || !ScopeText.IsVisible || !StopButton.IsVisible)
                        throw new InvalidOperationException("任务导航或常驻录音状态不可见。");
                    SaveLayoutImage(directory, $"{size.Label}-page-{page + 1}-light.png", 1, files);
                    if (page == 3 && transcriptionView is not null)
                    {
                        for (int subpage = 1; subpage < transcriptionView.WorkspaceTabs.Items.Count; subpage++)
                        {
                            transcriptionView.WorkspaceTabs.SelectedIndex = subpage;
                            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
                            SaveLayoutImage(directory, $"{size.Label}-transcription-{subpage + 1}-light.png", 1, files);
                            await CaptureScrolledPageAsync(directory, $"{size.Label}-transcription-{subpage + 1}-bottom-light.png", files);
                        }
                        transcriptionView.WorkspaceTabs.SelectedIndex = 0;
                    }
                    await CaptureScrolledPageAsync(directory, $"{size.Label}-page-{page + 1}-bottom-light.png", files);
                }
            }
            Width = 1280;
            Height = 900;
            useDarkTheme = true;
            ApplyVisualTheme();
            for (int page = 0; page < MainTabs.Items.Count; page++)
            {
                NavigationList.SelectedIndex = page;
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
                SaveLayoutImage(directory, $"standard-page-{page + 1}-dark.png", 1, files);
                if (page == 3 && transcriptionView is not null)
                {
                    for (int subpage = 1; subpage < transcriptionView.WorkspaceTabs.Items.Count; subpage++)
                    {
                        transcriptionView.WorkspaceTabs.SelectedIndex = subpage;
                        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
                        SaveLayoutImage(directory, $"standard-transcription-{subpage + 1}-dark.png", 1, files);
                    }
                    transcriptionView.WorkspaceTabs.SelectedIndex = 0;
                }
            }
            useDarkTheme = false;
            ApplyVisualTheme();
            NavigationList.SelectedIndex = 0;
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            SaveLayoutImage(directory, "recording-render-150-percent.png", 1.5, files);
            SaveLayoutImage(directory, "recording-render-200-percent.png", 2, files);
            return new
            {
                Status = "PASS", OsBuild = Environment.OSVersion.Version.ToString(),
                Test = "Actual WPF navigation/render + existing synthetic privacy/playback guard assertions",
                Pages = MainTabs.Items.Count, MinimumWidth = MinWidth, MinimumHeight = MinHeight,
                LightAndDarkRendered = true, HigherResolutionRenders = "150% and 200%; render scale only, not a real OS DPI switch",
                CaptureStarted = false, MicrophoneCollected = false, HistoricalRecordingsRead = false,
                SourceMode = "None selected; UI uses synthetic/redacted labels",
                InteractiveKeyboardAndScreenReader = "NOT_RUN", RealHighContrastSystem = "NOT_RUN", Images = files
            };
        }
        finally
        {
            Width = originalWidth;
            Height = originalHeight;
            TitleBox.Text = originalTitle;
            DataRootText.Text = originalDataRoot;
            StatusBox.Text = originalStatus;
            ScopeText.Text = originalScope;
            SessionStateText.Text = originalState;
            useDarkTheme = false;
            ApplyVisualTheme();
            NavigationList.SelectedIndex = 0;
        }
    }

    private async Task CaptureScrolledPageAsync(string directory, string fileName, List<string> files)
    {
        if (MainTabs.SelectedItem is not TabItem { Content: ScrollViewer scroll } || scroll.ScrollableHeight <= 0) return;
        scroll.ScrollToEnd();
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        SaveLayoutImage(directory, fileName, 1, files);
        scroll.ScrollToTop();
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
    }

    private void SaveLayoutImage(string directory, string fileName, double scale, List<string> files)
    {
        var visual = (FrameworkElement)Content;
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * scale),
            (int)Math.Ceiling(visual.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path = Path.Combine(directory, fileName);
        using var output = File.Create(path);
        encoder.Save(output);
        files.Add(path);
    }
}
