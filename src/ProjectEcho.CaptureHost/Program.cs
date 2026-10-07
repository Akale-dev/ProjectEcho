using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ProjectEcho.Capture.Windows;
using ProjectEcho.CaptureHost;
using ProjectEcho.Export;

// This executable has no autonomous recording entry point: an authenticated local
// parent pipe must deliver an explicit Start command containing the source consent.
if (args.Length != 2 || !args[0].StartsWith("ProjectEcho-", StringComparison.Ordinal)) return 2;
var sessionId = args[1];
using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut,
    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
await pipe.ConnectAsync(connectTimeout.Token);
using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
CaptureSession? capture = null;
RecordingSession? recording = null;
RecordingHealth? health = null;
var controlEvents = new ConcurrentQueue<HostEvent>();
CaptureLevels? levels = null;
var transportSafety = new HostTransportSafety();
using var transportLifetime = new CancellationTokenSource();
using var watchdogLifetime = new CancellationTokenSource();
var captureGate = new object();
var connectionFailed = 0;
var started = false;
var requestId = "";
Task? startup = null;
bool startupNotified = false;

void StopAndDisconnect()
{
    Interlocked.Exchange(ref connectionFailed, 1);
    // StartAsync only starts the dedicated capture thread. This short lock prevents
    // a timeout from losing its stop signal between assigning capture and starting it.
    lock (captureGate)
    {
        if (capture is not null) _ = capture.StopAsync();
        if (recording is not null) _ = recording.StopAsync();
    }
    pipe.Dispose();
}

Task Send(HostEvent value) => HostTransportSafety.SendAsync(writer, value, StopAndDisconnect, transportLifetime.Token);
var watchdog = transportSafety.WatchParentAsync(StopAndDisconnect, watchdogLifetime.Token);
try
{
    await Send(new(1, sessionId, "", "ready"));
    var read = reader.ReadLineAsync();
    while (true)
    {
        await Task.WhenAny(read, Task.Delay(200));
        if (Volatile.Read(ref connectionFailed) != 0) break;
        if (read.IsCompleted)
        {
            var line = await read;
            if (line is null) break;
            if (line.Length > 65536) throw new InvalidDataException("IPC 命令过大。");
            var command = JsonSerializer.Deserialize<HostCommand>(line) ?? throw new InvalidDataException("空命令。");
            if (command.ProtocolVersion != 1 || command.SessionId != sessionId) throw new InvalidDataException("IPC 会话不匹配。");
            transportSafety.RecordParentMessage();
            if (command.Command == "start")
            {
                if (started || command.Options is null) throw new InvalidOperationException("重复或缺少来源的启动命令。");
                started = true;
                requestId = command.RequestId;
                lock (captureGate)
                {
                    if (Volatile.Read(ref connectionFailed) != 0) throw new IOException("父进程连接已失效，拒绝启动采集。");
                    capture = new CaptureSession();
                    capture.LevelsChanged += value => Volatile.Write(ref levels, value);
                    // Activation must not block Stop/heartbeat/parent-disconnect processing.
                    startup = capture.StartAsync(command.Options);
                }
            }
            else if (command.Command == "record")
            {
                if (started || command.Recording is null) throw new InvalidOperationException("重复或缺少来源的录音命令。");
                started = true;
                requestId = command.RequestId;
                lock (captureGate)
                {
                    if (Volatile.Read(ref connectionFailed) != 0) throw new IOException("父进程已失联，拒绝录音。");
                    recording = new RecordingSession();
                    recording.HealthChanged += value => Volatile.Write(ref health, value);
                    recording.Acknowledged += value => controlEvents.Enqueue(new(1, sessionId, value.RequestId, "ack", Ack: value));
                    recording.AutoStopping += value => controlEvents.Enqueue(new(1, sessionId, "auto-stop", "finalizing", AutoStop: value));
                    startup = recording.StartAsync(command.Recording);
                }
            }
            else if (command.Command == "stop")
            {
                requestId = command.RequestId;
                if (recording is not null) _ = recording.StopAsync(command.RequestId);
                else if (capture is not null) _ = capture.StopAsync();
                else break;
            }
            else if (command.Command is "pause" or "resume" or "mic_on" or "mic_off" or "bookmark" or "auto_stop_settings")
            {
                if (recording is null) throw new InvalidOperationException("普通录音尚未开始。");
                var pending = recording.ControlAsync(command.RequestId, command.Command, command.DeviceId, command.AutoStopSettings);
                _ = pending.ContinueWith(task =>
                {
                    if (task.IsFaulted)
                        controlEvents.Enqueue(new(1, sessionId, command.RequestId, "control_error",
                            task.Exception!.GetBaseException().Message));
                }, TaskScheduler.Default);
            }
            else if (command.Command != "heartbeat") throw new InvalidDataException("未知命令。");
            read = reader.ReadLineAsync();
        }
        if (startup is { IsCompleted: true } && !startupNotified)
        {
            startupNotified = true;
            await startup;
            await Send(new(1, sessionId, requestId, "started", recording is null ? "正在试录，最多 10 秒。" : "正在录音；30秒原轨分块，mic状态独立。"));
        }
        while (controlEvents.TryDequeue(out var controlEvent)) await Send(controlEvent);
        if (recording is not null && recording.Completion.IsCompleted)
        {
            var result = await recording.Completion;
            watchdogLifetime.Cancel();
            await watchdog;
            await Send(new(1, sessionId, requestId, result.StorageComplete ? "recording_saved" : "recording_incomplete",
                string.Join(Environment.NewLine, result.Warnings), RecordingResult: result));
            break;
        }
        if (capture is not null && capture.Completion.IsCompleted)
        {
            var result = await capture.Completion;
            // Capture has stopped and its writer has finished. Preview rendering may
            // be slow and does not consume parent commands, so it must not be mistaken
            // for a missing parent. Outgoing messages still have their own deadline.
            watchdogLifetime.Cancel();
            await watchdog;
            // PCM/index and WAV close must finish before the application is told it may play.
            await Send(new(1, sessionId, requestId, "finalizing", "采集已停止，正在收尾并生成短试录混合 WAV。"));
            string? mixedPath = null;
            string? renderError = null;
            if (result.Complete)
            {
                try
                {
                    mixedPath = Path.Combine(result.SessionDirectory, "mixed-preview.wav");
                    TimelineWaveRenderer.Render(result, mixedPath);
                }
                catch (Exception ex) { mixedPath = null; renderError = "原轨已保存；混音失败：" + ex.Message; }
            }
            await Send(new(1, sessionId, requestId, result.Complete ? "saved" : "incomplete",
                renderError ?? string.Join(Environment.NewLine, result.Warnings), Result: result, MixedPath: mixedPath));
            break;
        }
        var current = Volatile.Read(ref levels);
        if (current is not null) await Send(new(1, sessionId, requestId, "levels", Levels: current));
        var currentHealth = Volatile.Read(ref health);
        if (currentHealth is not null) await Send(new(1, sessionId, requestId, "health", Health: currentHealth));
    }
    return 0;
}
catch (Exception ex)
{
    if (capture is not null)
    {
        try { await capture.StopAsync(); } catch { }
    }
    if (recording is not null) { try { await recording.StopAsync(); } catch { } }
    try { await Send(new(1, sessionId, requestId, "error", ex.Message)); }
    catch (Exception sendError) when (sendError is IOException or ObjectDisposedException or OperationCanceledException) { }
    return 1;
}
finally
{
    transportLifetime.Cancel();
    watchdogLifetime.Cancel();
    await watchdog;
    // Disconnect/parent failure always stops; the same Stop drains before finalizing.
    if (capture is not null)
    {
        try { await capture.DisposeAsync(); } catch { /* Keep partial originals for investigation. */ }
    }
    if (recording is not null) { try { await recording.DisposeAsync(); } catch { } }
    // A failed peer must not turn StreamWriter.Dispose's synchronous final flush
    // into another unbounded wait after the safety paths have already stopped capture.
    pipe.Dispose();
    try { writer.Dispose(); }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
}
