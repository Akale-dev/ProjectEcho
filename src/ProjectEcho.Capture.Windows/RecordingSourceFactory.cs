using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ProjectEcho.Core;

namespace ProjectEcho.Capture.Windows;

internal sealed class RecordingSourceFactory : IRecordingSourceFactory
{
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
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            throw new PlatformNotSupportedException("进程回环需要Windows build 20348+。");
        ValidateRemote(options);
        var client = AudioClient.ActivateProcessLoopbackAsync((uint)options.ProcessSource!.Root.ProcessId,
            ProcessLoopbackMode.IncludeTargetProcessTree).GetAwaiter().GetResult();
        return new WasapiSource("remote", client, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), true);
    }
    public IRecordingAudioSource OpenMicrophone(string deviceId) => OpenEndpoint("mic", deviceId, DataFlow.Capture);
    private static IRecordingAudioSource OpenEndpoint(string id, string? deviceId, DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = SourceDiscovery.GetExactDevice(enumerator, deviceId, flow);
        var client = device.CreateAudioClient();
        try
        {
            var format = client.MixFormat;
            if (flow == DataFlow.Capture) MicrophoneFormatPolicy.Validate(format, MicrophoneFormatPolicy.ReadDeviceFormat(device));
            return new WasapiSource(id, client, format, flow == DataFlow.Render);
        }
        catch { client.Dispose(); throw; }
    }

    private sealed class WasapiSource : IRecordingAudioSource
    {
        private readonly AudioClient client;
        private readonly AudioCaptureClient capture;
        private readonly AutoResetEvent ready = new(false);
        private readonly CaptureClockHealth clock = new();
        public string Id { get; }
        public WaveFormat WaveFormat { get; }
        public PcmFormat Format { get; }
        public WaitHandle PacketReady => ready;
        public WasapiSource(string id, AudioClient audioClient, WaveFormat waveFormat, bool loopback)
        {
            client = audioClient; Id = id; WaveFormat = waveFormat;
            try
            {
                Format = CaptureSession.Describe(waveFormat);
                Format.Validate();
                if (Format.Channels > 2) throw new NotSupportedException("当前只支持单/双声道录音格式。");
                var flags = AudioClientStreamFlags.EventCallback | (loopback ? AudioClientStreamFlags.Loopback : 0);
                client.Initialize(AudioClientShareMode.Shared, flags, 0, 0, waveFormat, Guid.Empty);
                client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
                capture = client.AudioCaptureClient;
            }
            catch { ready.Dispose(); client.Dispose(); throw; }
        }
        public void Start() => client.Start();
        public void Stop() => client.Stop();
        public RecordingBlock? Read(ulong epochQpc100ns, ulong? cutoffQpc100ns)
        {
            if (capture.GetNextPacketSize() <= 0) return null;
            var pointer = capture.GetBuffer(out int frames, out var flags, out long device, out long qpc);
            try
            {
                if (frames <= 0) return null;
                var quality = clock.Observe(device, qpc, frames, (flags & AudioClientBufferFlags.TimestampError) != 0, epochQpc100ns);
                int keep = cutoffQpc100ns is ulong cutoff
                    ? quality.QpcValid ? AudioClock.CompleteFramesBefore((ulong)qpc, cutoff, Format.SampleRate, frames) : 0
                    : frames;
                int length = checked(keep * Format.BlockAlign);
                if (length > 1024 * 1024) throw new IOException("音频包超过1MiB限额。");
                var pcm = new byte[length];
                if (keep > 0 && (flags & AudioClientBufferFlags.Silent) == 0)
                {
                    if (pointer == IntPtr.Zero) throw new IOException("非静音包为空指针。");
                    Marshal.Copy(pointer, pcm, 0, length);
                }
                return new(pcm, frames, keep, unchecked((ulong)device), unchecked((ulong)qpc), (int)flags,
                    quality.QpcValid, quality.DevicePositionValid, frames - keep);
            }
            finally { capture.ReleaseBuffer(frames); }
        }
        public void Dispose() { try { client.Dispose(); } finally { ready.Dispose(); } }
    }
}
