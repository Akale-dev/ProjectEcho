using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ProjectEcho.Core;

/// <summary>Explicit test-owned CPU process. No audio APIs, files, network, global scheduling or device operations.</summary>
internal static class CpuStressChild
{
    private static double checksum;
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1 || args[0] != "--cpu-stress-child") return 2;
        using var hardDeadline = new Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(120), Timeout.InfiniteTimeSpan);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(115));
        using var self = Process.GetCurrentProcess(); self.PriorityClass = ProcessPriorityClass.Normal;
        int workers = Math.Min(4, Math.Max(1, Environment.ProcessorCount - 2));
        int workMilliseconds = Environment.ProcessorCount == 1 ? 5 : 8, sleepMilliseconds = Environment.ProcessorCount == 1 ? 5 : 2;
        var emitGate = new SemaphoreSlim(1, 1);
        Task? programme = null;
        await Emit(new { Type = "ready", ProcessId = Environment.ProcessId, LogicalProcessors = Environment.ProcessorCount,
            Workers = workers, WorkMilliseconds = workMilliseconds, SleepMilliseconds = sleepMilliseconds,
            BurstStartSeconds = new[] { 20, 50 }, BurstSeconds = 10, Priority = self.PriorityClass.ToString(), HardDeadlineSeconds = 120 });
        try
        {
            while (true)
            {
                string? command = await Task.Run(Console.ReadLine).WaitAsync(lifetime.Token);
                if (command is null or "stop")
                {
                    lifetime.Cancel();
                    if (programme is not null) try { await programme; } catch (OperationCanceledException) { }
                    await Emit(new { Type = "stopped", ObservedQpc100ns = Now() }); return 0;
                }
                if (command != "go" || programme is not null) throw new InvalidOperationException("One go command, then stop or EOF, is required.");
                programme = Programme();
            }
        }
        catch (Exception ex)
        {
            lifetime.Cancel(); if (programme is not null) try { await programme; } catch { }
            await Emit(new { Type = "error", Error = ex.GetType().Name, ex.Message }); return 1;
        }

        async Task Programme()
        {
            var elapsed = Stopwatch.StartNew(); await Emit(new { Type = "go", ObservedQpc100ns = Now() });
            foreach (int offset in new[] { 20, 50 })
            {
                var delay = TimeSpan.FromSeconds(offset) - elapsed.Elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, lifetime.Token);
                TimeSpan cpuBefore = self.TotalProcessorTime;
                long begin = Now(); await Emit(new { Type = "burst_start", ProgrammeSecond = offset, ObservedQpc100ns = begin });
                long cutoff = Stopwatch.GetTimestamp() + 10L * Stopwatch.Frequency;
                var tasks = Enumerable.Range(0, workers).Select(index => Task.Factory.StartNew(() =>
                {
                    double value = index + .123;
                    while (!lifetime.IsCancellationRequested && Stopwatch.GetTimestamp() < cutoff)
                    {
                        long sliceEnd = Math.Min(cutoff, Stopwatch.GetTimestamp() + workMilliseconds * Stopwatch.Frequency / 1000);
                        while (!lifetime.IsCancellationRequested && Stopwatch.GetTimestamp() < sliceEnd)
                            for (int sample = 0; sample < 256; sample++) value = Math.Sqrt(value + 1.000001);
                        Volatile.Write(ref checksum, value);
                        Thread.Sleep(sleepMilliseconds);
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                await Task.WhenAll(tasks); lifetime.Token.ThrowIfCancellationRequested();
                await Emit(new { Type = "burst_end", ProgrammeSecond = offset, ObservedQpc100ns = Now(),
                    CpuSeconds = (self.TotalProcessorTime - cpuBefore).TotalSeconds, Workers = workers, Checksum = Volatile.Read(ref checksum) });
            }
            await Emit(new { Type = "done", ObservedQpc100ns = Now() });
        }
        async Task Emit(object value)
        { await emitGate.WaitAsync(); try { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value)); await Console.Out.FlushAsync(); } finally { emitGate.Release(); } }
    }

    private static long Now() => AudioClock.CounterTo100ns(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
}

internal sealed class OwnedCpuStress : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task pump;
    private readonly Task<string> stderr;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ConcurrentQueue<JsonElement> Events { get; } = new();
    internal int? ExitCode { get; private set; }
    internal JsonElement? ReadyInfo { get; private set; }
    private OwnedCpuStress(Process process)
    {
        this.process = process; stderr = process.StandardError.ReadToEndAsync();
        pump = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (line.Length > 8192) throw new InvalidDataException("CPU child output too large.");
                    using var value = JsonDocument.Parse(line); var item = value.RootElement.Clone(); Events.Enqueue(item);
                    if (item.GetProperty("Type").GetString() == "ready")
                    {
                        if (item.GetProperty("ProcessId").GetInt32() != process.Id || item.GetProperty("Priority").GetString() != "Normal")
                            throw new InvalidDataException("CPU child ready identity or priority differs.");
                        ReadyInfo = item; ready.TrySetResult();
                    }
                }
                ready.TrySetException(new IOException("CPU child exited before ready."));
            }
            catch (Exception ex) { ready.TrySetException(ex); throw; }
        });
    }
    internal static async Task<OwnedCpuStress> StartAsync(string root, CancellationToken token)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "ProjectEcho.Tests.exe");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        info.Environment["DOTNET_ROOT"] = Path.Combine(root, ".tools", "dotnet"); info.ArgumentList.Add("--cpu-stress-child");
        var child = new OwnedCpuStress(Process.Start(info) ?? throw new IOException("Could not start owned CPU child."));
        try
        {
            await child.ready.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            if (!string.Equals(child.process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) throw new IOException("CPU child executable identity differs.");
            return child;
        }
        catch { await child.DisposeAsync(); throw; }
    }
    internal async Task GoAsync(CancellationToken token)
    { await process.StandardInput.WriteLineAsync("go".AsMemory(), token); await process.StandardInput.FlushAsync(token); }
    public async ValueTask DisposeAsync()
    {
        if (ExitCode.HasValue) return;
        try
        {
            if (!process.HasExited)
            {
                try { await process.StandardInput.WriteLineAsync("stop"); process.StandardInput.Close(); } catch (IOException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
                catch (TimeoutException) { process.Kill(entireProcessTree: false); await process.WaitForExitAsync(); }
            }
            ExitCode = process.ExitCode; await pump; string error = await stderr;
            if (!string.IsNullOrWhiteSpace(error)) throw new IOException("CPU child stderr: " + error[..Math.Min(error.Length, 1024)]);
        }
        finally { process.Dispose(); }
    }
}
