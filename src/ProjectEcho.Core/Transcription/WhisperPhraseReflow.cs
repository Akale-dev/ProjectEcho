using System.Text;

namespace ProjectEcho.Core.Transcription;

internal enum WhisperPhraseBoundary { Raw, Pause, Clause, Sentence }
internal sealed record WhisperPhraseFragment(TranscriptSegment Cue,
    WhisperPhraseBoundary Boundary = WhisperPhraseBoundary.Raw, string? SeparatorBefore = null);

/// <summary>Preserve words/punctuation; all candidate timestamps come from fully matched raw tokens.</summary>
internal static class WhisperPhraseReflow
{
    internal static WhisperPhraseBoundary PreferredBoundary(string text, int position, long gapMs)
    {
        if (position <= 0 || position >= text.Length) return WhisperPhraseBoundary.Raw;
        int previous = position - 1, next = position;
        if (char.IsWhiteSpace(text[previous])) return WhisperPhraseBoundary.Raw;
        while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
        if (previous < 0 || next >= text.Length) return WhisperPhraseBoundary.Raw;
        if (text[previous] is ',' or ';' or ':' or '，' or '；' or '：')
        {
            // Decimal/thousands/time punctuation inside one expression is not a clause.
            if (previous > 0 && char.IsDigit(text[previous - 1]) && char.IsDigit(text[next]))
                return WhisperPhraseBoundary.Raw;
            if (text[previous] is ',' or ';' or ':' && !char.IsWhiteSpace(text[position]))
                return WhisperPhraseBoundary.Raw;
            return WhisperPhraseBoundary.Clause;
        }
        // A token can be only part of a word. Require actual whitespace, so English subwords
        // and unspaced CJK text cannot acquire a guessed word boundary from token gaps.
        return gapMs >= 300 && gapMs <= 1000 &&
            (char.IsWhiteSpace(text[position - 1]) || char.IsWhiteSpace(text[position])) &&
            char.IsLetterOrDigit(text[previous]) && char.IsLetterOrDigit(text[next])
            ? WhisperPhraseBoundary.Pause : WhisperPhraseBoundary.Raw;
    }

    internal static List<TranscriptSegment> Merge(List<WhisperPhraseFragment> fragments,
        Func<string, bool> endsSentence, CancellationToken token, out int merges, out int preferredCuts)
    {
        var result = new List<TranscriptSegment>(); var pending = new List<WhisperPhraseFragment>();
        int cuts = 0, joined = 0, characterCount = 0, lastNatural = -1, lastRaw = -1;
        void Track(int index)
        {
            var fragment = pending[index]; characterCount += fragment.Cue.Text.Length + 1;
            if (fragment.Boundary == WhisperPhraseBoundary.Raw) lastRaw = index;
            if (fragment.Boundary is WhisperPhraseBoundary.Pause or WhisperPhraseBoundary.Clause &&
                characterCount - index - 1 >= 40 && fragment.Cue.EndMs - pending[0].Cue.StartMs >= 4000) lastNatural = index;
        }
        void Flush(int count)
        {
            TranscriptSegment cue = pending[0].Cue; var text = new StringBuilder(cue.Text);
            for (int i = 1; i < count; i++)
            {
                string next = pending[i].Cue.Text;
                // Within one raw cue, restore its actual separator. Reconstructing it from
                // ASCII character classes would concatenate accented/non-Latin words.
                if (pending[i].SeparatorBefore is string separator) text.Append(separator);
                else if (NeedsSpace(text[^1], next[0])) text.Append(' ');
                text.Append(next); joined++;
            }
            result.Add(cue with { EndMs = pending[count - 1].Cue.EndMs, Text = text.ToString() });
            pending.RemoveRange(0, count); characterCount = 0; lastNatural = lastRaw = -1;
            for (int i = 0; i < pending.Count; i++) Track(i);
        }
        foreach (var fragment in fragments)
        {
            token.ThrowIfCancellationRequested();
            if (pending.Count > 0 && !Connects(pending[^1].Cue, fragment.Cue)) Flush(pending.Count);
            while (pending.Count > 0 && (fragment.Cue.EndMs - pending[0].Cue.StartMs > 30000 ||
                characterCount + fragment.Cue.Text.Length >= 240))
            {
                if (lastNatural >= 0) { Flush(lastNatural + 1); cuts++; }
                else if (lastRaw >= 0) Flush(lastRaw + 1);
                else break; // No defensible boundary: preserve the original cue even if it is long.
            }
            pending.Add(fragment); Track(pending.Count - 1);
            if (endsSentence(fragment.Cue.Text) || fragment.Cue.StartMs is null) Flush(pending.Count);
        }
        if (pending.Count > 0) Flush(pending.Count);
        merges = joined; preferredCuts = cuts; return result;
    }

    private static bool Connects(TranscriptSegment left, TranscriptSegment right) =>
        left.StartMs is not null && left.EndMs is not null && right.StartMs is not null && right.EndMs is not null &&
        left.EndMs <= right.StartMs && right.StartMs - left.EndMs <= 1000 &&
        left.SourceKind == right.SourceKind && left.SpeakerLabel == right.SpeakerLabel;

    private static bool NeedsSpace(char left, char right) => char.IsAsciiLetterOrDigit(right) &&
        (char.IsAsciiLetterOrDigit(left) || left is ',' or ';' or ':' or '"' or '\'' or ')' or ']');
}
