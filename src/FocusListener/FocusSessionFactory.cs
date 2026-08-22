namespace FocusListener;

public static class FocusSessionFactory
{
    public static IFocusSession CreateSimulation(string databasePath)
    {
        var clock = new SystemSessionClock();
        return new FocusSession(
            new GenericScriptedCandidateAdapter(clock),
            new SqliteSessionJournalAdapter(databasePath),
            clock);
    }

    public static Task ExportCsvAsync(
        string databasePath,
        string destinationPath,
        CancellationToken cancellation = default) =>
        SessionCsvExporter.ExportAsync(databasePath, destinationPath, cancellation);
}
