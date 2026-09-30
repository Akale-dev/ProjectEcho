using ProjectEcho.Core;

namespace ProjectEcho.Export;

/// <summary>Bounded PCM working set; absolute packet QPC anchors retain gaps and different source endings.</summary>
internal static class M2TimelineRenderer
{
    internal const int SampleRate = 48000;
    internal const float SourceGain = 0.4f;
    internal const float PeakCeiling = 0.8912509f;
    private const int BlockFrames = 8192;

    internal static void Render(CaptureResult capture, string destination, CancellationToken token)
    {
        long frames = FramesForDuration(capture.Duration100ns);
        using var output = new MonoPcmWriter(destination, frames);
        var sourceGroups = capture.Tracks.GroupBy(t => t.SourceId, StringComparer.Ordinal).ToArray();
        if (sourceGroups.Length > 2 || sourceGroups.Any(g => g.Key is not ("remote" or "mic")))
            throw new NotSupportedException("完整混音只接受独立的 remote 与可选 mic 来源。");
        var sources = sourceGroups.Select(group => new SourceCursor(group.ToArray(), capture.EpochQpc100ns, frames)).ToArray();
        try
        {
            var mix = new float[BlockFrames];
            for (long start = 0; start < frames; start += BlockFrames)
            {
                token.ThrowIfCancellationRequested(); int count = (int)Math.Min(BlockFrames, frames - start);
                Array.Clear(mix);
                foreach (var source in sources) source.Add(start, count, mix, token);
                output.Write(mix.AsSpan(0, count));
            }
            output.Finish();
        }
        finally { foreach (var source in sources) source.Dispose(); }
    }

    internal static long FramesForDuration(long duration100ns)
    {
        if (duration100ns <= 0) throw new InvalidDataException("录音时间轴为空。");
        long frames = checked((long)(((Int128)duration100ns * SampleRate + AudioClock.UnitsPerSecond - 1) / AudioClock.UnitsPerSecond));
        // Offline processing is bounded independently of RIFF's 32-bit size.
        // Normal live RecordingSession retains its existing eight-hour limit.
        if (duration100ns > 86400L * AudioClock.UnitsPerSecond || frames > MonoPcmWriter.MaximumFrames)
            throw new NotSupportedException("单次离线混音超过24小时处理上限；原轨保留，未截短音频。");
        return frames;
    }

    private sealed class Span(CapturedTrack track, CapturedPacket packet, double start, double end)
    {
        internal CapturedTrack Track = track;
        internal CapturedPacket Packet = packet;
        internal double Start = start, End = end;
    }

    private sealed class SourceCursor : IDisposable
    {
        private readonly Span[] spans;
        private int cursor;
        private CapturedTrack? currentTrack;
        private M2WaveReader? reader;

        internal SourceCursor(CapturedTrack[] tracks, ulong epoch, long totalFrames)
        {
            var prepared = new List<Span>();
            foreach (var track in tracks)
            {
                track.Format.Validate();
                if (track.Format.Channels > 2) throw new NotSupportedException("尚未验证多于两声道的降混；原轨保持不变。");
                long nextOffset = 0;
                foreach (var packet in track.Packets)
                {
                    if (!packet.TimestampValid || (packet.Flags & 4) != 0 || packet.FrameCount <= 0 ||
                        packet.RetainedFrameOffset < 0 || packet.RetainedFrameOffset + (long)packet.FrameCount > packet.RawFrameCount || packet.WaveFrameOffset != nextOffset)
                        throw new InvalidDataException("原轨 packet 时间或存储范围无效。");
                    nextOffset = checked(nextOffset + packet.FrameCount);
                    double start = (double)((Int128)packet.QpcPosition100ns - epoch) * SampleRate / AudioClock.UnitsPerSecond +
                        (double)packet.RetainedFrameOffset * SampleRate / track.Format.SampleRate;
                    double end = start + (double)packet.FrameCount * SampleRate / track.Format.SampleRate;
                    if (start < -0.0001 || end > totalFrames + 0.01) throw new InvalidDataException("包时间越过已确认完整会话边界。");
                    prepared.Add(new(track, packet, start, end));
                }
                if (nextOffset != track.FrameCount) throw new InvalidDataException("WAV 帧数与索引不一致。");
            }
            spans = prepared.OrderBy(p => p.Start).ToArray();
            for (int i = 0; i + 1 < spans.Length; i++)
            {
                var a = spans[i]; var b = spans[i + 1]; double difference = b.Start - a.End;
                // Two input frames is the M1 accepted QPC rounding tolerance. Re-anchor
                // each contiguous packet to the following API timestamp. Larger gaps
                // remain silence; they are never stretched away by a resampler.
                double tolerance = 2.0 * SampleRate / a.Track.Format.SampleRate + 0.0001;
                if (Math.Abs(difference) <= tolerance && b.Start > a.Start) a.End = b.Start;
                else if (difference < 0) throw new InvalidDataException("同一路来源时间范围重叠；拒绝重复混入 PCM。");
            }
        }

        internal void Add(long blockStart, int count, float[] mix, CancellationToken token)
        {
            long blockEnd = blockStart + count;
            while (cursor < spans.Length && spans[cursor].End <= blockStart) cursor++;
            for (int index = cursor; index < spans.Length && spans[index].Start < blockEnd; index++)
            {
                token.ThrowIfCancellationRequested(); var span = spans[index];
                long first = Math.Max(blockStart, (long)Math.Ceiling(span.Start - 0.00001));
                long end = Math.Min(blockEnd, (long)Math.Ceiling(span.End - 0.00001));
                if (end <= first) continue;
                if (!ReferenceEquals(currentTrack, span.Track))
                {
                    reader?.Dispose(); reader = new M2WaveReader(span.Track.WavePath, span.Track.Format); currentTrack = span.Track;
                    if (reader.FrameCount != span.Track.FrameCount) throw new InvalidDataException("原 WAV 帧数改变。");
                }
                double step = span.Packet.FrameCount / (span.End - span.Start);
                int readFirst = Math.Clamp((int)Math.Floor((first - span.Start) * step), 0, span.Packet.FrameCount - 1);
                int readEnd = Math.Clamp((int)Math.Floor((end - 1 - span.Start) * step) + 2, readFirst + 1, span.Packet.FrameCount);
                var pcm = reader!.ReadFrames(span.Packet.WaveFrameOffset + readFirst, readEnd - readFirst);
                int channels = span.Track.Format.Channels;
                for (long frame = first; frame < end; frame++)
                {
                    double position = Math.Clamp((frame - span.Start) * step - readFirst, 0, readEnd - readFirst - 1);
                    int low = (int)position, high = Math.Min(low + 1, readEnd - readFirst - 1); float fraction = (float)(position - low);
                    float mono = 0;
                    for (int ch = 0; ch < channels; ch++) mono += pcm[low * channels + ch] * (1 - fraction) + pcm[high * channels + ch] * fraction;
                    mix[frame - blockStart] += Math.Clamp(mono / channels, -1f, 1f) * SourceGain;
                }
            }
        }

        public void Dispose() => reader?.Dispose();
    }
}

internal sealed class MonoPcmWriter : IDisposable
{
    internal const long MaximumRiffFrames = (uint.MaxValue - 36L) / 2;
    internal const long MaximumFrames = (long.MaxValue - 80) / 2;
    private readonly FileStream stream;
    private readonly BinaryWriter writer;
    private readonly long expectedFrames;
    private long writtenFrames;

    internal MonoPcmWriter(string path, long frames)
    {
        if (frames <= 0 || frames > MaximumFrames) throw new ArgumentOutOfRangeException(nameof(frames));
        expectedFrames = frames; stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        writer = new(stream, System.Text.Encoding.UTF8, true);
        WriteHeader(writer, frames);
    }

    internal static long HeaderBytes(long frames) => frames <= MaximumRiffFrames ? 44 : 80;
    internal static long FileBytes(long frames) => checked(HeaderBytes(frames) + frames * 2);
    internal static long FramesFittingByteLimit(long bytes)
    {
        if (bytes < 46) return 0;
        long riff = (bytes - 44) / 2;
        return riff <= MaximumRiffFrames ? riff : Math.Max(MaximumRiffFrames, (bytes - 80) / 2);
    }

    // EBU Tech 3306-2009 §3.4/A.2: RF64 uses uint sentinel sizes and a mandatory
    // first ds64 chunk containing riffSize, dataSize, sampleCount and tableLength.
    // Small output remains byte-for-byte the existing 44-byte RIFF header.
    internal static void WriteHeader(BinaryWriter writer, long frames)
    {
        if (frames <= 0 || frames > MaximumFrames || writer.BaseStream.Position != 0) throw new ArgumentOutOfRangeException(nameof(frames));
        long dataBytes = checked(frames * 2); bool rf64 = frames > MaximumRiffFrames;
        writer.Write(rf64 ? 0x34364652u : 0x46464952u);
        writer.Write(rf64 ? uint.MaxValue : (uint)(36 + dataBytes)); writer.Write(0x45564157u);
        if (rf64)
        {
            writer.Write(0x34367364u); writer.Write(28u);
            writer.Write((ulong)(72 + dataBytes)); writer.Write((ulong)dataBytes); writer.Write((ulong)frames); writer.Write(0u);
        }
        writer.Write(0x20746d66u); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(M2TimelineRenderer.SampleRate); writer.Write(M2TimelineRenderer.SampleRate * 2); writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write(0x61746164u); writer.Write(rf64 ? uint.MaxValue : (uint)dataBytes);
    }

    internal void Write(ReadOnlySpan<float> samples, bool preservePcm16 = false)
    {
        if (samples.Length > expectedFrames - writtenFrames) throw new InvalidDataException("输出超出完整时间轴。");
        foreach (float sample in samples)
        {
            if (!float.IsFinite(sample)) throw new InvalidDataException("混音含无效样本。");
            writer.Write(preservePcm16
                ? (short)Math.Clamp(Math.Round(sample * 32768), short.MinValue, short.MaxValue)
                : (short)Math.Round(Math.Clamp(sample, -M2TimelineRenderer.PeakCeiling, M2TimelineRenderer.PeakCeiling) * short.MaxValue));
        }
        writtenFrames += samples.Length;
    }

    internal void Finish()
    {
        if (writtenFrames != expectedFrames) throw new InvalidDataException("输出缺失尾段。");
        writer.Flush();
        if (stream.Length != FileBytes(expectedFrames)) throw new InvalidDataException("WAV 容器实际字节长度不一致。");
        stream.Flush(true);
    }

    public void Dispose() { writer.Dispose(); stream.Dispose(); }
}
