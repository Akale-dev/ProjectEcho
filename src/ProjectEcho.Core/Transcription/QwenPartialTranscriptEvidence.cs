using System.Text.Json;

namespace ProjectEcho.Core.Transcription;

internal sealed record QwenPartialWindowProof(QwenWindow Window, string Status, string[] SegmentIds,
    string Text, QwenMappedSentence[] Sentences, string RawWords, string RawWordsSha256,
    QwenAlignmentTimingIssue[] TimingIssues, int TimingIssueCount);

/// <summary>Checks v2 partial-alignment evidence independently of the writer, before any speaker reuse.</summary>
internal static class QwenPartialTranscriptEvidence
{
    internal static IReadOnlyList<QwenPartialWindowProof> Validate(JsonElement root, TranscriptRevision transcript, string originalText)
    {
        var provenance = transcript.Alignment ?? throw new InvalidDataException("Qwen 部分定位稿缺少音频绑定。");
        Need(provenance.Policy == QwenTranscriber.PartialAlignmentPolicy &&
            root.GetProperty("schemaVersion").GetString() == "m4-qwen-alignment-2" &&
            root.GetProperty("policy").GetString() == QwenTranscriber.PartialAlignmentPolicy &&
            root.GetProperty("meetingId").GetString() == transcript.MeetingId &&
            root.GetProperty("expectedWaveSha256").GetString() == provenance.InputWaveSha256 &&
            root.GetProperty("sourceMixSha256").GetString() == provenance.SourceMixSha256 &&
            root.GetProperty("timelinePolicy").GetString() == provenance.TimelinePolicy &&
            root.GetProperty("frameCount").GetInt64() == provenance.FrameCount &&
            root.GetProperty("originalTextSha256").GetString() == QwenWindowAlignment.TextSha256(originalText) &&
            root.GetProperty("textCoverageComplete").GetBoolean() && !root.GetProperty("alignmentComplete").GetBoolean(),
            "Qwen 部分定位稿的身份、原文、音频或完成状态不一致。");
        var planValue = root.GetProperty("plan");
        Need(planValue.GetProperty("totalFrames").GetInt64() == provenance.FrameCount &&
            planValue.GetProperty("timelinePolicy").GetString() == provenance.TimelinePolicy, "Qwen 部分定位窗计划绑定无效。");
        var rawWindows = planValue.GetProperty("windows");
        Need(rawWindows.ValueKind == JsonValueKind.Array && rawWindows.GetArrayLength() is > 0 and <= 50000, "Qwen 部分定位窗口数量无效。");
        var windows = rawWindows.EnumerateArray().Select(ReadWindow).ToArray();
        var plan = QwenWindowPlan.Create(provenance.FrameCount, windows[0].OwnedFrameCount, timelinePolicy: provenance.TimelinePolicy);
        Need(windows.SequenceEqual(plan.Windows), "Qwen 部分定位窗顺序或数量不覆盖完整输入。");
        foreach (var window in windows) plan.ValidateWindow(window);
        var outcomes = JsonSerializer.Deserialize<QwenWindowOutcome[]>(root.GetProperty("outcomes").GetRawText(), TranscriptFiles.Json)
            ?? throw new InvalidDataException("Qwen 部分定位结果为空。");
        var coverage = QwenWindowCoverage.Evaluate(plan, outcomes);
        var savedCoverage = root.GetProperty("coverage");
        Need(coverage.TextComplete && !coverage.Complete && coverage.UntimedWindowIndexes.Count > 0 &&
            savedCoverage.GetProperty("textComplete").GetBoolean() && !savedCoverage.GetProperty("complete").GetBoolean() &&
            savedCoverage.GetProperty("totalFrames").GetInt64() == provenance.FrameCount &&
            savedCoverage.GetProperty("coveredFrames").GetInt64() == coverage.CoveredFrames &&
            savedCoverage.GetProperty("textCoveredFrames").GetInt64() == coverage.TextCoveredFrames &&
            savedCoverage.GetProperty("incompleteWindowIndexes").EnumerateArray().Select(value => value.GetInt32()).SequenceEqual(coverage.IncompleteWindowIndexes) &&
            savedCoverage.GetProperty("untimedWindowIndexes").EnumerateArray().Select(value => value.GetInt32()).SequenceEqual(coverage.UntimedWindowIndexes) &&
            savedCoverage.GetProperty("incompleteTextWindowIndexes").GetArrayLength() == 0,
            "Qwen 识字覆盖与定位覆盖不一致，不能发布完成文字稿。");
        var byIndex = outcomes.ToDictionary(outcome => outcome.WindowIndex);
        var seenWindows = new HashSet<int>(); var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var proofs = new List<QwenPartialWindowProof>(); var texts = new List<string>();
        int cursor = 0, lastWindow = -1;
        foreach (var item in root.GetProperty("windowEvidence").EnumerateArray())
        {
            var window = ReadWindow(item.GetProperty("window")); plan.ValidateWindow(window);
            Need(window.Index > lastWindow && seenWindows.Add(window.Index), "Qwen 部分定位窗口重复或反序。");
            lastWindow = window.Index;
            string status = item.GetProperty("alignmentStatus").GetString() ?? "";
            string[] ids = item.GetProperty("transcriptSegmentIds").EnumerateArray()
                .Select(value => value.GetString() ?? "").ToArray();
            var saved = JsonSerializer.Deserialize<QwenMappedSentence[]>(item.GetProperty("sentences").GetRawText(), TranscriptFiles.Json)
                ?? throw new InvalidDataException("Qwen 句子证据为空。");
            var evidence = JsonSerializer.Deserialize<QwenWindowAlignmentEvidence>(item.GetProperty("evidence").GetRawText(), TranscriptFiles.Json)
                ?? throw new InvalidDataException("Qwen 窗口证据为空。");
            string text = status == "untimed" ? item.GetProperty("untimedText").GetString() ?? "" : string.Concat(saved.Select(sentence => sentence.Text));
            var outcome = byIndex[window.Index];
            Need(status is "aligned" or "untimed" && ids.Length > 0 &&
                evidence.Source == "forced-alignment" && evidence.Version == QwenTranscriber.Policy &&
                evidence.ExecutableSha256 == root.GetProperty("hashes").GetProperty("alignerExecutable").GetString() &&
                evidence.ModelSha256 == root.GetProperty("hashes").GetProperty("alignerModel").GetString() &&
                TranscriptFiles.IsHash(evidence.ExecutableSha256) && TranscriptFiles.IsHash(evidence.ModelSha256) &&
                TranscriptFiles.IsHash(evidence.InputWaveSha256) && evidence.InputWaveSha256 == outcome.InputWaveSha256 &&
                evidence.OriginalTextSha256 == QwenWindowAlignment.TextSha256(text) &&
                evidence.OriginalTextSha256 == outcome.OriginalTextSha256 &&
                !string.IsNullOrWhiteSpace(text), "Qwen 窗口原文、识字结果或模型/音频绑定不一致。");
            string rawWords = item.GetProperty("rawWords").GetString() ?? "", rawHash = item.GetProperty("rawWordsSha256").GetString() ?? "";
            Need(rawWords == $"window-{window.Index:D6}.words.json" && TranscriptFiles.IsHash(rawHash),
                "Qwen 原始词界文件不属于对应窗口。");
            int issueCount = item.GetProperty("timingIssueCount").GetInt32();
            var issues = JsonSerializer.Deserialize<QwenAlignmentTimingIssue[]>(item.GetProperty("timingIssues").GetRawText(), TranscriptFiles.Json)
                ?? throw new InvalidDataException("Qwen 时间诊断缺失。");
            Need(ids.Length == outcome.SentenceCount && cursor + ids.Length <= transcript.Segments.Count, "Qwen 窗口段数与正文不一致。");
            foreach (string id in ids)
                Need(seenIds.Add(id) && transcript.Segments[cursor++].SegmentId == id, "Qwen 显式段标识重复、遗漏或不按原文顺序。");
            var selected = transcript.Segments.Skip(cursor - ids.Length).Take(ids.Length).ToArray();
            if (status == "untimed")
            {
                Need(outcome.Status == QwenWindowStatus.TranscribedUntimed && ids.Length == 1 && saved.Length == 0 &&
                    selected[0].Text == text && selected[0].StartMs is null && selected[0].EndMs is null &&
                    selected[0].TimestampQuality == "untimed" &&
                    issueCount > 0 && issues.Length == Math.Min(64, issueCount) &&
                    issues.All(issue => Enum.IsDefined(issue.Kind) && issue.WordNumber > 0 && issue.InputFrames == window.InputFrameCount),
                    "Qwen 待定位全文段与时间异常证据不一致。");
            }
            else
            {
                Need(outcome.Status == QwenWindowStatus.Aligned && issueCount == 0 && issues.Length == 0 && saved.Length == ids.Length,
                    "Qwen 已定位窗口状态或段数无效。");
                var local = saved.Select(sentence => new QwenWindowSentence(sentence.TextStart, sentence.TextLength,
                    sentence.StartFrame - window.InputStartFrame, sentence.EndFrame - window.InputStartFrame)).ToArray();
                var rebuilt = QwenWindowAlignment.Map(plan, window, text, outcome.InputWaveSha256, evidence, local);
                Need(rebuilt.SequenceEqual(saved), "Qwen 保存句子与原始帧映射不一致。");
                for (int index = 0; index < ids.Length; index++)
                {
                    var sentence = saved[index]; var segment = selected[index];
                    long endMs = sentence.EndMs;
                    if (endMs > transcript.MeetingDurationMs && sentence.EndFrame == provenance.FrameCount &&
                        endMs - transcript.MeetingDurationMs <= 1) endMs = transcript.MeetingDurationMs;
                    Need(sentence.Ownership == QwenWindowSentenceOwnership.Inside && segment.Text == sentence.Text &&
                        segment.StartMs == sentence.StartMs && segment.EndMs == endMs && segment.TimestampQuality == "approximate",
                        "Qwen 显式段标识与句子字文/时间不一致。");
                }
            }
            texts.Add(text); proofs.Add(new(window, status, ids, text, saved, rawWords, rawHash, issues, issueCount));
        }
        Need(cursor == transcript.Segments.Count &&
            seenWindows.SetEquals(outcomes.Where(outcome => outcome.Status is QwenWindowStatus.Aligned or QwenWindowStatus.TranscribedUntimed).Select(outcome => outcome.WindowIndex)) &&
            Canonical(string.Concat(texts)) == Canonical(originalText), "Qwen 部分定位证据遗漏已识别原文或窗口。");
        return proofs.AsReadOnly();
    }

    private static QwenWindow ReadWindow(JsonElement value) => new(value.GetProperty("index").GetInt32(),
        value.GetProperty("inputStartFrame").GetInt64(), value.GetProperty("inputFrameCount").GetInt32(),
        value.GetProperty("ownedStartFrame").GetInt64(), value.GetProperty("ownedFrameCount").GetInt32());
    private static string Canonical(string value) => string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
    private static void Need(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
}
