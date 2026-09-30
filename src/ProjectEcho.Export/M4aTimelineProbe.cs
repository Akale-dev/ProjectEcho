using System.Buffers.Binary;

namespace ProjectEcho.Export;

public sealed record M4aContainerTimeline(uint MovieTimescale, ulong MovieDurationTicks, ulong TrackDurationTicks,
    uint MediaTimescale, ulong MediaDurationTicks, ulong SampleTableDurationTicks, ulong EncodedSampleCount,
    ulong EncodedPayloadBytes)
{
    public double MovieDurationSeconds => (double)MovieDurationTicks / MovieTimescale;
    public double MediaDurationSeconds => (double)MediaDurationTicks / MediaTimescale;
    public double SampleTableDurationSeconds => (double)SampleTableDurationTicks / MediaTimescale;
}

/// <summary>
/// Read-only ISO BMFF time-table inspection for ProjectEcho's single-track, nonfragmented AAC/M4A output.
/// This does not rewrite boxes or use a player's possibly rounded duration property.
/// </summary>
public static class M4aTimelineProbe
{
    public static M4aContainerTimeline Inspect(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.RandomAccess);
        var parser = new Parser(input, cancellationToken);
        var top = parser.Children(0, input.Length);
        var moov = Only(top, "moov");
        var mdat = Only(top, "mdat");
        _ = Only(top, "ftyp");
        RejectBoxes(top, "moof", "mfra", "sidx");
        var movie = parser.Children(moov.Body, moov.End);
        RejectBoxes(movie, "mvex");
        var (movieScale, movieDuration) = parser.TimeHeader(Only(movie, "mvhd"));
        var trak = Only(movie, "trak");
        var track = parser.Children(trak.Body, trak.End);
        RejectBoxes(track, "edts", "elst");
        var trackDuration = parser.TrackDuration(Only(track, "tkhd"));
        var mdia = Only(track, "mdia");
        var media = parser.Children(mdia.Body, mdia.End);
        var (mediaScale, mediaDuration) = parser.TimeHeader(Only(media, "mdhd"));
        var handler = Only(media, "hdlr");
        if (handler.End - handler.Body < 24 || parser.Read32At(handler, 0) != 0) throw Invalid("M4A handler box 不完整或版本未验证。");
        if (parser.Read32At(handler, 8) != FourCc("soun")) throw Invalid("M4A 时间检查只支持音频 track。");
        var minf = Only(media, "minf");
        var mediaInformation = parser.Children(minf.Body, minf.End);
        var stbl = Only(mediaInformation, "stbl");
        var sampleTable = parser.Children(stbl.Body, stbl.End);
        RejectBoxes(sampleTable, "ctts", "elst");
        parser.ValidateSampleDescription(Only(sampleTable, "stsd"));
        var (samples, tableDuration) = parser.TimeToSample(Only(sampleTable, "stts"));
        var (sizedSamples, payloadBytes) = parser.SampleSizes(Only(sampleTable, "stsz"));
        if (samples == 0 || samples != sizedSamples || payloadBytes == 0 || payloadBytes > (ulong)(mdat.End - mdat.Body))
            throw Invalid("M4A sample 时间表、样本数量或媒体载荷不一致。");
        return new(movieScale, movieDuration, trackDuration, mediaScale, mediaDuration, tableDuration, samples, payloadBytes);
    }

    public static void ValidateAgainstDecoded(M4aContainerTimeline timeline, long decodedFrames, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (decodedFrames <= 0 || sampleRate <= 0 || timeline.MovieTimescale == 0 || timeline.MediaTimescale == 0)
            throw Invalid("M4A/解码时间尺度无效。");
        // Integer rational arithmetic: no fractional-second truncation or floating-point tolerance.
        if ((Int128)timeline.SampleTableDurationTicks * sampleRate != (Int128)decodedFrames * timeline.MediaTimescale)
            throw Invalid("M4A stts 时间表与实际 EOF 解码帧数不一致。");
        WithinOneFrame(timeline.MovieDurationTicks, timeline.MovieTimescale, decodedFrames, sampleRate);
        WithinOneFrame(timeline.TrackDurationTicks, timeline.MovieTimescale, decodedFrames, sampleRate);
        WithinOneFrame(timeline.MediaDurationTicks, timeline.MediaTimescale, decodedFrames, sampleRate);
    }

    private static void WithinOneFrame(ulong duration, uint timescale, long frames, int sampleRate)
    {
        var difference = (Int128)duration * sampleRate - (Int128)frames * timescale;
        if (difference < 0) difference = -difference;
        if (difference > timescale) throw Invalid("M4A 声明时长与实际解码相差超过一帧，拒绝发布。");
    }

    private readonly record struct Box(uint Type, long Body, long End);
    private static Box Only(IReadOnlyList<Box> boxes, string type)
    {
        var matching = boxes.Where(box => box.Type == FourCc(type)).ToArray();
        return matching.Length == 1 ? matching[0] : throw Invalid($"M4A 必须恰有一个 {type} box；当前范围未通过验证。");
    }
    private static void RejectBoxes(IReadOnlyList<Box> boxes, params string[] types)
    {
        if (boxes.Any(box => types.Any(type => box.Type == FourCc(type))))
            throw new NotSupportedException("M4A fragment、edit list 或 composition offset 不在当前已验证输出范围内。");
    }
    private static uint FourCc(string type) => (uint)type[0] << 24 | (uint)type[1] << 16 | (uint)type[2] << 8 | type[3];
    private static InvalidDataException Invalid(string message) => new(message);

    private sealed class Parser(FileStream stream, CancellationToken token)
    {
        private int visitedBoxes;
        public List<Box> Children(long start, long end)
        {
            if (start < 0 || end < start || end > stream.Length) throw Invalid("M4A box 边界无效。");
            var result = new List<Box>();
            long cursor = start;
            while (cursor < end)
            {
                token.ThrowIfCancellationRequested();
                if (++visitedBoxes > 4096 || end - cursor < 8) throw Invalid("M4A box 数量/头长度异常。");
                stream.Position = cursor;
                ulong size = Read32(); uint type = Read32(); long header = 8;
                if (size == 1)
                {
                    if (end - cursor < 16) throw Invalid("M4A 扩展 box 头不完整。");
                    size = Read64(); header = 16;
                }
                if (size == 0) size = (ulong)(end - cursor);
                if (size < (ulong)header || size > (ulong)(end - cursor)) throw Invalid("M4A box 超出父容器或文件边界。");
                long next = checked(cursor + (long)size);
                result.Add(new(type, cursor + header, next)); cursor = next;
            }
            return result;
        }

        public (uint Scale, ulong Duration) TimeHeader(Box box)
        {
            byte version = Version(box);
            long minimum = box.Type == FourCc("mvhd") ? (version == 0 ? 100 : 112) : (version == 0 ? 24 : 36);
            if (box.End - box.Body < minimum) throw Invalid("M4A 时间 header 不完整。");
            long offset = version == 0 ? 12 : 20;
            uint scale = Read32At(box, offset);
            ulong duration = version == 0 ? Read32At(box, offset + 4) : Read64At(box, offset + 4);
            if (scale == 0 || duration == ulong.MaxValue || (version == 0 && duration == uint.MaxValue))
                throw Invalid("M4A 时间尺度或未完成的 duration 无效。");
            return (scale, duration);
        }
        public ulong TrackDuration(Box box)
        {
            byte version = Version(box);
            if (box.End - box.Body < (version == 0 ? 84 : 96)) throw Invalid("M4A track header 不完整。");
            return version == 0 ? Read32At(box, 20) : Read64At(box, 28);
        }
        public void ValidateSampleDescription(Box box)
        {
            if (Read32At(box, 0) != 0 || Read32At(box, 4) != 1) throw Invalid("M4A sample description 不是单一受支持条目。");
            var entries = Children(box.Body + 8, box.End);
            if (entries.Count != 1 || entries[0].Type != FourCc("mp4a"))
                throw new NotSupportedException("当前 M4A 时间检查只接受单一 mp4a sample description。");
            if (entries[0].End - entries[0].Body < 28) throw Invalid("M4A 音频 sample entry 不完整。");
        }
        public (ulong Samples, ulong Duration) TimeToSample(Box box)
        {
            if (Read32At(box, 0) != 0) throw Invalid("不支持该 stts 版本/flags。");
            uint count = Read32At(box, 4);
            if ((ulong)(box.End - box.Body) != 8UL + 8UL * count) throw Invalid("M4A stts 表长度不一致。");
            stream.Position = box.Body + 8;
            ulong samples = 0, duration = 0;
            for (uint index = 0; index < count; index++)
            {
                if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
                uint sampleCount = Read32(), delta = Read32();
                if (sampleCount == 0 || delta == 0) throw Invalid("M4A stts 包含空样本/零时长。");
                samples = checked(samples + sampleCount);
                duration = checked(duration + (ulong)sampleCount * delta);
            }
            return (samples, duration);
        }
        public (ulong Samples, ulong Bytes) SampleSizes(Box box)
        {
            if (Read32At(box, 0) != 0) throw Invalid("不支持该 stsz 版本/flags。");
            uint fixedSize = Read32At(box, 4), count = Read32At(box, 8);
            ulong expectedLength = 12UL + (fixedSize == 0 ? 4UL * count : 0);
            if ((ulong)(box.End - box.Body) != expectedLength) throw Invalid("M4A stsz 表长度不一致。");
            if (fixedSize > 0) return (count, checked((ulong)count * fixedSize));
            stream.Position = box.Body + 12;
            ulong total = 0;
            for (uint index = 0; index < count; index++)
            {
                if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
                uint size = Read32();
                if (size == 0) throw Invalid("M4A 包含空压缩样本。");
                total = checked(total + size);
            }
            return (count, total);
        }
        private byte Version(Box box)
        {
            uint fullBox = Read32At(box, 0); byte version = (byte)(fullBox >> 24);
            if (version > 1) throw new NotSupportedException("M4A 时间 box 版本尚未验证。");
            return version;
        }
        public uint Read32At(Box box, long offset)
        {
            RequireRange(box, offset, 4); stream.Position = box.Body + offset; return Read32();
        }
        private ulong Read64At(Box box, long offset)
        {
            RequireRange(box, offset, 8); stream.Position = box.Body + offset; return Read64();
        }
        private static void RequireRange(Box box, long offset, int length)
        {
            if (offset < 0 || offset > box.End - box.Body - length) throw Invalid("M4A 时间字段超出 box。");
        }
        private uint Read32()
        {
            Span<byte> value = stackalloc byte[4]; stream.ReadExactly(value); return BinaryPrimitives.ReadUInt32BigEndian(value);
        }
        private ulong Read64()
        {
            Span<byte> value = stackalloc byte[8]; stream.ReadExactly(value); return BinaryPrimitives.ReadUInt64BigEndian(value);
        }
    }
}
