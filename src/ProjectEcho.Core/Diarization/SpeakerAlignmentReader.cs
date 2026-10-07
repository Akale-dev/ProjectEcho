using System.Text;
using System.Text.Json;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.Core.Diarization;

/// <summary>Reuses acoustic evidence belonging to the chosen text/audio. Missing evidence is never guessed.</summary>
public static class SpeakerAlignmentReader
{
    public static SpeakerMappingEvidence Read(string meetingDirectory, TranscriptRevision source, SpeakerAudioBinding audio,
        CancellationToken cancellationToken = default)
    {
        SpeakerService.ValidateAudio(audio); var boundaries = new List<SpeakerTextBoundary>(); var warnings = new List<string>();
        if (source.Alignment is not { } provenance)
            warnings.Add("所选旧稿或导入字幕没有已绑定的词级证据；可以整句标人，句内换人保留全部文字并待人工切点。");
        else
        {
            if (audio.InputWaveSha256 != provenance.InputWaveSha256 || audio.SourceMixSha256 != provenance.SourceMixSha256 ||
                audio.FrameCount != provenance.FrameCount || audio.SampleRate != provenance.SampleRate || audio.TimelinePolicy != provenance.TimelinePolicy)
                throw new InvalidDataException("不能把另一音频或时间轴的音文定位套到本场说话人分析。");
            byte[] bytes = TranscriptFiles.ReadBounded(TranscriptFiles.Inside(meetingDirectory, provenance.EvidenceFile), cancellationToken, 32 * 1024 * 1024);
            if (TranscriptFiles.Hash(bytes) != provenance.EvidenceSha256) throw new InvalidDataException("音文定位证据哈希改变。");
            using var json = TranscriptFiles.ParseJson(bytes);
            string originalId = source.OriginalFile.Split('/')[2];
            var original = TranscriptService.Load(meetingDirectory, originalId, cancellationToken);
            var originalSegments = original.Segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
            var unchanged = source with { Segments = source.Segments.Where(segment => originalSegments.TryGetValue(segment.SegmentId, out var before) &&
                before.Text == segment.Text && before.StartMs == segment.StartMs && before.EndMs == segment.EndMs).ToArray() };
            try
            {
                if (provenance.Policy == "whisper-exact-token-boundaries-v1")
                {
                    var normalized = TranscriptParser.Parse(TranscriptFiles.ReadBounded(TranscriptFiles.Inside(meetingDirectory, original.OriginalFile), cancellationToken),
                        Path.GetExtension(original.OriginalFile), original.MeetingId, []);
                    normalized = WhisperSentenceSegmenter.Normalize(normalized, TranscriptFiles.Inside(meetingDirectory, provenance.EvidenceFile),
                        original.MeetingDurationMs, [], cancellationToken);
                    normalized = WhisperTimestampBoundary.Normalize(normalized, original.MeetingDurationMs, []);
                    if (!normalized.SequenceEqual(original.Segments)) throw new InvalidDataException("Whisper 原稿与保存的分句/合并规则不一致。");
                    ReadWhisper(original, json.RootElement, boundaries, cancellationToken);
                    var usable = unchanged.Segments.Select(segment => segment.SegmentId).ToHashSet(StringComparer.Ordinal);
                    boundaries.RemoveAll(boundary => !usable.Contains(boundary.SegmentId));
                }
                else if (provenance.Policy == QwenTranscriber.Policy) ReadQwen(meetingDirectory, source, json.RootElement, boundaries, warnings, cancellationToken);
                else if (provenance.Policy == QwenTranscriber.PartialAlignmentPolicy)
                    ReadQwenPartial(meetingDirectory, source, original, json.RootElement, boundaries, warnings, cancellationToken);
                else warnings.Add("此音文定位策略未验证句内词界；保留句级时间并待人工切点。");
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            { boundaries.Clear(); warnings.Add("未通过严格原文/词界核对，句内换人保留待确认：" + exception.Message); }
        }
        if (source.ParentRevisionId is not null) warnings.Add("人工改字的段只复用与当前文字完全相同的原始词界；修改处没有重新定位。");
        return new(source.RevisionId, SpeakerService.TranscriptFingerprint(source), audio, boundaries.AsReadOnly(), warnings.AsReadOnly());
    }

    private static void ReadWhisper(TranscriptRevision source, JsonElement root, List<SpeakerTextBoundary> boundaries, CancellationToken cancellationToken)
    {
        int special = root.GetProperty("model").GetProperty("multilingual").GetBoolean() ? 50257 : 50256;
        var rawCharacters = new List<int>(); var tokens = new List<Word>();
        foreach (var entry in root.GetProperty("transcription").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text = entry.GetProperty("text").GetString() ?? "";
            long from = entry.GetProperty("offsets").GetProperty("from").GetInt64(), to = entry.GetProperty("offsets").GetProperty("to").GetInt64();
            int originalCount = rawCharacters.Count;
            rawCharacters.AddRange(text.EnumerateRunes().Where(rune => !Rune.IsWhiteSpace(rune)).Select(rune => rune.Value));
            var entryTokens = new List<Word>(); var combined = new StringBuilder(); long previousEnd = from; bool valid = true; int characterIndex = originalCount;
            if (!entry.TryGetProperty("tokens", out var array)) continue;
            foreach (var token in array.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                int id = token.GetProperty("id").GetInt32(); if (id >= special) continue;
                string part = token.GetProperty("text").GetString() ?? "";
                if (id < 0 || !token.TryGetProperty("offsets", out var offsets)) { valid = false; break; }
                long start = offsets.GetProperty("from").GetInt64(), end = offsets.GetProperty("to").GetInt64();
                if (start < previousEnd || end < start || end > to) { valid = false; break; }
                int textStart = characterIndex; combined.Append(part);
                characterIndex += part.EnumerateRunes().Count(rune => !Rune.IsWhiteSpace(rune));
                entryTokens.Add(new(textStart, characterIndex, start, end)); previousEnd = end;
            }
            if (!valid || combined.ToString() != text) continue;
            tokens.AddRange(entryTokens);
        }
        // Existing sentence normalization may trim cue whitespace or insert an English joining space.
        // Its exact output was checked above. Match every remaining Unicode scalar, including punctuation,
        // in sequence, so merged decode windows retain token lineage without inventing any word time.
        var selectedCharacters = new List<(int Value, TranscriptSegment Segment, int Offset)>();
        foreach (var segment in source.Segments)
        {
            int offset = 0;
            foreach (Rune rune in segment.Text.EnumerateRunes())
            {
                if (!Rune.IsWhiteSpace(rune)) selectedCharacters.Add((rune.Value, segment, offset));
                offset += rune.Utf16SequenceLength;
            }
        }
        if (selectedCharacters.Count != rawCharacters.Count || selectedCharacters.Where((character, index) => character.Value != rawCharacters[index]).Any())
            throw new InvalidDataException("Whisper 保存原稿与 token 文字顺序不一致。");
        for (int index = 1; index < tokens.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = tokens[index - 1]; var right = tokens[index];
            if (left.TextStart == left.TextEnd || right.TextStart == right.TextEnd || left.TextEnd != right.TextStart ||
                left.EndMs <= left.StartMs || right.EndMs <= right.StartMs || left.EndMs > right.StartMs) continue;
            var before = selectedCharacters[left.TextEnd - 1]; var after = selectedCharacters[right.TextStart];
            var segment = after.Segment;
            // Untimed text is preserved in the character lineage, but cannot acquire word times
            // from raw out-of-range/zero-duration model output. Nullable comparisons alone would
            // evaluate false and let a boundary through. Also reject a pair crossing real EOF.
            if (segment.StartMs is not long segmentStart || segment.EndMs is not long segmentEnd ||
                segmentStart < 0 || segmentEnd <= segmentStart || segmentEnd > source.MeetingDurationMs ||
                left.StartMs < 0 || right.EndMs > source.MeetingDurationMs ||
                before.Segment.SegmentId != segment.SegmentId || after.Offset <= 0 || after.Offset >= segment.Text.Length ||
                left.EndMs <= segmentStart || right.StartMs >= segmentEnd || !SpeakerService.IsTextBoundary(segment.Text, after.Offset)) continue;
            boundaries.Add(new(segment.SegmentId, after.Offset, left.EndMs, right.StartMs, "whisper-exact-token-boundary"));
        }
    }

    private static void ReadQwenPartial(string meeting, TranscriptRevision selected, TranscriptRevision original,
        JsonElement root, List<SpeakerTextBoundary> boundaries, List<string> warnings, CancellationToken token)
    {
        // Evidence belongs to the immutable original. Reviewed text may differ, so validate the
        // original first and only reuse words for individually unchanged selected segments.
        string originalText = TranscriptFiles.Decode(TranscriptFiles.ReadBounded(
            TranscriptFiles.Inside(meeting, original.OriginalFile), token));
        var proofs = QwenPartialTranscriptEvidence.Validate(root, original, originalText);
        string attemptId = root.GetProperty("attempt").GetString() ?? "";
        if (!Guid.TryParseExact(attemptId, "N", out _)) throw new InvalidDataException("Qwen 原始定位目录无效。");
        string attempt = TranscriptFiles.Inside(meeting, "transcripts/asr/" + attemptId);
        var byId = selected.Segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        foreach (var proof in proofs)
        {
            token.ThrowIfCancellationRequested();
            string path = TranscriptFiles.Inside(attempt, proof.RawWords);
            if (!File.Exists(path))
            {
                warnings.Add("部分 Qwen 原始词界已不存在；对应段不复用词界，其它窗口按显式段标识核对。");
                continue;
            }
            byte[] bytes = TranscriptFiles.ReadBounded(path, token);
            if (TranscriptFiles.Hash(bytes) != proof.RawWordsSha256)
                throw new InvalidDataException("Qwen 原始词界哈希改变。");
            var inspected = QwenAlignmentSentenceMapper.Inspect(proof.Text, bytes, proof.Window.InputFrameCount);
            if (proof.Status == "untimed")
            {
                if (inspected.TimingComplete || inspected.TimingIssueCount != proof.TimingIssueCount ||
                    !inspected.TimingIssues.SequenceEqual(proof.TimingIssues))
                    throw new InvalidDataException("Qwen 待定位段与保留的原始时间异常不一致。");
                continue; // Keep all words, but never invent a time or speaker from this window.
            }
            var local = proof.Sentences.Select(sentence => new QwenWindowSentence(sentence.TextStart, sentence.TextLength,
                sentence.StartFrame - proof.Window.InputStartFrame, sentence.EndFrame - proof.Window.InputStartFrame)).ToArray();
            if (!inspected.TimingComplete || !inspected.Sentences.SequenceEqual(local))
                throw new InvalidDataException("Qwen 正常窗口的保存句子与严格原始词界不一致。");
            using var json = TranscriptFiles.ParseJson(bytes);
            var characters = Characters(proof.Text); var words = new List<Word>(); int cursor = 0;
            foreach (var item in json.RootElement.EnumerateArray())
            {
                var value = Characters(item.GetProperty("word").GetString() ?? ""); int begin = cursor;
                foreach (var character in value)
                {
                    if (cursor >= characters.Count || character.Value != characters[cursor].Value)
                        throw new InvalidDataException("Qwen 词界文字不匹配。");
                    cursor++;
                }
                if (cursor == begin) throw new InvalidDataException("Qwen 空词界。");
                long start = proof.Window.InputStartFrame + item.GetProperty("start_sample").GetInt64();
                long end = proof.Window.InputStartFrame + item.GetProperty("end_sample").GetInt64();
                words.Add(new(characters[begin].Start, characters[cursor - 1].End,
                    start * 1000 / 16000, (end * 1000 + 15999) / 16000));
            }
            for (int index = 0; index < proof.Sentences.Length; index++)
            {
                var sentence = proof.Sentences[index]; string id = proof.SegmentIds[index];
                if (!byId.TryGetValue(id, out var segment) || segment.StartMs is null || segment.EndMs is null ||
                    segment.Text != sentence.Text || segment.StartMs != sentence.StartMs ||
                    Math.Abs(segment.EndMs.Value - sentence.EndMs) > 1) continue;
                int offset = sentence.TextStart;
                for (int wordIndex = 1; wordIndex < words.Count; wordIndex++)
                {
                    var left = words[wordIndex - 1]; var right = words[wordIndex];
                    int cut = right.TextStart - offset;
                    if (cut <= 0 || cut >= segment.Text.Length || left.TextStart < offset ||
                        right.TextEnd > offset + segment.Text.Length || left.StartMs == left.EndMs ||
                        right.StartMs == right.EndMs || left.EndMs > right.StartMs ||
                        left.EndMs <= segment.StartMs || right.StartMs >= segment.EndMs ||
                        !SpeakerService.IsTextBoundary(segment.Text, cut)) continue;
                    boundaries.Add(new(id, cut, left.EndMs, right.StartMs, "qwen-raw-acoustic-word-boundary"));
                }
            }
        }
    }

    private static void ReadQwen(string meeting, TranscriptRevision source, JsonElement root, List<SpeakerTextBoundary> boundaries,
        List<string> warnings, CancellationToken token)
    {
        if (root.GetProperty("schemaVersion").GetString() != "m4-qwen-alignment-1" || root.GetProperty("expectedWaveSha256").GetString() != source.Alignment!.InputWaveSha256 ||
            root.GetProperty("timelinePolicy").GetString() != source.Alignment.TimelinePolicy)
            throw new InvalidDataException("Qwen 定位来源不一致。");
        string attemptId = root.GetProperty("attempt").GetString() ?? "";
        if (!Guid.TryParseExact(attemptId, "N", out _)) throw new InvalidDataException("Qwen 原始定位目录无效。");
        string attempt = TranscriptFiles.Inside(meeting, "transcripts/asr/" + attemptId);
        var byId = source.Segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        int segmentIndex = 0;
        foreach (var window in root.GetProperty("windowEvidence").EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var sentences = window.GetProperty("sentences").EnumerateArray().ToArray();
            string text = string.Concat(sentences.Select(sentence => sentence.GetProperty("text").GetString()));
            if (QwenWindowAlignment.TextSha256(text) != window.GetProperty("evidence").GetProperty("originalTextSha256").GetString())
                throw new InvalidDataException("Qwen 窗口原文缺失或变化。");
            string rawName = window.GetProperty("rawWords").GetString() ?? "";
            if (Path.GetFileName(rawName) != rawName) throw new InvalidDataException("Qwen 词界文件路径不安全。");
            string rawPath = TranscriptFiles.Inside(attempt, rawName);
            if (!File.Exists(rawPath))
            { warnings.Add("部分 Qwen 原始词界已不存在；这些句内换人仍待确认。"); segmentIndex += sentences.Length; continue; }
            byte[] raw = TranscriptFiles.ReadBounded(rawPath, token, 8 * 1024 * 1024);
            if (TranscriptFiles.Hash(raw) != window.GetProperty("rawWordsSha256").GetString())
                throw new InvalidDataException("Qwen 原始词界哈希改变。");
            long inputStart = window.GetProperty("window").GetProperty("inputStartFrame").GetInt64();
            long frames = window.GetProperty("window").GetProperty("inputFrameCount").GetInt64();
            // The established M4 validator must accept the same original raw boundaries first.
            QwenAlignmentSentenceMapper.Map(text, rawPath, frames);
            using var wordsJson = TranscriptFiles.ParseJson(raw); var characters = Characters(text); int cursor = 0;
            var words = new List<Word>();
            foreach (var word in wordsJson.RootElement.EnumerateArray())
            {
                var wordCharacters = Characters(word.GetProperty("word").GetString() ?? ""); int begin = cursor;
                foreach (var character in wordCharacters)
                { if (cursor >= characters.Count || character.Value != characters[cursor].Value) throw new InvalidDataException("Qwen 词界文字不匹配。"); cursor++; }
                if (cursor == begin) throw new InvalidDataException("Qwen 空词界。");
                long startFrame = inputStart + word.GetProperty("start_sample").GetInt64(), endFrame = inputStart + word.GetProperty("end_sample").GetInt64();
                words.Add(new(characters[begin].Start, characters[cursor - 1].End, startFrame * 1000 / 16000, (endFrame * 1000 + 15999) / 16000));
            }
            foreach (var sentence in sentences)
            {
                string id = $"segment-{++segmentIndex:D6}";
                byId.TryGetValue(id, out var selected);
                string original = sentence.GetProperty("text").GetString() ?? "";
                long start = sentence.GetProperty("startMs").GetInt64(), end = sentence.GetProperty("endMs").GetInt64();
                if (selected is null || selected.Text != original || selected.StartMs != start || Math.Abs(selected.EndMs!.Value - end) > 1) continue;
                int offset = sentence.GetProperty("textStart").GetInt32();
                for (int i = 1; i < words.Count; i++)
                {
                    int cut = words[i].TextStart - offset;
                    if (cut <= 0 || cut >= original.Length || words[i - 1].TextStart < offset || words[i].TextEnd > offset + original.Length ||
                        words[i - 1].StartMs == words[i - 1].EndMs || words[i].StartMs == words[i].EndMs ||
                        words[i - 1].EndMs > words[i].StartMs || !SpeakerService.IsTextBoundary(original, cut)) continue;
                    boundaries.Add(new(id, cut, words[i - 1].EndMs, words[i].StartMs, "qwen-raw-acoustic-word-boundary"));
                }
            }
        }
    }

    private static List<Character> Characters(string text)
    {
        var result = new List<Character>(); int offset = 0;
        foreach (Rune rune in text.EnumerateRunes())
        { if (Rune.IsLetterOrDigit(rune) || rune.Value == '\'') result.Add(new(rune.Value, offset, offset + rune.Utf16SequenceLength)); offset += rune.Utf16SequenceLength; }
        return result;
    }
    private readonly record struct Character(int Value, int Start, int End);
    private readonly record struct Word(int TextStart, int TextEnd, long StartMs, long EndMs);
}
