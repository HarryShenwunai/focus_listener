using Microsoft.Data.Sqlite;

namespace FocusListener.Tests;

public sealed class SqliteJournalMigrationTests
{
    [Fact]
    public async Task Existing_database_gets_nullable_analytics_columns_without_losing_old_rows()
    {
        var directory = Path.Combine(Path.GetTempPath(), "focus-listener-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "legacy.db");
        try
        {
            await CreateLegacyDatabaseAsync(database);
            var journal = new SqliteSessionJournalAdapter(database);
            var session = SessionId.New();
            await journal.InitializeAsync(
                session,
                new SessionStart(ClassroomKind.InPerson, TimeSpan.FromMinutes(12)),
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            await journal.AppendAsync(new SessionEvent(session, DateTimeOffset.UtcNow, "QuestionShown", new
            {
                Subject = "科学",
                KnowledgeType = "Causality",
                QualityScore = 0.9,
                PriorityScore = 0.8,
                Trigger = "Automatic"
            }), CancellationToken.None);

            await using var connection = new SqliteConnection($"Data Source={database}");
            await connection.OpenAsync();
            await using var columns = connection.CreateCommand();
            columns.CommandText = "PRAGMA table_info(session_events);";
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await columns.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    names.Add(reader.GetString(1));
                }
            }

            Assert.Contains("subject", names);
            Assert.Contains("answer_ms", names);
            Assert.Contains("generation_failure_reason", names);
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM session_events;";
            Assert.Equal(2L, Convert.ToInt64(await count.ExecuteScalarAsync()));
            await connection.CloseAsync();
            await connection.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task CreateLegacyDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE sessions (
                session_id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL,
                completed_at TEXT NULL,
                classroom_kind TEXT NOT NULL,
                planned_seconds INTEGER NOT NULL,
                attention_rating INTEGER NULL,
                summary_json TEXT NULL
            );
            CREATE TABLE session_events (
                event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                event_type TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            INSERT INTO session_events(session_id, occurred_at, event_type, payload_json)
            VALUES ('legacy', '2026-08-22T00:00:00Z', 'LegacyEvent', '{}');
            """;
        await command.ExecuteNonQueryAsync();
    }
}
