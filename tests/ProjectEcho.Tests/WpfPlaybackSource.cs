using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;

/// <summary>Opt-in test-owned WPF player. It accepts only generated files in its own integration fixture.</summary>
internal static class WpfPlaybackSource
{
    internal const double PlayerVolume = .25;

    internal static Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3 || args[0] != "--wpf-playback-source" || args[2] is not ("wav" or "m4a") ||
            args[1].Length is < 10 or > 90 || args[1].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return Task.FromResult(2);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var work = RunStaAsync(args[1], args[2]);
                int exitCode = 1;
                var finish = work.ContinueWith(result =>
                {
                    exitCode = result.IsCompletedSuccessfully ? result.Result : 1;
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                Dispatcher.Run();
                finish.GetAwaiter().GetResult();
                completion.TrySetResult(exitCode);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); completion.TrySetResult(1); }
        }) { IsBackground = true, Name = "ProjectEcho synthetic WPF playback STA" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }

    private static async Task<int> RunStaAsync(string runId, string format)
    {
        using var hardLimit = new Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(40), Timeout.InfiniteTimeSpan);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var outputGate = new object();
        var token = deadline.Token;
        MediaPlayer? media = null;
        try
        {
            string root = SyntheticAudioProbe.FindProjectRoot();
            string folder = SyntheticAudioProbe.SafeProjectPath(root, ".local/WpfPlaybackIntegration/" + runId);
            string file = Path.Combine(folder, "signal." + format), proofPath = Path.Combine(folder, "fixture-proof.json");
            foreach (string path in new[] { file, proofPath })
                if (!File.Exists(path) || new FileInfo(path).Length > 4_000_000 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Only bounded test-owned fixture files may be played.");
            using var proof = JsonDocument.Parse(File.ReadAllBytes(proofPath));
            if (proof.RootElement.GetProperty("schema").GetString() != "owned-wpf-markers-1" || !proof.RootElement.GetProperty("isSynthetic").GetBoolean())
                throw new InvalidDataException("Missing synthetic fixture proof.");
            string expectedHash = proof.RootElement.GetProperty(format + "Sha256").GetString()!;
            using (var input = File.OpenRead(file))
                if (Convert.ToHexStringLower(SHA256.HashData(input)) != expectedHash) throw new InvalidDataException("Synthetic fixture changed before playback.");

            var quiet = new RegistrationSilence();
            await using var registrar = new WasapiPlayerBuilder().WithSharedMode().WithLatency(100).WithEventSync().Build();
            var playbackFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            registrar.PlaybackStopped += (_, e) => { if (e.Exception is not null) playbackFailure.TrySetResult(e.Exception); };
            registrar.Init(quiet); registrar.Play();
            await quiet.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await Task.Delay(150, token);
            if (registrar.PlaybackState != PlaybackState.Playing) throw new IOException("Silent registration stream did not start.");
            var activeMedia = new MediaPlayer { Volume = PlayerVolume, IsMuted = false, SpeedRatio = 1 };
            media = activeMedia;
            var clock = new Stopwatch();
            double? natural = null;
            activeMedia.MediaOpened += (_, _) =>
            {
                try
                {
                    natural = activeMedia.NaturalDuration.HasTimeSpan ? activeMedia.NaturalDuration.TimeSpan.TotalSeconds : null;
                    Emit(new { Type = "opened", Format = format, HasAudio = activeMedia.HasAudio, NaturalDurationSeconds = natural, ElapsedSeconds = clock.Elapsed.TotalSeconds });
                }
                catch (Exception ex) { playbackFailure.TrySetResult(ex); }
            };
            activeMedia.MediaEnded += (_, _) =>
            {
                try
                {
                    // Properties are diagnostic only. Never stop/seek at this reported duration.
                    Emit(new { Type = "done", Format = format, NaturalDurationSeconds = natural,
                        EndPositionSeconds = activeMedia.Position.TotalSeconds, ElapsedSeconds = clock.Elapsed.TotalSeconds, EndReason = "MediaEnded" });
                }
                catch (Exception ex) { playbackFailure.TrySetResult(ex); }
            };
            activeMedia.MediaFailed += (_, e) => playbackFailure.TrySetResult(e.ErrorException);
            Emit(new { Type = "ready", ProcessId = Environment.ProcessId, Format = format, FileSha256 = expectedHash,
                RegistrarOutputDeviceId = registrar.DeviceId, RegistrarOutputDeviceName = registrar.DeviceFriendlyName,
                RegistrarSignal = "digital_zero_only", WpfPlayerVolume = PlayerVolume, SystemVolumeOrRouteChanged = false,
                CaptureOrMicrophoneCreated = false, HardLimitSeconds = 40 });
            bool went = false;
            while (true)
            {
                var command = Task.Run(Console.ReadLine, CancellationToken.None);
                var winner = await Task.WhenAny(command, playbackFailure.Task).WaitAsync(token);
                if (winner == playbackFailure.Task) throw await playbackFailure.Task;
                string? value = await command;
                if (value is null or "stop")
                {
                    activeMedia.Stop(); activeMedia.Close(); registrar.Stop();
                    Emit(new { Type = "stopped", Format = format });
                    return 0;
                }
                if (value != "go" || went) throw new InvalidDataException("Expected one go followed by stop/EOF.");
                went = true; clock.Start();
                Emit(new { Type = "go", Format = format });
                activeMedia.Open(new Uri(file)); activeMedia.Play();
            }
        }
        catch (Exception ex)
        {
            try { Emit(new { Type = "error", Format = format, Error = ex.GetType().Name, ex.Message }); } catch { }
            return ex is OperationCanceledException ? 124 : 1;
        }
        finally
        {
            if (media is not null) { try { media.Stop(); media.Close(); } catch { } }
        }

        void Emit(object value)
        {
            // This protocol emits fewer than ten bounded lines; the independent
            // process deadline still protects against a broken/unread parent pipe.
            lock (outputGate) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
        }
    }

    private sealed class RegistrationSilence : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Read(Span<byte> buffer) { buffer.Clear(); FirstRead.TrySetResult(); return buffer.Length - buffer.Length % WaveFormat.BlockAlign; }
    }
}
