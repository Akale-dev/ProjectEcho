using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    internal async Task<object> M3hMeetingAsrSmokeAsync(string evidence, CancellationToken token)
    {
        Need(host is null && !microphoneEnabled && !transcriptBusy, "No capture allowed in settings smoke.");
        dataRoot = Path.Combine(evidence, "data"); Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        DirectoryInfo? workspace = new(projectRoot);
        while (workspace is not null && !(File.Exists(Path.Combine(workspace.FullName, "AGENTS.md")) && File.Exists(Path.Combine(workspace.FullName, "global.json"))))
            workspace = workspace.Parent;
        if (workspace is null) throw new InvalidOperationException("Run this ASR settings smoke inside the ProjectEcho developer workspace.");
        string executable = Path.Combine(workspace.FullName, "tests", "ProjectEcho.Tests", "bin", "Release", "net10.0-windows", "ProjectEcho.Tests.exe");
        Need(File.Exists(executable), "Build the existing synthetic CLI fixture before running the ASR settings smoke.");
        string model = Path.Combine(dataRoot, "synthetic-model.bin");
        // The existing valid test executable exits with code 7 for this model marker.
        // Never launch an invalid PE file: Windows may display a blocking loader dialog.
        File.WriteAllText(model, "synthetic-fail");
        Stage("fixture_ready");
        var initial = new WhisperConfiguration(executable, model, ModelName: "synthetic-model", ModelLicenseAcknowledged: true);
        MeetingWhisperSettingsStore.SaveDefaults(WhisperConfigurationPath, initial, AppContext.BaseDirectory);
        LoadWhisperConfiguration();
        string first = Path.Combine(RecordingsRoot, "a"), second = Path.Combine(RecordingsRoot, "b"), older = Path.Combine(RecordingsRoot, "older-unvisited");
        foreach (string directory in new[] { first, second, older }) WriteCloseFixture(directory);
        var originals = Directory.EnumerateFiles(RecordingsRoot, "*", SearchOption.AllDirectories).ToDictionary(path => path, CloseFixtureHash);
        var creation = PrepareNewMeetingWhisperDefaults(); creation(first); creation(second);
        var view = transcriptionView!;
        Need(view.HardwareBox.Items.Cast<ComboBoxItem>().Select(item => item.Tag?.ToString()).SequenceEqual(new[] { "Cpu", "Auto", "Vulkan" }), "CUDA remains selectable.");
        SelectMeeting(first, loadSavedMix: false); NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 0;
        Need(view.MeetingText.Visibility == Visibility.Collapsed, "Duplicated selected meeting heading is visible.");
        view.InitialPromptBox.Text = "A private prompt";
        view.ThreadsBox.Text = "6"; SelectHardware(LocalTranscriptionHardware.Auto);
        view.SaveConfigurationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var defaults = PortableWhisperConfiguration.Load(WhisperConfigurationPath, AppContext.BaseDirectory)!;
        Need(defaults.Threads == 6 && defaults.Hardware == LocalTranscriptionHardware.Auto && defaults.InitialPrompt is null, "Default save included prompt or missed options.");
        SelectMeeting(second, loadSavedMix: false);
        Need(view.InitialPromptBox.Text.Length == 0 && CurrentWhisperConfiguration().Threads == 2, "A prompt/default leaked into existing B.");
        view.InitialPromptBox.Text = "B private prompt";
        SelectMeeting(older, loadSavedMix: false);
        Need(CurrentWhisperConfiguration().Threads == 2 && view.InitialPromptBox.Text.Length == 0, "Old unvisited meeting inherited new defaults.");
        SelectMeeting(first, loadSavedMix: false);
        Need(view.InitialPromptBox.Text == "A private prompt" && CurrentWhisperConfiguration().Threads == 2, "A draft missing or default save changed A last-used settings.");
        currentMix = await Task.Run(() => M2ExportService.RenderMeeting(first, encoder: null, token), token);
        renderedMeetings[first] = currentMix;
        view.ThreadsBox.Text = "5"; SelectHardware(LocalTranscriptionHardware.Cpu);
        Stage("before_controlled_cli_failure");
        await RunBoundedTranscriptAsync(RunWhisperAsync);
        string failedAttempt = Directory.EnumerateFiles(Path.Combine(first, "transcripts", "asr"), "attempt.json", SearchOption.AllDirectories).Single();
        using (var attempt = JsonDocument.Parse(File.ReadAllText(failedAttempt)))
            Need(attempt.RootElement.GetProperty("status").GetString() == "Failed" && attempt.RootElement.GetProperty("exitCode").GetInt32() == 7,
                "The valid synthetic CLI did not produce its expected controlled failure and audit record.");
        Stage("controlled_cli_failure_saved");
        Need(MeetingWhisperSettingsStore.LoadExisting(first, AppContext.BaseDirectory)?.Configuration?.Threads == 5, "Failed recognition lost last-used configuration.");
        view.ThreadsBox.Text = "7"; view.InitialPromptBox.Text = "A cancelled prompt";
        await RunBoundedTranscriptAsync(async () => { transcriptCancellation!.Cancel(); await RunWhisperAsync(); });
        Stage("cancelled_preparation_saved");
        SelectMeeting(second, loadSavedMix: false); SelectMeeting(first, loadSavedMix: false);
        Need(CurrentWhisperConfiguration().Threads == 7 && view.InitialPromptBox.Text == "A cancelled prompt", "Cancelled preparation did not retain selected options.");
        view.InitialPromptBox.Text = "A later unsent draft";
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task background = RunTranscriptWorkAsync(async () => { await gate.Task; throw new InvalidDataException("A late synthetic failure"); }, "asr", first);
        SelectMeeting(second, loadSavedMix: false); view.StatusText.Text = "B status remains current";
        gate.SetResult(); await background;
        Stage("late_failure_isolated");
        Need(view.StatusText.Text == "B status remains current" && view.InitialPromptBox.Text == "B private prompt", "A late error contaminated selected B.");
        string next = Path.Combine(RecordingsRoot, "new"); WriteCloseFixture(next); PrepareNewMeetingWhisperDefaults()(next);
        SelectMeeting(next, loadSavedMix: false);
        Need(CurrentWhisperConfiguration().Threads == 6 && view.InitialPromptBox.Text.Length == 0, "New meeting did not inherit defaults without prompt.");

        string savedDefaults = File.ReadAllText(WhisperConfigurationPath);
        File.WriteAllText(WhisperConfigurationPath, "invalid global JSON");
        SelectMeeting(first, loadSavedMix: false);
        Need(meetingWhisperSettingsReady && CurrentWhisperConfiguration().Threads == 7 && view.InitialPromptBox.Text == "A later unsent draft",
            "Malformed global defaults blocked independent existing meeting settings.");
        File.WriteAllText(WhisperConfigurationPath, savedDefaults);
        string unsupported = Path.Combine(RecordingsRoot, "legacy-cuda"); WriteCloseFixture(unsupported);
        MeetingWhisperSettingsStore.SaveAttemptConfiguration(unsupported, initial with { Hardware = LocalTranscriptionHardware.Cuda }, AppContext.BaseDirectory);
        SelectMeeting(unsupported, loadSavedMix: false);
        Need(view.HardwareBox.SelectedIndex == -1 && HasValidationIssue(view.HardwareBox) && !TryValidateWhisperConfiguration(false, out _),
            "Legacy CUDA silently became CPU or remained runnable.");
        SelectMeeting(first, loadSavedMix: false);
        var reopened = new MainWindow(enumerateSourcesOnLoad: false) { dataRoot = dataRoot };
        try
        {
            reopened.Show(); reopened.SelectMeeting(first, loadSavedMix: false);
            Need(reopened.CurrentWhisperConfiguration().Threads == 7 && reopened.transcriptionView!.InitialPromptBox.Text == "A later unsent draft", "Reopening lost A settings or unsent draft.");
            reopened.SelectMeeting(second, loadSavedMix: false);
            Need(reopened.CurrentWhisperConfiguration().Threads == 2 && reopened.transcriptionView!.InitialPromptBox.Text == "B private prompt", "Reopening mixed A/B settings.");
        }
        finally { reopened.Close(); }
        Stage("reopened_meetings_checked");
        CheckInitialCorruptDefaultRepair();
        Stage("corrupt_defaults_repaired");
        var images = new List<string>();
        Width = MinWidth; Height = MinHeight;
        foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
        {
            ChangeLanguage(language, persist: false);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            Need(view.InitialPromptBox.Text == "A later unsent draft", "Language switch changed meeting prompt.");
            view.RunWhisperButton.BringIntoView(); await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            SaveLayoutImage(evidence, "m3h-asr-" + language + ".png", 1, images);
        }
        Need(originals.All(pair => CloseFixtureHash(pair.Key) == pair.Value), "ASR settings changed recording originals.");
        Need(host is null && !microphoneEnabled && player.Source is null, "ASR settings smoke opened capture or playback.");
        Stage("completed");
        return new
        {
            Status = "PASS", SourceMode = "SYNTHETIC FILES / VALID TEST CLI EXIT CODE 7 / NO DEVICE / NO REAL ASR",
            MeetingPromptsIsolatedAndReopened = true, DefaultsOnlyForNewMeetings = true,
            DefaultsExcludePrompt = true, OldUnvisitedMeetingFrozen = true, FailedAndCancelledChoicesRemembered = true,
            LateFailureDoesNotPolluteOtherMeeting = true, BrokenGlobalDefaultsDoNotBlockSavedMeeting = true,
            FirstUpgradeCorruptDefaultsRepairableWithoutChangingOldMeetings = true,
            NoCudaOption = true, DuplicateMeetingHeadingHidden = true, RecordingOriginalsUnchanged = true, Images = images
        };
        async Task RunBoundedTranscriptAsync(Func<Task> operation)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            using var registration = limit.Token.Register(() =>
            {
                if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(new Action(() => transcriptCancellation?.Cancel()));
            });
            Task work = RunTranscriptWorkAsync(operation, "asr", first);
            try { await work.WaitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                transcriptCancellation?.Cancel();
                // Await controlled child cleanup, with a separate bounded grace period.
                await work.WaitAsync(TimeSpan.FromSeconds(15));
                throw;
            }
        }
        void Stage(string stage) => File.AppendAllText(Path.Combine(evidence, "asr-stages.jsonl"),
            JsonSerializer.Serialize(new { Stage = stage, TimestampUtc = DateTimeOffset.UtcNow }) + Environment.NewLine);
        void CheckInitialCorruptDefaultRepair()
        {
            var repairWindow = new MainWindow(enumerateSourcesOnLoad: false)
            { dataRoot = Path.Combine(evidence, "initial-corrupt-defaults") };
            Directory.CreateDirectory(Path.GetDirectoryName(repairWindow.WhisperConfigurationPath)!);
            File.WriteAllText(repairWindow.WhisperConfigurationPath, "invalid first-upgrade defaults");
            string unvisited = Path.Combine(repairWindow.RecordingsRoot, "old-unvisited"); WriteCloseFixture(unvisited);
            try
            {
                repairWindow.Show(); repairWindow.LoadWhisperConfiguration();
                Need(!File.Exists(Path.Combine(repairWindow.dataRoot, "settings", MeetingWhisperSettingsStore.LegacyDefaultsFileName)),
                    "Loading invalid defaults silently wrote a migration baseline.");
                repairWindow.ApplyMeetingWhisperConfiguration(initial with { Threads = 11 }, null);
                repairWindow.SaveWhisperConfiguration();
                Need(PortableWhisperConfiguration.Load(repairWindow.WhisperConfigurationPath, AppContext.BaseDirectory)?.Threads == 11 &&
                    repairWindow.transcriptionView!.StatusText.Text.Contains("固定为空配置", StringComparison.Ordinal),
                    "User-selected valid configuration could not repair first-upgrade corrupt defaults, or missing-baseline uncertainty was hidden.");
                repairWindow.SelectMeeting(unvisited, loadSavedMix: false);
                Need(repairWindow.meetingWhisperSettingsReady && string.IsNullOrEmpty(repairWindow.whisperExecutable) &&
                    string.IsNullOrEmpty(repairWindow.whisperModel), "Repaired defaults silently populated an unvisited older meeting.");
                string afterRepair = Path.Combine(repairWindow.RecordingsRoot, "new-after-repair"); WriteCloseFixture(afterRepair);
                repairWindow.PrepareNewMeetingWhisperDefaults()(afterRepair);
                repairWindow.SelectMeeting(afterRepair, loadSavedMix: false);
                Need(repairWindow.CurrentWhisperConfiguration().Threads == 11, "New meeting failed to inherit repaired valid defaults.");
            }
            finally { repairWindow.Close(); }
        }
        void SelectHardware(LocalTranscriptionHardware hardware) => view.HardwareBox.SelectedItem = view.HardwareBox.Items.Cast<ComboBoxItem>().Single(item => item.Tag?.ToString() == hardware.ToString());
        static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    }
}
