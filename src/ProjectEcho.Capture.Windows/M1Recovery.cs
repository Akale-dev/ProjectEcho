using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using ProjectEcho.Core;

namespace ProjectEcho.Capture.Windows;

public sealed record M1RecoveryResult(CaptureResult Capture, bool HasCheckpoint, bool IsCleanEnd,
    IReadOnlyList<string> Issues, long LastCheckpointSequence);

/// <summary>Reconstructs only checkpoint-confirmed PCM using the on-disk journal, never live capture state.</summary>
public static class M1Recovery
{
    private const long MaximumJournalBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumRecords = 4_000_000, MaximumLineBytes = 1_048_576;

    public static M1RecoveryResult Read(string directory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        directory = Path.GetFullPath(directory); M1Paths.NoReparseAncestors(directory);
        string index = M1Paths.Resolve(directory, "journal/capture-index.jsonl");
        if (new FileInfo(index).Length > MaximumJournalBytes) throw new InvalidDataException("索引超过离线恢复读取上限。");
        var issues = new List<string>();
        var captureWarnings = new List<string>();
        var segments = new Dictionary<string, Segment>(StringComparer.Ordinal);
        var chunks = new Dictionary<string, Chunk>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Watermark>? checkpoint = null;
        bool checkpointHasObservedTime = false;
        long expected = 0, lastCheckpoint = -1, duration = 0, checkpointDuration = 0, previousObservationUs = 0;
        ulong epoch = 0; DateTimeOffset started = default;
        bool synthetic = false, readSession = false;
        try
        {
            foreach (var bytes in Lines(index, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (expected >= MaximumRecords) throw new InvalidDataException("索引记录数超过恢复上限。");
                using var document = VerifiedLine(bytes, expected);
                var envelope = document.RootElement;
                if (readSession && envelope.GetProperty("isSynthetic").GetBoolean() != synthetic) throw new InvalidDataException("合成标识不一致。");
                var data = envelope.GetProperty("data"); string type = S(data, "type");
                if (!readSession && type != "session") throw new InvalidDataException("首行必须为 session。");
                switch (type)
                {
                    case "session":
                        if (expected != 0 || S(data, "storageVersion") != "m1-recording-1") throw new InvalidDataException("不是支持的 M1 session。");
                        epoch = U(data, "epochQpc100ns"); started = data.GetProperty("startedAtUtc").GetDateTimeOffset();
                        synthetic = envelope.GetProperty("isSynthetic").GetBoolean(); readSession = true;
                        break;
                    case "segment":
                    {
                        string id = S(data, "segmentId"), sourceId = S(data, "sourceId");
                        if (segments.Values.Any(s => s.SourceId == sourceId && !s.Closed)) throw new InvalidDataException("同来源存在未结束 segment。");
                        var formatData = data.GetProperty("format"); var format = Format(formatData);
                        var anchor = data.GetProperty("anchor"); var quality = Quality(anchor);
                        ulong? qpc = NullableU(anchor, "qpcPosition100ns"), device = NullableU(anchor, "deviceFramePosition");
                        long sessionUs = Nonnegative(anchor, "sessionStartUs");
                        if ((quality == "invalid") != (qpc is null) || (quality == "device_referenced") != (device is not null))
                            throw new InvalidDataException("segment 锚点与时间质量不一致。");
                        if (qpc is { } anchorQpc && anchorQpc < epoch) throw new InvalidDataException("segment 锚点早于会话起点。");
                        segments.Add(id, new(id, sourceId, format, formatData.GetProperty("validBitsPerSample").GetInt32(), qpc, device, sessionUs, quality));
                        break;
                    }
                    case "packet":
                    {
                        string segmentId = S(data, "segmentId"), chunkId = S(data, "chunkId"), relative = S(data, "path");
                        var segment = segments[segmentId];
                        if (segment.Closed) throw new InvalidDataException("停采边界之后仍有 PCM。");
                        M1Paths.Resolve(directory, relative);
                        if (!relative.StartsWith("sources/", StringComparison.Ordinal) || !relative.EndsWith(".wav", StringComparison.Ordinal))
                            throw new InvalidDataException("原轨不在 sources WAV 路径。");
                        if (!chunks.TryGetValue(chunkId, out var chunk))
                        {
                            if (!paths.Add(relative)) throw new InvalidDataException("多个 chunk 引用相同路径。");
                            chunks.Add(chunkId, chunk = new(chunkId, relative, segment));
                        }
                        if (chunk.Segment != segment || chunk.Path != relative) throw new InvalidDataException("chunk 引用发生改变。");
                        int count = data.GetProperty("frameCount").GetInt32(), raw = data.GetProperty("rawFrameCount").GetInt32(), offset = data.GetProperty("retainedFrameOffset").GetInt32();
                        long chunkOffset = Nonnegative(data, "chunkFrameOffset");
                        if (count <= 0 || raw <= 0 || offset < 0 || (long)offset + count > raw || chunkOffset != chunk.Frames ||
                            chunk.Frames + count > 30L * segment.Format.SampleRate) throw new InvalidDataException("包帧范围、连续偏移或30秒块上限错误。");
                        ulong qpc = U(data, "qpcPosition100ns"), device = U(data, "deviceFramePosition");
                        string quality = Quality(data); bool deviceValid = data.GetProperty("devicePositionValid").GetBoolean();
                        int flags = Flags(data.GetProperty("flags")); bool valid = quality != "invalid";
                        if (valid && qpc < epoch) throw new InvalidDataException("包 QPC 早于会话起点。");
                        ulong declaredGap = data.TryGetProperty("gapBefore100ns", out _) ? U(data, "gapBefore100ns") : 0;
                        segment.HasTimelineGap |= declaredGap > 0;
                        string? gapReason = data.TryGetProperty("gapReason", out var reasonField) ? reasonField.GetString() : null;
                        if ((declaredGap == 0 && gapReason is not null) || (declaredGap != 0 && gapReason != "unobserved_audio_interval"))
                            throw new InvalidDataException("缺口声明与原因不一致。");
                        if ((valid && (flags & 4) != 0) || (quality == "device_referenced" && !deviceValid) || (quality == "qpc_referenced" && deviceValid))
                            throw new InvalidDataException("时间质量与API标志冲突。");
                        long sessionUs = Nonnegative(data, "sessionStartUs");
                        long derived = valid ? Math.Max(0, checked((long)((((Int128)qpc - epoch) * segment.Format.SampleRate + (Int128)offset * AudioClock.UnitsPerSecond) / (segment.Format.SampleRate * 10L)))) : 0;
                        if (derived != sessionUs) throw new InvalidDataException("派生 session 时间与原始 QPC/保留偏移不一致。");
                        var packet = new CapturedPacket(chunkOffset, count, raw, device, qpc, flags, valid, deviceValid, offset);
                        if (segment.Last is { } previous)
                        {
                            bool sameRaw = previous.QpcPosition100ns == qpc && previous.DevicePosition == device && previous.RawFrameCount == raw && previous.Flags == flags;
                            if (sameRaw)
                            {
                                if (offset != previous.RetainedFrameOffset + previous.FrameCount || previous.TimestampValid != valid || previous.DevicePositionValid != deviceValid)
                                    throw new InvalidDataException("同一原包的子区间重复、有洞或质量不一致。");
                                if (declaredGap != 0) throw new InvalidDataException("同一原包的后续子区间重复声明缺口。");
                            }
                            else
                            {
                                if (previous.RetainedFrameOffset + previous.FrameCount != previous.RawFrameCount)
                                    throw new InvalidDataException("被截断原包之后未建立停采边界/新segment便出现下一原包。");
                                if (valid && previous.TimestampValid && qpc <= previous.QpcPosition100ns) throw new InvalidDataException("原包 QPC 不严格递增。");
                                var clockDifference = ((Int128)qpc - previous.QpcPosition100ns) * segment.Format.SampleRate -
                                    (Int128)previous.RawFrameCount * AudioClock.UnitsPerSecond;
                                // Raw clocks can drift relative to nominal sample rate. A single-frame
                                // tolerance covers quantization; larger unreported gaps/overlaps do not.
                                if (valid && previous.TimestampValid && clockDifference < -AudioClock.UnitsPerSecond)
                                    throw new InvalidDataException("原包 QPC 范围重叠超过一帧。");
                                ulong expectedGap = clockDifference > AudioClock.UnitsPerSecond ? checked((ulong)(clockDifference / segment.Format.SampleRate)) : 0;
                                if (declaredGap != expectedGap) throw new InvalidDataException("原包 QPC 范围缺口未精确声明。");
                                if (deviceValid && previous.DevicePositionValid)
                                {
                                    var expectedDevice = (UInt128)previous.DevicePosition + (uint)previous.RawFrameCount;
                                    if (device < expectedDevice || (device != expectedDevice && declaredGap == 0 && (flags & 1) == 0))
                                        throw new InvalidDataException("原始 device 帧位置倒退或存在未报告缺口。");
                                }
                            }
                        }
                        else if (declaredGap != 0 || segment.AnchorQpc != (valid ? qpc : null) || segment.AnchorDevice != (valid && deviceValid ? device : null) ||
                            segment.AnchorUs != sessionUs || segment.AnchorQuality != quality) throw new InvalidDataException("segment 锚点不是首包实际位置。");
                        chunk.Packets.Add(packet); chunk.Frames += count; segment.Last = packet; segment.LastSequence = expected;
                        if (valid) segment.MaximumEnd = Int128.Max(segment.MaximumEnd, (Int128)qpc * segment.Format.SampleRate +
                            (Int128)(offset + count) * AudioClock.UnitsPerSecond);
                        else segment.HasInvalidTimestamp = true;
                        if (deviceValid) segment.MaximumDeviceEnd = UInt128.Max(segment.MaximumDeviceEnd, (UInt128)device + (uint)(offset + count));
                        if (valid) duration = Math.Max(duration, checked((long)((Int128)qpc - epoch + ((Int128)(offset + count) * AudioClock.UnitsPerSecond + segment.Format.SampleRate - 1) / segment.Format.SampleRate)));
                        break;
                    }
                    case "boundary":
                    {
                        var segment = segments[S(data, "segmentId")];
                        string operation = S(data, "operation");
                        if (segment.Closed || operation is not ("stop" or "pause" or "mic_off") || (operation == "mic_off" && segment.SourceId != "mic"))
                            throw new InvalidDataException("重复或非法停采边界。");
                        ulong cutoff = U(data, "effectiveQpc100ns");
                        if (cutoff < epoch || Nonnegative(data, "effectiveSessionUs") != (long)((cutoff - epoch) / 10)) throw new InvalidDataException("边界时间不一致。");
                        var accepted = data.GetProperty("acceptedThroughSequence");
                        long? sequence = accepted.ValueKind == JsonValueKind.Null ? null : accepted.GetInt64();
                        if (sequence != segment.LastSequence) throw new InvalidDataException("边界 acceptedThroughSequence 不是末个获准包。");
                        long discarded = Nonnegative(data, "discardedFrameCount");
                        string? reason = data.GetProperty("discardReason").GetString();
                        if (reason is not (null or "crossing_frame" or "after_boundary" or "timestamp_ambiguous") || (discarded > 0 && reason is null)) throw new InvalidDataException("未知或缺少边界丢弃原因。");
                        ulong? forbidden = NullableU(data, "firstForbiddenDeviceFrame");
                        if (segment.Last is not null)
                        {
                            if (segment.HasInvalidTimestamp || segment.MaximumEnd > (Int128)cutoff * segment.Format.SampleRate)
                                throw new InvalidDataException("截止之后或时间有歧义的 PCM 已写入。");
                            if (forbidden is { } boundary && segment.MaximumDeviceEnd > boundary)
                                throw new InvalidDataException("PCM 超过首个禁存设备帧。");
                        }
                        segment.Closed = true; duration = Math.Max(duration, checked((long)(cutoff - epoch)));
                        break;
                    }
                    case "checkpoint":
                    {
                        if (data.GetProperty("coveredThroughSequence").GetInt64() != expected - 1) throw new InvalidDataException("checkpoint 水位不覆盖连续前缀。");
                        bool hasObservedTime = data.TryGetProperty("observedSessionUs", out _);
                        long observed = hasObservedTime ? Nonnegative(data, "observedSessionUs") : 0;
                        if (observed < previousObservationUs) throw new InvalidDataException("checkpoint观测经过时间倒退。");
                        var candidate = new Dictionary<string, Watermark>(StringComparer.Ordinal);
                        foreach (var entry in data.GetProperty("chunks").EnumerateArray())
                        {
                            string id = S(entry, "chunkId"), segmentId = S(entry, "segmentId"), relative = S(entry, "path");
                            long frames = Nonnegative(entry, "committedFrameCount"), bytesCount = Nonnegative(entry, "committedPcmBytes"), dataOffset = Nonnegative(entry, "dataOffsetBytes");
                            string hash = HashString(entry, "pcmSha256");
                            if (!chunks.TryGetValue(id, out var chunk))
                            {
                                // A source can be successfully opened yet never deliver a packet.
                                // Its prepared zero-frame WAV is indexed explicitly, never called silence.
                                var segment = segments[segmentId]; M1Paths.Resolve(directory, relative);
                                if (frames != 0 || bytesCount != 0 || !relative.StartsWith("sources/", StringComparison.Ordinal) ||
                                    !relative.EndsWith(".wav", StringComparison.Ordinal) || !paths.Add(relative)) throw new InvalidDataException("无packet的checkpoint块不是合法空块。");
                                chunks.Add(id, chunk = new(id, relative, segment));
                            }
                            if (segmentId != chunk.Segment.Id || relative != chunk.Path || frames != chunk.Frames || bytesCount != checked(frames * chunk.Segment.Format.BlockAlign) || dataOffset < 44 || dataOffset > MaximumLineBytes)
                                throw new InvalidDataException("checkpoint 没有对应完整连续 packet 索引。");
                            candidate.Add(id, new(frames, bytesCount, dataOffset, hash, chunk.Packets.Count));
                        }
                        if (candidate.Count != chunks.Count) throw new InvalidDataException("checkpoint 遗漏 chunk。");
                        checkpoint = candidate; lastCheckpoint = expected; checkpointDuration = Math.Max(duration, checked(observed * 10)); previousObservationUs = observed;
                        checkpointHasObservedTime = hasObservedTime;
                        break;
                    }
                    default: throw new InvalidDataException("未知索引行类型。");
                }
                expected++;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or ArgumentException or OverflowException or InvalidOperationException or DecoderFallbackException or FormatException or NotSupportedException)
        { issues.Add($"索引在 sequence {expected} 停止：{ex.Message}"); }
        if (checkpoint is null) issues.Add("没有有效 checkpoint，未确认的尾部不会恢复。");
        else if (expected - 1 != lastCheckpoint) issues.Add("checkpoint 之后存在未确认尾部，已排除。");
        if (checkpoint is not null && !checkpointHasObservedTime)
            issues.Add("旧checkpoint缺少observedSessionUs；仅恢复包/边界已确认时长，不能确认Pause/无callback经过时间。");
        duration = checkpointDuration;
        var tracks = new List<CapturedTrack>();
        foreach (var (id, water) in checkpoint ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = chunks[id];
            try
            {
                string file = ResolveChunk(directory, chunk.Path);
                var wave = InspectWave(file, chunk.Segment.Format, chunk.Segment.ValidBits);
                if (wave.DataOffset != water.Offset) throw new InvalidDataException("WAV数据偏移与checkpoint不同。");
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length < checked(water.Offset + water.Bytes)) throw new InvalidDataException("WAV短于已确认水位，存在已保存数据损失。");
                stream.Position = water.Offset;
                if (HashBytes(stream, water.Bytes, cancellationToken) != water.Hash) throw new InvalidDataException("已确认PCM哈希不匹配，存在损坏。");
                tracks.Add(new(chunk.Segment.SourceId, file, chunk.Segment.Format, water.Frames, chunk.Packets.Take(water.PacketCount).ToArray()));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
            { issues.Add($"{id} 未恢复：{ex.Message}"); }
        }
        bool clean = issues.Count == 0 && checkpoint is not null;
        string manifest = M1Paths.Resolve(directory, "meeting.json");
        if (File.Exists(manifest))
        {
            try
            {
                if (new FileInfo(manifest).Length > 8L * 1024 * 1024) throw new InvalidDataException("manifest超过大小上限。");
                using var document = JsonDocument.Parse(File.ReadAllBytes(manifest)); RejectDuplicates(document.RootElement);
                var m = document.RootElement;
                if (S(m, "schemaVersion") != "m1-recording-1" || U(m, "epochQpc100ns") != epoch || m.GetProperty("finalCheckpointSequence").GetInt64() != lastCheckpoint)
                    throw new InvalidDataException("manifest 与最终 checkpoint 不一致。");
                long savedDuration = Nonnegative(m, "duration100ns");
                if (savedDuration < checkpointDuration) throw new InvalidDataException("manifest 截短时间轴。");
                if ((!checkpointHasObservedTime && savedDuration != checkpointDuration) ||
                    (checkpointHasObservedTime && savedDuration - checkpointDuration > 9))
                    throw new InvalidDataException("manifest 时长超出checkpoint已确认水位及微秒取整余量。");
                // Stage metadata until every field and final file has passed verification.
                // A malformed manifest must never widen the event recovery cutoff.
                var manifestWarnings = m.GetProperty("warnings").EnumerateArray().Select(w => w.GetString() ?? "").ToArray();
                var listed = m.GetProperty("chunks").EnumerateArray().ToArray();
                if (listed.Length != checkpoint?.Count) throw new InvalidDataException("manifest chunk 数不一致。");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in listed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string id = S(entry, "ChunkId"); if (!seen.Add(id)) throw new InvalidDataException("manifest重复chunk。");
                    var chunk = chunks[id]; var water = checkpoint![id];
                    if (S(entry, "Path") != chunk.Path || S(entry, "SegmentId") != chunk.Segment.Id || S(entry, "SourceId") != chunk.Segment.SourceId ||
                        entry.GetProperty("FrameCount").GetInt64() != water.Frames || entry.GetProperty("PcmBytes").GetInt64() != water.Bytes ||
                        entry.GetProperty("DataOffsetBytes").GetInt64() != water.Offset || HashString(entry, "PcmSha256") != water.Hash)
                        throw new InvalidDataException("manifest chunk 元数据不一致。");
                    if (entry.GetProperty("Format").Deserialize<PcmFormat>() != chunk.Segment.Format) throw new InvalidDataException("manifest 格式与segment不同。");
                    string file = M1Paths.Resolve(directory, chunk.Path);
                    using var stream = File.OpenRead(file);
                    if (HashBytes(stream, stream.Length, cancellationToken) != HashString(entry, "FileSha256")) throw new InvalidDataException("最终WAV文件hash不一致。");
                }
                bool storageComplete = m.GetProperty("storageComplete").GetBoolean();
                duration = savedDuration;
                captureWarnings.AddRange(manifestWarnings);
                clean &= storageComplete && segments.Values.All(s => s.Closed);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
            { clean = false; issues.Add("manifest 验证失败：" + ex.Message); }
        }
        else { clean = false; issues.Add("缺少已完成 manifest；仅可恢复确认的 PCM。"); }
        var eventPrefix = ReadEvents(directory, Math.Max(0, duration), synthetic, cancellationToken);
        issues.AddRange(eventPrefix.Issues);
        clean &= eventPrefix.Issues.Count == 0;
        // IsCleanEnd describes verified storage. The legacy Complete flag must not
        // silently promote missing/invalid device clocks to fully verified capture.
        bool legacyClockComplete = !segments.Values.Any(segment => segment.HasTimelineGap) && tracks.Count > 0 && tracks.All(track => track.FrameCount > 0 &&
            track.Packets.All(packet => packet.TimestampValid && packet.DevicePositionValid && (packet.Flags & 5) == 0));
        if (clean && !legacyClockComplete)
            captureWarnings.Add("原件及索引已完成验证；旧式完整捕获/设备时钟条件未通过，Complete仍为false。");
        return new(new(directory, index, started, epoch, Math.Max(0, duration), tracks, clean && legacyClockComplete,
            captureWarnings.Concat(issues).ToArray()), checkpoint is not null, clean, issues, lastCheckpoint);
    }

    public static CaptureResult RecoverTo(string sourceDirectory, string destinationDirectory)
    {
        var recovered = Read(sourceDirectory);
        if (!recovered.HasCheckpoint) throw new InvalidDataException("没有通过校验的可恢复checkpoint。");
        string target = Path.GetFullPath(destinationDirectory), source = Path.GetFullPath(sourceDirectory);
        M1Paths.NoReparseAncestors(target);
        if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase) || target.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("恢复副本不能写进原件目录。");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException("恢复目标必须为空。");
        Directory.CreateDirectory(target);
        var resultTracks = new List<CapturedTrack>();
        foreach (var track in recovered.Capture.Tracks)
        {
            var relative = Path.GetRelativePath(source, track.WavePath).Replace('\\', '/');
            if (relative.EndsWith(".partial", StringComparison.Ordinal)) relative = relative[..^8];
            var destination = M1Paths.Resolve(target, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var info = InspectWave(track.WavePath, track.Format, null);
            using (var input = File.OpenRead(track.WavePath))
            using (var output = new CaptureWaveFile(destination + ".partial", info.Format))
            {
                input.Position = info.DataOffset; long left = checked(track.FrameCount * track.Format.BlockAlign); var buffer = new byte[65536];
                while (left != 0) { int count = input.Read(buffer, 0, (int)Math.Min(left, buffer.Length)); if (count == 0) throw new EndOfStreamException(); output.Writer.Write(buffer, 0, count); left -= count; }
                output.Writer.Flush(); output.Stream.Flush(true);
            }
            File.Move(destination + ".partial", destination, false);
            resultTracks.Add(track with { WavePath = destination });
        }
        // Preserve evidence exactly; journal remains the source journal, and recovered result explicitly lists only verified files.
        Directory.CreateDirectory(M1Paths.Resolve(target, "journal"));
        string targetIndex = M1Paths.Resolve(target, "journal/capture-index.jsonl");
        using (var stream = new FileStream(targetIndex, FileMode.CreateNew, FileAccess.Write))
        {
            long sequence = 0;
            foreach (var line in Lines(recovered.Capture.IndexPath))
            { stream.Write(line); stream.WriteByte(10); if (sequence++ == recovered.LastCheckpointSequence) break; }
            stream.Flush(true);
        }
        // Events are separate from the authoritative packet clock. Preserve their exact,
        // verified prefix only up to the confirmed elapsed duration, never an untrusted tail.
        var eventPrefix = ReadEvents(source, recovered.Capture.Duration100ns, null);
        using (var stream = new FileStream(M1Paths.Resolve(target, "journal/events.jsonl"), FileMode.CreateNew, FileAccess.Write))
        {
            foreach (var line in eventPrefix.Lines) { stream.Write(line); stream.WriteByte(10); }
            stream.Flush(true);
        }
        var result = recovered.Capture with { SessionDirectory = target, IndexPath = targetIndex, Tracks = resultTracks,
            Complete = false, Warnings = recovered.Capture.Warnings.Concat(eventPrefix.Issues).Concat(["这是恢复副本；原件未修改，未确认尾部没有拼入。"] ).Distinct(StringComparer.Ordinal).ToArray() };
        M1Storage.WriteAtomicJson(M1Paths.Resolve(target, "recovery-result.json"), new { schemaVersion = "m1-recovery-1", status = "recovered", result });
        return result;
    }

    private static (IReadOnlyList<byte[]> Lines, IReadOnlyList<string> Issues) ReadEvents(string directory, long confirmedDuration100ns, bool? expectedSynthetic, CancellationToken cancellationToken = default)
    {
        var accepted = new List<byte[]>(); var issues = new List<string>(); long sequence = 0, lastUs = -1;
        try
        {
            string path = M1Paths.Resolve(directory, "journal/events.jsonl");
            if (!File.Exists(path)) throw new InvalidDataException("缺少events索引，无法确认书签事件。");
            if (new FileInfo(path).Length > 16L * 1024 * 1024) throw new InvalidDataException("events超过16MiB读取上限。");
            bool future = false;
            foreach (var line in Lines(path, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sequence >= 100000) throw new InvalidDataException("events超过10万条读取上限。");
                using var doc = VerifiedLine(line, sequence);
                var root = doc.RootElement; bool synthetic = root.GetProperty("isSynthetic").GetBoolean();
                expectedSynthetic ??= synthetic;
                if (synthetic != expectedSynthetic) throw new InvalidDataException("events合成标识不一致。");
                var data = root.GetProperty("data");
                if (S(data, "type") != "event") throw new InvalidDataException("events包含非event记录。");
                S(data, "operation"); S(data, "requestId"); long us = Nonnegative(data, "sessionUs");
                if (us < lastUs) throw new InvalidDataException("events会话时间倒退。");
                lastUs = us;
                if (us <= confirmedDuration100ns / 10) accepted.Add(line);
                else if (!future) { future = true; issues.Add("events在已确认时间之后存在未确认尾部，已排除。"); }
                sequence++;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or DecoderFallbackException)
        { issues.Add($"events在sequence {sequence}停止：{ex.Message}"); }
        return (accepted, issues);
    }

    private static JsonDocument VerifiedLine(byte[] bytes, long sequence)
    {
        var document = JsonDocument.Parse(bytes);
        try
        {
            var root = document.RootElement; RejectDuplicates(root);
            if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("sequence").GetInt64() != sequence) throw new InvalidDataException("schema/sequence无效。");
            string rawData = root.GetProperty("data").GetRawText();
            // GetRawText retains the exact JSON token spelling, whitespace and escapes. Strict UTF-8 line decode rejects replacement characters.
            if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawData))) != HashString(root, "sha256")) throw new InvalidDataException("data精确字节SHA256不符。");
            return document;
        }
        catch { document.Dispose(); throw; }
    }
    private static IEnumerable<byte[]> Lines(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var line = new MemoryStream(); var utf8 = new UTF8Encoding(false, true); var buffer = new byte[65536];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] == 10)
                {
                    byte[] bytes = line.ToArray(); line.SetLength(0);
                    if (bytes.Length == 0) throw new InvalidDataException("空索引行。");
                    utf8.GetCharCount(bytes); yield return bytes;
                }
                else { if (line.Length >= MaximumLineBytes) throw new InvalidDataException("索引行超过上限。"); line.WriteByte(buffer[i]); }
            }
        }
        if (line.Length != 0) throw new InvalidDataException("索引末行被截断（缺少换行）。");
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) { if (!seen.Add(property.Name)) throw new InvalidDataException("JSON重复属性。"); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
    private static PcmFormat Format(JsonElement data)
    {
        var encoding = S(data, "encoding"); int bits = encoding switch { "float32le" or "pcm_s32le" => 32, "pcm_s16le" => 16, "pcm_s24le" => 24, _ => throw new InvalidDataException("未知PCM编码。") };
        var format = new PcmFormat(data.GetProperty("sampleRate").GetInt32(), data.GetProperty("channels").GetInt32(), bits, encoding == "float32le", data.GetProperty("blockAlign").GetInt32(), data.GetProperty("channelMask").GetUInt32());
        format.Validate(); int validBits = data.GetProperty("validBitsPerSample").GetInt32();
        if (validBits < 1 || validBits > bits || (format.IsFloat && validBits != 32)) throw new InvalidDataException("有效位宽错误。");
        return format;
    }
    private static string Quality(JsonElement data)
    {
        string result = S(data, "timestampQuality");
        if (result is not ("device_referenced" or "qpc_referenced" or "invalid")) throw new InvalidDataException("不支持的时间戳质量。"); return result;
    }
    private static int Flags(JsonElement data)
    {
        int result = 0;
        foreach (var item in data.EnumerateArray()) { int flag = item.GetString() switch { "data_discontinuity" => 1, "silent" => 2, "timestamp_error" => 4, _ => throw new InvalidDataException("未知flag。") }; if ((result & flag) != 0) throw new InvalidDataException("重复flag。"); result |= flag; }
        return result;
    }
    private static string S(JsonElement data, string property) => data.GetProperty(property).GetString() is { Length: > 0 } value ? value : throw new InvalidDataException("空字段：" + property);
    private static ulong U(JsonElement data, string property)
    {
        string value = S(data, property);
        if (value.Any(c => c is < '0' or > '9') || (value.Length > 1 && value[0] == '0') || !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) throw new InvalidDataException("原始位置不是uint64十进制字符串。");
        return number;
    }
    private static ulong? NullableU(JsonElement data, string property) => data.GetProperty(property).ValueKind == JsonValueKind.Null ? null : U(data, property);
    private static long Nonnegative(JsonElement data, string property) { long n = data.GetProperty(property).GetInt64(); if (n < 0) throw new InvalidDataException("负水位/时间。"); return n; }
    private static string HashString(JsonElement data, string property) { string s = S(data, property); if (s.Length != 64 || s.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))) throw new InvalidDataException("SHA256格式错误。"); return s; }
    private static string ResolveChunk(string directory, string relative)
    {
        string final = M1Paths.Resolve(directory, relative), partial = M1Paths.Resolve(directory, relative + ".partial");
        if (File.Exists(final) == File.Exists(partial)) throw new InvalidDataException("chunk final/partial缺失或并存，拒绝猜测原件。");
        return File.Exists(final) ? final : partial;
    }
    private static string HashBytes(Stream stream, long bytes, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536];
        while (bytes > 0) { cancellationToken.ThrowIfCancellationRequested(); int n = stream.Read(buffer, 0, (int)Math.Min(bytes, buffer.Length)); if (n == 0) throw new EndOfStreamException(); hash.AppendData(buffer, 0, n); bytes -= n; }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static (long DataOffset, WaveFormat Format) InspectWave(string file, PcmFormat expected, int? expectedValidBits)
    {
        using var stream = File.OpenRead(file); using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 0x46464952) throw new InvalidDataException("无RIFF标识。"); reader.ReadUInt32();
        if (reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("无WAVE标识。");
        WaveFormat? format = null;
        while (stream.Position + 8 <= stream.Length && stream.Position < MaximumLineBytes)
        {
            uint tag = reader.ReadUInt32(), size = reader.ReadUInt32(); long begin = stream.Position;
            if (tag == 0x61746164) return format is null ? throw new InvalidDataException("data之前没有格式。"): (begin, format);
            if (size > 65536 || begin + size > stream.Length) throw new InvalidDataException("WAV头部范围错误。");
            if (tag == 0x20746d66)
            {
                if (format is not null || size < 16) throw new InvalidDataException("WAV格式重复或截断。");
                byte[] bytes = reader.ReadBytes((int)size);
                int encoding = BinaryPrimitives.ReadUInt16LittleEndian(bytes), channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2));
                int rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)), align = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12)), bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14));
                bool floating = encoding == 3; uint mask = 0; int validBits = bits;
                if (encoding == 65534)
                {
                    if (size < 40 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(16)) < 22) throw new InvalidDataException("Extensible格式截断。");
                    validBits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18)); mask = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20));
                    Guid subtype = new(bytes.AsSpan(24, 16)); floating = subtype == new Guid("00000003-0000-0010-8000-00aa00389b71");
                    if (!floating && subtype != new Guid("00000001-0000-0010-8000-00aa00389b71")) throw new InvalidDataException("未知Extensible子格式。");
                }
                else if (encoding is not (1 or 3)) throw new InvalidDataException("未知WAV编码。");
                if (new PcmFormat(rate, channels, bits, floating, align, mask) != expected || (expectedValidBits is { } v && v != validBits) ||
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) != checked(rate * align)) throw new InvalidDataException("WAV实际格式与segment不同。");
                format = encoding == 65534 ? new WaveFormatExtensible(rate, bits, channels, floating, validBits, unchecked((int)mask))
                    : floating ? WaveFormat.CreateIeeeFloatWaveFormat(rate, channels) : new WaveFormat(rate, bits, channels);
            }
            stream.Position = checked(begin + size + (size & 1));
        }
        throw new InvalidDataException("无WAV数据区。");
    }
    private sealed class Segment(string id, string source, PcmFormat format, int validBits, ulong? qpc, ulong? device, long us, string quality)
    {
        public string Id = id, SourceId = source, AnchorQuality = quality;
        public PcmFormat Format = format; public int ValidBits = validBits;
        public ulong? AnchorQpc = qpc, AnchorDevice = device; public long AnchorUs = us;
        public bool Closed; public long? LastSequence; public CapturedPacket? Last;
        public bool HasInvalidTimestamp, HasTimelineGap;
        public Int128 MaximumEnd;
        public UInt128 MaximumDeviceEnd;
    }
    private sealed class Chunk(string id, string path, Segment segment)
    {
        public string Id = id, Path = path; public Segment Segment = segment;
        public long Frames; public List<CapturedPacket> Packets = [];
    }
    private sealed record Watermark(long Frames, long Bytes, long Offset, string Hash, int PacketCount);
}
