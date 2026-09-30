using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using ProjectEcho.App;
using ProjectEcho.Capture.Windows;
using ProjectEcho.CaptureHost;
using ProjectEcho.Core;
using ProjectEcho.Export;

/// <summary>Opt-in owned-source capture while an unrelated synthetic export fails or is cancelled.</summary>
internal static class M2ConcurrentExportProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try { return await RunCoreAsync(args); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Concurrent export probe stopped without starting another capture: " + ex);
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] != "--m2-concurrent-export-probe" ||
            (args.Length == 3 && args[2] != "--require-blocked-network")) return 2;
        bool offline = args.Length == 3;
        string root = SyntheticAudioProbe.FindProjectRoot();
        string parent = SyntheticAudioProbe.SafeProjectPath(root, ".local/M2ConcurrentIntegration");
        Directory.CreateDirectory(parent);
        string directory = Path.Combine(parent, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var errors = new List<string>();
        var observations = new List<object>();
        object? network = null;
        if (offline)
        {
            // TEST-NET-1 is documentation address space: no HTTP request or application data is sent.
            // Only an explicit policy denial qualifies, not an ordinary timeout or unreachable peer.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 443), timeout.Token);
                errors.Add("network_not_demonstrably_blocked");
                network = new { Outcome = "CONNECT_SUCCEEDED", ApplicationPayloadSent = false };
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
            { network = new { Outcome = "POLICY_ACCESS_DENIED", NativeError = ex.NativeErrorCode, ApplicationPayloadSent = false }; }
            catch (Exception ex)
            {
                errors.Add("network_policy_not_verified:" + ex.GetType().Name);
                network = new { Outcome = "UNVERIFIED", Error = ex.Message, ApplicationPayloadSent = false };
            }
            if (errors.Count != 0) return WriteReport("BLOCKED_NETWORK_PRECONDITION", null, null, false, null, false);
        }
        string lockPath = SyntheticAudioProbe.SafeProjectPath(root, ".local/recording-probe.lock");
        using var runLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var existing = Process.GetProcessesByName("ProjectEcho.CaptureHost");
        try
        {
            if (existing.Length != 0)
            {
                errors.Add("existing_capture_host_no_new_capture_started");
                return WriteReport("BLOCKED_EXISTING_CAPTURE", null, null, false, null, false);
            }
        }
        finally { foreach (var process in existing) process.Dispose(); }

        string oldSession = Path.Combine(directory, "synthetic-old-meeting");
        CreateSynthetic(oldSession);
        var originalHashes = Hashes(oldSession);
        var oldMix = M2ExportService.RenderMeeting(oldSession, encoder: null);
        string captureDirectory = Path.Combine(directory, "new-owned-recording");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var client = new CaptureHostClient();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource<HostEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingHealth? latest = null;
        var events = new ConcurrentQueue<object>();
        client.EventReceived += value =>
        {
            if (value.Health is { } health) Volatile.Write(ref latest, health);
            else events.Enqueue(new { value.Kind, value.Message });
            if (value.Kind == "started") started.TrySetResult();
            if (value.Kind is "recording_saved" or "recording_incomplete" or "error")
            {
                terminal.TrySetResult(value);
                started.TrySetException(new IOException("Host ended before startup."));
            }
        };
        SyntheticAudioProbe.Producer? producer = null;
        RecordingResult? saved = null;
        M1RecoveryResult? recovered = null;
        FullMixResult? newMix = null;
        bool hostExited = false, unchanged = false;
        int? producerExit = null;
        try
        {
            producer = await SyntheticAudioProbe.Producer.StartAsync(root, "target-m1", deadline.Token, args[1]);
            var selection = await SyntheticAudioProbe.FindOwnedSourceAsync(producer, deadline.Token);
            if (!SyntheticAudioProbe.IsOwnedSelection(selection, producer.Identity)) throw new IOException("Owned source identity changed.");
            var options = SyntheticAudioProbe.CreateOptions(selection, captureDirectory) with
            { MicrophoneEnabled = false, MicrophoneDeviceId = null, Title = "导出故障隔离受控测试（mic关闭）" };
            await client.StartRecordingAsync(new RecordingOptions(options, 40, 1), root, () => deadline.IsCancellationRequested);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(12), deadline.Token);
            await producer.GoAsync(deadline.Token);
            await WaitForFrames(0, 48000);

            foreach (bool cancel in new[] { false, true })
            {
                using var stopExport = new CancellationTokenSource();
                using var encoder = new GatedEncoder(cancel);
                string output = Path.Combine(directory, cancel ? "cancelled-export" : "failed-export");
                var export = Task.Run(() => M2ExportService.ExportParts(oldMix,
                    new ExportOptions(M2ExportFormat.M4a, 20000, 1, 0), output, encoder, stopExport.Token));
                try
                {
                    if (!encoder.Entered.Wait(TimeSpan.FromSeconds(10), deadline.Token))
                        throw new IOException("Export did not reach its controlled encoder.");
                    long before = Volatile.Read(ref latest)?.RemoteFrames ?? 0;
                    await WaitForFrames(before, 48000);
                    long whileBlocked = Volatile.Read(ref latest)!.RemoteFrames;
                    if (cancel) stopExport.Cancel(); else encoder.Release.Set();
                    bool expectedFailure = false;
                    try { await export.WaitAsync(TimeSpan.FromSeconds(10), deadline.Token); }
                    catch (OperationCanceledException) when (cancel) { expectedFailure = true; }
                    catch (IOException) when (!cancel) { expectedFailure = true; }
                    if (!expectedFailure) throw new IOException("Export failure/cancellation was falsely reported complete.");
                    await WaitForFrames(whileBlocked, 48000);
                    bool filesCleared = !Directory.Exists(output) || !Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Any();
                    if (!filesCleared) errors.Add("derived_partial_files_left_after_" + (cancel ? "cancel" : "failure"));
                    observations.Add(new { Operation = cancel ? "cancel" : "failure", BeforeFrames = before,
                        DuringBlockedExportFrames = whileBlocked, AfterFrames = Volatile.Read(ref latest)!.RemoteFrames,
                        ExpectedFailureObserved = expectedFailure, DerivedFilesCleared = filesCleared });
                }
                finally
                {
                    stopExport.Cancel(); encoder.Release.Set();
                    try { await export.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
                }
            }
            await client.StopAsync();
            var final = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(30), deadline.Token);
            await client.WaitForExitAsync(); hostExited = true;
            producerExit = await producer.StopAsync(); producer = null;
            saved = final.RecordingResult ?? throw new IOException("No final recording result.");
            recovered = M1Recovery.Read(captureDirectory);
            if (!saved.StorageComplete || !recovered.IsCleanEnd || recovered.Issues.Count != 0) errors.Add("new_recording_not_clean");
            if (recovered.Capture.Tracks.Any(track => track.SourceId != "remote") ||
                (Volatile.Read(ref latest)?.MicFrames ?? 0) != 0) errors.Add("unexpected_microphone_data");
            var capturedHashes = Hashes(captureDirectory);
            newMix = M2ExportService.RenderMeeting(captureDirectory);
            if (newMix.CompressedPath is null) errors.Add("new_recording_compressed_mix_failed");
            var parts = M2ExportService.ExportParts(newMix,
                new ExportOptions(M2ExportFormat.M4a, 20000, 2, .1, true), Path.Combine(directory, "successful-new-export"));
            if (parts.Parts.Count == 0 || parts.Parts[^1].EndFrame != newMix.FrameCount) errors.Add("new_export_tail_missing");
            unchanged = Same(originalHashes, Hashes(oldSession)) && Same(capturedHashes, Hashes(captureDirectory));
            if (!unchanged) errors.Add("original_changed_by_export");
        }
        catch (Exception ex)
        { errors.Add(ex.GetType().Name + ":" + ex.Message); File.WriteAllText(Path.Combine(directory, "failure.txt"), ex.ToString()); }
        finally
        {
            try { await client.DisposeAsync(); hostExited = true; } catch (Exception ex) { errors.Add("host_shutdown:" + ex.Message); }
            if (producer is not null)
                try { producerExit = await producer.StopAsync(); } catch (Exception ex) { errors.Add("producer_shutdown:" + ex.Message); }
        }
        if (!hostExited || producerExit != 0) errors.Add("process_lifecycle_incomplete");
        return WriteReport(errors.Count == 0 ? offline ? "PASS_RESTRICTED_NETWORK_CONCURRENT_EXPORT" : "PASS_CONCURRENT_EXPORT" : "FAIL",
            saved, newMix, hostExited, producerExit, unchanged);

        async Task WaitForFrames(long baseline, long advance)
        {
            var limit = Stopwatch.StartNew();
            while ((Volatile.Read(ref latest)?.RemoteFrames ?? 0) < baseline + advance)
            {
                if (terminal.Task.IsCompleted) throw new IOException("Capture ended during independent export work.");
                var health = Volatile.Read(ref latest);
                if (health?.MicrophoneEnabled == true || health?.MicFrames > 0 || health?.Paused == true)
                    throw new IOException("Unexpected microphone or pause state.");
                if (limit.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Recording did not advance while export was active.");
                await Task.Delay(50, deadline.Token);
            }
        }

        int WriteReport(string status, RecordingResult? result, FullMixResult? mix, bool exited, int? sourceExit, bool originalsUnchanged)
        {
            var report = new { Status = status, FinishedAtUtc = DateTimeOffset.UtcNow,
                Os = Environment.OSVersion.Version.ToString(), Mode = "ProcessTree/test-owned; microphone disabled",
                FixedTestOutput = args[1], NetworkRestrictionRequired = offline, Network = network,
                Capture = result, Mix = mix, HostExited = exited, ProducerExitCode = sourceExit,
                OriginalsUnchanged = originalsUnchanged, ExportFaults = observations, Errors = errors,
                CleanWindows = "NOT_RUN; developer machine", ModelsUsed = false, Uploaded = false };
            string path = Path.Combine(directory, "probe-report.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(status + "\nReport: " + path);
            return status.StartsWith("PASS_", StringComparison.Ordinal) ? 0 : 1;
        }
    }

    private sealed class GatedEncoder(bool cancelled) : IM2AudioEncoder, IDisposable
    {
        public string Extension => ".m4a";
        public int BitsPerSecond => 96000;
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        public AudioEncodingResult Encode(string source, string destination, CancellationToken token)
        {
            File.WriteAllText(destination, "synthetic-incomplete-encoder-output"); Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(20), token)) throw new TimeoutException("Controlled encoder gate timed out.");
            token.ThrowIfCancellationRequested();
            throw new IOException(cancelled ? "Cancelled test encoder unexpectedly released." : "Injected encoder failure.");
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    private static void CreateSynthetic(string directory)
    {
        const int rate = 48000; const ulong epoch = 10_000_000_000;
        using var storage = new M1Storage(directory, epoch, DateTimeOffset.UtcNow, "合成旧会议", true);
        storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
        for (int packet = 0; packet < 300; packet++)
        {
            var pcm = new byte[480 * 4];
            for (int index = 0; index < 480; index++)
                BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan(index * 4), .1f * (float)Math.Sin(2 * Math.PI * 331 * (packet * 480 + index) / rate));
            storage.WritePacket("remote-1", pcm, new(packet * 480, 480, 480, (ulong)packet * 480, epoch + (ulong)packet * 100000, 0, true));
        }
        storage.WriteBoundary("remote-1", "stop", "stop", epoch + 30_000_000, 144000, 0, null);
        storage.Finish(30_000_000, true, []);
    }

    private static Dictionary<string, string> Hashes(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Where(path => !Path.GetRelativePath(directory, path).StartsWith("derived" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .ToDictionary(path => Path.GetRelativePath(directory, path), path => { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); });
    private static bool Same(Dictionary<string, string> left, Dictionary<string, string> right) =>
        left.OrderBy(pair => pair.Key).SequenceEqual(right.OrderBy(pair => pair.Key));
}
