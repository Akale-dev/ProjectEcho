using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

/// <summary>Gated in-memory validators and sources; no WASAPI, playback or microphone activation.</summary>
internal static class M1ValidationIsolationTests
{
    private const ulong Epoch = 70_000_000;
    private const string Mic = "fixed-synthetic-mic";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    internal static void Register(Action<string, Action> run, string output)
    {
        run("T51/T54-SYNTH slow identity validator cannot starve capture or block Stop/dispose", () => SlowIdentity(Path.Combine(output, "m1-slow-identity")));
        run("T51/T57-SYNTH slow disk validation stays bounded while PCM and Stop progress", () => SlowDisk(Path.Combine(output, "m1-slow-disk")));
        run("T07/T54-SYNTH timed-out validation fails closed without a replacement worker/source", () => TimeoutFailsClosed(Path.Combine(output, "m1-validation-timeout")));
        run("T07/T54-SYNTH identity failure closes exact source and a stale result cannot close resumed segment", () => IdentityFailure(Path.Combine(output, "m1-validation-identity-failure")));
        run("T54-SYNTH validation completion timing excludes delayed consumption and retains late-result deadline", CompletionTiming);
    }

    private static void SlowIdentity(string directory)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var returned = new ManualResetEventSlim();
        var clock = new Clock(); var factory = new Factory(() => { entered.Set(); Need(release.Wait(Timeout), "validator test gate timed out"); returned.Set(); });
        var session = new RecordingSession(factory, clock.Read, _ => 1L << 40); bool disposed = false;
        try
        {
            Await(session.StartAsync(Options(directory, mic: true))); Need(entered.Wait(Timeout), "后台身份检查没有开始。");
            factory.Remote.Push(Block(0)); factory.Microphone!.Push(Block(0)); clock.Set(Epoch + 100_000);
            // Regression assertion: the old inline ValidateRemote blocks before
            // ServiceStartedSources, so neither queued packet can be consumed.
            Wait(() => factory.Remote.Emitted == 1 && factory.Microphone.Emitted == 1, "身份检查阻塞时采集线程未继续读两路 PCM。", 750);
            clock.Set(Epoch + 10_000_000); Thread.Sleep(40);
            Need(factory.ValidationCalls == 1, "阻塞校验生成了额外后台任务。");
            var timer = Stopwatch.StartNew(); var result = Await(session.StopAsync());
            Need(timer.Elapsed < TimeSpan.FromSeconds(1) && !returned.IsSet, "Stop等待了被卡住的身份校验。");
            Need(result.StorageComplete && factory.Remote.Stops == 1 && factory.Microphone.Stops == 1, "停止收尾或双路边界改变。");
            var disk = M1Recovery.Read(directory);
            Need(disk.Capture.Tracks.Sum(track => track.FrameCount) == 960, "卡住检查时已获准 PCM 丢失。");
            Await(session.DisposeAsync().AsTask()); disposed = true;
            int reads = factory.Remote.Reads + factory.Microphone.Reads;
            release.Set(); Need(returned.Wait(Timeout), "迟到检查没有返回。"); Thread.Sleep(30);
            Need(factory.Remote.Reads + factory.Microphone.Reads == reads, "Stop后迟到检查重新读来源。");
        }
        finally { release.Set(); if (!disposed) Await(session.DisposeAsync().AsTask()); returned.Wait(Timeout); }
    }

    private static void SlowDisk(string directory)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var returned = new ManualResetEventSlim();
        var clock = new Clock(); var factory = new Factory(() => { }); int checks = 0;
        long Disk(string _)
        {
            if (Interlocked.Increment(ref checks) == 1) return 1L << 40; // negotiated-format startup budget
            entered.Set(); Need(release.Wait(Timeout), "disk test gate timed out"); returned.Set(); return 1L << 40;
        }
        var session = new RecordingSession(factory, clock.Read, Disk); bool disposed = false;
        try
        {
            Await(session.StartAsync(Options(directory, mic: false))); Need(entered.Wait(Timeout), "后台磁盘检查没有开始。");
            factory.Remote.Push(Block(0)); clock.Set(Epoch + 100_000);
            Wait(() => factory.Remote.Emitted == 1, "慢磁盘查询饿死来源服务。", 750);
            clock.Set(Epoch + 10_000_000); Thread.Sleep(40);
            Need(Volatile.Read(ref checks) == 2 && factory.ValidationCalls == 0, "磁盘查询重叠或磁盘查询未结束便误称身份验证。");
            var timer = Stopwatch.StartNew(); var result = Await(session.StopAsync());
            Need(timer.Elapsed < TimeSpan.FromSeconds(1) && result.StorageComplete && !returned.IsSet, "停止依赖慢磁盘查询完成。");
            Await(session.DisposeAsync().AsTask()); disposed = true; release.Set(); Need(returned.Wait(Timeout), "迟到磁盘查询没有返回。");
        }
        finally { release.Set(); if (!disposed) Await(session.DisposeAsync().AsTask()); returned.Wait(Timeout); }
    }

    private static void TimeoutFailsClosed(string directory)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var returned = new ManualResetEventSlim();
        var factory = new Factory(() => { entered.Set(); Need(release.Wait(TimeSpan.FromSeconds(10)), "timeout test gate timed out"); returned.Set(); });
        var clock = new Clock(); var session = new RecordingSession(factory, clock.Read, _ => 1L << 40); bool disposed = false;
        try
        {
            Await(session.StartAsync(Options(directory, mic: true))); Need(entered.Wait(Timeout), "后台验证未运行。");
            factory.Remote.Push(Block(0)); factory.Microphone!.Push(Block(0)); clock.Set(Epoch + 100_000);
            Wait(() => factory.Remote.Emitted == 1 && factory.Microphone.Emitted == 1, "超时前PCM没有被服务。");
            var result = Await(session.Completion);
            Need(!result.StorageComplete && result.Warnings.Any(w => w.Contains("检查超过2秒")), "验证超时没有明确fail closed。");
            Need(factory.Remote.Stops == 1 && factory.Microphone.Stops == 1 && factory.RemoteOpens == 1 && factory.MicOpens == 1 &&
                factory.ValidationCalls == 1 && !returned.IsSet, "超时扩大来源、重启worker或没有停止已同意的采集。");
            Need(M1Recovery.Read(directory).Capture.Tracks.Sum(track => track.FrameCount) == 960, "超时丢弃了已接收的有效前缀。");
            Await(session.DisposeAsync().AsTask()); disposed = true; release.Set(); Need(returned.Wait(Timeout), "超时后的迟到结果没有退出。");
        }
        finally { release.Set(); if (!disposed) Await(session.DisposeAsync().AsTask()); returned.Wait(Timeout); }
    }

    private static void IdentityFailure(string directory)
    {
        foreach (bool resume in new[] { false, true })
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var returned = new ManualResetEventSlim();
            var clock = new Clock(); var factory = new Factory(() =>
            {
                entered.Set(); Need(release.Wait(Timeout), "identity failure gate timed out"); returned.Set();
                throw new IOException("synthetic identity no longer matches selected process");
            });
            var session = new RecordingSession(factory, clock.Read, _ => 1L << 40);
            try
            {
                Await(session.StartAsync(Options(directory + (resume ? "-stale" : "-active"), mic: true)));
                Need(entered.Wait(Timeout), "身份检查未开始。");
                var original = factory.Remote;
                original.Push(Block(0)); factory.Microphone!.Push(Block(0)); clock.Set(Epoch + 100_000);
                Wait(() => original.Emitted == 1 && factory.Microphone.Emitted == 1, "失败前PCM未接收。");
                if (resume)
                {
                    Await(session.ControlAsync("pause", "pause")); clock.Set(Epoch + 200_000);
                    Await(session.ControlAsync("resume", "resume"));
                }
                release.Set(); Need(returned.Wait(Timeout), "身份失败没有返回。");
                if (resume)
                {
                    Thread.Sleep(50);
                    Need(factory.Remote.Stops == 0 && factory.RemoteOpens == 2, "旧segment验证结果关闭了用户明确恢复的新segment。");
                    factory.Remote.Push(Block(0) with { QpcPosition100ns = Epoch + 200_000 }); clock.Set(Epoch + 300_000);
                    Wait(() => factory.Remote.Emitted == 1, "恢复后来源没有继续服务。");
                }
                else
                {
                    Wait(() => factory.Remote.Stops == 1, "身份失败未关闭原定来源。");
                    Need(factory.RemoteOpens == 1 && factory.Microphone!.Stops == 0, "身份失败自动替换来源或改动独立mic同意。");
                }
                var result = Await(session.StopAsync());
                if (!resume) Need(!result.StorageComplete && result.Warnings.Any(w => w.Contains("不替换或扩大范围")), "身份失败未降级记录。");
            }
            finally { release.Set(); Await(session.DisposeAsync().AsTask()); returned.Wait(Timeout); }
        }
    }

    private static RecordingOptions Options(string directory, bool mic) => new(
        new(SourceMode.EndpointLoopback, null, "synthetic-render", true,
            mic, Mic, directory, "synthetic validation isolation", 10), MaximumSeconds: 60, ExpectedMinutes: 1);

    private static void CompletionTiming()
    {
        foreach (int delay in new[] { 10, 2100 })
        {
            using var worker = new CaptureValidationWorker(segment =>
            { Thread.Sleep(delay); return new(segment, 1L << 40, null, null); });
            var timer = Stopwatch.StartNew(); Need(worker.TryRequest("remote-1"), "验证请求被拒绝。");
            Wait(() => !worker.HasTimedOut(TimeSpan.Zero), "校验没有完成。");
            Thread.Sleep(80); // delayed capture-thread consumption must not inflate completion duration
            Need(worker.TryTake(out var result) && result is not null, "没有取得已完成结果。");
            Need(result!.Elapsed <= timer.Elapsed - TimeSpan.FromMilliseconds(60), "完成时间错误地包含了消费等待。");
            Need((result.Elapsed >= TimeSpan.FromSeconds(2)) == (delay > 2000), "完成结果丢失实际超时状态。");
        }
    }

    private static RecordingBlock Block(long offset) => new(new byte[960], 480, 480, (ulong)offset,
        Epoch + (ulong)offset * 10_000_000 / 48000, 2, true, true, 0);

    private sealed class Clock
    {
        private long value = (long)Epoch;
        internal ulong Read() => (ulong)Interlocked.Read(ref value);
        internal void Set(ulong qpc) => Interlocked.Exchange(ref value, (long)qpc);
    }

    private sealed class Factory(Action validate) : IRecordingSourceFactory
    {
        internal Source Remote = null!;
        internal Source? Microphone;
        internal int RemoteOpens, MicOpens, ValidationCalls;
        public void Preflight(RecordingOptions options) { }
        public void ValidateRemote(CaptureOptions options) { Interlocked.Increment(ref ValidationCalls); validate(); }
        public IRecordingAudioSource OpenRemote(CaptureOptions options)
        { Interlocked.Increment(ref RemoteOpens); return Remote = new("remote"); }
        public IRecordingAudioSource OpenMicrophone(string deviceId)
        { Need(deviceId == Mic, "替换了固定mic"); Interlocked.Increment(ref MicOpens); return Microphone = new("mic"); }
    }

    private sealed class Source(string id) : IRecordingAudioSource
    {
        private readonly ConcurrentQueue<RecordingBlock> pending = new();
        private int emitted, reads, stops, disposed;
        internal int Emitted => Volatile.Read(ref emitted);
        internal int Reads => Volatile.Read(ref reads);
        internal int Stops => Volatile.Read(ref stops);
        public string Id => id;
        public WaveFormat WaveFormat { get; } = new(48000, 16, 1);
        public PcmFormat Format { get; } = new(48000, 1, 16, false, 2);
        internal void Push(RecordingBlock packet) => pending.Enqueue(packet);
        public void Start() { }
        public void Stop() => Interlocked.Increment(ref stops);
        public RecordingBlock? Read(ulong epochQpc100ns, ulong? cutoffQpc100ns)
        {
            Need(Volatile.Read(ref disposed) == 0, "读取了已释放来源。"); Interlocked.Increment(ref reads);
            if (!pending.TryDequeue(out var packet)) return null;
            Interlocked.Increment(ref emitted); return packet;
        }
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
    }

    private static void Await(Task task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();
    private static T Await<T>(Task<T> task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();
    private static void Wait(Func<bool> condition, string message, int milliseconds = 3000)
    { var timer = Stopwatch.StartNew(); while (!condition()) { if (timer.ElapsedMilliseconds > milliseconds) throw new InvalidDataException(message); Thread.Sleep(1); } }
    private static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
