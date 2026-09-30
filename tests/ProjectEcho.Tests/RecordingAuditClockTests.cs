using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectEcho.Core;

/// <summary>Disk-only synthetic fixtures; no device, capture, or playback APIs.</summary>
internal static class RecordingAuditClockTests
{
    public static void Run(string output)
    {
        var validDirectory = CreateFixture(output, "audit-clock-valid", false);
        var validErrors = new List<string>();
        RecordingAudit.Inspect(validDirectory, validErrors, []);
        Require(validErrors.Count == 0, "Valid fixture failed: " + string.Join(",", validErrors));

        var repeatedDirectory = CreateFixture(output, "audit-clock-repeated", true);
        var originalHashes = HashFiles(repeatedDirectory);
        var repeatedErrors = new List<string>();
        RecordingAudit.Inspect(repeatedDirectory, repeatedErrors, []);
        Require(repeatedErrors.SequenceEqual(["remote:qpc_not_increasing:1"]),
            "Repeated API QPC must independently fail audit despite complete/valid metadata: " + string.Join(",", repeatedErrors));
        Require(originalHashes.SequenceEqual(HashFiles(repeatedDirectory)), "Read-only audit changed its input artifacts.");
    }

    private static string CreateFixture(string output, string name, bool repeatedQpc)
    {
        var directory = Path.GetFullPath(Path.Combine(output, name));
        Directory.CreateDirectory(Path.Combine(directory, "sources"));
        Directory.CreateDirectory(Path.Combine(directory, "journal"));
        File.WriteAllText(Path.Combine(directory, "TEST_FIXTURE"), "Synthetic clock audit data; not recorded audio.");
        var wavePath = Path.Combine(directory, "sources", "remote.wav");
        WriteSilentPcm16(wavePath, 960);
        WriteSilentPcm16(Path.Combine(directory, "mixed-preview.wav"), 960);
        const ulong epoch = 100_000;
        var format = new PcmFormat(48_000, 2, 16, false, 4);
        CapturedPacket[] packets =
        [
            new(0, 480, 480, 0, epoch, 2, true),
            new(480, 480, 480, 480, repeatedQpc ? epoch : epoch + 100_000, 2, true)
        ];
        var indexPath = Path.Combine(directory, "journal", "capture-index.jsonl");
        var result = new CaptureResult(directory, indexPath, DateTimeOffset.UnixEpoch, epoch, 200_000,
            [new CapturedTrack("remote", wavePath, format, 960, packets)], true, []);
        var manifest = new
        {
            schemaVersion = "m0-spike-1", sourceMode = "ProcessTree", microphoneEnabled = false,
            selection = new { reusableSelection = false, microphoneDeviceId = (string?)null }, result
        };
        File.WriteAllText(Path.Combine(directory, "capture-result.json"), JsonSerializer.Serialize(manifest), new UTF8Encoding(false));
        using var journal = new StreamWriter(new FileStream(indexPath, FileMode.CreateNew, FileAccess.Write), new UTF8Encoding(false));
        long sequence = 0;
        void Append(object data)
        {
            var json = JsonSerializer.Serialize(data);
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            journal.WriteLine($"{{\"schemaVersion\":1,\"isSynthetic\":false,\"sequence\":{sequence++},\"data\":{json},\"sha256\":\"{hash}\"}}");
        }
        // Mirror the capture schema's isSynthetic field so the actual audit path is exercised;
        // the directory is explicitly marked TEST_FIXTURE and never represented as capture evidence.
        Append(new { type = "session", epochQpc100ns = U(epoch) });
        Append(new { type = "segment", segmentId = "remote-1", sourceId = "remote" });
        foreach (var packet in packets)
            Append(new
            {
                type = "packet", segmentId = "remote-1", chunkId = "remote-1", path = "sources/remote.wav",
                deviceFramePosition = U(packet.DevicePosition), devicePositionValid = true,
                qpcPosition100ns = U(packet.QpcPosition100ns), rawFrameCount = packet.RawFrameCount,
                flags = new[] { "silent" }, retainedFrameOffset = 0, frameCount = packet.FrameCount,
                chunkFrameOffset = packet.WaveFrameOffset,
                sessionStartUs = AudioClock.SessionMicroseconds(packet.QpcPosition100ns, epoch),
                timestampQuality = "device_referenced"
            });
        Append(new { type = "boundary", segmentId = "remote-1", operation = "stop", acceptedThroughSequence = 3,
            discardedFrameCount = 0, effectiveSessionUs = 20_000 });
        Append(new { type = "checkpoint", coveredThroughSequence = 4,
            chunks = new[] { new { chunkId = "remote-1", path = "sources/remote.wav", committedFrameCount = 960,
                committedPcmBytes = 3840, dataOffsetBytes = 44 } } });
        return directory;
    }

    private static void WriteSilentPcm16(string path, int frames)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write("RIFF"u8); writer.Write(36 + frames * 4); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(48_000);
        writer.Write(192_000); writer.Write((short)4); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(frames * 4); writer.Write(new byte[frames * 4]);
    }

    private static string[] HashFiles(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => path + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private static string U(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
