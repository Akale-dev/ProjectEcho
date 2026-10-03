using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Export;

internal static class EndpointPacketClockTests
{
    private const ulong Epoch = 10_000_000_000;

    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "endpoint-packet-clock");
        foreach (int rate in new[] { 16000, 44100, 48000 })
        {
            int sampleRate = rate;
            run($"T22/T25/T52-SYNTH endpoint {rate} Hz 20ms packets preserve bounded device-contiguous QPC jitter and Stop tail",
                () => Timeline(Path.Combine(root, "timeline-" + sampleRate), sampleRate));
            run($"T25/T53-SYNTH endpoint {rate} Hz whole-packet jitter mapping survives chunk split and recovery copy",
                () => ChunkSplit(Path.Combine(root, "split-" + sampleRate), sampleRate));
        }
        run("T22/T53-SYNTH endpoint clock rule refuses QPC-only, discontinuity, counter gaps and excessive jitter",
            () => Safety(Path.Combine(root, "safety")));
        run("T22/T25-SYNTH endpoint actual device gaps and Pause intervals remain silent",
            () => Gaps(Path.Combine(root, "gaps")));
        run("T53-SYNTH endpoint disk recovery rejects missing, forged and unsupported jitter declarations",
            () => Forgery(Path.Combine(root, "forgery")));
    }

    private static void Timeline(string folder, int rate)
    {
        int raw = rate / 50; // Synthetic 20 ms packets: 320 / 882 / 960 frames.
        long[] qpcs = [0, 207600, 400000];
        const long cutoff = 500000;
        using (var storage = Store(folder, rate))
        {
            for (int i = 0; i < qpcs.Length; i++)
            {
                int keep = i == qpcs.Length - 1 ? raw / 2 : raw;
                var packet = Packet(i * (long)raw, keep, raw, (ulong)(i * raw), qpcs[i]);
                Need(AudioClock.CompleteFramesBefore(packet.QpcPosition100ns, Epoch + cutoff, rate, raw) == keep,
                    "Endpoint fixture does not obey the original complete-frame cutoff.");
                storage.WritePacket("remote-1", Samples(keep), packet);
            }
            End(storage, cutoff, (ulong)(raw * 2 + raw / 2), raw / 2);
        }
        var recovered = M1Recovery.Read(folder);
        Need(recovered.IsCleanEnd && recovered.Capture.Complete, "Device-contiguous endpoint jitter failed disk-only reconstruction.");
        var track = recovered.Capture.Tracks.Single();
        Need(track.FrameCount == raw * 2 + raw / 2, "Pre-Stop endpoint frames were lost or forbidden tail persisted.");
        Need(track.Packets.Select(p => p.QpcPosition100ns).SequenceEqual(qpcs.Select(qpc => Epoch + (ulong)qpc)) &&
            track.Packets.Select(p => p.DevicePosition).SequenceEqual(new[] { 0UL, (ulong)raw, (ulong)(raw * 2) }),
            "Raw endpoint API clocks were rewritten.");
        Need(track.Packets.Select(p => p.ClockJitter100ns).SequenceEqual(new long[] { 0, 7600, -7600 }),
            "Signed endpoint clock deviations were not persisted.");
        var pcm = Render(folder, recovered.Capture, "timeline.wav");
        Need(pcm.Take(2400).All(Signal) && pcm.Skip(2400).All(value => value == 0),
            "Endpoint jitter produced a hole, duplicate mix, lost tail or post-Stop sound.");
    }

    private static void ChunkSplit(string folder, int rate)
    {
        int raw = rate / 50;
        long waveOffset = 0;
        ulong splitQpc;
        long cutoff;
        using (var storage = Store(folder, rate))
        {
            Write(1, Epoch);
            for (int i = 0; i < 1499; i++) Write(raw, At(waveOffset));
            Need(waveOffset == 30L * rate - raw + 1, "Endpoint fixture did not reach the intended one-frame split.");
            splitQpc = At(waveOffset);
            Write(raw, splitQpc);
            // Exercise negative jitter on 16/44.1 kHz: mapping only the last
            // one-frame fragment would reverse it. 48 kHz covers positive jitter.
            ulong next = splitQpc + (rate == 48000 ? 207600UL : 192400UL);
            Write(raw, next);
            cutoff = checked((long)(next - Epoch)) + 200000;
            End(storage, cutoff, (ulong)waveOffset);
            void Write(int count, ulong qpc)
            {
                storage.WritePacket("remote-1", Samples(count), new(waveOffset, count, count, (ulong)waveOffset, qpc, 0, true, true));
                waveOffset += count;
            }
            ulong At(long frame) => Epoch + checked((ulong)((Int128)frame * AudioClock.UnitsPerSecond / rate));
        }
        var original = M1Recovery.Read(folder);
        Need(original.IsCleanEnd && original.Capture.Complete, "Endpoint chunk-split journal did not recover.");
        var fragments = original.Capture.Tracks.SelectMany(t => t.Packets).Where(p => p.QpcPosition100ns == splitQpc).ToArray();
        Need(fragments.Select(p => p.FrameCount).SequenceEqual(new[] { raw - 1, 1 }) &&
            fragments.Select(p => p.RetainedFrameOffset).SequenceEqual(new[] { 0, raw - 1 }), "Expected endpoint raw packet fragments are missing.");
        var copy = M1Recovery.RecoverTo(folder, folder + "-copy");
        foreach (var (capture, name) in new[] { (original.Capture, "original.wav"), (copy, "copy.wav") })
        {
            var pcm = Render(folder, capture, name);
            int end = checked((int)(((Int128)cutoff * 48000 + AudioClock.UnitsPerSecond - 1) / AudioClock.UnitsPerSecond));
            Need(pcm.Take(end).All(Signal) && pcm.Skip(end).All(value => value == 0),
                "Whole-packet endpoint mapping failed across chunk boundaries or recovery copy.");
        }
    }

    private static void Safety(string folder)
    {
        var first = Packet(0, 320, 320, 0, 0);
        var next = Packet(320, 320, 320, 320, 192400);
        Need(DevicePacketClock.BoundedJitter100ns("remote", 16000, first, next) == -7600, "Fixture is not a bounded endpoint overlap.");
        Need(MicrophonePacketClock.BoundedJitter100ns("remote", 16000, first, next) == 0, "Legacy microphone-only API changed meaning.");
        foreach (var invalid in new[]
        {
            next with { DevicePositionValid = false }, next with { DevicePosition = 0, DevicePositionValid = false },
            next with { DevicePosition = 319 }, next with { DevicePosition = 321 },
            next with { Flags = 1 }, next with { Flags = 4, TimestampValid = false },
            next with { QpcPosition100ns = Epoch }, next with { QpcPosition100ns = Epoch + 179999 }
        })
            Need(DevicePacketClock.BoundedJitter100ns("remote", 16000, first, invalid) == 0, "Unsafe endpoint clock evidence was classified as jitter.");
        Need(DevicePacketClock.BoundedJitter100ns("remote", 16000, first with { DevicePositionValid = false }, next) == 0 &&
            DevicePacketClock.BoundedJitter100ns("remote", 16000, first with { FrameCount = 319 }, next) == 0,
            "Missing device evidence or a prior cutoff was ignored.");
        Need(DevicePacketClock.BoundedJitter100ns("remote", 16000,
            Packet(0, 64, 64, 0, 0), Packet(64, 64, 64, 64, 25000)) == 0, "Jitter exceeded 25% of a short raw packet.");
        using var storage = Store(folder, 16000);
        storage.WritePacket("remote-1", Samples(320), first);
        var failure = Reject(() => storage.WritePacket("remote-1", Samples(320), next with { DevicePositionValid = false }));
        Need(failure.Message.Contains("rate=16000") && failure.Message.Contains("rawFrames=320->320") &&
            failure.Message.Contains("qpcDelta100ns=192400") && failure.Message.Contains("nominalDeviation100ns=-7600") &&
            failure.Message.Contains("deviceDeltaFrames=320") && failure.Message.Contains("deviceValid=True->False") &&
            failure.Message.Contains("flags=0->0") && !failure.Message.Contains(Epoch.ToString()),
            "Rejected-packet diagnostics lost required relative numeric evidence or exposed absolute clock.");
        Reject(() => storage.WritePacket("remote-1", Samples(320), next with { Flags = 1 }));
        Reject(() => storage.WritePacket("remote-1", Samples(320), next with { QpcPosition100ns = Epoch + 179999 }));
        storage.WritePacket("remote-1", Samples(320), next);
        End(storage, 400000, 640);
    }

    private static void Gaps(string folder)
    {
        using (var storage = Store(folder, 16000))
        {
            storage.WritePacket("remote-1", Samples(320), Packet(0, 320, 320, 0, 0));
            storage.WritePacket("remote-1", Samples(320), Packet(320, 320, 320, 322, 201250));
            storage.WriteBoundary("remote-1", "pause", "pause", Epoch + 401250, 642, 0, null);
            storage.AddSegment("remote-2", "remote", new WaveFormat(16000, 16, 1));
            storage.WritePacket("remote-2", Samples(160), Packet(0, 160, 160, 0, 401875));
            storage.WriteBoundary("remote-2", "stop", "stop", Epoch + 501875, 160, 0, null);
            storage.Finish(601875, true, []);
        }
        var read = M1Recovery.Read(folder);
        Need(read.IsCleanEnd && !read.Capture.Complete, "True endpoint device gap was relabelled complete.");
        var pcm = Render(folder, read.Capture, "gaps.wav");
        Need(pcm.Skip(960).Take(6).All(value => value == 0) && Signal(pcm[959]) && Signal(pcm[966]),
            "Endpoint device gap was interpolated away or adjacent valid samples were lost.");
        Need(pcm.Skip(1926).Take(3).All(value => value == 0) && Signal(pcm[1925]) && Signal(pcm[1929]),
            "Explicit Pause interval was filled by endpoint clock interpolation.");
    }

    private static void Forgery(string folder)
    {
        using (var storage = Store(folder, 44100))
        {
            storage.WritePacket("remote-1", Samples(882), Packet(0, 882, 882, 0, 0));
            storage.WritePacket("remote-1", Samples(882), Packet(882, 882, 882, 882, 192400));
            End(storage, 400000, 1764);
        }
        string index = Path.Combine(folder, "journal", "capture-index.jsonl");
        string[] original = File.ReadAllLines(index);
        foreach (string mutation in new[] { "missing", "wrong", "device-gap", "qpc-only" })
        {
            string[] lines = original.ToArray();
            int at = Array.FindIndex(lines, line => line.Contains("\"clockJitter100ns\":-7600"));
            var envelope = JsonNode.Parse(lines[at])!.AsObject();
            var data = envelope["data"]!.AsObject();
            if (mutation == "missing") data.Remove("clockJitter100ns");
            else if (mutation == "wrong") data["clockJitter100ns"] = -7599;
            else if (mutation == "device-gap") data["deviceFramePosition"] = "883";
            else { data["devicePositionValid"] = false; data["timestampQuality"] = "qpc_referenced"; }
            byte[] encoded = Encoding.UTF8.GetBytes(data.ToJsonString());
            envelope["sha256"] = Convert.ToHexStringLower(SHA256.HashData(encoded));
            lines[at] = envelope.ToJsonString();
            File.WriteAllLines(index, lines, new UTF8Encoding(false));
            var read = M1Recovery.Read(folder);
            Need(!read.IsCleanEnd && read.Issues.Count > 0, "Recovery accepted invalid endpoint jitter evidence: " + mutation);
        }
        File.WriteAllLines(index, original, new UTF8Encoding(false));
        Need(M1Recovery.Read(folder).IsCleanEnd, "Original endpoint journal failed after restoring forged test copies.");
    }

    private static M1Storage Store(string folder, int rate)
    {
        var storage = new M1Storage(folder, Epoch, DateTimeOffset.UtcNow, "合成端点时钟测试", true);
        storage.AddSegment("remote-1", "remote", new WaveFormat(rate, 16, 1));
        return storage;
    }
    private static void End(M1Storage storage, long cutoff, ulong forbidden, long discarded = 0)
    {
        storage.WriteBoundary("remote-1", "stop", "stop", Epoch + (ulong)cutoff, forbidden, discarded, discarded > 0 ? "after_boundary" : null);
        var result = storage.Finish(cutoff + 100000, true, []);
        Need(result.StorageComplete && result.Warnings.Any(w => w.Contains("小幅抖动")), "Endpoint jitter warning or complete storage result missing.");
    }
    private static CapturedPacket Packet(long waveOffset, int count, int raw, ulong device, long qpc) =>
        new(waveOffset, count, raw, device, Epoch + (ulong)qpc, 0, true, true);
    private static byte[] Samples(int count)
    {
        var bytes = new byte[count * 2];
        for (int i = 0; i < count; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), 8192);
        return bytes;
    }
    private static float[] Render(string folder, CaptureResult capture, string name)
    {
        var hashes = capture.Tracks.ToDictionary(t => t.WavePath, t => Hash(t.WavePath));
        string path = Path.Combine(folder, name);
        M2TimelineRenderer.Render(capture, path, CancellationToken.None);
        using var reader = new M2WaveReader(path);
        Need(reader.FrameCount == M2TimelineRenderer.FramesForDuration(capture.Duration100ns), "Endpoint mix lost full elapsed time.");
        Need(hashes.All(item => item.Value == Hash(item.Key)), "Endpoint mixing modified an original.");
        return reader.ReadFrames(0, checked((int)reader.FrameCount));
    }
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static bool Signal(float value) => Math.Abs(value - .25f * M2TimelineRenderer.SourceGain) < .001f;
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static InvalidDataException Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException ex) { return ex; }
        throw new InvalidOperationException("Invalid endpoint clock was accepted.");
    }
}
