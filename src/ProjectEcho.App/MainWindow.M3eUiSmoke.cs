using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

namespace ProjectEcho.App;

public partial class MainWindow
{
    // Exercises navigation and request construction only; no Host or audio client is opened.
    internal async Task<object> M3eUiSmokeAsync(string evidence, CancellationToken token)
    {
        Need(host is null && !microphoneEnabled && !microphoneRequested && SourceBox.SelectedIndex == -1,
            "M3E UI smoke requires the no-capture startup path.");
        dataRoot = Path.Combine(evidence, "synthetic-data");
        Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        var view = transcriptionView!;
        var images = new List<string>();
        const string titleDraft = "  尚未保存的新会议标题 · M3E draft  ";
        NavigateTo(0); TitleBox.Text = titleDraft;
        foreach (int page in new[] { 4, 1, 3, 4 })
        {
            NavigateTo(page);
            RecordNavigationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Need(TitleBox.Text == titleDraft && MainTabs.SelectedIndex == 0 && selectedMeetingDirectory is null,
                "Returning to the recording page discarded the title draft or retained a library selection.");
        }
        TitleBox.Clear(); NavigateTo(4);
        RecordNavigationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Need(TitleBox.Text.Length == 0, "Navigation replaced an intentionally empty title draft.");
        TitleBox.Text = titleDraft;

        Need(FindName("EndpointConsent") is null, "The redundant endpoint confirmation checkbox remains.");
        var endpoint = new AudioDevice("synthetic-endpoint-no-client", "合成输出设备 · Synthetic endpoint");
        EndpointBox.ItemsSource = new[] { endpoint };
        ModeBox.SelectedIndex = 1;
        Need(!OptionsForCurrentSelection().EndpointScopeConfirmed, "An unselected endpoint acquired confirmation.");
        EndpointBox.SelectedItem = endpoint;
        var normal = OptionsForCurrentSelection();
        var trial = OptionsForCurrentSelection(trial: true);
        Need(normal.Mode == SourceMode.EndpointLoopback && normal.EndpointScopeConfirmed && normal.RenderDeviceId == endpoint.Id &&
            trial.EndpointScopeConfirmed && !normal.MicrophoneEnabled && normal.MicrophoneDeviceId is null,
            "Explicit endpoint selection did not construct the requested scope with the microphone disabled.");
        ModeBox.SelectedIndex = 0;
        Need(OptionsForCurrentSelection() is { Mode: SourceMode.ProcessTree, EndpointScopeConfirmed: false, ProcessSource: null },
            "An absent process source silently changed to endpoint capture.");
        ModeBox.SelectedIndex = 1;

        Width = MinWidth; Height = MinHeight;
        foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
        {
            ChangeLanguage(language, persist: false);
            NavigateTo(0);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            Need(TitleBox.Text == titleDraft && EndpointScopeNotice.IsVisible && !string.IsNullOrWhiteSpace(EndpointScopeNotice.Text),
                "Changing language lost the title draft or hid the endpoint scope notice.");
            Need(ApplicationVersionText.Text == "M5E Pro" && ApplicationVersionText.IsVisible &&
                ApplicationVersionText.FontSize < 21, "The M3E version label is absent or not secondary text.");
            Need(LanguageButton.Parent is StackPanel footer && ReferenceEquals(footer.Children[^1], LanguageButton),
                "The language button is not the last footer item.");
            Need(!M3dVisualChildren<TextBlock>(this).Any(text => text.Text is
                "本地保存 · 手动上传" or "本地保存。手动上传" or "Saved locally · Uploaded manually" or "本地保存 · 手動上傳"),
                "The removed local-save footer remains.");
            string subtitleLabel = language switch { "en" => "Transcription & subtitles", "zh-Hant" => "轉寫與字幕", _ => "转写与字幕" };
            Need(Equals(((TabItem)MeetingTabs.Items[3]).Header, subtitleLabel), "The transcription/subtitle tab did not localize.");
            SaveLayoutImage(evidence, "m3e-recording-" + language + ".png", 1, images);

            NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 0;
            view.HardwareConfigurationRow.BringIntoView();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            Point hardware = view.HardwareBox.TranslatePoint(new Point(), view.HardwareConfigurationRow);
            Point threads = view.ThreadsBox.TranslatePoint(new Point(), view.HardwareConfigurationRow);
            Point gpu = view.GpuDeviceBox.TranslatePoint(new Point(), view.HardwareConfigurationRow);
            Need(Math.Abs(hardware.Y - threads.Y) <= 1 && Math.Abs(threads.Y - gpu.Y) <= 1 &&
                hardware.X < threads.X && threads.X < gpu.X && gpu.X + view.GpuDeviceBox.ActualWidth <= view.HardwareConfigurationRow.ActualWidth + 1,
                "Hardware, CPU threads and GPU index are not aligned in one unclipped row.");
            Need(view.RunWhisperButton.Parent is WrapPanel actions && ReferenceEquals(actions.Children[0], view.RunWhisperButton) &&
                ReferenceEquals(actions.Children[1], view.SaveConfigurationButton) &&
                ReferenceEquals(view.RunWhisperButton.Style, FindResource("PrimaryButton")),
                "Start recognition is not first or does not use the recording action's primary style.");
            SaveLayoutImage(evidence, "m3e-recognition-" + language + ".png", 1, images);
            view.RunWhisperButton.BringIntoView();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            SaveLayoutImage(evidence, "m3e-recognition-actions-" + language + ".png", 1, images);
        }
        ResetConsentAfterSession();
        Need(TitleBox.Text != titleDraft && EndpointBox.SelectedIndex == -1 && SourceBox.SelectedIndex == -1 && !microphoneRequested,
            "Completed-session cleanup did not reset the title and source/microphone selections.");
        ChangeLanguage("zh-CN", persist: false); ModeBox.SelectedIndex = 0; NavigateTo(0); UpdateControls();
        await savedMixWork; await replayLoadWork; await transcriptWork;
        Need(host is null && !microphoneEnabled && player.Source is null && state == SessionState.Idle,
            "The UI smoke unexpectedly opened capture or media.");
        return new
        {
            Status = "PASS", TitleDraftPreservedAcrossSettingsLibraryLanguageAndEmptyText = true,
            EndpointConfirmationOnExplicitSelectionAndStartRequest = true, MissingProcessNeverFallsBack = true,
            CompletedSessionStillResetsConsentAndTitle = true, VersionLabel = ApplicationVersionText.Text, LanguageButtonAtBottom = true,
            HardwareFieldsSingleRowAtMinimumWindow = true, PrimaryRecognitionButtonFirst = true,
            Languages = new[] { "zh-CN", "zh-Hant", "en" }, Images = images,
            CaptureStarted = false, MicrophoneCollected = false, SourceMode = "SYNTHETIC ENDPOINT / REQUEST CONSTRUCTION ONLY"
        };
        static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    }
}
