using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ProjectEcho.App;

/// <summary>
/// One window per Windows user and interactive session, independent of the portable
/// folder. The UI thread owns/releases the mutex. IPC carries only an activation
/// request; command lines, paths and meeting data are never forwarded.
/// </summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex mutex;
    private readonly NamedPipeServerStream pipe;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task listener;
    private bool disposed;

    private SingleInstanceCoordinator(Mutex mutex, string pipeName, Action activate)
    {
        this.mutex = mutex;
        pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        listener = ListenAsync(activate);
    }

    /// <returns>The primary-instance lease, or null after the existing instance accepted activation.</returns>
    /// <remarks>Call from and retain the WPF synchronization context; Windows mutex ownership is thread-affine.</remarks>
    internal static async Task<SingleInstanceCoordinator?> AcquireOrActivateAsync(Action activate,
        string? isolatedProbeScope = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string user = identity.User?.Value ?? throw new InvalidOperationException("无法确认当前 Windows 用户。");
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..24]
            + "." + Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture);
        if (isolatedProbeScope is not null)
            suffix += ".Probe." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(isolatedProbeScope)))[..24];
        string pipeName = "ProjectEcho.Activate.v1." + suffix;
        var mutex = new Mutex(false, @"Local\ProjectEcho.Window.v1." + suffix);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        bool ownsMutex = false;
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                try { ownsMutex = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { ownsMutex = true; }
                if (ownsMutex)
                {
                    var primary = new SingleInstanceCoordinator(mutex, pipeName, activate);
                    return primary;
                }
                if (await TryActivateAsync(pipeName, deadline.Token))
                {
                    mutex.Dispose();
                    return null;
                }
                // The first instance may still be starting or may have exited while
                // connecting. Recheck mutex ownership instead of creating another window.
                await Task.Delay(75, deadline.Token);
            }
            throw new TimeoutException("ProjectEcho 已在运行，但窗口尚未响应。请稍后再试。");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (ownsMutex) mutex.ReleaseMutex();
            mutex.Dispose();
            throw new TimeoutException("ProjectEcho 已在运行，但窗口尚未响应。请稍后再试。");
        }
        catch
        {
            if (ownsMutex) mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    private static async Task<bool> TryActivateAsync(string pipeName, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(400, attempt.Token).ConfigureAwait(false);
            using var reader = new StreamReader(client, Encoding.UTF8, false, 128, leaveOpen: true);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 128, leaveOpen: true) { AutoFlush = true };
            string? greeting = await reader.ReadLineAsync(attempt.Token).ConfigureAwait(false);
            if (!uint.TryParse(greeting, NumberStyles.None, CultureInfo.InvariantCulture, out uint processId)) return false;
            // A user-launched second process can grant the existing window permission
            // to become foreground. Windows still owns the final focus decision.
            AllowSetForegroundWindow(processId);
            await writer.WriteLineAsync("activate".AsMemory(), attempt.Token).ConfigureAwait(false);
            return await reader.ReadLineAsync(attempt.Token).ConfigureAwait(false) == "accepted";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { return false; }
    }

    private async Task ListenAsync(Action activate)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                request.CancelAfter(TimeSpan.FromSeconds(2));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 128, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 128, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(Environment.ProcessId.ToString(CultureInfo.InvariantCulture).AsMemory(), request.Token).ConfigureAwait(false);
                if (await reader.ReadLineAsync(request.Token).ConfigureAwait(false) == "activate")
                {
                    activate();
                    await writer.WriteLineAsync("accepted".AsMemory(), request.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally
            {
                try { if (pipe.IsConnected) pipe.Disconnect(); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stopping.Cancel();
        pipe.Dispose();
        mutex.ReleaseMutex();
        mutex.Dispose();
        // Do not synchronously block the UI waiting for an overlapped pipe operation.
        _ = listener.ContinueWith(_ => stopping.Dispose(), TaskScheduler.Default);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
