using System.Text;
using System.Text.Json;
using ProjectEcho.Core.Transcription;

internal static class M3SentenceSegmentationTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3b-sentence-segmentation"); Directory.CreateDirectory(root);
        run("T35/T38-SYNTH sentence subtitles use token times and preserve raw output and full text", () => Sentences(root));
        run("T35/T38-SYNTH English decimal abbreviation and quoted sentence boundaries", () => English(root));
        run("T35/T38-SYNTH missing invalid or mismatched token times preserve original cues", () => Fallback(root));
        run("T35/T38-SYNTH unfinished sentences join without crossing pauses or losing tail", () => Join(root));
        run("T35/T38-SYNTH raw/manual import and historical revisions remain untouched", () => OriginalAndRerun(root));
    }

    private static void Sentences(string root)
    {
        var (_, result) = Run(root, "basic"); var revision = result.Revision!;
        var cues = revision.Segments;
        Need(cues.Count == 3 && cues[0].Text == "第一句。" && cues[1].Text == "第二句！" && cues[2].Text == "“Third sentence?”", "中英标点/闭引号分句错误。");
        Need(cues.Select(s => (s.StartMs, s.EndMs)).SequenceEqual(new (long?, long?)[] { (100, 350), (400, 600), (650, 900) }), "未使用实际token边界或改变首尾。");
        Need(cues.All(s => s.TimestampQuality == "approximate" && s.SpeakerLabel is null) && cues.Select(s => s.SegmentId).Distinct().Count() == 3,
            "分句伪造精确时间/人物或重复段标识。");
        Need(revision.Warnings.Any(w => w.Contains("拆分 2 处")), "分句来源说明丢失。");
    }

    private static void English(string root)
    {
        var (_, result) = Run(root, "english"); var cues = result.Revision!.Segments;
        Need(cues.Count == 2 && cues[0].Text == "Dr. Lee paid 3.14 dollars." && cues[1].Text == "Next sentence!" &&
            cues[0].EndMs == 500 && cues[1].StartMs == 550, "缩写/小数被错误切句或句子时间猜测。");
    }

    private static void Fallback(string root)
    {
        foreach (string variant in new[] { "missing", "backward", "mismatch", "invalid-json", "inside-token", "overlap" })
        {
            var (_, result) = Run(root, variant); var cues = result.Revision!.Segments;
            Need(cues.Count == 1 && cues[0].Text == "第一句。第二句！“Third sentence?”" && cues[0].StartMs == 100 && cues[0].EndMs == 900,
                $"{variant} 时未保留原段，或按字数伪造时间。");
            Need(result.Revision.Warnings.Any(w => w.Contains("保留")), "无法可靠分句时缺少解释。");
        }
    }

    private static void Join(string root)
    {
        var (_, result) = Run(root, "join"); var cues = result.Revision!.Segments;
        Need(cues.Count == 2 && cues[0].Text == "这是一个完整句子。" && cues[0].StartMs == 100 && cues[0].EndMs == 500 &&
            cues[1].Text == "保留尾声" && cues[1].StartMs == 700 && cues[1].EndMs == 900, "相邻未完句/尾段保留失败。");
        var (_, paused) = Run(root, "pause");
        Need(paused.Revision!.Segments.Count == 2 && paused.Revision.Segments[1].StartMs == 1700, "跨长暂停合并未完句。");
        var (_, overlapping) = Run(root, "cue-overlap");
        Need(overlapping.Revision!.Segments.Count == 2 && overlapping.Revision.Warnings.Any(w => w.Contains("重叠")), "重叠原段被吞并。");
        var (_, englishComma) = Run(root, "english-comma");
        Need(englishComma.Revision!.Segments.Single().Text == "Hello, world.", "英文逗号边界合并丢失空格。");
    }

    private static void OriginalAndRerun(string root)
    {
        var (request, first) = Run(root, "immutable", "basic");
        string raw = Path.Combine(first.AttemptDirectory, "whisper-output.srt");
        string hash = M3TranscriptTests.Hash(raw);
        Need(hash == first.Revision!.OriginalSha256 && hash == M3TranscriptTests.Hash(Path.Combine(request.MeetingDirectory, first.Revision.OriginalFile)),
            "模型原始SRT被分句改写。");
        var manual = TranscriptService.Import(new(request.MeetingDirectory, request.MeetingId, request.MeetingDurationMs, raw, IsSynthetic: true));
        Need(manual.Segments.Count == 1, "手动/历史SRT导入被无授权重新分句。");
        var reviewed = TranscriptService.CreateRevision(request.MeetingDirectory, first.Revision.RevisionId,
            first.Revision.Segments.Select((cue, index) => index == 0 ? cue with { Text = "人工校订第一句。" } : cue).ToArray());
        var rerun = WhisperTranscriber.RunAsync(request).GetAwaiter().GetResult();
        Need(rerun.Status == "Succeeded" && rerun.Revision!.RevisionId != first.Revision.RevisionId &&
            TranscriptService.Load(request.MeetingDirectory, reviewed.RevisionId).Segments[0].Text == "人工校订第一句。" &&
            TranscriptService.Load(request.MeetingDirectory, manual.RevisionId).Segments.Count == 1 &&
            M3TranscriptTests.Hash(raw) == hash && M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256,
            "重跑改写旧稿/校订/原音频。");
        string subtitle = Path.Combine(root, "sentences.srt");
        TranscriptSubtitleExporter.Export(request.MeetingDirectory, rerun.Revision!.RevisionId, subtitle);
        var exported = TranscriptService.Import(new(request.MeetingDirectory, request.MeetingId, request.MeetingDurationMs, subtitle, IsSynthetic: true));
        Need(exported.Segments.Select(s => (s.StartMs, s.EndMs, s.Text)).SequenceEqual(rerun.Revision.Segments.Select(s => (s.StartMs, s.EndMs, s.Text))), "新分句导出往返丢失时间/文字。");
    }

    private static (WhisperRequest Request, WhisperRunResult Result) Run(string root, string id, string? variant = null)
    {
        string meeting = M3TranscriptTests.Meeting(root, id), wave = Path.Combine(meeting, "input.wav");
        using (var stream = File.Create(wave))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(96036); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(96000);
            for (int i = 0; i < 48000; i++) writer.Write((short)(i % 100));
        }
        string model = M3TranscriptTests.Input(meeting, "fixture.bin", "synthetic-sentence-" + (variant ?? id));
        var request = new WhisperRequest(meeting, id, 3000, wave, M3TranscriptTests.Hash(wave),
            new(Path.Combine(AppContext.BaseDirectory, "ProjectEcho.Tests.exe"), model, ModelName: "synthetic-sentence-fixture", ModelLicenseAcknowledged: true), true);
        var result = WhisperTranscriber.RunAsync(request).GetAwaiter().GetResult();
        Need(result.Status == "Succeeded" && result.Revision is not null, "合成分句流程失败：" + result.Message);
        return (request, result);
    }

    internal static int WriteFixture(string output, string behavior)
    {
        string variant = behavior["synthetic-sentence-".Length..];
        var cues = new List<Cue> { new(100, 900, "第一句。第二句！“Third sentence?”",
            [T("第一句", 100, 340), T("。", 350, 350), T("第二句", 400, 590), T("！", 600, 600), T("“Third", 650, 720), T(" sentence", 720, 880), T("?”", 890, 900)]) };
        if (variant == "english") cues = [new(100, 900, "Dr. Lee paid 3.14 dollars. Next sentence!",
            [T("Dr", 100, 120), T(".", 120, 120), T(" Lee paid ", 120, 250), T("3", 250, 280), T(".", 280, 280),
                T("14 dollars", 280, 480), T(".", 500, 500), T(" Next sentence", 550, 880), T("!", 900, 900)])];
        if (variant == "join") cues = [new(100, 300, "这是一个", [T("这是一个", 100, 300)]), new(320, 500, "完整句子。", [T("完整句子。", 320, 500)]), new(700, 900, "保留尾声", [T("保留尾声", 700, 900)])];
        if (variant == "pause") cues = [new(100, 300, "未完句", [T("未完句", 100, 300)]), new(1700, 1900, "暂停后另句", [T("暂停后另句", 1700, 1900)])];
        if (variant == "cue-overlap") cues = [new(100, 400, "重叠前", [T("重叠前", 100, 400)]), new(300, 900, "重叠后", [T("重叠后", 300, 900)])];
        if (variant == "english-comma") cues = [new(100, 400, "Hello,", [T("Hello,", 100, 400)]), new(400, 900, "world.", [T("world.", 400, 900)])];
        if (variant == "missing") cues[0].Tokens[1].Remove("offsets");
        if (variant is "backward" or "overlap") cues[0].Tokens[2]["offsets"] = new { from = 300, to = variant == "backward" ? 290 : 500 };
        if (variant == "inside-token") cues[0] = cues[0] with { Tokens = [T(cues[0].Text, 100, 900)] };
        var srt = new StringBuilder();
        for (int i = 0; i < cues.Count; i++) srt.Append($"{i + 1}\n{Time(cues[i].From)} --> {Time(cues[i].To)}\n{cues[i].Text}\n\n");
        File.WriteAllText(output + ".srt", srt.ToString(), new UTF8Encoding(false));
        File.WriteAllText(output + ".txt", string.Join('\n', cues.Select(c => c.Text)), new UTF8Encoding(false));
        string json = JsonSerializer.Serialize(new { model = new { multilingual = true }, transcription = cues.Select(c => new
        { offsets = new { from = c.From, to = c.To }, text = variant == "mismatch" ? "不匹配文本" : c.Text,
            tokens = new[] { new Dictionary<string, object> { ["id"] = 50364, ["text"] = "[_BEG_]" } }.Concat(c.Tokens) }) });
        File.WriteAllText(output + ".json", variant == "invalid-json" ? "{" : json, new UTF8Encoding(false)); return 0;
    }

    private sealed record Cue(long From, long To, string Text, Dictionary<string, object>[] Tokens);
    private static Dictionary<string, object> T(string text, long from, long to) => new() { ["id"] = 100, ["text"] = text, ["offsets"] = new { from, to } };
    private static string Time(long value) => $"00:00:{value / 1000:D2},{value % 1000:D3}";
    private static void Need(bool condition, string message) => M3TranscriptTests.Need(condition, message);
}
