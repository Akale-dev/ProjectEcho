using Microsoft.Data.Sqlite;
using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;

internal static class M5SpeakerIndexTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m5-speaker-index"); Directory.CreateDirectory(root);
        run("P5-03 SYNTH speaker name and text index rebuild preserves stable IDs and all revisions", () => Rebuild(root));
        run("P5-03 SYNTH same-count cache identity tampering is rebuilt from immutable speaker files", () => Tamper(root));
        run("P5-03 SYNTH corrupted source cannot be replaced by a previously valid speaker cache", () => CorruptSource(root));
    }

    private static void Rebuild(string root)
    {
        var (meeting, source, revision) = Fixture(root, "rebuild");
        string sourceHash = M3TranscriptTests.Hash(Path.Combine(meeting, source.OriginalFile));
        string speakerId = revision.Speakers[0].SpeakerId;
        var renamed = SpeakerService.Rename(meeting, revision.RevisionId, speakerId, "主持人甲");
        var nameHits = SpeakerSearchIndex.Search(meeting, "主持");
        Need(nameHits.Count == 1 && nameHits[0].RevisionId == renamed.RevisionId &&
            nameHits[0].Text == "收入没有下降。" && nameHits[0].SpeakerLabel == "主持人甲", "名称没有独立索引或污染原文。");
        Need(SpeakerSearchIndex.Search(meeting, "收入").Count == 2, "索引去掉了旧版本正文。");
        string database = Path.Combine(meeting, "diarization", "search.sqlite3");
        File.WriteAllBytes(database, [1, 2, 3, 4, 5]);
        var rebuilt = SpeakerSearchIndex.Search(meeting, "主持");
        Need(rebuilt.SequenceEqual(nameHits), "SQLite损坏重建改变了标签或正文。");
        Need(Directory.EnumerateFiles(Path.GetDirectoryName(database)!, "search.sqlite3.rebuild-*").Any(), "损坏索引证据未保留。");
        var reopened = SpeakerService.Load(meeting, renamed.RevisionId);
        Need(reopened.Speakers[0].SpeakerId == speakerId && reopened.ParentRevisionId == revision.RevisionId &&
            M3TranscriptTests.Hash(Path.Combine(meeting, source.OriginalFile)) == sourceHash, "重开或索引更改了身份/原稿。");
    }

    private static void Tamper(string root)
    {
        var (meeting, _, revision) = Fixture(root, "tamper");
        var before = SpeakerSearchIndex.Search(meeting, "收入");
        string database = Path.Combine(meeting, "diarization", "search.sqlite3");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE speaker_segments SET speaker_id='fake',display_name='错误姓名',folded='HIDDEN'";
            command.ExecuteNonQuery();
        }
        var after = SpeakerSearchIndex.Search(meeting, "收入");
        Need(after.SequenceEqual(before) && after[0].SpeakerLabel == "A", "同数量缓存篡改隐藏正文或误配身份。");
        Need(SpeakerService.Load(meeting, revision.RevisionId).Speakers[0].Code == "A", "缓存污染了权威代号。");
    }

    private static void CorruptSource(string root)
    {
        var (meeting, source, _) = Fixture(root, "corrupt-source");
        _ = SpeakerSearchIndex.Search(meeting, "收入");
        File.AppendAllText(Path.Combine(meeting, source.OriginalFile), "更改原稿");
        M3TranscriptTests.Reject(() => SpeakerSearchIndex.Search(meeting, "收入"));
    }

    private static (string Meeting, TranscriptRevision Source, SpeakerRevision Revision) Fixture(string root, string name)
    {
        string meeting = M3TranscriptTests.Meeting(root, name);
        string input = M3TranscriptTests.Input(meeting, "source.srt", "1\n00:00:00,000 --> 00:00:02,000\n收入没有下降。\n\n2\n00:00:03,000 --> 00:00:04,000\n请确认单位。\n");
        var source = TranscriptService.Import(new(meeting, name, 10000, input, IsSynthetic: true));
        var speakers = new[] { new MeetingSpeaker(Guid.NewGuid().ToString("N"), "A"), new MeetingSpeaker(Guid.NewGuid().ToString("N"), "B") };
        var analysis = SpeakerService.SaveAnalysis(meeting, new SpeakerAnalysis(1, name, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            new(new string('a', 64), new string('b', 64), 16000, 160000, "elapsed-v1"),
            new("synthetic-index-fixture", "1", new string('c', 64), new string('d', 64), new string('e', 64), null, .9, "synthetic", "global-fixture"),
            speakers, [new(0, 2000, speakers[0].SpeakerId), new(3000, 4000, speakers[1].SpeakerId)], [], true));
        return (meeting, source, SpeakerService.CreateRevision(meeting, source.RevisionId, analysis.AnalysisId));
    }

    private static void Need(bool condition, string message) => M3TranscriptTests.Need(condition, message);
}
