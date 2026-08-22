using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FocusListener;

internal sealed class SqliteSessionJournalAdapter : ISessionJournal
{
    private static readonly (string Name, string SqlType)[] AnalyticsColumns =
    [
        ("subject", "TEXT NULL"),
        ("knowledge_type", "TEXT NULL"),
        ("quality_score", "REAL NULL"),
        ("priority_score", "REAL NULL"),
        ("trigger_kind", "TEXT NULL"),
        ("answer_correct", "INTEGER NULL"),
        ("answer_ms", "INTEGER NULL"),
        ("generation_failure_reason", "TEXT NULL")
    ];

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
                    payload_json TEXT NOT NULL,
                    subject TEXT NULL,
                    knowledge_type TEXT NULL,
                    quality_score REAL NULL,
                    priority_score REAL NULL,
                    trigger_kind TEXT NULL,
                    answer_correct INTEGER NULL,
                    answer_ms INTEGER NULL,
                    generation_failure_reason TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_session_events_session
                    ON session_events(session_id, event_id);
                """;
            await schema.ExecuteNonQueryAsync(cancellation);
            await EnsureAnalyticsColumnsAsync(connection, cancellation);

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
            var payload = Serialize(sessionEvent.Data);
            var analytics = EventAnalytics.Read(payload);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO session_events(
                    session_id, occurred_at, event_type, payload_json,
                    subject, knowledge_type, quality_score, priority_score, trigger_kind,
                    answer_correct, answer_ms, generation_failure_reason)
                VALUES (
                    $session, $at, $type, $payload,
                    $subject, $knowledge, $quality, $priority, $trigger,
                    $correct, $answerMs, $failure);
                """;
            command.Parameters.AddWithValue("$session", sessionEvent.SessionId.ToString());
            command.Parameters.AddWithValue("$at", sessionEvent.At.ToString("O"));
            command.Parameters.AddWithValue("$type", sessionEvent.Type);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$subject", Db(analytics.Subject));
            command.Parameters.AddWithValue("$knowledge", Db(analytics.KnowledgeType));
            command.Parameters.AddWithValue("$quality", Db(analytics.QualityScore));
            command.Parameters.AddWithValue("$priority", Db(analytics.PriorityScore));
            command.Parameters.AddWithValue("$trigger", Db(analytics.Trigger));
            command.Parameters.AddWithValue("$correct", Db(analytics.AnswerCorrect is null ? null : analytics.AnswerCorrect.Value ? 1 : 0));
            command.Parameters.AddWithValue("$answerMs", Db(analytics.AnswerMilliseconds));
            command.Parameters.AddWithValue("$failure", Db(analytics.GenerationFailureReason));
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

    private static async Task EnsureAnalyticsColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellation)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(session_events);";
            await using var reader = await info.ExecuteReaderAsync(cancellation);
            while (await reader.ReadAsync(cancellation))
            {
                existing.Add(reader.GetString(1));
            }
        }

        foreach (var column in AnalyticsColumns)
        {
            if (existing.Contains(column.Name))
            {
                continue;
            }

            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE session_events ADD COLUMN {column.Name} {column.SqlType};";
            await alter.ExecuteNonQueryAsync(cancellation);
        }
    }

    private static object Db(object? value) => value ?? DBNull.Value;

    private static string Serialize(object? value) => value is null
        ? "{}"
        : JsonSerializer.Serialize(value, value.GetType());

    private sealed record EventAnalytics(
        string? Subject,
        string? KnowledgeType,
        double? QualityScore,
        double? PriorityScore,
        string? Trigger,
        bool? AnswerCorrect,
        long? AnswerMilliseconds,
        string? GenerationFailureReason)
    {
        public static EventAnalytics Read(string payload)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            return new EventAnalytics(
                Text(root, "Subject"),
                Text(root, "KnowledgeType"),
                Number(root, "QualityScore"),
                Number(root, "PriorityScore"),
                Text(root, "Trigger"),
                Boolean(root, "IsCorrect"),
                Integer(root, "ElapsedMilliseconds"),
                Text(root, "GenerationFailureReason") ?? FailureFromCode(root));
        }

        private static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static double? Number(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number)
                ? number
                : null;

        private static long? Integer(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number)
                ? number
                : null;

        private static bool? Boolean(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;

        private static string? FailureFromCode(JsonElement root)
        {
            var code = Text(root, "Code");
            return code is not null && code.StartsWith("CandidateRejected:", StringComparison.Ordinal)
                ? code["CandidateRejected:".Length..]
                : null;
        }
    }
}

public static class SessionCsvExporter
{
    private static readonly string[] Columns =
    [
        "session_id", "event_id", "occurred_at", "event_type", "payload_json",
        "subject", "knowledge_type", "quality_score", "priority_score", "trigger_kind",
        "answer_correct", "answer_ms", "generation_failure_reason"
    ];

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
        command.CommandText = $"""
            SELECT {string.Join(", ", Columns.Select(column => "e." + column))}
            FROM session_events e
            ORDER BY e.session_id, e.event_id;
            """;

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var writer = new StreamWriter(destinationPath, false, new UTF8Encoding(true));
        await writer.WriteLineAsync(string.Join(',', Columns));
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        while (await reader.ReadAsync(cancellation))
        {
            var row = string.Join(',', Enumerable.Range(0, Columns.Length).Select(index =>
                Csv(reader.IsDBNull(index)
                    ? string.Empty
                    : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty)));
            await writer.WriteLineAsync(row.AsMemory(), cancellation);
        }
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
