using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Export;

internal static class M3bMicrophoneClockTests
{
    private const ulong Epoch = 10_000_000_000;

    internal static void Register(Action<string, Action> run, string output)
    {
        // Relative API clock intervals only, normalized from the three failed
        // local journals. No identity, meeting text, audio or absolute time here.
        long[][] intervals = [[0, 100334, 199976, 307587],
            [0, 100045, 200042, 300071, 400075, 500070, 600040, 707649], [0, 107563]];
        for (int i = 0; i < intervals.Length; i++)
        {
            int index = i;
            run($"M3b/T22/T25/T52-SYNTH mic device-contiguous QPC jitter case {index + 1} preserves raw clocks and cutoff",
                () => Timeline(Path.Combine(output, "m3b-mic-jitter", $"case-{index + 1}"), intervals[index]));
        }
        run("M3b/T22/T53-SYNTH mic clock policy rejects undeclared corrupt/discontinuous clocks",
            () => Safety(Path.Combine(output, "m3b-mic-jitter", "safety")));
        foreach (int jitter in new[] { -7600, 7600, -625 })
            run($"M3b/T25/T52/T53-SYNTH mic 159+1-frame chunk split maps whole raw packet with {jitter} QPC jitter",
                () => ChunkSplit(Path.Combine(output, "m3b-mic-jitter", $"split-{jitter}"), jitter));
        run("M3b/T15/T52-SYNTH mic tiny explicit off gap is preserved between separate segments",
            () => TinyOffGap(Path.Combine(output, "m3b-mic-jitter", "tiny-off-gap")));
    }

    private static void Timeline(string root, long[] observed)
    {
        // The failed writer did not persist the rejected packet. This final
        // return-to-cadence packet is explicitly synthetic, not a recovered fact.
        // It demonstrates the old >one-frame overlap rejection after the exact
        // positive deviation that each preserved journal contains.
        long[] qpcs = [.. observed, observed[^2] + 200_000];
        long oldDifference = qpcs[^1] - qpcs[^2] - 100_000;
        Need(oldDifference < -625, "Fixture does not reproduce old >one-frame QPC-overlap rejection at 16 kHz.");
        int lastKept = 80;
        ulong cutoff = Epoch + (ulong)qpcs[^1] + 50_000;
        using (var storage = new M1Storage(root, Epoch, DateTimeOffset.UtcNow, "合成麦克风连续设备时钟", true))
        {
            storage.AddSegment("mic-1", "mic", new WaveFormat(16000, 16, 1));
            for (int i = 0; i < qpcs.Length; i++)
            {
                int frames = i == qpcs.Length - 1 ? lastKept : 160;
                var packet = Packet(i, qpcs[i], frames);
                Need(AudioClock.CompleteFramesBefore(packet.QpcPosition100ns, cutoff, 16000, 160) == frames,
                    "Synthetic cutoff lost or admitted a complete frame.");
                storage.WritePacket("mic-1", Samples(frames), packet);
            }
            storage.WriteBoundary("mic-1", "mic-off", "mic_off", cutoff, (ulong)((qpcs.Length - 1) * 160 + lastKept), 80, "after_boundary");
            var result = storage.Finish((long)(cutoff - Epoch) + 1_000_000, true, []);
            Need(result.StorageComplete, "Bounded valid device-contiguous jitter did not finish.");
            Need(result.Warnings.Any(w => w.Contains("小幅抖动")), "Clock deviation was hidden from diagnostics.");
        }
        var read = M1Recovery.Read(root);
        Need(read.IsCleanEnd && read.Capture.Complete, "Bounded jitter failed disk-only reconstruction: " + string.Join(" | ", read.Issues));
        var track = read.Capture.Tracks.Single();
        Need(track.FrameCount == (qpcs.Length - 1) * 160 + lastKept, "MicOff raw frames changed.");
        for (int i = 0; i < qpcs.Length; i++)
            Need(track.Packets[i].QpcPosition100ns == Epoch + (ulong)qpcs[i] && track.Packets[i].DevicePosition == (ulong)(i * 160),
                "Clock jitter handling rewrote raw API clocks.");
        Need(track.Packets.Any(p => p.ClockJitter100ns < 0) && track.Packets.Any(p => p.ClockJitter100ns > 0),
            "Signed clock deviations were not preserved.");
        string sourceHash = Hash(track.WavePath), mixed = Path.Combine(root, "mixed.wav");
        M2TimelineRenderer.Render(read.Capture, mixed, CancellationToken.None);
        using (var reader = new M2WaveReader(mixed))
        {
            var pcm = reader.ReadFrames(0, checked((int)reader.FrameCount));
            int micEnd = checked((int)Math.Ceiling((double)(cutoff - Epoch) * 48000 / AudioClock.UnitsPerSecond));
            Need(pcm.Take(micEnd).All(v => Math.Abs(v - .25f * M2TimelineRenderer.SourceGain) < .001f), "Packet jitter created a hole, duplicate mix or lost valid tail.");
            Need(pcm.Skip(micEnd).All(v => v == 0), "Mix contains post-MicOff PCM.");
        }
        Need(Hash(track.WavePath) == sourceHash, "Jitter rendering modified an original.");
        File.WriteAllText(Path.Combine(root, "clock-evidence.json"), JsonSerializer.Serialize(new
        {
            observedRelativeQpc100ns = observed, extrapolatedNextQpc100ns = qpcs[^1],
            oldRejection = "原包QPC重叠超过一帧，拒绝写入PCM。", oldDifference100ns = oldDifference,
            originalRejectedPacketAvailable = false, audio = "SYNTHETIC_CONSTANT_PCM", rawClocksPreserved = true,
            packetCount = qpcs.Length, frameCount = track.FrameCount,
            realCapturePerformed = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ChunkSplit(string root, int jitter)
    {
        const int rate = 16000;
        long frameOffset = 0;
        ulong previousQpc = 0, cutoff;
        using (var storage = new M1Storage(root, Epoch, DateTimeOffset.UtcNow, "合成跨块设备时钟抖动", true))
        {
            storage.AddSegment("mic-1", "mic", new WaveFormat(rate, 16, 1));
            Write(1, Epoch);
            for (int i = 0; i < 2999; i++) Write(160, Epoch + (ulong)frameOffset * 625);
            Need(frameOffset == 479841, "Fixture does not create a 159+1 split at 30 seconds.");
            previousQpc = Epoch + (ulong)frameOffset * 625;
            Write(160, previousQpc);
            ulong nextQpc = checked((ulong)((long)previousQpc + 100_000 + jitter));
            Write(160, nextQpc);
            cutoff = nextQpc + 100_000;
            storage.WriteBoundary("mic-1", "off", "mic_off", cutoff, (ulong)frameOffset, 0, null);
            storage.Finish((long)(cutoff - Epoch) + 100_000, true, []);
            void Write(int count, ulong qpc)
            {
                storage.WritePacket("mic-1", Samples(count), new(frameOffset, count, count, (ulong)frameOffset, qpc, 0, true));
                frameOffset += count;
            }
        }
        var recovered = M1Recovery.Read(root);
        Need(recovered.IsCleanEnd, "Split jitter journal failed to reopen.");
        var fragments = recovered.Capture.Tracks.SelectMany(t => t.Packets).Where(p => p.QpcPosition100ns == previousQpc).ToArray();
        Need(fragments.Select(p => p.FrameCount).SequenceEqual([159, 1]) &&
            fragments.Select(p => p.RetainedFrameOffset).SequenceEqual([0, 159]), "Raw packet was not split as required.");
        var originalHashes = recovered.Capture.Tracks.ToDictionary(t => t.WavePath, t => Hash(t.WavePath));
        var copy = M1Recovery.RecoverTo(root, root + "-recovered");
        foreach (var (capture, suffix) in new[] { (recovered.Capture, "original"), (copy, "copy") })
        {
            string destination = Path.Combine(root, $"mix-{suffix}.wav");
            M2TimelineRenderer.Render(capture, destination, CancellationToken.None);
            using var reader = new M2WaveReader(destination);
            var pcm = reader.ReadFrames(0, (int)reader.FrameCount);
            int end = checked((int)Math.Ceiling((double)(cutoff - Epoch) * 48000 / AudioClock.UnitsPerSecond));
            Need(pcm.Take(end).All(v => Math.Abs(v - .25f * M2TimelineRenderer.SourceGain) < .001f) && pcm.Skip(end).All(v => v == 0),
                "Whole-packet mapping across chunks lost/duplicated audio or crossed MicOff.");
        }
        Need(originalHashes.All(p => Hash(p.Key) == p.Value), "Recovery or cross-chunk mix modified originals.");
    }

    private static void TinyOffGap(string root)
    {
        using (var storage = new M1Storage(root, Epoch, DateTimeOffset.UtcNow, "合成明确关mic间隙", true))
        {
            storage.AddSegment("mic-1", "mic", new WaveFormat(16000, 16, 1));
            storage.WritePacket("mic-1", Samples(160), Packet(0, 0, 160));
            storage.WriteBoundary("mic-1", "off", "mic_off", Epoch + 100_000, 160, 0, null);
            storage.AddSegment("mic-2", "mic", new WaveFormat(16000, 16, 1));
            storage.WritePacket("mic-2", Samples(160), Packet(0, 100_625, 160));
            storage.WriteBoundary("mic-2", "stop", "stop", Epoch + 200_625, 160, 0, null);
            storage.Finish(201_000, true, []);
        }
        var read = M1Recovery.Read(root);
        Need(read.IsCleanEnd && read.Capture.Tracks.Select(t => t.SegmentId).SequenceEqual(["mic-1", "mic-2"]),
            "Disk-only recovery lost segment identities.");
        string destination = Path.Combine(root, "mixed.wav");
        M2TimelineRenderer.Render(read.Capture, destination, CancellationToken.None);
        using var reader = new M2WaveReader(destination);
        Need(reader.ReadFrames(480, 3).All(v => v == 0), "Quantization interpolation filled an explicit MicOff interval.");
        Need(Math.Abs(reader.ReadFrames(479, 1)[0] - .25f * M2TimelineRenderer.SourceGain) < .001f && Math.Abs(reader.ReadFrames(483, 1)[0] - .25f * M2TimelineRenderer.SourceGain) < .001f,
            "Tiny off gap moved the saved microphone audio.");
    }

    private static void Safety(string root)
    {
        var first = Packet(0, 0, 160); var next = Packet(1, 92_400, 160);
        Need(MicrophonePacketClock.BoundedJitter100ns("mic", 16000, first, next) == -7600, "Fixture is not bounded jitter.");
        foreach (var invalid in new[] { next with { DevicePositionValid = false }, next with { DevicePosition = 159 },
            next with { DevicePosition = 161 }, next with { Flags = 1 }, next with { Flags = 4 },
            next with { QpcPosition100ns = Epoch + 70_000 }, next with { QpcPosition100ns = Epoch } })
            Need(MicrophonePacketClock.BoundedJitter100ns("mic", 16000, first, invalid) == 0, "Invalid device/QPC clocks were classified as jitter.");
        Need(MicrophonePacketClock.BoundedJitter100ns("remote", 16000, first, next) == 0, "Mic policy changed process-loopback scope.");
        using (var store = new M1Storage(root, Epoch, DateTimeOffset.UtcNow, "合成时钟篡改检查", true))
        {
            store.AddSegment("mic-1", "mic", new WaveFormat(16000, 16, 1));
            store.WritePacket("mic-1", Samples(160), first);
            Reject(() => store.WritePacket("mic-1", Samples(160), next with { DevicePositionValid = false }));
            Reject(() => store.WritePacket("mic-1", Samples(160), next with { QpcPosition100ns = Epoch + 70_000 }));
            store.WritePacket("mic-1", Samples(160), next);
            store.WriteBoundary("mic-1", "stop", "stop", Epoch + 200_000, 320, 0, null);
            store.Finish(200_000, true, []);
        }
        string index = Path.Combine(root, "journal", "capture-index.jsonl");
        string[] original = File.ReadAllLines(index);
        foreach (string mutation in new[] { "missing-marker", "wrong-marker", "device-discontinuous" })
        {
            string[] lines = original.ToArray();
            int at = Array.FindIndex(lines, line => line.Contains("\"clockJitter100ns\":-7600"));
            var envelope = JsonNode.Parse(lines[at])!.AsObject(); var data = envelope["data"]!.AsObject();
            if (mutation == "missing-marker") data.Remove("clockJitter100ns");
            else if (mutation == "wrong-marker") data["clockJitter100ns"] = -7599;
            else data["deviceFramePosition"] = "161";
            byte[] encoded = Encoding.UTF8.GetBytes(data.ToJsonString());
            envelope["sha256"] = Convert.ToHexStringLower(SHA256.HashData(encoded));
            lines[at] = envelope.ToJsonString(); File.WriteAllLines(index, lines, new UTF8Encoding(false));
            var recovered = M1Recovery.Read(root);
            Need(!recovered.IsCleanEnd && recovered.Issues.Count != 0, "Recovery trusted forged/undeclared jitter: " + mutation);
        }
        File.WriteAllLines(index, original, new UTF8Encoding(false));
        Need(M1Recovery.Read(root).IsCleanEnd, "Restored original no longer recovers.");
    }

    private static CapturedPacket Packet(int i, long offset, int frames) => new(i * 160L, frames, 160,
        (ulong)(i * 160), Epoch + (ulong)offset, 0, true, true);
    private static byte[] Samples(int frames)
    {
        var bytes = new byte[frames * 2];
        for (int i = 0; i < frames; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), 8192);
        return bytes;
    }
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Invalid packet was not rejected."); }
}
