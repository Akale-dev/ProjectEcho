using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using ProjectEcho.App;
using ProjectEcho.Capture.Windows;
using ProjectEcho.CaptureHost;
using ProjectEcho.Core;
using ProjectEcho.Export;

/// <summary>Explicit opt-in integration test. Only newly spawned synthetic producers may be captured.</summary>
internal static class SyntheticAudioProbe
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1 || args[0] != "--synthetic-audio-probe") return 2;
        string root = FindProjectRoot();
        string baseDirectory = SafeProjectPath(root, ".local/AudioIntegration");
        Directory.CreateDirectory(baseDirectory);
        var lockPath = Path.Combine(root, ".local", "recording-probe.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Probe lock is a reparse point.");
        using var runLock = new FileStream(lockPath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var activeHosts = Process.GetProcessesByName("ProjectEcho.CaptureHost");
        try
        {
            if (activeHosts.Length != 0) { Console.Error.WriteLine("An existing CaptureHost is active; probe refused."); return 2; }
        }
        finally { foreach (var process in activeHosts) process.Dispose(); }

        var run = Path.Combine(baseDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "SYNTHETIC_SOURCE_REAL_WASAPI"),
            "Real Windows process-loopback capture of test-owned generated tones; not a meeting or a mocked capture.");
        var recording = Path.Combine(run, "recording");
        Console.WriteLine("Live probe: test-owned process tree only; microphone OFF; no endpoint fallback.");
        Console.WriteLine("Evidence: " + run);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var events = new ConcurrentQueue<object>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource<HostEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureClient = new CaptureHostClient(); // The exact IPC client source used by the WPF app.
        Producer? target = null, control = null;
        CaptureResult? result = null;
        ProbeSignalReport? signal = null;
        var auditErrors = new List<string>();
        var auditObservations = new List<object>();
        string? failure = null, previewFailure = null;
        bool hostExited = false, targetDone = false, controlDone = false;
        bool ownedSelection = false, micFilesAbsent = false;
        bool producerCleanup = true;
        int? targetExit = null, controlExit = null;
        var sourceHashes = new Dictionary<string, string>();
        captureClient.EventReceived += value =>
        {
            if (value.Kind != "levels") events.Enqueue(new { value.Kind, qpc100ns = Now100ns().ToString() });
            if (value.Kind == "started") started.TrySetResult();
            if (value.Kind is "saved" or "incomplete" or "error")
            {
                terminal.TrySetResult(value);
                if (!started.Task.IsCompleted) started.TrySetException(new IOException("Host terminated before started."));
            }
        };
        try
        {
            target = await Producer.StartAsync(root, "target", deadline.Token);
            control = await Producer.StartAsync(root, "control", deadline.Token);
            var selection = await FindOwnedSourceAsync(target, deadline.Token);
            _ = await FindOwnedSourceAsync(control, deadline.Token);
            ownedSelection = IsOwnedSelection(selection, target.Identity);
            if (!ownedSelection) throw new InvalidOperationException("Selected source is not the newly created producer.");
            var options = CreateOptions(selection, recording);
            var preflight = SourceDiscovery.Preflight(options);
            if (!preflight.CanStart) throw new IOException("Production preflight refused the synthetic source.");
            await captureClient.StartAsync(options, root, () => deadline.IsCancellationRequested);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            // Readiness, not a sleep or a slow UI click, controls the start of all audible samples.
            await BeginTonesAfterStartedAsync(started.Task,
                () => Task.WhenAll(target.GoAsync(deadline.Token), control.GoAsync(deadline.Token)), deadline.Token);
            events.Enqueue(new { Kind = "both-tone-go-sent", qpc100ns = Now100ns().ToString() });
            await Task.WhenAll(target.WaitDoneAsync(deadline.Token), control.WaitDoneAsync(deadline.Token));
            targetDone = controlDone = true;
            events.Enqueue(new { Kind = "both-tone-submitted-plus-500ms-silence", qpc100ns = Now100ns().ToString() });
            await captureClient.StopAsync().WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
            var final = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(20), deadline.Token);
            await captureClient.WaitForExitAsync();
            hostExited = true;
            result = final.Result ?? throw new IOException("Host did not return a capture result.");
            if (result.Tracks.Count != 1 || result.Tracks[0].SourceId != "remote")
                throw new InvalidDataException("Expected one remote track and no microphone track.");
            micFilesAbsent = !Directory.EnumerateFiles(recording, "mic*", SearchOption.AllDirectories).Any();
            foreach (var path in Directory.EnumerateFiles(recording, "*", SearchOption.AllDirectories))
                sourceHashes.Add(Path.GetRelativePath(recording, path), HashFile(path));
            signal = ProbeSignalChecks.Analyze(result.Tracks[0].WavePath);
            if (final.MixedPath is null)
            {
                try { TimelineWaveRenderer.Render(result, Path.Combine(recording, "mixed-preview-incomplete-" + Guid.NewGuid().ToString("N") + ".wav")); }
                catch (Exception ex) { previewFailure = ex.GetType().Name; }
            }
            // Never convert a device clock failure into a pass because the tone is audible.
            RecordingAudit.Inspect(recording, auditErrors, auditObservations);
            foreach (var (relative, hash) in sourceHashes)
                if (HashFile(Path.Combine(recording, relative)) != hash) throw new IOException("Original changed during offline verification.");
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            File.WriteAllText(Path.Combine(run, "failure.txt"), ex.ToString());
        }
        finally
        {
            try { await captureClient.DisposeAsync(); hostExited = true; }
            catch (Exception ex) { failure ??= "Host shutdown: " + ex.GetType().Name; }
            // Keep producers alive and silent until capture has really stopped, so PID identity
            // polling is not itself the reason capture terminates. Only owned producers may be killed.
            try { if (target is not null) { targetExit = await target.StopAsync(); producerCleanup &= targetExit == 0; } }
            catch (Exception ex) { producerCleanup = false; failure ??= "Target cleanup: " + ex.GetType().Name; }
            try { if (control is not null) { controlExit = await control.StopAsync(); producerCleanup &= controlExit == 0; } }
            catch (Exception ex) { producerCleanup = false; failure ??= "Control cleanup: " + ex.GetType().Name; }
        }
        bool auditPass = result is not null && failure is null && auditErrors.Count == 0;
        bool pass = failure is null && hostExited && producerCleanup && ownedSelection && micFilesAbsent
            && targetDone && controlDone && signal?.SignalPass == true && auditPass;
        using var os = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var report = new
        {
            schemaVersion = 1, checkedAtUtc = DateTimeOffset.UtcNow, status = pass ? "PASS" : "FAIL",
            test = "Synthetic producers through production CaptureHost IPC and Windows process loopback",
            environment = new { osBuild = os?.GetValue("CurrentBuildNumber"), ubr = os?.GetValue("UBR"),
                displayVersion = os?.GetValue("DisplayVersion"), architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                sdkPinned = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "global.json"))).RootElement.GetProperty("sdk").GetProperty("version").GetString(),
                naudio = typeof(NAudio.CoreAudioApi.AudioClient).Assembly.GetName().Version?.ToString() },
            sourceMode = "ProcessTree", microphoneEnabled = false, endpointFallback = false,
            ownedSelection, hostExited, producerCleanup, targetExit, controlExit, targetDone, controlDone,
            micFilesAbsent, signal, captureComplete = result?.Complete, previewFailure, failure,
            clockAndStructure = new { status = auditPass ? "PASS_WITH_LIMITATIONS" : "FAIL", errorCount = auditErrors.Count,
                errorsByType = auditErrors.GroupBy(x => string.Join(':', x.Split(':').Take(2))).ToDictionary(g => g.Key, g => g.Count()),
                firstErrors = auditErrors.Take(12), observations = auditObservations },
            originalHashes = sourceHashes, events = events.ToArray(),
            notEstablished = new[] { "Human listening", "Zoom/Webex", "Browser isolation", "Independent measurement of nonzero control output", "Hardware microphone privacy", "M1 stop-last-frame / recovery / two-hour drift" }
        };
        File.WriteAllText(Path.Combine(run, "probe-report.json"), JsonSerializer.Serialize(report, Json));
        File.WriteAllText(Path.Combine(run, "audit-errors.json"), JsonSerializer.Serialize(auditErrors, Json));
        Console.WriteLine($"{report.status}; signal={signal?.SignalPass}; captureComplete={result?.Complete}; auditErrors={auditErrors.Count}; hostExited={hostExited}; mic=OFF");
        if (failure is not null) Console.Error.WriteLine(failure);
        Console.WriteLine("Report: " + Path.Combine(run, "probe-report.json"));
        return pass ? 0 : 1;
    }

    internal static CaptureOptions CreateOptions(ProcessSource source, string directory) =>
        new(SourceMode.ProcessTree, source, null, false, false, null, directory, "自动合成音测试", 10);

    internal static bool IsOwnedSelection(ProcessSource source, ProcessIdentity identity) =>
        source.Root == identity && source.AudioProcess == identity;

    internal static async Task BeginTonesAfterStartedAsync(Task started, Func<Task> go, CancellationToken cancellationToken)
    {
        await started.WaitAsync(cancellationToken);
        await go();
    }

    internal static async Task<ProcessSource> FindOwnedSourceAsync(Producer producer, CancellationToken token)
    {
        var until = Stopwatch.StartNew();
        while (until.Elapsed < TimeSpan.FromSeconds(8))
        {
            token.ThrowIfCancellationRequested();
            if (!SourceDiscovery.IdentityMatches(producer.Identity)) throw new IOException("Synthetic producer exited or identity changed.");
            var candidate = SourceDiscovery.Enumerate().ProcessSources.FirstOrDefault(x => IsOwnedSelection(x, producer.Identity));
            if (candidate is not null) return candidate;
            await Task.Delay(100, token);
        }
        throw new IOException("No actual audio session belonging to the test producer was found; no fallback attempted.");
    }

    internal static string SafeProjectPath(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Probe output escaped the project.");
        for (var directory = new DirectoryInfo(full); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Probe path contains a reparse point.");
        return full;
    }

    internal static string FindProjectRoot()
    {
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent)
            if (File.Exists(Path.Combine(root.FullName, "AGENTS.md")) && File.Exists(Path.Combine(root.FullName, "global.json"))) return root.FullName;
        throw new DirectoryNotFoundException("Run the probe inside ProjectEcho.");
    }
    private static ulong Now100ns() => (ulong)AudioClock.CounterTo100ns(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
    private static string HashFile(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }

    internal sealed class Producer(Process process, string role)
    {
        private ProcessIdentity? identity;
        private readonly SemaphoreSlim stdoutGate = new(1, 1);
        internal JsonElement? ReadyInfo { get; private set; }
        internal string? FailureDiagnostic { get; private set; }
        internal ProcessIdentity Identity => identity ?? throw new InvalidOperationException("Producer has not completed its ready handshake.");
        private readonly Task<string> stderr = process.StandardError.ReadToEndAsync();
        internal static async Task<Producer> StartAsync(string root, string role, CancellationToken token, string? outputDeviceId = null)
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "ProjectEcho.Tests.exe");
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = root
            };
            info.Environment["DOTNET_ROOT"] = Path.Combine(root, ".tools", "dotnet");
            info.ArgumentList.Add("--probe-tone-source");
            info.ArgumentList.Add(role);
            if (outputDeviceId is not null) info.ArgumentList.Add(outputDeviceId);
            var child = new Producer(Process.Start(info) ?? throw new IOException("Could not start synthetic producer."), role);
            try
            {
                await child.WaitEventAsync("ready", token);
                child.identity = child.ReadIdentity();
                if (!string.Equals(child.identity.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase)) throw new IOException("Producer image path mismatch.");
                return child;
            }
            catch { await child.StopAsync(); throw; }
        }
        private ProcessIdentity ReadIdentity() => new(process.Id, process.StartTime.ToUniversalTime().Ticks,
            process.MainModule?.FileName ?? throw new IOException("Producer image identity is unavailable."), process.ProcessName);
        internal async Task GoAsync(CancellationToken token)
        {
            await process.StandardInput.WriteLineAsync("go".AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
        }
        internal Task WaitDoneAsync(CancellationToken token) => WaitEventAsync("done", token);
        private async Task WaitEventAsync(string kind, CancellationToken token)
        {
            while (true)
            {
                string? line;
                await stdoutGate.WaitAsync(token);
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(
                        kind == "done" ? ProbeToneSource.ExpectedFrames(role) / ProbeToneSource.SampleRate + 30 : 10), token);
                }
                finally { stdoutGate.Release(); }
                if (line is null) throw new IOException("Synthetic producer exited before " + kind + ".");
                if (line.Length > 8192) throw new InvalidDataException("Oversized producer event.");
                using var value = JsonDocument.Parse(line);
                var type = value.RootElement.GetProperty("Type").GetString();
                if (type == "error") { FailureDiagnostic = line; throw new IOException("Synthetic producer reported an audio output failure."); }
                if (type != kind) continue;
                if (kind == "ready" && (value.RootElement.GetProperty("ProcessId").GetInt32() != process.Id
                    || value.RootElement.GetProperty("Role").GetString() != role)) throw new IOException("Producer handshake mismatch.");
                if (kind == "ready") ReadyInfo = value.RootElement.Clone();
                if (kind == "done" && (value.RootElement.GetProperty("Role").GetString() != role
                    || value.RootElement.GetProperty("SubmittedFrames").GetInt32() != ProbeToneSource.ExpectedFrames(role)))
                    throw new IOException("Producer did not submit the complete signal.");
                return;
            }
        }
        internal async Task<int> StopAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await process.StandardInput.WriteLineAsync("stop".AsMemory(), timeout.Token); await process.StandardInput.FlushAsync(timeout.Token); }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { }
                    process.StandardInput.Close();
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (TimeoutException) { process.Kill(entireProcessTree: false); await process.WaitForExitAsync(); return -1; }
                }
                // A pending WaitDone reader may still be receiving EOF from the
                // just-exited process. Diagnostic reads must not race that reader.
                if (await stdoutGate.WaitAsync(TimeSpan.FromSeconds(2)))
                {
                    try
                    {
                        var remainingOutput = await process.StandardOutput.ReadToEndAsync();
                        foreach (string line in remainingOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            try
                            {
                                using var message = JsonDocument.Parse(line);
                                if (message.RootElement.TryGetProperty("Type", out var type) && type.GetString() == "error") FailureDiagnostic = line;
                            }
                            catch (JsonException) { }
                        }
                    }
                    finally { stdoutGate.Release(); }
                }
                string diagnosticError = await stderr;
                if (!string.IsNullOrWhiteSpace(diagnosticError)) FailureDiagnostic ??= diagnosticError.Length > 8192 ? diagnosticError[..8192] : diagnosticError;
                return process.ExitCode;
            }
            finally { process.Dispose(); }
        }
    }
}
