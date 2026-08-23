using Microsoft.Data.Sqlite;

namespace FocusListener.Tests;

public sealed class SqliteDataRetentionTests
{
    [Fact]
    public async Task Purge_removes_only_sessions_and_events_older_than_retention_window()
    {
        var directory = Path.Combine(Path.GetTempPath(), "focus-listener-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "retention.db");
        try
        {
            var journal = new SqliteSessionJournalAdapter(database);
            var oldSession = SessionId.New();
            var recentSession = SessionId.New();
            await journal.InitializeAsync(oldSession, new SessionStart(ClassroomKind.InPerson),
                DateTimeOffset.UtcNow.AddDays(-45), CancellationToken.None);
            await journal.AppendAsync(new SessionEvent(oldSession, DateTimeOffset.UtcNow.AddDays(-45),
                "OldEvent", new { Result = "old" }), CancellationToken.None);
            await journal.InitializeAsync(recentSession, new SessionStart(ClassroomKind.InPerson),
                DateTimeOffset.UtcNow.AddDays(-2), CancellationToken.None);
            await journal.AppendAsync(new SessionEvent(recentSession, DateTimeOffset.UtcNow.AddDays(-2),
                "RecentEvent", new { Result = "recent" }), CancellationToken.None);

            var result = await SqliteDataRetention.PurgeExpiredAsync(database, 30);

            Assert.Equal(1, result.SessionsRemoved);
            Assert.Equal(1, result.EventsRemoved);
            await using var connection = new SqliteConnection($"Data Source={database}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT session_id FROM sessions;";
            Assert.Equal(recentSession.ToString(), Convert.ToString(await command.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
