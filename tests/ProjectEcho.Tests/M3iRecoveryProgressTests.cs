using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

internal static class M3iRecoveryProgressTests
{
    private const int Rate = 8000, FramesPerPacket = 80;
    private const ulong Epoch = 100000000;
    private sealed class Reporter(Action<M1RecoveryProgress> receive) : IProgress<M1RecoveryProgress>
    { public void Report(M1RecoveryProgress value) => receive(value); }

    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3i-recovery-progress"); Directory.CreateDirectory(root);
        run("T50/T53-SYNTH M3I cached chunk path still rejects second packet path traversal duplicate path and segment changes", () => LaterPacketTampering(root));
        run("T50/T53-WINDOWS SYNTH M3I first chunk resolution rejects preexisting real junction without changing original PCM", () => Junction(root));
        run("T20/T53-SYNTH M3I all PCM and complete file hashes remain mandatory with progress", () => Hashes(root));
        run("T20/T53-SYNTH M3I recovery reports actual UTF8 index PCM and complete file byte counts with unchanged legacy result", () => ByteProgress(root));
        run("T34/T53-SYNTH M3I observer failures are isolated and cancellation in each byte stage remains effective", () => ObserverAndCancellation(root));
        run("T14/T53-SYNTH M3I no PCM reports zero byte work without inventing a completed audio capture", () => NoPcm(root));
    }

    private static void LaterPacketTampering(string root)
    {
        foreach (string variant in new[] { "path-change", "traversal", "duplicate-path", "changed-segment", "unsafe-new-chunk" })
        {
            string path = Fixture(root, variant);
            string[] lines = File.ReadAllLines(Index(path));
            var packets = Enumerable.Range(0, lines.Length).Where(index => Type(lines[index]) == "packet" &&
                JsonNode.Parse(lines[index])!["data"]!["segmentId"]!.GetValue<string>() == "remote-1").ToArray();
            lines[packets[1]] = Mutate(lines[packets[1]], data =>
            {
                if (variant == "path-change") data["path"] = "sources/remote/replaced.wav";
                else if (variant == "traversal") data["path"] = "sources/../escape.wav";
                else if (variant == "duplicate-path") data["chunkId"] = data["chunkId"]!.GetValue<string>() + "-duplicate";
                else if (variant == "changed-segment") data["segmentId"] = "mic-1";
                else { data["chunkId"] = "new-unsafe-chunk"; data["path"] = "sources/../escape.wav"; }
            });
            File.WriteAllLines(Index(path), lines, new UTF8Encoding(false));
            var before = Fingerprints(path);
            var result = M1Recovery.Read(path, default, new Reporter(_ => { }));
            Need(!result.IsCleanEnd && result.Issues.Count > 0 && !result.Capture.Complete,
                "路径缓存接受非法后续包：" + variant);
            Need(SameFiles(path, before), "拒绝非法路径时修改了原件。");
        }
    }

    private static void Junction(string root)
    {
        string path = Fixture(root, "junction"), target = Path.Combine(path, "sources", "remote");
        string junction = Path.Combine(path, "sources", "linked");
        var audioHashes = Directory.EnumerateFiles(target).ToDictionary(file => file, Hash);
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{junction.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("合成junction进程未启动。");
        if (!process.WaitForExit(10000)) { process.Kill(); throw new TimeoutException("合成junction创建超时。"); }
        Need(process.ExitCode == 0 && (File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0,
            "真实junction夹具未建立，此项未通过：" + process.StandardError.ReadToEnd());
        string[] lines = File.ReadAllLines(Index(path));
        int packet = Array.FindIndex(lines, line => Type(line) == "packet" &&
            JsonNode.Parse(line)!["data"]!["segmentId"]!.GetValue<string>() == "remote-1");
        lines[packet] = Mutate(lines[packet], data => data["path"] =
            "sources/linked/" + Path.GetFileName(data["path"]!.GetValue<string>()));
        File.WriteAllLines(Index(path), lines, new UTF8Encoding(false));
        var result = M1Recovery.Read(path);
        Need(!result.IsCleanEnd && result.Issues.Any(issue => issue.Contains("重解析点")), "首次chunk没有拒绝真实重解析点。");
        Need(audioHashes.All(pair => Hash(pair.Key) == pair.Value), "重解析点检查修改了音频。");
    }

    private static void Hashes(string root)
    {
        foreach (string variant in new[] { "pcm", "file-hash" })
        {
            string path = Fixture(root, "hash-" + variant);
            if (variant == "pcm")
            {
                string wave = Directory.EnumerateFiles(Path.Combine(path, "sources", "remote"), "*.wav").Single();
                using var stream = new FileStream(wave, FileMode.Open, FileAccess.ReadWrite);
                stream.Position = stream.Length - 1; stream.WriteByte(123);
            }
            else
            {
                string manifest = Path.Combine(path, "meeting.json");
                var json = JsonNode.Parse(File.ReadAllText(manifest))!;
                json["chunks"]![0]!["FileSha256"] = new string('0', 64);
                File.WriteAllText(manifest, json.ToJsonString(), new UTF8Encoding(false));
            }
            var before = Fingerprints(path);
            var result = M1Recovery.Read(path, default, new Reporter(_ => { }));
            Need(!result.IsCleanEnd && result.Issues.Any(issue => issue.Contains(variant == "pcm" ? "PCM哈希" : "文件hash")),
                "进度接口跳过了哈希：" + variant);
            Need(SameFiles(path, before), "哈希校验改变了原件。");
        }
    }

    private static void ByteProgress(string root)
    {
        string path = Fixture(root, "byte-counts", 4000);
        string[] lines = File.ReadAllLines(Index(path));
        var session = JsonNode.Parse(lines[0])!;
        string raw = session["data"]!.ToJsonString();
        raw = raw[..^1] + ",\"syntheticText\":\"中文🙂字节\"}";
        lines[0] = Envelope(0, raw);
        File.WriteAllLines(Index(path), lines, new UTF8Encoding(false));
        long indexBytes = new FileInfo(Index(path)).Length;
        Need(indexBytes > File.ReadAllText(Index(path)).Length, "夹具没有非ASCII UTF8字节。");
        var before = Fingerprints(path);
        var oldSignature = M1Recovery.Read(path);
        var reports = new List<M1RecoveryProgress>();
        var current = M1Recovery.Read(path, default, new Reporter(reports.Add));
        Need(current.IsCleanEnd && JsonSerializer.Serialize(oldSignature) == JsonSerializer.Serialize(current),
            "带进度恢复改变了旧两参接口结果。");
        long pcmBytes = current.Capture.Tracks.Sum(track => track.FrameCount * track.Format.BlockAlign);
        long fileBytes = current.Capture.Tracks.Sum(track => new FileInfo(track.WavePath).Length);
        foreach (var (stage, total) in new[] { ("index", indexBytes), ("pcm", pcmBytes), ("files", fileBytes) })
        {
            var values = reports.Where(value => value.Stage == stage).ToArray();
            Need(values.Length >= 2 && values[0].Completed == 0 && values[^1].Completed == total &&
                values[^1].Total == total && values.All(value => value.Total is null ||
                    value.Completed >= 0 && value.Completed <= value.Total) &&
                values.Select(value => value.Completed).SequenceEqual(values.Select(value => value.Completed).Order()),
                stage + " 进度没有按实际累计字节单调到达总量。");
        }
        Need(reports.Count < 1000, "进度没有节流，随每包大量发事件。");
        Need(SameFiles(path, before), "报告进度改变了原件hash。");
        File.WriteAllText(Path.Combine(root, "actual-byte-progress.json"),
            JsonSerializer.Serialize(new { IsSynthetic = true, IndexBytes = indexBytes, PcmBytes = pcmBytes,
                FileBytes = fileBytes, Reports = reports }));
    }

    private static void ObserverAndCancellation(string root)
    {
        string path = Fixture(root, "observer-cancel", 4000);
        var before = Fingerprints(path); int calls = 0;
        var recovered = M1Recovery.Read(path, default, new Reporter(_ => { calls++; throw new InvalidOperationException("SYNTHETIC_OBSERVER_FAILURE"); }));
        Need(recovered.IsCleanEnd && calls == 1, "观察者异常破坏恢复或未关闭故障观察者。");
        foreach (string stage in new[] { "index", "pcm", "files" })
        {
            using var cancellation = new CancellationTokenSource();
            bool reached = false;
            try
            {
                M1Recovery.Read(path, cancellation.Token, new Reporter(value =>
                {
                    if (value.Stage == stage) { reached = true; cancellation.Cancel(); }
                }));
                throw new InvalidOperationException("阶段取消被吞掉：" + stage);
            }
            catch (OperationCanceledException) { Need(reached, "未到达要测试的阶段。"); }
        }
        Need(SameFiles(path, before) && M1Recovery.Read(path).IsCleanEnd, "取消修改了原件或阻止重试。");
    }

    private static void NoPcm(string root)
    {
        string path = Path.Combine(root, "no-pcm");
        using (var storage = new M1Storage(path, Epoch, DateTimeOffset.UtcNow, "合成无PCM", true))
        {
            storage.AddSegment("remote-1", "remote", new WaveFormat(Rate, 16, 1));
            storage.WriteBoundary("remote-1", "stop", "stop", Epoch + 10000000, null, 0, null);
            storage.Finish(10000000, true, ["没有PCM，不代表静音"]);
        }
        var reports = new List<M1RecoveryProgress>();
        var result = M1Recovery.Read(path, default, new Reporter(reports.Add));
        Need(result.IsCleanEnd && !result.Capture.Complete && result.Capture.Tracks.Single().FrameCount == 0 &&
            reports.Where(value => value.Stage == "pcm").All(value => value.Completed == 0 && value.Total == 0),
            "无PCM进度伪造了已捕获音频。");
    }

    private static string Fixture(string root, string name, int packetCount = 4)
    {
        string path = Path.Combine(root, name);
        using var storage = new M1Storage(path, Epoch, DateTimeOffset.UtcNow, "合成路径缓存恢复", true);
        storage.AddSegment("remote-1", "remote", new WaveFormat(Rate, 16, 1));
        storage.AddSegment("mic-1", "mic", new WaveFormat(Rate, 16, 1));
        storage.WritePacket("mic-1", new byte[FramesPerPacket * 2], Packet(0));
        for (int index = 0; index < packetCount; index++)
            storage.WritePacket("remote-1", new byte[FramesPerPacket * 2], Packet(index * FramesPerPacket));
        ulong end = Epoch + (ulong)packetCount * FramesPerPacket * 10000000 / Rate;
        storage.WriteBoundary("remote-1", "stop", "stop", end, (ulong)(packetCount * FramesPerPacket), 0, null);
        storage.WriteBoundary("mic-1", "stop", "stop", end, FramesPerPacket, 0, null);
        storage.Finish((long)(end - Epoch), true, []); return path;
    }

    private static CapturedPacket Packet(int offset) => new(offset, FramesPerPacket, FramesPerPacket, (ulong)offset,
        Epoch + (ulong)offset * 10000000 / Rate, 0, true);
    private static string Index(string path) => Path.Combine(path, "journal", "capture-index.jsonl");
    private static string Type(string line) => JsonNode.Parse(line)!["data"]!["type"]!.GetValue<string>();
    private static string Mutate(string line, Action<JsonNode> mutation)
    {
        var node = JsonNode.Parse(line)!; mutation(node["data"]!);
        return Envelope(node["sequence"]!.GetValue<long>(), node["data"]!.ToJsonString());
    }
    private static string Envelope(long sequence, string data) =>
        $"{{\"schemaVersion\":1,\"isSynthetic\":true,\"sequence\":{sequence},\"data\":{data},\"sha256\":\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data)))}\"}}";
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static Dictionary<string, string> Fingerprints(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(path, file), Hash);
    private static bool SameFiles(string path, Dictionary<string, string> before) =>
        Fingerprints(path).OrderBy(pair => pair.Key).SequenceEqual(before.OrderBy(pair => pair.Key));
    private static void Need(bool value, string message) => M3TranscriptTests.Need(value, message);
}
