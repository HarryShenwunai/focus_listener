using Microsoft.Data.Sqlite;

namespace FocusListener;

public sealed record DataRetentionResult(int SessionsRemoved, int EventsRemoved);

public static class SqliteDataRetention
{
    public static async Task<DataRetentionResult> PurgeExpiredAsync(
        string databasePath,
        int retentionDays = 30,
        CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (retentionDays is < 1 or > 365)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        }

        var path = Path.GetFullPath(databasePath);
        if (!File.Exists(path))
        {
            return new DataRetentionResult(0, 0);
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellation);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellation);

        var sessions = await CountAsync(
            connection,
            transaction,
            "SELECT COUNT(*) FROM sessions WHERE julianday(started_at) < julianday($cutoff);",
            cutoff,
            cancellation);
        var events = await CountAsync(
            connection,
            transaction,
            """
            SELECT COUNT(*)
            FROM session_events
            WHERE session_id IN (SELECT session_id FROM sessions WHERE julianday(started_at) < julianday($cutoff));
            """,
            cutoff,
            cancellation);

        await ExecuteAsync(
            connection,
            transaction,
            """
            DELETE FROM session_events
            WHERE session_id IN (SELECT session_id FROM sessions WHERE julianday(started_at) < julianday($cutoff));
            """,
            cutoff,
            cancellation);
        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM sessions WHERE julianday(started_at) < julianday($cutoff);",
            cutoff,
            cancellation);
        await transaction.CommitAsync(cancellation);
        return new DataRetentionResult(sessions, events);
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string cutoff,
        CancellationToken cancellation)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellation));
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string cutoff,
        CancellationToken cancellation)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        await command.ExecuteNonQueryAsync(cancellation);
    }
}
