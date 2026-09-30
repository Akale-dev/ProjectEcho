using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.Core.Library;

public enum MeetingLibrarySort { DateDescending, DateAscending, DurationDescending, DurationAscending, TitleAscending, ProjectAscending }

public sealed record MeetingLibraryQuery(DateTimeOffset? DateFrom = null, DateTimeOffset? DateToExclusive = null,
    string? Project = null, long? MinDurationMs = null, long? MaxDurationMs = null,
    MeetingLibrarySort Sort = MeetingLibrarySort.DateDescending);

public sealed record MeetingLibraryEntry(string Directory, string MeetingId, string Title, string Project,
    DateTimeOffset? Date, long? DurationMs, bool Imported, string Status);

/// <summary>A disposable projection of local manifests and filing metadata. Originals remain authoritative.</summary>
public sealed class MeetingLibraryIndex
{
    private const int SchemaVersion = 1;
    private const string Schema = """
        CREATE TABLE meetings (
          directory_key TEXT PRIMARY KEY COLLATE NOCASE, meeting_id TEXT NOT NULL, title TEXT NOT NULL,
          project TEXT NOT NULL, date_ms INTEGER, duration_ms INTEGER, imported INTEGER NOT NULL, status TEXT NOT NULL);
        CREATE INDEX meetings_date ON meetings(date_ms);
        CREATE INDEX meetings_project_date ON meetings(project,date_ms);
        CREATE INDEX meetings_duration ON meetings(duration_ms);
        """;
    private readonly string dataRoot;
    private readonly string recordingsRoot;
    public string DatabasePath { get; }

    public MeetingLibraryIndex(string dataRoot)
    {
        this.dataRoot = Path.TrimEndingDirectorySeparator(TranscriptFiles.LocalPath(dataRoot));
        recordingsRoot = TranscriptFiles.Inside(this.dataRoot, "M1Recordings");
        DatabasePath = TranscriptFiles.Inside(this.dataRoot, "library.sqlite3");
    }

    public IReadOnlyList<MeetingLibraryEntry> Refresh(CancellationToken cancellationToken = default)
    {
        lock (SqliteDerivedCache.Gate(DatabasePath))
        {
            cancellationToken.ThrowIfCancellationRequested(); TranscriptFiles.NoReparse(recordingsRoot);
            var entries = new List<MeetingLibraryEntry>();
            if (System.IO.Directory.Exists(recordingsRoot))
                foreach (string directory in System.IO.Directory.EnumerateDirectories(recordingsRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Path.GetFileName(directory).StartsWith('.')) continue;
                    // A linked directory is not part of this data root, even if it has the expected file names.
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    entries.Add(ReadEntry(ValidateMeetingDirectory(directory)));
                    if (entries.Count > 100000) throw new InvalidDataException("会议库超过 100000 场，请分开数据目录。");
                }
            SqliteDerivedCache.Use(DatabasePath, SchemaVersion, Schema, connection =>
            {
                using var transaction = connection.BeginTransaction();
                SqliteDerivedCache.Execute(connection, "DELETE FROM meetings", transaction);
                using var insert = connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO meetings VALUES($key,$id,$title,$project,$date,$duration,$imported,$status)";
                foreach (string name in new[] { "$key", "$id", "$title", "$project", "$date", "$duration", "$imported", "$status" })
                    insert.Parameters.Add(new SqliteParameter(name, DBNull.Value));
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    insert.Parameters["$key"].Value = Path.GetFileName(entry.Directory);
                    insert.Parameters["$id"].Value = entry.MeetingId; insert.Parameters["$title"].Value = entry.Title;
                    insert.Parameters["$project"].Value = entry.Project; insert.Parameters["$date"].Value = Db(entry.Date?.ToUnixTimeMilliseconds());
                    insert.Parameters["$duration"].Value = Db(entry.DurationMs); insert.Parameters["$imported"].Value = entry.Imported ? 1 : 0;
                    insert.Parameters["$status"].Value = entry.Status; insert.ExecuteNonQuery();
                }
                cancellationToken.ThrowIfCancellationRequested(); transaction.Commit(); return 0;
            });
            return entries;
        }
    }

    public IReadOnlyList<MeetingLibraryEntry> Query(MeetingLibraryQuery? query = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query ??= new();
        if (query.MinDurationMs < 0 || query.MaxDurationMs < 0 || query.MinDurationMs > query.MaxDurationMs ||
            query.DateFrom >= query.DateToExclusive || query.Project?.Length > 100 || !Enum.IsDefined(query.Sort))
            throw new ArgumentException("会议筛选范围无效。", nameof(query));
        return SqliteDerivedCache.Use(DatabasePath, SchemaVersion, Schema, connection =>
        {
            using var command = connection.CreateCommand();
            string order = query.Sort switch
            {
                MeetingLibrarySort.DateAscending => "date_ms IS NULL,date_ms ASC",
                MeetingLibrarySort.DurationDescending => "duration_ms IS NULL,duration_ms DESC",
                MeetingLibrarySort.DurationAscending => "duration_ms IS NULL,duration_ms ASC",
                MeetingLibrarySort.TitleAscending => "title COLLATE NOCASE ASC",
                MeetingLibrarySort.ProjectAscending => "project COLLATE NOCASE ASC,date_ms DESC",
                _ => "date_ms IS NULL,date_ms DESC"
            };
            command.CommandText = """
                SELECT directory_key,meeting_id,title,project,date_ms,duration_ms,imported,status FROM meetings
                WHERE ($from IS NULL OR date_ms >= $from) AND ($to IS NULL OR date_ms < $to)
                AND ($project IS NULL OR project = $project)
                AND ($min IS NULL OR duration_ms >= $min) AND ($max IS NULL OR duration_ms <= $max)
                ORDER BY
                """ + " " + order + ",directory_key COLLATE NOCASE";
            command.Parameters.AddWithValue("$from", Db(query.DateFrom?.ToUnixTimeMilliseconds()));
            command.Parameters.AddWithValue("$to", Db(query.DateToExclusive?.ToUnixTimeMilliseconds()));
            command.Parameters.AddWithValue("$project", Db(query.Project));
            command.Parameters.AddWithValue("$min", Db(query.MinDurationMs)); command.Parameters.AddWithValue("$max", Db(query.MaxDurationMs));
            using var reader = command.ExecuteReader(); var result = new List<MeetingLibraryEntry>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = ResolveKey(reader.GetString(0));
                if (!System.IO.Directory.Exists(directory)) continue; // A completed move/delete wins over a stale cache.
                result.Add(new(directory, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.GetInt64(6) != 0, reader.GetString(7)));
            }
            return (IReadOnlyList<MeetingLibraryEntry>)result;
        });
    }

    public IReadOnlyList<string> Projects(CancellationToken cancellationToken = default) =>
        Query(cancellationToken: cancellationToken).Select(x => x.Project).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Move a confirmed meeting to a recoverable local directory. Never recursively delete originals.</summary>
    public string MoveToTrash(string meetingDirectory)
    {
        lock (SqliteDerivedCache.Gate(DatabasePath))
        {
            string source = ValidateMeetingDirectory(meetingDirectory);
            if (!System.IO.Directory.Exists(source)) throw new DirectoryNotFoundException("会议已不在当前数据目录。");
            ValidateTree(source);
            string trash = TranscriptFiles.Inside(dataRoot, "DeletedMeetings"); System.IO.Directory.CreateDirectory(trash);
            string container = TranscriptFiles.Inside(trash, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(container);
            // Keep the original directory identity, including when several meetings have the same title.
            string destination = TranscriptFiles.Inside(container, Path.GetFileName(source));
            // A single same-volume directory rename publishes deletion. Refresh only scans M1Recordings,
            // so a crash after the move or a broken database cannot resurrect the meeting.
            System.IO.Directory.Move(source, destination);
            // Query always checks source existence. Cache cleanup is best effort after the authoritative move.
            try
            {
                SqliteDerivedCache.Use(DatabasePath, SchemaVersion, Schema, connection =>
                {
                    using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM meetings WHERE directory_key=$key";
                    command.Parameters.AddWithValue("$key", Path.GetFileName(source)); command.ExecuteNonQuery(); return 0;
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException) { }
            return destination;
        }
    }

    private string ValidateMeetingDirectory(string directory)
    {
        string full = TranscriptFiles.LocalPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), recordingsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("只能访问当前数据目录 M1Recordings 下的直接会议目录。");
        return full;
    }

    private string ResolveKey(string key)
    {
        if (key.Contains('/') || key.Contains('\\')) throw new InvalidDataException("会议索引目录标识无效，请重建会议库。");
        return ValidateMeetingDirectory(TranscriptFiles.Inside(recordingsRoot, key));
    }

    private static MeetingLibraryEntry ReadEntry(string directory)
    {
        string id = Path.GetFileName(directory), title = id, project = SessionDefaults.DefaultProject;
        DateTimeOffset? date = null; long? duration = null; bool imported = false; string status = "incomplete";
        try
        {
            string manifest = TranscriptFiles.Inside(directory, "meeting.json");
            if (File.Exists(manifest))
            {
                using var json = TranscriptFiles.ParseJson(TranscriptFiles.ReadBounded(manifest, default, 16_000_000));
                var node = json.RootElement;
                title = Text(node, "title", title, 200); project = Text(node, "project", project, 100);
                id = Text(node, "meetingId", id, 200);
                imported = node.TryGetProperty("importedAt", out var dateNode);
                if (!imported) node.TryGetProperty("startedAtUtc", out dateNode);
                if (dateNode.ValueKind == JsonValueKind.String && dateNode.TryGetDateTimeOffset(out var parsed)) date = parsed;
                if (node.TryGetProperty("duration100ns", out var span))
                {
                    if (!span.TryGetInt64(out long value) || value < 0 || value / 10000 > TranscriptFiles.MaximumDurationMs)
                        throw new InvalidDataException("会议时长无效。");
                    duration = value / 10000;
                }
                status = imported || (node.TryGetProperty("storageComplete", out var complete) && complete.ValueKind == JsonValueKind.True)
                    ? "saved" : "incomplete";
            }
            string filing = TranscriptFiles.Inside(directory, "filing.json");
            if (File.Exists(filing))
            {
                using var json = TranscriptFiles.ParseJson(TranscriptFiles.ReadBounded(filing, default, 2_000_000));
                title = Text(json.RootElement, "title", title, 200); project = Text(json.RootElement, "project", project, 100);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        { status = "metadata_error"; }
        return new(directory, id, string.IsNullOrWhiteSpace(title) ? id : title,
            string.IsNullOrWhiteSpace(project) ? SessionDefaults.DefaultProject : project, date, duration, imported, status);
    }

    private static string Text(JsonElement node, string key, string fallback, int maximum)
    {
        if (!node.TryGetProperty(key, out var value)) return fallback;
        string text = value.GetString() ?? fallback;
        return text.Length <= maximum ? text : text[..maximum];
    }

    private static void ValidateTree(string directory)
    {
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.Count > 0)
        {
            string current = pending.Pop(); TranscriptFiles.NoReparse(current);
            foreach (string file in System.IO.Directory.EnumerateFileSystemEntries(current))
            {
                TranscriptFiles.NoReparse(file);
                if (System.IO.Directory.Exists(file)) pending.Push(file);
            }
        }
    }

    private static object Db(object? value) => value ?? DBNull.Value;
}
