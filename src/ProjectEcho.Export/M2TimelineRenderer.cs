using ProjectEcho.Core;

namespace ProjectEcho.Export;

/// <summary>Bounded PCM working set; absolute packet QPC anchors retain gaps and different source endings.</summary>
internal static class M2TimelineRenderer
{
    internal const int SampleRate = 48000;
    internal const float SourceGain = 1f;
    internal const float PeakCeiling = 0.8912509f;
    private const int BlockFrames = 8192;

    internal static TimelineRenderLevel Render(CaptureResult capture, string destination, CancellationToken token, IProgress<MixProgress>? progress = null)
    {
        var reporter = new MixProgressReporter(progress);
        RecordingTimeline.Validate(capture.TimelinePolicy);
        foreach (var track in capture.Tracks) RecordingTimeline.Validate(capture.TimelinePolicy, track.ExcludedPause100ns);
        long frames = FramesForDuration(capture.Duration100ns);
        var sourceGroups = capture.Tracks.GroupBy(t => t.SourceId, StringComparer.Ordinal).ToArray();
        if (sourceGroups.Length > 2 || sourceGroups.Any(g => g.Key is not ("remote" or "mic")))
            throw new NotSupportedException("完整混音只接受独立的 remote 与可选 mic 来源。");
        var sources = new List<SourceCursor>();
        try
        {
            foreach (var group in sourceGroups) sources.Add(new SourceCursor(group.ToArray(), capture.EpochQpc100ns, frames));
            var mix = new float[BlockFrames];
            // Measure the floating-point sum before PCM16 conversion. A fixed
            // 0.4 gain lowered every quiet recording, while unity followed by
            // PCM clipping would irreversibly distort overlapping loud tracks.
            var meter = new TruePeakMeter(SampleRate);
            reporter.Report(MixStages.RenderAnalysis, 0, frames, true);
            for (long start = 0; start < frames; start += BlockFrames)
            {
                token.ThrowIfCancellationRequested(); int count = (int)Math.Min(BlockFrames, frames - start);
                Array.Clear(mix);
                foreach (var source in sources) source.Add(start, count, mix, token);
                meter.AppendSamples(mix.AsSpan(0, count));
                reporter.Report(MixStages.RenderAnalysis, start + count, frames);
            }
            var peak = meter.Complete();
            double ceiling = Math.Pow(10, (TruePeakProtection.ProcessingCeilingDbTp - 0.01) / 20);
            double gain = peak.TruePeakLinear > ceiling ? ceiling / peak.TruePeakLinear : 1;
            foreach (var source in sources) source.Reset();
            using var output = new MonoPcmWriter(destination, frames);
            reporter.Report(MixStages.Rendering, 0, frames, true);
            for (long start = 0; start < frames; start += BlockFrames)
            {
                token.ThrowIfCancellationRequested(); int count = (int)Math.Min(BlockFrames, frames - start);
                Array.Clear(mix);
                foreach (var source in sources) source.Add(start, count, mix, token);
                if (gain < 1) for (int i = 0; i < count; i++) mix[i] *= (float)gain;
                output.Write(mix.AsSpan(0, count));
                reporter.Report(MixStages.Rendering, start + count, frames);
            }
            output.Finish();
            return new(gain, peak, sources.Sum(source => source.SmoothedPackets), sources.Select(source => source.MaximumCorrection100ns).DefaultIfEmpty().Max());
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
        internal int SmoothedPackets { get; private set; }
        internal double MaximumCorrection100ns { get; private set; }

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
                    if (!packet.TimestampValid || (packet.Flags & 4) != 0 || packet.QpcPosition100ns < epoch || packet.FrameCount <= 0 ||
                        packet.RetainedFrameOffset < 0 || packet.RetainedFrameOffset + (long)packet.FrameCount > packet.RawFrameCount || packet.WaveFrameOffset != nextOffset)
                        throw new InvalidDataException("原轨 packet 时间或存储范围无效。");
                    nextOffset = checked(nextOffset + packet.FrameCount);
                    double start = (double)RecordingTimeline.RawPosition100ns(packet.QpcPosition100ns, epoch, track.ExcludedPause100ns) * SampleRate / AudioClock.UnitsPerSecond +
                        (double)packet.RetainedFrameOffset * SampleRate / track.Format.SampleRate;
                    double end = start + (double)packet.FrameCount * SampleRate / track.Format.SampleRate;
                    if (start < -0.0001 || end > totalFrames + 0.01) throw new InvalidDataException("包时间越过已确认完整会话边界。");
                    prepared.Add(new(track, packet, start, end));
                }
                if (nextOffset != track.FrameCount) throw new InvalidDataException("WAV 帧数与索引不一致。");
            }
            // A raw packet can straddle the storage chunk boundary. Re-anchor
            // all its fragments together: assigning an entire 0.7 ms correction
            // to a one-frame last fragment would reverse it or leave a hole.
            var rawPackets = prepared.GroupBy(p => (p.Packet.QpcPosition100ns, p.Packet.DevicePosition,
                p.Packet.RawFrameCount, p.Packet.Flags, p.Track.Format, p.Track.SegmentId))
                .OrderBy(group => group.Key.QpcPosition100ns)
                .Select(group => group.OrderBy(p => p.Packet.RetainedFrameOffset).ToArray()).ToArray();
            if (rawPackets.Length > 0 && rawPackets[0][0].Packet.ClockJitter100ns != 0)
                throw new InvalidDataException("首个原包不能声明相邻时钟抖动。");
            for (int i = 1; i < rawPackets.Length; i++)
            {
                var previous = rawPackets[i - 1]; var current = rawPackets[i];
                var a = previous[^1]; var b = current[0];
                bool declaredJitter = b.Packet.ClockJitter100ns != 0;
                if (declaredJitter && (a.Track.Format != b.Track.Format || a.Track.SegmentId != b.Track.SegmentId ||
                    b.Packet.ClockJitter100ns != DevicePacketClock.BoundedJitter100ns(
                        a.Track.SourceId, a.Track.Format.SampleRate, a.Packet, b.Packet)))
                    throw new InvalidDataException("混音的时钟抖动声明缺少连续设备帧/原始QPC证据。");
                double rawStart = (double)RecordingTimeline.RawPosition100ns(a.Packet.QpcPosition100ns, epoch, a.Track.ExcludedPause100ns) * SampleRate / AudioClock.UnitsPerSecond;
                double rawDuration = (double)(b.Packet.QpcPosition100ns - a.Packet.QpcPosition100ns) * SampleRate / AudioClock.UnitsPerSecond;
                double nominal = (double)a.Packet.RawFrameCount * SampleRate / a.Track.Format.SampleRate;
                // Apply the existing quantization tolerance to the complete raw
                // packet too; even a one-input-frame correction can reverse a
                // one-frame chunk fragment if adjusted after splitting.
                // A valid device counter is independent evidence:
                // a small real loss must not be stretched away as QPC rounding.
                // Discontinuity describes the interval before that packet, so
                // A's flag must not forbid an independently continuous A->B pair.
                // Keep the existing process-loopback QPC-only policy unchanged;
                // endpoint remote streams with valid counters get the same gap
                // protection as microphone streams.
                bool deviceContiguous = (a.Track.SourceId != "mic" &&
                    !(a.Packet.DevicePositionValid && b.Packet.DevicePositionValid)) ||
                    (a.Packet.DevicePositionValid && b.Packet.DevicePositionValid &&
                     (b.Packet.Flags & 5) == 0 &&
                     (UInt128)a.Packet.DevicePosition + (uint)a.Packet.RawFrameCount == b.Packet.DevicePosition);
                bool quantizedContiguous = a.Track.Format == b.Track.Format && a.Track.SegmentId == b.Track.SegmentId &&
                    deviceContiguous &&
                    b.Packet.RetainedFrameOffset == 0 &&
                    a.Packet.RetainedFrameOffset + (long)a.Packet.FrameCount == a.Packet.RawFrameCount &&
                    Math.Abs(rawDuration - nominal) <= 2.0 * SampleRate / a.Track.Format.SampleRate + .0001;
                // Storage/recovery permit up to one input frame of negative QPC
                // quantization even at a reported discontinuity or a change in
                // device-clock validity. Such a packet is not evidence of a
                // continuous run, but rejecting its already-validated PCM here
                // made otherwise recoverable endpoint sessions unrenderable.
                // Only shorten the preceding complete raw packet to the next
                // anchor: never fill a positive gap or cross a segment/cutoff.
                Int128 deviation = ((Int128)b.Packet.QpcPosition100ns - a.Packet.QpcPosition100ns) * a.Track.Format.SampleRate
                    - (Int128)a.Packet.RawFrameCount * AudioClock.UnitsPerSecond;
                UInt128 expectedDevice = (UInt128)a.Packet.DevicePosition + (uint)a.Packet.RawFrameCount;
                bool deviceOrderValid = !(a.Packet.DevicePositionValid && b.Packet.DevicePositionValid) ||
                    (b.Packet.DevicePosition >= expectedDevice &&
                     (b.Packet.DevicePosition == expectedDevice || (b.Packet.Flags & 1) != 0));
                bool negativeQuantization = a.Track.Format == b.Track.Format && a.Track.SegmentId == b.Track.SegmentId &&
                    b.Packet.QpcPosition100ns > a.Packet.QpcPosition100ns &&
                    b.Packet.RetainedFrameOffset == 0 &&
                    a.Packet.RetainedFrameOffset + (long)a.Packet.FrameCount == a.Packet.RawFrameCount &&
                    deviceOrderValid && deviation < 0 && deviation >= -AudioClock.UnitsPerSecond;
                if (!declaredJitter && !quantizedContiguous && !negativeQuantization) continue;
                int firstRetainedOffset = previous[0].Packet.RetainedFrameOffset;
                if (firstRetainedOffset > 0)
                {
                    // Resume can discard a raw packet's pre-Start prefix. Its
                    // first retained frame is a confirmed privacy/start boundary,
                    // not an interior point that jitter correction may move into
                    // the preceding recording segment. Anchor the retained start
                    // exactly, then map only its retained suffix to the next raw
                    // QPC anchor. Map all chunk fragments together as before.
                    double retainedStart = previous[0].Start;
                    double retainedDuration = rawStart + rawDuration - retainedStart;
                    int retainedRawFrames = a.Packet.RawFrameCount - firstRetainedOffset;
                    if (retainedRawFrames <= 0 || retainedDuration <= 0)
                        throw new InvalidDataException("恢复首包的保留区间不能映射到下一实际时钟锚点。");
                    foreach (var fragment in previous)
                    {
                        fragment.Start = retainedStart + retainedDuration * (fragment.Packet.RetainedFrameOffset - firstRetainedOffset) / retainedRawFrames;
                        fragment.End = retainedStart + retainedDuration * (fragment.Packet.RetainedFrameOffset + fragment.Packet.FrameCount - firstRetainedOffset) / retainedRawFrames;
                    }
                    continue;
                }
                foreach (var fragment in previous)
                {
                    fragment.Start = rawStart + rawDuration * fragment.Packet.RetainedFrameOffset / fragment.Packet.RawFrameCount;
                    fragment.End = rawStart + rawDuration * (fragment.Packet.RetainedFrameOffset + fragment.Packet.FrameCount) / fragment.Packet.RawFrameCount;
                }
            }
            SmoothDeviceRuns(rawPackets, epoch);
            spans = prepared.OrderBy(p => p.Start).ToArray();
            for (int i = 0; i + 1 < spans.Length; i++)
            {
                var a = spans[i]; var b = spans[i + 1]; double difference = b.Start - a.End;
                // Whole raw packets were already mapped. Only floating-point
                // roundoff remains permissible between their chunk fragments.
                if (difference < -0.0001) throw new InvalidDataException("同一路来源时间范围重叠；拒绝重复混入 PCM。");
            }
        }

        private void SmoothDeviceRuns(Span[][] packets, ulong epoch)
        {
            // QPC jitter is not evidence that the actual sample clock speeds up
            // and slows down every packet. Within proven continuous device runs,
            // use shared window endpoints instead of modulating pitch per packet.
            // Keep raw clocks untouched and bound every mapped edge to 2 ms from
            // its raw position. Never smooth across a gap, discontinuity or segment.
            int start = 0;
            while (start < packets.Length)
            {
                int last = start;
                while (last + 1 < packets.Length && DeviceContinuous(packets[last][^1], packets[last + 1][0])) last++;
                int window = start;
                while (window < last)
                {
                    int boundary = window + 1;
                    while (boundary <= last &&
                        packets[boundary][0].Packet.QpcPosition100ns - packets[window][0].Packet.QpcPosition100ns < AudioClock.UnitsPerSecond)
                        boundary++;
                    int end = boundary <= last ? boundary - 1 : last;
                    MapWindow(window, end, boundary <= last ? packets[boundary][0] : null);
                    window = end + 1;
                }
                start = last + 1;
            }

            bool DeviceContinuous(Span a, Span b)
            {
                if (a.Track.Format != b.Track.Format || a.Track.SegmentId != b.Track.SegmentId ||
                    !a.Packet.DevicePositionValid || !b.Packet.DevicePositionValid ||
                    (b.Packet.Flags & 5) != 0 || b.Packet.QpcPosition100ns <= a.Packet.QpcPosition100ns ||
                    a.Packet.RetainedFrameOffset + (long)a.Packet.FrameCount != a.Packet.RawFrameCount ||
                    b.Packet.RetainedFrameOffset != 0 ||
                    (UInt128)a.Packet.DevicePosition + (uint)a.Packet.RawFrameCount != b.Packet.DevicePosition)
                    return false;
                Int128 difference = ((Int128)b.Packet.QpcPosition100ns - a.Packet.QpcPosition100ns) * a.Track.Format.SampleRate
                    - (Int128)a.Packet.RawFrameCount * AudioClock.UnitsPerSecond;
                return b.Packet.ClockJitter100ns != 0 || Int128.Abs(difference) <= AudioClock.UnitsPerSecond;
            }

            void MapWindow(int first, int last, Span? next)
            {
                if (last <= first) return;
                var a = packets[first][0]; var b = packets[last][^1];
                Int128 firstDevice = (Int128)a.Packet.DevicePosition + a.Packet.RetainedFrameOffset;
                Int128 endDevice = next is null ? (Int128)b.Packet.DevicePosition + b.Packet.RetainedFrameOffset + b.Packet.FrameCount
                    : (Int128)next.Packet.DevicePosition + next.Packet.RetainedFrameOffset;
                double startTime = a.Start, endTime = next?.Start ?? b.End;
                if (endDevice <= firstDevice || endTime <= startTime) return;
                double step = (endTime - startTime) / (double)(endDevice - firstDevice);
                double maximum = 0;
                for (int index = first; index <= last; index++)
                    foreach (var span in packets[index])
                    {
                        double mapped = startTime + (double)((Int128)span.Packet.DevicePosition + span.Packet.RetainedFrameOffset - firstDevice) * step;
                        double raw = (double)RecordingTimeline.RawPosition100ns(span.Packet.QpcPosition100ns, epoch, span.Track.ExcludedPause100ns) * SampleRate / AudioClock.UnitsPerSecond
                            + (double)span.Packet.RetainedFrameOffset * SampleRate / span.Track.Format.SampleRate;
                        maximum = Math.Max(maximum, Math.Max(Math.Abs(mapped - raw),
                            Math.Abs(mapped + span.Packet.FrameCount * step - raw - (double)span.Packet.FrameCount * SampleRate / span.Track.Format.SampleRate)));
                    }
                if (maximum > DevicePacketClock.MaximumJitter100ns * (double)SampleRate / AudioClock.UnitsPerSecond + .00001)
                {
                    // An unstable clock must not acquire an unbounded correction.
                    // Split using another real anchor, ultimately retaining the
                    // already-validated packet mapping when no window fits.
                    int middle = first + (last - first + 1) / 2;
                    MapWindow(first, middle - 1, packets[middle][0]);
                    MapWindow(middle, last, next);
                    return;
                }
                for (int index = first; index <= last; index++)
                    foreach (var span in packets[index])
                    {
                        span.Start = startTime + (double)((Int128)span.Packet.DevicePosition + span.Packet.RetainedFrameOffset - firstDevice) * step;
                        span.End = span.Start + span.Packet.FrameCount * step;
                    }
                SmoothedPackets += last - first + 1;
                MaximumCorrection100ns = Math.Max(MaximumCorrection100ns, maximum * AudioClock.UnitsPerSecond / SampleRate);
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
                float[]? following = null;
                if (readEnd == span.Packet.FrameCount && index + 1 < spans.Length && SharesNextSample(span, spans[index + 1]))
                {
                    var next = spans[index + 1];
                    if (ReferenceEquals(span.Track, next.Track)) following = reader.ReadFrames(next.Packet.WaveFrameOffset, 1);
                    else
                    {
                        using var nextReader = new M2WaveReader(next.Track.WavePath, next.Track.Format);
                        if (nextReader.FrameCount != next.Track.FrameCount) throw new InvalidDataException("相邻原 WAV 帧数改变。");
                        following = nextReader.ReadFrames(next.Packet.WaveFrameOffset, 1);
                    }
                }
                for (long frame = first; frame < end; frame++)
                {
                    int available = readEnd - readFirst;
                    double position = Math.Clamp((frame - span.Start) * step - readFirst, 0, available - (following is null ? 1 : 0));
                    int low = Math.Min((int)position, available - 1), high = Math.Min(low + 1, available - (following is null ? 1 : 0));
                    float fraction = (float)(position - low);
                    float mono = 0;
                    for (int ch = 0; ch < channels; ch++)
                        mono += pcm[low * channels + ch] * (1 - fraction)
                            + (high == available ? following![ch] : pcm[high * channels + ch]) * fraction;
                    mix[frame - blockStart] += Math.Clamp(mono / channels, -1f, 1f) * SourceGain;
                }
            }
        }

        private static bool SharesNextSample(Span a, Span b)
        {
            if (a.Track.Format != b.Track.Format || a.Track.SegmentId != b.Track.SegmentId || Math.Abs(a.End - b.Start) > .0001)
                return false;
            // Chunk fragments of the same raw packet are always adjacent PCM.
            if (a.Packet.QpcPosition100ns == b.Packet.QpcPosition100ns && a.Packet.DevicePosition == b.Packet.DevicePosition &&
                a.Packet.RawFrameCount == b.Packet.RawFrameCount && a.Packet.Flags == b.Packet.Flags)
                return a.Packet.RetainedFrameOffset + a.Packet.FrameCount == b.Packet.RetainedFrameOffset;
            return a.Packet.DevicePositionValid && b.Packet.DevicePositionValid && (b.Packet.Flags & 5) == 0 &&
                a.Packet.RetainedFrameOffset + (long)a.Packet.FrameCount == a.Packet.RawFrameCount && b.Packet.RetainedFrameOffset == 0 &&
                (UInt128)a.Packet.DevicePosition + (uint)a.Packet.RawFrameCount == b.Packet.DevicePosition;
        }

        internal void Reset() => cursor = 0;
        public void Dispose() => reader?.Dispose();
    }
}

internal sealed record TimelineRenderLevel(double AppliedGain, TruePeakMeasurement Before, int SmoothedPackets, double MaximumClockCorrection100ns);

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
