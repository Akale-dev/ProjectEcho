using System.Reflection;
using System.Text.Json;
using ProjectEcho.Core.Transcription;

internal static class M3jSentenceBoundaryTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3j-sentence-boundaries"); Directory.CreateDirectory(root);
        run("T35/T38-SYNTH M3J long unpunctuated speech prefers verified word pauses", () => PreferredPause(root));
        run("T35/T38-SYNTH M3J long subtitles prefer existing clause punctuation", () => PreferredClause(root));
        run("T35/T38-SYNTH M3J token gaps never split subwords or unspaced CJK", () => UnsafeGaps(root));
        run("T35/T38-SYNTH M3J phrase reflow is model independent and preserves raw punctuation", () => Models(root));
        run("T35/T38-SYNTH M3J invalid token evidence retains raw cues", () => InvalidTokens(root));
        run("T35/T38-SYNTH M3J dense short punctuation stays bounded and preserves text", () => DensePunctuation(root));
        run("T35/T38-SYNTH M3J Unicode word separators survive internal candidate rejoin", () => UnicodeSeparators(root));
    }

    private static readonly string First = "This is a synthetic complete phrase with several words";
    private static readonly string Tail = " followed by another long phrase that should remain connected to the next decoder window";
    private static readonly string Next = " and further discussion that carries on until the sentence finally ends.";

    private static void PreferredPause(string root)
    {
        var revision = Run(root, "pause", "base", First, Tail, 6500);
        Need(revision.Segments.Count == 2 && revision.Segments[0].Text == First &&
            revision.Segments[0].EndMs == 6000 && revision.Segments[1].StartMs == 6500,
            "达到长度保护时未选择已验证词间停顿。");
        Need(revision.Segments[1].Text == (Tail + Next).Trim() &&
            revision.Segments.All(c => c.TimestampQuality == "approximate"), "词句或原始时间质量丢失。");
    }

    private static void PreferredClause(string root)
    {
        var revision = Run(root, "clause", "base", First + ",", Tail, 6000);
        Need(revision.Segments.Count == 2 && revision.Segments[0].Text.EndsWith(',') &&
            revision.Segments[0].EndMs == 6000 && revision.Segments[1].StartMs == 6000,
            "未优先保留已有分句标点。");
    }

    private static void UnsafeGaps(string root)
    {
        foreach (var data in new[] { ("subword", new string('a', 70), new string('b', 75)),
                     ("cjk", new string('甲', 70), new string('乙', 75)) })
        {
            var revision = Run(root, data.Item1, "base", data.Item2, data.Item3, 6500);
            Need(revision.Segments[0].Text == data.Item2 + data.Item3 && revision.Segments[0].EndMs == 19000,
                "token 间隙被冒充词界。");
        }
        var giantGap = Run(root, "giant-gap", "base", First, Tail, 8000);
        Need(giantGap.Segments[0].EndMs == 19000, "无声学复核的巨大token间隙被用来重切原段。");
    }

    private static void Models(string root)
    {
        var a = Run(root, "base-model", "ggml-base", First, Tail, 6500);
        var b = Run(root, "turbo-model", "ggml-large-v3-turbo", First, Tail, 6500);
        Need(a.Segments.SequenceEqual(b.Segments), "分句逻辑依赖模型名称。");
        string text = string.Concat(a.Segments.Select(c => c.Text));
        Need(NoSpace(text) == NoSpace(First + Tail + Next) && text.Count(c => c == '.') == 1,
            "重排丢字、补字或改写标点。");
    }

    private static void InvalidTokens(string root)
    {
        var revision = Run(root, "invalid", "base", First, Tail, 5000);
        Need(revision.Segments[0].Text == First + Tail && revision.Segments[0].EndMs == 19000 &&
            revision.Segments.All(c => c.Text != First), "时码倒退的token被用于重切。");
    }

    private static void DensePunctuation(string root)
    {
        string meeting = M3TranscriptTests.Meeting(root, "dense-punctuation");
        string text = string.Join(' ', Enumerable.Repeat("word,", 10000));
        string srt = M3TranscriptTests.Input(meeting, "dense.srt", $"1\n00:00:00,000 --> 00:00:02,000\n{text}\n");
        string json = M3TranscriptTests.Input(meeting, "dense.json", JsonSerializer.Serialize(new
        {
            model = new { multilingual = true }, transcription = new[]
            {
                new { text, offsets = new { from = 0, to = 2000 }, tokens = Enumerable.Range(0, 10000).Select(i =>
                    new { id = 100, text = i == 0 ? "word," : " word,", offsets = new { from = i / 10, to = (i + 1) / 10 } }) }
            }
        }));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var method = typeof(TranscriptService).GetMethod("ImportWhisper", BindingFlags.Static | BindingFlags.NonPublic)!;
        var revision = (TranscriptRevision)method.Invoke(null,
            [new TranscriptImportRequest(meeting, "dense", 3000, srt, IsSynthetic: true), json, CancellationToken.None])!;
        Need(revision.Segments.Single().Text == text && timer.Elapsed < TimeSpan.FromSeconds(10),
            "密集短时标点被硬切、改写，或超出有界处理时间。");
    }

    private static void UnicodeSeparators(string root)
    {
        foreach (string separator in new[] { " ", "  ", "\t", "\u00a0" })
        {
            string id = "unicode-" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(separator));
            var revision = Run(root, id, "base", "José", separator + "mañana", 6500);
            Need(revision.Segments[0].Text == "José" + separator + "mañana",
                "内部候选重并改变了Unicode词之间的原始空白。");
        }
    }

    private static TranscriptRevision Run(string root, string name, string model, string first, string tail, long nextStart)
    {
        string meeting = M3TranscriptTests.Meeting(root, name);
        string srt = M3TranscriptTests.Input(meeting, "raw.srt",
            $"1\n00:00:00,000 --> 00:00:19,000\n{first}{tail}\n\n2\n00:00:19,000 --> 00:00:35,000\n{Next.Trim()}\n");
        object Token(string text, long from, long to) => new { id = 100, text, offsets = new { from, to } };
        string json = M3TranscriptTests.Input(meeting, "raw.json", JsonSerializer.Serialize(new
        {
            model = new { multilingual = true }, transcription = new[]
            {
                new { text = first + tail, offsets = new { from = 0, to = 19000 }, tokens = new[] { Token(first, 0, 6000), Token(tail, nextStart, 19000) } },
                new { text = Next, offsets = new { from = 19000, to = 35000 }, tokens = new[] { Token(Next, 19000, 35000) } }
            }
        }));
        string hash = M3TranscriptTests.Hash(srt);
        var method = typeof(TranscriptService).GetMethod("ImportWhisper", BindingFlags.Static | BindingFlags.NonPublic)!;
        var revision = (TranscriptRevision)method.Invoke(null,
            [new TranscriptImportRequest(meeting, name, 40000, srt, ModelName: model, IsSynthetic: true), json, CancellationToken.None])!;
        Need(M3TranscriptTests.Hash(srt) == hash, "原始SRT被改写。");
        return revision;
    }

    private static string NoSpace(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
    private static void Need(bool condition, string message) => M3TranscriptTests.Need(condition, message);
}
