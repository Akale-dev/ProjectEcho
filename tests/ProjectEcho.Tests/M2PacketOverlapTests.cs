using System.Buffers.Binary;
using System.Security.Cryptography;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Export;

internal static class M2PacketOverlapTests
{
    private const ulong Epoch = 20_000_000_000;

    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m2-packet-overlap");
        foreach (int rate in new[] { 16000, 44100, 48000 })
        {
            int sampleRate = rate;
            run($"T22/T25/T53-SYNTH {rate} Hz stored one-frame negative quantization remains renderable at discontinuity",
                () => Quantization(Path.Combine(root, "quantization-" + sampleRate), sampleRate));
        }
        run("T25/T51/T52/T53-SYNTH negative quantization across a raw-packet chunk split preserves gaps and mic-off tail",
            () => ChunkAndBoundaries(Path.Combine(root, "chunk-boundaries")));
        run("T22/T25-SYNTH quantization fallback rejects excessive overlap, backward device frames and cross-segment overlap",
            () => Safety(Path.Combine(root, "safety")));
    }

    private static void Quantization(string root, int rate)
    {
        int raw = rate / 50;
        long quantum = AudioClock.UnitsPerSecond / rate;
        foreach (string source in new[] { "remote", "mic" })
        foreach (string kind in new[] { "discontinuity", "declared-device-gap", "validity-loss", "validity-gain" })
        {
            string folder = Path.Combine(root, source + "-" + kind);
            bool firstValid = kind != "validity-gain", nextValid = kind != "validity-loss";
            int flags = kind is "discontinuity" or "declared-device-gap" ? 1 : 0;
            ulong device = (ulong)raw + (kind == "declared-device-gap" ? 2UL : 0);
            long nextQpc = 200000 - quantum, cutoff = nextQpc + 200000;
            using (var storage = Store(folder, source, rate))
            {
                storage.WritePacket(source + "-1", Samples(raw), Packet(0, raw, raw, 0, 0) with { DevicePositionValid = firstValid });
                storage.WritePacket(source + "-1", Samples(raw), Packet(raw, raw, raw, device, nextQpc, flags) with { DevicePositionValid = nextValid });
                storage.WriteBoundary(source + "-1", "stop", "stop", Epoch + (ulong)cutoff,
                    nextValid ? device + (ulong)raw : null, 0, null);
                Need(storage.Finish(cutoff + 100000, true, []).StorageComplete, "Writer did not accept the documented one-frame tolerance.");
            }
            var read = M1Recovery.Read(folder);
            Need(read.IsCleanEnd && read.Issues.Count == 0 && !read.Capture.Complete,
                "Storage/recovery fixture did not retain discontinuity or limited clock evidence.");
            Need(read.Capture.Tracks.SelectMany(t => t.Packets).All(p => p.ClockJitter100ns == 0),
                "One-frame quantization unexpectedly requires a bounded-jitter declaration.");
            var pcm = Render(folder, read.Capture);
            int end = checked((int)(((Int128)cutoff * 48000 + AudioClock.UnitsPerSecond - 1) / AudioClock.UnitsPerSecond));
            Need(pcm.Take(end).All(Signal) && pcm.Skip(end).All(v => v == 0),
                "Accepted negative quantization duplicated PCM, left a hole or lost the end.");
        }
    }

    private static void ChunkAndBoundaries(string folder)
    {
        const int rate = 16000, raw = 320;
        long frame = 0, splitStart = 0;
        using (var storage = Store(folder, "mic", rate))
        {
            Write(1);
            for (int i = 0; i < 1499; i++) Write(raw);
            splitStart = frame * 625;
            Write(raw); // A 319+1 fragment split precedes a negative one-frame offset.
            long nextQpc = splitStart + 200000 - 625;
            storage.WritePacket("mic-1", Samples(raw), Packet(frame, raw, raw, (ulong)frame + 2, nextQpc, 1));
            frame += raw;
            long gapQpc = nextQpc + 200000 + 1250;
            storage.WritePacket("mic-1", Samples(160), Packet(frame, 160, raw, (ulong)frame + 4, gapQpc, 1));
            long cutoff = gapQpc + 100000;
            storage.WriteBoundary("mic-1", "off", "mic_off", Epoch + (ulong)cutoff, (ulong)frame + 164, 160, "after_boundary");
            storage.AddSegment("mic-2", "mic", new WaveFormat(rate, 16, 1));
            storage.WritePacket("mic-2", Samples(160), Packet(0, 160, 160, 0, cutoff + 625));
            storage.WriteBoundary("mic-2", "stop", "stop", Epoch + (ulong)(cutoff + 100625), 160, 0, null);
            storage.Finish(cutoff + 200625, true, []);
            void Write(int count)
            {
                storage.WritePacket("mic-1", Samples(count), Packet(frame, count, count, (ulong)frame, frame * 625));
                frame += count;
            }
        }
        var read = M1Recovery.Read(folder);
        Need(read.IsCleanEnd && !read.Capture.Complete, "Gap/stop fixture did not recover with original integrity warnings.");
        var fragments = read.Capture.Tracks.SelectMany(t => t.Packets).Where(p => p.QpcPosition100ns == Epoch + (ulong)splitStart).ToArray();
        Need(fragments.Select(p => p.FrameCount).SequenceEqual(new[] { 319, 1 }), "Raw packet did not straddle the disk chunk.");
        var recoveredCopy = M1Recovery.RecoverTo(folder, folder + "-copy");
        foreach (var (capture, destination) in new[] { (read.Capture, folder), (recoveredCopy, folder + "-copy") })
        {
            var pcm = Render(destination, capture);
            int gapStart = checked((int)((splitStart + 399375) * 48000 / AudioClock.UnitsPerSecond));
            Need(pcm.Take(gapStart).All(Signal) && pcm.Skip(gapStart).Take(6).All(v => v == 0),
                "Chunk mapping reversed a fragment or filled the measured device gap.");
            int micOffStart = gapStart + 6 + 480;
            Need(pcm.Skip(gapStart + 6).Take(480).All(Signal) && pcm.Skip(micOffStart).Take(3).All(v => v == 0),
                "Pre-cutoff retained tail was lost or mic-off interval filled.");
            Need(pcm.Skip(micOffStart + 3).Take(480).All(Signal) && pcm.Skip(micOffStart + 483).All(v => v == 0),
                "New mic segment was bridged or PCM persisted past the final cutoff.");
        }
    }

    private static void Safety(string folder)
    {
        const int raw = 320;
        using (var storage = Store(folder, "remote", 16000))
        {
            storage.WritePacket("remote-1", Samples(raw), Packet(0, raw, raw, 0, 0));
            Reject(() => storage.WritePacket("remote-1", Samples(raw), Packet(raw, raw, raw, raw, 199374, 1)));
            storage.WritePacket("remote-1", Samples(raw), Packet(raw, raw, raw, raw, 199375, 1));
            storage.WriteBoundary("remote-1", "stop", "stop", Epoch + 399375, 640, 0, null);
            storage.Finish(500000, true, []);
        }
        var read = M1Recovery.Read(folder);
        Need(read.IsCleanEnd, "Safety base fixture did not recover.");
        var track = read.Capture.Tracks.Single(); var packet = track.Packets[1];
        foreach (var invalid in new[]
        {
            packet with { QpcPosition100ns = Epoch + 199374 }, // One 100 ns unit beyond one frame.
            packet with { DevicePosition = 319 }
        })
        {
            var altered = read.Capture with { Tracks = [track with { Packets = [track.Packets[0], invalid] }] };
            Reject(() => M2TimelineRenderer.Render(altered, Path.Combine(folder, "invalid-" + invalid.DevicePosition + "-" + invalid.QpcPosition100ns + ".wav"), CancellationToken.None));
        }
        // A source cannot use the tolerance to bridge explicitly separate segments.
        var crossSegment = read.Capture with
        {
            Tracks = [track with { FrameCount = raw, Packets = [track.Packets[0]], SegmentId = "remote-a" },
                track with { FrameCount = raw, Packets = [packet with { WaveFrameOffset = 0 }], SegmentId = "remote-b" }]
        };
        Reject(() => M2TimelineRenderer.Render(crossSegment, Path.Combine(folder, "cross-segment.wav"), CancellationToken.None));
    }

    private static M1Storage Store(string folder, string source, int rate)
    {
        var storage = new M1Storage(folder, Epoch, DateTimeOffset.UtcNow, "合成负量化重叠", true);
        storage.AddSegment(source + "-1", source, new WaveFormat(rate, 16, 1));
        return storage;
    }
    private static CapturedPacket Packet(long offset, int kept, int raw, ulong device, long qpc, int flags = 0) =>
        new(offset, kept, raw, device, Epoch + (ulong)qpc, flags, true, true);
    private static byte[] Samples(int frames)
    {
        var bytes = new byte[frames * 2];
        for (int i = 0; i < frames; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), 8192);
        return bytes;
    }
    private static float[] Render(string folder, CaptureResult capture)
    {
        var originalHashes = capture.Tracks.Select(t => t.WavePath).Append(capture.IndexPath)
            .ToDictionary(path => path, Hash);
        string path = Path.Combine(folder, "overlap-mix.wav");
        M2TimelineRenderer.Render(capture, path, CancellationToken.None);
        using var reader = new M2WaveReader(path);
        Need(reader.FrameCount == M2TimelineRenderer.FramesForDuration(capture.Duration100ns), "Mix lost full elapsed time.");
        Need(originalHashes.All(pair => Hash(pair.Key) == pair.Value), "Mix changed authoritative PCM or clock journal.");
        return reader.ReadFrames(0, checked((int)reader.FrameCount));
    }
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static bool Signal(float value) => Math.Abs(value - .25f * M2TimelineRenderer.SourceGain) < .001f;
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Unsafe overlap was accepted.");
    }
}
