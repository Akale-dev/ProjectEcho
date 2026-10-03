using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ProjectEcho.Core;

namespace ProjectEcho.App;

public partial class MainWindow
{
    internal async Task<object> M3dNavigationSmokeAsync(string evidence, CancellationToken token)
    {
        if (host is not null || microphoneRequested || SourceBox.SelectedIndex != -1)
            throw new InvalidDataException("M3D navigation test requires the no-capture startup path.");
        dataRoot = Path.Combine(evidence, "synthetic-data");
        Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        string first = Path.Combine(RecordingsRoot, "first"), second = Path.Combine(RecordingsRoot, "second");
        foreach (var (directory, title) in new[] { (first, "Synthetic earnings call A"), (second, "Synthetic earnings call B") })
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "meeting.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = "m1-recording-1", meetingId = Path.GetFileName(directory), title, project = "未分类",
                startedAtUtc = "2026-10-01T00:00:00Z", duration100ns = 600_000_000, storageComplete = true, isSynthetic = true
            }));
        }
        var originals = new[] { first, second }.ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(path, "meeting.json")));
        var images = new List<string>();
        RefreshRecovery_Click(this, new RoutedEventArgs());
        SelectMeeting(first, loadSavedMix: false);
        FiledTitleBox.Text = "Unsaved title A"; FiledProjectBox.Text = "Unsaved project A";
        NavigateTo(4);
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
        var firstEntry = RecoveryBox.Items.Cast<RecordingDirectory>().Single(entry => entry.Path == first);
        var firstItem = (ListBoxItem)RecoveryBox.ItemContainerGenerator.ContainerFromItem(firstEntry);
        firstItem.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = firstItem });
        if (MainTabs.SelectedIndex != 1 || selectedMeetingDirectory != first || FiledTitleBox.Text != "Unsaved title A")
            throw new InvalidDataException("Clicking the already selected meeting did not return to library or lost edits.");
        NavigateTo(4);
        RecoveryBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, Key.Enter)
        { RoutedEvent = UIElement.PreviewKeyDownEvent, Source = RecoveryBox });
        if (MainTabs.SelectedIndex != 1 || FiledTitleBox.Text != "Unsaved title A")
            throw new InvalidDataException("Keyboard activation of the selected meeting lost navigation or edits.");

        var secondEntry = RecoveryBox.Items.Cast<RecordingDirectory>().Single(entry => entry.Path == second);
        RecoveryBox.SelectedItem = secondEntry;
        if (selectedMeetingDirectory != second) throw new InvalidDataException("Meeting selection identity changed after WPF subscribed to row notifications.");
        FiledTitleBox.Text = "Unsaved title B";
        RecoveryBox.SelectedItem = firstEntry;
        if (FiledTitleBox.Text != "Unsaved title A" || FiledProjectBox.Text != "Unsaved project A")
            throw new InvalidDataException($"Switching meetings discarded unsaved filing edits: title='{FiledTitleBox.Text}', project='{FiledProjectBox.Text}', selected='{Path.GetFileName(selectedMeetingDirectory)}'.");
        RecordNavigationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (selectedMeetingDirectory is not null || MainTabs.SelectedIndex != 0)
            throw new InvalidDataException("New recording navigation did not clear the historical selection.");
        RecoveryBox.SelectedItem = firstEntry;
        if (FiledTitleBox.Text != "Unsaved title A")
            throw new InvalidDataException("Returning from new recording lost the meeting draft.");

        transcriptBusy = true;
        try
        {
            SetRecognitionMeeting(first); UpdateControls();
            if (!RecoveryBox.IsEnabled || !firstEntry.IsRecognizing || secondEntry.IsRecognizing)
                throw new InvalidDataException("Recognition did not identify precisely its captured meeting or disabled navigation.");
            RefreshRecovery_Click(this, new RoutedEventArgs());
            firstEntry = RecoveryBox.Items.Cast<RecordingDirectory>().Single(entry => entry.Path == first);
            secondEntry = RecoveryBox.Items.Cast<RecordingDirectory>().Single(entry => entry.Path == second);
            if (!firstEntry.IsRecognizing || secondEntry.IsRecognizing)
                throw new InvalidDataException("Refreshing the library detached the active recognition marker.");
            RecoveryBox.SelectedItem = secondEntry;
            NavigateTo(4);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            firstItem = (ListBoxItem)RecoveryBox.ItemContainerGenerator.ContainerFromItem(firstEntry);
            var recognition = M3dVisualChildren<Button>(firstItem).Single(button => button.Name == "RecognitionMeetingButton");
            var opacities = new List<double>();
            for (int sample = 0; sample < 4; sample++) { await Task.Delay(170, token); opacities.Add(recognition.Opacity); }
            if (!recognition.IsVisible || opacities.Max() - opacities.Min() < 0.05)
                throw new InvalidDataException("Recognition badge did not visibly pulse.");
            recognition.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (selectedMeetingDirectory != first || MainTabs.SelectedIndex != 1 || MeetingTabs.SelectedIndex != 3 ||
                transcriptionView!.WorkspaceTabs.SelectedIndex != 0 || FiledTitleBox.Text != "Unsaved title A")
                throw new InvalidDataException("Recognition badge opened the wrong meeting/page or discarded the draft.");
            Width = MinWidth; Height = MinHeight;
            foreach (string language in new[] { "zh-CN", "zh-Hant", "en" })
            {
                ChangeLanguage(language, persist: false);
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
                firstEntry = RecoveryBox.Items.Cast<RecordingDirectory>().Single(entry => entry.Path == first);
                firstItem = (ListBoxItem)RecoveryBox.ItemContainerGenerator.ContainerFromItem(firstEntry);
                recognition = M3dVisualChildren<Button>(firstItem).Single(button => button.Name == "RecognitionMeetingButton");
                string expected = language switch { "en" => "Transcribing…", "zh-Hant" => "識別中…", _ => "识别中…" };
                if (!Equals(recognition.Content, expected) || LanguageButton.BorderThickness.Left != 1 ||
                    LanguageButton.HorizontalAlignment != HorizontalAlignment.Left || LanguageButton.ActualWidth >= 120 ||
                    !Equals(LanguageButton.Content, "EN/中"))
                    throw new InvalidDataException("Recognition language or compact bordered language button differs.");
                SaveLayoutImage(evidence, "m3d-recognition-" + language + ".png", 1, images);
            }
            // State-only normal-recording navigation; no Host, audio clients or media are opened.
            var originalHost = host;
            string originalTitle = TitleBox.Text;
            foreach (var recordingState in new[] { SessionState.Recording, SessionState.Paused, SessionState.Stopping })
            {
                state = recordingState; isTrial = false;
                elapsedUs = 12_000_000; fixedMicrophoneId = "synthetic-fixed-mic-no-client";
                pendingControl = recordingState == SessionState.Recording ? "pause" : null;
                pendingControlRequestId = pendingControl is null ? null : "synthetic-no-ipc";
                UpdateControls();
                RecoveryBox.SelectedItem = secondEntry;
                NavigateTo(4);
                recognition.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (selectedMeetingDirectory != first || MainTabs.SelectedIndex != 1 || MeetingTabs.SelectedIndex != 3 ||
                    transcriptionView!.WorkspaceTabs.SelectedIndex != 0 || !RecoveryBox.IsEnabled || state != recordingState ||
                    !ReferenceEquals(host, originalHost) || elapsedUs != 12_000_000 || TitleBox.Text != originalTitle ||
                    fixedMicrophoneId != "synthetic-fixed-mic-no-client" || pendingControl != (recordingState == SessionState.Recording ? "pause" : null) ||
                    PlaybackButton.IsEnabled || PausePlaybackButton.IsEnabled ||
                    ReplayPositionSlider.IsEnabled || transcriptionView.PlayListeningButton.IsEnabled || transcriptionView.SeekButton.IsEnabled ||
                    FileMeetingButton.IsEnabled || RetryMixButton.IsEnabled || InspectRecoveryButton.IsEnabled || DataRootButton.IsEnabled)
                    throw new InvalidDataException("Recognition navigation mutated the capture state/identity or unlocked media/recording-time writes.");
            }
            state = SessionState.Idle; fixedMicrophoneId = null; pendingControl = null; pendingControlRequestId = null; elapsedUs = 0;
            UpdateControls();
            SetRecognitionMeeting(null);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
            if (firstEntry.IsRecognizing || recognition.IsVisible)
                throw new InvalidDataException("Finished recognition left a stale sidebar badge.");
            NavigateTo(4);
            recognition.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (MainTabs.SelectedIndex != 2) throw new InvalidDataException("A stale recognition badge still navigated.");
        }
        finally { SetRecognitionMeeting(null); transcriptBusy = false; UpdateControls(); }

        ChangeLanguage("zh-CN", persist: false);
        state = SessionState.Recording; UpdateControls();
        NavigateTo(4);
        var recordOpacities = new List<double>();
        for (int sample = 0; sample < 4; sample++) { await Task.Delay(170, token); recordOpacities.Add(RecordingIndicator.Opacity); }
        if (!RecordingIndicator.IsVisible || recordOpacities.Max() - recordOpacities.Min() < 0.05 || PausedRecordingIndicator.IsVisible)
            throw new InvalidDataException("Recording status did not pulse while another page was open.");
        SaveLayoutImage(evidence, "m3d-recording-status.png", 1, images);
        state = SessionState.Paused; UpdateControls();
        if (RecordingIndicator.IsVisible || !PausedRecordingIndicator.IsVisible || RecordingIndicator.HasAnimatedProperties)
            throw new InvalidDataException("Paused state retained the recording pulse or lacked a pause marker.");
        state = SessionState.Stopping; UpdateControls();
        if (!RecordingIndicator.IsVisible || RecordingIndicator.HasAnimatedProperties || PausedRecordingIndicator.IsVisible)
            throw new InvalidDataException("Saving state did not retain a distinct non-recording indicator.");
        state = SessionState.Saved; UpdateControls();
        if (RecordingIndicator.IsVisible || PausedRecordingIndicator.IsVisible || RecordingIndicator.HasAnimatedProperties)
            throw new InvalidDataException("Saved state retained a recording indicator or animation.");
        NavigateTo(0);
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
        if (M3dVisualChildren<TextBlock>(MainTabs).Any(text => text.IsVisible && text.Text == "专注当下，完整记录"))
            throw new InvalidDataException("Recording page retained the removed hero title.");
        SaveLayoutImage(evidence, "m3d-recording-page.png", 1, images);
        NavigateTo(2);
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
        if (M3dVisualChildren<TextBlock>(MainTabs).Any(text => text.IsVisible &&
            (text.Text == "一份完整、可带走的声音" || text.Text.StartsWith("从独立原轨", StringComparison.Ordinal) ||
             text.Text.StartsWith("左侧按日期选择历史会议", StringComparison.Ordinal))))
            throw new InvalidDataException("Library/export retained removed explanatory headings.");
        SaveLayoutImage(evidence, "m3d-export-page.png", 1, images);
        await savedMixWork; await replayLoadWork; await transcriptWork;
        foreach (var pair in originals)
            if (!pair.Value.SequenceEqual(File.ReadAllBytes(Path.Combine(pair.Key, "meeting.json"))))
                throw new InvalidDataException("Navigation changed authoritative meeting metadata.");
        return new
        {
            Status = "PASS", SameMeetingMouseAndKeyboardNavigation = true, FilingDraftsAcrossMeetingsAndNewRecording = true,
            RecognitionBadgeExactMeeting = true, RecognitionBadgePulseAndLifecycle = true, BusyRecognitionDeepLink = true,
            RecognitionNavigationDuringRecordingPausedAndSavingKeepsCaptureAndMediaGuards = true,
            RecordingPauseSavingSavedIndicator = true, CompactLanguageButton = true, RemovedHeadings = true,
            Languages = new[] { "zh-CN", "zh-Hant", "en" }, OriginalsUnchanged = true,
            CaptureStarted = false, MicrophoneCollected = false, ComputerUse = false, Images = images
        };
    }

    private static IEnumerable<T> M3dVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in M3dVisualChildren<T>(child)) yield return descendant;
        }
    }
}
