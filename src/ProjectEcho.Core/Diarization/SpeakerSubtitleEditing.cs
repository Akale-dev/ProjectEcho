using ProjectEcho.Core.Transcription;

namespace ProjectEcho.Core.Diarization;

public static partial class SpeakerService
{
    private const string EditedSubtitlesWarning = "含人工批量字幕校订：人物身份和原文字来源保留；修改的时间由用户输入，不是重新识别或自动定位，改时段的候选换人点已清除。原稿与父版本保持独立。";

    /// <summary>One atomic, hash-bound revision for the review editor's whole draft.</summary>
    public static SpeakerRevision EditSubtitles(string meetingDirectory, string parentRevisionId,
        IReadOnlyList<TranscriptSegment> segments, CancellationToken cancellationToken = default)
    {
        var parent = Load(meetingDirectory, parentRevisionId, cancellationToken);
        var source = TranscriptService.Load(meetingDirectory, parent.SourceTranscriptRevisionId, cancellationToken);
        var presentation = SpeakerDisplay.AsTranscript(parent, source);
        TranscriptFiles.ValidateSegments(segments, parent.MeetingDurationMs);
        if (segments.Count != presentation.Segments.Count) throw new InvalidDataException("批量校订不能增加或删除人物字幕。");
        var edits = new List<SpeakerSubtitleEdit>();
        for (int i = 0; i < segments.Count; i++)
        {
            var prior = presentation.Segments[i]; var next = segments[i];
            if (next.SegmentId != prior.SegmentId || next.SpeakerLabel != prior.SpeakerLabel || next.SourceKind != prior.SourceKind)
                throw new InvalidDataException("批量校订不能重排字幕或更改人物身份。");
            if (next.Text != prior.Text || next.StartMs != prior.StartMs || next.EndMs != prior.EndMs)
                edits.Add(new(prior.SegmentId, prior.Text, next.Text, prior.StartMs, prior.EndMs, next.StartMs, next.EndMs));
        }
        if (edits.Count == 0) throw new InvalidDataException("没有尚未保存的字幕修改。");
        var updated = ApplySubtitleEdits(parent, edits);
        return Revise(meetingDirectory, parent, parent.Speakers, updated,
            new("edit-subtitles", DateTimeOffset.UtcNow, edits.Select(edit => edit.SegmentId).ToArray(), SubtitleEdits: edits), cancellationToken);
    }

    private static IReadOnlyList<SpeakerSegment> ApplySubtitleEdits(SpeakerRevision parent,
        IReadOnlyList<SpeakerSubtitleEdit> edits, SpeakerRevision? child = null)
    {
        if (edits is null || edits.Count == 0 || edits.Count > parent.Segments.Count || edits.Any(edit => edit is null) ||
            edits.Select(edit => edit.SegmentId).Distinct().Count() != edits.Count)
            throw new InvalidDataException("批量校订段落范围无效。");
        var byId = edits.ToDictionary(edit => edit.SegmentId, StringComparer.Ordinal);
        if (!parent.Segments.Where(segment => byId.ContainsKey(segment.SegmentId)).Select(segment => segment.SegmentId)
            .SequenceEqual(edits.Select(edit => edit.SegmentId))) throw new InvalidDataException("批量校订段落不属于父稿或顺序错误。");
        var groups = parent.Segments.Where(segment => segment.TextEditGroupId is not null).Select(segment => segment.TextEditGroupId!).ToHashSet(StringComparer.Ordinal);
        return parent.Segments.Select(target =>
        {
            if (!byId.TryGetValue(target.SegmentId, out var edit)) return target;
            ValidateEditedText(edit.ReplacementText);
            bool textChanged = edit.ReplacementText != target.Text;
            bool timeChanged = edit.StartMs != target.StartMs || edit.EndMs != target.EndMs;
            if (edit.PreviousText != target.Text || edit.PreviousStartMs != target.StartMs || edit.PreviousEndMs != target.EndMs ||
                !textChanged && !timeChanged || (edit.StartMs is null) != (edit.EndMs is null) || edit.StartMs < 0 ||
                edit.EndMs > parent.MeetingDurationMs || edit.EndMs <= edit.StartMs)
                throw new InvalidDataException("批量校订与父稿不一致或时间无效。");
            string? group = target.TextEditGroupId;
            if (textChanged && group is null)
            {
                group = child is null ? Guid.NewGuid().ToString("N") : RequireSegment(child, target.SegmentId).TextEditGroupId;
                if (group is null || !groups.Add(group)) throw new InvalidDataException("批量正文修改缺少独立的来源组。");
            }
            return target with { Text = edit.ReplacementText, TextEditGroupId = group,
                StartMs = edit.StartMs, EndMs = edit.EndMs, ManualTiming = target.ManualTiming || timeChanged,
                TimingSource = timeChanged ? "human-edited-subtitle" : target.TimingSource,
                CandidateBoundaries = timeChanged ? [] : target.CandidateBoundaries };
        }).ToArray();
    }
}
