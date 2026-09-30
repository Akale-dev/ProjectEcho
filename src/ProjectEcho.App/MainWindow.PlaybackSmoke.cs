using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using ProjectEcho.Core;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private static readonly object playbackStageGate = new();

    internal static void AppendPlaybackSmokeStage(string path, string stage, object? detail = null)
    {
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(new { AtUtc = DateTimeOffset.UtcNow, Stage = stage, Detail = detail });
        lock (playbackStageGate)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(line); stream.WriteByte(10); stream.Flush(true);
        }
    }

    /// <summary>Explicit muted WPF playback of newly generated synthetic files only. Never opens capture.</summary>
    internal async Task<(bool Passed, object Report)> PlaybackSmokeAsync(CancellationToken token, string progressPath)
    {
        AppendPlaybackSmokeStage(progressPath, "fixture_setup_started");
        DirectoryInfo? workspace = new(projectRoot);
        while (workspace is not null && !(File.Exists(Path.Combine(workspace.FullName, "AGENTS.md")) && File.Exists(Path.Combine(workspace.FullName, "global.json"))))
            workspace = workspace.Parent;
        if (workspace is null) throw new InvalidOperationException("Run the synthetic playback smoke inside the ProjectEcho development workspace.");
        string fixture = Path.Combine(workspace.FullName, ".local", "UiPlaybackTests", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        RejectReparseAncestors(fixture); Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "SYNTHETIC_PLAYBACK_ONLY"), "New synthetic PCM16 WAV, RF64 and M4A only. MediaPlayer volume zero. No historical audio, capture, microphone or system volume change.");
        var results = new List<PlaybackFormatCheck>(); var errors = new List<string>();
        const int rate = 48000, riffFrames = rate * 5 / 4, rf64Frames = rate * 7 / 4;
        string riff = Path.Combine(fixture, "synthetic-pcm16.wav"), rf64 = Path.Combine(fixture, "synthetic-small-rf64.wav"), m4a = Path.Combine(fixture, "synthetic-actual-aac.m4a");
        AudioEncodingResult encoded = await Task.Run(() =>
        {
            AppendPlaybackSmokeStage(progressPath, "before_riff_fixture", new { File = riff });
            WritePlaybackFixture(riff, riffFrames, rf64: false);
            AppendPlaybackSmokeStage(progressPath, "riff_fixture_written");
            AppendPlaybackSmokeStage(progressPath, "before_rf64_fixture", new { File = rf64, DirectPlayback = "NOT_RUN" });
            WritePlaybackFixture(rf64, rf64Frames, rf64: true);
            using (var check = new M2WaveReader(rf64))
                if (!check.IsRf64 || check.FrameCount != rf64Frames) throw new InvalidDataException("Synthetic RF64 fixture is invalid.");
            AppendPlaybackSmokeStage(progressPath, "rf64_fixture_validated_before_aac_encode");
            var result = new MediaFoundationM4aEncoder().Encode(rf64, m4a, token);
            AppendPlaybackSmokeStage(progressPath, "actual_aac_encoded_and_decoded", new { result.DurationSeconds, result.DecodedFrames });
            return result;
        }, token);
        var hashes = new[] { riff, rf64, m4a }.ToDictionary(path => path, PlaybackFixtureHash);
        double previousVolume = player.Volume; bool previousMute = player.IsMuted;
        try
        {
            AppendPlaybackSmokeStage(progressPath, "before_initial_player_reset");
            player.Stop(); player.Close(); player.Volume = 0; player.IsMuted = true; player.SpeedRatio = 1;
            AppendPlaybackSmokeStage(progressPath, "player_muted", new { player.Volume, player.IsMuted });
            foreach (var blocked in new[] { SessionState.Recording, SessionState.Paused, SessionState.Stopping, SessionState.Faulted })
            {
                state = blocked; mixedPath = riff; UpdateControls();
                Playback_Click(this, new RoutedEventArgs());
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                if (PlaybackButton.IsEnabled || player.Source is not null)
                    throw new InvalidDataException("Production playback entry opened a file in locked state " + blocked);
            }
            AppendPlaybackSmokeStage(progressPath, "playback_state_locks_passed");
            state = SessionState.Saved;
            results.Add(await MeasurePlaybackAsync("pcm16_riff", riff, riffFrames / (double)rate, riffFrames / (double)rate, token, progressPath));
            AppendPlaybackSmokeStage(progressPath, "rf64_direct_playback_not_run", new { Reason = "Direct RF64 is not the production listening path; do not open it in this process." });
            results.Add(await MeasurePlaybackAsync("actual_m4a_direct", m4a, rf64Frames / (double)rate, encoded.DurationSeconds, token, progressPath));
            var fullMix = new FullMixResult(rf64, m4a, fixture, rate, rf64Frames, rf64Frames * 1_000_000L / rate, [])
            { WaveSha256 = hashes[rf64], CompressedSha256 = hashes[m4a], SourceStorageComplete = true };
            currentMix = fullMix;
            string? listening = SelectListeningPath(fullMix);
            if (listening != m4a) errors.Add("RF64 full mix did not select its validated M4A listening copy");
            else results.Add(await MeasurePlaybackAsync("rf64_full_mix_m4a_listening", listening, rf64Frames / (double)rate, encoded.DurationSeconds, token, progressPath));
            var ordinaryMix = fullMix with { WavePath = riff, FrameCount = riffFrames, DurationUs = riffFrames * 1_000_000L / rate, WaveSha256 = hashes[riff] };
            if (SelectListeningPath(ordinaryMix) != riff) errors.Add("Ordinary RIFF playback path changed");
            if (SelectListeningPath(fullMix with { CompressedPath = null, CompressedSha256 = null }) is not null)
                errors.Add("Unvalidated RF64 listening fallback was allowed");
            foreach (var required in results)
                if (!required.Passed) errors.Add(required.Format + ": " + required.Failure);
            if (results.Count != 3) errors.Add("Missing playback scenario");
            if (host is not null || microphoneEnabled) errors.Add("Unexpected capture state");
            bool sourcesUnchanged = hashes.All(pair => PlaybackFixtureHash(pair.Key) == pair.Value);
            if (!sourcesUnchanged) errors.Add("Playback changed a synthetic source");
            bool pass = errors.Count == 0;
            AppendPlaybackSmokeStage(progressPath, "production_playback_checks_finished", new { Passed = pass, Errors = errors });
            return (pass, new
            {
                Status = pass ? "PASS_PRODUCTION_LISTENING_PATHS" : "FAIL",
                Os = Environment.OSVersion.Version.ToString(), Fixture = fixture, StageProgress = progressPath, Results = results, Encoding = encoded,
                Rf64DirectPlayback = "NOT_RUN (not opened by this process; production uses validated M4A)",
                Rf64LargeFilePlayback = "NOT_RUN; complete validated M4A selected for listening; RF64 WAV retained for export",
                PlayerVolume = 0, PlayerMuted = true, SystemVolumeOrRouteChanged = false,
                ProductionPlaybackHandlerUsed = true, RecordingPausedStoppingFaultedLock = "PASS", SourceHashes = hashes, SourcesUnchanged = sourcesUnchanged,
                CaptureHostStarted = false, MicrophoneCollected = false, HistoricalDataRead = false, Uploaded = false, Errors = errors
            });
        }
        finally
        {
            AppendPlaybackSmokeStage(progressPath, "before_final_player_cleanup");
            player.Stop(); player.Close(); player.Volume = previousVolume; player.IsMuted = previousMute;
            state = SessionState.Idle; mixedPath = null; currentMix = null; UpdateControls();
            AppendPlaybackSmokeStage(progressPath, "final_player_cleanup_finished");
        }
    }

    private async Task<PlaybackFormatCheck> MeasurePlaybackAsync(string format, string path, double contentSeconds, double decodedSeconds, CancellationToken token, string progressPath)
    {
        AppendPlaybackSmokeStage(progressPath, "before_format_player_reset", new { Format = format });
        player.Stop(); player.Close();
        // Close resets MediaPlayer properties; keep each new format muted,
        // rather than relying on the prior player's discarded state.
        player.Volume = 0; player.IsMuted = true; player.SpeedRatio = 1;
        AppendPlaybackSmokeStage(progressPath, "format_player_reset_finished", new { Format = format });
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = Stopwatch.StartNew();
        bool hasAudio = false, ended = false; double? natural = null, endPosition = null, openedAt = null, endedAt = null;
        double largestPosition = 0; string? failure = null;
        EventHandler onOpened = (_, _) =>
        {
            try
            {
                openedAt = timer.Elapsed.TotalSeconds;
                hasAudio = player.HasAudio;
                natural = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : null;
                AppendPlaybackSmokeStage(progressPath, "media_opened", new { Format = format, HasAudio = hasAudio, NaturalDurationSeconds = natural });
                opened.TrySetResult();
            }
            catch (Exception ex) { failure = "MediaOpened: " + ex.Message; finished.TrySetResult(); }
        };
        EventHandler onEnded = (_, _) =>
        {
            try
            {
                ended = true; endedAt = timer.Elapsed.TotalSeconds; endPosition = player.Position.TotalSeconds;
                AppendPlaybackSmokeStage(progressPath, "media_ended", new { Format = format, PositionSeconds = endPosition, ElapsedSeconds = endedAt });
            }
            catch (Exception ex) { failure = "MediaEnded: " + ex.Message; }
            finished.TrySetResult();
        };
        EventHandler<ExceptionEventArgs> onFailed = (_, e) =>
        {
            failure = e.ErrorException.GetType().Name + ": " + e.ErrorException.Message;
            try { AppendPlaybackSmokeStage(progressPath, "media_failed", new { Format = format, Failure = failure }); }
            catch (Exception logError) { failure += " | stage log: " + logError.Message; }
            finished.TrySetResult();
        };
        var positionSampler = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        positionSampler.Tick += (_, _) =>
        {
            try { largestPosition = Math.Max(largestPosition, player.Position.TotalSeconds); }
            catch (Exception ex) { failure = "Position: " + ex.Message; finished.TrySetResult(); }
        };
        player.MediaOpened += onOpened; player.MediaEnded += onEnded; player.MediaFailed += onFailed;
        try
        {
            mixedPath = path; state = SessionState.Saved; UpdateControls();
            if (!PlaybackButton.IsEnabled || player.Volume != 0 || !player.IsMuted)
                throw new InvalidDataException($"Muted playback preconditions failed: enabled={PlaybackButton.IsEnabled}, volume={player.Volume}, muted={player.IsMuted}.");
            positionSampler.Start();
            AppendPlaybackSmokeStage(progressPath, "before_production_playback_click", new { Format = format, File = path, player.Volume, player.IsMuted });
            Playback_Click(this, new RoutedEventArgs());
            if (player.Volume != 0 || !player.IsMuted)
            {
                player.Stop(); player.Close();
                throw new InvalidDataException("Player mute state changed at Open; playback stopped.");
            }
            AppendPlaybackSmokeStage(progressPath, "production_playback_click_returned", new { Format = format });
            await Task.WhenAny(opened.Task, finished.Task).WaitAsync(TimeSpan.FromSeconds(8), token);
            if (failure is null) await finished.Task.WaitAsync(TimeSpan.FromSeconds(decodedSeconds + 8), token);
            double actualEnd = Math.Max(largestPosition, endPosition ?? 0);
            if (failure is null && (!opened.Task.IsCompletedSuccessfully || !ended || !hasAudio || natural is null)) failure = "Missing MediaOpened/audio/natural duration/MediaEnded";
            if (failure is null && (natural < contentSeconds - .03 || natural > decodedSeconds + .15)) failure = "Natural duration differs from full synthetic content";
            if (failure is null && (actualEnd < contentSeconds - .12 || endedAt - openedAt < contentSeconds - .25)) failure = "Playback ended before the complete content timeline";
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            AppendPlaybackSmokeStage(progressPath, "format_managed_failure", new { Format = format, Failure = failure });
        }
        finally
        {
            positionSampler.Stop(); player.MediaOpened -= onOpened; player.MediaEnded -= onEnded; player.MediaFailed -= onFailed;
            AppendPlaybackSmokeStage(progressPath, "before_format_player_cleanup", new { Format = format });
            player.Stop(); player.Close();
            AppendPlaybackSmokeStage(progressPath, "format_player_cleanup_finished", new { Format = format });
        }
        return new(format, Path.GetFileName(path), failure is null, opened.Task.IsCompletedSuccessfully, ended, hasAudio,
            contentSeconds, decodedSeconds, natural, endPosition, largestPosition, openedAt, endedAt, failure);
    }

    private static void WritePlaybackFixture(string path, int frames, bool rf64)
    {
        const int rate = 48000;
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(file);
        uint dataBytes = checked((uint)frames * 2);
        writer.Write(rf64 ? 0x34364652u : 0x46464952u); writer.Write(rf64 ? uint.MaxValue : 36u + dataBytes); writer.Write(0x45564157u);
        if (rf64)
        {
            writer.Write(0x34367364u); writer.Write(28u); writer.Write(72UL + dataBytes); writer.Write((ulong)dataBytes); writer.Write((ulong)frames); writer.Write(0u);
        }
        writer.Write(0x20746d66u); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write(0x61746164u); writer.Write(rf64 ? uint.MaxValue : dataBytes);
        for (int frame = 0; frame < frames; frame++)
        {
            double edge = Math.Clamp(Math.Min(frame, frames - 1 - frame) / 480d, 0, 1);
            writer.Write((short)Math.Round(300 * edge * Math.Sin(2 * Math.PI * 733 * frame / rate)));
        }
        writer.Flush(); file.Flush(true);
    }

    private static string PlaybackFixtureHash(string path)
    {
        using var input = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private sealed record PlaybackFormatCheck(string Format, string File, bool Passed, bool MediaOpened, bool MediaEnded,
        bool HasAudio, double ContentSeconds, double DecodedSeconds, double? NaturalDurationSeconds, double? PositionAtEndSeconds,
        double LargestObservedPositionSeconds, double? OpenedAtElapsedSeconds, double? EndedAtElapsedSeconds, string? Failure);
}
