using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.Core.Library;

internal static class SqliteTranscriptSearch
{
    private const string Schema = """
        CREATE TABLE source_state (id INTEGER PRIMARY KEY CHECK(id=1), fingerprint TEXT NOT NULL);
        CREATE TABLE segments (id INTEGER PRIMARY KEY, revision_id TEXT NOT NULL, segment_id TEXT NOT NULL,
          folded TEXT NOT NULL, revision_order INTEGER NOT NULL, segment_order INTEGER NOT NULL,
          UNIQUE(revision_id,segment_id));
        CREATE VIRTUAL TABLE segments_fts USING fts5(folded,content='segments',content_rowid='id',
          tokenize='trigram case_sensitive 1');
        """;

    internal static IReadOnlyList<TranscriptSearchHit> Search(string meetingDirectory, string query,
        CancellationToken cancellationToken)
    {
        string meeting = TranscriptFiles.Meeting(meetingDirectory);
        // Load verifies originals and all timing metadata before anything can become a search result.
        // A cache can never replace a revision or elevate corrupted JSON to a trusted transcript.
        var revisions = TranscriptService.ListRevisions(meeting, cancellationToken);
        string database = TranscriptFiles.Inside(meeting, "transcripts/search.sqlite3");
        string fingerprint = Fingerprint(revisions);
        var authoritative = revisions.SelectMany(revision => revision.Segments.Select(segment => (revision, segment)))
            .ToDictionary(item => (item.revision.RevisionId, item.segment.SegmentId));
        string folded = query.ToUpperInvariant();
        return SqliteDerivedCache.Use(database, 1, Schema, connection =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // One transaction holds the validated table/FTS snapshot through the query,
            // including against another app process refreshing the same cache.
            using var transaction = connection.BeginTransaction();
            EnsureCurrent(connection, transaction, fingerprint, revisions, cancellationToken);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            // FTS5 trigram supports literal substring matching in Chinese and English. Fewer than
            // three Unicode scalars have no trigram; use an exact SQLite substring scan for these.
            bool trigram = folded.EnumerateRunes().Take(3).Count() == 3 && !folded.Contains('\0');
            command.CommandText = trigram
                ? "SELECT s.revision_id,s.segment_id FROM segments_fts JOIN segments s ON s.id=segments_fts.rowid WHERE segments_fts MATCH $query ORDER BY s.revision_order,s.segment_order LIMIT 5000"
                : "SELECT revision_id,segment_id FROM segments WHERE instr(folded,$query)>0 ORDER BY revision_order,segment_order LIMIT 5000";
            command.Parameters.AddWithValue("$query", trigram ? "\"" + folded.Replace("\"", "\"\"") + "\"" : folded);
            using var reader = command.ExecuteReader(); var hits = new List<TranscriptSearchHit>();
            var seen = new HashSet<(string, string)>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string revisionId = reader.GetString(0), segmentId = reader.GetString(1);
                if (!authoritative.TryGetValue((revisionId, segmentId), out var item)) throw new SqliteDerivedCache.RebuildRequiredException();
                var (revision, segment) = item;
                if (!segment.Text.Contains(query, StringComparison.OrdinalIgnoreCase) || !seen.Add((revisionId, segmentId)))
                    throw new SqliteDerivedCache.RebuildRequiredException();
                hits.Add(new(revision.RevisionId, segment.SegmentId, segment.StartMs, segment.EndMs,
                    segment.Text, segment.SpeakerLabel, revision.ParentRevisionId is not null));
            }
            reader.Close(); cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
            return (IReadOnlyList<TranscriptSearchHit>)hits;
        });
    }

    private static void EnsureCurrent(SqliteConnection connection, SqliteTransaction transaction, string fingerprint,
        IReadOnlyList<TranscriptRevision> revisions, CancellationToken token)
    {
        using var current = connection.CreateCommand(); current.Transaction = transaction;
        current.CommandText = "SELECT fingerprint FROM source_state WHERE id=1";
        if (current.ExecuteScalar() is string value && value == fingerprint)
        {
            ValidateCachedContent(connection, transaction, revisions, token);
            return;
        }
        SqliteDerivedCache.Execute(connection, "DELETE FROM segments; DELETE FROM source_state;", transaction);
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO segments(revision_id,segment_id,folded,revision_order,segment_order) VALUES($revision,$segment,$text,$ro,$so)";
        foreach (string name in new[] { "$revision", "$segment", "$text", "$ro", "$so" }) insert.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        for (int r = 0; r < revisions.Count; r++)
            for (int s = 0; s < revisions[r].Segments.Count; s++)
            {
                token.ThrowIfCancellationRequested(); var segment = revisions[r].Segments[s];
                insert.Parameters["$revision"].Value = revisions[r].RevisionId;
                insert.Parameters["$segment"].Value = segment.SegmentId;
                insert.Parameters["$text"].Value = segment.Text.ToUpperInvariant();
                insert.Parameters["$ro"].Value = r; insert.Parameters["$so"].Value = s; insert.ExecuteNonQuery();
            }
        SqliteDerivedCache.Execute(connection, "INSERT INTO segments_fts(segments_fts) VALUES('rebuild')", transaction);
        using var state = connection.CreateCommand(); state.Transaction = transaction;
        state.CommandText = "INSERT INTO source_state(id,fingerprint) VALUES(1,$hash)";
        state.Parameters.AddWithValue("$hash", fingerprint); state.ExecuteNonQuery();
        token.ThrowIfCancellationRequested();
    }

    private static void ValidateCachedContent(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<TranscriptRevision> revisions, CancellationToken token)
    {
        using var count = connection.CreateCommand(); count.Transaction = transaction;
        count.CommandText = "SELECT count(*) FROM segments";
        if (Convert.ToInt64(count.ExecuteScalar()) != revisions.Sum(revision => (long)revision.Segments.Count))
            throw new SqliteDerivedCache.RebuildRequiredException();
        using (var rows = connection.CreateCommand())
        {
            rows.Transaction = transaction;
            rows.CommandText = "SELECT revision_id,segment_id,folded,revision_order,segment_order FROM segments ORDER BY revision_order,segment_order";
            using var reader = rows.ExecuteReader();
            for (int r = 0; r < revisions.Count; r++)
                for (int s = 0; s < revisions[r].Segments.Count; s++)
                {
                    token.ThrowIfCancellationRequested(); var revision = revisions[r]; var segment = revision.Segments[s];
                    // A matching saved fingerprint alone cannot attest cached rows: a same-count
                    // text edit would otherwise silently hide the real sentence from FTS results.
                    if (!reader.Read() || reader.GetValue(0) is not string revisionId || revisionId != revision.RevisionId ||
                        reader.GetValue(1) is not string segmentId || segmentId != segment.SegmentId ||
                        reader.GetValue(2) is not string folded || folded != segment.Text.ToUpperInvariant() ||
                        reader.GetValue(3) is not long revisionOrder || revisionOrder != r ||
                        reader.GetValue(4) is not long segmentOrder || segmentOrder != s)
                        throw new SqliteDerivedCache.RebuildRequiredException();
                }
            if (reader.Read()) throw new SqliteDerivedCache.RebuildRequiredException();
        }
        token.ThrowIfCancellationRequested();
        // rank=1 also compares an external-content FTS index against its content table.
        // SQLite reports SQLITE_CORRUPT_VTAB when token rows are missing or inconsistent.
        SqliteDerivedCache.Execute(connection,
            "INSERT INTO segments_fts(segments_fts,rank) VALUES('integrity-check',1)", transaction);
    }

    private static string Fingerprint(IReadOnlyList<TranscriptRevision> revisions)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var revision in revisions)
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(revision, TranscriptFiles.Json));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
