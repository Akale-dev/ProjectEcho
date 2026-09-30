using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProjectEcho.Core.Library;
using ProjectEcho.Core.Transcription;

internal static class M3cSqliteTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3c-sqlite"); Directory.CreateDirectory(root);
        run("M3C-SYNTH SQLite metadata migration filters sorting imported and incomplete meetings", () => Metadata(root));
        run("M3C-SYNTH SQLite duplicated meeting IDs remain independent directory identities", () => DuplicateIds(root));
        run("M3C-SYNTH SQLite corrupt and future cache rebuild preserves original and cache evidence", () => Rebuild(root));
        run("T50/M3C-SYNTH SQLite tampered paths and linked meeting metadata never escape data root", () => Safety(root));
        run("M3C-SYNTH SQLite recoverable deletion and rebuild cannot resurrect deleted meeting", () => Trash(root));
        run("M3C-SYNTH SQLite concurrent instances and cancellation retain complete metadata snapshots", () => Concurrency(root));
        run("T38/M3C-SYNTH SQLite FTS Chinese short strings literal operators and case-insensitive English", () => Search(root));
        run("T38/M3C-SYNTH SQLite revision refresh reopen and cache deletion preserve authoritative results", () => SearchRevisions(root));
        run("T20/T38/M3C-SYNTH SQLite corrupt search recovery still rejects changed originals", () => SearchIntegrity(root));
    }

    private static void Metadata(string root)
    {
        string data = NewRoot(root, "metadata");
        string first = Meeting(data, "old", "会议 A", "甲项目", "2026-09-29T12:00:00Z", 60000);
        Meeting(data, "new", "会议 B", "乙项目", "2026-09-30T12:00:00Z", 120000, imported: true);
        string incomplete = Path.Combine(data, "M1Recordings", "unfinished"); Directory.CreateDirectory(incomplete);
        File.WriteAllText(Path.Combine(incomplete, "checkpoint.json"), "{}");
        File.WriteAllText(Path.Combine(first, "filing.json"), "{\"title\":\"归档名\",\"project\":\"归档项目\"}");
        string original = Hash(Path.Combine(first, "meeting.json"));
        var index = new MeetingLibraryIndex(data); index.Refresh();
        var list = index.Query();
        Need(list.Count == 3 && list[0].Imported && list[0].Status == "saved" && list[2].Status == "incomplete", "导入/未完成状态或日期排序错误。");
        Need(list[1].Title == "归档名" && list[1].Project == "归档项目" && list[1].DurationMs == 60000, "filing未迁移到SQLite。");
        Need(index.Query(new(Project: "归档项目", MinDurationMs: 60000, MaxDurationMs: 60000)).Single().Directory == first, "项目/时长筛选错误。");
        Need(index.Query(new(DateFrom: DateTimeOffset.Parse("2026-09-30T00:00:00Z"), DateToExclusive: DateTimeOffset.Parse("2026-10-01T00:00:00Z"))).Single().Imported, "UTC日期筛选错误。");
        Need(index.Query(new(Sort: MeetingLibrarySort.DurationAscending))[0].Directory == first, "时长排序错误。");
        Need(index.Query(new(Sort: MeetingLibrarySort.TitleAscending)).Select(x => x.Title).SequenceEqual(list.Select(x => x.Title).Order(StringComparer.OrdinalIgnoreCase)), "名称排序错误。");
        Need(index.Projects().Contains("未分类") && index.Projects().Contains("归档项目"), "项目列表缺项。");
        Need(Hash(Path.Combine(first, "meeting.json")) == original, "SQLite迁移修改了原manifest。");
        Reject(() => index.Query(new(MinDurationMs: 100, MaxDurationMs: 99)));
    }

    private static void DuplicateIds(string root)
    {
        string data = NewRoot(root, "duplicates");
        Meeting(data, "copy-a", "相同名称", "项目", "2026-09-30T12:00:00Z", 1000, meetingId: "same-id");
        Meeting(data, "copy-b", "相同名称", "项目", "2026-09-30T12:00:00Z", 1000, meetingId: "same-id");
        var index = new MeetingLibraryIndex(data); index.Refresh();
        Need(index.Query().Count == 2 && index.Query().Select(x => x.Directory).Distinct().Count() == 2, "复制会议的同ID互相覆盖。");
    }

    private static void Rebuild(string root)
    {
        string data = NewRoot(root, "rebuild"); string meeting = Meeting(data, "original", "不可改变", "项目", "2026-09-30T12:00:00Z", 1000);
        string original = Hash(Path.Combine(meeting, "meeting.json"));
        var index = new MeetingLibraryIndex(data); index.Refresh();
        Execute(index.DatabasePath, "PRAGMA user_version=999"); index.Refresh();
        Need(index.Query().Count == 1 && Directory.GetFiles(data, "library.sqlite3.rebuild-*").Length == 1, "未来schema未保留备份重建。");
        File.WriteAllText(index.DatabasePath, "not a sqlite database"); index.Refresh();
        Need(index.Query().Count == 1 && Directory.GetFiles(data, "library.sqlite3.rebuild-*").Length == 2, "坏库没有恢复。");
        Execute(index.DatabasePath, "DROP TABLE meetings"); index.Refresh();
        Need(index.Query().Count == 1 && Directory.GetFiles(data, "library.sqlite3.rebuild-*").Length == 3, "结构损坏未保留重建。");
        Need(Hash(Path.Combine(meeting, "meeting.json")) == original, "重建更改了原件。");
    }

    private static void Safety(string root)
    {
        string data = NewRoot(root, "safety"); string meeting = Meeting(data, "safe", "安全", "项目", "2026-09-30T12:00:00Z", 1000);
        var index = new MeetingLibraryIndex(data); index.Refresh();
        Execute(index.DatabasePath, "UPDATE meetings SET directory_key='../outside'");
        Reject(() => index.Query()); index.Refresh();
        Reject(() => index.MoveToTrash(data)); Reject(() => index.MoveToTrash(Path.Combine(data, "..", "other")));
        Need(Directory.Exists(meeting), "越界拒绝仍移动了会议。");
        string bad = Path.Combine(data, "M1Recordings", "bad"); Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "meeting.json"), "{invalid"); index.Refresh();
        Need(index.Query().Single(x => x.Directory == bad).Status == "metadata_error", "坏metadata隐藏了可恢复会议。");
        // Junction creation is local and does not require symlink privilege on Windows.
        string linked = Path.Combine(data, "M1Recordings", "linked");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{linked}\" \"{meeting}\"", UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        process.WaitForExit(); Need(process.ExitCode == 0, "测试junction创建失败，不能标通过。");
        index.Refresh(); Need(index.Query().Count == 2, "重解析目录进入会议库。");
        Reject(() => index.MoveToTrash(linked));
        string childLink = Path.Combine(meeting, "linked-child");
        using var childProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{childLink}\" \"{bad}\"", UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        childProcess.WaitForExit(); Need(childProcess.ExitCode == 0, "测试子junction创建失败，不能标通过。");
        Reject(() => index.MoveToTrash(meeting)); Need(Directory.Exists(meeting), "删除检查失败仍移动了会议。");
    }

    private static void Trash(string root)
    {
        string data = NewRoot(root, "trash"); string meeting = Meeting(data, "delete-me", "保留原件", "项目", "2026-09-30T12:00:00Z", 1000);
        string original = Hash(Path.Combine(meeting, "meeting.json"));
        var index = new MeetingLibraryIndex(data); index.Refresh(); string trash = index.MoveToTrash(meeting);
        Need(!Directory.Exists(meeting) && Directory.Exists(trash) && Path.GetFileName(trash) == Path.GetFileName(meeting) &&
            Hash(Path.Combine(trash, "meeting.json")) == original, "删除没有完整保留原目录身份。");
        Need(index.Query().Count == 0, "旧库残留删除会议。"); File.Delete(index.DatabasePath); index.Refresh();
        Need(new MeetingLibraryIndex(data).Query().Count == 0, "重建后删除会议复活。");
        // Manual restore is a directory move back, so originals stay portable and recoverable.
        Directory.Move(trash, meeting); index.Refresh(); Need(index.Query().Count == 1, "手动恢复目录未重新入库。");
    }

    private static void Concurrency(string root)
    {
        string data = NewRoot(root, "concurrency");
        for (int i = 0; i < 10; i++) Meeting(data, "m" + i, "会议" + i, "项目", "2026-09-30T12:00:00Z", 1000 + i);
        var index = new MeetingLibraryIndex(data); index.Refresh();
        Parallel.For(0, 8, _ =>
        {
            var parallel = new MeetingLibraryIndex(data); parallel.Refresh(); Need(parallel.Query().Count == 10, "并发刷新暴露部分快照。");
        });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Reject(() => index.Refresh(cancelled.Token)); Reject(() => index.Query(cancellationToken: cancelled.Token));
        Need(index.Query().Count == 10, "取消破坏已有库。");
    }

    private static void Search(string root)
    {
        string data = NewRoot(root, "search"); string meeting = Meeting(data, "search", "搜索", "项目", "2026-09-30T12:00:00Z", 10000);
        string input = Path.Combine(meeting, "source.txt");
        File.WriteAllText(input, "中文全文检索预算调整。\n\nHello SQLITE ordinary words.\n\nSQL OR NEAR * \"quoted\" 100% _underscore emoji 😀保持。\n\n繁體中文預算調整。");
        var revision = TranscriptService.Import(new(meeting, "search", 10000, input, IsSynthetic: true));
        foreach (string query in new[] { "预算", "全文检索", "sqlite", "or near", "\"quoted\"", "100%", "_underscore", "😀保持", "預算" })
            Need(TranscriptService.Search(meeting, query).Count == 1, "SQLite字面搜索失败：" + query);
        Need(TranscriptService.Search(meeting, "中文").Count == 2 && TranscriptService.Search(meeting, "中").Count == 2, "短中文应该同时匹配简繁文本。");
        Need(TranscriptService.Search(meeting, "' OR 1=1; --").Count == 0, "SQL语法被执行。");
        Need(TranscriptService.Search(meeting, "不存在").Count == 0, "无结果搜索返回伪文本。");
        string db = Path.Combine(meeting, "transcripts", "search.sqlite3");
        Need(File.Exists(db) && Scalar(db, "SELECT count(*) FROM sqlite_schema WHERE name='segments_fts'") == 1, "未使用真实FTS5索引。");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT sqlite_version()";
            File.WriteAllText(Path.Combine(root, "sqlite-native-version.txt"), command.ExecuteScalar()?.ToString());
        }
        Need(TranscriptService.Search(meeting, "SQLITE").Single().RevisionId == revision.RevisionId, "版本身份丢失。");
    }

    private static void SearchRevisions(string root)
    {
        string data = NewRoot(root, "search-revisions"); string meeting = Meeting(data, "versions", "版本", "项目", "2026-09-30T12:00:00Z", 10000);
        string input = Path.Combine(meeting, "source.srt"); File.WriteAllText(input, "1\n00:00:01,000 --> 00:00:02,000\n原稿预算\n");
        var original = TranscriptService.Import(new(meeting, "versions", 10000, input, IsSynthetic: true));
        Need(TranscriptService.Search(meeting, "预算").Count == 1, "初稿检索失败。");
        var revised = TranscriptService.CreateRevision(meeting, original.RevisionId, [original.Segments[0] with { Text = "校订预算增加" }]);
        var hits = TranscriptService.Search(meeting, "预算");
        Need(hits.Count == 2 && hits[0].IsReviewed && hits[0].RevisionId == revised.RevisionId && hits[0].StartMs == 1000, "修订没有刷新SQLite或时间丢失。");
        File.Delete(Path.Combine(meeting, "transcripts", "search.sqlite3"));
        Need(TranscriptService.Search(meeting, "预算").Count == 2, "删除缓存后未从原件重建。");
        Parallel.For(0, 4, _ => Need(TranscriptService.Search(meeting, "预算").Count == 2, "并发搜索失败。"));
    }

    private static void SearchIntegrity(string root)
    {
        string data = NewRoot(root, "search-integrity"); string meeting = Meeting(data, "integrity", "原件", "项目", "2026-09-30T12:00:00Z", 10000);
        string input = Path.Combine(meeting, "source.txt"); File.WriteAllText(input, "可信原件预算");
        var revision = TranscriptService.Import(new(meeting, "integrity", 10000, input, IsSynthetic: true));
        Need(TranscriptService.Search(meeting, "预算").Count == 1, "首次搜索失败。");
        string db = Path.Combine(meeting, "transcripts", "search.sqlite3"); File.WriteAllText(db, "broken derived database");
        Need(TranscriptService.Search(meeting, "预算").Count == 1 && Directory.GetFiles(Path.GetDirectoryName(db)!, "search.sqlite3.rebuild-*").Length == 1, "损坏索引未保留重建。");
        Execute(db, "DELETE FROM segments");
        Need(TranscriptService.Search(meeting, "预算").Count == 1, "缓存段丢失但source指纹不变时，权威原稿从结果消失。");
        Execute(db, "UPDATE segments SET folded='FICTIONAL'; INSERT INTO segments_fts(segments_fts) VALUES('rebuild')");
        Need(TranscriptService.Search(meeting, "预算").Count == 1 && TranscriptService.Search(meeting, "fictional").Count == 0,
            "同数缓存文本被篡改后，真实搜索漏检或伪造文本被返回。");
        Execute(db, "INSERT INTO segments_fts(segments_fts) VALUES('delete-all')");
        Need(TranscriptService.Search(meeting, "可信原件").Count == 1, "只损坏FTS词元导致原稿漏检。");
        Need(Directory.GetFiles(Path.GetDirectoryName(db)!, "search.sqlite3.rebuild-*").Length == 4,
            "损坏缓存证据未全部保留。");
        File.AppendAllText(Path.Combine(meeting, revision.OriginalFile), "篡改");
        Reject(() => TranscriptService.Search(meeting, "预算"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); Reject(() => TranscriptService.Search(meeting, "预算", cancelled.Token));
    }

    private static string NewRoot(string root, string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private static string Meeting(string data, string name, string title, string project, string date, long duration,
        bool imported = false, string? meetingId = null)
    {
        string path = Path.Combine(data, "M1Recordings", name); Directory.CreateDirectory(path);
        var node = new Dictionary<string, object?> { ["schemaVersion"] = imported ? "m3-audio-import-1" : "m1-recording-1",
            ["title"] = title, ["project"] = project, ["duration100ns"] = duration * 10000,
            ["storageComplete"] = true, ["meetingId"] = meetingId ?? name, ["isSynthetic"] = true,
            [imported ? "importedAt" : "startedAtUtc"] = date };
        File.WriteAllText(Path.Combine(path, "meeting.json"), JsonSerializer.Serialize(node)); return path;
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static long Scalar(string database, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar());
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or OperationCanceledException) { return; }
        throw new InvalidOperationException("预期拒绝，但操作成功。");
    }
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
