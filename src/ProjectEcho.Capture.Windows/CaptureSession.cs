using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ProjectEcho.Core;

namespace ProjectEcho.Capture.Windows;

/// <summary>M0 short capture. Construction/preflight never starts audio. StartAsync requires the UI's user action.</summary>
public sealed class CaptureSession : IAsyncDisposable
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CaptureResult> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _writerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEvent _stop = new(false);
    private readonly ConcurrentQueue<string> _warnings = new();
    private readonly BlockingCollection<WriteItem> _queue = new(256);
    private long _queuedBytes;
    private int _startOnce;
    private ulong _epoch;
    private DateTimeOffset _startedAt;
    private volatile Exception? _writerError;
    private float _remotePeak, _microphonePeak;
    private long _remoteFrames, _microphoneFrames;
    public Task<CaptureResult> Completion => _completed.Task;
    public event Action<CaptureLevels>? LevelsChanged;

    public Task StartAsync(CaptureOptions options)
    {
        if (Interlocked.Exchange(ref _startOnce, 1) != 0) throw new InvalidOperationException("每个 CaptureSession 只能启动一次。");
        var thread = new Thread(() => CaptureMain(options)) { IsBackground = true, Name = "ProjectEcho M0 WASAPI" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return _started.Task;
    }

    public Task<CaptureResult> StopAsync()
    {
        if (Volatile.Read(ref _startOnce) == 0) throw new InvalidOperationException("会话尚未启动。");
        _stop.Set();
        return Completion;
    }

    private void CaptureMain(CaptureOptions options)
    {
        var sources = new List<LiveSource>();
        Task<IReadOnlyList<CapturedTrack>>? writerTask = null;
        CaptureThreadScheduling.Lease? scheduling = null;
        IReadOnlyList<CapturedTrack> tracks = [];
        bool complete = true;
        string directory = Path.GetFullPath(options.SessionDirectory);
        string indexPath = Path.Combine(directory, "journal", "capture-index.jsonl");
        string scopeDescription = "预检未完成";
        _startedAt = DateTimeOffset.UtcNow;
        _epoch = Now100ns();
        ulong ended = _epoch;
        try
        {
            var preflight = SourceDiscovery.Preflight(options);
            scopeDescription = preflight.ScopeDescription;
            if (!preflight.CanStart) throw new InvalidOperationException(string.Join("\n", preflight.Errors));
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                throw new IOException("试录目录必须为空，拒绝覆盖已有原件。");
            Directory.CreateDirectory(Path.Combine(directory, "sources"));
            Directory.CreateDirectory(Path.Combine(directory, "journal"));
            sources.Add(OpenRemote(options));
            if (options.MicrophoneEnabled) sources.Add(OpenEndpoint("mic", options.MicrophoneDeviceId, DataFlow.Capture));
            if (options.Mode == SourceMode.ProcessTree) SourceDiscovery.ValidateSource(options.ProcessSource!);
            // Set epoch only after activation/format negotiation; QPC values share WASAPI's 100 ns scale.
            _epoch = Now100ns();
            _startedAt = DateTimeOffset.UtcNow;
            writerTask = Task.Run(() => WriteMain(directory, indexPath, sources));
            _writerReady.Task.GetAwaiter().GetResult();
            scheduling = CaptureThreadScheduling.TryEnter();
            if (!scheduling.Diagnostic.Registered || !scheduling.Diagnostic.PriorityApplied)
                _warnings.Enqueue("试录线程多媒体调度未启用，Win32错误：" + scheduling.Diagnostic.ErrorCode);
            CaptureLifecycle.StartAfterWriterReady(_writerReady.Task, sources.Select(source => (Action)(() =>
            {
                source.Client.Start();
                source.Started = true;
            })), () => _stop.WaitOne(0));
            _started.TrySetResult();
            RaiseLevels("正在录制；尚未收到音频包");
            var waitHandles = new WaitHandle[] { _stop }.Concat(sources.Select(x => (WaitHandle)x.Ready)).ToArray();
            ulong identityCheckAt = _epoch;
            while (!_stop.WaitOne(0) && Now100ns() - _epoch < (ulong)options.MaximumSeconds * 10_000_000)
            {
                if (_writerError is not null) throw new IOException("写盘线程失败。", _writerError);
                if (options.Mode == SourceMode.ProcessTree && Now100ns() >= identityCheckAt)
                {
                    SourceDiscovery.ValidateSource(options.ProcessSource!);
                    identityCheckAt = Now100ns() + 5_000_000;
                }
                foreach (var source in sources) Drain(source, null);
                WaitHandle.WaitAny(waitHandles, 20);
            }
        }
        catch (Exception ex)
        {
            complete = false;
            _warnings.Enqueue($"采集未完整结束：{ex.Message} (0x{ex.HResult:X8})");
            _started.TrySetException(ex);
        }
        finally
        {
            CaptureLifecycle.StopAllBeforeDrain(sources.OrderByDescending(source => source.Id == "mic")
                .Select(source => new CaptureStopActions(
                Stop: () =>
                {
                    if (!source.Started) return;
                    // The same capture thread stops each client and then reads its remaining queue.
                    // No Reset occurs before the valid pre-cutoff packets have been copied.
                    try { source.Client.Stop(); }
                    finally { source.Cutoff = Now100ns(); }
                },
                Drain: () => { if (source.Started) Drain(source, source.Cutoff); },
                Release: source.Dispose,
                Failed: (operation, ex) =>
                {
                    complete = false;
                    _warnings.Enqueue($"{source.Id} {operation}失败：{ex.Message} (0x{ex.HResult:X8})");
                })).ToArray());
            foreach (var source in sources) ended = Math.Max(ended, source.Cutoff);
            try { scheduling?.Dispose(); }
            catch (Exception ex) { _warnings.Enqueue("试录线程调度收尾失败：" + ex.GetType().Name); }
            if (scheduling?.Diagnostic is { Registered: true, Released: false, ReleaseErrorCode: { } releaseError })
                _warnings.Enqueue("试录线程多媒体调度释放未确认，Win32错误：" + releaseError);
            _queue.CompleteAdding();
            if (writerTask is not null)
            {
                try { tracks = writerTask.GetAwaiter().GetResult(); }
                catch (Exception ex) { complete = false; _warnings.Enqueue($"保存不完整，保留 .partial：{ex.Message}"); }
            }
            if (_writerError is not null) complete = false;
            while (_queue.TryTake(out var remaining)) { Interlocked.Add(ref _queuedBytes, -remaining.Bytes.Length); Array.Clear(remaining.Bytes); }
            if (sources.Any(x => x.Discarded > 0 || x.InvalidTimestamps || !x.ClockHealth.IsHealthy || x.Discontinuity)) complete = false;
            foreach (var source in sources.Where(x => x.FrameCount == 0))
                _warnings.Enqueue($"{source.Id} 未收到音频包；无包不等于静音，不宣称已录到声音。");
            if (sources.Any(x => x.FrameCount == 0)) complete = false;
            ended = Math.Max(ended, _epoch);
            var result = new CaptureResult(directory, indexPath, _startedAt, _epoch,
                checked((long)(ended - _epoch)), tracks, complete, _warnings.ToArray());
            if (writerTask is not null)
            {
                try
                {
                    var manifest = new { schemaVersion = "m0-spike-1", title = options.Title, project = "未分类", scheduling = scheduling?.Diagnostic,
                        sourceMode = options.Mode.ToString(), microphoneEnabled = options.MicrophoneEnabled,
                        selection = new {
                            scopeDescription,
                            root = options.Mode == SourceMode.ProcessTree ? options.ProcessSource?.Root : null,
                            audioProcess = options.Mode == SourceMode.ProcessTree ? options.ProcessSource?.AudioProcess : null,
                            observedAudioEndpointId = options.Mode == SourceMode.ProcessTree ? options.ProcessSource?.EndpointId : null,
                            renderEndpointId = options.Mode == SourceMode.EndpointLoopback ? options.RenderDeviceId : null,
                            endpointScopeConfirmed = options.Mode == SourceMode.EndpointLoopback && options.EndpointScopeConfirmed,
                            microphoneDeviceId = options.MicrophoneEnabled ? options.MicrophoneDeviceId : null,
                            reusableSelection = false
                        },
                        m1RecoveryImplemented = false, result };
                    File.WriteAllText(Path.Combine(directory, "capture-result.json"), JsonSerializer.Serialize(manifest,
                        new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    _warnings.Enqueue($"保存结果清单失败：{ex.Message}");
                    result = result with { Complete = false, Warnings = _warnings.ToArray() };
                }
            }
            RaiseLevels(result.Complete ? "已停止并保存原轨" : "已停止；请检查未完成/无包提示");
            _completed.TrySetResult(result);
        }
    }

    private static LiveSource OpenRemote(CaptureOptions options)
    {
        if (options.Mode == SourceMode.EndpointLoopback) return OpenEndpoint("remote", options.RenderDeviceId, DataFlow.Render);
        SourceDiscovery.ValidateSource(options.ProcessSource!);
        return ProcessLoopbackSupport.Activate(() =>
        {
            if (!ProcessLoopbackSupport.CanAttemptCurrent) throw new PlatformNotSupportedException(ProcessLoopbackSupport.Current.PreflightMessage);
            var client = AudioClient.ActivateProcessLoopbackAsync((uint)options.ProcessSource!.Root.ProcessId,
                ProcessLoopbackMode.IncludeTargetProcessTree).GetAwaiter().GetResult();
            // Process loopback has no MixFormat and cannot use AutoConvertPcm. Explicit shared float format.
            return Initialize("remote", client, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), true);
        });
    }

    private static LiveSource OpenEndpoint(string id, string? deviceId, DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = SourceDiscovery.GetExactDevice(enumerator, deviceId, flow);
        if (flow == DataFlow.Capture)
        {
            var microphone = MicrophoneFormatPolicy.OpenClient(device, initialize: true);
            try
            {
                return Initialize(id, microphone.Client, microphone.Format, false, microphone.ConversionFlags,
                    initialized: true, processingMode: microphone.ProcessingMode);
            }
            catch { microphone.Dispose(); throw; }
        }
        var client = device.CreateAudioClient();
        try
        {
            var format = client.MixFormat;
            return Initialize(id, client, format, flow == DataFlow.Render);
        }
        catch { client.Dispose(); throw; }
    }

    private static LiveSource Initialize(string id, AudioClient client, WaveFormat waveFormat, bool loopback,
        AudioClientStreamFlags conversionFlags = 0, bool initialized = false, string? processingMode = null)
    {
        var ready = new AutoResetEvent(false);
        try
        {
            PcmFormat format = Describe(waveFormat);
            format.Validate();
            if (format.Channels > 2) throw new NotSupportedException("M0 仅支持单/双声道试录与真实混音；此设备多声道格式尚未验证。");
            var flags = AudioClientStreamFlags.EventCallback | conversionFlags;
            if (loopback) flags |= AudioClientStreamFlags.Loopback;
            // Shared event-driven streams require both durations to be zero (IAudioClient::Initialize).
            // Let the engine select its buffer; a fixed 100 ms request violates that contract.
            if (!initialized) client.Initialize(AudioClientShareMode.Shared, flags, 0, 0, waveFormat, Guid.Empty);
            client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
            return new(id, client, client.AudioCaptureClient, ready, waveFormat, format, processingMode);
        }
        catch { ready.Dispose(); client.Dispose(); throw; }
    }

    private void Drain(LiveSource source, ulong? cutoff)
    {
        int guard = 0;
        while (source.Capture.GetNextPacketSize() > 0)
        {
            if (_writerError is not null) throw new IOException("写盘已经失败，停止读取新 PCM。", _writerError);
            if (++guard > 1024) throw new IOException("采集包连续积压超过上限，安全停止并报告缺口。");
            var pointer = source.Capture.GetBuffer(out int rawFrames, out var flags, out long device, out long qpc);
            try
            {
                if (rawFrames <= 0) continue;
                var clock = source.ClockHealth.Observe(device, qpc, rawFrames,
                    (flags & AudioClientBufferFlags.TimestampError) != 0, _epoch);
                bool valid = clock.QpcValid;
                int retained = rawFrames;
                if (cutoff.HasValue)
                    retained = valid ? AudioClock.CompleteFramesBefore((ulong)qpc, cutoff.Value, source.Format.SampleRate, rawFrames) : 0;
                if (retained != rawFrames)
                {
                    source.Discarded += rawFrames - retained;
                    source.DiscardReason = valid ? "crossing_frame" : "timestamp_ambiguous";
                }
                if (!valid) source.InvalidTimestamps = true;
                if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0)
                {
                    if (source.FrameCount == 0) source.InitialDiscontinuity = true;
                    else source.Discontinuity = true;
                }
                if (retained == 0) continue;
                int size = checked(retained * source.Format.BlockAlign);
                if (size > 1024 * 1024) throw new IOException("单个 WASAPI 包超过样机内存限额。");
                var bytes = new byte[size];
                if ((flags & AudioClientBufferFlags.Silent) == 0)
                {
                    if (pointer == IntPtr.Zero) throw new IOException("非静音包返回空指针。");
                    Marshal.Copy(pointer, bytes, 0, size);
                }
                if (Interlocked.Add(ref _queuedBytes, size) > 32L * 1024 * 1024)
                {
                    Interlocked.Add(ref _queuedBytes, -size);
                    Array.Clear(bytes);
                    throw new IOException("写盘积压超过 32 MiB，停止采集；未静默丢弃或扩大来源。");
                }
                var packet = new CapturedPacket(source.FrameCount, retained, rawFrames, unchecked((ulong)device),
                    unchecked((ulong)qpc), (int)flags, valid, clock.DevicePositionValid);
                if (!_queue.TryAdd(new(source.Id, bytes, packet)))
                {
                    Interlocked.Add(ref _queuedBytes, -size);
                    Array.Clear(bytes);
                    throw new IOException("写盘队列已满或关闭，停止采集并报告缺口。");
                }
                source.FrameCount += retained;
            }
            finally { source.Capture.ReleaseBuffer(rawFrames); }
        }
    }

    private IReadOnlyList<CapturedTrack> WriteMain(string directory, string indexPath, List<LiveSource> sources)
    {
        var writers = new Dictionary<string, TrackWriter>();
        try
        {
            using var journalStream = new FileStream(indexPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var journal = new StreamWriter(journalStream, new UTF8Encoding(false), 4096, true);
            long sequence = 0;
            void Append(object value)
            {
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
                string hash = Convert.ToHexStringLower(SHA256.HashData(data));
                journal.WriteLine($"{{\"schemaVersion\":1,\"isSynthetic\":false,\"sequence\":{sequence++},\"data\":{Encoding.UTF8.GetString(data)},\"sha256\":\"{hash}\"}}");
            }
            Append(new { type = "session", meetingId = Path.GetFileName(directory), epochQpc100ns = U(_epoch),
                startedAtUtc = _startedAt, displayTimeZone = TimeZoneInfo.Local.Id });
            foreach (var source in sources)
            {
                var track = new TrackWriter(directory, source);
                writers.Add(source.Id, track);
            }
            // No AudioClient.Start until all original files and the initial index are writable.
            foreach (var track in writers.Values) track.Stream.Flush(true);
            journal.Flush();
            journalStream.Flush(true);
            _writerReady.TrySetResult();
            long flushAt = Stopwatch.GetTimestamp();
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                try
                {
                    var track = writers[item.Id];
                    var packet = item.Packet;
                    if (!track.SegmentWritten)
                    {
                        Append(Segment(track.Source, packet));
                        track.SegmentWritten = true;
                    }
                    track.Writer.Write(item.Bytes, 0, item.Bytes.Length);
                    track.Packets.Add(packet);
                    var flags = PacketFlags(packet.Flags);
                    Append(new { type = "packet", segmentId = item.Id + "-1", chunkId = item.Id + "-1", path = track.RelativePath,
                        deviceFramePosition = U(packet.DevicePosition), devicePositionValid = packet.DevicePositionValid,
                        qpcPosition100ns = U(packet.QpcPosition100ns),
                        rawFrameCount = packet.RawFrameCount, flags, retainedFrameOffset = 0, frameCount = packet.FrameCount,
                        chunkFrameOffset = packet.WaveFrameOffset, sessionStartUs = SessionUs(packet),
                        timestampQuality = packet.GetTimestampQuality() });
                    track.LastPacketSequence = sequence - 1;
                    float peak = Peak(item.Bytes, track.Source.Format);
                    if (item.Id == "remote") { _remotePeak = peak; _remoteFrames += packet.FrameCount; }
                    else { _microphonePeak = peak; _microphoneFrames += packet.FrameCount; }
                    RaiseLevels((packet.Flags & (int)AudioClientBufferFlags.Silent) != 0 ? "收到明确 SILENT 静音包" : "收到音频包（电平仅对应所选来源）");
                    if (Stopwatch.GetElapsedTime(flushAt).TotalSeconds >= 1)
                    {
                        FlushCheckpoint();
                        flushAt = Stopwatch.GetTimestamp();
                    }
                }
                finally { Interlocked.Add(ref _queuedBytes, -item.Bytes.Length); Array.Clear(item.Bytes); }
            }
            foreach (var track in writers.Values)
            {
                if (!track.SegmentWritten) Append(Segment(track.Source, null));
                var source = track.Source;
                if (source.InvalidTimestamps) _warnings.Enqueue($"{source.Id} 包存在无效时间戳，不能宣称同步完整。");
                if (source.ClockHealth.HasInvalidDevicePosition) _warnings.Enqueue($"{source.Id} 原始设备帧位置停滞、回退或不可用；已保留 API 原值并单独标记 QPC 质量，本次时钟完整性未通过。");
                if (source.InitialDiscontinuity) _warnings.Enqueue($"{source.Id} 首包报告 DATA_DISCONTINUITY，保留标志；可能为启动状态转换，尚无前包可核验连续性。");
                if (source.Discontinuity) _warnings.Enqueue($"{source.Id} WASAPI 报告 DATA_DISCONTINUITY，须试听/调查缺口。");
                if (source.Discarded > 0) _warnings.Enqueue($"{source.Id} 停止边界舍弃 {source.Discarded} 帧，原因 {source.DiscardReason}。");
                Append(new { type = "boundary", requestId = "m0-stop-" + source.Id, operation = "stop",
                    segmentId = source.Id + "-1", effectiveSessionUs = source.Cutoff >= _epoch ? (long)((source.Cutoff - _epoch) / 10) : 0,
                    firstForbiddenDeviceFrame = (string?)null, acceptedThroughSequence = track.LastPacketSequence,
                    discardedFrameCount = source.Discarded, discardReason = source.DiscardReason });
            }
            FlushCheckpoint();
            foreach (var track in writers.Values) track.Finish();
            return writers.Values.Select(x => new CapturedTrack(x.Source.Id, x.FinalPath, x.Source.Format,
                x.Packets.Sum(p => (long)p.FrameCount), x.Packets.ToArray(), ProcessingMode: x.Source.ProcessingMode)).ToArray();

            void FlushCheckpoint()
            {
                foreach (var track in writers.Values) { track.Writer.Flush(); track.Stream.Flush(true); }
                journal.Flush(); journalStream.Flush(true);
                long covered = sequence - 1;
                Append(new { type = "checkpoint", coveredThroughSequence = covered,
                    chunks = writers.Values.Select(x => new { chunkId = x.Source.Id + "-1", path = x.RelativePath,
                        committedFrameCount = x.Packets.Sum(p => (long)p.FrameCount),
                        committedPcmBytes = x.Packets.Sum(p => (long)p.FrameCount) * x.Source.Format.BlockAlign,
                        dataOffsetBytes = x.DataOffset }).ToArray() });
                journal.Flush(); journalStream.Flush(true);
            }
        }
        catch (Exception ex) { _writerError = ex; _stop.Set(); _writerReady.TrySetException(ex); throw; }
        finally
        {
            foreach (var track in writers.Values)
            {
                try { track.Dispose(); }
                catch (Exception ex) { _writerError = ex; _warnings.Enqueue($"{track.Source.Id} 文件收尾失败：{ex.Message}"); }
            }
            while (_queue.TryTake(out var remaining)) { Interlocked.Add(ref _queuedBytes, -remaining.Bytes.Length); Array.Clear(remaining.Bytes); }
        }
    }

    private object Segment(LiveSource source, CapturedPacket? packet) => new
    {
        type = "segment", segmentId = source.Id + "-1", sourceId = source.Id, processingMode = source.ProcessingMode,
        format = new { sampleRate = source.Format.SampleRate, channels = source.Format.Channels,
            encoding = source.Format.IsFloat ? "float32le" : $"pcm_s{source.Format.BitsPerSample}le",
            blockAlign = source.Format.BlockAlign, channelMask = source.Format.ChannelMask },
        anchor = new { deviceFramePosition = packet is { TimestampValid: true, DevicePositionValid: true } ? U(packet.DevicePosition) : null,
            qpcPosition100ns = packet is { TimestampValid: true } ? U(packet.QpcPosition100ns) : null,
            sessionStartUs = packet is null ? 0 : SessionUs(packet),
            timestampQuality = packet?.GetTimestampQuality() ?? "invalid" }
    };
    private long SessionUs(CapturedPacket packet) => packet.TimestampValid ? Math.Max(0, AudioClock.SessionMicroseconds(packet.QpcPosition100ns, _epoch)) : 0;
    private static string U(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static ulong Now100ns() => checked((ulong)AudioClock.CounterTo100ns(Stopwatch.GetTimestamp(), Stopwatch.Frequency));
    private static string[] PacketFlags(int flags)
    {
        var result = new List<string>();
        if ((flags & (int)AudioClientBufferFlags.Silent) != 0) result.Add("silent");
        if ((flags & (int)AudioClientBufferFlags.DataDiscontinuity) != 0) result.Add("data_discontinuity");
        if ((flags & (int)AudioClientBufferFlags.TimestampError) != 0) result.Add("timestamp_error");
        return result.ToArray();
    }
    private void RaiseLevels(string status)
    {
        try { LevelsChanged?.Invoke(new(_remotePeak, _microphonePeak, _remoteFrames, _microphoneFrames, status)); }
        catch { /* A UI status handler must never interrupt capture or writer finalization. */ }
    }
    internal static PcmFormat Describe(WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        uint mask = 0;
        if (format is WaveFormatExtensible extensible)
        {
            isFloat = extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
            if (!isFloat && extensible.SubFormat != new Guid("00000001-0000-0010-8000-00aa00389b71"))
                throw new NotSupportedException("不支持的原轨编码。");
            mask = unchecked((uint)extensible.ChannelMask);
        }
        else if (!isFloat && format.Encoding != WaveFormatEncoding.Pcm) throw new NotSupportedException("不支持的原轨编码。");
        return new(format.SampleRate, format.Channels, format.BitsPerSample, isFloat, format.BlockAlign, mask);
    }
    private static float Peak(byte[] bytes, PcmFormat format)
    {
        float peak = 0;
        int width = format.BitsPerSample / 8;
        for (int i = 0; i < bytes.Length; i += width)
        {
            float value = format.IsFloat ? BitConverter.ToSingle(bytes, i) : width switch
            {
                2 => BitConverter.ToInt16(bytes, i) / 32768f,
                3 => ((bytes[i] | bytes[i + 1] << 8 | bytes[i + 2] << 16) << 8 >> 8) / 8388608f,
                4 => BitConverter.ToInt32(bytes, i) / 2147483648f,
                _ => 0
            };
            if (float.IsFinite(value)) peak = Math.Max(peak, Math.Abs(value));
        }
        return Math.Min(1, peak);
    }
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _startOnce) != 0) await StopAsync().ConfigureAwait(false);
        _stop.Dispose(); _queue.Dispose();
    }
    private sealed record WriteItem(string Id, byte[] Bytes, CapturedPacket Packet);
    private sealed class LiveSource(string id, AudioClient client, AudioCaptureClient capture, AutoResetEvent ready,
        WaveFormat waveFormat, PcmFormat format, string? processingMode = null) : IDisposable
    {
        public string Id { get; } = id;
        public AudioClient Client { get; } = client;
        public AudioCaptureClient Capture { get; } = capture;
        public AutoResetEvent Ready { get; } = ready;
        public WaveFormat WaveFormat { get; } = waveFormat;
        public PcmFormat Format { get; } = format;
        public string? ProcessingMode { get; } = processingMode;
        public CaptureClockHealth ClockHealth { get; } = new();
        public bool Started, InvalidTimestamps, Discontinuity, InitialDiscontinuity;
        public long FrameCount, Discarded;
        public ulong Cutoff;
        public string? DiscardReason;
        public void Dispose() { try { Client.Dispose(); } finally { Ready.Dispose(); } }
    }
    private sealed class TrackWriter : IDisposable
    {
        private readonly CaptureWaveFile _file;
        public LiveSource Source { get; }
        public FileStream Stream => _file.Stream;
        public WaveFileWriter Writer => _file.Writer;
        public string FinalPath { get; }
        public string RelativePath { get; }
        public long DataOffset => _file.DataOffset;
        public List<CapturedPacket> Packets { get; } = [];
        public bool SegmentWritten;
        public long? LastPacketSequence;
        private bool _closed;
        public TrackWriter(string directory, LiveSource source)
        {
            Source = source;
            RelativePath = $"sources/{source.Id}.wav";
            FinalPath = Path.Combine(directory, "sources", source.Id + ".wav");
            _file = new CaptureWaveFile(FinalPath + ".partial", source.WaveFormat);
        }
        public void Finish()
        {
            Dispose();
            using (var reader = new WaveFileReader(FinalPath + ".partial"))
                if (reader.Length != Packets.Sum(x => (long)x.FrameCount) * Source.Format.BlockAlign)
                    throw new IOException("WAV 数据长度与 packet 索引不一致。");
            File.Move(FinalPath + ".partial", FinalPath, false);
        }
        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            _file.Dispose();
        }
    }
}
