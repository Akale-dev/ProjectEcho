using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

namespace ProjectEcho.App;

public partial class MainWindow
{
    // Runs on a real WPF window constructed with enumerateSourcesOnLoad:false.
    // Synthetic device identities and a synthetic saved meeting only; no Host, native
    // meter client, real microphone, playback or ASR engine is started by this smoke.
    internal async Task<object> M3hUiSmokeAsync(string evidence, CancellationToken token)
    {
        Need(host is null && !microphoneEnabled && !microphoneRequested && state == SessionState.Idle &&
            !levelPreviewNativeEnabled && levelPreview is null,
            "M3H UI smoke requires no-capture startup with microphone consent off.");
        dataRoot = Path.Combine(evidence, "synthetic-data");
        Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        int preflightCalls = 0, toggleClicks = 0;
        recordingPreflight = _ =>
        {
            preflightCalls++;
            throw new InvalidOperationException("Microphone UI verification must not start recording preflight.");
        };
        var images = new List<string>();
        var measurements = new List<object>();
        var microphone = new AudioDevice("synthetic-m3h-microphone-never-opened", "合成麦克风 · Synthetic microphone");
        var view = transcriptionView!;
        Width = MinWidth; Height = MinHeight; NavigateTo(0);
        MicBox.DisplayMemberPath = "Name"; MicBox.ItemsSource = new[] { microphone }; MicBox.SelectedIndex = -1;
        await RenderAsync();

        Need(MicIconBody.Source is BitmapSource && MicOffMark.Source is BitmapSource,
            "The microphone states are not raster image resources.");
        var onBitmap = MicIconBody.Source as BitmapSource ?? throw new InvalidDataException("ON bitmap resource is missing.");
        var offBitmap = MicOffMark.Source as BitmapSource ?? throw new InvalidDataException("OFF bitmap resource is missing.");
        Need(onBitmap.PixelWidth == onBitmap.PixelHeight && onBitmap.PixelWidth == offBitmap.PixelWidth &&
            onBitmap.PixelHeight == offBitmap.PixelHeight,
            "The microphone images do not share an identical square canvas.");
        object onAlpha = InspectAlpha(onBitmap), offAlpha = InspectAlpha(offBitmap);
        Need(MicControlButton.Background is SolidColorBrush { Color.A: 0 },
            "The microphone button no longer has a transparent idle background.");
        Need(MicControlButton.Content is Grid { Width: 64, Height: 64 } &&
            MicIconBody.Width == 64 && MicIconBody.Height == 64 && MicOffMark.Width == 64 && MicOffMark.Height == 64 &&
            MicIconBody.Stretch == Stretch.Uniform && MicOffMark.Stretch == Stretch.Uniform,
            "The matching microphone images are not pinned to one fixed 64 DIP canvas.");
        Need(MicControlButton.Width == 76 && MicControlButton.Height == 76 && MicStateText.Height == 34,
            "The button or state-label reservation can change size while toggling.");

        MicControlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await RenderAsync();
        Need(HasValidationIssue(MicBox) && !microphoneRequested && !microphoneEnabled && MicOffMark.IsVisible,
            "The microphone button accepted a request without an explicitly selected device.");
        MicBox.SelectedItem = microphone;
        await RenderAsync();
        Need(!HasValidationIssue(MicBox) && !microphoneRequested && !microphoneEnabled &&
            OptionsForCurrentSelection().MicrophoneDeviceId is null,
            "Selecting a device enabled microphone collection or passed a disabled microphone to the Host.");

        double largestCenterDelta = 0;
        foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
        foreach (bool dark in new[] { false, true })
        {
            ChangeLanguage(language, persist: false);
            useDarkTheme = dark; ApplyVisualTheme(); NavigateTo(0); UpdateControls();
            await RenderAsync();
            string label = language + (dark ? "-dark" : "-light");
            Need(ApplicationVersionText.Text == "Ver. M3O Lite", "The UI version does not identify M3H.");
            Need(ReferenceEquals(RefreshButton.Style, FindResource("PrimaryButton")) &&
                ReferenceEquals(RefreshButton.Style, StartButton.Style),
                "Refresh sources and Start recording do not use the same primary button style.");
            Need(M3dVisualChildren<TextBlock>(this).Any(text => text.Text == UiText.T("录音状态")),
                "The recording-status heading was not renamed in this language.");
            Need(MicOffMark.IsVisible && !MicIconBody.IsVisible && !microphoneRequested &&
                MicCaptionText.Text == UiText.T("本机Mic录音"),
                "The microphone does not begin the layout case in its captioned OFF state.");
            Point buttonCenter = Center(MicControlButton);
            Point imageCenter = Center(MicOffMark);
            Point captionPosition = MicCaptionText.TranslatePoint(new Point(), this);
            Point statusPosition = MicStateText.TranslatePoint(new Point(), this);
            Need(Distance(buttonCenter, imageCenter) <= 0.6 &&
                Math.Abs(MicControlButton.ActualWidth - 76) <= 0.1 && Math.Abs(MicControlButton.ActualHeight - 76) <= 0.1,
                "The idle microphone canvas is not centered in its fixed-size button.");
            Point selectorBottom = MicBox.TranslatePoint(new Point(0, MicBox.ActualHeight), SelectionPanel);
            Point buttonTop = MicControlButton.TranslatePoint(new Point(), SelectionPanel);
            Need(buttonTop.Y >= selectorBottom.Y && buttonTop.X >= 0 &&
                buttonTop.X + MicControlButton.ActualWidth <= SelectionPanel.ActualWidth + 1,
                "The microphone button is not below the selector or is clipped at minimum window width.");
            SaveLayoutImage(evidence, "m3h-microphone-off-" + label + ".png", 1, images);

            // Exercise the actual Click handler repeatedly. In this constructor mode the
            // pre-recording request may change, but native level preview is disabled.
            for (int cycle = 0; cycle < 4; cycle++)
            {
                MicControlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); toggleClicks++;
                await RenderAsync();
                Need(microphoneRequested && !microphoneEnabled && MicIconBody.IsVisible && !MicOffMark.IsVisible,
                    "An explicit pre-recording microphone request did not display the ON image.");
                AssertPinned(MicIconBody);
                if (cycle == 0)
                {
                    SaveLayoutImage(evidence, "m3h-microphone-on-" + label + ".png", 1, images);
                    await CheckPressedTemplateAsync(MicIconBody, label, save: language == "zh-CN");
                    AssertPinned(MicIconBody);
                }
                MicControlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); toggleClicks++;
                await RenderAsync();
                Need(!microphoneRequested && !microphoneEnabled && MicOffMark.IsVisible && !MicIconBody.IsVisible,
                    "The second microphone click did not return to the OFF image.");
                AssertPinned(MicOffMark);
            }
            measurements.Add(new
            {
                Language = language, Theme = dark ? "dark" : "light", WindowWidth = ActualWidth, WindowHeight = ActualHeight,
                ButtonWidth = MicControlButton.ActualWidth, ButtonHeight = MicControlButton.ActualHeight,
                CanvasWidth = MicOffMark.ActualWidth, CanvasHeight = MicOffMark.ActualHeight,
                StateLabelHeight = MicStateText.ActualHeight,
                CenterX = buttonCenter.X, CenterY = buttonCenter.Y
            });

            void AssertPinned(Image visibleImage)
            {
                double delta = Distance(Center(visibleImage), imageCenter);
                largestCenterDelta = Math.Max(largestCenterDelta, delta);
                Need(delta <= 0.1 && Distance(Center(MicControlButton), buttonCenter) <= 0.1 &&
                    Distance(MicCaptionText.TranslatePoint(new Point(), this), captionPosition) <= 0.1 &&
                    Distance(MicStateText.TranslatePoint(new Point(), this), statusPosition) <= 0.1 &&
                    Math.Abs(visibleImage.ActualWidth - 64) <= 0.1 && Math.Abs(visibleImage.ActualHeight - 64) <= 0.1 &&
                    Math.Abs(MicStateText.ActualHeight - 34) <= 0.1,
                    $"Microphone on/off layout drifted in {label}: center delta {delta:F3} DIP.");
            }
        }

        // Create a small synthetic saved meeting so duplicate-title behavior is tested
        // through selection/navigation, without reading the user's meeting files.
        string meeting = Path.Combine(RecordingsRoot, "synthetic-m3h-layout");
        await Task.Run(() => WriteCloseFixture(meeting), token);
        SelectMeeting(meeting, loadSavedMix: false);
        await Task.WhenAll(savedMixWork, replayLoadWork, transcriptWork).WaitAsync(token);
        foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
        foreach (bool dark in new[] { false, true })
        {
            ChangeLanguage(language, persist: false); useDarkTheme = dark; ApplyVisualTheme();
            NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 0; UpdateControls();
            await RenderAsync();
            Need(selectedMeetingDirectory == meeting && SelectedMeetingText.IsVisible &&
                view.MeetingText.Visibility == Visibility.Collapsed,
                "The selected meeting lost its main title or retains the redundant transcription title.");
            Need(view.MeetingText.Parent is Grid, "The transcription layout root is no longer a grid.");
            var workspace = view.MeetingText.Parent as Grid ?? throw new InvalidDataException("Transcription layout root is missing.");
            Need(workspace.RowDefinitions[0].ActualHeight <= 0.1 &&
                view.WorkspaceTabs.TranslatePoint(new Point(), workspace).Y <= 0.1,
                "The removed duplicate title still reserves vertical space above the workspace tabs.");
            Need(view.HardwareBox.Items.Cast<ComboBoxItem>().Select(item => item.Tag?.ToString())
                    .SequenceEqual(new[] { "Cpu", "Auto", "Vulkan" }),
                "CUDA is still selectable or a supported hardware option disappeared.");
            Need(view.SaveConfigurationButton.Content?.ToString() == UiText.T("保存为默认配置"),
                "The default-settings button was not renamed in this language.");
            view.HardwareConfigurationRow.BringIntoView(); await RenderAsync();
            Point hardware = view.HardwareBox.TranslatePoint(new Point(), view.HardwareConfigurationRow);
            Point threads = view.ThreadsBox.TranslatePoint(new Point(), view.HardwareConfigurationRow);
            Point gpu = view.GpuDeviceBox.TranslatePoint(new Point(), view.HardwareConfigurationRow);
            Need(view.HardwareBox.ActualWidth is > 0 and <= 300 &&
                Math.Abs(hardware.Y - threads.Y) <= 1 && Math.Abs(threads.Y - gpu.Y) <= 1 &&
                hardware.X < threads.X && threads.X < gpu.X &&
                gpu.X + view.GpuDeviceBox.ActualWidth <= view.HardwareConfigurationRow.ActualWidth + 1,
                "The shortened hardware selector or adjacent numeric fields are clipped/misaligned at minimum width.");
            SaveLayoutImage(evidence, "m3h-transcription-" + language + (dark ? "-dark" : "-light") + ".png", 1, images);
        }
        ResetConsentAfterSession(); ClearMeetingForNewRecording();
        ChangeLanguage("zh-CN", persist: false); useDarkTheme = false; ApplyVisualTheme(); NavigateTo(0); UpdateControls();
        Width = 1280; Height = 930; MicBox.SelectedItem = microphone;
        await RenderAsync(); SaveLayoutImage(evidence, "m3h-recording-full-off.png", 1, images);
        MicControlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await RenderAsync(); SaveLayoutImage(evidence, "m3h-recording-full-on.png", 1, images);
        MicControlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ResetConsentAfterSession(); UpdateControls();
        await RenderAsync();
        Need(host is null && !microphoneEnabled && !microphoneRequested && player.Source is null && preflightCalls == 0 &&
            !levelPreviewNativeEnabled && levelPreview is null &&
            !transcriptBusy && state == SessionState.Idle,
            "The UI smoke unexpectedly started recording, microphone collection, playback or transcription.");
        return new
        {
            Status = "PASS", Version = ApplicationVersionText.Text, ActualWpfControls = true,
            Languages = new[] { "zh-CN", "zh-Hant", "en" }, Themes = new[] { "light", "dark" },
            MatchingRasterCanvasAndTransparency = true, RasterWidth = onBitmap.PixelWidth, RasterHeight = onBitmap.PixelHeight,
            OnAlpha = onAlpha, OffAlpha = offAlpha, ToggleClicks = toggleClicks, LargestCenterDeltaDip = largestCenterDelta,
            FixedButtonCanvasAndStateLabel = true, PrimaryRefreshSources = true, HardwareDropdownAtMost300Dip = true,
            CudaOptionRemoved = true, DefaultConfigurationButtonRenamed = true,
            DuplicateMeetingTitleCollapsedWithoutGap = true,
            PressedTemplate = "ACTUAL WPF TRIGGER; ButtonBase.IsPressed protected setter invoked by reflection; not physical input",
            PressScale = 0.92, PressedCenterUnchanged = true, Measurements = measurements, Images = images,
            SourceMode = "SYNTHETIC DEVICE METADATA / SYNTHETIC SAVED MEETING / NATIVE PREVIEW DISABLED",
            CaptureStarted = false, MicrophoneCollected = false, PhysicalMouseAndCrossMonitorDpi = "NOT_RUN"
        };

        async Task RenderAsync()
        {
            await levelPreviewTransition.WaitAsync(token);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, token);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
        }

        async Task CheckPressedTemplateAsync(Image visibleImage, string label, bool save)
        {
            MicControlButton.ApplyTemplate();
            Need(MicControlButton.Template.FindName("MicChrome", MicControlButton) is Border,
                "The microphone button does not use its dedicated tactile template.");
            var chrome = MicControlButton.Template.FindName("MicChrome", MicControlButton) as Border
                ?? throw new InvalidDataException("Microphone template border is missing.");
            var setter = typeof(ButtonBase).GetProperty(nameof(ButtonBase.IsPressed), BindingFlags.Instance | BindingFlags.Public)
                ?.GetSetMethod(nonPublic: true) ?? throw new InvalidDataException("Cannot exercise the WPF IsPressed setter.");
            Point before = Center(visibleImage), buttonBefore = Center(MicControlButton);
            try
            {
                setter.Invoke(MicControlButton, [true]); await RenderAsync();
                Need(MicControlButton.IsPressed && chrome.RenderTransform is ScaleTransform { ScaleX: 0.92, ScaleY: 0.92 } &&
                    chrome.RenderTransformOrigin == new Point(0.5, 0.5) && Math.Abs(chrome.Opacity - 0.76) <= 0.001,
                    "The actual pressed-state template did not apply its centered scale and shading.");
                Need(Distance(Center(visibleImage), before) <= 0.1 && Distance(Center(MicControlButton), buttonBefore) <= 0.1,
                    "Pressed feedback moves the microphone/button center instead of only scaling it.");
                Point topLeft = visibleImage.TranslatePoint(new Point(), this);
                Point bottomRight = visibleImage.TranslatePoint(new Point(64, 64), this);
                Need(Math.Abs(bottomRight.X - topLeft.X - 64 * 0.92) <= 0.1 &&
                    Math.Abs(bottomRight.Y - topLeft.Y - 64 * 0.92) <= 0.1,
                    "The microphone image did not visually shrink with the pressed template.");
                if (save) SaveLayoutImage(evidence, "m3h-microphone-pressed-" + label + ".png", 1, images);
            }
            finally { setter.Invoke(MicControlButton, [false]); await RenderAsync(); }
            Need(!MicControlButton.IsPressed && chrome.RenderTransform.Value.IsIdentity && Math.Abs(chrome.Opacity - 1) <= 0.001,
                "Releasing the simulated press did not restore the normal icon appearance.");
        }

        Point Center(FrameworkElement element) => element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), this);
        static double Distance(Point first, Point second) => Math.Max(Math.Abs(first.X - second.X), Math.Abs(first.Y - second.Y));
        static object InspectAlpha(BitmapSource source)
        {
            var rgba = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[checked(rgba.PixelWidth * rgba.PixelHeight * 4)];
            rgba.CopyPixels(pixels, rgba.PixelWidth * 4, 0);
            int transparent = 0, visible = 0;
            for (int index = 3; index < pixels.Length; index += 4)
                if (pixels[index] == 0) transparent++; else visible++;
            Need(transparent > rgba.PixelWidth * rgba.PixelHeight / 4 && visible > rgba.PixelWidth * rgba.PixelHeight / 10 &&
                pixels[3] == 0 && pixels[(rgba.PixelWidth - 1) * 4 + 3] == 0 && pixels[^1] == 0,
                "The generated icon is blank, lacks real transparent margins or has an opaque background.");
            return new { FullyTransparentPixels = transparent, VisiblePixels = visible };
        }
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
