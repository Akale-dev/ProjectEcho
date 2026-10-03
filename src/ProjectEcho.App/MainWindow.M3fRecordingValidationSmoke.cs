using System.IO;
using System.Windows;
using System.Windows.Threading;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    internal async Task<object> M3fRecordingValidationSmokeAsync(string evidence, CancellationToken token)
    {
        dataRoot = Path.Combine(evidence, "data"); Directory.CreateDirectory(dataRoot);
        void Stage(string value) => File.AppendAllText(Path.Combine(evidence, "stages.txt"), DateTimeOffset.UtcNow.ToString("O") + " " + value + "\n");
        Stage("start; nativePreview=" + levelPreviewNativeEnabled);
        Stage("runtime=" + Environment.Version + "; core=" + typeof(object).Assembly.Location + "; cwd=" + Directory.GetCurrentDirectory());
        foreach (string name in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_MODIFIABLE_ASSEMBLIES", "DOTNET_STARTUP_HOOKS", "DOTNET_LAUNCH_PROFILE" })
            Stage(name + "=" + Environment.GetEnvironmentVariable(name));
        var originalRenderOperation = renderOperation;
        renderOperation = (directory, cancellation) =>
        {
            Stage("render-enter");
            var result = originalRenderOperation(directory, cancellation);
            Stage("render-return");
            return result;
        };
        string first = Path.Combine(RecordingsRoot, "meeting-a"), second = Path.Combine(RecordingsRoot, "meeting-b");
        foreach (string directory in new[] { first, second })
        {
            WriteCloseFixture(directory);
            Stage("fixture-written");
            QueueMix(directory); await backgroundWork.WaitAsync(token);
            Stage("mix-complete");
            Need(renderedMeetings.TryGetValue(directory, out var savedMix) && savedMix.CompressedPath is null &&
                !Directory.EnumerateFiles(directory, "*.m4a", SearchOption.AllDirectories).Any(), "Automatic mix still created an unrequested M4A.");
            string srt = Path.Combine(evidence, Path.GetFileName(directory) + ".srt");
            File.WriteAllText(srt, "1\n00:00:00,000 --> 00:00:01,000\nSynthetic transcript only.\n");
            TranscriptService.Import(new(directory, Path.GetFileName(directory), 2000, srt, "Synthetic", IsSynthetic: true));
            Stage("transcript-imported");
        }
        int calls = 0;
        recordingPreflight = _ => { calls++; throw new InvalidOperationException("Local missing fields must prevent API preflight."); };
        SelectMeeting(first, loadSavedMix: false); NavigateTo(0);
        string draft = "M3F retain invalid-start draft"; TitleBox.Text = draft;
        var initialMix = currentMix;
        await StartCaptureAsync(trial: false);
        Need(calls == 0 && host is null && state == SessionState.Idle && selectedMeetingDirectory == first &&
            currentMix == initialMix && TitleBox.Text == draft && HasValidationIssue(SourceBox), "Missing source changed capture ownership or existing meeting.");
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
        Need(RecordingValidationText.IsVisible, "Missing source has no visible inline explanation.");
        ModeBox.SelectedIndex = 1;
        await StartCaptureAsync(trial: false);
        Need(HasValidationIssue(EndpointBox) && !HasValidationIssue(SourceBox), "Endpoint validation highlighted the wrong field.");
        EndpointBox.ItemsSource = new[] { new AudioDevice("synthetic-endpoint", "Synthetic endpoint") };
        EndpointBox.SelectedIndex = 0;
        Need(!HasValidationIssue(EndpointBox), "Correcting endpoint did not clear red field.");
        SetMicrophoneRequested(true);
        await StartCaptureAsync(trial: false);
        Need(HasValidationIssue(MicBox) && calls == 0 && host is null, "Missing mic did not block before API/capture.");
        SetMicrophoneRequested(false);
        Need(!HasValidationIssue(MicBox), "MicOff did not clear the missing mic field.");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recordingPreflight = _ =>
        {
            calls++; entered.SetResult(); release.Task.Wait(token);
            return new(false, ["所选输出端点不可用：synthetic device removed"], [], 96000, 1_000_000_000, "synthetic");
        };
        var pending = StartCaptureAsync(trial: false);
        await entered.Task.WaitAsync(token);
        await StartCaptureAsync(trial: false);
        Need(preflighting && state == SessionState.Idle && !StartButton.IsEnabled && calls == 1, "Repeated start entered capture while preflight was pending.");
        release.SetResult(); await pending.WaitAsync(token);
        Need(!preflighting && state == SessionState.Idle && HasValidationIssue(EndpointBox) && host is null && TitleBox.Text == draft,
            "Static preflight failure did not restore UI or preserve draft.");
        var images = new List<string>();
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render, token);
        SaveLayoutImage(evidence, "recording-invalid-endpoint.png", 1, images);
        int played = 0;
        foreach (string directory in new[] { first, second })
        {
            SelectMeeting(directory, loadSavedMix: false); NavigateTo(5);
            await replayLoadWork.WaitAsync(token); UpdateControls();
            Need(PlaybackButton.IsEnabled && ReplaySegmentsBox.IsEnabled && ReplaySegmentsBox.Items.Count == 1,
                "A previously transcribed meeting remained grey after rejected recording.");
            player.Volume = 0; player.IsMuted = true;
            Playback_Click(this, new RoutedEventArgs());
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.MediaOpened += (_, _) => opened.TrySetResult();
            player.MediaFailed += (_, args) => opened.TrySetException(args.ErrorException);
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(8), token);
            Need(playbackOpened && player.IsMuted && player.Volume == 0, "Historical replay failed to open safely.");
            played++; ResetTranscriptPlayback();
        }
        state = SessionState.StoppedWithError; SelectMeeting(first, loadSavedMix: false); UpdateControls();
        Need(PlaybackButton.IsEnabled && ReplaySegmentsBox.IsEnabled, "Confirmed stopped error still blocks history.");
        foreach (var unsafeState in new[] { SessionState.Recording, SessionState.Paused, SessionState.Stopping, SessionState.Faulted })
        {
            state = unsafeState; UpdateControls();
            Need(!PlaybackButton.IsEnabled && !ReplaySegmentsBox.IsEnabled, "Unsafe capture state allowed historical replay.");
        }
        state = SessionState.Idle; UpdateControls();
        Need(Directory.GetDirectories(RecordingsRoot).Length == 2 && host is null && !microphoneEnabled, "Invalid-start tests created a recording or enabled collection.");
        return new { Status = "PASS", SourceMode = "SYNTHETIC FILES / INJECTED PREFLIGHT", MissingFieldsStopBeforePreflight = true,
            AutomaticMixIsWaveOnly = true,
            FailedPreflightKeepsHistoryAndDraft = true, DuplicateStartBlocked = true, ExistingMeetingsReplayedMuted = played,
            UnsafeCaptureStillLocksPlayback = true, CaptureStarted = false, MicrophoneCollected = false, Images = images };
        static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
