using ProjectEcho.Core.Transcription;

namespace ProjectEcho.Core.Diarization;

/// <summary>Associates evidence with exact source text. No word allocation by character length.</summary>
public static class SpeakerMapper
{
    public const long BoundaryToleranceMs = 160;

    public static IReadOnlyList<SpeakerSegment> Map(TranscriptRevision transcript, SpeakerAnalysis analysis,
        SpeakerMappingEvidence? evidence = null, CancellationToken cancellationToken = default)
    {
        if (evidence is not null && (evidence.TranscriptRevisionId != transcript.RevisionId ||
            evidence.TranscriptSha256 != SpeakerService.TranscriptFingerprint(transcript) || evidence.Audio != analysis.Audio))
            throw new InvalidDataException("音文映射与所选转写/音频不一致。");
        cancellationToken.ThrowIfCancellationRequested();
        var boundariesBySegment = evidence?.Boundaries.ToLookup(boundary => boundary.SegmentId, StringComparer.Ordinal);
        var intervals = analysis.Intervals.OrderBy(interval => interval.StartMs).ThenBy(interval => interval.EndMs).ToArray();
        var prefixEnd = new long[intervals.Length];
        for (int i = 0; i < intervals.Length; i++) prefixEnd[i] = Math.Max(intervals[i].EndMs, i == 0 ? 0 : prefixEnd[i - 1]);
        var result = new List<SpeakerSegment>();
        foreach (var source in transcript.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.StartMs is null)
            { result.Add(New(source, 0, source.Text.Length, null, null, null, "untimed", [])); continue; }
            long start = source.StartMs.Value, end = source.EndMs!.Value;
            int index = Array.BinarySearch(prefixEnd, start + 1);
            if (index < 0) index = ~index; else while (index > 0 && prefixEnd[index - 1] >= start + 1) index--;
            var intersecting = new List<SpeakerInterval>();
            for (; index < intervals.Length && intervals[index].StartMs < end; index++)
                if (intervals[index].EndMs > start) intersecting.Add(intervals[index]);
            string[] ids = intersecting.Where(interval => interval.SpeakerId is not null).Select(interval => interval.SpeakerId!).Distinct().ToArray();
            var boundaries = new List<SpeakerCandidateBoundary>();
            // Runtime Overlap flags describe an entire interval that intersects another
            // voice somewhere. Limit ambiguity to the actual intersection inside this cue;
            // a 118ms interruption must not make the remaining five seconds "overlap".
            bool overlap = HasLocalOverlap(intersecting, start, end);
            for (int i = 1; i < intersecting.Count; i++)
            {
                var left = intersecting[i - 1]; var right = intersecting[i];
                if (left.SpeakerId == right.SpeakerId) continue;
                if (Math.Min(left.EndMs, end) > Math.Max(right.StartMs, start)) overlap = true;
                if (right.StartMs > start && right.StartMs < end)
                    boundaries.Add(new(right.StartMs, left.SpeakerId, right.SpeakerId));
            }
            bool uncertain = intersecting.Any(interval => interval.SpeakerId is null || interval.Quality == "uncertain");
            long covered = Covered(intersecting, start, end);
            if (ids.Length == 1 && !overlap && !uncertain && covered >= 150 && covered * 5 >= end - start)
            { result.Add(New(source, 0, source.Text.Length, start, end, ids[0], "assigned", [])); continue; }
            if (ids.Length > 1 && !overlap && !uncertain && boundaries.Count > 0 && evidence is not null &&
                TrySplit(source, boundaries, boundariesBySegment![source.SegmentId].ToArray(), out var split))
            { result.AddRange(split); continue; }
            string status = overlap ? "overlap-uncertain" : ids.Length > 1 ? "multiple-speakers" : "unconfirmed";
            result.Add(New(source, 0, source.Text.Length, start, end, null, status, boundaries));
        }
        return result.AsReadOnly();
    }

    private static bool TrySplit(TranscriptSegment source, IReadOnlyList<SpeakerCandidateBoundary> changes,
        IReadOnlyList<SpeakerTextBoundary> evidence, out IReadOnlyList<SpeakerSegment> result)
    {
        result = []; var cuts = new List<(SpeakerCandidateBoundary Change, SpeakerTextBoundary Text)>();
        foreach (var change in changes)
        {
            var candidates = evidence.Where(boundary => boundary.SegmentId == source.SegmentId &&
                boundary.TextOffset > 0 && boundary.TextOffset < source.Text.Length &&
                boundary.LeftEndMs <= boundary.RightStartMs && boundary.LeftEndMs > source.StartMs &&
                boundary.RightStartMs < source.EndMs &&
                Math.Abs(boundary.LeftEndMs - change.TimeMs) <= BoundaryToleranceMs &&
                Math.Abs(boundary.RightStartMs - change.TimeMs) <= BoundaryToleranceMs).ToArray();
            // Ambiguous boundaries remain a user decision; nearest-token guessing can move a negation.
            if (candidates.Length != 1 || change.BeforeSpeakerId is null || change.AfterSpeakerId is null) return false;
            var selected = candidates[0];
            if (cuts.Count > 0 && (selected.TextOffset <= cuts[^1].Text.TextOffset ||
                selected.LeftEndMs <= cuts[^1].Text.RightStartMs || cuts[^1].Change.AfterSpeakerId != change.BeforeSpeakerId)) return false;
            if (!IsAutomaticTextBoundary(source.Text, selected.TextOffset)) return false;
            cuts.Add((change, selected));
        }
        var segments = new List<SpeakerSegment>(); int offset = 0; long start = source.StartMs!.Value;
        foreach (var cut in cuts)
        {
            int length = cut.Text.TextOffset - offset;
            if (string.IsNullOrWhiteSpace(source.Text.Substring(offset, length)) || cut.Text.LeftEndMs - start < 300) return false;
            segments.Add(New(source, offset, length, start, cut.Text.LeftEndMs, cut.Change.BeforeSpeakerId, "assigned", [], cut.Text.Source));
            offset += length; start = cut.Text.RightStartMs;
        }
        if (string.IsNullOrWhiteSpace(source.Text[offset..]) || source.EndMs - start < 300) return false;
        segments.Add(New(source, offset, source.Text.Length - offset, start, source.EndMs, cuts[^1].Change.AfterSpeakerId,
            "assigned", [], cuts[^1].Text.Source));
        result = segments; return true;
    }

    private static bool IsAutomaticTextBoundary(string text, int offset)
    {
        if (!SpeakerService.IsTextBoundary(text, offset)) return false;
        // Whisper BPE tokens can divide one word (including a negation or a number).
        // That internal token edge is not enough evidence for changing its speaker.
        static bool WordPart(char c) => char.IsDigit(c) || char.IsLetter(c) && c <= '\u024f' || c is '\'' or '\u2019' or '-';
        return !(offset > 0 && offset < text.Length && WordPart(text[offset - 1]) && WordPart(text[offset]));
    }

    private static long Covered(IEnumerable<SpeakerInterval> intervals, long start, long end)
    {
        long covered = 0, last = start;
        foreach (var interval in intervals)
        { long left = Math.Max(last, Math.Max(start, interval.StartMs)), right = Math.Min(end, interval.EndMs); if (right > left) covered += right - left; last = Math.Max(last, right); }
        return covered;
    }

    private static bool HasLocalOverlap(IReadOnlyList<SpeakerInterval> intervals, long start, long end)
    {
        string? longestSpeaker = null; long longestEnd = start;
        foreach (var interval in intervals)
        {
            if (interval.SpeakerId is null) { if (interval.Overlap) return true; continue; }
            long left = Math.Max(start, interval.StartMs), right = Math.Min(end, interval.EndMs);
            if (right <= left) continue;
            if (longestSpeaker is not null && longestSpeaker != interval.SpeakerId && longestEnd > left) return true;
            if (right > longestEnd) { longestEnd = right; longestSpeaker = interval.SpeakerId; }
        }
        return false;
    }

    private static SpeakerSegment New(TranscriptSegment source, int offset, int length, long? start, long? end,
        string? speakerId, string status, IReadOnlyList<SpeakerCandidateBoundary> changes, string timingSource = "source-subtitle") =>
        new(Guid.NewGuid().ToString("N"), source.SegmentId, offset, length, start, end,
            source.Text.Substring(offset, length), speakerId, status, changes, timingSource);
}
