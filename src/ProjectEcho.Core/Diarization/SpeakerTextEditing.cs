using System.Text;
using System.Text.Json;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.Core.Diarization;

public static partial class SpeakerService
{
    private const string EditedTextWarning = "含人工正文校订：保留原句回听时间和人物引用；改字后的文字没有逐字音文映射，原稿及父版本保持独立。";
    private const string ManualTimeBoundaryWarning = "含人工输入的换人时刻：时间及文字切点由用户明确指定，不是模型确认或按字数计算；原稿及父版本保持独立。";

    /// <summary>Replace only this segment's displayed body. Source ranges remain coarse provenance;
    /// neither word times nor new source characters are manufactured for the replacement.</summary>
    public static SpeakerRevision EditText(string meetingDirectory, string parentRevisionId, string segmentId,
        string text, CancellationToken cancellationToken = default)
    {
        ValidateEditedText(text);
        var parent = Load(meetingDirectory, parentRevisionId, cancellationToken);
        var target = RequireSegment(parent, segmentId);
        var edited = target with { Text = text, TextEditGroupId = target.TextEditGroupId ?? Guid.NewGuid().ToString("N") };
        return Revise(meetingDirectory, parent, parent.Speakers,
            parent.Segments.Select(segment => segment.SegmentId == segmentId ? edited : segment).ToArray(),
            new("edit-text", DateTimeOffset.UtcNow, [segmentId], PreviousText: target.Text, ReplacementText: text), cancellationToken);
    }

    private static void ValidateEditedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > TranscriptFiles.MaximumSegmentCharacters || text.Contains('\0'))
            throw new InvalidDataException("正文不能为空、含 NUL 或超过十万字符。");
        try { _ = new UTF8Encoding(false, true).GetByteCount(text); }
        catch (EncoderFallbackException exception) { throw new InvalidDataException("正文包含不完整 Unicode 字符，未保存。", exception); }
    }

    // A schema-2 revision is one checked transformation of its hash-bound parent. This prevents an
    // edited-range marker from becoming an escape hatch for missing, duplicated or unrelated text.
    // A chain can start at an unchanged legacy v1 revision, preserving existing manual corrections.
    private static void ValidateTextTransition(SpeakerRevision parent, SpeakerRevision child)
    {
        if (child.ParentRevisionId != parent.RevisionId || child.Corrections.Count != parent.Corrections.Count + 1 ||
            !Same(child.Corrections.Take(parent.Corrections.Count).ToArray(), parent.Corrections))
            throw new InvalidDataException("正文修订必须完整保留父稿的人工修正历史。");
        var correction = child.Corrections[^1];
        var speakers = parent.Speakers;
        var segments = parent.Segments;
        var warnings = parent.Warnings;
        switch (correction.Operation)
        {
            case "edit-subtitles":
                segments = ApplySubtitleEdits(parent, correction.SubtitleEdits!, child);
                warnings = parent.Warnings.Append(EditedSubtitlesWarning).Distinct().ToArray();
                break;
            case "edit-text":
            {
                NeedIds(correction, 1); var target = RequireSegment(parent, correction.SegmentIds[0]);
                var result = RequireSegment(child, target.SegmentId);
                if (correction.PreviousText != target.Text || correction.ReplacementText != result.Text ||
                    result.TextEditGroupId is null || target.TextEditGroupId is not null && result.TextEditGroupId != target.TextEditGroupId ||
                    target.TextEditGroupId is null && parent.Segments.Any(segment => segment.TextEditGroupId == result.TextEditGroupId))
                    throw new InvalidDataException("正文校订与父稿或独立原文字来源组不一致。");
                segments = parent.Segments.Select(segment => segment.SegmentId == target.SegmentId
                    ? segment with { Text = result.Text, TextEditGroupId = result.TextEditGroupId } : segment).ToArray();
                warnings = parent.Warnings.Append(EditedTextWarning).Distinct().ToArray();
                break;
            }
            case "rename":
                NeedIds(correction, 0); RequireNonNullSpeaker(parent, correction.FromSpeakerId);
                if (correction.FromSpeakerId != correction.ToSpeakerId) throw new InvalidDataException("改名不能更换内部人物身份。");
                speakers = parent.Speakers.Select(speaker => speaker.SpeakerId == correction.FromSpeakerId
                    ? speaker with { DisplayName = correction.DisplayName } : speaker).ToArray();
                break;
            case "assign":
            {
                NeedIds(correction, 1); var target = RequireSegment(parent, correction.SegmentIds[0]);
                RequireSpeaker(parent, correction.ToSpeakerId);
                if (target.SpeakerId != correction.FromSpeakerId) throw new InvalidDataException("单段改人的原人物与父稿不一致。");
                segments = parent.Segments.Select(segment => segment.SegmentId == target.SegmentId
                    ? segment with { SpeakerId = correction.ToSpeakerId, Status = correction.ToSpeakerId is null ? "unconfirmed" : "manual" } : segment).ToArray();
                break;
            }
            case "merge":
            {
                RequireNonNullSpeaker(parent, correction.FromSpeakerId); RequireNonNullSpeaker(parent, correction.ToSpeakerId);
                if (correction.FromSpeakerId == correction.ToSpeakerId ||
                    !parent.Segments.Where(segment => segment.SpeakerId == correction.FromSpeakerId).Select(segment => segment.SegmentId).SequenceEqual(correction.SegmentIds))
                    throw new InvalidDataException("人物合并的范围与父稿不一致。");
                speakers = parent.Speakers.Where(speaker => speaker.SpeakerId != correction.FromSpeakerId).ToArray();
                segments = parent.Segments.Select(segment => segment with
                {
                    SpeakerId = segment.SpeakerId == correction.FromSpeakerId ? correction.ToSpeakerId : segment.SpeakerId,
                    Status = segment.SpeakerId == correction.FromSpeakerId ? "manual" : segment.Status,
                    CandidateBoundaries = segment.CandidateBoundaries.Select(boundary => boundary with
                    {
                        BeforeSpeakerId = boundary.BeforeSpeakerId == correction.FromSpeakerId ? correction.ToSpeakerId : boundary.BeforeSpeakerId,
                        AfterSpeakerId = boundary.AfterSpeakerId == correction.FromSpeakerId ? correction.ToSpeakerId : boundary.AfterSpeakerId
                    }).ToArray()
                }).ToArray();
                break;
            }
            case "split-speaker":
            {
                if (correction.SegmentIds.Count == 0 || correction.ToSpeakerId is null || parent.Speakers.Any(speaker => speaker.SpeakerId == correction.ToSpeakerId))
                    throw new InvalidDataException("拆出人物必须创建新身份并选定字幕段。");
                foreach (string id in correction.SegmentIds) RequireSegment(parent, id);
                var added = child.Speakers.SingleOrDefault(speaker => speaker.SpeakerId == correction.ToSpeakerId)
                    ?? throw new InvalidDataException("拆出人物缺少新身份。");
                if (added.DisplayName is not null) throw new InvalidDataException("拆出人物不能推断显示姓名。");
                speakers = parent.Speakers.Append(added).ToArray();
                var selected = correction.SegmentIds.ToHashSet(StringComparer.Ordinal);
                segments = parent.Segments.Select(segment => selected.Contains(segment.SegmentId)
                    ? segment with { SpeakerId = added.SpeakerId, Status = "manual" } : segment).ToArray();
                break;
            }
            case "split-segment":
            case "split-segment-manual-time":
            {
                bool manualTime = correction.Operation == "split-segment-manual-time";
                NeedIds(correction, 3); var target = RequireSegment(parent, correction.SegmentIds[0]);
                RequireSpeaker(parent, correction.FromSpeakerId); RequireSpeaker(parent, correction.ToSpeakerId);
                if (correction.TextOffset is not { } cut || correction.TimeMs is not { } time || target.StartMs is null ||
                    cut <= 0 || cut >= target.Text.Length || !IsTextBoundary(target.Text, cut) ||
                    string.IsNullOrWhiteSpace(target.Text[..cut]) || string.IsNullOrWhiteSpace(target.Text[cut..]) ||
                    time <= target.StartMs || time >= target.EndMs || !manualTime && !target.CandidateBoundaries.Any(boundary => boundary.TimeMs == time))
                    throw new InvalidDataException("人工正文切点缺少有效文字边界、段内明确时间或所要求的模型候选证据。");
                string timingSource = manualTime ? "human-entered-speaker-boundary" : "human-confirmed-speaker-boundary";
                var left = target with
                {
                    SegmentId = correction.SegmentIds[1], TextLength = target.TextEditGroupId is null ? cut : target.TextLength,
                    Text = target.Text[..cut], EndMs = time, SpeakerId = correction.FromSpeakerId,
                    Status = correction.FromSpeakerId is null ? "unconfirmed" : "manual", TimingSource = timingSource,
                    CandidateBoundaries = target.CandidateBoundaries.Where(boundary => boundary.TimeMs < time).ToArray()
                };
                var right = target with
                {
                    SegmentId = correction.SegmentIds[2], TextStart = target.TextEditGroupId is null ? target.TextStart + cut : target.TextStart,
                    TextLength = target.TextEditGroupId is null ? target.Text.Length - cut : target.TextLength,
                    Text = target.Text[cut..], StartMs = time, SpeakerId = correction.ToSpeakerId,
                    Status = correction.ToSpeakerId is null ? "unconfirmed" : "manual", TimingSource = timingSource,
                    CandidateBoundaries = target.CandidateBoundaries.Where(boundary => boundary.TimeMs > time).ToArray()
                };
                segments = parent.Segments.SelectMany(segment => segment.SegmentId == target.SegmentId ? new[] { left, right } : [segment]).ToArray();
                if (manualTime) warnings = parent.Warnings.Append(ManualTimeBoundaryWarning).Distinct().ToArray();
                break;
            }
            default: throw new InvalidDataException("不支持的人物正文修订操作。");
        }
        var expected = parent with
        {
            SchemaVersion = 2, RevisionId = child.RevisionId, ParentRevisionId = parent.RevisionId,
            ParentRevisionSha256 = child.ParentRevisionSha256, CreatedAt = child.CreatedAt, Speakers = speakers,
            Segments = segments, Corrections = child.Corrections, Warnings = warnings
        };
        if (!Same(expected, child)) throw new InvalidDataException("人物修订含未声明的正文、时间、身份或来源更改。");
    }

    private static void NeedIds(SpeakerCorrection correction, int count)
    { if (correction.SegmentIds.Count != count) throw new InvalidDataException("人物修订的字幕范围无效。"); }
    private static void RequireNonNullSpeaker(SpeakerRevision parent, string? id)
    { if (id is null) throw new InvalidDataException("修订缺少人物身份。"); RequireSpeaker(parent, id); }
    private static bool Same<T>(T first, T second) =>
        JsonSerializer.SerializeToUtf8Bytes(first, TranscriptFiles.Json).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(second, TranscriptFiles.Json));
}
