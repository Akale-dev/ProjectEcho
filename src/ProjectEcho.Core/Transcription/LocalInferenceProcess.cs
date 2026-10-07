using System.Diagnostics;
using System.Text;

namespace ProjectEcho.Core.Transcription;

internal sealed record LocalInferenceProcessResult(int ExitCode, long ElapsedMilliseconds, long PeakWorkingSetBytes,
    long PeakPrivateBytes, double CpuMilliseconds, string Priority, string StandardOutput, string StandardError);

/// <summary>A bounded, owned native subprocess. No shell, network setup, capture or shared process termination.</summary>
internal static class LocalInferenceProcess
{
    internal static async Task<LocalInferenceProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        string directory, string prefix, CancellationToken token, Action<string>? outputLine = null, Action<string>? errorLine = null)
    {
        var start = new ProcessStartInfo(TranscriptFiles.LocalPath(executable))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true)
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        var watch = Stopwatch.StartNew();
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("本地推理进程未启动。");
        string priority;
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; priority = "BelowNormal"; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { priority = ex.Message; }
        using var registration = token.Register(() => Kill(process));
        long peakWorkingSet = 0, peakPrivate = 0; double cpuMilliseconds = 0;
        using var monitorStop = new CancellationTokenSource();
        async Task Monitor()
        {
            while (!monitorStop.IsCancellationRequested)
            {
                try
                {
                    process.Refresh();
                    peakWorkingSet = Math.Max(peakWorkingSet, process.PeakWorkingSet64);
                    peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
                    cpuMilliseconds = Math.Max(cpuMilliseconds, process.TotalProcessorTime.TotalMilliseconds);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                try { await Task.Delay(100, monitorStop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        Task monitor = Monitor();
        async Task<string> Read(StreamReader reader, string suffix, Action<string>? report)
        {
            try
            {
                using var file = new FileStream(TranscriptFiles.Inside(directory, prefix + suffix), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(file, new UTF8Encoding(false));
                var all = new StringBuilder(); var pending = new StringBuilder(); var buffer = new char[4096];
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                {
                    if (all.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("本地推理诊断输出超过 2 Mi 字符，已停止该进程。");
                    all.Append(buffer, 0, count);
                    await writer.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    await writer.FlushAsync(token).ConfigureAwait(false);
                    if (report is not null)
                        for (int index = 0; index < count; index++)
                        {
                            char value = buffer[index];
                            if (value == '\n') { report(pending.ToString().TrimEnd('\r')); pending.Clear(); }
                            else pending.Append(value);
                        }
                }
                if (pending.Length > 0) report?.Invoke(pending.ToString());
                return all.ToString();
            }
            catch { Kill(process); throw; }
        }
        Task<string> stdout = Read(process.StandardOutput, ".stdout.txt", outputLine), stderr = Read(process.StandardError, ".stderr.txt", errorLine);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(token), stdout, stderr).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(process.ExitCode, watch.ElapsedMilliseconds, peakWorkingSet, peakPrivate, cpuMilliseconds, priority,
                await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        finally
        {
            Kill(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            finally
            {
                monitorStop.Cancel(); await monitor.ConfigureAwait(false);
                // Observe pipe completion even on cancellation; readers cannot outlive the owned process.
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or InvalidOperationException or DecoderFallbackException) { }
            }
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
