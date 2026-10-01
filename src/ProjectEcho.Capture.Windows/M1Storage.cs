using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using ProjectEcho.Core;

namespace ProjectEcho.Capture.Windows;

public sealed record M1ChunkSummary(string ChunkId, string SegmentId, string SourceId, string Path,
    PcmFormat Format, long FrameCount, long PcmBytes, long DataOffsetBytes, string PcmSha256, string FileSha256);
public sealed record M1StorageResult(string ManifestPath, bool StorageComplete, long Duration100ns,
    IReadOnlyList<string> Warnings, IReadOnlyList<M1ChunkSummary> Chunks);

/// <summary>Single writer owned by CaptureHost. Never owns or starts an audio device.</summary>
public sealed class M1Storage : IDisposable
{
    private readonly string directory, title;
    private readonly ulong epoch;
    private readonly DateTimeOffset started;
    private readonly bool synthetic;
    private readonly FileStream journal, events;
    private readonly Dictionary<string, Segment> segments = new(StringComparer.Ordinal);
    private readonly List<Chunk> chunks = [];
    private readonly HashSet<string> storageWarnings = new(StringComparer.Ordinal);
    private long sequence, eventSequence, checkpointAt = Stopwatch.GetTimestamp();
    private long observedSessionUs;
    private bool disposed, finished;
    public DateTimeOffset? LastCheckpointUtc { get; private set; }
    public long BytesWritten { get; private set; }
    public string IndexPath => Path.Combine(directory, "journal", "capture-index.jsonl");

    public M1Storage(string directory, ulong epochQpc100ns, DateTimeOffset startedAtUtc, string title, bool isSynthetic = false)
    {
        this.directory = Path.GetFullPath(directory);
        M1Paths.NoReparseAncestors(this.directory);
        this.title = title; epoch = epochQpc100ns; started = startedAtUtc; synthetic = isSynthetic;
        Directory.CreateDirectory(this.directory);
        Directory.CreateDirectory(M1Paths.Resolve(this.directory, "sources"));
        Directory.CreateDirectory(M1Paths.Resolve(this.directory, "journal"));
        journal = new FileStream(IndexPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try { events = new FileStream(Path.Combine(this.directory, "journal", "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        catch { journal.Dispose(); throw; }
        Append(new { type = "session", meetingId = Path.GetFileName(this.directory), epochQpc100ns = U(epoch),
            startedAtUtc = started, displayTimeZone = TimeZoneInfo.Local.Id, storageVersion = "m1-recording-1" });
        journal.Flush(true);
    }

    public void AddSegment(string segmentId, string sourceId, WaveFormat waveFormat, CapturedPacket? firstPacket = null,
        string? processingMode = null)
    {
        EnsureOpen(); ValidateId(segmentId); ValidateId(sourceId);
        if (firstPacket is { TimestampValid: true } && firstPacket.QpcPosition100ns < epoch)
            throw new InvalidDataException("segment 锚点早于会话起点。");
        if (segments.Values.Any(s => s.SourceId == sourceId && !s.Closed))
            throw new InvalidOperationException("同一来源必须先确认旧 segment 的停采边界。");
        var format = CaptureSession.Describe(waveFormat); format.Validate();
        ValidateProcessingMode(processingMode);
        var segment = new Segment(segmentId, sourceId, waveFormat, format) { ProcessingMode = processingMode };
        segments.Add(segmentId, segment);
        // A successful AddSegment is the writer-readiness barrier. Create the actual
        // original file before AudioClient.Start, even though its first clock anchor
        // is only available once the first real packet arrives.
        var chunk = NewChunk(segment);
        chunk.File!.Writer.Flush(); chunk.File.Stream.Flush(true);
        if (firstPacket is not null) WriteSegment(segment, firstPacket);
    }

    public void WritePacket(string segmentId, byte[] pcm, CapturedPacket packet, int retainedFrameOffset = 0)
    {
        EnsureOpen(); var segment = segments[segmentId];
        // Caller provides retained PCM only, never a raw buffer containing forbidden tail PCM.
        int offset = retainedFrameOffset == 0 ? packet.RetainedFrameOffset : retainedFrameOffset;
        if (segment.Closed || packet.FrameCount <= 0 || offset < 0 || packet.RawFrameCount < (long)offset + packet.FrameCount ||
            packet.WaveFrameOffset != segment.Frames || pcm.Length != checked(packet.FrameCount * segment.Format.BlockAlign))
            throw new InvalidDataException("写入包范围、来源边界或 PCM 长度无效。");
        packet = packet with { RetainedFrameOffset = offset };
        if ((packet.Flags & ~7) != 0) throw new InvalidDataException("未知 WASAPI flags。");
        if (!packet.TimestampValid || (packet.Flags & 4) != 0 || packet.QpcPosition100ns < epoch)
            throw new InvalidDataException("时间有歧义或早于会话起点的 PCM 不进入 M1 原件。");
        if ((packet.Flags & 2) != 0 && pcm.Any(b => b != 0)) throw new InvalidDataException("SILENT 包含非零 PCM。");
        ulong gap = ValidateContinuity(segment, packet, out long clockJitter);
        if (clockJitter != 0) storageWarnings.Add(segment.SourceId + " 连续设备帧的 API QPC 存在小幅抖动；已保留原始时钟及偏差，回听按相邻实际时钟锚点映射。长时同步仍需实测。");
        if (gap != 0) storageWarnings.Add(segment.SourceId + " 存在未观察到音频包的时间区间；已保存明确缺口，不当作采集的静音。");
        if (!segment.Written) WriteSegment(segment, packet);
        int remaining = packet.FrameCount, consumed = 0;
        while (remaining != 0)
        {
            var chunk = segment.Active ?? NewChunk(segment);
            int count = (int)Math.Min(remaining, (long)segment.Format.SampleRate * 30 - chunk.Frames);
            int bytes = checked(count * segment.Format.BlockAlign), byteOffset = checked(consumed * segment.Format.BlockAlign);
            chunk.File!.Writer.Write(pcm, byteOffset, bytes);
            chunk.Hash.AppendData(pcm.AsSpan(byteOffset, bytes));
            Append(new { type = "packet", segmentId, chunkId = chunk.Id, path = chunk.RelativePath,
                deviceFramePosition = U(packet.DevicePosition), devicePositionValid = packet.DevicePositionValid,
                qpcPosition100ns = U(packet.QpcPosition100ns), rawFrameCount = packet.RawFrameCount,
                flags = Flags(packet.Flags), retainedFrameOffset = checked(offset + consumed), frameCount = count,
                chunkFrameOffset = chunk.Frames, sessionStartUs = SessionUs(packet, offset + consumed, segment.Format.SampleRate),
                timestampQuality = packet.GetTimestampQuality(), gapBefore100ns = U(consumed == 0 ? gap : 0),
                clockJitter100ns = consumed == 0 ? clockJitter : 0,
                gapReason = consumed == 0 && gap != 0 ? "unobserved_audio_interval" : null });
            segment.LastSequence = sequence - 1;
            if (packet.TimestampValid)
                segment.MaximumEnd = Int128.Max(segment.MaximumEnd, (Int128)packet.QpcPosition100ns * segment.Format.SampleRate +
                    (Int128)(offset + consumed + count) * AudioClock.UnitsPerSecond);
            observedSessionUs = Math.Max(observedSessionUs, SessionUs(packet, offset + consumed + count, segment.Format.SampleRate));
            if (packet.DevicePositionValid)
                segment.MaximumDeviceEnd = UInt128.Max(segment.MaximumDeviceEnd, (UInt128)packet.DevicePosition + (uint)(offset + consumed + count));
            chunk.Frames += count; segment.Frames += count; BytesWritten += bytes;
            remaining -= count; consumed += count;
            if (chunk.Frames == (long)segment.Format.SampleRate * 30)
            {
                Checkpoint();
                FinalizeChunk(chunk); segment.Active = null;
            }
        }
        segment.PreviousPacket = packet;
        CheckpointIfDue();
    }

    public void WriteBoundary(string segmentId, string requestId, string operation, ulong cutoffQpc100ns,
        ulong? firstForbiddenDeviceFrame, long discardedFrameCount, string? discardReason)
    {
        EnsureOpen(); var segment = segments[segmentId];
        if (segment.Closed) throw new InvalidOperationException("segment 边界已保存。");
        if (operation is not ("stop" or "pause" or "mic_off") || discardedFrameCount < 0 ||
            discardReason is not (null or "crossing_frame" or "after_boundary" or "timestamp_ambiguous") || cutoffQpc100ns < epoch ||
            string.IsNullOrWhiteSpace(requestId) || (operation == "mic_off" && segment.SourceId != "mic") || (discardedFrameCount > 0 && discardReason is null))
            throw new ArgumentException("无效边界。");
        if (!segment.Written) WriteSegment(segment, null);
        if (segment.MaximumEnd > (Int128)cutoffQpc100ns * segment.Format.SampleRate ||
            (firstForbiddenDeviceFrame is { } forbidden && segment.MaximumDeviceEnd > forbidden))
            throw new InvalidDataException("已经写入的 PCM 超过实际停采边界。");
        Append(new { type = "boundary", requestId, operation, segmentId,
            effectiveSessionUs = checked((long)(((Int128)cutoffQpc100ns - epoch) / 10)), effectiveQpc100ns = U(cutoffQpc100ns),
            firstForbiddenDeviceFrame = firstForbiddenDeviceFrame is { } frame ? U(frame) : null,
            acceptedThroughSequence = segment.LastSequence, discardedFrameCount, discardReason });
        observedSessionUs = Math.Max(observedSessionUs, checked((long)((cutoffQpc100ns - epoch) / 10)));
        segment.Closed = true;
        Checkpoint();
        if (segment.Active is { } chunk) { FinalizeChunk(chunk); segment.Active = null; }
    }

    public void WriteEvent(string operation, string requestId, long sessionUs, string? microphoneDeviceId = null)
    {
        EnsureOpen();
        if (sessionUs < 0 || string.IsNullOrWhiteSpace(operation) || string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("无效事件。");
        WriteLine(events, eventSequence++, new { type = "event", operation, requestId, sessionUs, microphoneDeviceId }, synthetic);
        events.Flush(true);
    }

    /// <summary>Capture-thread elapsed time, including pause/no-callback intervals. Never substitutes for packet clocks.</summary>
    public void SetObservedSessionUs(long sessionUs)
    {
        EnsureOpen();
        if (sessionUs < 0 || sessionUs > long.MaxValue / 10) throw new ArgumentOutOfRangeException(nameof(sessionUs));
        observedSessionUs = Math.Max(observedSessionUs, sessionUs);
    }

    /// <summary>Call on the writer's idle timeout as well as after packets. Does not promise hard real-time disk latency.</summary>
    public void CheckpointIfDue()
    {
        if (Stopwatch.GetElapsedTime(checkpointAt).TotalMilliseconds >= 1000) Checkpoint();
    }

    public void Checkpoint()
    {
        EnsureOpen();
        foreach (var chunk in chunks.Where(c => c.File is not null)) { chunk.File!.Writer.Flush(); chunk.File.Stream.Flush(true); }
        journal.Flush(true);
        Append(new { type = "checkpoint", coveredThroughSequence = sequence - 1, observedSessionUs,
            chunks = chunks.Where(c => c.Segment.Written).Select(c => new { chunkId = c.Id, segmentId = c.Segment.Id, path = c.RelativePath, committedFrameCount = c.Frames,
                committedPcmBytes = checked(c.Frames * c.Segment.Format.BlockAlign), dataOffsetBytes = c.DataOffset,
                pcmSha256 = c.PcmHash ?? Convert.ToHexStringLower(c.Hash.GetCurrentHash()) }).ToArray() });
        journal.Flush(true); events.Flush(true);
        LastCheckpointUtc = DateTimeOffset.UtcNow; checkpointAt = Stopwatch.GetTimestamp();
    }

    public M1StorageResult Finish(long duration100ns, bool complete, IReadOnlyList<string> warnings)
    {
        EnsureOpen();
        if (duration100ns < 0) throw new ArgumentOutOfRangeException(nameof(duration100ns));
        if ((Int128)duration100ns < (Int128)observedSessionUs * 10 || segments.Values.Any(s =>
            s.MaximumEnd > ((Int128)epoch + duration100ns) * s.Format.SampleRate))
            throw new InvalidDataException("完成时长不能截短已确认经过时间或包尾帧。");
        SetObservedSessionUs(duration100ns / 10);
        foreach (var segment in segments.Values) if (!segment.Written) WriteSegment(segment, null);
        // Missing actual capture-thread boundary must never be labelled completely saved.
        if (segments.Values.Any(s => !s.Closed)) complete = false;
        warnings = warnings.Concat(storageWarnings).Distinct(StringComparer.Ordinal).ToArray();
        Checkpoint();
        foreach (var chunk in chunks) FinalizeChunk(chunk);
        var summaries = chunks.Select(c => new M1ChunkSummary(c.Id, c.Segment.Id, c.Segment.SourceId, c.RelativePath,
            c.Segment.Format, c.Frames, checked(c.Frames * c.Segment.Format.BlockAlign), c.DataOffset, c.PcmHash!, c.FileHash!)).ToArray();
        var manifest = Path.Combine(directory, "meeting.json");
        WriteAtomicJson(manifest, new { schemaVersion = "m1-recording-1", isSynthetic = synthetic, title,
            project = "未分类", startedAtUtc = started, epochQpc100ns = U(epoch), duration100ns,
            captureIndexPath = "journal/capture-index.jsonl", storageComplete = complete,
            finalCheckpointSequence = sequence - 1, warnings, chunks = summaries });
        finished = true;
        return new(manifest, complete, duration100ns, warnings.ToArray(), summaries);
    }

    private Chunk NewChunk(Segment segment)
    {
        string id = segment.Id + "-" + (++segment.ChunkNumber).ToString("D6", CultureInfo.InvariantCulture);
        string relative = $"sources/{segment.SourceId}/{id}.wav";
        var path = M1Paths.Resolve(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var chunk = new Chunk(id, relative, segment, new CaptureWaveFile(path + ".partial", segment.WaveFormat));
        chunks.Add(chunk); segment.Active = chunk; return chunk;
    }

    private void FinalizeChunk(Chunk chunk)
    {
        if (chunk.File is null) return;
        chunk.File.Writer.Flush(); chunk.File.Stream.Flush(true);
        chunk.File.Dispose(); chunk.File = null;
        string path = M1Paths.Resolve(directory, chunk.RelativePath);
        using (var reader = new WaveFileReader(path + ".partial"))
            if (reader.Length != checked(chunk.Frames * chunk.Segment.Format.BlockAlign)) throw new InvalidDataException("WAV 与帧索引长度不一致。");
        chunk.PcmHash = Convert.ToHexStringLower(chunk.Hash.GetCurrentHash());
        using (var stream = File.OpenRead(path + ".partial")) chunk.FileHash = Convert.ToHexStringLower(SHA256.HashData(stream));
        File.Move(path + ".partial", path, false);
    }

    private void WriteSegment(Segment segment, CapturedPacket? first)
    {
        var format = segment.Format;
        Append(new { type = "segment", segmentId = segment.Id, sourceId = segment.SourceId, processingMode = segment.ProcessingMode,
            format = new { sampleRate = format.SampleRate, channels = format.Channels,
                encoding = format.IsFloat ? "float32le" : $"pcm_s{format.BitsPerSample}le", blockAlign = format.BlockAlign, channelMask = format.ChannelMask,
                validBitsPerSample = segment.WaveFormat is WaveFormatExtensible e ? e.ValidBitsPerSample : format.BitsPerSample },
            anchor = new { deviceFramePosition = first is { TimestampValid: true, DevicePositionValid: true } ? U(first.DevicePosition) : null,
                qpcPosition100ns = first is { TimestampValid: true } ? U(first.QpcPosition100ns) : null,
                sessionStartUs = first is null ? 0 : SessionUs(first, first.RetainedFrameOffset, format.SampleRate),
                timestampQuality = first?.GetTimestampQuality() ?? "invalid" } });
        segment.Written = true;
        segment.AnchorPacket = first;
    }

    private long SessionUs(CapturedPacket packet, int offset, int rate) => packet.TimestampValid
        ? Math.Max(0, checked((long)((((Int128)packet.QpcPosition100ns - epoch) * rate + (Int128)offset * AudioClock.UnitsPerSecond) / (rate * 10L)))) : 0;
    private static ulong ValidateContinuity(Segment segment, CapturedPacket packet, out long clockJitter)
    {
        clockJitter = 0;
        if (segment.PreviousPacket is not { } previous)
        {
            if (segment.AnchorPacket is { } anchor && (anchor.QpcPosition100ns != packet.QpcPosition100ns || anchor.DevicePosition != packet.DevicePosition ||
                anchor.RetainedFrameOffset != packet.RetainedFrameOffset || anchor.TimestampValid != packet.TimestampValid || anchor.DevicePositionValid != packet.DevicePositionValid))
                throw new InvalidDataException("首个PCM包与已写segment锚点不同。");
            return 0;
        }
        bool sameRaw = previous.QpcPosition100ns == packet.QpcPosition100ns && previous.DevicePosition == packet.DevicePosition &&
            previous.RawFrameCount == packet.RawFrameCount && previous.Flags == packet.Flags;
        if (sameRaw)
        {
            if (packet.RetainedFrameOffset != previous.RetainedFrameOffset + previous.FrameCount || previous.DevicePositionValid != packet.DevicePositionValid)
                throw new InvalidDataException("同一原包子区间重复或有洞，拒绝写入PCM。");
            return 0;
        }
        if (previous.RetainedFrameOffset + previous.FrameCount != previous.RawFrameCount)
            throw new InvalidDataException("被截断原包之后必须先建立边界及新segment。");
        if (packet.QpcPosition100ns <= previous.QpcPosition100ns) throw new InvalidDataException("原始QPC不递增，拒绝写入PCM。");
        var difference = ((Int128)packet.QpcPosition100ns - previous.QpcPosition100ns) * segment.Format.SampleRate -
            (Int128)previous.RawFrameCount * AudioClock.UnitsPerSecond;
        clockJitter = MicrophonePacketClock.BoundedJitter100ns(segment.SourceId, segment.Format.SampleRate, previous, packet);
        if (clockJitter == 0 && difference < -AudioClock.UnitsPerSecond)
            throw new InvalidDataException("原包QPC重叠超过一帧，且没有连续设备帧的小幅抖动证据，拒绝写入PCM。");
        ulong gap = clockJitter == 0 && difference > AudioClock.UnitsPerSecond ? checked((ulong)(difference / segment.Format.SampleRate)) : 0;
        if (packet.DevicePositionValid && previous.DevicePositionValid)
        {
            var expected = (UInt128)previous.DevicePosition + (uint)previous.RawFrameCount;
            if (packet.DevicePosition < expected || (packet.DevicePosition != expected && gap == 0 && (packet.Flags & 1) == 0))
                throw new InvalidDataException("设备帧倒退或出现未报告缺口，拒绝写入PCM。");
        }
        return gap;
    }
    private void Append(object data) => WriteLine(journal, sequence++, data, synthetic);
    internal static void WriteLine(FileStream stream, long seq, object data, bool synthetic)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var head = Encoding.UTF8.GetBytes($"{{\"schemaVersion\":1,\"isSynthetic\":{(synthetic ? "true" : "false")},\"sequence\":{seq},\"data\":");
        stream.Write(head); stream.Write(bytes); stream.Write(Encoding.UTF8.GetBytes($",\"sha256\":\"{hash}\"}}\n"));
    }
    internal static void WriteAtomicJson(string path, object value)
    {
        var temporary = path + ".partial";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
        File.Move(temporary, path, false);
    }
    internal static string U(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static string[] Flags(int flags) => new[] { (1, "data_discontinuity"), (2, "silent"), (4, "timestamp_error") }
        .Where(f => (flags & f.Item1) != 0).Select(f => f.Item2).ToArray();
    internal static void ValidateProcessingMode(string? value)
    {
        if (value is not (null or "shared-default" or "shared-raw" or "shared-converted"))
            throw new InvalidDataException("未知采集处理模式。");
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("source/segment ID 只能包含 ASCII 字母数字、连字符和下划线。");
    }
    private void EnsureOpen() { ObjectDisposedException.ThrowIf(disposed, this); if (finished) throw new InvalidOperationException("存储已完成。"); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try { foreach (var chunk in chunks) { try { chunk.File?.Dispose(); } finally { chunk.Hash.Dispose(); } } }
        finally { journal.Dispose(); events.Dispose(); }
    }
    private sealed class Segment(string id, string sourceId, WaveFormat waveFormat, PcmFormat format)
    {
        public string Id = id, SourceId = sourceId;
        public WaveFormat WaveFormat = waveFormat;
        public PcmFormat Format = format;
        public string? ProcessingMode;
        public bool Written, Closed;
        public Int128 MaximumEnd;
        public UInt128 MaximumDeviceEnd;
        public int ChunkNumber;
        public long Frames;
        public long? LastSequence;
        public CapturedPacket? PreviousPacket;
        public CapturedPacket? AnchorPacket;
        public Chunk? Active;
    }
    private sealed class Chunk(string id, string path, Segment segment, CaptureWaveFile file)
    {
        public string Id = id, RelativePath = path;
        public Segment Segment = segment;
        public CaptureWaveFile? File = file;
        public long Frames, DataOffset = file.DataOffset;
        public IncrementalHash Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public string? PcmHash, FileHash;
    }
}

internal static class M1Paths
{
    internal static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\\') ||
            relative.Split('/').Any(p => p is "" or "." or ".." || p.TrimEnd(' ', '.') != p)) throw new InvalidDataException("拒绝不安全相对路径。");
        string absoluteRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(absoluteRoot, relative));
        if (!result.StartsWith(absoluteRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径越出会议目录。");
        NoReparseAncestors(result); return result;
    }
    internal static void NoReparseAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("拒绝重解析点路径。");
    }
}
