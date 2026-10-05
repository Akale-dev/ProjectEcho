using System.Text.Json;

namespace ProjectEcho.Core.Transcription;

/// <summary>Local, immutable transcript revisions. This service has no capture, playback or network dependencies.</summary>
public static class TranscriptService
{
    public static TranscriptRevision Import(TranscriptImportRequest request, CancellationToken cancellationToken = default)
        => ImportCore(request, null, cancellationToken);

    internal static TranscriptRevision ImportWhisper(TranscriptImportRequest request, string tokenJsonPath,
        CancellationToken cancellationToken = default) => ImportCore(request, tokenJsonPath, cancellationToken);

    private static TranscriptRevision ImportCore(TranscriptImportRequest request, string? whisperTokenJsonPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        string meeting = TranscriptFiles.Meeting(request.MeetingDirectory);
        TranscriptFiles.ValidateIdentity(request.MeetingId, request.MeetingDurationMs);
        if (string.IsNullOrWhiteSpace(request.ProviderName) || request.ProviderName.Length > 200 || request.ModelName?.Length > 200)
            throw new InvalidDataException("来源名称必须为 1–200 字符，模型名称不超过 200 字符。");
        string extension = Path.GetExtension(request.InputPath).ToLowerInvariant();
        if (extension is not (".txt" or ".md" or ".srt" or ".vtt" or ".json")) throw new InvalidDataException("只接受 TXT、MD、SRT、VTT 或 ProjectEcho JSON。");
        byte[] original = TranscriptFiles.ReadBounded(request.InputPath, cancellationToken);
        var warnings = new List<string>();
        var segments = TranscriptParser.Parse(original, extension, request.MeetingId, warnings);
        if (whisperTokenJsonPath is not null)
        {
            // The adapter validates every field before processing; only its narrowly defined
            // per-cue zero-duration and overrun policies can preserve text with unsupported times.
            segments = WhisperSentenceSegmenter.Normalize(segments, whisperTokenJsonPath,
                request.MeetingDurationMs, warnings, cancellationToken);
            segments = WhisperTimestampBoundary.Normalize(segments, request.MeetingDurationMs, warnings);
        }
        // Local subtitle ranges are checked before and after applying a manifest. Untimed text stays untimed.
        TranscriptFiles.ValidateSegments(segments, TranscriptFiles.MaximumDurationMs);
        TranscriptMapping? mapping = null;
        if (request.ExportManifestPath is not null)
        {
            if (extension == ".json")
            {
                using var document = TranscriptFiles.ParseJson(original);
                if (document.RootElement.GetProperty("schemaVersion").GetInt32() == 2)
                    throw new InvalidDataException("v2 转写已是会话时间，不能再次套用分片映射。");
            }
            mapping = TranscriptMapping.Read(request, cancellationToken);
            segments = segments.Select(mapping.Apply).ToList();
            warnings.Add("已按当前完整混音哈希核对的分片清单映射；外部字幕时间仍为 approximate。不同分片同名 Speaker 不代表同一个人。");
        }
        else if (request.InputPartId is not null) throw new InvalidDataException("分片标识必须与导出映射清单一起提供。");
        TranscriptFiles.ValidateSegments(segments, request.MeetingDurationMs);
        if (segments.Any(s => s.StartMs is null)) warnings.Add("无时间文本标记 untimed；搜索可用，不能凭文本长度推测回放位置。");
        long lastEnd = -1;
        foreach (var segment in segments.Where(s => s.StartMs is not null).OrderBy(s => s.StartMs))
        {
            if (segment.StartMs < lastEnd) { warnings.Add("含有重叠字幕；所有原文均保留，不自动去重。"); break; }
            lastEnd = Math.Max(lastEnd, segment.EndMs!.Value);
        }
        string id = Guid.NewGuid().ToString("N"), relativeOriginal = $"transcripts/original/{id}/original{extension}";
        var revision = new TranscriptRevision(2, request.IsSynthetic, request.MeetingId, id, null,
            request.ProviderName.Trim(), request.ModelName, DateTimeOffset.UtcNow, request.MeetingDurationMs,
            mapping?.ExportId, mapping?.PartId, mapping?.MixHash, mapping?.SampleRate, mapping?.Spans ?? [],
            relativeOriginal, TranscriptFiles.Hash(original), null, segments, warnings.Distinct().ToArray());
        Save(meeting, "original", revision, original, extension, mapping?.OriginalManifest, cancellationToken);
        return revision;
    }

    public static TranscriptRevision CreateRevision(string meetingDirectory, string parentRevisionId,
        IReadOnlyList<TranscriptSegment> segments, string reviewNote = "", CancellationToken cancellationToken = default)
    {
        string meeting = TranscriptFiles.Meeting(meetingDirectory);
        var parent = Load(meeting, parentRevisionId, cancellationToken);
        if (reviewNote is null || reviewNote.Length > 10000) throw new InvalidDataException("修订备注超过一万字符。");
        TranscriptFiles.ValidateSegments(segments, parent.MeetingDurationMs);
        if (segments.Count != parent.Segments.Count || segments.Where((s, i) => s with { Text = parent.Segments[i].Text } != parent.Segments[i]).Any())
            throw new InvalidDataException("本版修订只修改文字，段标识、时间、来源与说话人保持原样。");
        var revision = parent with { RevisionId = Guid.NewGuid().ToString("N"), ParentRevisionId = parent.RevisionId,
            ImportedAt = DateTimeOffset.UtcNow, ReviewNote = reviewNote, Segments = segments.ToArray() };
        Save(meeting, "reviewed", revision, null, null, null, cancellationToken); return revision;
    }

    public static TranscriptRevision Load(string meetingDirectory, string revisionId, CancellationToken cancellationToken = default)
    {
        string meeting = TranscriptFiles.Meeting(meetingDirectory);
        if (!Guid.TryParseExact(revisionId, "N", out _)) throw new InvalidDataException("转写版本标识无效。");
        string[] paths = new[] { "original", "reviewed" }.Select(kind => TranscriptFiles.Inside(meeting, $"transcripts/{kind}/{revisionId}/revision.json")).Where(File.Exists).ToArray();
        if (paths.Length != 1) throw new InvalidDataException("转写版本未找到或冲突。");
        byte[] bytes = TranscriptFiles.ReadBounded(paths[0], cancellationToken, 32 * 1024 * 1024);
        using var document = TranscriptFiles.ParseJson(bytes);
        var revision = JsonSerializer.Deserialize<TranscriptRevision>(document.RootElement.GetRawText(), TranscriptFiles.Json)
            ?? throw new InvalidDataException("转写版本为空。");
        if (revision.SchemaVersion != 2 || revision.RevisionId != revisionId || revision.OriginalFile is null ||
            !revision.OriginalFile.StartsWith("transcripts/original/", StringComparison.Ordinal) || !TranscriptFiles.IsHash(revision.OriginalSha256))
            throw new InvalidDataException("转写版本元数据无效。");
        TranscriptFiles.ValidateIdentity(revision.MeetingId, revision.MeetingDurationMs);
        TranscriptFiles.ValidateSegments(revision.Segments, revision.MeetingDurationMs);
        if (revision.ParentRevisionId is not null && (!Guid.TryParseExact(revision.ParentRevisionId, "N", out _) || revision.ParentRevisionId == revisionId))
            throw new InvalidDataException("修订父版本无效。");
        if (revision.SourceTimeMapping is null || revision.Warnings is null || revision.SourceTimeMapping.Count > 10000 || revision.Warnings.Count > 10000)
            throw new InvalidDataException("转写映射/提示元数据无效。");
        string original = TranscriptFiles.Inside(meeting, revision.OriginalFile);
        if (TranscriptFiles.Hash(TranscriptFiles.ReadBounded(original, cancellationToken)) != revision.OriginalSha256)
            throw new InvalidDataException("原始转写 SHA-256 不一致；请保留现场检查损坏，不覆盖原稿。");
        return revision;
    }

    public static IReadOnlyList<TranscriptRevision> ListRevisions(string meetingDirectory, CancellationToken cancellationToken = default)
    {
        string meeting = TranscriptFiles.Meeting(meetingDirectory); var revisions = new List<TranscriptRevision>();
        foreach (string kind in new[] { "original", "reviewed" })
        {
            string root = TranscriptFiles.Inside(meeting, "transcripts/" + kind);
            if (!Directory.Exists(root)) continue;
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested(); TranscriptFiles.NoReparse(directory);
                string id = Path.GetFileName(directory);
                if (!Guid.TryParseExact(id, "N", out _)) continue; // Incomplete .pending-* never masquerades as a saved revision.
                if (revisions.Count >= 2000) throw new InvalidDataException("会议转写超过 2000 个版本，请缩小检索范围。");
                revisions.Add(Load(meeting, id, cancellationToken));
            }
        }
        return revisions.OrderByDescending(r => r.ImportedAt).ToArray();
    }

    public static IReadOnlyList<TranscriptSearchHit> Search(string meetingDirectory, string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        if (query.Length > 1000) throw new ArgumentException("搜索词不能超过 1000 字符。", nameof(query));
        return Library.SqliteTranscriptSearch.Search(meetingDirectory, query, cancellationToken);
    }

    public static IReadOnlyList<TranscriptOverlapCandidate> FindOverlapCandidates(string meetingDirectory,
        CancellationToken cancellationToken = default)
    {
        var revisions = ListRevisions(meetingDirectory, cancellationToken).Where(r => r.ParentRevisionId is null &&
            r.InputExportId is not null && r.InputPartId is not null && r.SourceMixSha256 is not null).ToArray();
        var candidates = new List<TranscriptOverlapCandidate>();
        for (int a = 0; a < revisions.Length; a++) for (int b = a + 1; b < revisions.Length; b++)
        {
            var left = revisions[a]; var right = revisions[b]; cancellationToken.ThrowIfCancellationRequested();
            if (left.InputExportId != right.InputExportId || left.InputPartId == right.InputPartId || left.SourceMixSha256 != right.SourceMixSha256) continue;
            var leftSegments = left.Segments.Where(s => s.StartMs is not null).OrderBy(s => s.StartMs).ToArray();
            var rightSegments = right.Segments.Where(s => s.StartMs is not null).OrderBy(s => s.StartMs).ToArray();
            int first = 0;
            foreach (var l in leftSegments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (first < rightSegments.Length && rightSegments[first].EndMs <= l.StartMs) first++;
                for (int i = first; i < rightSegments.Length && rightSegments[i].StartMs < l.EndMs; i++)
                {
                    var r = rightSegments[i]; long start = Math.Max(l.StartMs!.Value, r.StartMs!.Value), end = Math.Min(l.EndMs!.Value, r.EndMs!.Value);
                    if (end <= start) continue;
                    candidates.Add(new(left.RevisionId, l.SegmentId, right.RevisionId, r.SegmentId, start, end, string.Equals(l.Text.Trim(), r.Text.Trim(), StringComparison.Ordinal)));
                    if (candidates.Count == 1000) return candidates;
                }
            }
        }
        return candidates;
    }

    private static void Save(string meeting, string kind, TranscriptRevision revision, byte[]? original,
        string? extension, byte[]? mapping, CancellationToken token)
    {
        string root = TranscriptFiles.Inside(meeting, "transcripts/" + kind); Directory.CreateDirectory(root);
        string pending = TranscriptFiles.Inside(root, ".pending-" + revision.RevisionId), final = TranscriptFiles.Inside(root, revision.RevisionId);
        Directory.CreateDirectory(pending);
        // A unique staging folder is retained on disk failure/cancellation; never overwrite/delete a previous version.
        if (original is not null) TranscriptFiles.WriteNew(TranscriptFiles.Inside(pending, "original" + extension), original);
        if (mapping is not null) TranscriptFiles.WriteNew(TranscriptFiles.Inside(pending, "source-export-manifest.json"), mapping);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(revision, TranscriptFiles.Json);
        if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("规范化转写超过 32 MiB。");
        TranscriptFiles.WriteNew(TranscriptFiles.Inside(pending, "revision.json"), bytes);
        token.ThrowIfCancellationRequested(); TranscriptFiles.NoReparse(final); Directory.Move(pending, final);
    }
}
