namespace FocusListener;

internal static class DiagnosticQuestionInput
{
    public static TranscriptUnit? Create(string? liveTranscript, DateTimeOffset recognizedAt)
    {
        if (string.IsNullOrWhiteSpace(liveTranscript))
        {
            return null;
        }

        return new TranscriptUnit(liveTranscript.Trim(), recognizedAt, TimeSpan.Zero);
    }
}
