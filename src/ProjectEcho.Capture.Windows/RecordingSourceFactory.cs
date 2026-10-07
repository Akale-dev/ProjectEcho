using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ProjectEcho.Core;

namespace ProjectEcho.Capture.Windows;

internal sealed class RecordingSourceFactory : IRecordingSourceFactory
{
    public IRecordingProcessLifetime? WatchProcess(CaptureOptions options) => options.Mode == SourceMode.ProcessTree
        ? new RecordingProcessLifetime(options.ProcessSource!.Root) : null;
    public void Preflight(RecordingOptions options)
    {
        if (options.MaximumSeconds is < 1 or > 28800 || options.ExpectedMinutes is < 1 or > 480)
            throw new ArgumentOutOfRangeException(nameof(options), "录音上限须为1秒至8小时，预算须为1至480分钟。");
        var preflight = SourceDiscovery.Preflight(options.Selection with { MaximumSeconds = 10 });
        if (!preflight.CanStart) throw new InvalidOperationException(string.Join(Environment.NewLine, preflight.Errors));
        long budget = checked(preflight.EstimatedBytesPerSecond * options.ExpectedMinutes * 60 * 4 + 64L * 1024 * 1024);
        if (preflight.AvailableBytes < budget) throw new IOException("磁盘不足以保存预算时长的原轨及混音/临时文件。");
    }
    public void ValidateRemote(CaptureOptions options)
    {
        if (options.Mode == SourceMode.ProcessTree) SourceDiscovery.ValidateSource(options.ProcessSource!);
    }
    public IRecordingAudioSource OpenRemote(CaptureOptions options)
    {
        if (options.Mode == SourceMode.EndpointLoopback)
        {
            if (!options.EndpointScopeConfirmed) throw new InvalidOperationException("端点范围未确认。");
            return OpenEndpoint("remote", options.RenderDeviceId, DataFlow.Render);
        }
        ValidateRemote(options);
        return ProcessLoopbackSupport.Activate(() =>
        {
            if (!ProcessLoopbackSupport.CanAttemptCurrent) throw new PlatformNotSupportedException(ProcessLoopbackSupport.Current.PreflightMessage);
            var client = AudioClient.ActivateProcessLoopbackAsync((uint)options.ProcessSource!.Root.ProcessId,
                ProcessLoopbackMode.IncludeTargetProcessTree).GetAwaiter().GetResult();
            return new WasapiSource("remote", client, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), true);
        });
    }
    public IRecordingAudioSource OpenMicrophone(string deviceId)
    {
        using var prepared = PrepareMicrophone(deviceId);
        return prepared.CompleteOnCaptureThread();
    }
    public IPreparedRecordingSource PrepareMicrophone(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = SourceDiscovery.GetExactDevice(enumerator, deviceId, DataFlow.Capture);
        return new PreparedMicrophone(MicrophoneFormatPolicy.OpenClient(device, initialize: true));
    }
    private static IRecordingAudioSource OpenEndpoint(string id, string? deviceId, DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = SourceDiscovery.GetExactDevice(enumerator, deviceId, flow);
        var client = device.CreateAudioClient();
        try
        {
            var format = client.MixFormat;
            return new WasapiSource(id, client, format, flow == DataFlow.Render);
        }
        catch { client.Dispose(); throw; }
    }

    private sealed class PreparedMicrophone(MicrophoneClientSelection<AudioClient> selection) : IPreparedRecordingSource
    {
        private MicrophoneClientSelection<AudioClient>? pending = selection;
        public IRecordingAudioSource CompleteOnCaptureThread()
        {
            var microphone = Interlocked.Exchange(ref pending, null)
                ?? throw new ObjectDisposedException(nameof(PreparedMicrophone));
            // WasapiSource owns the client even when its constructor fails. Its
            // AudioCaptureClient/GetService and later Release both occur on the
            // capture thread, per the WASAPI service lifetime contract.
            return new WasapiSource("mic", microphone.Client, microphone.Format, false,
                microphone.ConversionFlags, initialized: true, processingMode: microphone.ProcessingMode);
        }
        public void Dispose() => Interlocked.Exchange(ref pending, null)?.Dispose();
    }

    private sealed class WasapiSource : IRecordingAudioSource
    {
        private readonly AudioClient client;
        private readonly AudioCaptureClient capture;
        private readonly AutoResetEvent ready = new(false);
        private readonly CaptureClockHealth clock = new();
        private readonly CapturePacketScratch scratch;
        private readonly CaptureReadMetrics readMetrics = new();
        private readonly int? bufferFrames;
        private readonly long? streamLatency100ns;
        public string Id { get; }
        public WaveFormat WaveFormat { get; }
        public PcmFormat Format { get; }
        public string? ProcessingMode { get; }
        public WaitHandle PacketReady => ready;
        // Snapshot construction is deliberately outside Read's held-buffer path.
        public RecordingSourceDiagnostics Diagnostics => new(bufferFrames, streamLatency100ns, scratch.Capacity,
            readMetrics.PollCount, readMetrics.PacketCount, readMetrics.MaximumPollInterval100ns,
            readMetrics.MaximumBufferHold100ns, readMetrics.PollIntervalsOver20Ms, readMetrics.BufferHoldsOver2Ms);
        public WasapiSource(string id, AudioClient audioClient, WaveFormat waveFormat, bool loopback,
            AudioClientStreamFlags conversionFlags = 0, bool initialized = false, string? processingMode = null)
        {
            client = audioClient; Id = id; WaveFormat = waveFormat; ProcessingMode = processingMode;
            try
            {
                Format = CaptureSession.Describe(waveFormat);
                Format.Validate();
                if (Format.Channels > 2) throw new NotSupportedException("当前只支持单/双声道录音格式。");
                var flags = AudioClientStreamFlags.EventCallback | (loopback ? AudioClientStreamFlags.Loopback : 0) | conversionFlags;
                if (!initialized) client.Initialize(AudioClientShareMode.Shared, flags, 0, 0, waveFormat, Guid.Empty);
                client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
                capture = client.AudioCaptureClient;
                try { bufferFrames = client.BufferSize; }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException) { }
                try { streamLatency100ns = client.StreamLatency; }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException) { }
                scratch = new CapturePacketScratch(bufferFrames, Format.BlockAlign);
            }
            catch { ready.Dispose(); client.Dispose(); throw; }
        }
        public void Start() => client.Start();
        public void Stop() => client.Stop();
        public RecordingBlock? Read(ulong epochQpc100ns, ulong? cutoffQpc100ns)
        {
            readMetrics.Poll(Stopwatch.GetTimestamp());
            scratch.Clear();
            if (capture.GetNextPacketSize() <= 0) return null;
            var pointer = capture.GetBuffer(out int frames, out var flags, out long device, out long qpc);
            long heldAt = Stopwatch.GetTimestamp();
            try
            {
                CaptureClockObservation quality = default;
                int keep = 0;
                ScratchCopyResult copied = ScratchCopyResult.Success;
                try
                {
                    if (frames > 0)
                    {
                        quality = clock.Observe(device, qpc, frames, (flags & AudioClientBufferFlags.TimestampError) != 0, epochQpc100ns);
                        keep = cutoffQpc100ns is ulong cutoff
                            ? quality.QpcValid ? AudioClock.CompleteFramesBefore((ulong)qpc, cutoff, Format.SampleRate, frames) : 0
                            : frames;
                        // No managed arrays, records or error objects while the native
                        // packet is held. Only the original authorized prefix is copied.
                        copied = scratch.CopyPrefix(pointer, (long)keep * Format.BlockAlign,
                            (flags & AudioClientBufferFlags.Silent) != 0);
                    }
                }
                finally
                {
                    try { capture.ReleaseBuffer(frames); }
                    finally { if (frames > 0) readMetrics.Released(heldAt, Stopwatch.GetTimestamp()); }
                }
                if (frames <= 0) return null;
                if (copied == ScratchCopyResult.ExceedsPacketLimit) throw new IOException($"音频包超过1MiB限额；rawFrames={frames}，retainedFrames={keep}，blockAlign={Format.BlockAlign}，reportedBufferFrames={bufferFrames}，scratchBytes={scratch.Capacity}。未扩大采集范围。");
                if (copied == ScratchCopyResult.ExceedsScratchCapacity) throw new IOException($"音频包超过预分配采集缓冲区；rawFrames={frames}，retainedFrames={keep}，blockAlign={Format.BlockAlign}，reportedBufferFrames={bufferFrames}，scratchBytes={scratch.Capacity}。未扩大采集范围。");
                if (copied == ScratchCopyResult.MissingPointer) throw new IOException("非静音包为空指针。");
                byte[] pcm = scratch.MaterializeAndClear();
                return new(pcm, frames, keep, unchecked((ulong)device), unchecked((ulong)qpc), (int)flags,
                    quality.QpcValid, quality.DevicePositionValid, frames - keep);
            }
            finally { scratch.Clear(); }
        }
        public void Dispose()
        {
            try { scratch.Dispose(); }
            finally { try { client.Dispose(); } finally { ready.Dispose(); } }
        }
    }
}
