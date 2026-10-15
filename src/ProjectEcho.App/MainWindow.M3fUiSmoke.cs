using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProjectEcho.Core;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    // Exercises actual WPF controls and synthetic files. Never executes an ASR engine or opens capture.
    internal async Task<object> M3fUiSmokeAsync(string evidence, CancellationToken token)
    {
        Need(host is null && !microphoneEnabled && !microphoneRequested, "UI smoke must not capture audio.");
        dataRoot = Path.Combine(evidence, "synthetic-data"); Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        var view = transcriptionView!;
        Width = MinWidth; Height = MinHeight;
        var images = new List<string>();
        foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
        {
            ChangeLanguage(language, persist: false); NavigateTo(0);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            Need(BrandLogo.Source is DrawingImage drawing && drawing.Drawing is DrawingGroup { Children.Count: 5 }, "Sidebar logo is not the original five-part vector.");
            Need(ApplicationVersionText.Text == "Ver. M3P Lite", "M3F version is missing.");
            Point logo = BrandLogo.TranslatePoint(new Point(0, BrandLogo.ActualHeight / 2), BrandHeaderPanel);
            Point name = BrandNameText.TranslatePoint(new Point(0, BrandNameText.ActualHeight / 2), BrandHeaderPanel);
            Point version = ApplicationVersionText.TranslatePoint(new Point(ApplicationVersionText.ActualWidth, 0), BrandHeaderPanel);
            Need(Math.Abs(logo.Y - name.Y) <= 1 && logo.X + BrandLogo.ActualWidth <= name.X && version.X <= BrandHeaderPanel.ActualWidth + 1,
                "Logo, title and version are not aligned within the sidebar.");
            Need(!M3dVisualChildren<TextBlock>(this).Any(block => block.Text == UiText.T("确认会议声音来自哪里，再决定是否录下自己的麦克风。")),
                "Removed recording instruction remains visible.");
            SaveLayoutImage(evidence, "m3f-brand-" + language + ".png", 1, images);
        }
        SaveLayoutImage(evidence, "m3f-vector-200-percent.png", 2, images);
        ChangeLanguage("zh-CN", persist: false);
        NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 0;
        whisperExecutable = whisperModel = null;
        view.WhisperExeText.Text = UiText.T("未配置程序"); view.WhisperModelText.Text = UiText.T("未配置模型");
        view.ModelLicenseBox.IsChecked = false; view.ThreadsBox.Text = "0"; view.GpuDeviceBox.Text = "99";
        view.HardwareBox.SelectedIndex = -1; UpdateControls();
        Need(view.RunWhisperButton.IsEnabled, "ASR validation is unreachable without a selected meeting.");
        view.RunWhisperButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
        Need(new FrameworkElement[] { view.MeetingText, view.WhisperExeButton, view.WhisperModelButton, view.ModelLicenseBox,
            view.ThreadsBox, view.GpuDeviceBox, view.HardwareBox }.All(HasValidationIssue), "Invalid ASR inputs are not individually highlighted.");
        Need(fieldValidationIssues[view.MeetingText].Outline is not null && fieldValidationIssues[view.WhisperExeButton].Outline is not null &&
            fieldValidationIssues[view.ModelLicenseBox].Outline is not null && !transcriptBusy && transcriptWork.IsCompleted,
            "Visible invalid inputs have no red outline, or validation started a transcription job.");
        SaveLayoutImage(evidence, "m3f-asr-invalid-fields.png", 1, images);

        view.ThreadsBox.Text = "x"; Need(HasValidationIssue(view.ThreadsBox), "An invalid correction cleared the thread error.");
        view.ThreadsBox.Text = "4"; view.GpuDeviceBox.Text = "0"; view.HardwareBox.SelectedIndex = 0;
        view.ModelLicenseBox.IsChecked = true;
        Need(!HasValidationIssue(view.ThreadsBox) && !HasValidationIssue(view.GpuDeviceBox) &&
            !HasValidationIssue(view.HardwareBox) && !HasValidationIssue(view.ModelLicenseBox), "Valid edits did not clear their red frames.");
        string exe = Path.Combine(dataRoot, "never-executed.exe"), model = Path.Combine(dataRoot, "synthetic-model.bin");
        File.WriteAllText(exe, "Synthetic executable path validation only; never executed.");
        File.WriteAllText(model, "Synthetic model path validation only; never passed to an engine.");
        whisperExecutable = exe; whisperModel = model;
        view.WhisperExeText.Text = exe; view.WhisperModelText.Text = model;
        Need(TryValidateWhisperConfiguration(requireAudio: false, out var config) && config.Threads == 4 &&
            !HasValidationIssue(view.WhisperExeButton) && !HasValidationIssue(view.WhisperModelButton), "Valid configuration remains blocked.");
        Need(!TryValidateWhisperConfiguration(requireAudio: true, out _) && HasValidationIssue(view.MeetingText), "Missing meeting did not remain blocked.");
        string meeting = Path.Combine(RecordingsRoot, "synthetic-validation"); WriteCloseFixture(meeting);
        ProjectEcho.Core.Transcription.MeetingWhisperSettingsStore.SnapshotDefaults(meeting, config, AppContext.BaseDirectory);
        SelectMeeting(meeting, loadSavedMix: false); NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 0;
        Need(!TryValidateWhisperConfiguration(requireAudio: true, out _) && HasValidationIssue(view.AudioReadinessText) &&
            !HasValidationIssue(view.MeetingText), "Missing mix was not mapped to the local audio prerequisite.");
        currentMix = await Task.Run(() => M2ExportService.RenderMeeting(meeting, encoder: null, token), token);
        UpdateControls();
        Need(TryValidateWhisperConfiguration(requireAudio: true, out _) && !WhisperValidationFields().Any(HasValidationIssue),
            "Correcting all prerequisites did not permit recognition validation.");
        Need(host is null && !microphoneEnabled && player.Source is null && state == SessionState.Idle && !transcriptBusy,
            "Validation started capture, playback or ASR.");
        return new
        {
            Status = "PASS", Version = "Ver. M3P Lite", VectorLogoSharesTitleRow = true,
            RemovedRecordingInstruction = true, MinimumWidthThreeLanguages = true, RasterPreviewAtTwoScales = true,
            AsrInvalidFieldsOutlined = true, CorrectedFieldsClearAutomatically = true, MissingMeetingAndMixStayBlocked = true,
            SourceMode = "SYNTHETIC FILES / NO CAPTURE / NO MODEL EXECUTION", CaptureStarted = false,
            MicrophoneCollected = false, Images = images
        };
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
