using System.Buffers.Binary;
using System.Security.Cryptography;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Export;

internal static class M3cMicrophoneGapRenderTests
{
    private const ulong Epoch = 10_000_000_000;
    private const int Rate = 16000;

    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3c-mic-gap-render");
        run("T22/T25-SYNTH mic two-device-frame loss remains silence in the listening timeline", () => DeviceGap(Path.Combine(root, "device-gap")));
        run("T22/T25-SYNTH mic discontinuity is not smoothed by subpacket QPC quantization", () => Discontinuity(Path.Combine(root, "discontinuity")));
        run("T25-SYNTH mic actual device-contiguous quantization and remote QPC-only mapping remain supported", () => Quantization(Path.Combine(root, "quantization")));
        run("T25/T53-SYNTH mic real gap after a 159+1 raw packet chunk split is not filled", () => ChunkSplit(Path.Combine(root, "chunk-split")));
        run("T51/T52-SYNTH mic gap rendering preserves full pre-cutoff frames and no post-boundary sound", () => Cutoffs(Path.Combine(root, "cutoffs")));
    }

    private static void DeviceGap(string folder)
    {
        using (var storage = Store(folder))
        {
            storage.WritePacket("mic-1", Samples(160), Packet(0, 160, 160, 0, 0));
            storage.WritePacket("mic-1", Samples(160), Packet(160, 160, 160, 162, 101250));
            End(storage, 201250, 322);
        }
        var read = M1Recovery.Read(folder);
        Need(read.IsCleanEnd && !read.Capture.Complete && read.Issues.Count == 0, "真实设备缺口未从磁盘保留为不完整捕获。");
        var pcm = RenderAndCheckOriginals(folder, read.Capture);
        Need(pcm.Skip(480).Take(6).All(value => value == 0), "2帧真实mic缺口被量化插值填掉。");
        Need(Signal(pcm[479]) && Signal(pcm[486]), "缺口处理损失了相邻合法PCM。");
    }

    private static void Discontinuity(string folder)
    {
        foreach (bool deviceGap in new[] { false, true })
        {
            string current = folder + (deviceGap ? "-device-gap" : "-continuous-counter");
            // B declares a discontinuity before B. The following C is device
            // contiguous with B and has a one-frame negative QPC rounding offset.
            // Preserve A->B's gap, while B->C must still map without overlap.
            ulong secondDevice = deviceGap ? 162UL : 160UL;
            using (var storage = Store(current))
            {
                storage.WritePacket("mic-1", Samples(160), Packet(0, 160, 160, 0, 0));
                storage.WritePacket("mic-1", Samples(160), Packet(160, 160, 160, secondDevice, 101250, 1));
                storage.WritePacket("mic-1", Samples(160), Packet(320, 160, 160, secondDevice + 160, 200625));
                End(storage, 300625, secondDevice + 320);
            }
            var read = M1Recovery.Read(current); Need(read.IsCleanEnd && !read.Capture.Complete, "不连续标志在磁盘恢复时丢失。");
            var pcm = RenderAndCheckOriginals(current, read.Capture);
            Need(pcm.Skip(480).Take(6).All(value => value == 0), "B前的discontinuity被当作连续量化误差填平。");
            Need(pcm.Take(480).All(Signal) && pcm.Skip(486).Take(957).All(Signal) && pcm.Skip(1443).All(value => value == 0),
                "B前的断续错误阻止了B到C的连续设备帧量化映射，或损失合法样本。");
        }
    }

    private static void Quantization(string folder)
    {
        foreach (string source in new[] { "mic", "remote" })
        {
            string current = folder + "-" + source;
            using (var storage = Store(current, source))
            {
                storage.WritePacket(source + "-1", Samples(160), Packet(0, 160, 160, 0, 0) with { DevicePositionValid = source == "mic" });
                storage.WritePacket(source + "-1", Samples(160), Packet(160, 160, 160, source == "mic" ? 160UL : 0, 100625) with { DevicePositionValid = source == "mic" });
                storage.WriteBoundary(source + "-1", "stop", "stop", Epoch + 200625, source == "mic" ? 320UL : null, 0, null);
                storage.Finish(300625, true, []);
            }
            var read = M1Recovery.Read(current); Need(read.IsCleanEnd, "连续量化夹具无法恢复。");
            var pcm = RenderAndCheckOriginals(current, read.Capture);
            Need(pcm.Take(963).All(Signal) && pcm.Skip(963).All(value => value == 0), "合法连续量化或remote QPC-only既有映射被破坏。");
        }
        string firstFlag = folder + "-initial-discontinuity";
        using (var storage = Store(firstFlag))
        {
            storage.WritePacket("mic-1", Samples(160), Packet(0, 160, 160, 0, 0, 1));
            storage.WritePacket("mic-1", Samples(160), Packet(160, 160, 160, 160, 99375));
            End(storage, 199375, 320);
        }
        var initial = M1Recovery.Read(firstFlag); Need(initial.IsCleanEnd, "首包discontinuity夹具恢复失败。");
        var initialPcm = RenderAndCheckOriginals(firstFlag, initial.Capture);
        Need(initialPcm.Take(957).All(Signal) && initialPcm.Skip(957).All(value => value == 0),
            "首包之前的discontinuity误阻止首包到后包的实际设备连续负一帧量化映射。");
    }

    private static void ChunkSplit(string folder)
    {
        long waveOffset = 0; ulong rawStart = 0;
        using (var storage = Store(folder))
        {
            Write(1);
            for (int i = 0; i < 2999; i++) Write(160);
            Need(waveOffset == 479841, "跨块夹具位置错误。");
            rawStart = (ulong)waveOffset * 625;
            Write(160); // One original packet is persisted as 159 + 1 frames.
            storage.WritePacket("mic-1", Samples(160), Packet(waveOffset, 160, 160, (ulong)waveOffset + 2, (long)rawStart + 101250));
            End(storage, (long)rawStart + 201250, (ulong)waveOffset + 162);
            void Write(int count)
            {
                storage.WritePacket("mic-1", Samples(count), Packet(waveOffset, count, count, (ulong)waveOffset, waveOffset * 625));
                waveOffset += count;
            }
        }
        var read = M1Recovery.Read(folder); Need(read.IsCleanEnd, "真实gap跨块索引恢复失败。");
        var fragments = read.Capture.Tracks.SelectMany(track => track.Packets).Where(packet => packet.QpcPosition100ns == Epoch + rawStart).ToArray();
        Need(fragments.Select(packet => packet.FrameCount).SequenceEqual([159, 1]) && fragments.Select(packet => packet.RetainedFrameOffset).SequenceEqual([0, 159]), "同一原包未正确跨块。");
        var pcm = RenderAndCheckOriginals(folder, read.Capture);
        const int gapStart = 480001 * 3;
        Need(pcm.Take(gapStart).All(Signal) && pcm.Skip(gapStart).Take(6).All(value => value == 0) && Signal(pcm[gapStart + 6]),
            "同一原包跨块被误当缺口，或其后真实2帧缺口被补平。");
    }

    private static void Cutoffs(string folder)
    {
        foreach (long cutoff in new long[] { 50000, 100625, 101250, 151250 })
        {
            string current = folder + "-" + cutoff;
            using (var storage = Store(current))
            {
                int first = AudioClock.CompleteFramesBefore(Epoch, Epoch + (ulong)cutoff, Rate, 160);
                storage.WritePacket("mic-1", Samples(first), Packet(0, first, 160, 0, 0));
                int next = AudioClock.CompleteFramesBefore(Epoch + 101250, Epoch + (ulong)cutoff, Rate, 160);
                if (next > 0) storage.WritePacket("mic-1", Samples(next), Packet(first, next, 160, 162, 101250));
                ulong deviceBoundary = next > 0 ? 162UL + (ulong)next : (ulong)first;
                End(storage, cutoff, deviceBoundary, 160 - first + (cutoff >= 101250 ? 160 - next : 0));
            }
            var read = M1Recovery.Read(current); Need(read.IsCleanEnd, "停采边界夹具恢复失败。");
            var pcm = RenderAndCheckOriginals(current, read.Capture);
            int cutoffOutputFrame = (int)(cutoff * 48000 / AudioClock.UnitsPerSecond);
            Need(pcm.Skip(cutoffOutputFrame).All(value => value == 0), "混音出现真实cutoff后的mic PCM。");
            int firstFrames = Math.Min(160, (int)(cutoff / 625)) * 3;
            Need(pcm.Take(firstFrames).All(Signal), "截止前完整首包帧丢失。");
            if (cutoff >= 101250) Need(pcm.Skip(480).Take(6).All(value => value == 0), "停采包前真实gap被填平。");
            if (cutoff == 151250) Need(pcm.Skip(486).Take(240).All(Signal), "跨边界尾包的有效80帧没有完整保留。");
        }
    }

    private static M1Storage Store(string folder, string source = "mic")
    {
        var storage = new M1Storage(folder, Epoch, DateTimeOffset.UtcNow, "合成设备缺帧混音边界", true);
        storage.AddSegment(source + "-1", source, new WaveFormat(Rate, 16, 1));
        return storage;
    }

    private static void End(M1Storage storage, long cutoff, ulong deviceBoundary, long discarded = 0)
    {
        storage.WriteBoundary("mic-1", "off", "mic_off", Epoch + (ulong)cutoff, deviceBoundary, discarded, discarded > 0 ? "after_boundary" : null);
        storage.Finish(cutoff + 100000, true, []);
    }

    private static CapturedPacket Packet(long waveOffset, int kept, int raw, ulong device, long qpc, int flags = 0)
        => new(waveOffset, kept, raw, device, Epoch + (ulong)qpc, flags, true, true);

    private static float[] RenderAndCheckOriginals(string folder, CaptureResult capture)
    {
        var hashes = capture.Tracks.ToDictionary(track => track.WavePath, track => Hash(track.WavePath));
        string destination = Path.Combine(folder, "gap-mix.wav");
        M2TimelineRenderer.Render(capture, destination, CancellationToken.None);
        using var reader = new M2WaveReader(destination);
        Need(reader.FrameCount == M2TimelineRenderer.FramesForDuration(capture.Duration100ns), "混音没有保持完整经过时间。");
        var pcm = reader.ReadFrames(0, checked((int)reader.FrameCount));
        Need(hashes.All(pair => Hash(pair.Key) == pair.Value), "gap混音改变了原PCM。");
        return pcm;
    }

    private static byte[] Samples(int frames)
    {
        var pcm = new byte[frames * 2];
        for (int i = 0; i < frames; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), 8192);
        return pcm;
    }
    private static bool Signal(float value) => Math.Abs(value - .25f * M2TimelineRenderer.SourceGain) < .001f;
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
