using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

internal static class MicrophoneProcessingMetadataTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        run("T53-SYNTH microphone processing mode survives disk-only recovery without changing raw clocks", () => RoundTrip(Path.Combine(output, "mic-processing-roundtrip")));
        run("T50/T53-SYNTH legacy missing processing mode remains readable and unknown mode is rejected", () => LegacyAndInvalid(Path.Combine(output, "mic-processing-legacy")));
        run("T22/T53-SYNTH RAW label cannot erase a real startup gap or repair incomplete PCM", () => LabelDoesNotExcuseGap(Path.Combine(output, "mic-processing-gap")));
    }

    private const ulong Epoch = 10_000_000;
    private static void Fixture(string folder, string? mode, int firstFrames = 160)
    {
        using var store = new M1Storage(folder, Epoch, DateTimeOffset.UnixEpoch, "synthetic processing metadata", true);
        store.AddSegment("mic-1", "mic", new WaveFormat(16000, 16, 1), processingMode: mode);
        store.WritePacket("mic-1", new byte[firstFrames * 2], new(0, firstFrames, firstFrames, 0, Epoch, 0, true));
        store.WritePacket("mic-1", new byte[320], new(firstFrames, 160, 160, 160, Epoch + 100000, 0, true));
        store.WriteBoundary("mic-1", "stop", "stop", Epoch + 200000, 320, 0, null);
        store.Finish(200000, true, []);
    }

    private static void RoundTrip(string root)
    {
        foreach (string mode in new[] { "shared-default", "shared-raw", "shared-converted" })
        {
            string folder = Path.Combine(root, mode); Fixture(folder, mode);
            var original = Files(folder); var read = M1Recovery.Read(folder);
            Check(read.IsCleanEnd && read.Issues.Count == 0, "Processing metadata blocked valid storage.");
            Check(read.Capture.Tracks.All(t => t.ProcessingMode == mode), "Disk read lost processing mode.");
            var packets = read.Capture.Tracks.SelectMany(t => t.Packets).ToArray();
            Check(packets.Length == 2 && packets[0].DevicePosition == 0 && packets[1].DevicePosition == 160 &&
                packets[0].QpcPosition100ns == Epoch && packets[1].QpcPosition100ns == Epoch + 100000 &&
                packets.All(p => p.RawFrameCount == 160 && p.FrameCount == 160), "Processing metadata rewrote clocks or frames.");
            var recovered = M1Recovery.RecoverTo(folder, folder + "-recovered");
            Check(recovered.Tracks.All(t => t.ProcessingMode == mode), "Recovery copy lost processing provenance.");
            Check(original.All(p => Hash(p.Key) == p.Value), "Processing/recovery changed originals.");
        }
    }

    private static void LegacyAndInvalid(string root)
    {
        string legacy = Path.Combine(root, "legacy"); Fixture(legacy, null);
        RewriteMode(legacy, remove: true, null);
        var read = M1Recovery.Read(legacy);
        Check(read.IsCleanEnd && read.Capture.Tracks.All(t => t.ProcessingMode is null), "Legacy mode-less recording rejected or mislabeled RAW.");
        string invalid = Path.Combine(root, "invalid"); Fixture(invalid, "shared-raw");
        RewriteMode(invalid, remove: false, "invented-mode");
        var refused = M1Recovery.Read(invalid);
        Check(!refused.IsCleanEnd && refused.Issues.Count > 0, "Unknown processing mode was accepted.");
        using var store = new M1Storage(Path.Combine(root, "writer-reject"), Epoch, DateTimeOffset.UnixEpoch, "synthetic", true);
        try { store.AddSegment("mic-1", "mic", new WaveFormat(16000, 16, 1), processingMode: "invented-mode"); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("Writer accepted an unknown processing mode.");
    }

    private static void LabelDoesNotExcuseGap(string root)
    {
        Fixture(root, "shared-raw", 124);
        var read = M1Recovery.Read(root);
        Check(read.IsCleanEnd && !read.Capture.Complete && read.Capture.Warnings.Any(w => w.Contains("缺口")), "RAW label hid missing frames.");
        var packets = read.Capture.Tracks.SelectMany(t => t.Packets).ToArray();
        Check(packets[0].RawFrameCount == 124 && packets[1].DevicePosition == 160 && packets[1].ClockJitter100ns == 0,
            "RAW label fabricated a full initial packet or a jitter exemption.");
    }

    private static void RewriteMode(string root, bool remove, string? mode)
    {
        string path = Path.Combine(root, "journal", "capture-index.jsonl");
        var lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            var envelope = JsonNode.Parse(lines[i])!.AsObject();
            var data = envelope["data"]!.AsObject();
            if (data["type"]!.GetValue<string>() != "segment") continue;
            if (remove) data.Remove("processingMode"); else data["processingMode"] = mode;
            string body = data.ToJsonString();
            envelope["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
            lines[i] = envelope.ToJsonString();
        }
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }
    private static Dictionary<string, string> Files(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, Hash);
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
