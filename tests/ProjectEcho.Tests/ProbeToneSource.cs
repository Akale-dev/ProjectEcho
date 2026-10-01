using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

/// <summary>
/// Opt-in, playback-only test child. It never opens a capture or microphone device.
/// Invocation: --probe-tone-source target|control|target-m1|target-silence. Stdin: go (once), stop, or EOF.
/// Stdout is JSON lines with Type=ready/go/done/stopped/error. Before go and after
/// done the same render stream continues with digital silence so its session/PID
/// remains selectable while the parent captures and finalizes its own recording.
/// </summary>
internal static class ProbeToneSource
{
    internal const int SampleRate = 48_000;
    internal const int Channels = 2;
    internal const int MainFrames = SampleRate * 5;
    internal const int ToneFrames = SampleRate * 6;
    internal const int M1ToneFrames = SampleRate * 40;
    internal const int SilenceFrames = SampleRate * 900;
    internal const float PeakAmplitude = .02f;
    private const int DrainAllowanceMilliseconds = 500;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] != "--probe-tone-source" || args[1] is not ("target" or "control" or "target-m1" or "target-silence" or "target-sync-short" or "target-sync-stress" or "target-sync-medium" or "target-sync-long") ||
            (args.Length == 3 && args[1] is not ("target-sync-short" or "target-sync-stress" or "target-sync-medium" or "target-sync-long" or "target-m1")))
        {
            Console.Error.WriteLine("Usage: --probe-tone-source target|control|target-m1|target-silence");
            return 2;
        }

        // Independent of stdin/stdout and async scheduling: never leave an audible
        // orphan after a parent failure, a blocked pipe, or a stalled native call.
        var m1 = args[1] == "target-m1";
        var silent = args[1] == "target-silence";
        var sync = args[1].StartsWith("target-sync-", StringComparison.Ordinal);
        var longSync = args[1] == "target-sync-long";
        var mediumSync = args[1] == "target-sync-medium";
        var stressSync = args[1] == "target-sync-stress";
        var hardTimeoutSeconds = sync ? (longSync ? 7320 : mediumSync ? 1320 : stressSync ? 180 : 75) : silent ? 960 : m1 ? 75 : 30;
        using var hardDeadline = new Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(hardTimeoutSeconds), Timeout.InfiniteTimeSpan);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(silent ? 955 : hardTimeoutSeconds - 2));
        using var outputGate = new SemaphoreSlim(1, 1);
        var token = lifetime.Token;
        var role = args[1];
        var provider = new ProbeWaveProvider(role == "control", m1, silent, sync, longSync, mediumSync, stressSync);
        Task? reportDone = null;
        var playbackFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Verified against the project's locked NAudio.Wasapi 3.1.0 XML API.
            // A fixed default endpoint is selected on construction; no automatic
            // stream routing or endpoint/session volume changes are requested.
            using var outputEnumerator = args.Length == 3 ? new MMDeviceEnumerator() : null;
            using var chosenOutput = outputEnumerator?.GetDevice(args[2]);
            if (chosenOutput is not null && (chosenOutput.State != DeviceState.Active || chosenOutput.DataFlow != DataFlow.Render))
                throw new IOException("Explicit test output endpoint is not an active render device; no fallback attempted.");
            var builder = new WasapiPlayerBuilder().WithSharedMode().WithLatency(100).WithEventSync();
            if (chosenOutput is not null) builder = builder.WithDevice(chosenOutput);
            await using var player = builder.Build();
            player.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null) playbackFailure.TrySetResult(e.Exception);
            };
            player.Init(provider);
            player.Play();
            await provider.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await Task.Delay(150, token);
            if (playbackFailure.Task.IsCompleted) throw await playbackFailure.Task;
            if (player.PlaybackState != PlaybackState.Playing)
                throw new IOException("Synthetic render stream stopped before ready.");
            await EmitAsync(new
            {
                Type = "ready", ProcessId = Environment.ProcessId, Role = role,
                OutputDeviceId = player.DeviceId, OutputDeviceName = player.DeviceFriendlyName, OutputSessionMuted = player.IsMuted,
                SampleRate, Channels, ToneFrames = ExpectedFrames(role),
                MainHz = silent || sync ? 0 : role == "control" ? 440 : 660,
                TailHz = silent || sync ? 0 : role == "control" ? 440 : 880, TailStartFrame = silent || sync ? (int?)null : MainFrames,
                MarkerFrequenciesHz = sync ? new[] { 733, 997, 1319, 1699 } : [],
                PeakAmplitude = silent ? 0 : PeakAmplitude,
                HardTimeoutSeconds = hardTimeoutSeconds
            });

            while (true)
            {
                // Console's redirected reader may use a synchronous implementation.
                // Keep it off this continuation so timeout/playback errors can win.
                var read = Task.Run(Console.ReadLine, CancellationToken.None);
                var winner = await Task.WhenAny(read, playbackFailure.Task).WaitAsync(token);
                if (winner == playbackFailure.Task) throw await playbackFailure.Task;
                var command = await read;
                if (command is null or "stop")
                {
                    provider.Stop();
                    lifetime.Cancel();
                    if (reportDone is not null)
                    {
                        try { await reportDone; }
                        catch (OperationCanceledException) { }
                    }
                    player.Stop();
                    // The lifetime has ended; report shutdown with a separate short
                    // bound while the independent hard deadline remains armed.
                    using var finalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await EmitAsync(new { Type = "stopped", Role = role }, finalDeadline.Token);
                    return 0;
                }
                if (command != "go" || !provider.Start())
                    throw new InvalidOperationException("Expected one go command, then stop or EOF.");
                await EmitAsync(new { Type = "go", Role = role });
                reportDone = ReportDoneAsync();
            }
        }
        catch (Exception ex)
        {
            provider.Stop();
            lifetime.Cancel();
            if (reportDone is not null)
            {
                try { await reportDone; }
                catch { /* The original failure is the useful result. */ }
            }
            try
            {
                using var errorDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await EmitAsync(new { Type = "error", Role = role, Error = ex.GetType().Name, ex.Message }, errorDeadline.Token);
            }
            catch { /* Broken parent output must not keep playback alive. */ }
            return ex is OperationCanceledException ? 124 : 1;
        }

        async Task EmitAsync(object value, CancellationToken? writeToken = null)
        {
            var cancellation = writeToken ?? token;
            await outputGate.WaitAsync(cancellation);
            try
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), cancellation);
                await Console.Out.FlushAsync(cancellation);
            }
            finally { outputGate.Release(); }
        }

        async Task ReportDoneAsync()
        {
            await provider.ToneSubmitted.Task.WaitAsync(token);
            // The provider was read by WASAPI; this is not a physical-speaker
            // completion measurement. Allow 500ms of following silence (requested
            // render latency 100ms), then let the parent inspect the recorded tail.
            await Task.Delay(DrainAllowanceMilliseconds, token);
            await EmitAsync(new
            {
                Type = "done", Role = role, SubmittedFrames = ExpectedFrames(role),
                DrainAllowanceMilliseconds, Completion = "provider_consumed_plus_silence_allowance"
            });
        }
    }

    /// <summary>Pure synthetic checks; no audio device enumeration or activation.</summary>
    internal static void TestWaveform()
    {
        foreach (var control in new[] { false, true })
        {
            var provider = new ProbeWaveProvider(control);
            var bytes = new byte[480 * Channels * sizeof(float)];
            Require(provider.Read(bytes) == bytes.Length && bytes.All(b => b == 0), "Unarmed source was not silent.");
            Require(!provider.ToneSubmitted.Task.IsCompleted, "Unarmed source completed a tone.");
            Require(provider.Start() && !provider.Start(), "Source must accept exactly one go.");
            var samples = new float[ToneFrames];
            var position = 0;
            while (position < ToneFrames)
            {
                Require(provider.Read(bytes) == bytes.Length, "Source returned a short buffer.");
                var floats = MemoryMarshal.Cast<byte, float>(bytes);
                for (var frame = 0; frame < 480; frame++)
                {
                    var value = floats[frame * Channels];
                    Require(value == floats[frame * Channels + 1], "Stereo channels differ.");
                    Require(float.IsFinite(value) && Math.Abs(value) <= PeakAmplitude, "Amplitude bound failed.");
                    samples[position++] = value;
                }
            }
            Require(provider.ToneSubmitted.Task.IsCompleted, "Tone did not finish at exactly six seconds.");
            Require(provider.Read(bytes) == bytes.Length && bytes.All(b => b == 0), "Source was not silent after its tail.");
            Require(Correlation(samples, SampleRate, SampleRate, control ? 440 : 660) > .019, "Main frequency missing.");
            Require(Correlation(samples, MainFrames + SampleRate / 4, SampleRate / 2, control ? 440 : 880) > .019,
                "Final marker frequency missing.");
            Require(Correlation(samples, SampleRate, SampleRate, control ? 660 : 440) < .00001, "Unexpected control frequency.");
            provider.Stop();
            Require(!provider.Start() && provider.Read(bytes) == bytes.Length && bytes.All(b => b == 0), "Stopped source restarted.");
        }
        var early = new ProbeWaveProvider(false);
        Require(early.Start(), "Early-stop setup failed.");
        early.Stop();
        var stoppedBytes = new byte[480 * Channels * sizeof(float)];
        early.Read(stoppedBytes);
        Require(stoppedBytes.All(b => b == 0) && !early.ToneSubmitted.Task.IsCompleted, "Early stop emitted or completed tone.");
        var longProvider = new ProbeWaveProvider(false, m1: true);
        Require(longProvider.Start(), "M1 source setup failed.");
        var longSamples = new float[M1ToneFrames];
        for (int start = 0; start < M1ToneFrames; start += 480)
        {
            longProvider.Read(stoppedBytes);
            var floats = MemoryMarshal.Cast<byte, float>(stoppedBytes);
            for (int frame = 0; frame < 480; frame++) longSamples[start + frame] = floats[frame * Channels];
        }
        Require(longProvider.ToneSubmitted.Task.IsCompleted, "M1 source must finish at forty seconds.");
        Require(longSamples.AsSpan(ToneFrames, 33 * SampleRate).IndexOfAnyExcept(0f) < 0, "M1 middle must be digital silence.");
        Require(Correlation(longSamples, SampleRate, SampleRate, 660) > .019, "M1 initial 660 tone missing.");
        Require(Correlation(longSamples, 5 * SampleRate + SampleRate / 4, SampleRate / 2, 880) > .019, "M1 first 880 marker missing.");
        Require(Correlation(longSamples, 39 * SampleRate + SampleRate / 4, SampleRate / 2, 880) > .019, "M1 final 880 marker missing.");
        longProvider.Read(stoppedBytes);
        Require(stoppedBytes.All(b => b == 0), "M1 source did not return to silence.");
        var silentProvider = new ProbeWaveProvider(false, silent: true);
        var oneSecond = new byte[SampleRate * Channels * sizeof(float)];
        silentProvider.Read(oneSecond);
        Require(oneSecond.AsSpan().IndexOfAnyExcept((byte)0) < 0 && !silentProvider.ToneSubmitted.Task.IsCompleted,
            "Unarmed long-silence source was not silent.");
        Require(silentProvider.Start(), "Long-silence source setup failed.");
        // Advance 900 seconds of samples immediately; this pure check performs no
        // device operations and no wall-clock sleep.
        for (int second = 0; second < 900; second++)
        {
            Require(silentProvider.Read(oneSecond) == oneSecond.Length && oneSecond.AsSpan().IndexOfAnyExcept((byte)0) < 0,
                "Long-silence provider emitted nonzero PCM or a short buffer.");
            Require(silentProvider.ToneSubmitted.Task.IsCompleted == (second == 899), "Long-silence duration differs from 900 seconds.");
        }
        silentProvider.Read(oneSecond);
        Require(oneSecond.AsSpan().IndexOfAnyExcept((byte)0) < 0, "Long-silence provider emitted audio after done.");
        silentProvider.Stop();
        Require(!silentProvider.Start(), "Long-silence provider restarted after stop.");
    }

    private static double Correlation(float[] data, int start, int count, int frequency)
    {
        double sine = 0, cosine = 0;
        for (var i = 0; i < count; i++)
        {
            var angle = 2 * Math.PI * frequency * (start + i) / SampleRate;
            sine += data[start + i] * Math.Sin(angle);
            cosine += data[start + i] * Math.Cos(angle);
        }
        return 2 * Math.Sqrt(sine * sine + cosine * cosine) / count;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static int ExpectedFrames(string role) => role switch
    {
        "target-sync-short" => M2LongRunProbe.ShortSeconds * SampleRate,
        "target-sync-medium" => M2LongRunProbe.MediumSeconds * SampleRate,
        "target-sync-stress" => M2LongRunProbe.StressSeconds * SampleRate,
        "target-sync-long" => M2LongRunProbe.LongSeconds * SampleRate,
        "target-silence" => SilenceFrames,
        "target-m1" => M1ToneFrames,
        _ => ToneFrames
    };

    private sealed class ProbeWaveProvider(bool control, bool m1 = false, bool silent = false, bool sync = false, bool longSync = false, bool mediumSync = false, bool stressSync = false) : IWaveProvider
    {
        private int _state;
        private int _toneFrame;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ToneSubmitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Start() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
        public void Stop() => Interlocked.Exchange(ref _state, 2);

        public int Read(Span<byte> buffer)
        {
            buffer.Clear();
            var count = buffer.Length - buffer.Length % WaveFormat.BlockAlign;
            var samples = MemoryMarshal.Cast<byte, float>(buffer[..count]);
            if (Volatile.Read(ref _state) == 1)
            {
                var totalFrames = sync ? (longSync ? M2LongRunProbe.LongSeconds : mediumSync ? M2LongRunProbe.MediumSeconds : stressSync ? M2LongRunProbe.StressSeconds : M2LongRunProbe.ShortSeconds) * SampleRate
                    : silent ? SilenceFrames : m1 ? M1ToneFrames : ToneFrames;
                if (silent) _toneFrame = Math.Min(totalFrames, _toneFrame + count / WaveFormat.BlockAlign);
                for (var sample = 0; !silent && sample < samples.Length && _toneFrame < totalFrames; sample += Channels, _toneFrame++)
                {
                    if (Volatile.Read(ref _state) != 1) break;
                    if (sync)
                    {
                        var marker = mediumSync ? M2LongRunProbe.MediumMarkerSample(_toneFrame)
                            : stressSync ? M2LongRunProbe.StressMarkerSample(_toneFrame) : M2LongRunProbe.MarkerSample(_toneFrame, longSync);
                        samples[sample] = marker; samples[sample + 1] = marker;
                        continue;
                    }
                    if (m1 && _toneFrame >= ToneFrames && _toneFrame < M1ToneFrames - SampleRate) continue;
                    var frequency = control ? 440 : _toneFrame < MainFrames ? 660 : 880;
                    var finalM1Tail = m1 && _toneFrame >= M1ToneFrames - SampleRate;
                    var regionStart = finalM1Tail ? M1ToneFrames - SampleRate : control || _toneFrame < MainFrames ? 0 : MainFrames;
                    var regionEnd = finalM1Tail ? M1ToneFrames : control || _toneFrame >= MainFrames ? ToneFrames : MainFrames;
                    // Five-ms edges reduce clicks without hiding the one-second tail.
                    var fadeFrames = SampleRate / 200;
                    var envelope = Math.Min(1d, Math.Min(_toneFrame - regionStart, regionEnd - 1 - _toneFrame) / (double)fadeFrames);
                    var value = (float)(PeakAmplitude * envelope * Math.Sin(2 * Math.PI * frequency * _toneFrame / SampleRate));
                    samples[sample] = value;
                    samples[sample + 1] = value;
                }
                if (_toneFrame == totalFrames) ToneSubmitted.TrySetResult();
            }
            FirstRead.TrySetResult();
            return count;
        }
    }
}
