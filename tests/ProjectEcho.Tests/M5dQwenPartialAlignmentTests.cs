using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;

internal static class M5dQwenPartialAlignmentTests
{
    private const string Text = "第一句。末句。";
    private const string PartialPolicy = "qwen3-transcribe-verified-text-partial-alignment-v2";
    private const string LegacyPolicy = "qwen3-transcribe-raw-forced-alignment-v1";

    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m5d-qwen-partial-alignment"); Directory.CreateDirectory(root);
        run("P4-02/M5D SYNTH timing inspection is typed bounded and strict Map still rejects every bad time", () => Inspection(root));
        run("P4-02/M5D WINDOWS SYNTHETIC CLI early timing fault cannot hide late missing changed or malformed words", () => CorruptText(root));
        run("P4-02/P5-02/M5D WINDOWS SYNTHETIC CLI bad first window preserves good next-window explicit IDs and word boundaries", () => FirstBadThenGood(root));
        run("P4-02/P5-02/M5D WINDOWS SYNTHETIC CLI all untimed windows preserve original TXT JSON search edits and no speaker inference", () => AllUntimed(root));
        run("P4-02/M5D SYNTH text coverage and complete acoustic alignment remain separate", Coverage);
        run("P4-02/P5-02/M5D WINDOWS SYNTHETIC CLI swapped duplicated and altered alignment evidence cannot authorize word cuts", () => Tampering(root));
        run("P4-02/P5-02/M5D WINDOWS SYNTHETIC CLI legacy v1 Qwen evidence remains readable with original word cuts", () => Legacy(root));
        run("P4-02/M5D WINDOWS SYNTHETIC CLI every numeric timing failure saves text without clipping or epsilon", () => TimingVariants(root));
    }

    private static void Inspection(string root)
    {
        foreach (var (mode, kind) in new[]
        {
            ("m5d-all-reverse", QwenAlignmentTimingIssueKind.NonMonotonicStart),
            ("m5d-outside", QwenAlignmentTimingIssueKind.OutsideWindow),
            ("m5d-negative", QwenAlignmentTimingIssueKind.NegativeBoundary),
            ("m5d-overlap-sentences", QwenAlignmentTimingIssueKind.OverlappingSentences),
            ("m5d-zero-sentence", QwenAlignmentTimingIssueKind.NonPositiveSentence)
        })
        {
            string path = Path.Combine(root, mode + ".json"); WriteFixtureWords(path, "w000000", mode, 16000);
            byte[] raw = File.ReadAllBytes(path); string before = M3TranscriptTests.Hash(path);
            var inspected = QwenAlignmentSentenceMapper.Inspect(Text, raw, 16000);
            Need(!inspected.TimingComplete && inspected.Sentences.Count == 0 && inspected.TimingIssueCount > 0 &&
                inspected.TimingIssues.Any(issue => issue.Kind == kind), mode + " 未得到明确的时间异常分类。");
            Bad(() => QwenAlignmentSentenceMapper.Map(Text, path, 16000));
            Need(M3TranscriptTests.Hash(path) == before, "严格Map或Inspect改写了原词界。");
        }
        string manyText = new('甲', 100);
        byte[] many = JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0, 100).Select(_ =>
            new { word = "甲", start_sample = -2, end_sample = -1 }));
        var bounded = QwenAlignmentSentenceMapper.Inspect(manyText, many, 16000);
        Need(!bounded.TimingComplete && bounded.TimingIssueCount >= 100 && bounded.TimingIssues.Count <= 64,
            "逐词诊断无界，或被诊断条数上限误截断检测。");
    }

    private static void CorruptText(string root)
    {
        foreach (string mode in new[] { "m5d-late-text-fault", "m5d-late-missing-word", "m5d-late-json-fault",
            "m5d-late-number-type", "m5d-late-null-word" })
        {
            var request = Request(root, mode, mode); var result = Run(request);
            Need(result.Status == LocalTranscriptionStatus.Failed && result.Revision is null &&
                TranscriptService.ListRevisions(request.MeetingDirectory).Count == 0,
                mode + " 因先看到时间错而吞掉了后续文字/结构损坏。");
            string raw = Path.Combine(result.AttemptDirectory, "window-000000.words.json");
            Need(File.Exists(raw) && File.Exists(Path.Combine(result.AttemptDirectory, "window-000000.txt")), "失败丢失原始输出。");
            Bad(() => QwenAlignmentSentenceMapper.Inspect(Text, File.ReadAllBytes(raw), 16000));
            Need(M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256, "结构拒绝流程改变原音频。");
        }
    }

    private static void FirstBadThenGood(string root)
    {
        var request = Request(root, "first-bad", "m5d-first-reverse", seconds: 31); var result = Run(request); var revision = Success(result);
        Need(revision.Segments.Count == 3 && revision.Segments[0] is
            { SegmentId: "segment-000001", StartMs: null, EndMs: null, TimestampQuality: "untimed", Text: Text },
            "坏首窗未按完整原文保存为唯一无时间段。");
        Need(revision.Segments[1] is { SegmentId: "segment-000002", StartMs: 15600, EndMs: 15900, Text: "第一句。" } &&
            revision.Segments[2] is { SegmentId: "segment-000003", StartMs: 16100, EndMs: 16400, Text: "末句。" },
            "正常后窗的段ID或真实窗口偏移错误。");
        using var evidence = JsonDocument.Parse(File.ReadAllBytes(EvidencePath(request, revision)));
        var windows = evidence.RootElement.GetProperty("windowEvidence");
        Need(windows[0].GetProperty("alignmentStatus").GetString() == "untimed" &&
            windows[0].GetProperty("transcriptSegmentIds")[0].GetString() == "segment-000001" &&
            windows[1].GetProperty("alignmentStatus").GetString() == "aligned" &&
            windows[1].GetProperty("transcriptSegmentIds").EnumerateArray().Select(id => id.GetString()).SequenceEqual(new[] { "segment-000002", "segment-000003" }),
            "逐窗证据没有明确绑定含untimed后的真正段ID。");
        var wordEvidence = SpeakerAlignmentReader.Read(request.MeetingDirectory, revision, Audio(request));
        Need(wordEvidence.Boundaries.Count > 0 && wordEvidence.Boundaries.All(boundary => boundary.SegmentId is "segment-000002" or "segment-000003") &&
            wordEvidence.Boundaries.Any(boundary => boundary.SegmentId == "segment-000002" && boundary.TextOffset == 1 &&
                boundary.LeftEndMs == 15700 && boundary.RightStartMs == 15700),
            "前坏窗使后好窗词界错位、全部失效或套用到无时间段。");
        var edited = TranscriptService.CreateRevision(request.MeetingDirectory, revision.RevisionId,
            revision.Segments.Select((cue, index) => index == 1 ? cue with { Text = "人工改写后句。" } : cue).ToArray());
        var editedWords = SpeakerAlignmentReader.Read(request.MeetingDirectory, edited, Audio(request));
        Need(editedWords.Boundaries.All(boundary => boundary.SegmentId != "segment-000002") &&
            editedWords.Boundaries.Any(boundary => boundary.SegmentId == "segment-000003"),
            "人工改字仍复用旧词界，或错误连带禁用了未改段。");
        CheckSaved(request, result, 2, 1);
    }

    private static void AllUntimed(string root)
    {
        var request = Request(root, "all-untimed", "m5d-all-reverse", seconds: 61); var result = Run(request); var revision = Success(result);
        Need(revision.Segments.Count == 3 && revision.Segments.All(cue => cue.StartMs is null && cue.EndMs is null &&
            cue.TimestampQuality == "untimed" && cue.Text == Text), "全部待定位时丢弃原稿或使用窗口时间冒充句子时间。");
        var loaded = TranscriptService.Load(request.MeetingDirectory, revision.RevisionId);
        Need(loaded.Segments.SequenceEqual(revision.Segments), "全部待定位的JSON不能完整重开。");
        string txt = Path.Combine(root, "all-untimed.txt");
        TranscriptSubtitleExporter.ExportText(request.MeetingDirectory, revision.RevisionId, txt);
        Need(File.ReadAllText(txt) == string.Concat(Enumerable.Repeat(Text + "\n", 3)), "TXT没有完整保留各窗原文和次序。");
        Need(TranscriptService.Search(request.MeetingDirectory, "第一句").Any(hit => hit.RevisionId == revision.RevisionId && hit.StartMs is null),
            "全部无时间原文没有进入检索或检索猜出了时码。");
        foreach (string extension in new[] { ".srt", ".vtt" })
        {
            string target = Path.Combine(root, "cannot-drop-untimed" + extension);
            Bad(() => TranscriptSubtitleExporter.Export(request.MeetingDirectory, revision.RevisionId, target));
            Need(!File.Exists(target), "同步字幕静默漏掉无时间文字。");
        }
        var review = TranscriptService.CreateRevision(request.MeetingDirectory, revision.RevisionId,
            revision.Segments.Select((cue, index) => index == 1 ? cue with { Text = "合成人工校订，保持无时间。" } : cue).ToArray());
        Need(TranscriptService.Load(request.MeetingDirectory, review.RevisionId).Segments[1] is
            { StartMs: null, Text: "合成人工校订，保持无时间。" }, "无时间文字不能校订/重开。");
        var speaker = new MeetingSpeaker(Guid.NewGuid().ToString("N"), "A");
        var analysis = new SpeakerAnalysis(1, request.MeetingId, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            Audio(request), new("synthetic", "fixture", new('1', 64), new('2', 64), new('3', 64), 1, .9, "CPU", "synthetic-only"),
            [speaker], [new(0, request.MeetingDurationMs, speaker.SpeakerId)], [], true);
        var mapping = SpeakerAlignmentReader.Read(request.MeetingDirectory, revision, analysis.Audio);
        var mapped = SpeakerMapper.Map(revision, analysis, mapping);
        Need(mapping.Boundaries.Count == 0 && mapped.Count == 3 && mapped.All(cue => cue.Status == "untimed" &&
            cue.SpeakerId is null && cue.StartMs is null) && string.Concat(mapped.Select(cue => cue.Text)) == Text + Text + Text,
            "没有定位的文字被自动归给人物，或人物映射丢字。");
        string rawBefore = M3TranscriptTests.Hash(Path.Combine(request.MeetingDirectory, revision.OriginalFile));
        var rerun = Run(request);
        Need(Success(rerun).RevisionId != revision.RevisionId && rerun.AttemptDirectory != result.AttemptDirectory &&
            M3TranscriptTests.Hash(Path.Combine(request.MeetingDirectory, revision.OriginalFile)) == rawBefore &&
            TranscriptService.Load(request.MeetingDirectory, review.RevisionId).Segments[1].Text == "合成人工校订，保持无时间。",
            "重试覆写原文/人工稿。");
        CheckSaved(request, result, 3, 3);
    }

    private static void Coverage()
    {
        var plan = QwenWindowPlan.Create(48000, 16000, timelinePolicy: "pause-excluded-v1");
        string wave = new('a', 64), text = QwenWindowAlignment.TextSha256(Text);
        QwenWindowOutcome[] outcomes = [new(0, wave, QwenWindowStatus.TranscribedUntimed, text, 1),
            new(1, wave, QwenWindowStatus.Aligned, text, 2), new(2, wave, QwenWindowStatus.DigitalSilence)];
        var result = QwenWindowCoverage.Evaluate(plan, outcomes);
        Need(!result.Complete && result.CoveredFrames == 32000 && result.TextComplete && result.TextCoveredFrames == 48000 &&
            result.IncompleteWindowIndexes.SequenceEqual(new[] { 0 }) && result.UntimedWindowIndexes.SequenceEqual(new[] { 0 }) &&
            result.IncompleteTextWindowIndexes.Count == 0, "识字完成冒充全部定位完成。");
        Bad(() => QwenWindowCoverage.Evaluate(plan, outcomes.Select((value, index) => index == 0 ? value with { OriginalTextSha256 = null } : value).ToArray()));
        Bad(() => QwenWindowCoverage.Evaluate(plan, outcomes.Select((value, index) => index == 0 ? value with { SentenceCount = 0 } : value).ToArray()));
        var missing = QwenWindowCoverage.Evaluate(plan, outcomes[..2]);
        Need(!missing.TextComplete && missing.IncompleteTextWindowIndexes.SequenceEqual(new[] { 2 }), "遗漏窗口仍被计作全文覆盖。");
        var context = QwenWindowCoverage.Evaluate(plan, outcomes.Select((value, index) => index == 0 ? value with { HasUnresolvedContext = true } : value).ToArray());
        Need(!context.TextComplete, "未解决上下文被计作保真全文。");
    }

    private static void Tampering(string root)
    {
        foreach (string variant in new[] { "evidence-bytes", "word-bytes", "id-swap", "id-duplicate", "id-unknown", "raw-swap" })
        {
            var request = Request(root, "tamper-" + variant, "m5d-first-reverse", seconds: 31); var result = Run(request); var revision = Success(result);
            string evidencePath = EvidencePath(request, revision);
            if (variant == "evidence-bytes") File.AppendAllText(evidencePath, " ");
            else if (variant == "word-bytes") File.AppendAllText(Path.Combine(result.AttemptDirectory, "window-000001.words.json"), " ");
            else
            {
                var evidence = JsonNode.Parse(File.ReadAllText(evidencePath))!; var windows = evidence["windowEvidence"]!.AsArray();
                var goodIds = windows[1]!["transcriptSegmentIds"]!.AsArray();
                if (variant == "id-swap") goodIds[0] = windows[0]!["transcriptSegmentIds"]![0]!.GetValue<string>();
                else if (variant == "id-duplicate") goodIds[1] = goodIds[0]!.GetValue<string>();
                else if (variant == "id-unknown") goodIds[0] = "segment-999999";
                else
                {
                    windows[1]!["rawWords"] = windows[0]!["rawWords"]!.GetValue<string>();
                    windows[1]!["rawWordsSha256"] = windows[0]!["rawWordsSha256"]!.GetValue<string>();
                }
                File.WriteAllText(evidencePath, evidence.ToJsonString());
                RebindEvidenceHash(request, revision, M3TranscriptTests.Hash(evidencePath));
            }
            bool rejected = false;
            try
            {
                var loaded = TranscriptService.Load(request.MeetingDirectory, revision.RevisionId);
                var words = SpeakerAlignmentReader.Read(request.MeetingDirectory, loaded, Audio(request));
                rejected = words.Boundaries.Count == 0 && words.Warnings.Count > 0;
            }
            catch (InvalidDataException) { rejected = true; }
            Need(rejected, variant + " 的损坏/冒用证据仍授权了人物词界切分。");
            Need(M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256, "证据损坏检查改变音频。");
        }
    }

    private static void Legacy(string root)
    {
        var request = Request(root, "legacy-v1"); var result = Run(request); var revision = Success(result);
        string evidencePath = EvidencePath(request, revision); var evidence = JsonNode.Parse(File.ReadAllText(evidencePath))!;
        evidence["schemaVersion"] = "m4-qwen-alignment-1"; evidence["policy"] = LegacyPolicy;
        foreach (var window in evidence["windowEvidence"]!.AsArray())
        {
            window!.AsObject().Remove("transcriptSegmentIds"); window.AsObject().Remove("alignmentStatus");
            window.AsObject().Remove("untimedText"); window.AsObject().Remove("timingIssues"); window.AsObject().Remove("timingIssueCount");
        }
        File.WriteAllText(evidencePath, evidence.ToJsonString());
        RebindEvidenceHash(request, revision, M3TranscriptTests.Hash(evidencePath), LegacyPolicy);
        var old = TranscriptService.Load(request.MeetingDirectory, revision.RevisionId);
        var boundaries = SpeakerAlignmentReader.Read(request.MeetingDirectory, old, Audio(request));
        Need(old.Alignment!.Policy == LegacyPolicy && old.Segments.SequenceEqual(revision.Segments) && boundaries.Boundaries.Count > 0 &&
            boundaries.Boundaries.Any(boundary => boundary.SegmentId == "segment-000001" && boundary.TextOffset == 1 &&
                boundary.LeftEndMs == 200 && boundary.RightStartMs == 200), "既有v1原稿不再能复用真实词界。");
    }

    private static void TimingVariants(string root)
    {
        foreach (string mode in new[] { "m5d-outside", "m5d-negative", "m5d-overlap-sentences", "m5d-zero-sentence" })
        {
            var request = Request(root, mode, mode); var result = Run(request); var revision = Success(result);
            Need(revision.Segments.Single() is { StartMs: null, EndMs: null, Text: Text, TimestampQuality: "untimed" },
                mode + " 被裁边界、添1ms或丢弃原文。");
            CheckSaved(request, result, 1, 1);
        }
    }

    private static void CheckSaved(LocalTranscriptionRequest request, LocalTranscriptionResult result, int windows, int untimed)
    {
        var revision = result.Revision!;
        Need(revision.Alignment?.Policy == PartialPolicy && revision.Segments.Count(cue => cue.StartMs is null) == untimed &&
            string.Concat(revision.Segments.Select(cue => cue.Text)) == string.Concat(Enumerable.Repeat(Text, windows)) &&
            File.ReadAllText(Path.Combine(request.MeetingDirectory, revision.OriginalFile)) == string.Join("\n\n", Enumerable.Repeat(Text, windows)),
            "Partial政策、全部原文/段顺序或原始全文不符。");
        using var attempt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(result.AttemptDirectory, "attempt.json")));
        Need(attempt.RootElement.GetProperty("textCoverageComplete").GetBoolean() &&
            !attempt.RootElement.GetProperty("alignmentComplete").GetBoolean(), "Succeeded被报告成全部定位完成。");
        Need(TranscriptService.Load(request.MeetingDirectory, revision.RevisionId).Segments.SequenceEqual(revision.Segments) &&
            M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256, "保存后重开失败或改变原音频。");
        using var evidence = JsonDocument.Parse(File.ReadAllBytes(EvidencePath(request, revision)));
        Need(evidence.RootElement.GetProperty("schemaVersion").GetString() == "m4-qwen-alignment-2", "缺少分窗待定位新证据版本。");
    }

    internal static void WriteFixtureWords(string path, string id, string behavior, long frames)
    {
        string[] words = ["第", "一", "句", "末", "句"];
        long[] start = [1600, 3200, 4800, 9600, 11200], end = [3200, 4800, 6400, 11200, 14400];
        if (behavior == "m5d-all-reverse" || behavior == "m5d-first-reverse" && id == "w000000") start[3] = 100;
        if (behavior == "m5d-outside") end[^1] = frames + 36;
        if (behavior == "m5d-negative") start[0] = -1;
        if (behavior == "m5d-overlap-sentences") start[3] = 6000;
        if (behavior == "m5d-zero-sentence") for (int i = 0; i < 3; i++) start[i] = end[i] = 0;
        if (behavior.StartsWith("m5d-late-", StringComparison.Ordinal)) start[0] = 4000;
        if (behavior == "m5d-late-text-fault") words[^1] = "错";
        var rows = words.Select((word, i) => new Dictionary<string, object?>
            { ["word"] = word, ["start_sample"] = start[i], ["end_sample"] = end[i], ["confidence"] = 0 }).ToList();
        if (behavior == "m5d-late-missing-word") rows.RemoveAt(rows.Count - 1);
        if (behavior == "m5d-late-number-type") rows[^1]["start_sample"] = "11200";
        if (behavior == "m5d-late-null-word") rows[^1]["word"] = null;
        string json = JsonSerializer.Serialize(rows);
        File.WriteAllText(path, behavior == "m5d-late-json-fault" ? json[..^2] : json, new UTF8Encoding(false));
    }

    private static void RebindEvidenceHash(LocalTranscriptionRequest request, TranscriptRevision revision, string hash, string? policy = null)
    {
        string path = Path.Combine(request.MeetingDirectory, "transcripts", "original", revision.RevisionId, "revision.json");
        var saved = JsonNode.Parse(File.ReadAllText(path))!; saved["alignment"]!["evidenceSha256"] = hash;
        if (policy is not null) saved["alignment"]!["policy"] = policy;
        File.WriteAllText(path, saved.ToJsonString());
    }
    private static LocalTranscriptionRequest Request(string root, string name, string mode = "ok", int seconds = 1) =>
        M4QwenAdapterTests.Request(root, name, mode, seconds: seconds);
    private static LocalTranscriptionResult Run(LocalTranscriptionRequest request) => new QwenTranscriber().RunAsync(request).GetAwaiter().GetResult();
    private static TranscriptRevision Success(LocalTranscriptionResult result)
    { Need(result.Status == LocalTranscriptionStatus.Succeeded && result.Revision is not null, result.Message); return result.Revision!; }
    private static SpeakerAudioBinding Audio(LocalTranscriptionRequest request) =>
        new(request.ExpectedWaveSha256, request.SourceMixSha256, 16000, request.MeetingDurationMs * 16, request.TimelinePolicy);
    private static string EvidencePath(LocalTranscriptionRequest request, TranscriptRevision revision) =>
        Path.Combine(request.MeetingDirectory, revision.Alignment!.EvidenceFile);
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Bad(Action action)
    { try { action(); } catch (Exception error) when (error is InvalidDataException or JsonException) { return; } throw new InvalidOperationException("损坏或无支持证据被接受。"); }
}
