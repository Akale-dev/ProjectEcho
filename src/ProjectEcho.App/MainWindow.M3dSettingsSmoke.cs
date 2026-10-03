using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

public partial class MainWindow
{
    internal async Task<object> M3dSettingsSmokeAsync(string evidence, CancellationToken token)
    {
        if (host is not null || microphoneEnabled) throw new InvalidOperationException("No capture permitted in settings smoke.");
        dataRoot = Path.Combine(evidence, "d"); Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        var view = transcriptionView!;
        string exe = Path.Combine(dataRoot, "synthetic-never-executed.exe"), model = Path.Combine(dataRoot, "synthetic-model.bin");
        File.WriteAllText(exe, "Synthetic path validation fixture, never execute."); File.WriteAllText(model, "Synthetic model path fixture, never infer.");
        Directory.CreateDirectory(Path.GetDirectoryName(WhisperConfigurationPath)!);
        File.WriteAllText(WhisperConfigurationPath, JsonSerializer.Serialize(new
        { ExecutablePath = exe, ModelPath = model, Threads = 7, Language = "en", ExpectedModelSha256 = "removed-check", ModelLicenseAcknowledged = true }));
        LoadWhisperConfiguration();
        var legacy = CurrentWhisperConfiguration();
        Need(legacy.Threads == 7 && legacy.Hardware == LocalTranscriptionHardware.Cpu && legacy.InitialPrompt is null &&
            legacy.ExpectedModelSha256 is null && WhisperTranscriber.Inspect(legacy).Ready, "Legacy configuration migration failed.");
        Need(view.FindName("ModelHashBox") is null, "Model hash field is still present.");
        const string prompt = "Example Corp, TEST.A, 王先生, EBITDA, \"free cash flow\"\n--not-an-option";
        foreach (var hardware in new[] { LocalTranscriptionHardware.Cpu, LocalTranscriptionHardware.Auto, LocalTranscriptionHardware.Vulkan })
        {
            view.HardwareBox.SelectedItem = view.HardwareBox.Items.Cast<ComboBoxItem>().Single(item => item.Tag?.ToString() == hardware.ToString());
            view.ThreadsBox.Text = "6"; view.GpuDeviceBox.Text = "1"; view.InitialPromptBox.Text = prompt;
            view.SaveConfigurationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved = JsonSerializer.Deserialize<WhisperConfiguration>(File.ReadAllText(WhisperConfigurationPath))!;
            Need(saved.Hardware == hardware && saved.Threads == 6 && saved.GpuDeviceIndex == 1 && saved.InitialPrompt is null && saved.ExpectedModelSha256 is null,
                "Default hardware options changed values, retained a prompt or retained the obsolete hash requirement.");
            view.InitialPromptBox.Clear(); view.ThreadsBox.Text = "2";
            LoadWhisperConfiguration();
            Need(CurrentWhisperConfiguration() == saved, "Settings did not round trip through disk and controls.");
        }
        string previousSettings = File.ReadAllText(WhisperConfigurationPath);
        view.ThreadsBox.Text = "0"; SaveWhisperConfiguration();
        Need(File.ReadAllText(WhisperConfigurationPath) == previousSettings, "Invalid thread count overwrote valid settings.");
        view.ThreadsBox.Text = "6"; view.GpuDeviceBox.Text = "-1"; SaveWhisperConfiguration();
        Need(File.ReadAllText(WhisperConfigurationPath) == previousSettings, "Invalid device overwrote valid settings.");
        LoadWhisperConfiguration();
        string meeting = Path.Combine(RecordingsRoot, "settings-fixture"); WriteCloseFixture(meeting);
        PrepareNewMeetingWhisperDefaults()(meeting); SelectMeeting(meeting, loadSavedMix: false);
        view.InitialPromptBox.Text = prompt; FlushMeetingWhisperPrompt();
        transcriptBusy = true; UpdateTranscriptionControls();
        Need(!view.HardwareBox.IsEnabled && !view.InitialPromptBox.IsEnabled && !view.ThreadsBox.IsEnabled && !view.GpuDeviceBox.IsEnabled,
            "Busy recognition settings are editable.");
        transcriptBusy = false; UpdateTranscriptionControls();
        Need(view.HardwareBox.IsEnabled && view.InitialPromptBox.IsEnabled, "Settings did not unlock.");
        NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 0;
        Width = MinWidth; Height = MinHeight;
        var images = new List<string>();
        foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
        {
            ChangeLanguage(language, persist: false);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            Need(view.InitialPromptBox.Text == prompt, "UI language changed the user's prompt.");
            string expected = language == "en" ? "CPU (compatibility)" : language == "zh-Hant" ? "CPU（相容模式）" : "CPU（兼容模式）";
            Need(Equals(((ComboBoxItem)view.HardwareBox.Items[0]).Content, expected), "Hardware labels did not localize.");
            view.RunWhisperButton.BringIntoView(); await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            SaveLayoutImage(evidence, "settings-" + language + ".png", 1, images);
            view.WhisperExeHelpButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var help = asrSetupHelp ?? throw new InvalidDataException("Engine help button did not open its page.");
            Need(help.Owner == this && help.TopicTabs.SelectedIndex == 0, "Engine help opened the wrong page or owner.");
            help.Width = help.MinWidth; help.Height = help.MinHeight;
            await help.Dispatcher.InvokeAsync(help.UpdateLayout, DispatcherPriority.Render, token);
            string? openedLink = null; help.OpenLink = url => openedLink = url;
            var links = Descendants(help).OfType<Button>().Where(button => button.Tag is string url && url.StartsWith("https://", StringComparison.Ordinal)).ToArray();
            Need(links.Length >= 1, "Help has no official reference links.");
            foreach (var link in links)
            {
                string expectedUrl = (string)link.Tag;
                Need(new Uri(expectedUrl).Host is "github.com" or "huggingface.co", "Help uses an unreviewed download host.");
                link.Tag = "https://invalid.example/never-open";
                link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Need(openedLink == expectedUrl, "Help link did not use its fixed reviewed destination.");
                link.Tag = expectedUrl;
            }
            SaveHelp(help, "help-engine-" + language + ".png");
            view.WhisperModelHelpButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Need(ReferenceEquals(asrSetupHelp, help) && help.TopicTabs.SelectedIndex == 1, "Model help did not reuse the window at the correct page.");
            await help.Dispatcher.InvokeAsync(help.UpdateLayout, DispatcherPriority.Render, token);
            SaveHelp(help, "help-model-" + language + ".png");
            help.HelpScroller.ScrollToEnd(); await help.Dispatcher.InvokeAsync(help.UpdateLayout, DispatcherPriority.Render, token);
            Need(help.CloseButton.IsVisible, "Help close control disappeared while reading.");
            help.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Need(asrSetupHelp is null && CurrentWhisperConfiguration().InitialPrompt == prompt, "Closing help lost settings or window cleanup.");
        }
        Need(host is null && !microphoneEnabled && player.Source is null, "Settings started media or capture.");
        return new { Status = "PASS", LegacyCpuDefault = true, NoModelHashFieldOrComparison = true, HardwarePromptThreadsRoundTrip = true,
            InvalidSettingsPreserveFile = true, BusySettingsLocked = true, ThreeLanguagesPreservePrompt = true, Images = images,
            HelpButtonsOpenCorrectTopics = true, HelpReusesOwnedWindow = true, OfficialLinksInterceptedNoBrowser = true,
            SourceMode = "SYNTHETIC CONFIGURATION ONLY / NO EXECUTION / NO CAPTURE" };
        static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
        static IEnumerable<DependencyObject> Descendants(DependencyObject element)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            { var child = VisualTreeHelper.GetChild(element, i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
        }
        void SaveHelp(Window help, string name)
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(help.ActualWidth), (int)Math.Ceiling(help.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(help); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string file = Path.Combine(evidence, name); using var output = File.Create(file); encoder.Save(output); images.Add(file);
        }
    }
}
