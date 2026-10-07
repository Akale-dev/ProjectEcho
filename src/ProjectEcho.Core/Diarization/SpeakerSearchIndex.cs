using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProjectEcho.Core.Library;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.Core.Diarization;

/// <summary>Rebuildable meeting-local index. JSON, source text and speaker IDs remain authoritative.</summary>
public static class SpeakerSearchIndex
{
    private const string Schema = """
        CREATE TABLE source_state (id INTEGER PRIMARY KEY CHECK(id=1), fingerprint TEXT NOT NULL);
        CREATE TABLE speaker_segments (revision_id TEXT NOT NULL, segment_id TEXT NOT NULL,
          speaker_id TEXT, display_name TEXT NOT NULL, body TEXT NOT NULL, folded TEXT NOT NULL,
          revision_order INTEGER NOT NULL, segment_order INTEGER NOT NULL,
          PRIMARY KEY(revision_id,segment_id));
        """;

    public static IReadOnlyList<TranscriptSearchHit> Search(string meetingDirectory, string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        if (query.Length > 1000) throw new ArgumentException("搜索词不能超过 1000 字符。", nameof(query));
        string meeting = TranscriptFiles.Meeting(meetingDirectory);
        // Verify every authority file before consulting the disposable cache. A corrupt source
        // cannot become trusted because the previously indexed version happened to be valid.
        var revisions = SpeakerService.ListRevisions(meeting, cancellationToken);
        var rows = revisions.SelectMany((revision, ri) => revision.Segments.Select((segment, si) =>
            new Row(revision, segment, SpeakerDisplay.Label(revision, segment), ri, si))).ToArray();
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(revisions, TranscriptFiles.Json)));
        string database = TranscriptFiles.Inside(meeting, "diarization/search.sqlite3");
        return SqliteDerivedCache.Use(database, 1, Schema, connection =>
        {
            using var transaction = connection.BeginTransaction();
            EnsureCurrent(connection, transaction, rows, fingerprint, cancellationToken);
            // A literal substring query also works for one-character Chinese names. Names and
            // body are separate authority fields; export prefixes never pollute spoken text.
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT revision_id,segment_id FROM speaker_segments WHERE instr(folded,$query)>0 ORDER BY revision_order,segment_order LIMIT 5000";
            command.Parameters.AddWithValue("$query", query.ToUpperInvariant());
            var lookup = rows.ToDictionary(row => (row.Revision.RevisionId, row.Segment.SegmentId));
            var hits = new List<TranscriptSearchHit>();
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!lookup.TryGetValue((reader.GetString(0), reader.GetString(1)), out var row) ||
                        !row.Folded.Contains(query.ToUpperInvariant(), StringComparison.Ordinal))
                        throw new SqliteDerivedCache.RebuildRequiredException();
                    hits.Add(new(row.Revision.RevisionId, row.Segment.SegmentId, row.Segment.StartMs,
                        row.Segment.EndMs, row.Segment.Text, row.Label, row.Revision.Corrections.Count > 0));
                }
            cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
            return (IReadOnlyList<TranscriptSearchHit>)hits;
        });
    }

    private static void EnsureCurrent(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<Row> rows, string fingerprint, CancellationToken token)
    {
        using var current = connection.CreateCommand(); current.Transaction = transaction;
        current.CommandText = "SELECT fingerprint FROM source_state WHERE id=1";
        if (current.ExecuteScalar() is string saved && saved == fingerprint)
        {
            using var verify = connection.CreateCommand(); verify.Transaction = transaction;
            verify.CommandText = "SELECT revision_id,segment_id,speaker_id,display_name,body,folded,revision_order,segment_order FROM speaker_segments ORDER BY revision_order,segment_order";
            using var reader = verify.ExecuteReader();
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                if (!reader.Read() || reader.GetString(0) != row.Revision.RevisionId ||
                    reader.GetString(1) != row.Segment.SegmentId ||
                    (reader.IsDBNull(2) ? null : reader.GetString(2)) != row.Segment.SpeakerId ||
                    reader.GetString(3) != row.Label || reader.GetString(4) != row.Segment.Text ||
                    reader.GetString(5) != row.Folded || reader.GetInt64(6) != row.RevisionOrder ||
                    reader.GetInt64(7) != row.SegmentOrder)
                    throw new SqliteDerivedCache.RebuildRequiredException();
            }
            if (reader.Read()) throw new SqliteDerivedCache.RebuildRequiredException();
            return;
        }
        SqliteDerivedCache.Execute(connection, "DELETE FROM speaker_segments; DELETE FROM source_state;", transaction);
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO speaker_segments VALUES($revision,$segment,$speaker,$name,$body,$folded,$ro,$so)";
        foreach (string name in new[] { "$revision", "$segment", "$speaker", "$name", "$body", "$folded", "$ro", "$so" })
            insert.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            insert.Parameters["$revision"].Value = row.Revision.RevisionId;
            insert.Parameters["$segment"].Value = row.Segment.SegmentId;
            insert.Parameters["$speaker"].Value = (object?)row.Segment.SpeakerId ?? DBNull.Value;
            insert.Parameters["$name"].Value = row.Label;
            insert.Parameters["$body"].Value = row.Segment.Text;
            insert.Parameters["$folded"].Value = row.Folded;
            insert.Parameters["$ro"].Value = row.RevisionOrder;
            insert.Parameters["$so"].Value = row.SegmentOrder; insert.ExecuteNonQuery();
        }
        using var state = connection.CreateCommand(); state.Transaction = transaction;
        state.CommandText = "INSERT INTO source_state VALUES(1,$hash)";
        state.Parameters.AddWithValue("$hash", fingerprint); state.ExecuteNonQuery();
    }

    private sealed record Row(SpeakerRevision Revision, SpeakerSegment Segment, string Label,
        int RevisionOrder, int SegmentOrder)
    {
        internal string Folded => (Segment.Text + "\n" + Label).ToUpperInvariant();
    }
}
