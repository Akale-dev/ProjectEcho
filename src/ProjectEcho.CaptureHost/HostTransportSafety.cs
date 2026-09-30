using System.Diagnostics;
using System.Text.Json;

namespace ProjectEcho.CaptureHost;

/// <summary>Host-specific IPC deadlines. Neither safety path waits for an outgoing status message.</summary>
public sealed class HostTransportSafety
{
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ParentTimeout = TimeSpan.FromSeconds(5);
    private long _lastParentMessage = Stopwatch.GetTimestamp();

    public void RecordParentMessage() => Interlocked.Exchange(ref _lastParentMessage, Stopwatch.GetTimestamp());

    public async Task WatchParentAsync(Action stopAndDisconnect, CancellationToken cancellationToken,
        TimeSpan? parentTimeout = null)
    {
        var timeout = parentTimeout ?? ParentTimeout;
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(parentTimeout));
        var interval = TimeSpan.FromMilliseconds(Math.Min(100, timeout.TotalMilliseconds / 4));
        try
        {
            while (true)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastParentMessage)) < timeout) continue;
                stopAndDisconnect();
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public static async Task SendAsync(StreamWriter writer, HostEvent value, Action stopAndDisconnect,
        CancellationToken cancellationToken, TimeSpan? writeTimeout = null)
    {
        var timeout = writeTimeout ?? WriteTimeout;
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(writeTimeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            // NamedPipeStream supports cancellation of a pending asynchronous write.
            // The independent parent watchdog also disposes the pipe if its peer stops responding.
            await writer.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            stopAndDisconnect();
            throw new IOException("Host IPC 写入超时；已请求停采并断开连接，未确认保存完整。", ex);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            stopAndDisconnect();
            throw;
        }
    }
}
