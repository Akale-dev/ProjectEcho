using System.Text.Json;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.Core.Diarization;

/// <summary>Immutable meeting-local authority. Staging directories are never exposed as completed work.</summary>
public static partial class SpeakerService
{
    public static string TranscriptFingerprint(TranscriptRevision revision) =>
        TranscriptFiles.Hash(JsonSerializer.SerializeToUtf8Bytes(revision, TranscriptFiles.Json));

    public static SpeakerAnalysis SaveAnalysis(string meetingDirectory, SpeakerAnalysis analysis, CancellationToken cancellationToken = default)
    {
        string meeting = TranscriptFiles.Meeting(meetingDirectory); ValidateAnalysis(analysis); ValidateMeeting(meeting, analysis.MeetingId, analysis.IsSynthetic);
        Save(meeting, "analyses", analysis.AnalysisId, "analysis.json", analysis, cancellationToken); return analysis;
    }

    public static SpeakerAnalysis LoadAnalysis(string meetingDirectory, string analysisId, CancellationToken cancellationToken = default)
    {
        var result = Read<SpeakerAnalysis>(meetingDirectory, "analyses", analysisId, "analysis.json", cancellationToken);
        ValidateAnalysis(result);
        ValidateMeeting(meetingDirectory, result.MeetingId, result.IsSynthetic);
        if (result.AnalysisId != analysisId) throw new InvalidDataException("说话人分析目录与标识不一致。");
        return result;
    }

    public static IReadOnlyList<SpeakerAnalysis> ListAnalyses(string meetingDirectory, CancellationToken cancellationToken = default) =>
        ListIds(meetingDirectory, "analyses").Select(id => LoadAnalysis(meetingDirectory, id, cancellationToken)).OrderByDescending(item => item.CreatedAt).ToArray();

    public static SpeakerRevision CreateRevision(string meetingDirectory, string sourceTranscriptRevisionId, string analysisId,
        CancellationToken cancellationToken = default)
    {
        string meeting = TranscriptFiles.Meeting(meetingDirectory);
        var source = TranscriptService.Load(meeting, sourceTranscriptRevisionId, cancellationToken);
        var analysis = LoadAnalysis(meeting, analysisId, cancellationToken);
        ValidateBinding(source, analysis);
        var mapping = SpeakerAlignmentReader.Read(meeting, source, analysis.Audio, cancellationToken);
        var result = new SpeakerRevision(1, source.MeetingId, Guid.NewGuid().ToString("N"), null, DateTimeOffset.UtcNow,
            source.RevisionId, TranscriptFingerprint(source), analysisId, AnalysisHash(meeting, analysisId, cancellationToken),
            analysis.Audio, source.MeetingDurationMs, analysis.Speakers.ToArray(), SpeakerMapper.Map(source, analysis, mapping, cancellationToken), [],
            analysis.Warnings.Concat(mapping.Warnings).Concat(["人物代号仅限本场声音分组，时间和归属均需核对；没有推断姓名或音源身份。"]).Distinct().ToArray(), source.IsSynthetic || analysis.IsSynthetic);
        ValidateRevision(result, source, analysis);
        Save(meeting, "revisions", result.RevisionId, "revision.json", result, cancellationToken); return result;
    }

    public static SpeakerRevision Load(string meetingDirectory, string revisionId, CancellationToken cancellationToken = default)
        => LoadWithAncestors(meetingDirectory, revisionId, new(StringComparer.Ordinal), cancellationToken);

    private static SpeakerRevision LoadWithAncestors(string meetingDirectory, string revisionId,
        Dictionary<string, SpeakerRevision> validated, CancellationToken cancellationToken)
    {
        if (validated.TryGetValue(revisionId, out var cached)) return cached;
        var result = LoadRevision(meetingDirectory, revisionId, cancellationToken);
        var chain = new List<SpeakerRevision> { result };
        var current = result; var seen = new HashSet<string>(StringComparer.Ordinal) { result.RevisionId };
        while (current.SchemaVersion == 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.ParentRevisionId is not { } parentId || !seen.Add(parentId) || seen.Count > 2000)
                throw new InvalidDataException("人物文字修订的父版本链无效或过长。");
            if (RevisionHash(meetingDirectory, parentId, cancellationToken) != current.ParentRevisionSha256)
                throw new InvalidDataException("人物文字修订的父版本哈希不一致；不能丢弃修改来源。");
            bool checkedParent = validated.TryGetValue(parentId, out var parent);
            parent ??= LoadRevision(meetingDirectory, parentId, cancellationToken);
            ValidateTextTransition(parent, current);
            if (checkedParent) break;
            chain.Add(parent); current = parent;
        }
        foreach (var item in chain) validated[item.RevisionId] = item;
        return result;
    }

    private static SpeakerRevision LoadRevision(string meetingDirectory, string revisionId, CancellationToken cancellationToken)
    {
        var result = Read<SpeakerRevision>(meetingDirectory, "revisions", revisionId, "revision.json", cancellationToken);
        if (result.RevisionId != revisionId) throw new InvalidDataException("说话人修订目录与标识不一致。");
        var source = TranscriptService.Load(meetingDirectory, result.SourceTranscriptRevisionId, cancellationToken);
        var analysis = LoadAnalysis(meetingDirectory, result.AnalysisId, cancellationToken);
        if (TranscriptFingerprint(source) != result.SourceTranscriptSha256 || AnalysisHash(meetingDirectory, result.AnalysisId, cancellationToken) != result.AnalysisSha256)
            throw new InvalidDataException("说话人修订引用的文字/分析哈希不一致；保留原件检查，不能猜测关联。");
        ValidateRevision(result, source, analysis); return result;
    }

    public static IReadOnlyList<SpeakerRevision> ListRevisions(string meetingDirectory, CancellationToken cancellationToken = default)
    {
        // Request-scoped only: index/reopen must recheck disk authority. Shared ancestor validation
        // avoids a quadratic walk when a meeting has many one-line manual edits.
        var validated = new Dictionary<string, SpeakerRevision>(StringComparer.Ordinal);
        return ListIds(meetingDirectory, "revisions").Select(id => LoadWithAncestors(meetingDirectory, id, validated, cancellationToken))
            .OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public static SpeakerRevision Rename(string meetingDirectory, string parentRevisionId, string speakerId, string? displayName,
        CancellationToken cancellationToken = default)
    {
        var parent = Load(meetingDirectory, parentRevisionId, cancellationToken); RequireSpeaker(parent, speakerId);
        displayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(); ValidateName(displayName);
        return Revise(meetingDirectory, parent, parent.Speakers.Select(speaker => speaker.SpeakerId == speakerId ? speaker with { DisplayName = displayName } : speaker).ToArray(),
            parent.Segments, new("rename", DateTimeOffset.UtcNow, [], speakerId, speakerId, displayName), cancellationToken);
    }

    public static SpeakerRevision Assign(string meetingDirectory, string parentRevisionId, string segmentId, string? speakerId,
        CancellationToken cancellationToken = default)
    {
        var parent = Load(meetingDirectory, parentRevisionId, cancellationToken); RequireSpeaker(parent, speakerId); var target = RequireSegment(parent, segmentId);
        return Revise(meetingDirectory, parent, parent.Speakers, parent.Segments.Select(segment => segment.SegmentId == segmentId ?
            segment with { SpeakerId = speakerId, Status = speakerId is null ? "unconfirmed" : "manual" } : segment).ToArray(),
            new("assign", DateTimeOffset.UtcNow, [segmentId], target.SpeakerId, speakerId), cancellationToken);
    }

    public static SpeakerRevision Merge(string meetingDirectory, string parentRevisionId, string fromSpeakerId, string toSpeakerId,
        CancellationToken cancellationToken = default)
    {
        var parent = Load(meetingDirectory, parentRevisionId, cancellationToken); RequireSpeaker(parent, fromSpeakerId); RequireSpeaker(parent, toSpeakerId);
        if (fromSpeakerId == toSpeakerId) throw new InvalidDataException("请选择两个不同人物。");
        var affected = parent.Segments.Where(segment => segment.SpeakerId == fromSpeakerId).Select(segment => segment.SegmentId).ToArray();
        return Revise(meetingDirectory, parent, parent.Speakers.Where(speaker => speaker.SpeakerId != fromSpeakerId).ToArray(),
            parent.Segments.Select(segment => segment with
            {
                SpeakerId = segment.SpeakerId == fromSpeakerId ? toSpeakerId : segment.SpeakerId,
                Status = segment.SpeakerId == fromSpeakerId ? "manual" : segment.Status,
                CandidateBoundaries = segment.CandidateBoundaries.Select(boundary => boundary with
                { BeforeSpeakerId = boundary.BeforeSpeakerId == fromSpeakerId ? toSpeakerId : boundary.BeforeSpeakerId,
                  AfterSpeakerId = boundary.AfterSpeakerId == fromSpeakerId ? toSpeakerId : boundary.AfterSpeakerId }).ToArray()
            }).ToArray(), new("merge", DateTimeOffset.UtcNow, affected, fromSpeakerId, toSpeakerId), cancellationToken);
    }

    public static SpeakerRevision SplitSpeaker(string meetingDirectory, string parentRevisionId, IReadOnlyList<string> segmentIds,
        CancellationToken cancellationToken = default)
    {
        var parent = Load(meetingDirectory, parentRevisionId, cancellationToken);
        if (segmentIds is null || segmentIds.Count == 0 || segmentIds.Distinct().Count() != segmentIds.Count) throw new InvalidDataException("请选择要拆出的字幕段。");
        foreach (string id in segmentIds) RequireSegment(parent, id);
        int next = LoadAnalysis(meetingDirectory, parent.AnalysisId, cancellationToken).Speakers.Count + parent.Corrections.Count(correction => correction.Operation == "split-speaker");
        var speaker = new MeetingSpeaker(Guid.NewGuid().ToString("N"), NextCode(parent.Speakers, next));
        var selected = segmentIds.ToHashSet(StringComparer.Ordinal);
        return Revise(meetingDirectory, parent, parent.Speakers.Append(speaker).ToArray(), parent.Segments.Select(segment => selected.Contains(segment.SegmentId)
            ? segment with { SpeakerId = speaker.SpeakerId, Status = "manual" } : segment).ToArray(),
            new("split-speaker", DateTimeOffset.UtcNow, segmentIds.ToArray(), ToSpeakerId: speaker.SpeakerId), cancellationToken);
    }

    public static SpeakerRevision SplitSegment(string meetingDirectory, string parentRevisionId, string segmentId, int textOffset,
        long timeMs, string? leftSpeakerId, string? rightSpeakerId, CancellationToken cancellationToken = default)
        => SplitSegmentCore(meetingDirectory, parentRevisionId, segmentId, textOffset, timeMs, leftSpeakerId, rightSpeakerId, false, cancellationToken);

    /// <summary>The user explicitly supplies both an audio-clock time and a text cut. This is not model evidence.</summary>
    public static SpeakerRevision SplitSegmentAtManualTime(string meetingDirectory, string parentRevisionId, string segmentId, int textOffset,
        long timeMs, string? leftSpeakerId, string? rightSpeakerId, CancellationToken cancellationToken = default)
        => SplitSegmentCore(meetingDirectory, parentRevisionId, segmentId, textOffset, timeMs, leftSpeakerId, rightSpeakerId, true, cancellationToken);

    private static SpeakerRevision SplitSegmentCore(string meetingDirectory, string parentRevisionId, string segmentId, int textOffset,
        long timeMs, string? leftSpeakerId, string? rightSpeakerId, bool manualTime, CancellationToken cancellationToken)
    {
        var parent = Load(meetingDirectory, parentRevisionId, cancellationToken); var target = RequireSegment(parent, segmentId);
        RequireSpeaker(parent, leftSpeakerId); RequireSpeaker(parent, rightSpeakerId);
        if (target.StartMs is null || textOffset <= 0 || textOffset >= target.Text.Length || !IsTextBoundary(target.Text, textOffset) ||
            string.IsNullOrWhiteSpace(target.Text[..textOffset]) || string.IsNullOrWhiteSpace(target.Text[textOffset..]) ||
            timeMs <= target.StartMs || timeMs >= target.EndMs || !manualTime && !target.CandidateBoundaries.Any(boundary => boundary.TimeMs == timeMs))
            throw new InvalidDataException(manualTime ? "人工换人时刻必须严格位于本段起止之间，且文字切点两侧非空；不会按字数生成时间。"
                : "人工切点须选择本段模型候选换人时间与有效文字边界；不会按字数生成时间。");
        string timingSource = manualTime ? "human-entered-speaker-boundary" : "human-confirmed-speaker-boundary";
        var left = target with { SegmentId = Guid.NewGuid().ToString("N"), TextLength = target.TextEditGroupId is null ? textOffset : target.TextLength, Text = target.Text[..textOffset], EndMs = timeMs,
            SpeakerId = leftSpeakerId, Status = leftSpeakerId is null ? "unconfirmed" : "manual", TimingSource = timingSource,
            CandidateBoundaries = target.CandidateBoundaries.Where(boundary => boundary.TimeMs < timeMs).ToArray() };
        var right = target with { SegmentId = Guid.NewGuid().ToString("N"), TextStart = target.TextEditGroupId is null ? target.TextStart + textOffset : target.TextStart,
            TextLength = target.TextEditGroupId is null ? target.Text.Length - textOffset : target.TextLength, Text = target.Text[textOffset..], StartMs = timeMs,
            SpeakerId = rightSpeakerId, Status = rightSpeakerId is null ? "unconfirmed" : "manual", TimingSource = timingSource,
            CandidateBoundaries = target.CandidateBoundaries.Where(boundary => boundary.TimeMs > timeMs).ToArray() };
        return Revise(meetingDirectory, parent, parent.Speakers, parent.Segments.SelectMany(segment => segment.SegmentId == segmentId ? new[] { left, right } : [segment]).ToArray(),
            new(manualTime ? "split-segment-manual-time" : "split-segment", DateTimeOffset.UtcNow, [segmentId, left.SegmentId, right.SegmentId], leftSpeakerId, rightSpeakerId, TextOffset: textOffset, TimeMs: timeMs), cancellationToken);
    }

    public static bool IsTextBoundary(string text, int offset) => offset >= 0 && offset <= text.Length &&
        (offset == 0 || offset == text.Length || !char.IsHighSurrogate(text[offset - 1]) || !char.IsLowSurrogate(text[offset]));

    public static string CodeFor(int zeroBased)
    {
        if (zeroBased < 0 || zeroBased >= 1000) throw new InvalidDataException("人物数超过支持范围。");
        string result = ""; int value = zeroBased + 1;
        while (value > 0) { value--; result = (char)('A' + value % 26) + result; value /= 26; } return result;
    }

    private static string NextCode(IReadOnlyList<MeetingSpeaker> speakers, int from)
    { for (int i = from; i < 1000; i++) { string code = CodeFor(i); if (speakers.All(speaker => speaker.Code != code)) return code; } throw new InvalidDataException("人物数超限。"); }

    private static SpeakerRevision Revise(string meeting, SpeakerRevision parent, IReadOnlyList<MeetingSpeaker> speakers,
        IReadOnlyList<SpeakerSegment> segments, SpeakerCorrection correction, CancellationToken token)
    {
        int schema = correction.Operation is "edit-text" or "split-segment-manual-time" or "edit-subtitles" ? 2 : parent.SchemaVersion;
        var revision = parent with { SchemaVersion = schema, RevisionId = Guid.NewGuid().ToString("N"), ParentRevisionId = parent.RevisionId, CreatedAt = DateTimeOffset.UtcNow,
            ParentRevisionSha256 = schema == 2 ? RevisionHash(meeting, parent.RevisionId, token) : null,
            Speakers = speakers, Segments = segments, Corrections = parent.Corrections.Append(correction).ToArray(),
            Warnings = correction.Operation == "edit-text" ? parent.Warnings.Append(EditedTextWarning).Distinct().ToArray()
                : correction.Operation == "split-segment-manual-time" ? parent.Warnings.Append(ManualTimeBoundaryWarning).Distinct().ToArray()
                : correction.Operation == "edit-subtitles" ? parent.Warnings.Append(EditedSubtitlesWarning).Distinct().ToArray() : parent.Warnings };
        ValidateRevision(revision, TranscriptService.Load(meeting, parent.SourceTranscriptRevisionId, token), LoadAnalysis(meeting, parent.AnalysisId, token));
        if (schema == 2) ValidateTextTransition(parent, revision);
        Save(meeting, "revisions", revision.RevisionId, "revision.json", revision, token); return revision;
    }

    private static void ValidateAnalysis(SpeakerAnalysis analysis)
    {
        if (analysis is null || analysis.SchemaVersion != 1) throw new InvalidDataException("不支持的说话人分析版本。");
        ValidateId(analysis.AnalysisId); ValidateAudio(analysis.Audio);
        TranscriptFiles.ValidateIdentity(analysis.MeetingId, Duration(analysis.Audio)); ValidateSpeakers(analysis.Speakers);
        if (analysis.Engine is not { } engine || string.IsNullOrWhiteSpace(engine.Engine) || string.IsNullOrWhiteSpace(engine.Version) ||
            !TranscriptFiles.IsHash(engine.ExecutableSha256) || !TranscriptFiles.IsHash(engine.SegmentationModelSha256) || !TranscriptFiles.IsHash(engine.EmbeddingModelSha256) ||
            engine.KnownSpeakerCount is < 1 or > 100 || !double.IsFinite(engine.ClusteringThreshold) || engine.ClusteringThreshold <= 0 || engine.ClusteringThreshold > 2 ||
            string.IsNullOrWhiteSpace(engine.ActualDevice) || string.IsNullOrWhiteSpace(engine.ClusteringPolicy) || engine.ElapsedMilliseconds < 0 || engine.PeakWorkingSetBytes < 0)
            throw new InvalidDataException("说话人引擎、模型或全场聚类来源无效。");
        if (analysis.Intervals is null || analysis.Intervals.Count > 500000 || analysis.Warnings is null || analysis.Warnings.Count > 10000) throw new InvalidDataException("分析区间或提示无效。");
        var speakers = analysis.Speakers.Select(speaker => speaker.SpeakerId).ToHashSet(StringComparer.Ordinal);
        long duration = Duration(analysis.Audio);
        foreach (var interval in analysis.Intervals)
            if (interval is null || interval.StartMs < 0 || interval.EndMs <= interval.StartMs || interval.EndMs > duration ||
                interval.SpeakerId is not null && !speakers.Contains(interval.SpeakerId) || interval.Quality is not ("estimated" or "uncertain" or "verified"))
                throw new InvalidDataException("说话人区间越界或身份引用无效。");
    }

    internal static void ValidateAudio(SpeakerAudioBinding audio)
    {
        if (audio is null || !TranscriptFiles.IsHash(audio.InputWaveSha256) || audio.SourceMixSha256 is not null && !TranscriptFiles.IsHash(audio.SourceMixSha256) ||
            audio.SampleRate != 16000 || audio.FrameCount <= 0 || audio.FrameCount > 7L * 24 * 60 * 60 * 16000 ||
            audio.TimelinePolicy is not ("elapsed-v1" or "pause-excluded-v1" or "file-v1")) throw new InvalidDataException("说话人音频依据无效。");
    }

    internal static void ValidateBinding(TranscriptRevision source, SpeakerAnalysis analysis)
    {
        if (source.MeetingId != analysis.MeetingId || Math.Abs(source.MeetingDurationMs - analysis.Audio.FrameCount * 1000m / analysis.Audio.SampleRate) > 1)
            throw new InvalidDataException("说话人分析与所选字幕会议/时长不一致。");
        if (source.SourceMixSha256 is { } mix && mix != analysis.Audio.SourceMixSha256) throw new InvalidDataException("转写导出来源与分析混音哈希不一致。");
        if (source.Alignment is { } alignment && (alignment.InputWaveSha256 != analysis.Audio.InputWaveSha256 ||
            alignment.SourceMixSha256 != analysis.Audio.SourceMixSha256 || alignment.SampleRate != analysis.Audio.SampleRate ||
            alignment.FrameCount != analysis.Audio.FrameCount || alignment.TimelinePolicy != analysis.Audio.TimelinePolicy))
            throw new InvalidDataException("音文对齐与分析不属于同一音频/时间轴。");
    }

    private static void ValidateRevision(SpeakerRevision revision, TranscriptRevision source, SpeakerAnalysis analysis)
    {
        if (revision is null || revision.SchemaVersion is not (1 or 2) || revision.MeetingId != source.MeetingId || revision.MeetingDurationMs != source.MeetingDurationMs ||
            revision.AnalysisId != analysis.AnalysisId || revision.SourceTranscriptRevisionId != source.RevisionId || revision.Audio != analysis.Audio ||
            !TranscriptFiles.IsHash(revision.SourceTranscriptSha256) || !TranscriptFiles.IsHash(revision.AnalysisSha256) ||
            revision.Corrections is null || revision.Corrections.Count > 10000 || revision.Warnings is null || revision.Warnings.Count > 10000)
            throw new InvalidDataException("说话人修订来源无效。");
        if (revision.SchemaVersion == 2 && (revision.ParentRevisionId is null || !TranscriptFiles.IsHash(revision.ParentRevisionSha256)) ||
            revision.SchemaVersion == 1 && revision.ParentRevisionSha256 is not null)
            throw new InvalidDataException("人物文字修订须保留父版本及哈希。");
        ValidateBinding(source, analysis); ValidateId(revision.RevisionId); ValidateSpeakers(revision.Speakers);
        if (revision.ParentRevisionId is { } parent) { ValidateId(parent); if (parent == revision.RevisionId) throw new InvalidDataException("修订不能引用自身。"); }
        if (revision.ParentRevisionId is null && revision.Corrections.Count != 0) throw new InvalidDataException("人工纠错须保留父版本。");
        var originalSpeakers = analysis.Speakers.ToDictionary(speaker => speaker.SpeakerId, StringComparer.Ordinal);
        var createdSpeakers = revision.Corrections.Where(correction => correction is not null && correction.Operation == "split-speaker")
            .Select(correction => correction.ToSpeakerId).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var speaker in revision.Speakers)
        {
            if (originalSpeakers.TryGetValue(speaker.SpeakerId, out var originalSpeaker))
            { if (speaker.Code != originalSpeaker.Code) throw new InvalidDataException("原人物内部身份与代号不得重排。"); }
            else if (!createdSpeakers.Contains(speaker.SpeakerId)) throw new InvalidDataException("新人物缺少人工拆出来源。");
        }
        if (revision.Segments is null || revision.Segments.Count == 0 || revision.Segments.Count > 100000) throw new InvalidDataException("说话人字幕段数无效。");
        var speakers = revision.Speakers.Select(speaker => speaker.SpeakerId).ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal); var editGroups = new HashSet<string>(StringComparer.Ordinal);
        int sourceIndex = 0, offset = 0; long? previousEnd = null;
        for (int segmentIndex = 0; segmentIndex < revision.Segments.Count; segmentIndex++)
        {
            var segment = revision.Segments[segmentIndex];
            if (sourceIndex >= source.Segments.Count) throw new InvalidDataException("说话人字幕添加了无来源文字。");
            var original = source.Segments[sourceIndex];
            if (segment is null || !ids.Add(segment.SegmentId) || segment.SourceSegmentId != original.SegmentId || segment.TextStart != offset ||
                segment.TextLength <= 0 || segment.TextLength > original.Text.Length - offset ||
                (segment.TextEditGroupId is null ? segment.Text != original.Text.Substring(offset, segment.TextLength) : revision.SchemaVersion != 2) ||
                !IsTextBoundary(original.Text, offset) || !IsTextBoundary(original.Text, offset + segment.TextLength) ||
                segment.SpeakerId is not null && !speakers.Contains(segment.SpeakerId) ||
                segment.Status is not ("assigned" or "manual" or "unconfirmed" or "multiple-speakers" or "overlap-uncertain" or "untimed") ||
                (segment.Status is "assigned" or "manual") != (segment.SpeakerId is not null) || string.IsNullOrWhiteSpace(segment.TimingSource) ||
                segment.CandidateBoundaries is null || segment.CandidateBoundaries.Count > 10000)
                throw new InvalidDataException("说话人字幕文字范围或身份无效，要求全文不丢不重。");
            ValidateEditedText(segment.Text);
            bool groupContinues = segment.TextEditGroupId is { } group && segmentIndex + 1 < revision.Segments.Count &&
                revision.Segments[segmentIndex + 1]?.TextEditGroupId == group;
            bool groupStarted = segment.TextEditGroupId is { } groupId && segmentIndex > 0 && revision.Segments[segmentIndex - 1].TextEditGroupId == groupId;
            if (segment.TextEditGroupId is { } editGroup)
            {
                ValidateId(editGroup);
                if (!groupStarted && !editGroups.Add(editGroup)) throw new InvalidDataException("人工文字来源组不能反序或重复。");
                if (groupStarted && revision.Segments[segmentIndex - 1].TextLength != segment.TextLength)
                    throw new InvalidDataException("人工文字来源组范围必须相同，不代表新字的逐字映射。");
            }
            ValidateId(segment.SegmentId);
            if (segment.ManualTiming)
            {
                if (revision.SchemaVersion != 2 || (segment.StartMs is null) != (segment.EndMs is null) ||
                    segment.StartMs < 0 || segment.EndMs > revision.MeetingDurationMs || segment.EndMs <= segment.StartMs)
                    throw new InvalidDataException("人工字幕时间无效或超出会议范围。");
            }
            else if (original.StartMs is null)
            { if (segment.StartMs is not null || segment.EndMs is not null || offset != 0 || segment.TextLength != original.Text.Length) throw new InvalidDataException("无时间文字不能自动产生或拆分时间。"); }
            else if (segment.StartMs is null || segment.EndMs is null || segment.StartMs < original.StartMs || segment.EndMs > original.EndMs || segment.EndMs <= segment.StartMs ||
                offset == 0 && !groupStarted && segment.StartMs != original.StartMs || (offset > 0 || groupStarted) &&
                !revision.Segments[segmentIndex - 1].ManualTiming && segment.StartMs < previousEnd ||
                !groupContinues && offset + segment.TextLength == original.Text.Length && segment.EndMs != original.EndMs)
                throw new InvalidDataException("说话人字幕超出原句时间范围。");
            foreach (var boundary in segment.CandidateBoundaries)
                if (segment.StartMs is null || boundary.TimeMs <= segment.StartMs || boundary.TimeMs >= segment.EndMs ||
                    boundary.BeforeSpeakerId is not null && !speakers.Contains(boundary.BeforeSpeakerId) || boundary.AfterSpeakerId is not null && !speakers.Contains(boundary.AfterSpeakerId))
                    throw new InvalidDataException("候选换人边界无效。");
            previousEnd = segment.EndMs; if (!groupContinues) offset += segment.TextLength;
            if (offset == original.Text.Length) { sourceIndex++; offset = 0; previousEnd = null; }
        }
        if (offset != 0 || sourceIndex != source.Segments.Count) throw new InvalidDataException("说话人字幕遗漏原稿尾部文字。");
        foreach (var correction in revision.Corrections)
        {
            if (correction is null || correction.Operation is not ("rename" or "assign" or "merge" or "split-speaker" or "split-segment" or "split-segment-manual-time" or "edit-text" or "edit-subtitles") ||
                correction.SegmentIds is null || correction.SegmentIds.Count > 100000 || correction.SegmentIds.Distinct().Count() != correction.SegmentIds.Count ||
                correction.TextOffset is < 1 || correction.TimeMs is < 0) throw new InvalidDataException("人物人工纠错记录无效。");
            foreach (string id in correction.SegmentIds) ValidateId(id);
            if (correction.FromSpeakerId is { } from) ValidateId(from);
            if (correction.ToSpeakerId is { } to) ValidateId(to);
            ValidateName(correction.DisplayName);
            if (correction.Operation == "edit-subtitles")
            {
                if (revision.SchemaVersion != 2 || correction.SubtitleEdits is not { Count: > 0 } edits ||
                    edits.Any(edit => edit is null) || !edits.Select(edit => edit.SegmentId).SequenceEqual(correction.SegmentIds))
                    throw new InvalidDataException("批量字幕校订缺少逐段修改来源。");
                foreach (var edit in edits) { ValidateEditedText(edit.PreviousText); ValidateEditedText(edit.ReplacementText); }
            }
            else if (correction.SubtitleEdits is not null) throw new InvalidDataException("非字幕校订不能附带批量修改。");
            if (correction.Operation == "split-segment-manual-time" && revision.SchemaVersion != 2)
                throw new InvalidDataException("人工输入的换人时刻必须保留父版本哈希与修订来源。");
            if (correction.Operation == "edit-text")
            {
                if (revision.SchemaVersion != 2 || correction.SegmentIds.Count != 1 || correction.PreviousText is null || correction.ReplacementText is null)
                    throw new InvalidDataException("人工正文校订必须记录原文及新文。");
                ValidateEditedText(correction.PreviousText); ValidateEditedText(correction.ReplacementText);
            }
            else if (correction.PreviousText is not null || correction.ReplacementText is not null)
                throw new InvalidDataException("非正文校订不能声明正文替换。");
        }
    }

    private static void ValidateSpeakers(IReadOnlyList<MeetingSpeaker> speakers)
    {
        if (speakers is null || speakers.Count > 1000) throw new InvalidDataException("人物列表无效。");
        var ids = new HashSet<string>(StringComparer.Ordinal); var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var speaker in speakers)
        {
            if (speaker is null) throw new InvalidDataException("空人物记录。"); ValidateId(speaker.SpeakerId); ValidateName(speaker.DisplayName);
            if (!ids.Add(speaker.SpeakerId) || string.IsNullOrEmpty(speaker.Code) || speaker.Code.Length > 3 || speaker.Code.Any(c => c < 'A' || c > 'Z') || !codes.Add(speaker.Code))
                throw new InvalidDataException("人物内部身份或显示代号重复/无效。");
        }
    }

    private static void ValidateName(string? name)
    { if (name is not null && (name.Length > 80 || name.Any(char.IsControl) || name.Contains('[') || name.Contains(']') || name.Contains("-->", StringComparison.Ordinal))) throw new InvalidDataException("显示名称至多80字，不能包含控制字符、方括号或字幕箭头。"); }
    private static void RequireSpeaker(SpeakerRevision parent, string? id)
    { if (id is not null && !parent.Speakers.Any(speaker => speaker.SpeakerId == id)) throw new InvalidDataException("人物不属于所选修订。"); }
    private static SpeakerSegment RequireSegment(SpeakerRevision parent, string id) => parent.Segments.SingleOrDefault(segment => segment.SegmentId == id) ?? throw new InvalidDataException("字幕段不属于所选修订。");
    private static long Duration(SpeakerAudioBinding audio) => (audio.FrameCount * 1000 + audio.SampleRate - 1) / audio.SampleRate;
    private static void ValidateId(string id) { if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("说话人版本或内部身份标识无效。"); }
    private static void ValidateMeeting(string meeting, string meetingId, bool synthetic)
    {
        using var json = TranscriptFiles.ParseJson(TranscriptFiles.ReadBounded(TranscriptFiles.Inside(meeting, "meeting.json"), default, 16 * 1024 * 1024));
        if (json.RootElement.TryGetProperty("meetingId", out var id))
        { if (id.GetString() != meetingId) throw new InvalidDataException("说话人分析与会议清单身份不一致。"); }
        else if (!(synthetic && json.RootElement.TryGetProperty("isSynthetic", out var marker) && marker.ValueKind == JsonValueKind.True) && Path.GetFileName(Path.TrimEndingDirectorySeparator(meeting)) != meetingId)
            throw new InvalidDataException("说话人分析与会议目录身份不一致。");
    }
    private static string AnalysisHash(string meeting, string id, CancellationToken token) => TranscriptFiles.Hash(TranscriptFiles.ReadBounded(PathFor(meeting, "analyses", id, "analysis.json"), token, 64 * 1024 * 1024));
    private static string RevisionHash(string meeting, string id, CancellationToken token) => TranscriptFiles.Hash(TranscriptFiles.ReadBounded(PathFor(meeting, "revisions", id, "revision.json"), token, 64 * 1024 * 1024));
    private static string PathFor(string meeting, string kind, string id, string file)
    { ValidateId(id); return TranscriptFiles.Inside(TranscriptFiles.Meeting(meeting), $"diarization/{kind}/{id}/{file}"); }
    private static T Read<T>(string meeting, string kind, string id, string file, CancellationToken token)
    {
        using var json = TranscriptFiles.ParseJson(TranscriptFiles.ReadBounded(PathFor(meeting, kind, id, file), token, 64 * 1024 * 1024));
        return JsonSerializer.Deserialize<T>(json.RootElement.GetRawText(), TranscriptFiles.Json) ?? throw new InvalidDataException("说话人文件为空。");
    }
    private static IEnumerable<string> ListIds(string meeting, string kind)
    {
        string root = TranscriptFiles.Inside(TranscriptFiles.Meeting(meeting), "diarization/" + kind);
        if (!Directory.Exists(root)) return [];
        string[] directories = Directory.EnumerateDirectories(root).ToArray(); if (directories.Length > 2000) throw new InvalidDataException("说话人版本超过2000项。");
        foreach (string directory in directories) TranscriptFiles.NoReparse(directory);
        return directories.Select(Path.GetFileName).Where(id => Guid.TryParseExact(id, "N", out _)).Select(id => id!);
    }
    private static void Save<T>(string meeting, string kind, string id, string filename, T value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ValidateId(id);
        string root = TranscriptFiles.Inside(TranscriptFiles.Meeting(meeting), "diarization/" + kind); Directory.CreateDirectory(root);
        string pending = TranscriptFiles.Inside(root, ".pending-" + id), final = TranscriptFiles.Inside(root, id);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, TranscriptFiles.Json);
        if (bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("说话人版本超过64MiB。");
        Directory.CreateDirectory(pending); TranscriptFiles.WriteNew(TranscriptFiles.Inside(pending, filename), bytes);
        token.ThrowIfCancellationRequested(); TranscriptFiles.NoReparse(final); Directory.Move(pending, final);
    }
}
