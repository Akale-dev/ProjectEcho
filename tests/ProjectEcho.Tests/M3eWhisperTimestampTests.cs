using System.Text;
using System.Text.Json;
using ProjectEcho.Core.Transcription;

internal static class M3eWhisperTimestampTests
{
    private const long DurationMs = 39715;
    private const string Prefix = "synthetic-m3e-timestamp-";

    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3e-whisper-timestamps"); Directory.CreateDirectory(root);
        run("T35/T38-WINDOWS SYNTHETIC CLI M3E 39840ms final timestamp on 39715ms audio preserves raw text and output", () => SmallOverrun(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3E final timestamp tolerance has an inclusive 250ms limit", () => Limit(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3E invalid and unsupported timestamp intervals remain failed with numeric evidence", () => InvalidIntervals(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3E timestamp repair keeps original revisions and subtitle round trip", () => Immutable(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3E normal Whisper output and manual import keep strict independent rules", () => ManualAndValid(root));
    }

    private static void SmallOverrun(string root)
    {
        var (request, result) = Run(root, "small");
        Need(result.Status == "Succeeded" && result.Revision is not null, result.Message);
        var revision = result.Revision!;
        Need(revision.Segments.Count == 2 && revision.Segments[0].EndMs == 35000 && revision.Segments[1].StartMs == 35000 &&
            revision.Segments[1].EndMs == DurationMs && revision.Segments.All(value => value.TimestampQuality == "approximate"),
            "没有只修正最后结束边界，或破坏 token 分句/时间质量。");
        Need(string.Concat(revision.Segments.Select(value => value.Text)) == "合成第一句。合成尾句。", "边界处理删除或改写文字。");
        Need(revision.Warnings.Any(value => value.Contains("39840 ms → 39715 ms") && value.Contains("125 ms")) &&
            result.Message.Contains("125 ms"), "修正幅度没有出现在不可覆盖版本及 UI 结果。");
        string raw = Path.Combine(result.AttemptDirectory, "whisper-output.srt");
        Need(File.ReadAllText(raw).Contains("00:00:39,840") && M3TranscriptTests.Hash(raw) == revision.OriginalSha256 &&
            M3TranscriptTests.Hash(Path.Combine(request.MeetingDirectory, revision.OriginalFile)) == revision.OriginalSha256,
            "原始模型时间被覆盖，或原稿哈希不匹配。");
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(result.AttemptDirectory, "whisper-output.json")));
        Need(json.RootElement.GetProperty("transcription")[0].GetProperty("offsets").GetProperty("to").GetInt64() == 39840,
            "JSON 原始时间被改写。");
        using var attempt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(result.AttemptDirectory, "attempt.json")));
        Need(attempt.RootElement.GetProperty("timestampBoundaryPolicy").GetString() == "whisper-final-cue-overrun-at-most-250ms-v1" &&
            attempt.RootElement.GetProperty("meetingDurationMs").GetInt64() == DurationMs &&
            attempt.RootElement.GetProperty("transcriptWarnings").EnumerateArray().Any(value => value.GetString()!.Contains("125 ms")),
            "尝试日志缺少边界规则、实际时长或修正记录。");
        Need(M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256, "边界处理修改了输入音频。");
    }

    private static void Limit(string root)
    {
        var (_, limit) = Run(root, "limit");
        Need(limit.Status == "Succeeded" && limit.Revision!.Segments[^1].EndMs == DurationMs && limit.Message.Contains("250 ms"),
            "250 ms 边界未按合同修正。");
        var (request, rejected) = Run(root, "large");
        Need(rejected.Status == "Failed" && rejected.Revision is null && rejected.Message.Contains("251 ms") &&
            rejected.Message.Contains("39966") && rejected.Message.Contains("39715") &&
            TranscriptService.ListRevisions(request.MeetingDirectory).Count == 0,
            "超过有限误差仍成功，或错误缺少具体边界。");
        Need(File.ReadAllText(Path.Combine(rejected.AttemptDirectory, "whisper-output.srt")).Contains("00:00:39,966"),
            "失败没有保留原始越界输出。");
    }

    private static void InvalidIntervals(string root)
    {
        foreach (string variant in new[] { "backward", "after", "nonfinal" })
        {
            var (request, result) = Run(root, variant);
            Need(result.Status == "Failed" && result.Revision is null && result.Message.Contains("segment-") &&
                result.Message.Contains("ms") && TranscriptService.ListRevisions(request.MeetingDirectory).Count == 0,
                variant + " 的非法时间被隐藏、文字被删除或错误未给出具体段。");
            Need(File.Exists(Path.Combine(result.AttemptDirectory, "whisper-output.srt")) &&
                File.Exists(Path.Combine(result.AttemptDirectory, "whisper-output.json")) &&
                M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256,
                "错误流程修改了音频或丢弃原始引擎输出。");
        }
    }

    private static void Immutable(string root)
    {
        var (request, first) = Run(root, "immutable", "small");
        var original = first.Revision!;
        var revised = TranscriptService.CreateRevision(request.MeetingDirectory, original.RevisionId,
            original.Segments.Select((value, index) => index == 0 ? value with { Text = "人工修订第一句。" } : value).ToArray());
        var second = WhisperTranscriber.RunAsync(request).GetAwaiter().GetResult();
        Need(second.Status == "Succeeded" && second.Revision!.RevisionId != original.RevisionId &&
            first.AttemptDirectory != second.AttemptDirectory &&
            TranscriptService.Load(request.MeetingDirectory, original.RevisionId).Segments.SequenceEqual(original.Segments) &&
            TranscriptService.Load(request.MeetingDirectory, revised.RevisionId).Segments[0].Text == "人工修订第一句。",
            "重跑修改原版本或人工校订。");
        string export = Path.Combine(root, "normalized-final.srt");
        TranscriptSubtitleExporter.Export(request.MeetingDirectory, second.Revision!.RevisionId, export);
        var imported = TranscriptService.Import(new(request.MeetingDirectory, request.MeetingId, DurationMs, export, IsSynthetic: true));
        Need(imported.Segments.Select(value => (value.StartMs, value.EndMs, value.Text)).SequenceEqual(
            second.Revision.Segments.Select(value => (value.StartMs, value.EndMs, value.Text))) &&
            File.ReadAllText(export).Contains("00:00:39,715"), "规范化字幕往返丢失时间或文字。");
    }

    private static void ManualAndValid(string root)
    {
        var (request, result) = Run(root, "valid");
        Need(result.Status == "Succeeded" && result.Revision!.Segments[^1].EndMs == 39700 &&
            !result.Revision.Warnings.Any(value => value.StartsWith("Whisper 尾端时间", StringComparison.Ordinal)),
            "有效时间被不必要地延长到录音末尾。");
        string manual = M3TranscriptTests.Input(request.MeetingDirectory, "manual.srt", "1\n00:00:30,000 --> 00:00:39,840\n人工外部字幕\n");
        try
        {
            TranscriptService.Import(new(request.MeetingDirectory, request.MeetingId, DurationMs, manual, "whisper.cpp", IsSynthetic: true));
            throw new InvalidOperationException("手动导入仅自称 whisper.cpp 就绕过了严格时间合同。");
        }
        catch (InvalidDataException ex)
        { Need(ex.Message.Contains("39840") && ex.Message.Contains("39715") && ex.Message.Contains("125"), "手动越界错误缺少具体数值。"); }
    }

    private static (WhisperRequest Request, WhisperRunResult Result) Run(string root, string id, string? variant = null)
    {
        string meeting = M3TranscriptTests.Meeting(root, id), wave = Path.Combine(meeting, "synthetic-input.wav");
        int frames = checked((int)(DurationMs * 16)), bytes = frames * 2;
        using (var file = File.Create(wave))
        using (var writer = new BinaryWriter(file))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
            writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(bytes);
            for (int frame = 0; frame < frames; frame++) writer.Write((short)(frame % 97));
        }
        string model = M3TranscriptTests.Input(meeting, "synthetic-model.bin", Prefix + (variant ?? id));
        var request = new WhisperRequest(meeting, id, DurationMs, wave, M3TranscriptTests.Hash(wave),
            new(Path.Combine(AppContext.BaseDirectory, "ProjectEcho.Tests.exe"), model,
                ModelName: "synthetic timestamp fixture; not a real model", ModelLicenseAcknowledged: true), true);
        return (request, WhisperTranscriber.RunAsync(request).GetAwaiter().GetResult());
    }

    internal static int WriteFixture(string output, string behavior)
    {
        string variant = behavior[Prefix.Length..];
        if (variant.StartsWith("m3i-", StringComparison.Ordinal))
            return M3iWhisperTimestampTests.WriteFixture(output, variant["m3i-".Length..]);
        long start = variant == "after" ? DurationMs : variant == "backward" ? 39840 : 30000;
        long end = variant switch { "valid" => 39700, "limit" => 39965, "large" => 39966,
            "zero" => start, "backward" => 39000, _ => 39840 };
        var cues = new List<(long Start, long End, string Text)> { (start, end, "合成第一句。合成尾句。") };
        if (variant == "nonfinal") cues.Add((39000, 39700, "重叠后段。"));
        var srt = new StringBuilder();
        for (int index = 0; index < cues.Count; index++)
        {
            var cue = cues[index];
            srt.Append($"{index + 1}\n{Time(cue.Start)} --> {Time(cue.End)}\n{cue.Text}\n\n");
        }
        File.WriteAllText(output + ".srt", srt.ToString(), new UTF8Encoding(false));
        File.WriteAllText(output + ".txt", string.Join('\n', cues.Select(value => value.Text)), new UTF8Encoding(false));
        File.WriteAllText(output + ".json", JsonSerializer.Serialize(new
        {
            model = new { multilingual = true },
            transcription = cues.Select((cue, index) => new
            {
                text = cue.Text, offsets = new { from = cue.Start, to = cue.End },
                tokens = index == 0 ? new object[]
                {
                    new { id = 100, text = "合成第一句。", offsets = new { from = cue.Start, to = 35000L } },
                    new { id = 101, text = "合成尾句。", offsets = new { from = 35000L, to = cue.End } }
                } : new object[] { new { id = 100, text = cue.Text, offsets = new { from = cue.Start, to = cue.End } } }
            })
        }), new UTF8Encoding(false));
        return 0;
    }

    private static string Time(long value) => $"00:00:{value / 1000:D2},{value % 1000:D3}";
    private static void Need(bool value, string message) => M3TranscriptTests.Need(value, message);
}
