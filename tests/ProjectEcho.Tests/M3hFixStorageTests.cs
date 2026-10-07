using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Export;

internal static class M3hFixStorageTests
{
    private const ulong Epoch = 90_000_000_000;
    private const ulong Pause = 30_000_000;
    private const int Rate = 8000;

    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3hfix-storage"); Directory.CreateDirectory(root);
        run("T15/T22/T24/T53-SYNTH M3Hfix disk-only pause mapping mixes simultaneous remote mic and keeps remaining tail", () => MappedMix(root));
        run("T15/T22/T53-SYNTH M3Hfix legacy missing policy retains full elapsed pause silence", () => Legacy(root));
        run("T20/T53-SYNTH M3Hfix crash while preparing resume recovers only active checkpoint and unchanged clocks", () => ResumePreparationCrash(root));
        run("T22/T53-SYNTH M3Hfix rejects missing changed or incompatible pause clock mapping", () => MappingIntegrity(root));
        run("T22/T52-SYNTH M3Hfix freezes segment clock offset before first PCM and preserves raw boundary checks", () => MappingBoundary(root));
        run("T15/T22/T52-SYNTH M3Hfix resume preroll keeps raw clocks and maps retained prefix to recording zero", () => ResumePreroll(root));
    }

    private static void MappedMix(string root)
    {
        string path = Fixture(root, "mapped", RecordingTimeline.ExcludePaused);
        var before = HashOriginals(path);
        var recovered = M1Recovery.Read(path);
        Need(recovered.IsCleanEnd && recovered.Capture.Duration100ns == 5_000_000, string.Join(" | ", recovered.Issues));
        Need(recovered.Capture.TimelinePolicy == RecordingTimeline.ExcludePaused, "新录音策略未从磁盘恢复。");
        var later = recovered.Capture.Tracks.Where(track => track.SegmentId is "remote-2" or "mic-2").ToArray();
        Need(later.Length == 2 && later.All(track => track.ExcludedPause100ns == Pause), "双路未共享同一暂停扣除。");
        Need(later.All(track => track.Packets[0].QpcPosition100ns == Epoch + Pause + 2_500_000 && track.Packets[0].DevicePosition == 70000),
            "恢复篡改了原始 QPC 或设备帧。");
        var mix = M2ExportService.RenderMeeting(path, encoder: null);
        Need(mix.FrameCount == 24000, "新混音保留了暂停静音或截短有效尾部。");
        using (var reader = new WaveFileReader(mix.WavePath))
        {
            var pcm = new byte[checked((int)reader.Length)]; reader.ReadExactly(pcm);
            Near(Sample(pcm, 4800), .3); Near(Sample(pcm, 14400), .5); Near(Sample(pcm, 21600), .3);
        }
        using (var render = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(mix.Directory, "render.json"))))
            Need(render.RootElement.GetProperty("timeline").GetString() == "pause_excluded_qpc_packet_mapping", "混音元数据误标完整经过时间。");
        Need(M2SavedMixLoader.LoadLatest(path)?.FrameCount == 24000, "磁盘重新加载不识别新录音时间轴。");
        string renderPath = Path.Combine(mix.Directory, "render.json"); byte[] validRender = File.ReadAllBytes(renderPath);
        try
        {
            var changed = JsonNode.Parse(validRender)!.AsObject(); changed["timeline"] = "complete_elapsed_qpc_packet_mapping";
            File.WriteAllText(renderPath, changed.ToJsonString());
            Reject<InvalidDataException>(() => M2SavedMixLoader.LoadLatest(path));
            Reject<InvalidDataException>(() => M2ExportService.ExportParts(mix, new(M2ExportFormat.Wave, 1_000_000, 30, 0, true), Path.Combine(root, "wrong-policy-export")));
        }
        finally { File.WriteAllBytes(renderPath, validRender); }
        var exported = M2ExportService.ExportParts(mix, new(M2ExportFormat.Wave, 1_000_000, 30, 0, true), Path.Combine(root, "mapped-export"));
        using (var manifest = JsonDocument.Parse(File.ReadAllBytes(exported.ManifestPath!)))
            Need(manifest.RootElement.GetProperty("timeline").GetString() == "pause_excluded_qpc_packet_mapping", "分片导出丢失时间轴策略。");
        Need(before.OrderBy(x => x.Key).SequenceEqual(HashOriginals(path).OrderBy(x => x.Key)), "混音/导出修改原轨或时钟证据。");
    }

    private static void Legacy(string root)
    {
        string path = Fixture(root, "legacy", RecordingTimeline.Elapsed);
        RewriteJournal(path, data => { data.Remove("timelinePolicy"); data.Remove("excludedPause100ns"); });
        RewriteManifest(path, data =>
        {
            data.Remove("timelinePolicy");
            foreach (var chunk in data["chunks"]!.AsArray()) chunk!.AsObject().Remove("ExcludedPause100ns");
        });
        var recovered = M1Recovery.Read(path);
        Need(recovered.IsCleanEnd && recovered.Capture.TimelinePolicy == RecordingTimeline.Elapsed && recovered.Capture.Duration100ns == 35_000_000,
            "旧无policy录音的完整经过时间被改变。 " + string.Join(" | ", recovered.Issues));
        Need(recovered.Capture.Tracks.All(track => track.ExcludedPause100ns == 0), "旧录音被凭空扣除暂停。");
        var mix = M2ExportService.RenderMeeting(path, encoder: null);
        Need(mix.FrameCount == 168000, "旧录音时长被新暂停策略剪短。");
        using var reader = new WaveFileReader(mix.WavePath);
        var pcm = new byte[checked((int)reader.Length)]; reader.ReadExactly(pcm);
        Near(Sample(pcm, 48000), 0); Near(Sample(pcm, 160800), .5);
        Need(M2SavedMixLoader.LoadLatest(path)?.FrameCount == 168000, "旧混音时间轴无法重新加载。");
    }

    private static void ResumePreparationCrash(string root)
    {
        string path = Path.Combine(root, "resume-crash");
        using (var storage = new M1Storage(path, Epoch, DateTimeOffset.UtcNow, "合成恢复准备中断", true, RecordingTimeline.ExcludePaused))
        {
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1));
            storage.WritePacket("remote-1", Pcm(2000, .2f), Packet(Epoch, 0, 2000));
            storage.WriteBoundary("remote-1", "pause", "pause", Epoch + 2_500_000, 2000, 0, null);
            storage.WriteEvent("pause", "pause", 250000);
            storage.SetObservedSessionUs(250000); storage.Checkpoint();
            storage.AddSegment("remote-2", "remote", WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1));
            storage.SetSegmentTimelineOffset("remote-2", Pause);
            storage.Checkpoint(); // prepared empty file is not an observed audio segment
        }
        var recovered = M1Recovery.Read(path);
        Need(recovered.HasCheckpoint && !recovered.IsCleanEnd && recovered.Capture.Duration100ns == 2_500_000 && recovered.Capture.Tracks.Count == 1,
            "准备中断把未启动段或等待时间当成已录音。");
        string copy = Path.Combine(root, "resume-crash-recovered");
        var materialized = M1Recovery.RecoverTo(path, copy);
        Need(materialized.TimelinePolicy == RecordingTimeline.ExcludePaused && materialized.Duration100ns == 2_500_000 &&
            materialized.Tracks.Single().Packets.Single().QpcPosition100ns == Epoch, "恢复副本丢失时间轴策略或改写原始时钟。");
        Need(File.ReadAllLines(Path.Combine(copy, "journal/events.jsonl")).Length == 1, "已确认暂停事件丢失。");
    }

    private static void MappingIntegrity(string root)
    {
        var mutations = new (string Name, Action<JsonObject> Apply)[]
        {
            ("missing-offset", data => { if (data["type"]!.GetValue<string>() == "segment") data.Remove("excludedPause100ns"); }),
            ("changed-offset", data => { if (data["segmentId"]?.GetValue<string>() == "remote-2" && data["type"]!.GetValue<string>() == "segment") data["excludedPause100ns"] = "31000000"; }),
            ("legacy-nonzero", data => { if (data["type"]!.GetValue<string>() == "session") data["timelinePolicy"] = RecordingTimeline.Elapsed; }),
            ("unknown-policy", data => { if (data["type"]!.GetValue<string>() == "session") data["timelinePolicy"] = "guess-pause"; })
        };
        foreach (var mutation in mutations)
        {
            string path = Fixture(root, mutation.Name, RecordingTimeline.ExcludePaused);
            RewriteJournal(path, mutation.Apply);
            var recovered = M1Recovery.Read(path);
            Need(!recovered.IsCleanEnd && recovered.Issues.Count != 0, "错误映射被当成有效原件：" + mutation.Name);
        }
        string mismatch = Fixture(root, "manifest-policy", RecordingTimeline.ExcludePaused);
        RewriteManifest(mismatch, data => data.Remove("timelinePolicy"));
        Need(!M1Recovery.Read(mismatch).IsCleanEnd, "manifest策略缺失没有与journal核对。");
    }

    private static void MappingBoundary(string root)
    {
        using var storage = new M1Storage(Path.Combine(root, "mapping-guard"), Epoch, DateTimeOffset.UtcNow, "合成时钟固定", true, RecordingTimeline.ExcludePaused);
        storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1));
        storage.SetSegmentTimelineOffset("remote-1", Pause);
        Reject<InvalidOperationException>(() => storage.SetSegmentTimelineOffset("remote-1", Pause));
        Reject<InvalidDataException>(() => storage.WritePacket("remote-1", Pcm(8, .2f), Packet(Epoch, 0, 8)));
        storage.WritePacket("remote-1", Pcm(8, .2f), Packet(Epoch + Pause, 0, 8));
        Reject<InvalidOperationException>(() => storage.SetSegmentTimelineOffset("remote-1", 0));
        Reject<InvalidDataException>(() => storage.WriteBoundary("remote-1", "bad", "stop", Epoch + Pause + 9999, 8, 0, null));
        storage.WriteBoundary("remote-1", "stop", "stop", Epoch + Pause + 10000, 8, 0, null);
        storage.Finish(10000, true, []);
    }

    private static void ResumePreroll(string root)
    {
        string path = Path.Combine(root, "resume-preroll");
        ulong rawQpc = Epoch + Pause - 2500;
        using (var storage = new M1Storage(path, Epoch, DateTimeOffset.UtcNow, "合成恢复前滚", true, RecordingTimeline.ExcludePaused))
        {
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1));
            storage.SetSegmentTimelineOffset("remote-1", Pause);
            Reject<InvalidDataException>(() => storage.WritePacket("remote-1", Pcm(9, .2f),
                new(0, 9, 10, 100, rawQpc, 0, true, true, RetainedFrameOffset: 1)));
            storage.WritePacket("remote-1", Pcm(8, .2f), new(0, 8, 10, 100, rawQpc, 0, true, true, RetainedFrameOffset: 2));
            storage.WritePacket("remote-1", Pcm(8, .2f), new(8, 8, 8, 110, Epoch + Pause + 10000, 0, true));
            storage.WriteBoundary("remote-1", "stop", "stop", Epoch + Pause + 20000, 118, 2, "after_boundary");
            storage.Finish(20000, true, []);
        }
        var recovered = M1Recovery.Read(path);
        Need(recovered.IsCleanEnd && recovered.Capture.Duration100ns == 20000, string.Join(" | ", recovered.Issues));
        var first = recovered.Capture.Tracks.Single().Packets[0];
        Need(first.QpcPosition100ns == rawQpc && first.DevicePosition == 100 && first.RawFrameCount == 10 && first.RetainedFrameOffset == 2,
            "恢复前滚处理改写原始时钟或丢失保留前缀偏移。");
        var mix = M2ExportService.RenderMeeting(path, encoder: null);
        Need(mix.FrameCount == 96, "前滚映射生成负起点、静音或错误长度。");
        using var reader = new WaveFileReader(mix.WavePath);
        var pcm = new byte[checked((int)reader.Length)]; reader.ReadExactly(pcm);
        Near(Sample(pcm, 0), .2); Near(Sample(pcm, 95), .2);
    }

    private static string Fixture(string root, string name, string policy)
    {
        string path = Path.Combine(root, name); bool active = policy == RecordingTimeline.ExcludePaused;
        using var storage = new M1Storage(path, Epoch, DateTimeOffset.UtcNow, "合成暂停混音", true, policy);
        foreach (string source in new[] { "remote", "mic" })
        {
            storage.AddSegment(source + "-1", source, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1));
            storage.WritePacket(source + "-1", Pcm(2000, source == "remote" ? .1f : .2f), Packet(Epoch, 0, 2000));
            storage.WriteBoundary(source + "-1", "pause", "pause", Epoch + 2_500_000, 2000, 0, null);
        }
        storage.WriteEvent("pause", "pause", 250000);
        storage.WriteEvent("resume", "resume", active ? 250000 : 3250000);
        foreach (string source in new[] { "remote", "mic" })
        {
            storage.AddSegment(source + "-2", source, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1));
            if (active) storage.SetSegmentTimelineOffset(source + "-2", Pause);
            int frames = source == "remote" ? 2000 : 1000;
            storage.WritePacket(source + "-2", Pcm(frames, source == "remote" ? .3f : .2f), Packet(Epoch + Pause + 2_500_000, 70000, frames));
            storage.WriteBoundary(source + "-2", source == "remote" ? "stop" : "mic-off", source == "remote" ? "stop" : "mic_off",
                Epoch + Pause + 2_500_000 + (ulong)frames * 1250, (ulong)(70000 + frames), 0, null);
        }
        storage.Finish(active ? 5_000_000 : 35_000_000, true, []);
        return path;
    }

    private static CapturedPacket Packet(ulong qpc, ulong device, int frames) => new(0, frames, frames, device, qpc, 0, true);
    private static byte[] Pcm(int frames, float value)
    {
        var bytes = new byte[frames * 4];
        for (int i = 0; i < frames; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), value);
        return bytes;
    }
    private static double Sample(byte[] pcm, int frame) => BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(frame * 2)) / 32768d;
    private static void Near(double actual, double expected) => Need(Math.Abs(actual - expected) < .0001, $"混音内容不符：{actual} / {expected}");
    private static Dictionary<string, string> HashOriginals(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .Where(file => !Path.GetRelativePath(path, file).StartsWith("derived", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(file => Path.GetRelativePath(path, file), file => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
    private static void RewriteJournal(string path, Action<JsonObject> change)
    {
        string file = Path.Combine(path, "journal/capture-index.jsonl");
        var lines = File.ReadAllLines(file).Select(line =>
        {
            var envelope = JsonNode.Parse(line)!.AsObject(); var data = envelope["data"]!.AsObject(); change(data);
            envelope["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToJsonString())));
            return envelope.ToJsonString();
        }).ToArray();
        File.WriteAllLines(file, lines, new UTF8Encoding(false));
    }
    private static void RewriteManifest(string path, Action<JsonObject> change)
    {
        string file = Path.Combine(path, "meeting.json"); var data = JsonNode.Parse(File.ReadAllText(file))!.AsObject(); change(data); File.WriteAllText(file, data.ToJsonString());
    }
    private static void Need(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("未拒绝预期错误：" + typeof(T).Name);
    }
}
