using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ProjectEcho.CaptureHost;

/// <summary>Real local pipe backpressure, without constructing a capture session or opening audio devices.</summary>
internal static class HostTransportSafetyTests
{
    public static void Run()
    {
        BoundedWriteAsync().GetAwaiter().GetResult();
        WatchdogDuringBlockedWriteAsync().GetAwaiter().GetResult();
        HealthyMessageAsync().GetAwaiter().GetResult();
    }

    private static async Task BoundedWriteAsync()
    {
        var name = "ProjectEcho-Test-" + Guid.NewGuid().ToString("N");
        using var server = NewServer(name);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await ConnectAsync(server, client);
        var writer = NewWriter(server);
        int stops = 0;
        var elapsed = Stopwatch.StartNew();
        try
        {
            // The connected client deliberately never reads. The event exceeds the
            // explicit pipe buffer by orders of magnitude and forces a pending write.
            await HostTransportSafety.SendAsync(writer, LargeEvent(), () =>
            {
                Interlocked.Increment(ref stops);
                server.Dispose();
            }, CancellationToken.None, TimeSpan.FromMilliseconds(250));
            throw new InvalidOperationException("A blocked pipe write incorrectly completed.");
        }
        catch (IOException ex)
        {
            Require(ex.InnerException is OperationCanceledException, "Expected the write deadline to trigger cancellation.");
            Require(stops == 1, "The write deadline did not request stop exactly once.");
            Require(elapsed.Elapsed < TimeSpan.FromSeconds(5), "The bounded write exceeded its safety deadline tolerance.");
        }
        finally { DisposeWriter(writer, server); }
    }

    private static async Task WatchdogDuringBlockedWriteAsync()
    {
        var name = "ProjectEcho-Test-" + Guid.NewGuid().ToString("N");
        using var server = NewServer(name);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await ConnectAsync(server, client);
        var writer = NewWriter(server);
        using var lifetime = new CancellationTokenSource();
        var safety = new HostTransportSafety();
        int stops = 0;
        void StopAndDisconnect()
        {
            Interlocked.CompareExchange(ref stops, 1, 0);
            server.Dispose();
        }
        var elapsed = Stopwatch.StartNew();
        var watchdog = safety.WatchParentAsync(StopAndDisconnect, lifetime.Token, TimeSpan.FromMilliseconds(250));
        try
        {
            // The write's own ten-second deadline cannot explain the expected early
            // stop: only the concurrently running parent watchdog can release it.
            await HostTransportSafety.SendAsync(writer, LargeEvent(), StopAndDisconnect,
                lifetime.Token, TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("A watchdog-terminated blocked write incorrectly completed.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Require(stops == 1, "Watchdog did not request stop while output was blocked.");
            Require(elapsed.Elapsed < TimeSpan.FromSeconds(5), "Watchdog was stalled behind the outgoing write.");
        }
        finally
        {
            lifetime.Cancel();
            await watchdog;
            DisposeWriter(writer, server);
        }
    }

    private static async Task HealthyMessageAsync()
    {
        using var memory = new MemoryStream();
        using var writer = new StreamWriter(memory, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        int unexpectedStops = 0;
        await HostTransportSafety.SendAsync(writer, new(1, "test", "request", "ready"),
            () => unexpectedStops++, CancellationToken.None);
        using var document = JsonDocument.Parse(memory.ToArray());
        Require(document.RootElement.GetProperty("Kind").GetString() == "ready", "Healthy output was not a valid IPC event.");
        Require(unexpectedStops == 0, "Healthy output triggered a safety stop.");

        using var lifetime = new CancellationTokenSource();
        var safety = new HostTransportSafety();
        var watchdog = safety.WatchParentAsync(() => unexpectedStops++, lifetime.Token, TimeSpan.FromMilliseconds(400));
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(75);
            safety.RecordParentMessage();
        }
        lifetime.Cancel();
        await watchdog;
        Require(unexpectedStops == 0, "An active parent or cancelled watchdog triggered a stop.");
    }

    private static NamedPipeServerStream NewServer(string name) => new(name, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);

    private static StreamWriter NewWriter(Stream stream) => new(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

    private static HostEvent LargeEvent() => new(1, "test", "request", "levels", new string('x', 1024 * 1024));

    private static async Task ConnectAsync(NamedPipeServerStream server, NamedPipeClientStream client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connecting = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await connecting;
    }

    private static void DisposeWriter(StreamWriter writer, Stream transport)
    {
        transport.Dispose();
        try { writer.Dispose(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private static void Require(bool value, string failure)
    {
        if (!value) throw new InvalidOperationException(failure);
    }
}
