using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FocusListener;

internal sealed class SqliteSessionJournalAdapter : ISessionJournal
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SqliteSessionJournalAdapter(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async ValueTask InitializeAsync(
        SessionId sessionId,
        SessionStart start,
        DateTimeOffset startedAt,
        CancellationToken cancellation)
    {
        await _writeGate.WaitAsync(cancellation);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellation);
            await using var schema = connection.CreateCommand();
            schema.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS sessions (
                    session_id TEXT PRIMARY KEY,
                    started_at TEXT NOT NULL,
                    completed_at TEXT NULL,
                    classroom_kind TEXT NOT NULL,
                    planned_seconds INTEGER NOT NULL,
                    attention_rating INTEGER NULL,
                    summary_json TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS session_events (
                    event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    payload_json TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_session_events_session
                    ON session_events(session_id, event_id);
                """;
            await schema.ExecuteNonQueryAsync(cancellation);

            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO sessions(session_id, started_at, classroom_kind, planned_seconds)
                VALUES ($session, $started, $kind, $seconds);
                """;
            insert.Parameters.AddWithValue("$session", sessionId.ToString());
            insert.Parameters.AddWithValue("$started", startedAt.ToString("O"));
            insert.Parameters.AddWithValue("$kind", start.ClassroomKind.ToString());
            insert.Parameters.AddWithValue("$seconds", (long)start.PlannedDuration.TotalSeconds);
            await insert.ExecuteNonQueryAsync(cancellation);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask AppendAsync(SessionEvent sessionEvent, CancellationToken cancellation)
    {
        await _writeGate.WaitAsync(cancellation);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellation);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO session_events(session_id, occurred_at, event_type, payload_json)
                VALUES ($session, $at, $type, $payload);
                """;
            command.Parameters.AddWithValue("$session", sessionEvent.SessionId.ToString());
            command.Parameters.AddWithValue("$at", sessionEvent.At.ToString("O"));
            command.Parameters.AddWithValue("$type", sessionEvent.Type);
            command.Parameters.AddWithValue("$payload", Serialize(sessionEvent.Data));
            await command.ExecuteNonQueryAsync(cancellation);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask CompleteAsync(SessionSummary summary, CancellationToken cancellation)
    {
        await _writeGate.WaitAsync(cancellation);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellation);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE sessions
                SET completed_at = $completed,
                    attention_rating = $rating,
                    summary_json = $summary
                WHERE session_id = $session;
                """;
            command.Parameters.AddWithValue("$completed", summary.CompletedAt.ToString("O"));
            command.Parameters.AddWithValue("$rating", (object?)summary.AttentionRating ?? DBNull.Value);
            command.Parameters.AddWithValue("$summary", JsonSerializer.Serialize(summary));
            command.Parameters.AddWithValue("$session", summary.SessionId.ToString());
            await command.ExecuteNonQueryAsync(cancellation);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string Serialize(object? value) => value is null
        ? "{}"
        : JsonSerializer.Serialize(value, value.GetType());
}

public static class SessionCsvExporter
{
    public static async Task ExportAsync(
        string databasePath,
        string destinationPath,
        CancellationToken cancellation = default)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.session_id, e.event_id, e.occurred_at, e.event_type, e.payload_json
            FROM session_events e
            ORDER BY e.session_id, e.event_id;
            """;

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var writer = new StreamWriter(destinationPath, false, new UTF8Encoding(true));
        await writer.WriteLineAsync("session_id,event_id,occurred_at,event_type,payload_json");
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        while (await reader.ReadAsync(cancellation))
        {
            var row = string.Join(',', Enumerable.Range(0, 5).Select(index => Csv(reader.GetValue(index)?.ToString() ?? string.Empty)));
            await writer.WriteLineAsync(row.AsMemory(), cancellation);
        }
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
