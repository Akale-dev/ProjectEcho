using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Export;

internal static class M2SavedMixLoaderTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "saved-mix"); Directory.CreateDirectory(root);
        run("M3b/T25/T50-SYNTH saved mix reopens without rendering or changing originals", () => RoundTrip(root));
        run("M3b/T50-SYNTH saved mix rejects legacy and malformed provenance", () => Metadata(root));
        run("M3b/T50/T53-SYNTH saved mix rejects changed audio and original files", () => ChangedFiles(root));
        run("M3b/T22/T50-SYNTH saved mix cannot reuse same PCM with another meeting timeline", () => OtherMeeting(root));
        run("M3b/T20-SYNTH saved mix no-cache and cancellation are read-only", () => NoCache(root));
        run("M3b/T28-WINDOWS saved M4A mix reopens with full EOF verification", () => Compressed(root));
        run("M3F/T25/T50-WINDOWS WAV listening validates sources without decoding an unused compressed copy", () => ListeningWave(root));
        run("M3F/T28/T50-WINDOWS RF64 listening still requires verified compressed EOF", () => ListeningRf64(root));
    }

    private static void RoundTrip(string root)
    {
        string meeting = Session(root, "roundtrip");
        var generated = M2ExportService.RenderMeeting(meeting, encoder: null);
        var before = Fingerprints(meeting);
        var reopened = M2SavedMixLoader.LoadLatest(meeting) ?? throw new Exception("Saved render missing.");
        Need(reopened.WavePath == generated.WavePath && reopened.Directory == generated.Directory &&
            reopened.FrameCount == generated.FrameCount && reopened.DurationUs == generated.DurationUs &&
            reopened.WaveSha256 == generated.WaveSha256 && reopened.SourceStorageComplete == generated.SourceStorageComplete &&
            reopened.WaveTruePeak == generated.WaveTruePeak && reopened.WaveAppliedGain == generated.WaveAppliedGain,
            "Reopening changed the verified full mix.");
        Need(before.OrderBy(x => x.Key).SequenceEqual(Fingerprints(meeting).OrderBy(x => x.Key)), "Reopening wrote or rendered files.");
        Need(Directory.EnumerateDirectories(Path.Combine(meeting, "derived"), "mix-*").Count() == 1, "Reopening generated a duplicate mix.");
    }

    private static void Metadata(string root)
    {
        string meeting = Session(root, "metadata");
        var mix = M2ExportService.RenderMeeting(meeting, encoder: null);
        string render = Path.Combine(mix.Directory, "render.json");
        byte[] original = File.ReadAllBytes(render);
        var cases = new Action<JsonObject>[]
        {
            node => node["renderVersion"] = "m2-qpc-mono-2-rf64-truepeak",
            node => node["renderVersion"] = "m2-qpc-mono-3-mic-gap-preserving",
            node => node["renderVersion"] = "m2-qpc-mono-4-device-clock-preserving",
            node => node.Remove("sourceIndexSha256"),
            node => node["sourceManifestSha256"] = new string('0', 64),
            node => node["sourceIndexSha256"] = new string('0', 64),
            node => node["frameCount"] = 1,
            node => node["duration100ns"] = 1,
            node => node["sourceStorageComplete"] = false,
            node => node["sourceHashes"]![0]!["sha256"] = new string('0', 64),
            node => node["sourceHashes"] = new JsonArray(),
            node => node["wave"]!["file"] = "../../another-meeting.wav",
            node => node["wave"]!["sha256"] = new string('0', 64),
            node => node["wave"]!["additionalDelayFrames"] = 1,
            node => node["wave"]!["appliedGain"] = 2,
            node => node["wave"]!["truePeak"]!["FrameCount"] = 1
        };
        foreach (var mutate in cases)
        {
            var node = JsonNode.Parse(original)!.AsObject(); mutate(node);
            File.WriteAllText(render, node.ToJsonString());
            Reject<InvalidDataException>(() => M2SavedMixLoader.LoadLatest(meeting));
        }
        File.WriteAllBytes(render, original);
        string json = System.Text.Encoding.UTF8.GetString(original);
        File.WriteAllText(render, json.Insert(1, "\"schemaVersion\":\"m2-render-1\","));
        Reject<InvalidDataException>(() => M2SavedMixLoader.LoadLatest(meeting));
        File.WriteAllBytes(render, original);
        Need(M2SavedMixLoader.LoadLatest(meeting) is not null, "Restored verified metadata still rejected.");
    }

    private static void ChangedFiles(string root)
    {
        string meeting = Session(root, "changed");
        var mix = M2ExportService.RenderMeeting(meeting, encoder: null);
        byte[] wave = File.ReadAllBytes(mix.WavePath);
        var changed = wave.ToArray(); changed[^1] ^= 0x40; File.WriteAllBytes(mix.WavePath, changed);
        Reject<InvalidDataException>(() => M2SavedMixLoader.LoadLatest(meeting));
        File.WriteAllBytes(mix.WavePath, wave);
        string source = Directory.EnumerateFiles(Path.Combine(meeting, "sources"), "*.wav", SearchOption.AllDirectories).Single();
        var sourceBytes = File.ReadAllBytes(source); sourceBytes[^1] ^= 0x40; File.WriteAllBytes(source, sourceBytes);
        Reject<InvalidDataException>(() => M2SavedMixLoader.LoadLatest(meeting));
    }

    private static void OtherMeeting(string root)
    {
        string first = Session(root, "timeline-a", 0), second = Session(root, "timeline-b", 5_000_000);
        var mix = M2ExportService.RenderMeeting(first, encoder: null);
        string FirstWave(string path) => Directory.EnumerateFiles(Path.Combine(path, "sources"), "*.wav", SearchOption.AllDirectories).Single();
        Need(Hash(FirstWave(first)) == Hash(FirstWave(second)), "Timeline substitution test does not have identical original PCM.");
        string copied = Path.Combine(second, "derived", Path.GetFileName(mix.Directory)); Directory.CreateDirectory(copied);
        foreach (string file in Directory.EnumerateFiles(mix.Directory)) File.Copy(file, Path.Combine(copied, Path.GetFileName(file)));
        Reject<InvalidDataException>(() => M2SavedMixLoader.LoadLatest(second));
        // Even with a matching meeting-manifest fingerprint, the timeline index
        // independently binds the QPC placement of identical PCM to this meeting.
        string render = Path.Combine(copied, "render.json");
        var node = JsonNode.Parse(File.ReadAllBytes(render))!.AsObject();
        node["sourceManifestSha256"] = Hash(Path.Combine(second, "meeting.json")); File.WriteAllText(render, node.ToJsonString());
        Reject<InvalidDataException>(() => M2SavedMixLoader.LoadLatest(second));
    }

    private static void NoCache(string root)
    {
        string meeting = Session(root, "no-cache");
        Need(M2SavedMixLoader.LoadLatest(meeting) is null, "No render was invented.");
        string incomplete = Path.Combine(meeting, "derived", "mix-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(incomplete);
        File.WriteAllBytes(Path.Combine(incomplete, "mixed_full.wav.partial"), []);
        Need(M2SavedMixLoader.LoadLatest(meeting) is null, "Incomplete render was promoted.");
        var before = Fingerprints(meeting);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Reject<OperationCanceledException>(() => M2SavedMixLoader.LoadLatest(meeting, cancel.Token));
        Need(before.OrderBy(x => x.Key).SequenceEqual(Fingerprints(meeting).OrderBy(x => x.Key)), "Cancelled reopen modified files.");
    }

    private static void Compressed(string root)
    {
        string meeting = Session(root, "compressed");
        var generated = M2ExportService.RenderMeeting(meeting);
        Need(generated.CompressedPath is not null, "Actual M4A fixture failed to encode.");
        var loaded = M2SavedMixLoader.LoadLatest(meeting)!;
        Need(loaded.CompressedPath == generated.CompressedPath && loaded.CompressedSha256 == generated.CompressedSha256,
            "Reopened M4A association changed.");
        var bytes = File.ReadAllBytes(generated.CompressedPath!); bytes[^1] ^= 0x40; File.WriteAllBytes(generated.CompressedPath!, bytes);
        Reject<InvalidDataException>(() => M2SavedMixLoader.LoadLatest(meeting));
    }

    private static void ListeningWave(string root)
    {
        string meeting = Session(root, "listening-wave");
        var generated = M2ExportService.RenderMeeting(meeting);
        Need(generated.CompressedPath is not null, "Actual compressed fixture did not encode.");
        var before = Fingerprints(meeting);
        var listening = M2SavedMixLoader.LoadLatestForListening(meeting)!;
        Need(listening.WavePath == generated.WavePath && listening.WaveSha256 == generated.WaveSha256 &&
            listening.FrameCount == generated.FrameCount && listening.SourceStorageComplete == generated.SourceStorageComplete &&
            listening.CompressedPath is null && listening.CompressedSha256 is null,
            "Listening result must retain verified WAV provenance and omit unverified compressed audio.");
        Need(before.OrderBy(x => x.Key).SequenceEqual(Fingerprints(meeting).OrderBy(x => x.Key)), "Listening validation mutated originals.");
        File.WriteAllBytes(generated.CompressedPath!, [1, 2, 3]);
        Need(M2SavedMixLoader.LoadLatestForListening(meeting)?.WavePath == generated.WavePath,
            "A separate unused compressed copy delayed/rejected an independently valid WAV.");
        Reject<InvalidDataException>(() => M2SavedMixLoader.LoadLatest(meeting));
        var exported = M2ExportService.ExportParts(listening, new(M2ExportFormat.Wave, 1000000, 10, 0, false),
            Path.Combine(root, "listening-wave-export"));
        Need(exported.Parts.Count == 1, "Independently verified WAV failed the normal export checks.");
        var compressedExport = M2ExportService.ExportParts(listening, new(M2ExportFormat.M4a, 1000000, 10, 0, true),
            Path.Combine(root, "listening-m4a-export"));
        Need(compressedExport.Parts.Count == 1 && compressedExport.Parts[0].EndFrame == listening.FrameCount &&
            Hash(generated.WavePath) == generated.WaveSha256,
            "On-demand full M4A export failed complete decoding or modified its source WAV.");
        byte[] wave = File.ReadAllBytes(generated.WavePath); var damaged = wave.ToArray(); damaged[^1] ^= 0x40;
        File.WriteAllBytes(generated.WavePath, damaged);
        Reject<InvalidDataException>(() => M2SavedMixLoader.LoadLatestForListening(meeting));
        File.WriteAllBytes(generated.WavePath, wave);
        string source = Directory.EnumerateFiles(Path.Combine(meeting, "sources"), "*.wav", SearchOption.AllDirectories).Single();
        byte[] original = File.ReadAllBytes(source); original[^1] ^= 0x40; File.WriteAllBytes(source, original);
        Reject<InvalidDataException>(() => M2SavedMixLoader.LoadLatestForListening(meeting));
    }

    private static void ListeningRf64(string root)
    {
        string meeting = Session(root, "listening-rf64");
        var generated = M2ExportService.RenderMeeting(meeting);
        byte[] pcm;
        using (var reader = new M2WaveReader(generated.WavePath))
        {
            pcm = new byte[checked((int)reader.DataLength)];
            reader.ReadExactly(pcm);
        }
        // Small RF64 fixture exercises the actual WPF fallback contract without a multi-GB allocation.
        using (var stream = File.Create(generated.WavePath))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0x34364652u); writer.Write(uint.MaxValue); writer.Write(0x45564157u);
            writer.Write(0x34367364u); writer.Write(28u); writer.Write((ulong)(72 + pcm.Length));
            writer.Write((ulong)pcm.Length); writer.Write((ulong)generated.FrameCount); writer.Write(0u);
            writer.Write(0x20746d66u); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write(48000); writer.Write(96000); writer.Write((ushort)2); writer.Write((ushort)16);
            writer.Write(0x61746164u); writer.Write(uint.MaxValue); writer.Write(pcm);
        }
        string renderPath = Path.Combine(generated.Directory, "render.json");
        var render = JsonNode.Parse(File.ReadAllBytes(renderPath))!.AsObject();
        render["wave"]!["sha256"] = Hash(generated.WavePath); File.WriteAllText(renderPath, render.ToJsonString());
        var loaded = M2SavedMixLoader.LoadLatestForListening(meeting)!;
        Need(loaded.CompressedPath == generated.CompressedPath && loaded.CompressedSha256 == generated.CompressedSha256,
            "RF64 listening omitted its required decoded/validated compressed fallback.");
        File.WriteAllBytes(generated.CompressedPath!, [1, 2, 3]);
        Reject<InvalidDataException>(() => M2SavedMixLoader.LoadLatestForListening(meeting));
    }

    private static string Session(string root, string name, ulong start = 0)
    {
        string path = Path.Combine(root, name); const ulong epoch = 30_000_000_000;
        using var storage = new M1Storage(path, epoch, DateTimeOffset.UtcNow, "缓存合成测试", true);
        storage.AddSegment("remote-1", "remote", new WaveFormat(8000, 16, 1));
        var bytes = new byte[1600];
        for (int i = 0; i < 800; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)(3000 * Math.Sin(2 * Math.PI * 320 * i / 8000)));
        storage.WritePacket("remote-1", bytes, new CapturedPacket(0, 800, 800, 0, epoch + start, 0, true));
        storage.WriteBoundary("remote-1", "stop", "stop", epoch + start + 1_000_000, 800, 0, null);
        storage.Finish(10_000_000, true, []); return path;
    }

    private static Dictionary<string, string> Fingerprints(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(path, file), Hash);
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected rejection did not occur: " + typeof(T).Name); }
}
