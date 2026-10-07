using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.Wave;
using ProjectEcho.Core;

namespace ProjectEcho.Capture.Windows;

/// <summary>One capture thread and one bounded writer. All controls take effect on the capture thread.</summary>
public sealed class RecordingSession : IAsyncDisposable
{
    private readonly IRecordingSourceFactory factory;
    private readonly Func<ulong> now;
    private readonly Func<string, long> freeBytes;
    private readonly bool synthetic;
    private readonly ConcurrentQueue<Control> controls = new();
    private readonly object controlGate = new();
    private readonly Dictionary<string, Control> submittedControls = new(StringComparer.Ordinal);
    private readonly BlockingCollection<Write> writes = new(256);
    private readonly AutoResetEvent wake = new(false);
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RecordingResult> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> warnings = new();
    private readonly List<Live> sources = [];
    private readonly List<RecordedSourceDiagnostics> sourceDiagnostics = [];
    private WaitHandle[]? sourceWaitHandles;
    private long queuedBytes, savedUs, duration100ns, observedSessionUs;
    private long remoteFrames, micFrames;
    private int startOnce, stopping, nextSegment;
    private string stopId = "stop";
    private volatile Exception? writerError;
    private bool paused, micEnabled, captureFault;
    private ulong epoch, lastIteration;
    private RecordingTimeClock? recordingClock;
    private long available;
    private RecordingOptions options = null!;
    private RecordingAutoStopMonitor? autoStopMonitor;
    private IRecordingProcessLifetime? selectedProcess;
    private RecordingAutoStopInfo? autoStopInfo;
    private RecordingAutoStopOptions autoStopSettings = new();
    private string? fixedMicrophoneDeviceId;
    private string remoteFault = "", micFault = "";
    private MicrophonePreparation? microphonePreparation;
    private Control? pendingMicrophoneControl;
    private long microphonePreparationStarted;
    public Task<RecordingResult> Completion => completed.Task;
    public event Action<RecordingHealth>? HealthChanged;
    public event Action<RecordingAck>? Acknowledged;
    public event Action<RecordingAutoStopInfo>? AutoStopping;

    public RecordingSession() : this(new RecordingSourceFactory(), Now100ns,
        path => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace, synthetic: false) { }
    internal RecordingSession(IRecordingSourceFactory sourceFactory, Func<ulong> clock, Func<string, long> disk, bool synthetic = true)
    { factory = sourceFactory; now = clock; freeBytes = disk; this.synthetic = synthetic; }

    public Task StartAsync(RecordingOptions selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(selection.Selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.Selection.SessionDirectory);
        selection.AutoStop?.Validate();
        if (Interlocked.Exchange(ref startOnce, 1) != 0) throw new InvalidOperationException("录音会话不可复用。");
        options = selection;
        autoStopSettings = selection.AutoStop ?? new();
        fixedMicrophoneDeviceId = selection.Selection.MicrophoneDeviceId;
        var thread = new Thread(Main) { IsBackground = true, Name = "ProjectEcho M1 capture" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return started.Task;
    }
    public Task<RecordingResult> StopAsync(string requestId = "stop")
    {
        if (Volatile.Read(ref startOnce) == 0) throw new InvalidOperationException("会话尚未启动。");
        lock (controlGate) { if (Volatile.Read(ref stopping) == 0) { stopId = requestId; Volatile.Write(ref stopping, 1); } }
        wake.Set();
        return completed.Task;
    }
    public Task<RecordingAck> ControlAsync(string requestId, string operation, string? deviceId = null,
        RecordingAutoStopOptions? automatic = null)
    {
        lock (controlGate)
        {
            if (submittedControls.TryGetValue(requestId, out var prior))
                return prior.Operation == operation && prior.DeviceId == deviceId && prior.Automatic == automatic ? prior.Done.Task
                    : Task.FromException<RecordingAck>(new InvalidOperationException("同一请求ID不能用于不同操作。"));
            if (Volatile.Read(ref startOnce) == 0 || Volatile.Read(ref stopping) != 0 || completed.Task.IsCompleted || submittedControls.Count >= 4096)
                return Task.FromException<RecordingAck>(new InvalidOperationException("会话已停止或控制次数达到限额。"));
            var control = new Control(requestId, operation, deviceId, automatic);
            submittedControls.Add(requestId, control);
            controls.Enqueue(control); wake.Set();
            return control.Done.Task;
        }
    }

    private void Main()
    {
        Task<M1StorageResult>? writer = null;
        CaptureThreadScheduling.Lease? scheduling = null;
        long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread(), maximumLoopInterval = 0;
        int gen0AtStart = GC.CollectionCount(0), gen1AtStart = GC.CollectionCount(1), gen2AtStart = GC.CollectionCount(2);
        TimeSpan gcPauseAtStart = GC.GetTotalPauseDuration();
        CaptureValidationWorker? validation = null;
        string directory = options.Selection.SessionDirectory;
        ulong end = 0;
        try
        {
            epoch = now();
            recordingClock = new(epoch);
            directory = Path.GetFullPath(directory);
            factory.Preflight(options);
            ValidateDirectory(directory);
            if (options.AutoStop is { Enabled: true } automatic)
            {
                autoStopMonitor = new(automatic);
                if (options.Selection.Mode == SourceMode.ProcessTree)
                    selectedProcess = factory.WatchProcess(options.Selection)
                        ?? throw new InvalidOperationException("无法监测所选应用进程，尚未开始录音。");
            }
            epoch = now(); lastIteration = epoch;
            recordingClock = new(epoch);
            micEnabled = options.Selection.MicrophoneEnabled;
            var writerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            writer = Task.Run(() => WriterMain(directory, writerReady));
            writerReady.Task.GetAwaiter().GetResult();
            validation = new CaptureValidationWorker(segmentId =>
            {
                long remaining = 0; Exception? diskFailure = null, identityFailure = null;
                try { remaining = freeBytes(directory); } catch (Exception ex) { diskFailure = ex; }
                if (diskFailure is null && segmentId is not null && validation?.IsStopped != true)
                {
                    try { factory.ValidateRemote(options.Selection); } catch (Exception ex) { identityFailure = ex; }
                }
                return new(segmentId, remaining, diskFailure, identityFailure);
            });
            if (!synthetic && options.EnableMultimediaScheduling)
            {
                scheduling = CaptureThreadScheduling.TryEnter();
                if (!scheduling.Diagnostic.Registered || !scheduling.Diagnostic.PriorityApplied)
                    warnings.Enqueue("录音线程多媒体调度未启用，Win32错误：" + scheduling.Diagnostic.ErrorCode);
            }
            OpenSources();
            started.TrySetResult();
            ulong checkAt = epoch, healthAt = epoch;
            while (Volatile.Read(ref stopping) == 0 && recordingClock.Elapsed100ns(now()) < (long)options.MaximumSeconds * 10_000_000)
            {
                if (writerError is not null) throw new IOException("原轨写入失败。", writerError);
                ulong current = now();
                maximumLoopInterval = Math.Max(maximumLoopInterval, checked((long)(current - lastIteration)));
                ObserveRecordingTime(current);
                if (current - lastIteration > 20_000_000 && !paused)
                {
                    CancelPendingMicrophone("检测到系统/调度时间缺口，等待中的麦克风未启动。");
                    ulong pauseBoundary = CloseSources(sources.ToArray(), "pause", "system-gap");
                    paused = true;
                    warnings.Enqueue("检测到系统/调度时间缺口；已暂停，需明确恢复。未更改电源策略。");
                    Event("system_gap", "system-gap", Us(current));
                    NotifyAck(new("system-gap", "pause", true, micEnabled, Us(pauseBoundary), "系统/调度缺口，已暂停"));
                }
                lastIteration = current;
                while (controls.TryDequeue(out var control)) Execute(control);
                if (Volatile.Read(ref stopping) != 0) break;
                if (selectedProcess?.HasExited == true) { RequestAutomaticStop("source_process_exited"); break; }
                CompletePendingMicrophone();
                if (validation.TryTake(out var validationResult))
                {
                    if (validationResult!.Elapsed >= TimeSpan.FromSeconds(2))
                        throw new IOException("来源身份/磁盘检查超过2秒才返回；未采用迟到结果，停止并保存已有原轨。");
                    if (validationResult.DiskError is { } diskError) throw new IOException("磁盘余量检查失败，停止并保存已有原轨。", diskError);
                    available = validationResult.AvailableBytes;
                    long bytesPerSecond = sources.Sum(x => (long)x.Source.Format.SampleRate * x.Source.Format.BlockAlign);
                    if (available < 64L * 1024 * 1024 + bytesPerSecond * 2) throw new IOException("磁盘余量低于安全预留，停止并保存已有原轨。");
                    var validatedSource = sources.FirstOrDefault(x => x.SegmentId == validationResult.RemoteSegmentId);
                    if (validatedSource is not null && validationResult.RemoteError is { } sourceError)
                    {
                        if (selectedProcess?.HasExited == true) { RequestAutomaticStop("source_process_exited"); break; }
                        FailSource(validatedSource, sourceError);
                    }
                }
                if (validation.HasTimedOut(TimeSpan.FromSeconds(2)))
                    throw new IOException("来源身份/磁盘检查超过2秒未返回；为保持明确采集范围，停止并保存已有原轨。");
                if (current >= checkAt && validation.TryRequest(paused ? null : sources.FirstOrDefault(x => x.Source.Id == "remote")?.SegmentId))
                    checkAt = current + 5_000_000;
                ServiceStartedSources();
                // Drain first: a brief sound within the last service interval must reset the timer.
                if (autoStopMonitor?.IsDue(now(), paused, pendingMicrophoneControl is not null) == true)
                { RequestAutomaticStop("no_audio_activity"); break; }
                if (current >= healthAt) { ulong sampled = now(); RaiseHealth(sampled); healthAt = sampled + 2_000_000; }
                WaitForSourceOrControl();
            }
        }
        catch (Exception ex)
        {
            captureFault = true;
            warnings.Enqueue("录音降级：" + ex.Message);
            started.TrySetException(ex);
        }
        finally
        {
            lock (controlGate) Volatile.Write(ref stopping, 1);
            CancelPendingMicrophone("会话已停止，麦克风未启动。");
            validation?.Dispose();
            end = now();
            try { end = CloseSources(sources.ToArray(), "stop", stopId); }
            catch (Exception ex) { captureFault = true; warnings.Enqueue("停采收尾异常：" + ex.Message); }
            selectedProcess?.Dispose(); selectedProcess = null;
            try { scheduling?.Dispose(); }
            catch (Exception ex) { warnings.Enqueue("录音线程调度收尾失败：" + ex.GetType().Name); }
            if (scheduling?.Diagnostic is { Registered: true, Released: false, ReleaseErrorCode: { } releaseError })
                warnings.Enqueue("录音线程多媒体调度释放未确认，Win32错误：" + releaseError);
            end = Math.Max(epoch, end);
            Interlocked.Exchange(ref duration100ns, recordingClock?.Elapsed100ns(end) ?? checked((long)(end - epoch)));
            ObserveRecordingTime(end);
            while (controls.TryDequeue(out var control)) control.Done.TrySetException(new OperationCanceledException("会话已停止。"));
            M1StorageResult? stored = null;
            if (writer is not null)
            {
                try
                {
                    // This is the last writer item; every pre-cutoff packet precedes it.
                    Enqueue(new(store => store.WriteEvent("stop", stopId, Us(end))));
                }
                catch (Exception ex) { captureFault = true; warnings.Enqueue("停止事件写入失败：" + ex.Message); }
                finally { writes.CompleteAdding(); }
                try { stored = writer.GetAwaiter().GetResult(); }
                catch (Exception ex) { warnings.Enqueue("保存失败，原件保留供恢复：" + ex.Message); }
            }
            else writes.CompleteAdding();
            while (writes.TryTake(out var remaining)) Clear(remaining.Pcm);
            var result = new RecordingResult(directory, stored?.ManifestPath ?? Path.Combine(directory, "meeting.json"),
                stored?.StorageComplete == true && writerError is null, Us(end),
                warnings.Concat(stored?.Warnings ?? []).Distinct().ToArray(),
                new(scheduling?.Diagnostic, maximumLoopInterval, GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart,
                    GC.CollectionCount(0) - gen0AtStart, GC.CollectionCount(1) - gen1AtStart, GC.CollectionCount(2) - gen2AtStart,
                    (GC.GetTotalPauseDuration() - gcPauseAtStart).TotalMilliseconds, sourceDiagnostics.ToArray()), autoStopInfo);
            // A writer validation failure may prevent meeting.json from being written.
            // Keep the technical failure separately so the library can explain why the
            // preserved partial originals need inspection after the Host has exited.
            if (writer is not null)
            {
                try { M1Storage.WriteAtomicJson(Path.Combine(directory, "recording-result.json"), result); }
                catch { /* Never turn a secondary diagnostics write into a failed shutdown. */ }
            }
            completed.TrySetResult(result);
        }
    }

    private M1StorageResult WriterMain(string directory, TaskCompletionSource ready)
    {
        try
        {
            using var store = new M1Storage(directory, epoch, DateTimeOffset.UtcNow, options.Selection.Title, synthetic,
                timelinePolicy: RecordingTimeline.ExcludePaused);
            M1Storage.WriteAtomicJson(Path.Combine(directory, "selection.json"), new
            {
                schemaVersion = "m1-selection-1", isSynthetic = synthetic, sourceMode = options.Selection.Mode.ToString(),
                root = options.Selection.Mode == SourceMode.ProcessTree ? options.Selection.ProcessSource?.Root : null,
                audioProcess = options.Selection.Mode == SourceMode.ProcessTree ? options.Selection.ProcessSource?.AudioProcess : null,
                renderEndpointId = options.Selection.Mode == SourceMode.EndpointLoopback ? options.Selection.RenderDeviceId : null,
                endpointScopeConfirmed = options.Selection.Mode == SourceMode.EndpointLoopback && options.Selection.EndpointScopeConfirmed,
                initialMicrophoneEnabled = options.Selection.MicrophoneEnabled,
                selectedMicrophoneDeviceId = options.Selection.MicrophoneDeviceId, reusableSelection = false,
                autoStop = options.AutoStop ?? new RecordingAutoStopOptions(),
                autoStopMicrophoneActivity = new { version = "m3n", method = "per-channel-dc-removed-10ms-rms", thresholdDbfs = -50 }
            });
            ready.TrySetResult();
            DateTimeOffset? lastCheckpoint = null;
            while (!writes.IsCompleted)
            {
                if (writes.TryTake(out var item, 100))
                {
                    try { item.Action(store); }
                    finally { Clear(item.Pcm); }
                }
                store.SetObservedSessionUs(Interlocked.Read(ref observedSessionUs));
                store.CheckpointIfDue();
                if (store.LastCheckpointUtc != lastCheckpoint)
                {
                    lastCheckpoint = store.LastCheckpointUtc;
                    Interlocked.Exchange(ref savedUs, store.LastCheckpointSessionUs);
                }
            }
            store.SetObservedSessionUs(Interlocked.Read(ref observedSessionUs));
            return store.Finish(Interlocked.Read(ref duration100ns), !captureFault, warnings.Distinct().ToArray());
        }
        catch (Exception ex)
        {
            writerError = ex; ready.TrySetException(ex); Interlocked.Exchange(ref stopping, 1); wake.Set(); throw;
        }
    }

    private void OpenSources()
    {
        IRecordingAudioSource? remote = null, microphone = null;
        try
        {
            remote = factory.OpenRemote(options.Selection);
            if (micEnabled)
            {
                BeginMicrophonePreparation(fixedMicrophoneDeviceId ?? throw new InvalidOperationException("未选择固定麦克风。"));
                IPreparedRecordingSource? prepared;
                Exception? failure;
                while (!microphonePreparation!.TryTake(out prepared, out failure))
                {
                    if (Volatile.Read(ref stopping) != 0) throw new OperationCanceledException("启动已取消。");
                    if (Stopwatch.GetElapsedTime(microphonePreparationStarted) > TimeSpan.FromSeconds(10))
                        throw new TimeoutException("麦克风初始化超过10秒；尚未启动采集。");
                    WaitForSourceOrControl();
                }
                microphonePreparation = null;
                using (prepared)
                {
                    if (failure is not null) throw new IOException("麦克风初始化失败：" + failure.Message, failure);
                    if (Volatile.Read(ref stopping) != 0) throw new OperationCanceledException("启动已取消。");
                    microphone = prepared!.CompleteOnCaptureThread();
                }
            }
            // Both native clients and both disk segments must be prepared before
            // either starts. Slow microphone activation cannot fill an unread
            // remote buffer during initial Start or Resume.
            if (Volatile.Read(ref stopping) != 0) throw new OperationCanceledException("启动已取消。");
            var readyRemote = remote; remote = null; Open(readyRemote, start: false);
            if (microphone is not null) { var readyMic = microphone; microphone = null; Open(readyMic, start: false); }
            if (Volatile.Read(ref stopping) != 0) throw new OperationCanceledException("启动已取消。");
            var preparedSources = sources.Where(source => !source.Started).ToArray();
            bool resuming = recordingClock!.IsPaused;
            ulong resumeAt = now();
            foreach (var live in preparedSources)
            {
                if (resuming) live.ActiveStartBoundary = resumeAt;
                StartPreparedSource(live);
                if (resuming && recordingClock.IsPaused)
                {
                    // Commit only once the first Start succeeds. All disk files
                    // are already ready; queue the mapping before any packet is
                    // serviced, without waiting for I/O while capture is active.
                    ulong excluded = recordingClock.Resume(resumeAt);
                    foreach (var prepared in preparedSources) SetSegmentOffset(prepared, excluded);
                }
            }
            autoStopMonitor?.Reset(now()); // Preparation and paused time never consume the new interval.
        }
        catch
        {
            CancelPendingMicrophone("启动已取消，麦克风未启动。");
            // If an actual source started before another source failed, retain
            // its valid prefix, then freeze again. Never rewind recorded audio.
            CloseSources(sources.ToArray(), paused ? "pause" : "stop", "start-failed");
            throw;
        }
        finally { microphone?.Dispose(); remote?.Dispose(); }
    }
    private void Open(IRecordingAudioSource source, bool start = true)
    {
        var live = new Live(source, source.Id + "-" + (++nextSegment))
            { ExcludedPause100ns = recordingClock?.Excluded100ns ?? 0 };
        sources.Add(live);
        sourceWaitHandles = null;
        try
        {
        long actualBytesPerSecond = sources.Sum(x => (long)x.Source.Format.SampleRate * x.Source.Format.BlockAlign);
        long actualBudget = checked(actualBytesPerSecond * options.ExpectedMinutes * 60 * 4 + 64L * 1024 * 1024);
        available = freeBytes(options.Selection.SessionDirectory);
        if (available < actualBudget)
            throw new IOException("实际协商格式所需原轨及派生文件预算超过磁盘可用空间。");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new(store =>
        {
            try { store.AddSegment(live.SegmentId, source.Id, source.WaveFormat, processingMode: source.ProcessingMode,
                excludedPause100ns: live.ExcludedPause100ns); store.Checkpoint(); ready.TrySetResult(); }
            catch (Exception ex) { ready.TrySetException(ex); throw; }
        }));
        // The writer task fault must also wake an item waiting behind an earlier failed write.
        while (!ready.Task.IsCompleted)
        {
            if (writerError is not null) throw new IOException("writer未能准备来源。", writerError);
            // Preparing another track's files must not starve a running source.
            ServiceStartedSources();
            WaitForSourceOrControl();
        }
        ready.Task.GetAwaiter().GetResult();
        live.Prepared = true;
        if (start)
        {
            // Late mic activation must not retain a driver's pre-activation prefix.
            live.ActiveStartBoundary = now();
            StartPreparedSource(live);
        }
        }
        catch
        {
            try { CloseSources([live], "stop", "open-failed"); }
            catch (Exception ex) { warnings.Enqueue("启动来源清理失败：" + ex.Message); Interlocked.Exchange(ref stopping, 1); }
            throw;
        }
    }

    private void SetSegmentOffset(Live live, ulong excluded)
    {
        if (live.OffsetAssigned) throw new InvalidOperationException("录音段不能重复更改时间轴映射。");
        live.ExcludedPause100ns = excluded; live.OffsetAssigned = true;
        Enqueue(new(store => store.SetSegmentTimelineOffset(live.SegmentId, excluded)));
    }

    private void StartPreparedSource(Live live)
    {
        if (Volatile.Read(ref stopping) != 0) throw new OperationCanceledException("启动已取消。");
        live.Source.Start(); live.Started = true; sourceWaitHandles = null;
        lastIteration = now();
    }

    private void BeginMicrophonePreparation(string deviceId)
    {
        if (microphonePreparation is { HasExited: false })
            throw new InvalidOperationException("上次麦克风初始化尚未退出；不会重复创建或更换设备。");
        microphonePreparationStarted = Stopwatch.GetTimestamp();
        microphonePreparation = new(() => factory.PrepareMicrophone(deviceId));
    }

    private void CancelPendingMicrophone(string reason)
    {
        microphonePreparation?.Cancel();
        pendingMicrophoneControl?.Done.TrySetException(new OperationCanceledException(reason));
        pendingMicrophoneControl = null;
    }

    private void CompletePendingMicrophone()
    {
        if (pendingMicrophoneControl is not { } control) return;
        if (paused || Volatile.Read(ref stopping) != 0)
        {
            CancelPendingMicrophone("会话已暂停或停止，等待中的麦克风未启动。");
            return;
        }
        if (Stopwatch.GetElapsedTime(microphonePreparationStarted) > TimeSpan.FromSeconds(10))
        {
            CancelPendingMicrophone("麦克风初始化超过10秒；麦克风未启动，现有录音继续。");
            warnings.Enqueue("控制未完成：mic_on：麦克风初始化超过10秒。");
            return;
        }
        if (!microphonePreparation!.TryTake(out var prepared, out var failure)) return;
        microphonePreparation = null; pendingMicrophoneControl = null;
        using (prepared)
        {
            try
            {
                if (failure is not null) throw new IOException("麦克风初始化失败：" + failure.Message, failure);
                if (Volatile.Read(ref stopping) != 0) throw new OperationCanceledException("会话已停止，麦克风未启动。");
                Open(prepared!.CompleteOnCaptureThread());
                fixedMicrophoneDeviceId ??= control.DeviceId;
                micEnabled = true; micFault = "";
                AcknowledgeControl(control);
            }
            catch (Exception ex)
            {
                control.Done.TrySetException(ex);
                warnings.Enqueue("控制未完成：mic_on：" + ex.Message);
            }
        }
    }

    private void Execute(Control control)
    {
        try
        {
            string? message = null;
            ulong? boundaryAt = null;
            switch (control.Operation)
            {
                case "auto_stop_settings":
                    var automatic = control.Automatic ?? throw new InvalidOperationException("缺少自动结束设置。");
                    automatic.Validate();
                    IRecordingProcessLifetime? newProcess = null;
                    if (automatic.Enabled && selectedProcess is null && options.Selection.Mode == SourceMode.ProcessTree)
                        newProcess = factory.WatchProcess(options.Selection) ?? throw new InvalidOperationException("无法监测所选应用进程，自动结束设置未改变。");
                    if (!automatic.Enabled) { selectedProcess?.Dispose(); selectedProcess = null; }
                    else selectedProcess ??= newProcess;
                    autoStopSettings = automatic;
                    autoStopMonitor = automatic.Enabled ? new(automatic) : null;
                    autoStopMonitor?.Reset(now());
                    foreach (var live in sources) { live.MicrophoneActivity = null; live.ActivityBlock = null; live.ActivityFrames = 0; }
                    break;
                case "pause":
                    CancelPendingMicrophone("已暂停，等待中的麦克风未启动。");
                    if (!paused) { boundaryAt = CloseSources(sources.ToArray(), "pause", control.Id); paused = true; }
                    break;
                case "resume":
                    if (paused) { OpenSources(); paused = false; remoteFault = ""; micFault = ""; }
                    break;
                case "mic_off":
                    CancelPendingMicrophone("麦克风开启已取消；没有启动或采集麦克风 PCM。");
                    boundaryAt = CloseSources(sources.Where(x => x.Source.Id == "mic").ToArray(), "mic_off", control.Id);
                    micEnabled = false;
                    break;
                case "mic_on":
                    if (paused) throw new InvalidOperationException("请先恢复录音，再明确开启麦克风。");
                    if (string.IsNullOrWhiteSpace(control.DeviceId) ||
                        (fixedMicrophoneDeviceId is not null && control.DeviceId != fixedMicrophoneDeviceId))
                        throw new InvalidOperationException("仅允许本场明确选定的固定麦克风；不会替换设备。");
                    if (!micEnabled)
                    {
                        BeginMicrophonePreparation(control.DeviceId);
                        // Initial MicOff/null is not a permanent prohibition: the first
                        // explicit MicOn selects the exact device. A failed open does not
                        // commit the selection, and later toggles/resumes never replace it.
                        pendingMicrophoneControl = control;
                        return; // The capture loop keeps servicing remote PCM until ready.
                    }
                    break;
                case "bookmark": message = "已标记此刻"; break;
                default: throw new InvalidOperationException("未知录音控制命令。");
            }
            AcknowledgeControl(control, boundaryAt, message);
        }
        catch (Exception ex)
        {
            control.Done.TrySetException(ex);
            warnings.Enqueue("控制未完成：" + control.Operation + "：" + ex.Message);
        }
    }

    private void AcknowledgeControl(Control control, ulong? boundaryAt = null, string? message = null)
    {
        if (control.Operation is "mic_on" or "mic_off") autoStopMonitor?.Reset(now());
        long effective = Us(boundaryAt ?? now());
        ObserveRecordingTime(now());
        if (control.Operation == "mic_on")
        {
            string? confirmedDevice = fixedMicrophoneDeviceId;
            Enqueue(new(store => store.WriteEvent(control.Operation, control.Id, effective, confirmedDevice)));
        }
        else if (control.Operation == "auto_stop_settings")
        {
            var confirmed = autoStopSettings;
            Enqueue(new(store => store.WriteEvent(control.Operation, control.Id, effective, autoStopSettings: confirmed)));
        }
        else Event(control.Operation, control.Id, effective);
        var ack = new RecordingAck(control.Id, control.Operation, paused, micEnabled, effective, message,
            control.Operation == "auto_stop_settings" ? autoStopSettings : null);
        control.Done.TrySetResult(ack);
        NotifyAck(ack);
        RaiseHealth(now());
    }

    private void Drain(Live source, ulong? cutoff, int limit)
    {
        for (int i = 0; i < limit; i++)
        {
            if (writerError is not null) throw new IOException("停止读取新PCM：写盘失败。", writerError);
            var block = source.Source.Read(epoch, cutoff);
            if (block is null) return;
            source.Discarded += block.DiscardedFrames;
            if (!block.TimestampValid)
            {
                Array.Clear(block.Pcm); source.Discarded += block.FrameCount;
                throw new IOException("无效API时间戳；有歧义PCM未进入写盘队列。");
            }
            ObserveDevicePosition(source, block);
            if ((block.Flags & 1) != 0 && source.SeenPacket) { captureFault = true; warnings.Enqueue(source.Source.Id + " 报告音频不连续。"); }
            source.SeenPacket = true;
            source.LastPacketAt = now(); source.LastSilent = (block.Flags & 2) != 0;
            block = RemoveInactivePrefix(source, block);
            if (block.Pcm.Length == 0) continue;
            if (Interlocked.Add(ref queuedBytes, block.Pcm.Length) > 32L * 1024 * 1024)
            {
                Interlocked.Add(ref queuedBytes, -block.Pcm.Length); Array.Clear(block.Pcm);
                throw new IOException("PCM积压超过32MiB；安全停止而非静默丢弃。");
            }
            source.Pending.Enqueue(block);
            if (cutoff.HasValue) PublishPending(source, cutoff.Value, true);
            else PublishPending(source, now(), false);
        }
        if (cutoff.HasValue) throw new IOException("停采尾包超过受控上限。");
    }

    private static RecordingBlock RemoveInactivePrefix(Live source, RecordingBlock block)
    {
        if (source.ActiveStartBoundary == 0 || block.QpcPosition100ns >= source.ActiveStartBoundary) return block;
        // Retain only whole frames beginning at/after the shared active boundary.
        // Keep raw API QPC/device positions intact and describe the retained slice.
        UInt128 delta = (UInt128)(source.ActiveStartBoundary - block.QpcPosition100ns) * (uint)source.Source.Format.SampleRate;
        UInt128 first = (delta + AudioClock.UnitsPerSecond - 1) / AudioClock.UnitsPerSecond;
        int retainedStart = (int)UInt128.Min((uint)block.RawFrameCount, first);
        int drop = Math.Clamp(retainedStart - block.RetainedFrameOffset, 0, block.FrameCount);
        if (drop == 0) return block;
        byte[] kept = block.Pcm.AsSpan(checked(drop * source.Source.Format.BlockAlign)).ToArray();
        Array.Clear(block.Pcm);
        source.Discarded += drop; source.DiscardedBeforeStart += drop;
        return block with { Pcm = kept, FrameCount = block.FrameCount - drop, RetainedFrameOffset = block.RetainedFrameOffset + drop };
    }

    private void ServiceStartedSources()
    {
        for (int index = 0; index < sources.Count;)
        {
            var source = sources[index];
            if (!source.Started) { index++; continue; }
            try { Drain(source, null, 32); PublishPending(source, now(), false); }
            catch (Exception ex) { FailSource(source, ex); }
            if (index < sources.Count && ReferenceEquals(sources[index], source)) index++;
        }
    }

    private void WaitForSourceOrControl()
    {
        // Wait on the actual WASAPI events, not a timer rounded to the system tick.
        // A short timeout still services no-callback health and synthetic sources.
        var handles = sourceWaitHandles ??= new WaitHandle[] { wake }.Concat(sources.Where(source => source.Started)
            .Select(source => source.Source.PacketReady).OfType<WaitHandle>()).ToArray();
        WaitHandle.WaitAny(handles, handles.Length == 1 ? 5 : 20);
    }

    private void ObserveDevicePosition(Live source, RecordingBlock block)
    {
        // A limitation is an observed property of this entire segment, not a generic
        // exemption for any source whose device counter fails. Never modify raw API values.
        source.AllDevicePositionsZero &= block.DevicePosition == 0;
        if (block.DevicePositionValid && !(source.DeviceLimited && !source.AllDevicePositionsZero)) return;
        bool knownProcessZero = !source.DeviceClockAnomaly && source.SeenPacket && source.Source.Id == "remote"
            && options.Selection.Mode == SourceMode.ProcessTree && source.AllDevicePositionsZero;
        if (knownProcessZero)
        {
            source.DeviceLimited = true;
            return;
        }

        source.DeviceLimited = false;
        captureFault = true;
        if (!source.DeviceClockAnomaly)
            warnings.Enqueue(source.Source.Id + " 设备帧位置异常；不能归类为已观测的 ProcessTree 全零能力限制。原始设备位置与有效 API QPC 保留，完整性降级。");
        source.DeviceClockAnomaly = true;
    }

    private void PublishPending(Live source, ulong cutoff, bool final)
    {
        while (source.Pending.TryPeek(out var block))
        {
            int rawThrough = AudioClock.CompleteFramesBefore(block.QpcPosition100ns, cutoff, source.Source.Format.SampleRate, block.RawFrameCount);
            int keep = Math.Clamp(rawThrough - block.RetainedFrameOffset, 0, block.FrameCount);
            if (!final && keep < block.FrameCount)
            {
                if (block.QpcPosition100ns > cutoff + 2_000_000) throw new IOException("API包时间超前超过200ms，拒绝不明边界。");
                // The packet can straddle the inactivity deadline. Observe only its already
                // elapsed prefix; waiting for the whole packet would miss a last-moment voice.
                if (keep > 0 && autoStopMonitor is not null)
                    ObserveAutomaticEndActivity(source, block, keep);
                return; // Do not persist a frame until its end is known to precede this capture-thread instant.
            }
            if (keep > 0 && autoStopMonitor is not null) ObserveAutomaticEndActivity(source, block, keep);
            source.Pending.Dequeue();
            byte[] pcm = block.Pcm;
            if (keep != block.FrameCount)
            {
                source.Discarded += block.FrameCount - keep;
                var prefix = pcm.AsSpan(0, keep * source.Source.Format.BlockAlign).ToArray();
                Clear(pcm); pcm = prefix; Interlocked.Add(ref queuedBytes, pcm.Length);
            }
            if (keep == 0) { Clear(pcm); continue; }
            var packet = new CapturedPacket(source.Frames, keep, block.RawFrameCount, block.DevicePosition,
                block.QpcPosition100ns, block.Flags, true, block.DevicePositionValid, block.RetainedFrameOffset);
            source.Peak = Peak(pcm, source.Source.Format);
            source.ObservedSound |= source.Peak > .0001f;
            Enqueue(new(store => store.WritePacket(source.SegmentId, pcm, packet), pcm));
            source.Frames += keep;
            if (source.Source.Id == "remote") Interlocked.Add(ref remoteFrames, keep);
            else Interlocked.Add(ref micFrames, keep);
        }
    }

    private static ulong PacketEnd(Live source, RecordingBlock block, int frames) => checked(block.QpcPosition100ns +
        (ulong)((UInt128)(uint)(block.RetainedFrameOffset + frames) * 10_000_000 / (uint)source.Source.Format.SampleRate));

    private void ObserveAutomaticEndActivity(Live source, RecordingBlock block, int keep)
    {
        if (source.Source.Id != "mic")
        {
            autoStopMonitor!.Observe(PacketEnd(source, block, keep), Peak(block.Pcm, source.Source.Format, keep));
            return;
        }
        // A pending packet may be revisited many times before publication. Analyze each
        // elapsed frame once; repeated peeks must not manufacture microphone activity.
        if (!ReferenceEquals(source.ActivityBlock, block)) { source.ActivityBlock = block; source.ActivityFrames = 0; }
        int previous = source.ActivityFrames;
        if (keep <= previous) return;
        int align = source.Source.Format.BlockAlign;
        var activity = source.MicrophoneActivity ??= new(source.Source.Format);
        int activeEnd = activity.Observe(block.Pcm.AsSpan(previous * align, (keep - previous) * align), PacketEnd(source, block, previous));
        source.ActivityFrames = keep;
        if (activeEnd > 0) autoStopMonitor!.Observe(PacketEnd(source, block, previous + activeEnd), 1);
    }

    private ulong CloseSources(IReadOnlyList<Live> selected, string operation, string requestId)
    {
        bool stopUnconfirmed = false;
        var ordered = selected.OrderByDescending(x => x.Source.Id == "mic").ToArray();
        foreach (var source in ordered)
        {
            if (source.Started)
            {
                try { source.Source.Stop(); }
                catch (Exception ex) { source.StopFailed = true; captureFault = true; stopUnconfirmed = true; warnings.Enqueue(source.Source.Id + " 停采失败：" + ex.Message); }
            }
            source.Cutoff = now();
        }
        ulong boundary = ordered.Length == 0 ? now() : ordered.Max(source => source.Cutoff);
        if (!stopUnconfirmed && operation is "pause" or "system_gap")
        {
            // Freeze at the actual last Stop boundary, before potentially slow
            // tail writing or Dispose. A paused stop must not add cleanup silence.
            recordingClock!.Pause(boundary);
            ObserveRecordingTime(boundary);
        }
        foreach (var source in ordered)
        {
            if (source.Prepared && !source.Started && !source.OffsetAssigned && recordingClock!.IsPaused)
                SetSegmentOffset(source, recordingClock.ExcludedAtFrozenBoundary(source.Cutoff));
            try
            {
                if (source.Started && !source.StopFailed) Drain(source, source.Cutoff, 1024);
                PublishPending(source, source.Cutoff, true);
            }
            catch (Exception ex) { captureFault = true; warnings.Enqueue(source.Source.Id + " 尾包未完整处理：" + ex.Message); }
            finally
            {
                try
                {
                    if (source.Source.Diagnostics is { } timing)
                        sourceDiagnostics.Add(new(source.SegmentId, source.Source.Id, source.Source.ProcessingMode, timing));
                }
                catch (Exception ex) { warnings.Enqueue("采集性能诊断未完成：" + ex.GetType().Name); }
                try { source.Source.Dispose(); }
                catch (Exception ex) { captureFault = true; stopUnconfirmed = true; warnings.Enqueue(source.Source.Id + " 释放失败：" + ex.Message); }
                while (source.Pending.TryDequeue(out var pending)) { source.Discarded += pending.FrameCount; Clear(pending.Pcm); }
                sources.Remove(source);
                sourceWaitHandles = null;
            }
            if (source.DeviceLimited) warnings.Enqueue(source.Source.Id + " 设备帧计数不可依赖；保留原值，以实际API QPC标记时间。长时/双轨同步未验收。");
            if (source.Discarded > 0) warnings.Enqueue(source.Source.Id + $" 边界舍弃 {source.Discarded} 帧。");
            if (source.Started && source.Frames == 0) { captureFault = true; warnings.Enqueue(source.Source.Id + " 本段未收到有效音频包，不等于静音。"); }
            // A late MicOn can be refused by the actual-format disk budget
            // before its segment exists. Never poison the still-recording remote
            // writer by queuing a boundary for an uncreated segment.
            if (!source.Prepared) continue;
            try
            {
                Enqueue(new(store => store.WriteBoundary(source.SegmentId, requestId, operation, source.Cutoff, null,
                    source.Discarded, source.Discarded == 0 ? null : source.DiscardedBeforeStart > 0 ? "outside_active_boundary" : "after_boundary")));
            }
            catch (Exception ex) { captureFault = true; warnings.Enqueue("边界未能写入：" + ex.Message); }
        }
        if (stopUnconfirmed)
        {
            Interlocked.Exchange(ref stopping, 1);
            throw new IOException("未确认所有来源成功停止/释放；结束本场并等待Host退出，不回执已暂停或mic已关。");
        }
        return boundary;
    }
    private void FailSource(Live source, Exception ex)
    {
        captureFault = true;
        string message = source.Source.Id + " 来源故障；不替换或扩大范围：" + ex.Message;
        warnings.Enqueue(message);
        if (source.Source.Id == "remote") remoteFault = message;
        else { micFault = message; micEnabled = false; }
        CloseSources([source], "stop", "source-fault");
        Event("source_fault", "source-fault", Us(now()));
    }
    private void RequestAutomaticStop(string reason)
    {
        lock (controlGate)
        {
            if (Volatile.Read(ref stopping) != 0) return; // Manual Stop wins an already accepted request.
            ulong current = now();
            bool Missing(string id) => sources.FirstOrDefault(source => source.Source.Id == id) is not { SeenPacket: true } live
                || PacketHealthTimedOut(current, live.LastPacketAt);
            autoStopInfo = new(reason, autoStopSettings.SilenceMinutes, Us(current),
                !paused && Missing("remote"), !paused && micEnabled && Missing("mic"));
            stopId = "auto-stop-" + reason;
            Volatile.Write(ref stopping, 1);
        }
        // Persist the decision before the ordinary stop/boundary items. Never report Saved here.
        var info = autoStopInfo;
        Enqueue(new(store => store.WriteEvent("auto_stop", stopId, info.TriggeredSessionUs, autoStop: info)));
        try { AutoStopping?.Invoke(info); } catch { }
    }
    private void Event(string operation, string requestId, long us) => Enqueue(new(store => store.WriteEvent(operation, requestId, us)));
    private void Enqueue(Write item)
    {
        if (writerError is not null || writes.IsAddingCompleted || !writes.TryAdd(item))
        { Clear(item.Pcm); throw new IOException("录音写盘队列不可用；未静默丢弃。"); }
    }
    private void Clear(byte[]? pcm)
    {
        if (pcm is null) return;
        Array.Clear(pcm); Interlocked.Add(ref queuedBytes, -pcm.Length);
    }
    private void RaiseHealth(ulong current)
    {
        var remote = sources.FirstOrDefault(x => x.Source.Id == "remote");
        var mic = sources.FirstOrDefault(x => x.Source.Id == "mic");
        string Status(Live? live, string fault, bool enabled)
        {
            if (fault.Length != 0) return fault;
            if (paused) return "已暂停 · 不采集";
            if (!enabled) return "关闭 · 不采集";
            if (live is null || !live.SeenPacket) return "尚未收到音频包";
            if (PacketHealthTimedOut(current, live.LastPacketAt)) return "超过2秒无音频包 · 不等于静音";
            string clockStatus = live.DeviceClockAnomaly ? " · 设备位置异常，完整性降级"
                : live.DeviceLimited ? " · API QPC计时，设备计数不可用" : "";
            if (live.LastSilent) return "已收到明确SILENT静音包" + clockStatus;
            return (live.ObservedSound ? "已观察到声音" : "收到有效包 · 尚未观察到声音") + clockStatus;
        }
        try { HealthChanged?.Invoke(new(Us(current), paused, micEnabled, remote?.Peak ?? 0, mic?.Peak ?? 0,
            Interlocked.Read(ref remoteFrames), Interlocked.Read(ref micFrames), Status(remote, remoteFault, true),
            pendingMicrophoneControl is not null ? "麦克风正在初始化，尚未开始采集" : Status(mic, micFault, micEnabled),
            Interlocked.Read(ref savedUs), available)); } catch { }
    }
    internal static bool PacketHealthTimedOut(ulong current, ulong lastPacketAt)
    {
        // The loop samples current before Drain, which can observe a newer packet.
        // A packet newer than this health snapshot is fresh, never an unsigned age wrap.
        return current > lastPacketAt && current - lastPacketAt > 20_000_000;
    }
    private void NotifyAck(RecordingAck ack) { try { Acknowledged?.Invoke(ack); } catch { } }
    private long Us(ulong value) => recordingClock?.Elapsed100ns(value) / 10
        ?? (value >= epoch ? checked((long)((value - epoch) / 10)) : 0);
    private void ObserveRecordingTime(ulong value) => Interlocked.Exchange(ref observedSessionUs,
        Math.Max(Interlocked.Read(ref observedSessionUs), Us(value)));
    private static ulong Now100ns() => (ulong)AudioClock.CounterTo100ns(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
    private static void ValidateDirectory(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("录音路径含重解析点。");
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("拒绝覆盖已有会话目录。");
    }
    private static float Peak(byte[] pcm, PcmFormat format, int? frames = null)
    {
        float peak = 0; int width = format.BitsPerSample / 8;
        int bytes = frames.HasValue ? checked(frames.Value * format.BlockAlign) : pcm.Length;
        for (int i = 0; i < bytes; i += width)
        {
            float value = format.IsFloat ? BitConverter.ToSingle(pcm, i) : width switch
            { 2 => BitConverter.ToInt16(pcm, i) / 32768f, 3 => ((pcm[i] | pcm[i + 1] << 8 | pcm[i + 2] << 16) << 8 >> 8) / 8388608f,
                4 => BitConverter.ToInt32(pcm, i) / 2147483648f, _ => 0 };
            if (float.IsFinite(value)) peak = Math.Max(peak, Math.Abs(value));
        }
        return Math.Min(1, peak);
    }
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref startOnce) != 0) await StopAsync().ConfigureAwait(false);
        wake.Dispose(); writes.Dispose();
    }
    private sealed record Write(Action<M1Storage> Action, byte[]? Pcm = null);
    private sealed record Control(string Id, string Operation, string? DeviceId, RecordingAutoStopOptions? Automatic)
    { public TaskCompletionSource<RecordingAck> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private sealed class Live(IRecordingAudioSource source, string segmentId)
    {
        public MicrophoneSoundActivity? MicrophoneActivity;
        public RecordingBlock? ActivityBlock;
        public int ActivityFrames;
        public IRecordingAudioSource Source { get; } = source;
        public string SegmentId { get; } = segmentId;
        public Queue<RecordingBlock> Pending { get; } = new();
        public bool Prepared, Started, StopFailed, DeviceLimited, DeviceClockAnomaly, SeenPacket, LastSilent, ObservedSound;
        public bool AllDevicePositionsZero = true;
        public ulong Cutoff, LastPacketAt, ExcludedPause100ns, ActiveStartBoundary;
        public bool OffsetAssigned;
        public long Frames, Discarded, DiscardedBeforeStart;
        public float Peak;
    }
}
