using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectEcho.Core.Transcription;

internal static class M4QwenWindowTests
{
    private const int Rate = QwenWindowPlan.SampleRate;
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m4-qwen-windows"); Directory.CreateDirectory(root);
        run("P4-02-SYNTH Qwen integer-frame ownership covers complete input and preserves timeline policy", Plan);
        run("P4-02-SYNTH Qwen streaming WAV windows retain every source frame and final sample", () => Extract(root));
        run("P4-02-SYNTH Qwen RF64 format hash and malformed-container boundaries", () => InvalidAudio(root));
        run("P4-02-SYNTH Qwen real-evidence mapping adds local offset once and retains exact sentence text", Alignment);
        run("P4-02-SYNTH Qwen rejects guessed times wrong text omissions and repeated window-start timing", RejectAlignment);
        run("P4-02-SYNTH Qwen context sentences are retained and labeled for explicit boundary reconciliation", Context);
        run("P4-03/04-SYNTH Qwen incomplete failed cancelled and unresolved windows cannot complete a meeting", Coverage);
        run("P4-04-SYNTH Qwen window cancellation and retries do not overwrite prior outputs or input", () => Cancellation(root));
    }

    /// <summary>Separate synthetic disk/stream probe; never a three-hour real-model quality/performance claim.</summary>
    internal static int RunLongProbe(string output)
    {
        string root = SyntheticAudioProbe.SafeProjectPath(SyntheticAudioProbe.FindProjectRoot(), output);
        Directory.CreateDirectory(root);
        long frames = 3L * 60 * 60 * Rate + 17;
        string wave = Path.Combine(root, "synthetic-three-hours.wav");
        using (var file = new FileStream(wave, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            file.Write(WaveHeader(frames)); file.SetLength(44 + frames * 2);
            foreach (long frame in new[] { 0L, 30L * Rate - 1, 30L * Rate, 3600L * Rate, 7200L * Rate, frames - 1 })
            {
                file.Position = 44 + frame * 2;
                file.WriteByte((byte)(frame % 251 + 1)); file.WriteByte(0x12);
            }
            file.Flush(true);
        }
        string originalHash = Hash(wave);
        var plan = QwenWindowPlan.Create(frames, 30 * Rate, timelinePolicy: "pause-excluded-v1");
        var timer = Stopwatch.StartNew();
        long counted = 0, allocatedBefore = GC.GetTotalAllocatedBytes(true), peakWorkingSet = 0;
        int nonzeroWindows = 0;
        using var reference = File.OpenRead(wave);
        using var audio = new QwenWindowAudio(wave, originalHash);
        var buffer = new byte[65536];
        var compare = new byte[65536];
        var manifest = new List<object>();
        foreach (var window in plan.Windows)
        {
            var part = audio.WriteWindow(plan, window, root);
            using var actual = File.OpenRead(part.Path);
            actual.Position = 44;
            reference.Position = 44 + window.InputStartFrame * 2;
            long bytesLeft = part.Frames * 2;
            while (bytesLeft > 0)
            {
                int count = (int)Math.Min(buffer.Length, bytesLeft);
                actual.ReadExactly(buffer.AsSpan(0, count)); reference.ReadExactly(compare.AsSpan(0, count));
                Need(buffer.AsSpan(0, count).SequenceEqual(compare.AsSpan(0, count)), "三小时流式窗口字节与源文件不一致。");
                bytesLeft -= count;
            }
            Need(actual.Position == actual.Length, "窗口尾端存在未核验数据。");
            counted += part.Frames;
            if (!part.IsDigitalSilence) nonzeroWindows++;
            using var process = Process.GetCurrentProcess(); process.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
            manifest.Add(new { window, part.Sha256, part.Frames, part.IsDigitalSilence });
        }
        Need(counted == frames && Hash(wave) == originalHash && nonzeroWindows == 5,
            "三小时原始帧、尾部脉冲或输入哈希丢失。");
        File.WriteAllText(Path.Combine(root, "synthetic-three-hours-summary.json"), JsonSerializer.Serialize(new
        {
            isSynthetic = true, actualModelInference = false, os = Environment.OSVersion.VersionString,
            runtime = Environment.Version.ToString(), frameCount = frames, durationSeconds = (decimal)frames / Rate,
            sampleRate = Rate, inputWaveSha256 = originalHash, timelinePolicy = plan.TimelinePolicy,
            windows = plan.Windows.Count, copiedFrames = counted, tailFrames = plan.Windows[^1].OwnedFrameCount,
            nonzeroWindows, elapsedMs = timer.ElapsedMilliseconds, observedProcessWorkingSetBytes = peakWorkingSet,
            totalManagedAllocationsDuringProbeBytes = GC.GetTotalAllocatedBytes(true) - allocatedBefore,
            sourceReadBufferBytes = 65536, result = "PASS", manifest
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static void Plan()
    {
        foreach (string policy in new[] { "elapsed-v1", "pause-excluded-v1", "file-v1" })
        {
            var plan = QwenWindowPlan.Create(3L * 60 * 60 * Rate + 17, 30 * Rate, 2 * Rate, policy);
            long end = 0;
            foreach (var window in plan.Windows)
            {
                Need(window.OwnedStartFrame == end && window.InputStartFrame <= end && window.InputEndFrame >= window.OwnedEndFrame,
                    "窗口ownership存在缺口、重复或缺少上下文。");
                end = window.OwnedEndFrame;
            }
            Need(end == plan.TotalFrames && plan.Windows[^1].OwnedFrameCount == 17 && plan.TimelinePolicy == policy,
                "尾窗残帧或既有时间轴策略被改写。");
            Reject<InvalidDataException>(() => plan.ValidateWindow(plan.Windows[0] with { InputStartFrame = 1 }));
        }
        Reject<InvalidDataException>(() => QwenWindowPlan.Create(0, Rate));
        Reject<InvalidDataException>(() => QwenWindowPlan.Create(50001, 1));
        Reject<InvalidDataException>(() => QwenWindowPlan.Create(Rate, Rate, timelinePolicy: "guessed-pause-removal"));
        Reject<InvalidDataException>(() => QwenWindowPlan.Create(Rate, 600 * Rate, 1));
    }

    private static void Extract(string root)
    {
        string folder = Subfolder(root, "extract");
        var samples = Enumerable.Range(0, 3 * Rate + 17).Select(i => (short)(i * 73 % 60001 - 30000)).ToArray();
        string input = Wave(folder, "input.wav", samples);
        string hash = Hash(input);
        var plan = QwenWindowPlan.Create(samples.Length, Rate, timelinePolicy: "pause-excluded-v1");
        using var audio = new QwenWindowAudio(input, hash);
        var recovered = new List<short>();
        foreach (var window in plan.Windows)
        {
            var part = audio.WriteWindow(plan, window, folder);
            byte[] bytes = File.ReadAllBytes(part.Path);
            Need(part.Sha256 == Hash(part.Path) && part.Frames == window.OwnedFrameCount && !part.IsDigitalSilence,
                "窗口哈希、帧数或数字零判断错误。");
            for (int i = 44; i < bytes.Length; i += 2) recovered.Add(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i, 2)));
        }
        Need(samples.SequenceEqual(recovered) && Hash(input) == hash, "全覆盖裁窗改写、丢失或重复源PCM。");
        string silentFolder = Subfolder(root, "digital-zero");
        short[] zero = new short[Rate + 1]; zero[^1] = 1;
        string silent = Wave(silentFolder, "input.wav", zero);
        var silentPlan = QwenWindowPlan.Create(zero.Length, Rate);
        using var silentAudio = new QwenWindowAudio(silent, Hash(silent));
        Need(silentAudio.WriteWindow(silentPlan, silentPlan.Windows[0], silentFolder).IsDigitalSilence &&
            !silentAudio.WriteWindow(silentPlan, silentPlan.Windows[1], silentFolder).IsDigitalSilence,
            "数字零保护遗漏尾部一个非零帧。");
    }

    private static void InvalidAudio(string root)
    {
        string folder = Subfolder(root, "bad-audio");
        string good = Wave(folder, "good.wav", [1, 2, 3, 4]);
        Reject<InvalidDataException>(() => { using var rejected = new QwenWindowAudio(good, HashA); });
        byte[] bytes = File.ReadAllBytes(good);
        var badRate = (byte[])bytes.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(badRate.AsSpan(24), 48000);
        string rate = Path.Combine(folder, "rate.wav"); File.WriteAllBytes(rate, badRate);
        Reject<InvalidDataException>(() => { using var rejected = new QwenWindowAudio(rate, Hash(rate)); });
        string tail = Path.Combine(folder, "truncated.wav"); File.WriteAllBytes(tail, bytes[..^1]);
        Reject<InvalidDataException>(() => { using var rejected = new QwenWindowAudio(tail, Hash(tail)); });
        string rf64 = Path.Combine(folder, "rf64.wav");
        using (var stream = File.Create(rf64))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write("RF64"u8); writer.Write(uint.MaxValue); writer.Write("WAVEds64"u8);
            writer.Write(28u); writer.Write(80ul); writer.Write(8ul); writer.Write(4ul); writer.Write(0u);
            writer.Write(bytes.AsSpan(12, 24)); writer.Write("data"u8); writer.Write(uint.MaxValue); writer.Write(bytes.AsSpan(44));
        }
        using var valid = new QwenWindowAudio(rf64, Hash(rf64));
        Need(valid.FrameCount == 4, "合法RF64小样帧数错误。");
    }

    private static void Alignment()
    {
        const string text = "第一句。\nSecond sentence.";
        var plan = QwenWindowPlan.Create(100 * Rate, 30 * Rate, timelinePolicy: "pause-excluded-v1");
        var mapped = QwenWindowAlignment.Map(plan, plan.Windows[1], text, HashA, Evidence(text),
            [new(0, 4, Rate, 3 * Rate), new(5, text.Length - 5, 5 * Rate, 8 * Rate)]);
        Need(mapped[0].StartMs == 31000 && mapped[0].EndMs == 33000 && mapped[1].StartMs == 35000 &&
            mapped[1].EndMs == 38000 && mapped[0].Text == "第一句。" && mapped[1].Text == "Second sentence." &&
            mapped.All(x => x.TimestampQuality == "approximate" && x.TimingSource == "forced-alignment"),
            "全局偏移重复应用、原文改变或对齐冒充精确时间。");
    }

    private static void RejectAlignment()
    {
        const string text = "一。二。";
        var plan = QwenWindowPlan.Create(Rate, Rate);
        QwenWindowSentence[] sentences = [new(0, 2, 0, 1000), new(2, 2, 2000, 3000)];
        void Map(QwenWindowAlignmentEvidence evidence, IReadOnlyList<QwenWindowSentence> spans) =>
            QwenWindowAlignment.Map(plan, plan.Windows[0], text, HashA, evidence, spans);
        Reject<InvalidDataException>(() => Map(Evidence(text) with { Source = "window" }, sentences));
        Reject<InvalidDataException>(() => Map(Evidence(text) with { Source = "vad" }, sentences));
        Reject<InvalidDataException>(() => Map(Evidence(text) with { OriginalTextSha256 = HashB }, sentences));
        Reject<InvalidDataException>(() => Map(Evidence(text) with { InputWaveSha256 = HashB }, sentences));
        Reject<InvalidDataException>(() => Map(Evidence(text), [sentences[0]]));
        Reject<InvalidDataException>(() => Map(Evidence(text), [sentences[0], new(2, 2, 0, 3000)]));
        Reject<InvalidDataException>(() => Map(Evidence(text), [sentences[0], new(2, 2, 3000, Rate + 1)]));
        const string emoji = "😀。";
        Reject<InvalidDataException>(() => QwenWindowAlignment.Map(plan, plan.Windows[0], emoji, HashA, Evidence(emoji),
            [new(0, 1, 0, 100), new(1, 2, 200, 300)]));
    }

    private static void Context()
    {
        const string text = "前。跨。内。后。";
        var plan = QwenWindowPlan.Create(3 * Rate, Rate, Rate / 4);
        var mapped = QwenWindowAlignment.Map(plan, plan.Windows[1], text, HashA, Evidence(text),
            [new(0, 2, 0, Rate / 8), new(2, 2, Rate / 8, Rate / 2),
                new(4, 2, Rate / 2, Rate), new(6, 2, 5 * Rate / 4, 3 * Rate / 2)]);
        Need(string.Concat(mapped.Select(x => x.Text)) == text &&
            mapped.Select(x => x.Ownership).SequenceEqual(new[] { QwenWindowSentenceOwnership.ContextOnly,
                QwenWindowSentenceOwnership.CrossesBoundary, QwenWindowSentenceOwnership.Inside, QwenWindowSentenceOwnership.ContextOnly }),
            "上下文句被剪字、删掉或默认为可完成。");
    }

    private static void Coverage()
    {
        var plan = QwenWindowPlan.Create(2 * Rate + 17, Rate);
        QwenWindowOutcome[] results = [new(0, HashA, QwenWindowStatus.Aligned, HashB, 2),
            new(1, HashA, QwenWindowStatus.DigitalSilence), new(2, HashA, QwenWindowStatus.Aligned, HashB, 1)];
        Need(QwenWindowCoverage.Evaluate(plan, results).Complete, "完整已定位/数字零覆盖未完成。");
        foreach (var state in new[] { QwenWindowStatus.Cancelled, QwenWindowStatus.Failed, QwenWindowStatus.AlignmentPending, QwenWindowStatus.Pending })
        {
            var changed = results.ToArray(); changed[2] = changed[2] with { Status = state };
            var coverage = QwenWindowCoverage.Evaluate(plan, changed);
            Need(!coverage.Complete && coverage.CoveredFrames == 2 * Rate && coverage.IncompleteWindowIndexes.SequenceEqual([2]),
                "取消/失败/待定位窗口被当成全场成功。");
        }
        Need(!QwenWindowCoverage.Evaluate(plan, results[..^1]).Complete, "丢失尾窗仍标完成。");
        var noText = results.ToArray();
        noText[1] = new(1, HashA, QwenWindowStatus.ModelNoText, QwenWindowAlignment.TextSha256(""));
        Need(QwenWindowCoverage.Evaluate(plan, noText).Complete, "实际已处理的空模型原文窗口不能计入处理覆盖。");
        Reject<InvalidDataException>(() => QwenWindowCoverage.Evaluate(plan,
            [noText[1] with { OriginalTextSha256 = HashB }]));
        var context = results.ToArray(); context[0] = context[0] with { HasUnresolvedContext = true };
        Need(!QwenWindowCoverage.Evaluate(plan, context).Complete, "未解决上下文被静默当完成。");
        Reject<InvalidDataException>(() => QwenWindowCoverage.Evaluate(plan, [results[0], results[0]]));
        Reject<InvalidDataException>(() => QwenWindowCoverage.Evaluate(plan, [results[0] with { OriginalTextSha256 = null }]));
    }

    private static void Cancellation(string root)
    {
        string folder = Subfolder(root, "cancel"); string wave = Wave(folder, "input.wav", [1, 2, 3]); string hash = Hash(wave);
        var plan = QwenWindowPlan.Create(3, 3);
        using var audio = new QwenWindowAudio(wave, hash);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Reject<OperationCanceledException>(() => audio.WriteWindow(plan, plan.Windows[0], folder, cancellation.Token));
        Need(!Directory.EnumerateFiles(folder, "window-*.wav").Any(), "预取消发布成功窗口。");
        var first = audio.WriteWindow(plan, plan.Windows[0], folder);
        Reject<IOException>(() => audio.WriteWindow(plan, plan.Windows[0], folder));
        string retry = Subfolder(root, "cancel-retry");
        var second = audio.WriteWindow(plan, plan.Windows[0], retry);
        Need(first.Sha256 == second.Sha256 && Hash(first.Path) == first.Sha256 && Hash(wave) == hash,
            "重试覆盖成功窗口或源文件。");
    }

    private static QwenWindowAlignmentEvidence Evidence(string text) => new("forced-alignment", "synthetic-contract-fixture",
        HashA, HashB, HashA, QwenWindowAlignment.TextSha256(text));
    private static string Subfolder(string root, string name) { string folder = Path.Combine(root, name); Directory.CreateDirectory(folder); return folder; }
    private static string Wave(string root, string name, short[] samples)
    {
        string path = Path.Combine(root, name);
        using var file = new FileStream(path, FileMode.CreateNew); file.Write(WaveHeader(samples.Length));
        var pair = new byte[2]; foreach (short sample in samples) { BinaryPrimitives.WriteInt16LittleEndian(pair, sample); file.Write(pair); }
        return path;
    }
    private static byte[] WaveHeader(long frames)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory, Encoding.UTF8, true);
        writer.Write("RIFF"u8); writer.Write(checked((uint)(36 + frames * 2))); writer.Write("WAVEfmt "u8); writer.Write(16u);
        writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(Rate); writer.Write(Rate * 2); writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write("data"u8); writer.Write(checked((uint)(frames * 2))); writer.Flush(); return memory.ToArray();
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    private static void Need(bool condition, string message) => M3TranscriptTests.Need(condition, message);
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"预期拒绝 {typeof(T).Name}，实际通过。"); }
}
