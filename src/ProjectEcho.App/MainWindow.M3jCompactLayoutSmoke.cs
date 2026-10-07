using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProjectEcho.Core;

namespace ProjectEcho.App;

public partial class MainWindow
{
    // Only actual WPF layout and no-capture controls. The size cases use logical
    // window units; they never change Windows display scaling or open a browser.
    internal async Task<object> M3jCompactLayoutSmokeAsync(string evidence, CancellationToken token)
    {
        Need(!levelPreviewNativeEnabled && host is null && !microphoneEnabled && !microphoneRequested &&
            SourceBox.SelectedIndex == -1 && state == SessionState.Idle,
            "Compact-layout smoke requires the no-capture constructor and unset consent.");
        dataRoot = Path.Combine(evidence, "synthetic-data");
        Directory.CreateDirectory(dataRoot);
        DataRootText.Text = dataRoot;
        var images = new List<string>();
        var measurements = new List<object>();
        string originalLanguage = UiText.Language;
        double originalWidth = Width, originalHeight = Height;
        try
        {
            foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
            foreach (var size in new[]
            {
                (Width: 1920d, Height: 1000d, Label: "1080-display-logical-100"),
                (Width: 1536d, Height: 820d, Label: "1080-display-logical-125"),
                (Width: 1280d, Height: 930d, Label: "default"),
                (Width: 960d, Height: 640d, Label: "minimum")
            })
            {
                Width = size.Width; Height = size.Height;
                ChangeLanguage(language, persist: false); NavigateTo(0); ModeBox.SelectedIndex = 0;
                UpdateControls(); RecordingScrollViewer.ScrollToTop(); await RenderAsync();
                Need(FindName("BrowserSetupExpander") is null &&
                    !M3dVisualChildren<Button>(this).Any(button => button.Content?.ToString() == UiText.T("打开 Chrome 专用实例与合成测试页")),
                    "The removed synthetic-browser entry remains in the recording UI.");
                Need(ScopeText.IsVisible && !string.IsNullOrWhiteSpace(ScopeText.Text) &&
                    MicOffMark.IsVisible && !MicIconBody.IsVisible && !OptionsForCurrentSelection().MicrophoneEnabled,
                    "Compaction hid the recording scope or changed microphone consent.");
                Need(MicControlButton.ActualWidth == 76 && MicControlButton.ActualHeight == 76 &&
                    MicOffMark.ActualWidth == 64 && MicOffMark.ActualHeight == 64,
                    "Compaction reduced the microphone click target or image size.");
                double sourceHeight = RecordingSourceCard.ActualHeight;
                double statusHeight = RecordingStateCard.ActualHeight;
                double previousStatusHeight = statusHeight - 30; // Same content, former vertical padding 10 instead of 25.
                if (language == "zh-CN" && size.Label is "default" or "1080-display-logical-100")
                    Need(statusHeight / previousStatusHeight is >= 1.23 and <= 1.27,
                        "The recording-status card did not grow by the requested approximately 25 percent.");
                Point receiptBottom = RecordingProcessingStatus.TranslatePoint(
                    new Point(0, RecordingProcessingStatus.ActualHeight), RecordingScrollViewer);
                Point receiptTop = RecordingProcessingStatus.TranslatePoint(new Point(), RecordingScrollViewer);
                bool receiptVisibleWithoutScrolling = receiptTop.Y >= 0 &&
                    receiptBottom.Y <= RecordingScrollViewer.ActualHeight - RecordingScrollViewer.Padding.Bottom + 1;
                SaveLayoutImage(evidence, $"m3j-compact-{language}-{size.Label}.png", 1, images);
                if (size.Label == "1080-display-logical-100")
                    Need(receiptVisibleWithoutScrolling, $"The post-recording receipt is below the initial 1080-display viewport: requested {size.Width}x{size.Height}, actual {ActualWidth}x{ActualHeight}; source={sourceHeight}; receipt={receiptTop.Y}..{receiptBottom.Y}; scroll={RecordingScrollViewer.ActualHeight}.");
                Need(sourceHeight <= (size.Label == "minimum" ? 310 : 275),
                    $"The compact source card regained unnecessary vertical space: {language}, {size.Label}, {sourceHeight} DIP.");
                double recordingHeader = PageHeader.ActualHeight;
                if (size.Label is "default" or "1080-display-logical-100")
                {
                    BeginRecordingProcessing(Path.Combine(dataRoot, "synthetic-no-audio-meeting"),
                        "Synthetic layout · 合成界面 · this is not a recorded meeting");
                    foreach (SessionState displayedState in new[] { SessionState.Recording, SessionState.Paused, SessionState.Stopping })
                    {
                        state = displayedState; UpdateControls(); await RenderAsync();
                        Need(ScopeText.IsVisible && !ModeBox.IsEnabled && !PlaybackButton.IsEnabled &&
                            RecordingProcessingHeading.IsVisible, "An active recording state lost scope, locks or the processing card.");
                    }
                    state = SessionState.Saved;
                    recordingProcessingState = string.Join('\n', Enumerable.Repeat(
                        "SYNTHETIC RESULT / 合成处理记录：完整 WAV 已校验；原轨保留；详细结果可滚动查看。", 12));
                    UpdateControls(); RecordingScrollViewer.ScrollToTop(); await RenderAsync();
                    double longReceiptBottom = RecordingProcessingStatus.TranslatePoint(
                        new Point(0, RecordingProcessingStatus.ActualHeight), RecordingScrollViewer).Y;
                    Need(RecordingProcessingStatus.ActualHeight <= 100 && RecordingProcessingStatus.ExtentHeight >
                        RecordingProcessingStatus.ViewportHeight, "Long processing results no longer use their own bounded scroll area.");
                    if (size.Label == "1080-display-logical-100")
                        Need(RecordingProcessingHeading.TranslatePoint(new Point(), RecordingScrollViewer).Y < RecordingScrollViewer.ActualHeight,
                            "The taller status card hid the postprocessing heading.");
                    SaveLayoutImage(evidence, $"m3j-compact-long-result-{language}-{size.Label}.png", 1, images);
                    state = SessionState.Idle; ClearRecordingProcessing(); UpdateControls(); await RenderAsync();
                }

                ModeBox.SelectedIndex = 1; UpdateControls(); await RenderAsync();
                Need(EndpointScopeNotice.IsVisible && !OptionsForCurrentSelection().EndpointScopeConfirmed &&
                    ModeBox.Items.Count == 2 && EndpointBox.IsEnabled,
                    "Compaction removed endpoint scope confirmation or the source selector.");
                foreach (int page in new[] { 1, 2, 3, 5 })
                {
                    NavigateTo(page); await RenderAsync();
                    Need(ScopeText.Visibility == Visibility.Collapsed &&
                        PageHeader.ActualHeight <= recordingHeader - 35,
                        "The meeting library still reserves a scope/status subtitle row.");
                }
                if (size.Label == "default")
                    SaveLayoutImage(evidence, $"m3j-compact-library-{language}.png", 1, images);
                double libraryHeader = PageHeader.ActualHeight;
                NavigateTo(4); await RenderAsync();
                Need(ScopeText.IsVisible, "The diagnosis page lost the source scope text.");
                measurements.Add(new
                {
                    Language = language, size.Label, RequestedWidth = size.Width, RequestedHeight = size.Height,
                    WindowWidth = ActualWidth, WindowHeight = ActualHeight, SourceCardHeight = sourceHeight,
                    StatusCardHeight = statusHeight, PreviousStatusCardHeight = previousStatusHeight,
                    ReceiptBottom = receiptBottom.Y, ReceiptVisibleWithoutScrolling = receiptVisibleWithoutScrolling,
                    RecordingHeaderHeight = recordingHeader, LibraryHeaderHeight = libraryHeader
                });
            }
            Need(host is null && !microphoneRequested && !microphoneEnabled && levelPreview is null &&
                player.Source is null && !transcriptBusy, "Layout verification started audio or inference.");
            var dpi = VisualTreeHelper.GetDpi(this);
            return new
            {
                Status = "PASS", Version = ApplicationVersionText.Text, ActualWpfLayout = true,
                BrowserSyntheticEntryRemoved = true, RecordingScopeAndEndpointConsentRetained = true,
                LibraryScopeRowCollapsed = true, MicButtonAndBitmapSizesRetained = true,
                Measurements = measurements, Images = images, ActualDpiX = dpi.PixelsPerInchX, ActualDpiY = dpi.PixelsPerInchY,
                WindowSizeCases = "WPF logical units only; physical 1080 display / Windows DPI switch NOT_RUN",
                CaptureStarted = false, MicrophoneCollected = false, PlaybackStarted = false, BrowserOpened = false
            };
        }
        finally
        {
            state = SessionState.Idle; ClearRecordingProcessing();
            Width = originalWidth; Height = originalHeight; ModeBox.SelectedIndex = 0;
            ChangeLanguage(originalLanguage, persist: false); NavigateTo(0); UpdateControls();
        }

        async Task RenderAsync()
        {
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
        }
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
