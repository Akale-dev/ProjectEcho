using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ProjectEcho.Capture.Windows;
using ProjectEcho.CaptureHost;

namespace ProjectEcho.App;

internal sealed class CaptureHostClient : IAsyncDisposable
{
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private NamedPipeServerStream? pipe;
    private StreamWriter? writer;
    private Process? process;
    private Task? reading;
    private Task? heartbeat;
    private bool completed;
    private Task? disposal;
    public event Action<HostEvent>? EventReceived;

    public Task StartAsync(CaptureOptions options, string projectRoot, Func<bool> isCancelled) =>
        StartCoreAsync(options, null, projectRoot, isCancelled);

    public Task StartRecordingAsync(RecordingOptions options, string projectRoot, Func<bool> isCancelled) =>
        StartCoreAsync(null, options, projectRoot, isCancelled);

    private async Task StartCoreAsync(CaptureOptions? options, RecordingOptions? recording, string projectRoot, Func<bool> isCancelled)
    {
        var pipeName = "ProjectEcho-" + sessionId;
        pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var host = Path.Combine(AppContext.BaseDirectory, "CaptureHost", "ProjectEcho.CaptureHost.exe");
        if (!File.Exists(host)) throw new FileNotFoundException("缺少 CaptureHost。请先完整 Build。", host);
        var info = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = projectRoot };
        info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add(sessionId);
        var localRuntime = Path.Combine(projectRoot, ".tools", "dotnet");
        if (File.Exists(Path.Combine(localRuntime, "dotnet.exe"))) info.Environment["DOTNET_ROOT"] = localRuntime;
        process = Process.Start(info) ?? throw new InvalidOperationException("CaptureHost 无法启动。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await pipe.WaitForConnectionAsync(timeout.Token);
        writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        reading = ReadEventsAsync();
        heartbeat = HeartbeatAsync();
        await SendAsync(recording is null ? "start" : "record", options, recording, isCancelled: isCancelled);
    }

    public Task StopAsync() => SendAsync("stop");

    public Task ControlAsync(string command, string? deviceId = null, string? requestId = null)
    {
        if (command is not ("pause" or "resume" or "mic_on" or "mic_off" or "bookmark"))
            throw new ArgumentOutOfRangeException(nameof(command), "未知录音控制。");
        return SendAsync(command, deviceId: deviceId, requestId: requestId);
    }

    private async Task SendAsync(string command, CaptureOptions? options = null, RecordingOptions? recording = null,
        string? deviceId = null, Func<bool>? isCancelled = null, string? requestId = null)
    {
        await sendGate.WaitAsync(lifetime.Token);
        try
        {
            if (isCancelled?.Invoke() == true) throw new OperationCanceledException("启动已取消，没有发送采集命令。");
            if (completed)
            {
                if (command is "stop" or "heartbeat") return;
                throw new InvalidOperationException("采集会话已结束，未发送控制命令。");
            }
            if (writer is null) throw new IOException("CaptureHost 尚未连接，未发送控制命令。");
            await writer.WriteLineAsync(JsonSerializer.Serialize(new HostCommand(1, sessionId,
                requestId ?? Guid.NewGuid().ToString("N"), command, options, recording, deviceId)));
        }
        finally { sendGate.Release(); }
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(lifetime.Token)) await SendAsync("heartbeat");
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ReadEventsAsync()
    {
        try
        {
            using var reader = new StreamReader(pipe!, Encoding.UTF8, false, 4096, leaveOpen: true);
            while (await reader.ReadLineAsync(lifetime.Token) is { } line)
            {
                var value = JsonSerializer.Deserialize<HostEvent>(line) ?? throw new InvalidDataException("空 host 事件。");
                if (value.ProtocolVersion != 1 || value.SessionId != sessionId) throw new InvalidDataException("Host 会话不匹配。");
                if (value.Kind is "saved" or "incomplete" or "recording_saved" or "recording_incomplete" or "error") completed = true;
                EventReceived?.Invoke(value);
            }
            if (!completed && !lifetime.IsCancellationRequested)
                EventReceived?.Invoke(new(1, sessionId, "", "error", "CaptureHost 连接中断；未确认保存完整，保留原件。"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!lifetime.IsCancellationRequested) EventReceived?.Invoke(new(1, sessionId, "", "error", "Host 通信失败：" + ex.Message));
        }
    }

    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        lifetime.Cancel();
        pipe?.Dispose();
        if (heartbeat is not null) await heartbeat;
        if (reading is not null) await reading;
        // Keep the UI session closed to new capture until the old process has actually exited.
        // Never kill a writer to manufacture a successful save.
        await WaitForExitAsync();
        process?.Dispose();
        lifetime.Dispose();
    }

    public async Task WaitForExitAsync()
    {
        if (process is null || process.HasExited) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            throw new IOException("CaptureHost 尚未退出，无法确认已停采。已禁止新试录；请检查该进程。原件未删除。");
        }
    }
}
