using System.Text.Json;

namespace ProjectEcho.Core.Transcription;

internal sealed record TranscriptMapping(string ExportId, string PartId, string MixHash,
    int SampleRate, IReadOnlyList<TranscriptTimeSpan> Spans, byte[] OriginalManifest)
{
    internal static TranscriptMapping Read(TranscriptImportRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.InputPartId) || !TranscriptFiles.IsHash(request.ExpectedMixSha256))
            throw new InvalidDataException("分片导入必须选择 part 文件名，并提供当前完整混音 SHA-256 以核对会议归属。");
        byte[] bytes = TranscriptFiles.ReadBounded(request.ExportManifestPath!, token);
        using var document = TranscriptFiles.ParseJson(bytes); var root = document.RootElement;
        if (root.TryGetProperty("export", out var export)) root = export;
        if (root.GetProperty("schemaVersion").GetString() != "m2-export-1" ||
            !string.Equals(root.GetProperty("sourceMixSha256").GetString(), request.ExpectedMixSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("分片清单版本不支持，或与当前会议完整混音 SHA-256 不符。");
        int rate = root.GetProperty("sampleRate").GetInt32(); long totalFrames = root.GetProperty("completeTimelineFrames").GetInt64();
        if (rate < 8000 || rate > 192000 || totalFrames <= 0 || totalFrames > TranscriptFiles.MaximumDurationMs * rate / 1000 ||
            Math.Abs(decimal.Ceiling(totalFrames * 1000m / rate) - request.MeetingDurationMs) > 1)
            throw new InvalidDataException("分片清单总时长或采样率不符。");
        var all = root.GetProperty("parts");
        if (all.GetArrayLength() is < 1 or > 10000) throw new InvalidDataException("分片清单数量无效。");
        var matches = all.EnumerateArray().Where(p => p.GetProperty("file").GetString() == request.InputPartId).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("分片标识未找到或重复。");
        var selected = matches[0]; var spans = new List<TranscriptTimeSpan>(); long previousFileEnd = -1, previousSourceEnd = -1;
        long startFrame = selected.GetProperty("startFrame").GetInt64(), endFrame = selected.GetProperty("endFrame").GetInt64();
        if (startFrame < 0 || endFrame <= startFrame || endFrame > totalFrames) throw new InvalidDataException("分片范围无效。");
        foreach (var item in selected.GetProperty("spans").EnumerateArray())
        {
            long file = item.GetProperty("fileStartFrame").GetInt64(), source = item.GetProperty("sourceStartFrame").GetInt64(), count = item.GetProperty("frameCount").GetInt64();
            if (file < 0 || source < startFrame || count <= 0 || count > totalFrames || source > endFrame - count || file > TranscriptFiles.MaximumDurationMs * rate / 1000 - count ||
                file < previousFileEnd || source < previousSourceEnd || spans.Count >= 10000)
                throw new InvalidDataException("分片映射倒置、重叠或越界。");
            spans.Add(new(file, source, count)); previousFileEnd = file + count; previousSourceEnd = source + count;
        }
        if (spans.Count == 0 || spans[0].SourceStartFrame != startFrame || spans[^1].SourceStartFrame + spans[^1].FrameCount != endFrame)
            throw new InvalidDataException("分片映射没有覆盖声明的首尾。");
        return new(TranscriptFiles.Hash(System.Text.Encoding.UTF8.GetBytes(root.GetRawText())), request.InputPartId,
            request.ExpectedMixSha256!.ToLowerInvariant(), rate, spans, bytes);
    }

    internal TranscriptSegment Apply(TranscriptSegment segment)
    {
        if (segment.StartMs is null) return segment;
        decimal localStart = segment.StartMs.Value * SampleRate / 1000m, localEnd = segment.EndMs!.Value * SampleRate / 1000m;
        decimal? sourceStart = null, sourceEnd = null; decimal cursor = localStart; long? offset = null;
        foreach (var span in Spans)
        {
            long fileEnd = span.FileStartFrame + span.FrameCount;
            if (fileEnd <= cursor || span.FileStartFrame >= localEnd) continue;
            if (span.FileStartFrame > cursor) throw new InvalidDataException("字幕跨越未映射的音频区间，不能推测全局时间。");
            long currentOffset = span.SourceStartFrame - span.FileStartFrame;
            if (offset is not null && offset != currentOffset) throw new InvalidDataException("字幕跨越不连续的会话映射，需先拆开该段。");
            offset = currentOffset; sourceStart ??= cursor + currentOffset; cursor = Math.Min(localEnd, fileEnd); sourceEnd = cursor + currentOffset;
            if (cursor >= localEnd) break;
        }
        if (cursor != localEnd || sourceStart is null || sourceEnd is null) throw new InvalidDataException("字幕时间超出该分片有效音频映射（包括编码前后填充）。");
        return segment with { StartMs = (long)decimal.Floor(sourceStart.Value * 1000 / SampleRate),
            EndMs = (long)decimal.Ceiling(sourceEnd.Value * 1000 / SampleRate), TimestampQuality = "approximate" };
    }
}
