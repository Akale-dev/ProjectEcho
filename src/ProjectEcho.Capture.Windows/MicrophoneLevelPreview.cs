using System.Diagnostics;
using ProjectEcho.Core;

namespace ProjectEcho.Capture.Windows;

internal sealed record MicrophonePreviewState(AudioLevelPreviewReading Reading, bool Started);

/// <summary>
/// Runs only after a user explicitly enables mic preview. The exact selected
/// microphone is opened with the production RAW/format policy. PCM never leaves
/// this MTA: each temporary packet is zeroed in finally and no writer is created.
/// </summary>
internal sealed class MicrophoneLevelPreview : IAsyncDisposable
{
    private readonly Func<IPreparedRecordingSource> prepare;
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration cancellationRegistration;
    private int stopRequested;
    // Atomic lifecycle gate: 0=pending, 1=Start already authorized, 2=cancelled
    // before Start. RequestStop never waits for a potentially slow native Start.
    private int startGate;
    private MicrophonePreviewState current = new(new(0, AudioLevelPreviewState.Preparing), false);
    internal MicrophonePreviewState Current => Volatile.Read(ref current);

    internal MicrophoneLevelPreview(string selectedDeviceId, CancellationToken cancellation = default)
        : this(SelectedFactory(selectedDeviceId), cancellation) { }

    private static Func<IPreparedRecordingSource> SelectedFactory(string selectedDeviceId)
    {
        if (string.IsNullOrWhiteSpace(selectedDeviceId)) throw new ArgumentException("An explicitly enabled microphone is required.", nameof(selectedDeviceId));
        return () => new RecordingSourceFactory().PrepareMicrophone(selectedDeviceId);
    }

    internal MicrophoneLevelPreview(Func<IPreparedRecordingSource> prepare, CancellationToken cancellation = default)
    {
        this.prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
        // Registration happens before creating the thread. An already-cancelled
        // outer reader factory can therefore never start late microphone work.
        cancellationRegistration = cancellation.Register(RequestStop);
        var worker = new Thread(Run) { IsBackground = true, Name = "ProjectEcho microphone level preview" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    private void Run()
    {
        IPreparedRecordingSource? prepared = null;
        IRecordingAudioSource? source = null;
        Exception? failure = null, cleanupFailure = null;
        bool started = false;
        try
        {
            if (Stopped) return;
            prepared = prepare();
            if (Stopped) return; // A cancelled slow native initialization must never Start.
            source = prepared.CompleteOnCaptureThread();
            prepared.Dispose(); prepared = null;
            if (Stopped || Interlocked.CompareExchange(ref startGate, 1, 0) != 0) return;
            // Successful CAS linearizes Start authorization against RequestStop.
            // A later Off waits asynchronously for real Stop/Dispose; no PCM is
            // read after its flag, even if native Start itself returns slowly.
            source.Start(); started = true;
            if (Stopped) return;
            Publish(new(0, AudioLevelPreviewState.NoSignalObserved, "waiting_for_pcm"), true);
            ulong epoch = Now100ns();
            long lastPacketAt = Stopwatch.GetTimestamp();
            while (!Stopped)
            {
                bool readAny = false;
                while (!Stopped)
                {
                    var packet = source.Read(epoch, null);
                    if (packet is null) break;
                    try
                    {
                        if (Stopped) break;
                        readAny = true;
                        lastPacketAt = Stopwatch.GetTimestamp();
                        Publish(AudioLevelPreviewReading.FromPeak(Peak(packet.Pcm, source.Format)), true);
                    }
                    finally { Array.Clear(packet.Pcm); }
                }
                if (!readAny && Stopwatch.GetElapsedTime(lastPacketAt) > TimeSpan.FromMilliseconds(250))
                    Publish(new(0, AudioLevelPreviewState.NoSignalObserved, "waiting_for_pcm"), true);
                // This service has no file/capture callback dependency; checking
                // cancellation every 5ms bounds idle stop latency without a UI wait.
                if (!Stopped) Thread.Sleep(5);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            if (started)
            {
                try { source!.Stop(); }
                catch (Exception ex) { cleanupFailure = ex; }
            }
            try { source?.Dispose(); }
            catch (Exception ex) { cleanupFailure ??= ex; }
            try { prepared?.Dispose(); }
            catch (Exception ex) { cleanupFailure ??= ex; }
            cancellationRegistration.Dispose();
            if (cleanupFailure is not null)
            {
                Publish(Failed(cleanupFailure), started); // Do not claim mic-off when release is unconfirmed.
                finished.TrySetException(cleanupFailure);
            }
            else
            {
                Publish(failure is null ? AudioLevelPreviewReading.NotSelected : Failed(failure), false);
                finished.TrySetResult();
            }
        }
    }

    private bool Stopped => Volatile.Read(ref stopRequested) != 0;
    private void Publish(AudioLevelPreviewReading reading, bool started) => Volatile.Write(ref current, new(reading, started));
    private static AudioLevelPreviewReading Failed(Exception ex) => new(0, AudioLevelPreviewState.Unavailable,
        $"mic_preview:{ex.GetType().Name}:0x{ex.HResult:X8}");
    private static ulong Now100ns() => (ulong)AudioClock.CounterTo100ns(Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    internal static float Peak(byte[] pcm, PcmFormat format)
    {
        float peak = 0;
        int width = format.BitsPerSample / 8;
        if (width is < 2 or > 4 || pcm.Length % format.BlockAlign != 0 || format.IsFloat && width != 4)
            throw new InvalidDataException("Invalid preview PCM format or packet alignment.");
        for (int i = 0; i < pcm.Length; i += width)
        {
            float value = format.IsFloat ? BitConverter.ToSingle(pcm, i) : width switch
            {
                2 => BitConverter.ToInt16(pcm, i) / 32768f,
                3 => ((pcm[i] | pcm[i + 1] << 8 | pcm[i + 2] << 16) << 8 >> 8) / 8388608f,
                4 => BitConverter.ToInt32(pcm, i) / 2147483648f,
                _ => throw new InvalidDataException("Unsupported preview PCM width.")
            };
            if (!float.IsFinite(value)) throw new InvalidDataException("Nonfinite preview PCM sample.");
            peak = Math.Max(peak, Math.Abs(value));
        }
        return Math.Min(1, peak);
    }

    public ValueTask DisposeAsync()
    {
        RequestStop();
        return new ValueTask(finished.Task);
    }
    internal void RequestStop()
    {
        Interlocked.CompareExchange(ref startGate, 2, 0);
        Interlocked.Exchange(ref stopRequested, 1);
    }
}
