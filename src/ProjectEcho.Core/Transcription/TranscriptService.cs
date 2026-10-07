using System.Text.Json;

namespace ProjectEcho.Core.Transcription;

/// <summary>Local, immutable transcript revisions. This service has no capture, playback or network dependencies.</summary>
public static class TranscriptService
{
    public static TranscriptRevision Import(TranscriptImportRequest request, CancellationToken cancellationToken = default)
        => ImportCore(request, null, cancellationToken);

    internal static TranscriptRevision ImportWhisper(TranscriptImportRequest request, string tokenJsonPath,
        CancellationToken cancellationToken = default, WhisperRequest? audioRequest = null, long frameCount = 0)
        => ImportCore(request, tokenJsonPath, cancellationToken, audioRequest, frameCount);

    internal static TranscriptRevision ImportAligned(LocalTranscriptionRequest request, string originalText,
        IReadOnlyList<TranscriptSegment> segments, byte[] alignmentEvidence, long frameCount,
        string alignmentPolicy, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string meeting = TranscriptFiles.Meeting(request.MeetingDirectory);
        TranscriptFiles.ValidateIdentity(request.MeetingId, request.MeetingDurationMs);
        TranscriptFiles.ValidateSegments(segments, request.MeetingDurationMs);
        if (segments.Any(segment => segment.StartMs is null || segment.TimestampQuality != "approximate") ||
            !TranscriptFiles.IsHash(request.ExpectedWaveSha256) ||
            (request.SourceMixSha256 is not null && !TranscriptFiles.IsHash(request.SourceMixSha256)) ||
            request.TimelinePolicy is not ("elapsed-v1" or "pause-excluded-v1" or "file-v1") ||
            frameCount <= 0 || Math.Abs(frameCount * 1000m / 16000 - request.MeetingDurationMs) > 1 ||
            alignmentEvidence.Length > 32 * 1024 * 1024)
            throw new InvalidDataException("音文对齐来源、帧覆盖或时间质量无效，未保存成功稿。");
        byte[] original = System.Text.Encoding.UTF8.GetBytes(originalText);
        if (original.Length == 0 || original.Length > TranscriptFiles.MaximumFileBytes)
            throw new InvalidDataException("原始模型文字为空或超限。");
        using var evidence = TranscriptFiles.ParseJson(alignmentEvidence);
        string id = Guid.NewGuid().ToString("N");
        var provenance = new TranscriptAlignmentProvenance($"transcripts/original/{id}/alignment.json",
            TranscriptFiles.Hash(alignmentEvidence), request.ExpectedWaveSha256, request.SourceMixSha256,
            16000, frameCount, request.TimelinePolicy, alignmentPolicy);
        var revision = new TranscriptRevision(2, request.IsSynthetic, request.MeetingId, id, null,
            "Qwen3-ASR / transcribe.cpp（本地，待人工校对）", request.Configuration.ModelName,
            DateTimeOffset.UtcNow, request.MeetingDurationMs, null, null, null, null, [],
            $"transcripts/original/{id}/original.txt", TranscriptFiles.Hash(original), null, segments,
            ["句子时间来自独立 Qwen3-ForcedAligner 的声学采样边界，为 approximate；对齐不证明文字正确。",
             "对齐证据绑定本版原稿与输入音频；人工修改文字后，沿用的回听时间须结合原音复核。"], provenance);
        Save(meeting, "original", revision, original, ".txt", null, token, alignmentEvidence);
        return revision;
    }

    internal static TranscriptRevision ImportQwenUnaligned(LocalTranscriptionRequest request, string originalText,
        IReadOnlyList<TranscriptSegment> segments, byte[] alignmentEvidence, long frameCount,
        IReadOnlyList<string> timingWarnings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string meeting = TranscriptFiles.Meeting(request.MeetingDirectory);
        TranscriptFiles.ValidateIdentity(request.MeetingId, request.MeetingDurationMs);
        TranscriptFiles.ValidateSegments(segments, request.MeetingDurationMs);
        if (!segments.Any(segment => segment.StartMs is null) ||
            !TranscriptFiles.IsHash(request.ExpectedWaveSha256) ||
            request.SourceMixSha256 is not null && !TranscriptFiles.IsHash(request.SourceMixSha256) ||
            request.TimelinePolicy is not ("elapsed-v1" or "pause-excluded-v1" or "file-v1") ||
            frameCount <= 0 || Math.Abs(frameCount * 1000m / 16000 - request.MeetingDurationMs) > 1 ||
            alignmentEvidence.Length > 32 * 1024 * 1024 || timingWarnings.Count > 102)
            throw new InvalidDataException("Qwen 待定位稿的音频绑定、时间质量或诊断无效。");
        byte[] original = new System.Text.UTF8Encoding(false, true).GetBytes(originalText);
        if (original.Length == 0 || original.Length > TranscriptFiles.MaximumFileBytes)
            throw new InvalidDataException("原始模型文字为空或超限。");
        using var evidence = TranscriptFiles.ParseJson(alignmentEvidence);
        string id = Guid.NewGuid().ToString("N");
        var provenance = new TranscriptAlignmentProvenance($"transcripts/original/{id}/alignment.json",
            TranscriptFiles.Hash(alignmentEvidence), request.ExpectedWaveSha256, request.SourceMixSha256,
            16000, frameCount, request.TimelinePolicy, QwenTranscriber.PartialAlignmentPolicy);
        string[] warnings =
        [
            "Qwen 识别文字已完整保存；部分窗口声学时间未通过校验，保留全文为 untimed，未猜测回听位置。",
            "正常窗口仍保留已验证句子时间；待定位段可查看、搜索和导出 TXT，不能生成遗漏文字的同步字幕。",
            .. timingWarnings
        ];
        var revision = new TranscriptRevision(2, request.IsSynthetic, request.MeetingId, id, null,
            "Qwen3-ASR / transcribe.cpp（本地，待人工校对）", request.Configuration.ModelName,
            DateTimeOffset.UtcNow, request.MeetingDurationMs, null, null, null, null, [],
            $"transcripts/original/{id}/original.txt", TranscriptFiles.Hash(original), null, segments,
            warnings.Distinct().ToArray(), provenance);
        QwenPartialTranscriptEvidence.Validate(evidence.RootElement, revision, originalText);
        token.ThrowIfCancellationRequested();
        Save(meeting, "original", revision, original, ".txt", null, token, alignmentEvidence);
        return revision;
    }

    private static TranscriptRevision ImportCore(TranscriptImportRequest request, string? whisperTokenJsonPath,
        CancellationToken cancellationToken, WhisperRequest? audioRequest = null, long frameCount = 0)
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
            // Only the local Whisper adapter may preserve unsupported per-cue times as untimed.
            // The original token evidence and M5 audio/timeline binding remain unchanged below.
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
        byte[]? tokenEvidence = null; TranscriptAlignmentProvenance? tokenProvenance = null;
        if (audioRequest is not null && whisperTokenJsonPath is not null && File.Exists(whisperTokenJsonPath))
        {
            if (!TranscriptFiles.IsHash(audioRequest.ExpectedWaveSha256) ||
                audioRequest.SourceMixSha256 is not null && !TranscriptFiles.IsHash(audioRequest.SourceMixSha256) ||
                audioRequest.TimelinePolicy is not ("elapsed-v1" or "pause-excluded-v1" or "file-v1") ||
                frameCount <= 0 || Math.Abs(frameCount * 1000m / 16000 - request.MeetingDurationMs) > 1)
                throw new InvalidDataException("Whisper token 音频绑定无效。");
            // Missing/oversized token evidence must not invalidate usable sentence subtitles.
            if (new FileInfo(whisperTokenJsonPath).Length <= 32 * 1024 * 1024)
            {
                try
                {
                    tokenEvidence = TranscriptFiles.ReadBounded(whisperTokenJsonPath, cancellationToken, 32 * 1024 * 1024);
                    using var parsed = TranscriptFiles.ParseJson(tokenEvidence);
                    tokenProvenance = new($"transcripts/original/{id}/alignment.json", TranscriptFiles.Hash(tokenEvidence),
                        audioRequest.ExpectedWaveSha256, audioRequest.SourceMixSha256, 16000, frameCount,
                        audioRequest.TimelinePolicy, "whisper-exact-token-boundaries-v1");
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException)
                { tokenEvidence = null; warnings.Add("token JSON 不可验证；保留句级字幕，句内人物换人待确认。"); }
            }
            else warnings.Add("完整 token JSON 超过持久证据上限；保留句级字幕，句内人物换人待确认。");
        }
        var revision = new TranscriptRevision(2, request.IsSynthetic, request.MeetingId, id, null,
            request.ProviderName.Trim(), request.ModelName, DateTimeOffset.UtcNow, request.MeetingDurationMs,
            mapping?.ExportId, mapping?.PartId, mapping?.MixHash, mapping?.SampleRate, mapping?.Spans ?? [],
            relativeOriginal, TranscriptFiles.Hash(original), null, segments, warnings.Distinct().ToArray(), tokenProvenance);
        Save(meeting, "original", revision, original, extension, mapping?.OriginalManifest, cancellationToken, tokenEvidence);
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

    /// <summary>Save one explicit batch of text/time corrections while keeping provenance immutable.</summary>
    public static TranscriptRevision CreateEditedRevision(string meetingDirectory, string parentRevisionId,
        IReadOnlyList<TranscriptSegment> segments, string reviewNote = "", CancellationToken cancellationToken = default)
    {
        string meeting = TranscriptFiles.Meeting(meetingDirectory);
        var parent = Load(meeting, parentRevisionId, cancellationToken);
        if (reviewNote is null || reviewNote.Length > 10000) throw new InvalidDataException("修订备注超过一万字符。");
        TranscriptFiles.ValidateSegments(segments, parent.MeetingDurationMs);
        if (segments.Count != parent.Segments.Count) throw new InvalidDataException("修订不能增删字幕段。");
        for (int index = 0; index < segments.Count; index++)
        {
            var segment = segments[index]; var original = parent.Segments[index];
            if (segment with { Text = original.Text, StartMs = original.StartMs, EndMs = original.EndMs,
                    TimestampQuality = original.TimestampQuality } != original)
                throw new InvalidDataException("修订只修改文字和起止时间，段标识、来源与说话人保持原样。");
            string quality = segment.StartMs is null ? "untimed" :
                segment.StartMs == original.StartMs && segment.EndMs == original.EndMs ? original.TimestampQuality : "approximate";
            if (segment.TimestampQuality != quality) throw new InvalidDataException("人工修改时间仍需结合原录音核对，不能自动标记为已验证。");
        }
        if (segments.SequenceEqual(parent.Segments)) throw new InvalidDataException("没有尚未保存的字幕修改。");
        var revision = parent with { RevisionId = Guid.NewGuid().ToString("N"), ParentRevisionId = parent.RevisionId,
            ImportedAt = DateTimeOffset.UtcNow, ReviewNote = reviewNote, Segments = segments.ToArray() };
        Save(meeting, "reviewed", revision, null, null, null, cancellationToken);
        return revision;
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
        if (revision.Alignment is { } alignment)
        {
            string expectedPrefix = Path.GetDirectoryName(revision.OriginalFile)!.Replace('\\', '/') + "/";
            if (alignment.EvidenceFile != expectedPrefix + "alignment.json" || !TranscriptFiles.IsHash(alignment.EvidenceSha256) ||
                !TranscriptFiles.IsHash(alignment.InputWaveSha256) ||
                (alignment.SourceMixSha256 is not null && !TranscriptFiles.IsHash(alignment.SourceMixSha256)) ||
                alignment.SampleRate != 16000 || alignment.FrameCount <= 0 ||
                Math.Abs(alignment.FrameCount * 1000m / alignment.SampleRate - revision.MeetingDurationMs) > 1 ||
                alignment.TimelinePolicy is not ("elapsed-v1" or "pause-excluded-v1" or "file-v1") ||
                string.IsNullOrWhiteSpace(alignment.Policy)) throw new InvalidDataException("句子定位来源无效。");
            byte[] evidence = TranscriptFiles.ReadBounded(TranscriptFiles.Inside(meeting, alignment.EvidenceFile), cancellationToken, 32 * 1024 * 1024);
            if (TranscriptFiles.Hash(evidence) != alignment.EvidenceSha256)
                throw new InvalidDataException("句子定位证据 SHA-256 不一致；原稿与声音请保留检查。");
        }
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
        string? extension, byte[]? mapping, CancellationToken token, byte[]? alignment = null)
    {
        string root = TranscriptFiles.Inside(meeting, "transcripts/" + kind); Directory.CreateDirectory(root);
        string pending = TranscriptFiles.Inside(root, ".pending-" + revision.RevisionId), final = TranscriptFiles.Inside(root, revision.RevisionId);
        Directory.CreateDirectory(pending);
        // A unique staging folder is retained on disk failure/cancellation; never overwrite/delete a previous version.
        if (original is not null) TranscriptFiles.WriteNew(TranscriptFiles.Inside(pending, "original" + extension), original);
        if (mapping is not null) TranscriptFiles.WriteNew(TranscriptFiles.Inside(pending, "source-export-manifest.json"), mapping);
        if (alignment is not null) TranscriptFiles.WriteNew(TranscriptFiles.Inside(pending, "alignment.json"), alignment);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(revision, TranscriptFiles.Json);
        if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("规范化转写超过 32 MiB。");
        TranscriptFiles.WriteNew(TranscriptFiles.Inside(pending, "revision.json"), bytes);
        token.ThrowIfCancellationRequested(); TranscriptFiles.NoReparse(final); Directory.Move(pending, final);
    }
}
