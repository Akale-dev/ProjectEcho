using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using ProjectEcho.App;
using ProjectEcho.Capture.Windows;
using ProjectEcho.CaptureHost;
using ProjectEcho.Core;
using ProjectEcho.Export;

/// <summary>Records only a newly created WPF test player's process tree. No endpoint fallback or mic.</summary>
internal static class WpfPlaybackIntegration
{
    private const int Rate = 48000, Frames = Rate * 11 / 4;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1 || args[0] != "--wpf-playback-integration") return 2;
        string root = SyntheticAudioProbe.FindProjectRoot();
        string parent = SyntheticAudioProbe.SafeProjectPath(root, ".local/WpfPlaybackIntegration"); Directory.CreateDirectory(parent);
        string runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        string run = Path.Combine(parent, runId); Directory.CreateDirectory(run);
        var results = new List<object>(); var errors = new List<string>();
        try
        {
            string lockPath = SyntheticAudioProbe.SafeProjectPath(root, ".local/recording-probe.lock");
            if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Probe lock is a reparse point.");
            using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var existing = Process.GetProcessesByName("ProjectEcho.CaptureHost");
            try { if (existing.Length != 0) throw new IOException("Existing CaptureHost: no player or recording was started."); }
            finally { foreach (var process in existing) process.Dispose(); }
            File.WriteAllText(Path.Combine(run, "SYNTHETIC_OWNED_WPF_ONLY"), "Only a generated low-amplitude WPF test-player process is captured. Microphone disabled. No private files, meetings, external PID or system route/volume change.");
            string wav = Path.Combine(run, "signal.wav"), m4a = Path.Combine(run, "signal.m4a");
            WriteSignal(wav);
            using var codecLimit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var encoded = new MediaFoundationM4aEncoder().Encode(wav, m4a, codecLimit.Token);
            File.WriteAllText(Path.Combine(run, "fixture-proof.json"), JsonSerializer.Serialize(new
            {
                schema = "owned-wpf-markers-1", isSynthetic = true, frames = Frames, sampleRate = Rate, sourcePeakAtMost = .02,
                firstMarker = new { frequency = 660, startSeconds = .15, endSeconds = .35 },
                tailMarker = new { frequency = 880, startSeconds = 2.45, endSeconds = 2.65 },
                wavSha256 = Hash(wav), m4aSha256 = Hash(m4a), encoding = encoded
            }, Json));
            Console.WriteLine("Owned WPF playback integration: " + run);
            foreach (string format in new[] { "wav", "m4a" })
            {
                var result = await RunCaseAsync(root, runId, run, format);
                results.Add(result);
                if (!result.Passed) errors.Add(format + ": " + string.Join(" | ", result.Errors));
            }
        }
        catch (Exception ex) { errors.Add(ex.GetType().Name + ": " + ex.Message); File.WriteAllText(Path.Combine(run, "failure.txt"), ex.ToString()); }
        bool pass = errors.Count == 0 && results.Count == 2;
        var report = new { Status = pass ? "PASS_OWNED_WPF_RENDERED_MARKERS" : "FAIL_OR_BLOCKED", Os = Environment.OSVersion.Version.ToString(),
            Mode = "ProcessTree/exact newly created WPF test player; microphone disabled", Results = results, Errors = errors,
            Limitation = "Actual rendered first/tail marker evidence; no human listening claim. WMP NaturalDuration/EndedPosition are recorded but never used to stop, seek or truncate playback." };
        string reportPath = Path.Combine(run, "probe-report.json"); File.WriteAllText(reportPath, JsonSerializer.Serialize(report, Json));
        Console.WriteLine(report.Status + "; report: " + reportPath);
        return pass ? 0 : 1;
    }

    private static async Task<CaseResult> RunCaseAsync(string root, string runId, string run, string format)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(35)); var token = limit.Token;
        var errors = new List<string>(); var events = new ConcurrentQueue<object>();
        var client = new CaptureHostClient();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource<HostEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.EventReceived += value =>
        {
            if (value.Kind != "health") events.Enqueue(new { value.Kind, value.Message });
            if (value.Health is { } health && (health.MicrophoneEnabled || health.MicFrames != 0))
                terminal.TrySetException(new InvalidDataException("Unexpected microphone collection."));
            if (value.Kind == "started") started.TrySetResult();
            if (value.Kind is "recording_saved" or "recording_incomplete" or "error")
            { terminal.TrySetResult(value); started.TrySetException(new IOException("Host ended before ready.")); }
        };
        Process? player = null; Task<string>? stderr = null; JsonElement? ready = null, opened = null, done = null;
        RecordingResult? saved = null; M1RecoveryResult? recovered = null; MarkerResult? signal = null;
        bool owned = false, hostExited = false, unchanged = false; int? playerExit = null;
        string recording = Path.Combine(run, "recording-" + format); var hashes = new Dictionary<string, string>();
        try
        {
            string executable = Path.Combine(AppContext.BaseDirectory, "ProjectEcho.Tests.exe");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = root, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["DOTNET_ROOT"] = Path.Combine(root, ".tools", "dotnet");
            start.ArgumentList.Add("--wpf-playback-source"); start.ArgumentList.Add(runId); start.ArgumentList.Add(format);
            player = Process.Start(start) ?? throw new IOException("Could not create the owned WPF player.");
            stderr = player.StandardError.ReadToEndAsync();
            ready = await ReadEventAsync("ready");
            if (ready.Value.GetProperty("ProcessId").GetInt32() != player.Id || ready.Value.GetProperty("Format").GetString() != format)
                throw new InvalidDataException("WPF source ready identity mismatch.");
            var identity = new ProcessIdentity(player.Id, player.StartTime.ToUniversalTime().Ticks,
                player.MainModule?.FileName ?? throw new IOException("Owned player image unreadable."), player.ProcessName);
            if (!string.Equals(identity.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase)) throw new IOException("Owned executable path mismatch.");
            ProcessSource? selection = null; var discover = Stopwatch.StartNew();
            while (selection is null && discover.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (!SourceDiscovery.IdentityMatches(identity)) throw new IOException("Owned player exited during selection.");
                selection = SourceDiscovery.Enumerate().ProcessSources.FirstOrDefault(source => SyntheticAudioProbe.IsOwnedSelection(source, identity));
                if (selection is null) await Task.Delay(100, token);
            }
            if (selection is null) throw new IOException("No exact owned WPF-player audio session; no fallback attempted.");
            owned = SyntheticAudioProbe.IsOwnedSelection(selection, identity);
            var options = SyntheticAudioProbe.CreateOptions(selection, recording) with { Title = "WPF合成首尾标记验证", MicrophoneEnabled = false, MicrophoneDeviceId = null };
            await client.StartRecordingAsync(new RecordingOptions(options, 15, 1), root, () => token.IsCancellationRequested);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            await player.StandardInput.WriteLineAsync("go".AsMemory(), token); await player.StandardInput.FlushAsync(token);
            done = await ReadEventAsync("done");
            // MediaEnded is not an acoustic drain measurement. Preserve the following
            // 750ms of silence before the actual capture-thread Stop boundary.
            await Task.Delay(750, token);
            await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(3), token);
            var final = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            await client.WaitForExitAsync(); hostExited = true;
            saved = final.RecordingResult ?? throw new IOException("Missing final capture result.");
            foreach (string file in Directory.EnumerateFiles(recording, "*", SearchOption.AllDirectories)) hashes.Add(Path.GetRelativePath(recording, file), Hash(file));
            recovered = M1Recovery.Read(recording);
            if (!saved.StorageComplete || !recovered.IsCleanEnd) errors.Add("capture_storage_incomplete");
            if (recovered.Capture.Tracks.Any(track => track.SourceId != "remote") || Directory.Exists(Path.Combine(recording, "sources", "mic"))) errors.Add("unexpected_source_or_mic");
            signal = Analyze(ReadCapture(recovered.Capture));
            if (!signal.Passed) errors.Add("rendered_first_tail_marker_or_interval_failed");
            unchanged = hashes.All(pair => Hash(Path.Combine(recording, pair.Key)) == pair.Value);
            if (!unchanged) errors.Add("original_changed");
            if (opened is null || !opened.Value.GetProperty("HasAudio").GetBoolean() || done.Value.GetProperty("EndReason").GetString() != "MediaEnded")
                errors.Add("WPF_media_events_incomplete");
        }
        catch (Exception ex) { errors.Add(ex.GetType().Name + ": " + ex.Message); }
        finally
        {
            try { await client.DisposeAsync(); hostExited = true; } catch (Exception ex) { errors.Add("host_shutdown:" + ex.Message); }
            if (player is not null)
            {
                try
                {
                    if (!player.HasExited)
                    {
                        await player.StandardInput.WriteLineAsync("stop"); await player.StandardInput.FlushAsync(); player.StandardInput.Close();
                        try { await player.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                        catch (TimeoutException) { player.Kill(entireProcessTree: false); await player.WaitForExitAsync(); errors.Add("owned_player_stop_timeout"); }
                    }
                    playerExit = player.ExitCode;
                    string residual = await player.StandardOutput.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(residual)) File.WriteAllText(Path.Combine(run, "player-" + format + "-tail.jsonl"), residual);
                    if (stderr is not null) { string diagnostic = await stderr; if (!string.IsNullOrWhiteSpace(diagnostic)) File.WriteAllText(Path.Combine(run, "player-" + format + "-stderr.txt"), diagnostic); }
                }
                catch (Exception ex) { errors.Add("owned_player_shutdown:" + ex.Message); }
                finally { player.Dispose(); }
            }
        }
        if (!owned || !hostExited || playerExit != 0) errors.Add("owned_lifecycle_incomplete");
        if (recovered is null && hostExited && File.Exists(Path.Combine(recording, "journal", "capture-index.jsonl")))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(recording, "*", SearchOption.AllDirectories))
                    hashes.TryAdd(Path.GetRelativePath(recording, file), Hash(file));
                recovered = M1Recovery.Read(recording);
                signal = Analyze(ReadCapture(recovered.Capture));
                unchanged = hashes.All(pair => Hash(Path.Combine(recording, pair.Key)) == pair.Value);
            }
            catch (Exception ex) { errors.Add("failure_evidence_read:" + ex.Message); }
        }
        return new(format, errors.Count == 0, ready, opened, done, signal, saved, recovered?.IsCleanEnd,
            owned, hostExited, playerExit, unchanged, hashes, events.ToArray(), errors);

        async Task<JsonElement> ReadEventAsync(string wanted)
        {
            while (true)
            {
                using var readLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
                readLimit.CancelAfter(TimeSpan.FromSeconds(10));
                string? line = await player!.StandardOutput.ReadLineAsync(readLimit.Token);
                if (line is null) throw new IOException("Owned WPF player exited before " + wanted);
                if (line.Length > 8192) throw new InvalidDataException("Oversized WPF player event.");
                using var item = JsonDocument.Parse(line); string type = item.RootElement.GetProperty("Type").GetString()!;
                events.Enqueue(new { PlayerEventReadAtUtc = DateTimeOffset.UtcNow, PlayerEvent = item.RootElement.Clone() });
                if (type == "error") throw new IOException("WPF player: " + item.RootElement.GetProperty("Message").GetString());
                if (type == "opened") opened = item.RootElement.Clone();
                if (type == wanted) return item.RootElement.Clone();
            }
        }
    }

    private static void WriteSignal(string path)
    {
        using var wav = new WaveFileWriter(path, new WaveFormat(Rate, 16, 1));
        var samples = Signal(); var pcm = new byte[samples.Length * 2];
        for (int frame = 0; frame < samples.Length; frame++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * 2, 2), (short)Math.Round(samples[frame] * 32767));
        wav.Write(pcm, 0, pcm.Length); wav.Flush();
    }

    private static float[] Signal()
    {
        var samples = new float[Frames];
        foreach (var marker in new[] { (Start: .15, Frequency: 660), (Start: 2.45, Frequency: 880) })
        {
            int begin = (int)Math.Round(marker.Start * Rate), count = Rate / 5;
            for (int i = 0; i < count; i++)
            {
                double edge = Math.Clamp(Math.Min(i, count - 1 - i) / (Rate * .005), 0, 1);
                samples[begin + i] = (float)(.02 * edge * Math.Sin(2 * Math.PI * marker.Frequency * i / Rate));
            }
        }
        return samples;
    }

    private static float[] ReadCapture(CaptureResult capture)
    {
        int length = checked((int)((capture.Duration100ns * (Int128)Rate + 9999999) / 10000000));
        if (length <= 0 || length > Rate * 20) throw new InvalidDataException("Unexpected integration capture length.");
        var samples = new float[length];
        foreach (var track in capture.Tracks.Where(track => track.SourceId == "remote"))
        {
            if (track.Format.SampleRate != Rate || !track.Format.IsFloat || track.Format.BitsPerSample != 32 || track.Format.Channels is < 1 or > 2)
                throw new InvalidDataException("Expected production 48kHz Float32 process-loopback originals.");
            using var wave = new WaveFileReader(track.WavePath);
            foreach (var packet in track.Packets)
            {
                if (!packet.TimestampValid) throw new InvalidDataException("Invalid QPC cannot establish rendered marker timing.");
                long start = AudioClock.FrameAt(packet.QpcPosition100ns, capture.EpochQpc100ns, Rate) + packet.RetainedFrameOffset;
                if (start < 0 || start + packet.FrameCount > length + 1) throw new InvalidDataException("Packet outside confirmed session timeline.");
                wave.Position = packet.WaveFrameOffset * track.Format.BlockAlign;
                var bytes = new byte[packet.FrameCount * track.Format.BlockAlign]; wave.ReadExactly(bytes);
                for (int frame = 0; frame < packet.FrameCount && start + frame < length; frame++)
                {
                    double value = 0;
                    for (int channel = 0; channel < track.Format.Channels; channel++) value += BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(frame * track.Format.BlockAlign + channel * 4, 4));
                    samples[start + frame] = (float)(value / track.Format.Channels);
                }
            }
        }
        return samples;
    }

    private static MarkerResult Analyze(float[] samples)
    {
        var first = FindTone(samples, 660); var tail = FindTone(samples, 880);
        double? interval = first.Found && tail.Found ? tail.OnsetSeconds - first.OnsetSeconds : null;
        bool pass = first.Found && tail.Found && Math.Abs(interval!.Value - 2.30) <= .04
            && first.DurationSeconds is >= .14 and <= .27 && tail.DurationSeconds is >= .14 and <= .27;
        return new(pass, first, tail, interval, interval is null ? null : .15 + interval, 2.30, .04);
    }

    private static ToneMark FindTone(float[] samples, int frequency)
    {
        const int window = 960, hop = 240;
        int count = Math.Max(0, (samples.Length - window) / hop + 1); var amplitude = new double[count];
        var sine = new double[window]; var cosine = new double[window]; double weight = 0;
        for (int i = 0; i < window; i++)
        {
            double w = .5 - .5 * Math.Cos(2 * Math.PI * i / (window - 1)); weight += w;
            sine[i] = w * Math.Sin(2 * Math.PI * frequency * i / Rate); cosine[i] = w * Math.Cos(2 * Math.PI * frequency * i / Rate);
        }
        for (int step = 0; step < count; step++)
        {
            double a = 0, b = 0;
            for (int i = 0; i < window; i++) { a += samples[step * hop + i] * sine[i]; b += samples[step * hop + i] * cosine[i]; }
            amplitude[step] = 2 * Math.Sqrt(a * a + b * b) / weight;
        }
        double peak = amplitude.Length == 0 ? 0 : amplitude.Max(); double threshold = Math.Max(.0001, peak * .25);
        int first = Array.FindIndex(amplitude, value => value >= threshold), last = Array.FindLastIndex(amplitude, value => value >= threshold);
        return first < 0 ? new(false, 0, 0, peak) : new(true, (first * hop + window / 2d) / Rate, (last - first + 1) * hop / (double)Rate, peak);
    }

    internal static void RunSyntheticChecks()
    {
        var complete = Signal();
        if (!Analyze(complete).Passed) throw new InvalidDataException("Full WPF marker fixture was rejected.");
        var cutAtIntegerDuration = complete.ToArray(); Array.Clear(cutAtIntegerDuration, Rate * 2, cutAtIntegerDuration.Length - Rate * 2);
        if (Analyze(cutAtIntegerDuration).Passed) throw new InvalidDataException("Integer-duration truncation escaped the tail-marker test.");
        var shiftedTail = new float[Frames + Rate / 5];
        Array.Copy(complete, 0, shiftedTail, 0, Rate); Array.Copy(complete, Rate, shiftedTail, Rate + Rate / 5, complete.Length - Rate);
        if (Analyze(shiftedTail).Passed) throw new InvalidDataException("200ms tail timing error escaped the marker interval test.");
        if (Analyze(new float[Frames]).Passed) throw new InvalidDataException("Silence passed the WPF rendered marker test.");
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private sealed record ToneMark(bool Found, double OnsetSeconds, double DurationSeconds, double PeakAmplitude);
    private sealed record MarkerResult(bool Passed, ToneMark First, ToneMark Tail, double? FirstToTailSeconds, double? TailContentPositionSeconds, double ExpectedIntervalSeconds, double ToleranceSeconds);
    private sealed record CaseResult(string Format, bool Passed, JsonElement? Ready, JsonElement? Opened, JsonElement? Done,
        MarkerResult? Signal, RecordingResult? CaptureResult, bool? OfflineCleanEnd, bool OwnedSelection, bool HostExited, int? PlayerExitCode,
        bool OriginalsUnchanged, IReadOnlyDictionary<string, string> OriginalHashes, IReadOnlyList<object> Events, IReadOnlyList<string> Errors);
}
