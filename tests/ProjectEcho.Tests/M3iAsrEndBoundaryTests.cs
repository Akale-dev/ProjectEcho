using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ProjectEcho.Core.Transcription;

internal static class M3iAsrEndBoundaryTests
{
    private const string Prefix = "synthetic-m3e-timestamp-m3i-eof-";
    private const string Policy = "whisper-out-of-range-cues-own-in-range-tokens-or-untimed-v2";
    private const string TailText = "合成尾句甲。合成尾句乙。";
    private sealed record Token(int Id, string Text, long From, long To);
    private sealed record Cue(long Start, long End, string Text, Token[] Tokens);
    private sealed class ProgressLog : IProgress<LocalTranscriptionProgress>
    {
        internal readonly ConcurrentQueue<LocalTranscriptionProgress> Values = new();
        public void Report(LocalTranscriptionProgress value) => Values.Enqueue(value);
    }

    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3i-asr-end-boundary"); Directory.CreateDirectory(root);
        run("T35/T38-WINDOWS SYNTHETIC CLI M3I 914ms final overrun preserves all text after engine 100 percent", () => NumericCase(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3I final complete own in-range tokens recover only actual end and preserve start", () => SupportedTokens(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3I final unsupported lexical timings retain local untimed text without guessing", () => UnsupportedTokens(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3I missing corrupt and mismatched JSON retain final text without partial recovery", () => IncompleteEvidence(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3J reversed negative and absolute range errors remain fatal", () => InvalidSourceRanges(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3I zero middle and unsupported final cues both survive with timed neighbors intact", () => ZeroAndFinal(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3I EOF fallback reload search TXT and immutable rerun preserve complete original words", () => Persistence(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3J every out-of-range positive cue survives independently of position", () => PerSegmentRanges(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3J multiple overruns recover only their own full token evidence", () => MultipleRecoveries(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3J global JSON mismatch rolls back every overrun recovery", () => MultipleFallback(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3J 10001 out-of-range cues retain all text with bounded diagnostics and reload", () => ManyOverruns(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3J legacy small final tolerance cannot create invalid split subcues", () => LegacyFinalSplit(root));
    }

    private static void NumericCase(string root)
    {
        var progress = new ProgressLog();
        var (request, result) = Run(root, "numeric", progress: progress);
        TranscriptRevision revision = Success(result);
        Need(revision.Segments.Count == 3 && revision.Segments[^1] is
            { StartMs: null, EndMs: null, TimestampQuality: "untimed", Text: TailText }, "914ms末段未完整保留为无时间文字。");
        Need(result.Message.Contains("2470160–2476240") && result.Message.Contains("2475326 ms") &&
            result.Message.Contains("914 ms") && result.Message.Contains("1 个片段时间待校对"), "缺少准确末端数值或可用性提示。");
        Need(revision.Segments[0].StartMs == 100 && revision.Segments[0].EndMs == 400 &&
            revision.Segments[1].StartMs == 500 && revision.Segments[1].EndMs == 900,
            "正常邻段的分句/回听时间被末段故障改变。");
        Need(progress.Values.Any(value => value.Stage == LocalTranscriptionStage.Recognizing && value.Percent == 100) &&
            progress.Values.Any(value => value.Stage == LocalTranscriptionStage.Saving), "未经过合成引擎100%和保存阶段。");
        CheckOriginals(request, result, "numeric");
    }

    private static void SupportedTokens(string root)
    {
        foreach (string variant in new[] { "valid-tokens", "token-eof", "control-tokens" })
        {
            var (request, result) = Run(root, variant); TranscriptRevision revision = Success(result);
            long expectedEnd = variant == "token-eof" ? 4000 : 3900;
            Need(revision.Segments.Count == 4 && revision.Segments.All(cue => cue.StartMs is not null && cue.TimestampQuality == "approximate") &&
                revision.Segments[2].StartMs == 3000 && revision.Segments[2].EndMs == 3400 &&
                revision.Segments[3].StartMs == 3500 && revision.Segments[3].EndMs == expectedEnd,
                variant + " 未保持原始start或未使用本段实际词结束边界。");
            Need(result.Message.Contains($"3000–{expectedEnd}") && result.Message.Contains("approximate"), "恢复依据未报告。");
            foreach (string extension in new[] { ".srt", ".vtt" })
            {
                string path = Path.Combine(root, variant + extension);
                TranscriptSubtitleExporter.Export(request.MeetingDirectory, revision.RevisionId, path);
                var roundTrip = TranscriptService.Import(new(request.MeetingDirectory, request.MeetingId,
                    request.MeetingDurationMs, path, IsSynthetic: true));
                Need(roundTrip.Segments.Select(cue => (cue.StartMs, cue.EndMs, cue.Text)).SequenceEqual(
                    revision.Segments.Select(cue => (cue.StartMs, cue.EndMs, cue.Text))), "有效恢复时间导出往返不一致。");
            }
            CheckOriginals(request, result, variant);
        }
    }

    private static void UnsupportedTokens(string root)
    {
        foreach (string variant in new[] { "cross", "overrun-251", "token-at-eof", "token-backward", "token-overlap",
            "token-before-source", "token-negative", "token-zero", "token-empty", "token-text", "token-missing-word",
            "token-missing-offset", "token-missing-id", "token-negative-id", "token-control-only", "no-tokens" })
        {
            var (request, result) = Run(root, variant); TranscriptRevision revision = Success(result);
            Need(revision.Segments.Count == 3 && revision.Segments[^1] is
                { StartMs: null, EndMs: null, TimestampQuality: "untimed", Text: TailText },
                variant + " 裁剪/猜测了词时间或未保留完整末段。");
            Need(!revision.Warnings.Any(value => value.Contains("恢复为")) && result.Message.Contains("未裁剪词时间"),
                variant + " 把无支持词时间报告为恢复成功。");
            CheckOriginals(request, result, variant);
        }
    }

    private static void IncompleteEvidence(string root)
    {
        foreach (string variant in new[] { "missing-json", "malformed-json", "mismatch-text", "mismatch-offset", "mismatch-count" })
        {
            var (request, result) = Run(root, variant); TranscriptRevision revision = Success(result);
            Need(revision.Segments.Count == 2 && revision.Segments[0].StartMs == 100 && revision.Segments[0].EndMs == 900 &&
                revision.Segments[^1] is { StartMs: null, EndMs: null, Text: TailText },
                variant + " 回退没有保留原始段及全部尾段文字。");
            Need(!revision.Warnings.Any(value => value.Contains("恢复为")), "全量对应失败仍留下部分恢复结果或警告。");
            CheckOriginals(request, result, variant);
        }
    }

    private static void InvalidSourceRanges(string root)
    {
        foreach (string variant in new[] { "reversed", "negative", "absolute-limit" })
        {
            var (request, result) = Run(root, variant);
            Need(result.Status == "Failed" && result.Revision is null &&
                TranscriptService.ListRevisions(request.MeetingDirectory).Count == 0, variant + " 原始非法区间被分句或回退掩盖。");
            Need(File.Exists(Path.Combine(result.AttemptDirectory, "whisper-output.srt")) &&
                File.Exists(Path.Combine(result.AttemptDirectory, "whisper-output.json")) &&
                M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256, "失败丢失原始输出或改变音频。");
        }
    }

    private static void ZeroAndFinal(string root)
    {
        var (request, result) = Run(root, "zero-middle"); TranscriptRevision revision = Success(result);
        Need(revision.Segments.Count == 4 && revision.Segments.Count(cue => cue.StartMs is null) == 2 &&
            revision.Segments[2].Text == "合成零时长正文" && revision.Segments[^1].Text == TailText &&
            result.Message.Contains("2 个片段时间待校对"), "中间零时长与末段越界没有同时完整保留。");
        Need(revision.Warnings.Any(value => value.StartsWith("Whisper 零时长片段", StringComparison.Ordinal)) &&
            revision.Warnings.Any(value => value.StartsWith("Whisper 越界时间", StringComparison.Ordinal)), "两种独立诊断未持久保留。");
        CheckOriginals(request, result, "zero-middle");
    }

    private static void Persistence(string root)
    {
        var (request, result) = Run(root, "persistence", "cross"); TranscriptRevision revision = Success(result);
        string originalSrt = Path.Combine(result.AttemptDirectory, "whisper-output.srt");
        string originalJson = Path.Combine(result.AttemptDirectory, "whisper-output.json");
        string srtHash = M3TranscriptTests.Hash(originalSrt), jsonHash = M3TranscriptTests.Hash(originalJson);
        var loaded = TranscriptService.Load(request.MeetingDirectory, revision.RevisionId);
        Need(loaded.Segments.SequenceEqual(revision.Segments) && loaded.Warnings.SequenceEqual(revision.Warnings), "revision JSON重开丢字或丢时间质量。");
        Need(TranscriptService.Search(request.MeetingDirectory, "合成尾句甲").Any(hit => hit.StartMs is null && hit.Text == TailText),
            "SQLite未保留无时间尾段或搜索虚构回听位置。");
        string txt = Path.Combine(root, "complete-final-text.txt");
        TranscriptSubtitleExporter.ExportText(request.MeetingDirectory, revision.RevisionId, txt);
        Need(File.ReadAllText(txt) == string.Join('\n', revision.Segments.Select(cue => cue.Text)) + "\n", "TXT遗漏或重排正文。");
        foreach (string extension in new[] { ".srt", ".vtt" })
        {
            string destination = Path.Combine(root, "must-not-drop-final" + extension);
            Reject(() => TranscriptSubtitleExporter.Export(request.MeetingDirectory, revision.RevisionId, destination));
            Need(!File.Exists(destination), "字幕导出静默丢弃无时间尾段。");
        }
        Reject(() => TranscriptService.Import(new(request.MeetingDirectory, request.MeetingId,
            request.MeetingDurationMs, originalSrt, "whisper.cpp", IsSynthetic: true)));
        Need(TranscriptService.ListRevisions(request.MeetingDirectory).Count == 1, "普通导入冒充Whisper绕过严格区间校验。");
        var reviewed = TranscriptService.CreateRevision(request.MeetingDirectory, revision.RevisionId,
            revision.Segments.Select(cue => cue.StartMs is null ? cue with { Text = "人工尾段校订" } : cue).ToArray());
        var rerun = WhisperTranscriber.RunAsync(request).GetAwaiter().GetResult(); TranscriptRevision next = Success(rerun);
        Need(next.RevisionId != revision.RevisionId && rerun.AttemptDirectory != result.AttemptDirectory &&
            TranscriptService.Load(request.MeetingDirectory, revision.RevisionId).Segments.SequenceEqual(revision.Segments) &&
            TranscriptService.Load(request.MeetingDirectory, reviewed.RevisionId).Segments[^1].Text == "人工尾段校订" &&
            M3TranscriptTests.Hash(originalSrt) == srtHash && M3TranscriptTests.Hash(originalJson) == jsonHash,
            "重跑覆盖原稿、raw输出或人工校订。");
        CheckOriginals(request, result, "cross");
    }

    private static void PerSegmentRanges(string root)
    {
        foreach (string variant in new[] { "nonfinal", "nonfinal-small", "start-eof", "start-after",
            "last-two", "three-tail", "middle-valid-after", "all-overrun" })
        {
            var (request, result) = Run(root, variant); var revision = Success(result);
            Cue[] unsupported = Cues(variant).Where(cue => cue.End > request.MeetingDurationMs).ToArray();
            Need(revision.Segments.Count(cue => cue.StartMs is null) == unsupported.Length &&
                revision.Segments.Where(cue => cue.StartMs is null).Select(cue => cue.Text).SequenceEqual(unsupported.Select(cue => cue.Text)) &&
                revision.Segments.Where(cue => cue.StartMs is not null).All(cue => cue.EndMs <= request.MeetingDurationMs),
                variant + " 没有逐段保全文，或仍保留越界回听时间。");
            foreach (Cue valid in Cues(variant).Where(cue => cue.End <= request.MeetingDurationMs && cue.Tokens.Length == 1))
                Need(revision.Segments.Any(cue => cue.Text == valid.Text && cue.StartMs == valid.Start && cue.EndMs == valid.End),
                    "处理越界中段改变了后续合法句子的时码。");
            if (variant is "start-eof" or "start-after" or "all-overrun")
                Need(result.Message.Contains("没有可回听区间"), "完全在EOF外的文字未明确标记不能回听。");
            Need(TranscriptService.Load(request.MeetingDirectory, revision.RevisionId).Segments.SequenceEqual(revision.Segments),
                "多段越界结果无法从持久JSON完整重开。");
            CheckOriginals(request, result, variant);
        }
    }

    private static void MultipleRecoveries(string root)
    {
        foreach (string variant in new[] { "multiple-tokens", "middle-small-tokens" })
        {
            var (request, result) = Run(root, variant); var revision = Success(result);
            Need(revision.Segments.All(cue => cue.StartMs is not null && cue.EndMs <= 4000 && cue.TimestampQuality == "approximate"),
                variant + " 完整本段词证据未恢复为音频内时码。");
            if (variant == "multiple-tokens")
                Need(revision.Segments.Any(cue => cue.Text == "合成第一越界句。" && cue.StartMs == 1000 && cue.EndMs == 2200) &&
                    revision.Segments.Any(cue => cue.Text == "合成第二越界句。" && cue.StartMs == 2600 && cue.EndMs == 3900) &&
                    revision.Warnings.Count(value => value.Contains("恢复为")) == 2,
                    "两段越界借用了邻段时间或整体平移，未分别恢复。");
            else
                Need(revision.Segments.Any(cue => cue.Text == "合成尾句乙。" && cue.EndMs == 3800) &&
                    revision.Segments[^1].StartMs == 3900 && revision.Segments[^1].EndMs == 4000,
                    "非末段小幅越界被套用旧末段裁尾，或改变正常后句。");
            CheckOriginals(request, result, variant);
        }
    }

    private static void MultipleFallback(string root)
    {
        foreach (string variant in new[] { "multiple-mismatch", "multiple-missing-json" })
        {
            var (request, result) = Run(root, variant); var revision = Success(result);
            Need(revision.Segments.Count == 3 && revision.Segments[0].StartMs == 100 && revision.Segments[0].EndMs == 900 &&
                revision.Segments.Skip(1).All(cue => cue.StartMs is null) &&
                !revision.Warnings.Any(value => value.Contains("恢复为")),
                "完整JSON核对失败仍留下前段恢复时码/诊断，或只保留一个异常段。");
            CheckOriginals(request, result, variant);
        }
    }

    private static void ManyOverruns(string root)
    {
        var (request, result) = Run(root, "many-overrun"); var revision = Success(result);
        Need(revision.Segments.Count == 10001 && revision.Segments.All(cue => cue.StartMs is null) &&
            revision.Warnings.Count < 110 && revision.Warnings.Count(value => value.StartsWith("Whisper 越界时间 segment-", StringComparison.Ordinal)) == 100 &&
            revision.Warnings.Any(value => value.Contains("10001 个") && value.Contains("前 100 条") && value.Contains("whisper-output.srt")),
            "大量越界提示未有界、总数未报告，或截断了正文。");
        var reopened = TranscriptService.Load(request.MeetingDirectory, revision.RevisionId);
        Need(reopened.Segments.SequenceEqual(revision.Segments) && reopened.Warnings.SequenceEqual(revision.Warnings),
            "10001段越界结果因诊断超限不能完整重开。");
        string export = Path.Combine(root, "all-10001-untimed.txt");
        TranscriptSubtitleExporter.ExportText(request.MeetingDirectory, revision.RevisionId, export);
        Need(File.ReadAllText(export) == string.Join('\n', Cues("many-overrun").Select(cue => cue.Text)) + "\n",
            "大量无时间文字导出不完整或顺序改变。");
        CheckOriginals(request, result, "many-overrun");
    }

    private static void LegacyFinalSplit(string root)
    {
        var (request, result) = Run(root, "legacy-split"); var revision = Success(result);
        Need(revision.Segments.Count == 3 && revision.Segments[^1] is
            { StartMs: 3000, EndMs: 4000, TimestampQuality: "approximate", Text: TailText } &&
            revision.Warnings.Any(value => value.StartsWith("Whisper 尾端时间", StringComparison.Ordinal) && value.Contains("200 ms")),
            "旧<=250ms最后原段被分成多个非法越界子句，或改变既有有限裁尾合同。");
        Need(File.ReadAllText(Path.Combine(result.AttemptDirectory, "whisper-output.srt")) == Outputs("legacy-split").Srt &&
            M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256,
            "旧政策分句保护改变原始SRT或音频。");
    }

    private static (WhisperRequest Request, WhisperRunResult Result) Run(string root, string id,
        string? variant = null, IProgress<LocalTranscriptionProgress>? progress = null)
    {
        variant ??= id; long duration = variant == "numeric" ? 2475326 : 4000;
        string meeting = M3TranscriptTests.Meeting(root, id), wave = Path.Combine(meeting, "synthetic-input.wav");
        int bytes = checked((int)(duration * 32));
        using (var file = File.Create(wave))
        using (var writer = new BinaryWriter(file))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
            writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(bytes);
            writer.Write((short)1); writer.Flush(); file.SetLength(44L + bytes);
        }
        string model = M3TranscriptTests.Input(meeting, "synthetic-model.bin", Prefix + variant);
        var request = new WhisperRequest(meeting, id, duration, wave, M3TranscriptTests.Hash(wave),
            new(Path.Combine(AppContext.BaseDirectory, "ProjectEcho.Tests.exe"), model,
                ModelName: "synthetic EOF fixture; not actual ASR", ModelLicenseAcknowledged: true), true);
        return (request, WhisperTranscriber.RunAsync(request, progress: progress).GetAwaiter().GetResult());
    }

    internal static int WriteFixture(string output, string variant)
    {
        var (srt, json) = Outputs(variant);
        Console.Error.WriteLine("whisper_print_progress_callback: progress = 100%");
        File.WriteAllText(output + ".srt", srt, new UTF8Encoding(false));
        File.WriteAllText(output + ".txt", string.Join('\n', Cues(variant).Select(cue => cue.Text)), new UTF8Encoding(false));
        if (json is not null) File.WriteAllText(output + ".json", json, new UTF8Encoding(false));
        return 0;
    }

    private static (string Srt, string? Json) Outputs(string variant)
    {
        List<Cue> cues = Cues(variant);
        string srt = string.Concat(cues.Select((cue, index) => $"{index + 1}\n{Time(cue.Start)} --> {Time(cue.End)}\n{cue.Text}\n\n"));
        if (variant is "missing-json" or "multiple-missing-json") return (srt, null);
        if (variant == "malformed-json") return (srt, "{invalid JSON");
        var entries = cues.Select((cue, index) => new
        {
            text = (variant is "mismatch-text" or "multiple-mismatch") && index == cues.Count - 1 ? "合成不匹配文字" : cue.Text,
            offsets = new { from = cue.Start, to = variant == "mismatch-offset" && index == cues.Count - 1 ? cue.End + 1 : cue.End },
            tokens = cue.Tokens.Select((value, tokenIndex) =>
            {
                var token = new Dictionary<string, object> { ["id"] = value.Id, ["text"] = value.Text,
                    ["offsets"] = new { from = value.From, to = value.To } };
                if (index == cues.Count - 1 && tokenIndex == 1 && variant == "token-missing-offset") token.Remove("offsets");
                if (index == cues.Count - 1 && tokenIndex == 1 && variant == "token-missing-id") token.Remove("id");
                if (value.Id >= 50257) token.Remove("offsets");
                return token;
            }).ToArray()
        }).ToArray();
        return (srt, JsonSerializer.Serialize(new { model = new { multilingual = true },
            transcription = variant == "mismatch-count" ? entries.Skip(1) : entries }));
    }

    private static List<Cue> Cues(string variant)
    {
        static Cue Whole(long start, long end, string text) => new(start, end, text, [new(100, text, start, end)]);
        Cue Normal() => new(100, 900, "合成前段甲。合成前段乙。",
            [new(100, "合成前段甲。", 100, 400), new(101, "合成前段乙。", 500, 900)]);
        if (variant == "many-overrun") return Enumerable.Range(0, 10001)
            .Select(index => Whole(4000 + index, 5000 + index, $"合成无时间完整正文{index + 1}。")).ToList();
        if (variant == "all-overrun") return
            [Whole(4000, 4500, "合成全越界首句。"), Whole(4500, 5100, "合成全越界中句。"), Whole(5100, 5900, "合成全越界末句。")];
        if (variant == "last-two") return
            [Normal(), Whole(2900, 4600, "合成越界倒数第二句。"), Whole(3500, 4914, "合成越界末句。")];
        if (variant == "three-tail") return
            [Normal(), Whole(2900, 4400, "合成连续尾句一。"), Whole(3500, 4914, "合成连续尾句二。"), Whole(4200, 5100, "合成连续尾句三。")];
        if (variant == "middle-valid-after") return
            [Normal(), Whole(1500, 4914, "合成越界中间句。"), Whole(2200, 2900, "合成合法后句。")];
        if (variant is "multiple-tokens" or "multiple-mismatch" or "multiple-missing-json") return
            [Normal(), new(1000, 4400, "合成第一越界句。", [new(100, "合成第一越界句。", 1100, 2200)]),
             new(2600, 4914, "合成第二越界句。", [new(100, "合成第二越界句。", 2700, 3900)])];
        long start = variant == "numeric" ? 2470160 : 3000;
        long end = variant switch { "numeric" => 2476240, "overrun-251" => 4251, "absolute-limit" => 604800001,
            "legacy-split" => 4200, "middle-small-tokens" => 4100, _ => 4914 };
        long middle = variant == "numeric" ? 2473000 : 3400;
        Token[] tokens = [new(100, "合成尾句甲。", start, middle), new(101, "合成尾句乙。", middle, end)];
        if (variant is "valid-tokens" or "token-eof" or "control-tokens" or "missing-json" or "malformed-json" ||
            variant.StartsWith("mismatch-", StringComparison.Ordinal))
            tokens = [new(100, "合成尾句甲。", 3100, 3400), new(101, "合成尾句乙。", 3500, variant == "token-eof" ? 4000 : 3900)];
        tokens = variant switch
        {
            "token-at-eof" => [tokens[0], new(101, "合成尾句乙。", 4000, 4200)],
            "token-backward" => [tokens[0], new(101, "合成尾句乙。", 3700, 3500)],
            "token-overlap" => [tokens[0], new(101, "合成尾句乙。", 3300, 3900)],
            "token-before-source" => [new(100, "合成尾句甲。", 2900, 3400), new(101, "合成尾句乙。", 3500, 3900)],
            "token-negative" => [new(100, "合成尾句甲。", -1, 3400), new(101, "合成尾句乙。", 3500, 3900)],
            "token-zero" => [tokens[0], new(101, "合成尾句乙。", 3500, 3500)],
            "token-empty" => [new(100, "", 3000, 3100), new(101, TailText, 3100, 3900)],
            "token-text" => [tokens[0], new(101, "合成不同末词。", 3500, 3900)],
            "token-missing-word" => [tokens[0]],
            "token-negative-id" => [tokens[0], new(-1, "合成尾句乙。", 3500, 3900)],
            "token-control-only" => [new(50364, TailText, 3000, 3900)],
            "no-tokens" => [],
            "control-tokens" => [new(50364, "[_BEG_]", 0, 0), .. tokens, new(50257, "[_END_]", 0, 0)],
            "middle-small-tokens" => [new(100, "合成尾句甲。", 3000, 3400), new(101, "合成尾句乙。", 3500, 3800)],
            "legacy-split" => [new(100, "合成尾句甲。", 3000, 4100), new(101, "合成尾句乙。", 4100, 4200)],
            _ => tokens
        };
        if (variant == "reversed") { start = 3500; end = 3000; }
        if (variant == "negative") start = -1;
        if (variant == "start-eof") start = 4000;
        if (variant == "start-after") start = 4001;
        var cues = new List<Cue> { new(100, 900, "合成前段甲。合成前段乙。",
            [new(100, "合成前段甲。", 100, 400), new(101, "合成前段乙。", 500, 900)]) };
        if (variant == "zero-middle") cues.Add(new(1000, 1000, "合成零时长正文", [new(100, "合成零时长正文", 1000, 1000)]));
        cues.Add(new(start, variant == "nonfinal-small" ? 4100 : end, TailText, tokens));
        if (variant == "middle-small-tokens")
            cues.Add(Whole(3900, 4000, "合成合法后段。"));
        if (variant is "nonfinal" or "nonfinal-small")
            cues.Add(new(3500, 3900, "合成合法后段。", [new(100, "合成合法后段。", 3500, 3900)]));
        return cues;
    }

    private static void CheckOriginals(WhisperRequest request, WhisperRunResult result, string variant)
    {
        TranscriptRevision revision = result.Revision!; var (srt, json) = Outputs(variant);
        string rawSrt = Path.Combine(result.AttemptDirectory, "whisper-output.srt");
        string rawJson = Path.Combine(result.AttemptDirectory, "whisper-output.json");
        Need(string.Concat(revision.Segments.Select(cue => cue.Text)) == string.Concat(Cues(variant).Select(cue => cue.Text)),
            "末段处理丢弃或重排了原始正文。");
        Need(File.ReadAllText(rawSrt) == srt && M3TranscriptTests.Hash(rawSrt) == revision.OriginalSha256 &&
            M3TranscriptTests.Hash(Path.Combine(request.MeetingDirectory, revision.OriginalFile)) == revision.OriginalSha256 &&
            M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256, "原始SRT/副本/输入音频改变。");
        Need(json is null ? !File.Exists(rawJson) : File.ReadAllText(rawJson) == json, "原始JSON改变或被伪造。");
        using var attempt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(result.AttemptDirectory, "attempt.json")));
        Need(attempt.RootElement.GetProperty("finalCueTimestampPolicy").GetString() == Policy &&
            attempt.RootElement.GetProperty("outOfRangeTimestampPolicy").GetString() == Policy &&
            attempt.RootElement.GetProperty("timestampBoundaryPolicy").GetString() == "whisper-final-cue-overrun-at-most-250ms-v1" &&
            attempt.RootElement.GetProperty("transcriptWarnings").EnumerateArray().Any(value => value.GetString()!.StartsWith("Whisper 越界时间", StringComparison.Ordinal)),
            "attempt未保留新旧边界政策和数值诊断。");
    }

    private static TranscriptRevision Success(WhisperRunResult result)
    { Need(result.Status == "Succeeded" && result.Revision is not null, result.Message); return result.Revision!; }
    private static string Time(long value) => value < 0 ? "-" + Time(-value) :
        $"{value / 3600000:D2}:{value / 60000 % 60:D2}:{value / 1000 % 60:D2},{value % 1000:D3}";
    private static void Reject(Action action)
    { try { action(); throw new InvalidOperationException("应拒绝的操作成功。"); } catch (InvalidDataException) { } }
    private static void Need(bool value, string message) => M3TranscriptTests.Need(value, message);
}
