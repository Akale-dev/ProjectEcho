using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectEcho.Core.Transcription;

internal static class M3iWhisperTimestampTests
{
    private const string Prefix = "synthetic-m3e-timestamp-m3i-";
    private const long ShortDurationMs = 4000, LongDurationMs = 3439371;
    private const string UntimedText = "零时长独立文字";
    private sealed record Token(int Id, string Text, long From, long To);
    private sealed record Cue(long Start, long End, string Text, Token[] Tokens);
    private sealed class ProgressLog : IProgress<LocalTranscriptionProgress>
    {
        internal readonly ConcurrentQueue<LocalTranscriptionProgress> Values = new();
        public void Report(LocalTranscriptionProgress value) => Values.Enqueue(value);
    }

    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3i-whisper-timestamps"); Directory.CreateDirectory(root);
        run("T35/T38-WINDOWS SYNTHETIC CLI M3I cue 906 at 2864760ms preserves all text after engine 100 percent", () => LongRecording(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3I exact own tokens recover positive approximate times and subtitle round trip", () => OwnTokens(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3I unusable tokens retain untimed text without joining and split valid neighboring cues", () => UnusableTokens(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3I absent malformed and globally mismatched token JSON cannot recover times", () => IncompleteEvidence(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3I zero timestamps at start middle and final in-range sample remain text", () => MultipleZeros(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3J negative reversed EOF-zero fail while positive nonfinal overrun is untimed", () => InvalidRanges(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3I manual import remains strict TXT complete and timed subtitle export blocked", () => ExportsAndManualImport(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3I rerun preserves original reviewed revision input and raw output hashes", () => Immutable(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3I 10001 zero cues bound diagnostics and remain fully reloadable", () => ManyZeros(root));
    }

    private static void LongRecording(string root)
    {
        var progress = new ProgressLog();
        var (request, result) = Run(root, "long", progress: progress);
        var revision = Success(result);
        Need(revision.Segments.Count == 908, "906段附近的无时间文字或有效后段分句丢失。");
        var zero = revision.Segments[905];
        Need(zero.Text == UntimedText && zero.StartMs is null && zero.EndMs is null &&
            zero.TimestampQuality == "untimed", "现场相同零时长未保留为无时间文字。");
        Need(revision.Segments[904].EndMs == 2864760 && revision.Segments[906].StartMs == 2864760 &&
            revision.Segments[906].EndMs == 2865760 && revision.Segments[907].EndMs == 2866760,
            "零时长处理猜测、借用或破坏了相邻有效时间。");
        string expected = string.Concat(Cues("long").Select(cue => cue.Text));
        Need(string.Concat(revision.Segments.Select(cue => cue.Text)) == expected, "906段之前或之后的文字/顺序改变。");
        Need(revision.Warnings.Any(value => value.Contains("segment-000906") && value.Contains("2864760–2864760") &&
            value.Contains("无时间")) && result.Message.Contains("1 个片段时间待校对"), "没有持久报告确切零时长和可用性。");
        var updates = progress.Values.ToArray();
        Need(updates.Any(value => value.Stage == LocalTranscriptionStage.Recognizing && value.Percent == 100) &&
            updates.Any(value => value.Stage == LocalTranscriptionStage.Saving), "未真实经过合成引擎100%和保存阶段。");
        CheckOriginals(request, result, "long");
    }

    private static void OwnTokens(string root)
    {
        var (request, result) = Run(root, "tokens");
        var revision = Success(result);
        Need(revision.Segments.Count == 5 && revision.Segments.All(cue => cue.StartMs is not null &&
            cue.EndMs > cue.StartMs && cue.TimestampQuality == "approximate"), "完整本段token未恢复有效时间/分句。");
        Need(revision.Segments[1].StartMs == 900 && revision.Segments[1].EndMs == 1000 &&
            revision.Segments[2].StartMs == 1000 && revision.Segments[2].EndMs == 1200,
            "恢复时间不是原始本段token的实际包络/边界。");
        Need(result.Message.Contains("900–1200") && result.Message.Contains("approximate"), "token恢复没有可见说明。");
        foreach (string extension in new[] { ".srt", ".vtt" })
        {
            string file = Path.Combine(root, "own-token-times" + extension);
            TranscriptSubtitleExporter.Export(request.MeetingDirectory, revision.RevisionId, file);
            var reimported = TranscriptService.Import(new(request.MeetingDirectory, request.MeetingId,
                request.MeetingDurationMs, file, IsSynthetic: true));
            Need(reimported.Segments.Select(cue => (cue.StartMs, cue.EndMs, cue.Text)).SequenceEqual(
                revision.Segments.Select(cue => (cue.StartMs, cue.EndMs, cue.Text))), "恢复字幕导出往返不一致。");
        }
        CheckOriginals(request, result, "tokens");
    }

    private static void UnusableTokens(string root)
    {
        foreach (string variant in new[] { "zero", "token-backward", "token-outside", "token-text", "token-anchor",
            "token-missing", "token-negative", "token-overlap", "token-empty", "token-partial-zero" })
        {
            var (request, result) = Run(root, variant);
            var revision = Success(result);
            Need(revision.Segments.Count == 4 && revision.Segments[1].StartMs is null &&
                revision.Segments[1].Text == Cues(variant)[1].Text &&
                revision.Segments[0].StartMs == 100 && revision.Segments[0].EndMs == 900 &&
                revision.Segments[2].StartMs == 1000 && revision.Segments[2].EndMs == 1600 &&
                revision.Segments[3].StartMs == 1600 && revision.Segments[3].EndMs == 2000,
                variant + " 猜测零时长或跨无时间片段合并，或禁用了有效邻段分句。");
            Need(string.Concat(revision.Segments.Select(cue => cue.Text)) == string.Concat(Cues(variant).Select(cue => cue.Text)),
                variant + " 删除或重排了文字。");
            CheckOriginals(request, result, variant);
        }
    }

    private static void IncompleteEvidence(string root)
    {
        foreach (string variant in new[] { "missing-json", "malformed-json", "mismatch-json", "mismatch-offset", "mismatch-count" })
        {
            var (request, result) = Run(root, variant);
            var revision = Success(result);
            Need(revision.Segments.Count == 3 && revision.Segments[1].StartMs is null &&
                revision.Segments[1].Text == Cues(variant)[1].Text &&
                revision.Segments[0].StartMs == 100 && revision.Segments[2].EndMs == 2000,
                variant + " 在证据不完整时恢复了时间或改变有效原段。");
            Need(!revision.Warnings.Any(value => value.Contains("恢复为")), "全局核对失败仍留下已恢复时间的警告。");
            CheckOriginals(request, result, variant);
        }
    }

    private static void MultipleZeros(string root)
    {
        var (request, result) = Run(root, "multiple");
        var revision = Success(result);
        Need(revision.Segments.Count == 5 && revision.Segments.Count(cue => cue.StartMs is null) == 3 &&
            revision.Segments[0].Text == "零点文字" && revision.Segments[^1].Text == "末尾文字" &&
            result.Message.Contains("3 个片段时间待校对"), "首/中/末多处零时长没有全部保留。");
        CheckOriginals(request, result, "multiple");
    }

    private static void InvalidRanges(string root)
    {
        foreach (string variant in new[] { "backward", "eof", "after", "negative" })
        {
            var (request, result) = Run(root, variant);
            Need(result.Status == "Failed" && result.Revision is null &&
                TranscriptService.ListRevisions(request.MeetingDirectory).Count == 0,
                variant + " 原有严格异常被降级掩盖为成功。");
            Need(File.Exists(Path.Combine(result.AttemptDirectory, "whisper-output.srt")) &&
                M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256, "失败丢失原输出或改变音频。");
        }
        var (overrunRequest, overrunResult) = Run(root, "overrun");
        var retained = Success(overrunResult);
        Need(retained.Segments.Count == 4 && retained.Segments[1] is { StartMs: null, EndMs: null } &&
            retained.Segments[1].Text == UntimedText && retained.Segments[^1].EndMs == 2000 &&
            M3TranscriptTests.Hash(overrunRequest.WavePath) == overrunRequest.ExpectedWaveSha256,
            "v2非末段正向越界没有局部保全文，或破坏正常后续字幕。");
    }

    private static void ExportsAndManualImport(string root)
    {
        var (request, result) = Run(root, "export", "zero");
        var revision = Success(result);
        string exported = Path.Combine(root, "all-text.txt");
        TranscriptSubtitleExporter.ExportText(request.MeetingDirectory, revision.RevisionId, exported);
        Need(File.ReadAllText(exported) == string.Join('\n', revision.Segments.Select(cue => cue.Text)) + "\n",
            "TXT未按完整原顺序导出无时间文字。");
        var hits = TranscriptService.Search(request.MeetingDirectory, "零时长独立文字");
        Need(hits.Any(hit => hit.StartMs is null && hit.Text.Contains(UntimedText)), "无时间文字不可搜索或搜索伪造了时间。");
        foreach (string extension in new[] { ".srt", ".vtt" })
        {
            string path = Path.Combine(root, "must-not-omit-text" + extension);
            Reject(() => TranscriptSubtitleExporter.Export(request.MeetingDirectory, revision.RevisionId, path));
            Need(!File.Exists(path), "字幕导出偷偷丢弃无时间文字。");
        }
        string original = Path.Combine(result.AttemptDirectory, "whisper-output.srt");
        Reject(() => TranscriptService.Import(new(request.MeetingDirectory, request.MeetingId,
            request.MeetingDurationMs, original, "whisper.cpp", IsSynthetic: true)));
        Need(TranscriptService.ListRevisions(request.MeetingDirectory).Count == 1,
            "手动导入靠来源名称绕过了零时长校验。");
    }

    private static void Immutable(string root)
    {
        var (request, first) = Run(root, "immutable", "zero");
        var revision = Success(first);
        string rawSrt = Path.Combine(first.AttemptDirectory, "whisper-output.srt");
        string rawJson = Path.Combine(first.AttemptDirectory, "whisper-output.json");
        string srtHash = M3TranscriptTests.Hash(rawSrt), jsonHash = M3TranscriptTests.Hash(rawJson);
        var reviewed = TranscriptService.CreateRevision(request.MeetingDirectory, revision.RevisionId,
            revision.Segments.Select(cue => cue.StartMs is null ? cue with { Text = "人工修订保留" } : cue).ToArray());
        var second = WhisperTranscriber.RunAsync(request).GetAwaiter().GetResult();
        var next = Success(second);
        Need(first.AttemptDirectory != second.AttemptDirectory && revision.RevisionId != next.RevisionId &&
            TranscriptService.Load(request.MeetingDirectory, revision.RevisionId).Segments.SequenceEqual(revision.Segments) &&
            TranscriptService.Load(request.MeetingDirectory, reviewed.RevisionId).Segments.Any(cue => cue.Text == "人工修订保留"),
            "重跑覆盖了原稿或人工修订。");
        Need(M3TranscriptTests.Hash(rawSrt) == srtHash && M3TranscriptTests.Hash(rawJson) == jsonHash &&
            M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256, "重跑改写原输出或音频。");
    }

    private static void ManyZeros(string root)
    {
        var (request, result) = Run(root, "many");
        var revision = Success(result);
        Need(revision.Segments.Count == 10001 && revision.Segments.All(cue => cue.StartMs is null) &&
            revision.Warnings.Count < 110 && revision.Warnings.Any(value => value.Contains("10001 个") &&
            value.Contains("前 100 条") && value.Contains("whisper-output.srt")), "诊断超限或截断了正文。");
        var loaded = TranscriptService.Load(request.MeetingDirectory, revision.RevisionId);
        Need(loaded.Segments.SequenceEqual(revision.Segments) &&
            string.Concat(loaded.Segments.Select(cue => cue.Text)) == string.Concat(Cues("many").Select(cue => cue.Text)),
            "大量零时长保存后不能重开或文字丢失。");
        CheckOriginals(request, result, "many");
    }

    private static TranscriptRevision Success(WhisperRunResult result)
    {
        Need(result.Status == "Succeeded" && result.Revision is not null, result.Message);
        return result.Revision!;
    }

    private static void CheckOriginals(WhisperRequest request, WhisperRunResult result, string variant)
    {
        var revision = result.Revision!;
        var (srt, json) = Outputs(variant);
        string rawSrt = Path.Combine(result.AttemptDirectory, "whisper-output.srt");
        Need(File.ReadAllText(rawSrt) == srt && M3TranscriptTests.Hash(rawSrt) == revision.OriginalSha256 &&
            M3TranscriptTests.Hash(Path.Combine(request.MeetingDirectory, revision.OriginalFile)) == revision.OriginalSha256,
            "原始SRT文字或原始模型时间被改写。");
        string rawJson = Path.Combine(result.AttemptDirectory, "whisper-output.json");
        if (json is null) Need(!File.Exists(rawJson), "缺失JSON被伪造。");
        else Need(M3TranscriptTests.Hash(rawJson) == Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
            "原始JSON被改写。");
        Need(M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256, "输入音频改变。");
        using var attempt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(result.AttemptDirectory, "attempt.json")));
        Need(attempt.RootElement.GetProperty("zeroDurationPolicy").GetString() ==
            "whisper-in-range-zero-duration-own-tokens-or-untimed-v1" &&
            attempt.RootElement.GetProperty("transcriptWarnings").GetArrayLength() > 0,
            "attempt缺少零时长规则或持久警告。");
    }

    private static (WhisperRequest Request, WhisperRunResult Result) Run(string root, string id,
        string? variant = null, IProgress<LocalTranscriptionProgress>? progress = null)
    {
        variant ??= id; long duration = variant == "long" ? LongDurationMs : ShortDurationMs;
        string meeting = M3TranscriptTests.Meeting(root, id), wave = Path.Combine(meeting, "synthetic-input.wav");
        int bytes = checked((int)(duration * 16 * 2));
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
                ModelName: "synthetic M3I fixture; not actual ASR", ModelLicenseAcknowledged: true), true);
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
        var cues = Cues(variant);
        string srt = string.Concat(cues.Select((cue, index) =>
            $"{index + 1}\n{Time(cue.Start)} --> {Time(cue.End)}\n{cue.Text}\n\n"));
        if (variant == "missing-json") return (srt, null);
        if (variant == "malformed-json") return (srt, "{not valid JSON");
        var entries = cues.Select((cue, index) => new
        {
            text = variant == "mismatch-json" && index == cues.Count - 1 ? "不匹配的JSON原文" : cue.Text,
            offsets = new { from = cue.Start, to = variant == "mismatch-offset" && index == cues.Count - 1 ? cue.End + 1 : cue.End },
            tokens = cue.Tokens.Select(value => new { id = value.Id, text = value.Text, offsets = new { from = value.From, to = value.To } })
        }).ToArray();
        return (srt, JsonSerializer.Serialize(new
        {
            model = new { multilingual = true },
            transcription = variant == "mismatch-count" ? entries.Skip(1) : entries
        }));
    }

    private static List<Cue> Cues(string variant)
    {
        static Cue Whole(long start, long end, string text) => new(start, end, text, [new(100, text, start, end)]);
        if (variant == "many") return Enumerable.Range(0, 10001)
            .Select(index => Whole(1000, 1000, $"无时间完整正文{index + 1}")).ToList();
        if (variant == "long")
        {
            var longCues = Enumerable.Range(0, 904).Select(index => Whole(index * 3000, index * 3000 + 2000, $"合成正常{index + 1}。")).ToList();
            longCues.Add(Whole(2864000, 2864760, "零时长前句未完"));
            longCues.Add(Whole(2864760, 2864760, UntimedText));
            longCues.Add(new(2864760, 2866760, "后一段第一句。后一段第二句。",
                [new(100, "后一段第一句。", 2864760, 2865760), new(101, "后一段第二句。", 2865760, 2866760)]));
            return longCues;
        }
        if (variant == "multiple") return
            [Whole(0, 0, "零点文字"), Whole(100, 900, "第一段。"), Whole(1000, 1000, UntimedText),
             Whole(1500, 2000, "第二段。"), Whole(3999, 3999, "末尾文字")];
        bool complete = variant == "tokens" || variant.StartsWith("mismatch-", StringComparison.Ordinal);
        string middleText = complete ? "中句一。中句二。" : UntimedText;
        Token[] middleTokens = complete
            ? [new(100, "中句一。", 900, 1000), new(101, "中句二。", 1000, 1200)]
            : [new(100, middleText, 1000, 1000)];
        middleTokens = variant switch
        {
            "token-backward" => [new(100, middleText, 1200, 900)],
            "token-outside" => [new(100, middleText, 900, 5000)],
            "token-text" => [new(100, "不匹配词", 900, 1100)],
            "token-anchor" => [new(100, middleText, 2000, 2200)],
            "token-missing" => [],
            "token-negative" => [new(100, middleText, -10, 1100)],
            "token-empty" => [new(100, "", 900, 950), new(101, middleText, 950, 1100)],
            "token-overlap" => [new(100, "零时长", 900, 1100), new(101, "独立文字", 1000, 1200)],
            "token-partial-zero" => [new(100, "零时长", 900, 1000), new(101, "独立文字", 1000, 1000)],
            _ => middleTokens
        };
        long middleStart = variant switch { "backward" => 1200, "eof" => 4000, "after" => 4001, "negative" => -1, _ => 1000 };
        long middleEnd = variant switch { "eof" => 4000, "after" => 4001, "overrun" => 5000, _ => 1000 };
        return [Whole(100, complete ? 400 : 900, complete ? "前句。" : "前句未完"),
            new(middleStart, middleEnd, middleText, middleTokens),
            new(1000, 2000, "后一段第一句。后一段第二句。",
                [new(100, "后一段第一句。", 1000, 1600), new(101, "后一段第二句。", 1600, 2000)])];
    }

    private static string Time(long value)
    {
        if (value < 0) return "-" + Time(-value);
        return $"{value / 3600000:D2}:{value / 60000 % 60:D2}:{value / 1000 % 60:D2},{value % 1000:D3}";
    }
    private static void Reject(Action action)
    {
        try { action(); throw new InvalidOperationException("应拒绝的操作成功。"); }
        catch (InvalidDataException) { }
    }
    private static void Need(bool value, string message) => M3TranscriptTests.Need(value, message);
}
