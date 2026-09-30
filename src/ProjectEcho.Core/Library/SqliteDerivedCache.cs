using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.Core.Library;

/// <summary>Only rebuildable indexes use this helper. No audio callback or original-file write depends on it.</summary>
internal static class SqliteDerivedCache
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    internal static object Gate(string path) => Gates.GetOrAdd(Path.GetFullPath(path), _ => new object());

    internal static T Use<T>(string path, int version, string schema, Func<SqliteConnection, T> action)
    {
        lock (Gate(path))
        {
            TranscriptFiles.LocalPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ValidateSidecars(path);
            try { return Run(path, version, schema, action); }
            catch (Exception ex) when (NeedsRebuild(ex))
            {
                // A damaged/old cache is evidence, not an original. Preserve it and rebuild once.
                string backup = path + ".rebuild-" + Guid.NewGuid().ToString("N");
                foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
                {
                    string source = path + suffix;
                    TranscriptFiles.NoReparse(source);
                    if (File.Exists(source)) File.Move(source, backup + suffix);
                }
                return Run(path, version, schema, action);
            }
        }
    }

    private static bool NeedsRebuild(Exception exception) => exception is RebuildRequiredException ||
        exception is SqliteException { SqliteErrorCode: 11 or 26 } ||
        exception is SqliteException { SqliteErrorCode: 1 } sql &&
        (sql.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase) ||
         sql.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase) ||
         sql.Message.Contains("malformed database schema", StringComparison.OrdinalIgnoreCase));

    private static T Run<T>(string path, int version, string schema, Func<SqliteConnection, T> action)
    {
        ValidateSidecars(path);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 2
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA trusted_schema=OFF; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        int actual = Convert.ToInt32(versionCommand.ExecuteScalar());
        if (actual != version && (actual != 0 || HasTables(connection))) throw new RebuildRequiredException();
        if (actual == 0)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, schema, transaction);
            Execute(connection, "PRAGMA user_version=" + version, transaction);
            transaction.Commit();
        }
        return action(connection);
    }

    private static bool HasTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private static void ValidateSidecars(string path)
    {
        foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" }) TranscriptFiles.NoReparse(path + suffix);
    }

    internal static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    internal sealed class RebuildRequiredException : Exception;
}
