using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.CaptureHost;
using ProjectEcho.Core;

internal static partial class M3mAutoStopTests
{
    private const ulong Epoch = 10_000_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3m-auto-stop");
        run("T238-SYNTH M3M opt-in options and legacy IPC defaults", Options);
        foreach (int minutes in new[] { 1, 5, 10 })
            run($"T239-SYNTH M3M exact {minutes} minute inactivity boundary", () => Threshold(minutes));
        run("T239-SYNTH M3M brief sound resets activity and invalid peaks do not", SoundReset);
        run("T239-SYNTH M3M pause and microphone preparation cannot expire timer", PausedMonitor);
        run("T240-SYNTH M3M silent auto-stop drains all frames and persists one decision", () => Silence(Path.Combine(root, "silent")));
        run("T239/T240-SYNTH M3M deadline-crossing packet voice resets timer before publication", () => CrossingVoice(Path.Combine(root, "crossing"), false));
        run("T239/T240-SYNTH M3M future packet voice cannot postpone an elapsed deadline", () => CrossingVoice(Path.Combine(root, "future"), true));
        run("T240-SYNTH M3M no packets are recorded as missing and incomplete", () => NoPackets(Path.Combine(root, "missing")));
        run("T239-SYNTH M3M microphone speech resets combined inactivity", () => Microphone(Path.Combine(root, "mic")));
        run("T239-SYNTH M3M mic-off collects no more PCM and resets interval", () => MicOff(Path.Combine(root, "mic-off")));
        run("T239-SYNTH M3M resume gives a fresh interval after long pause", () => Resume(Path.Combine(root, "pause")));
        run("T241-SYNTH M3M selected root exit auto-stops while paused", () => ProcessExit(Path.Combine(root, "exit")));
        run("T241-SYNTH M3M child identity failure is not root exit", () => ChildFailure(Path.Combine(root, "child")));
        run("T238-SYNTH M3M disabled policy never watches process or auto-stops", () => Disabled(Path.Combine(root, "disabled")));
        run("T240-SYNTH M3M accepted manual Stop wins timer race and waits for writer", () => ManualRace(Path.Combine(root, "manual")));
        run("T240-SYNTH M3M automatic Stop announces saving and waits for capture boundary", () => AutomaticGate(Path.Combine(root, "automatic-gate")));
        run("T241-WINDOWS M3M real owned process handle exits and rejects wrong identity", ProcessHandle);
    }

    private static void Options()
    {
        var legacy = JsonSerializer.Deserialize<RecordingOptions>("{\"Selection\":null}")!;
        Need(legacy.AutoStop is null && !new RecordingAutoStopOptions().Enabled, "Legacy recordings silently enabled automatic stopping.");
        foreach (int minutes in new[] { 1, 5, 10 }) new RecordingAutoStopOptions(true, minutes).Validate();
        foreach (int minutes in new[] { -1, 0, 2, 6, 11, int.MaxValue })
        {
            try { new RecordingAutoStopOptions(true, minutes).Validate(); }
            catch (ArgumentOutOfRangeException) { continue; }
            throw new InvalidDataException("Invalid threshold accepted.");
        }
        var command = new HostCommand(1, "session", "record", "record", Recording: MakeOptions("unused", new(true, 10)));
        Need(JsonSerializer.Deserialize<HostCommand>(JsonSerializer.Serialize(command))!.Recording!.AutoStop == new RecordingAutoStopOptions(true, 10),
            "IPC dropped the automatic stopping policy.");
        var info = new RecordingAutoStopInfo("no_audio_activity", 5, 300_000_000, true, false);
        var message = new HostEvent(1, "session", "auto-stop", "finalizing", AutoStop: info);
        Need(JsonSerializer.Deserialize<HostEvent>(JsonSerializer.Serialize(message))!.AutoStop == info, "IPC dropped finalization evidence.");
    }
    private static void Threshold(int minutes)
    {
        var timer = new RecordingAutoStopMonitor(new(true, minutes)); timer.Reset(Epoch);
        ulong limit = Epoch + (ulong)minutes * 600_000_000;
        Need(!timer.IsDue(limit - 1, false, false) && timer.IsDue(limit, false, false), "Timer stopped early or missed the exact boundary.");
        var disabled = new RecordingAutoStopMonitor(new(false, minutes)); disabled.Reset(Epoch);
        Need(!disabled.IsDue(limit + 1, false, false), "Disabled policy stopped.");
    }
    private static void SoundReset()
    {
        var timer = new RecordingAutoStopMonitor(new(true, 1)); timer.Reset(Epoch);
        timer.Observe(Epoch + 590_000_000, .0002f);
        timer.Observe(Epoch + 600_000_000, 0); timer.Observe(Epoch + 610_000_000, float.NaN);
        timer.Observe(Epoch + 620_000_000, float.PositiveInfinity);
        Need(!timer.IsDue(Epoch + 1_190_000_000 - 1, false, false) && timer.IsDue(Epoch + 1_190_000_000, false, false),
            "A brief audible packet was lost or silence/nonfinite levels reset activity.");
        Need(!timer.IsDue(Epoch, false, false), "Clock age underflow triggered a stop.");
    }
    private static void PausedMonitor()
    {
        var timer = new RecordingAutoStopMonitor(new(true, 1)); timer.Reset(Epoch);
        Need(!timer.IsDue(Epoch + 36_000_000_000, true, false) && !timer.IsDue(Epoch + 36_000_000_000, false, true),
            "Pause or pending microphone consumed the interval.");
        timer.Reset(Epoch + 36_000_000_000);
        Need(!timer.IsDue(Epoch + 36_590_000_000, false, false), "Resume did not start a fresh interval.");
    }
    private static void Silence(string path)
    {
        using var fixture = new Fixture(path);
        for (int second = 1; second <= 60; second++) fixture.Tick();
        var result = Await(fixture.Session.Completion);
        Need(result.StorageComplete && result.AutoStop is { Reason: "no_audio_activity", RemotePacketMissing: false, MicrophonePacketMissing: false },
            "Silent source did not end complete: " + string.Join(" | ", result.Warnings));
        Need(result.DurationUs == 60_000_000 && Frames(path, "remote") == 480_000, "Auto-stop discarded a valid final packet.");
        Need(fixture.Factory.MicOpenCount == 0 && fixture.Factory.WatchCount == 0, "Endpoint automatic stopping opened a microphone or watched an unrelated process.");
        Persisted(path, result);
    }
    private static void NoPackets(string path)
    {
        using var fixture = new Fixture(path);
        for (int second = 1; second <= 60; second++) fixture.Tick(packets: false);
        var result = Await(fixture.Session.Completion);
        Need(!result.StorageComplete && result.AutoStop is { RemotePacketMissing: true } && result.Warnings.Any(w => w.Contains("不等于静音")),
            "No-data input became a complete silent recording.");
        Persisted(path, result);
    }
    private static void CrossingVoice(string path, bool futureOnly)
    {
        using var fixture = new Fixture(path);
        for (int i = 0; i < 59; i++) fixture.Tick();
        var source = fixture.Factory.Latest("remote"); int before = source.Emitted;
        source.Push(fixture.RawNow, true, 16000, futureOnly);
        fixture.Advance(1); Wait(() => source.Emitted > before, "Crossing packet not read.");
        if (futureOnly)
        {
            var result = Await(fixture.Session.Completion);
            Need(result.StorageComplete && result.DurationUs == 60_000_000 && result.AutoStop is not null && Frames(path, "remote") == 480_000,
                "Future-only sound reset inactivity or post-cutoff PCM was saved.");
        }
        else
        {
            Await(fixture.Session.ControlAsync("crossing-observed", "bookmark"));
            Need(!fixture.Session.Completion.IsCompleted, "An audible valid prefix failed to reset the deadline.");
            fixture.Advance(1);
            var result = Await(fixture.Session.StopAsync());
            Need(result.StorageComplete && result.AutoStop is null && Frames(path, "remote") == 488_000,
                "Pending crossing packet was not retained by normal Stop.");
        }
    }
    private static void Microphone(string path)
    {
        using var fixture = new Fixture(path, mic: true);
        for (int second = 1; second <= 60; second++) fixture.Tick(micSound: second == 59);
        Need(!fixture.Session.Completion.IsCompleted, "Microphone speech failed to reset activity.");
        for (int second = 61; second <= 119; second++) fixture.Tick();
        var result = Await(fixture.Session.Completion);
        Need(result.StorageComplete && result.DurationUs == 119_000_000 && Frames(path, "mic") == 952_000,
            "Combined source activity or microphone final tail was lost.");
    }
    private static void MicOff(string path)
    {
        using var fixture = new Fixture(path, mic: true);
        for (int i = 0; i < 30; i++) fixture.Tick();
        var mic = fixture.Factory.Latest("mic");
        Await(fixture.Session.ControlAsync("mic-off", "mic_off"));
        int reads = mic.Reads;
        for (int i = 0; i < 60; i++) fixture.Tick();
        var result = Await(fixture.Session.Completion);
        Need(result.StorageComplete && result.DurationUs == 90_000_000 && mic.Reads == reads && Frames(path, "mic") == 240_000,
            "Mic-off was counted incorrectly or continued to read/persist PCM.");
    }
    private static void Resume(string path)
    {
        using var fixture = new Fixture(path);
        for (int i = 0; i < 55; i++) fixture.Tick();
        Await(fixture.Session.ControlAsync("pause", "pause"));
        fixture.Advance(3600); Await(fixture.Session.ControlAsync("paused-marker", "bookmark"));
        Need(!fixture.Session.Completion.IsCompleted, "Paused inactivity ended the recording.");
        Await(fixture.Session.ControlAsync("resume", "resume"));
        for (int i = 0; i < 60; i++) fixture.Tick();
        var result = Await(fixture.Session.Completion);
        Need(result.StorageComplete && result.DurationUs == 115_000_000 && result.AutoStop!.TriggeredSessionUs == 115_000_000,
            "Pause/preparation time leaked into the automatic end or the persisted timeline.");
    }
    private static void ProcessExit(string path)
    {
        using var fixture = new Fixture(path, process: true);
        fixture.Tick(remoteSound: true);
        Await(fixture.Session.ControlAsync("pause", "pause"));
        fixture.Factory.Lifetime.Exited = true;
        var result = Await(fixture.Session.Completion);
        Need(result.StorageComplete && result.DurationUs == 1_000_000 && result.AutoStop!.Reason == "source_process_exited" && fixture.Factory.WatchCount == 1,
            "Selected root exit did not stop a paused meeting cleanly.");
        Need(fixture.Factory.Lifetime.Disposed, "Pinned process handle was not released.");
        Persisted(path, result);
    }
    private static void ChildFailure(string path)
    {
        using var fixture = new Fixture(path, process: true);
        fixture.Tick(remoteSound: true); fixture.Factory.ValidationFailure = true;
        fixture.Advance(1);
        Wait(() => fixture.Factory.Latest("remote").Stops == 1, "Identity failure did not stop its source.");
        Need(!fixture.Session.Completion.IsCompleted && !fixture.Factory.Lifetime.HasExited,
            "An audio child/identity failure was mistaken for confirmed root exit.");
        var result = Await(fixture.Session.StopAsync());
        Need(!result.StorageComplete && result.AutoStop is null, "A source fault was promoted to a clean automatic exit.");
    }
    private static void Disabled(string path)
    {
        using var fixture = new Fixture(path, process: true, enabled: false);
        fixture.Factory.Lifetime.Exited = true;
        for (int i = 0; i < 65; i++) fixture.Tick();
        Need(!fixture.Session.Completion.IsCompleted && fixture.Factory.WatchCount == 0, "Unchecked policy changed recording lifecycle.");
        var result = Await(fixture.Session.StopAsync()); Need(result.StorageComplete && result.AutoStop is null, "Manual-only recording changed.");
    }
    private static void ManualRace(string path)
    {
        using var fixture = new Fixture(path);
        for (int i = 0; i < 59; i++) fixture.Tick();
        var source = fixture.Factory.Latest("remote"); source.BlockStop = true;
        try
        {
            var stopping = fixture.Session.StopAsync("manual-first");
            Need(source.StopEntered.Wait(Timeout), "Stop gate not reached."); fixture.Advance(1);
            Need(!stopping.IsCompleted, "Saved was reported before source stop completed.");
            source.AllowStop.Set(); var result = Await(stopping);
            Need(result.StorageComplete && result.AutoStop is null && source.Stops == 1 &&
                ReferenceEquals(result, Await(fixture.Session.StopAsync("duplicate"))), "Timer overrode manual Stop or stopped twice.");
        }
        finally { source.AllowStop.Set(); }
    }
    private static void ProcessHandle()
    {
        string executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(M3mAutoStopTests).Assembly.Location);
        start.ArgumentList.Add("--m3m-lifetime-child");
        using var child = Process.Start(start)!;
        try
        {
            var identity = new ProcessIdentity(child.Id, child.StartTime.ToUniversalTime().Ticks, child.MainModule!.FileName, child.ProcessName);
            using var watch = new RecordingProcessLifetime(identity);
            Need(!watch.HasExited, "A live child was marked exited.");
            bool rejected = false;
            try { using var wrong = new RecordingProcessLifetime(identity with { StartTimeUtcTicks = identity.StartTimeUtcTicks - 1 }); }
            catch (InvalidOperationException) { rejected = true; }
            Need(rejected, "Same PID with a different creation time bypassed identity validation.");
            child.StandardInput.Close(); Need(child.WaitForExit(8000) && child.ExitCode == 0 && watch.HasExited,
                "Pinned process handle did not signal the actual process exit.");
        }
        finally { if (!child.HasExited) { child.StandardInput.Close(); if (!child.WaitForExit(8000)) child.Kill(); } }
    }

    private static void AutomaticGate(string path)
    {
        using var fixture = new Fixture(path);
        for (int i = 0; i < 59; i++) fixture.Tick();
        int notifications = 0;
        fixture.Session.AutoStopping += _ => Interlocked.Increment(ref notifications);
        var source = fixture.Factory.Latest("remote"); source.BlockStop = true;
        try
        {
            source.Push(fixture.RawNow, false); fixture.Advance(1);
            Need(source.StopEntered.Wait(Timeout), "Automatic Stop never reached the source boundary.");
            Need(notifications == 1 && !fixture.Session.Completion.IsCompleted && !File.Exists(Path.Combine(path, "meeting.json")),
                "Automatic end reported Saved before capture and writer finalization.");
            var manual = fixture.Session.StopAsync("manual-after-automatic");
            source.AllowStop.Set(); var result = Await(manual);
            Need(result.StorageComplete && result.AutoStop is not null && source.Stops == 1 && Frames(path, "remote") == 480_000,
                "Manual/automatic convergence lost the final packet or repeated physical Stop.");
            Persisted(path, result);
        }
        finally { source.AllowStop.Set(); }
    }

    private static RecordingOptions MakeOptions(string path, RecordingAutoStopOptions automatic, bool mic = false, bool process = false) =>
        new(new(process ? SourceMode.ProcessTree : SourceMode.EndpointLoopback, null, process ? null : "synthetic-output", !process,
            mic, mic ? "synthetic-mic" : null, path, "M3M SYNTHETIC ONLY"), EnableMultimediaScheduling: false, AutoStop: automatic);
    private static void Persisted(string path, RecordingResult result)
    {
        var disk = JsonSerializer.Deserialize<RecordingResult>(File.ReadAllText(Path.Combine(path, "recording-result.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Need(disk!.AutoStop == result.AutoStop, "Stop evidence was not persisted in recording-result.json.");
        var events = File.ReadLines(Path.Combine(path, "journal", "events.jsonl")).Select(line => JsonDocument.Parse(line).RootElement.GetProperty("data").Clone()).ToArray();
        Need(events.Count(e => e.GetProperty("operation").GetString() == "auto_stop") == 1 && events.Count(e => e.GetProperty("operation").GetString() == "stop") == 1,
            "Automatic decision or final Stop journal item duplicated/missing.");
        using var selection = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "selection.json")));
        Need(selection.RootElement.GetProperty("autoStop").GetProperty("Enabled").GetBoolean(), "Consent policy was not recorded.");
    }
    private static long Frames(string path, string source) => Directory.EnumerateFiles(Path.Combine(path, "sources", source), "*.wav")
        .Sum(file => { using var reader = new WaveFileReader(file); return reader.Length / reader.WaveFormat.BlockAlign; });
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Await(Task task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();
    private static T Await<T>(Task<T> task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();
    private static void Wait(Func<bool> condition, string message)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > Timeout) throw new TimeoutException(message); Thread.Sleep(1); } }

    private sealed class Fixture : IDisposable
    {
        private long qpc = (long)Epoch;
        internal readonly Factory Factory = new();
        internal readonly RecordingSession Session;
        internal ulong RawNow => checked((ulong)Interlocked.Read(ref qpc));
        internal Fixture(string path, bool mic = false, bool process = false, bool enabled = true)
        {
            Session = new(Factory, () => checked((ulong)Interlocked.Read(ref qpc)), _ => 1L << 40);
            Await(Session.StartAsync(MakeOptions(path, new(enabled, 1), mic, process)));
        }
        internal void Advance(int seconds) => Interlocked.Add(ref qpc, (long)seconds * 10_000_000);
        internal void Tick(bool packets = true, bool remoteSound = false, bool micSound = false, Func<int, short>? microphoneSamples = null)
        {
            var sources = Factory.Sources.Where(source => source.Stops == 0).ToArray();
            var emissions = sources.Select(source => source.Emitted).ToArray();
            if (packets) foreach (var source in sources)
            {
                if (source.Id == "mic" && microphoneSamples is not null) source.PushSamples((ulong)qpc, microphoneSamples);
                else source.Push((ulong)qpc, source.Id == "mic" ? micSound : remoteSound);
            }
            Advance(1);
            if (packets) for (int i = 0; i < sources.Length; i++) { int index = i; Wait(() => sources[index].Emitted > emissions[index], "Synthetic packet was not consumed."); }
            else Thread.Sleep(8);
            // A control round-trip confirms the preceding capture iteration without advancing time.
            if (!Session.Completion.IsCompleted)
            {
                try { Await(Session.ControlAsync(Guid.NewGuid().ToString("N"), "bookmark")); }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { Await(Session.Completion); }
            }
        }
        public void Dispose() => Await(Session.DisposeAsync().AsTask());
    }
    private sealed class Lifetime : IRecordingProcessLifetime
    {
        internal volatile bool Exited, Disposed;
        public bool HasExited => Exited;
        public void Dispose() => Disposed = true;
    }
    private sealed class Factory : IRecordingSourceFactory
    {
        internal readonly ConcurrentQueue<Source> Sources = new();
        internal readonly Lifetime Lifetime = new();
        internal volatile bool ValidationFailure;
        internal int MicOpenCount, WatchCount;
        internal Source Latest(string id) => Sources.Last(source => source.Id == id);
        public void Preflight(RecordingOptions options) { }
        public void ValidateRemote(CaptureOptions options) { if (ValidationFailure) throw new InvalidOperationException("Synthetic audio child changed; root still alive."); }
        public IRecordingProcessLifetime WatchProcess(CaptureOptions options) { WatchCount++; return Lifetime; }
        public IRecordingAudioSource OpenRemote(CaptureOptions options) { var source = new Source("remote"); Sources.Enqueue(source); return source; }
        public IRecordingAudioSource OpenMicrophone(string deviceId) { MicOpenCount++; var source = new Source("mic"); Sources.Enqueue(source); return source; }
    }
    private sealed class Source(string id) : IRecordingAudioSource
    {
        private readonly ConcurrentQueue<RecordingBlock> queue = new();
        private ulong frames;
        internal int Emitted, Reads, Stops;
        internal bool BlockStop;
        internal readonly ManualResetEventSlim StopEntered = new(), AllowStop = new();
        public string Id => id;
        public WaveFormat WaveFormat { get; } = new(8000, 16, 1);
        public PcmFormat Format { get; } = new(8000, 1, 16, false, 2);
        internal void Push(ulong at, bool sound, int frameCount = 8000, bool soundAtEnd = false)
        {
            var pcm = new byte[frameCount * 2];
            if (sound) { int offset = soundAtEnd ? pcm.Length - 2 : 0; pcm[offset] = 0x10; pcm[offset + 1] = 0x27; }
            queue.Enqueue(new(pcm, frameCount, frameCount, frames, at, sound ? 0 : 2, true, true, 0)); frames += (uint)frameCount;
        }
        internal void PushSamples(ulong at, Func<int, short> sample)
        {
            var pcm = new byte[16000];
            for (int frame = 0; frame < 8000; frame++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * 2), sample(frame));
            queue.Enqueue(new(pcm, 8000, 8000, frames, at, 0, true, true, 0)); frames += 8000;
        }
        public void Start() { }
        public void Stop() { Interlocked.Increment(ref Stops); StopEntered.Set(); if (BlockStop) Need(AllowStop.Wait(Timeout), "Stop gate timed out."); }
        public RecordingBlock? Read(ulong epochQpc100ns, ulong? cutoffQpc100ns)
        { Interlocked.Increment(ref Reads); if (!queue.TryDequeue(out var block)) return null; Interlocked.Increment(ref Emitted); return block; }
        public void Dispose() { }
    }
}
