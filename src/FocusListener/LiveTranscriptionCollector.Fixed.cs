namespace FocusListener;

internal sealed record LiveTranscriptionEvent(
    string? InterimText,
    string? FinalText,
    bool FinalFinished,
    bool ModelTurnComplete);

internal sealed class LiveTranscriptionCollector
{
    private string _committed = string.Empty;
    private string _interim = string.Empty;

    public string Text => Combine(_committed, _interim);
    public bool IsFinished { get; private set; }

    public bool Apply(LiveTranscriptionEvent update)
    {
        var before = Text;
        if (!string.IsNullOrWhiteSpace(update.FinalText))
        {
            MergeFinal(update.FinalText.Trim());
            _interim = string.Empty;
        }
        else if (!string.IsNullOrWhiteSpace(update.InterimText))
        {
            _interim = update.InterimText.Trim();
        }

        if (update.FinalFinished)
        {
            IsFinished = true;
        }

        return !string.Equals(before, Text, StringComparison.Ordinal);
    }

    private void MergeFinal(string update)
    {
        if (_committed.Length == 0 || update.StartsWith(_committed, StringComparison.Ordinal))
        {
            _committed = update;
            return;
        }

        if (_committed.EndsWith(update, StringComparison.Ordinal))
        {
            return;
        }

        _committed = Combine(_committed, update);
    }

    private static string Combine(string first, string second)
    {
        var left = first.Trim();
        var right = second.Trim();
        if (left.Length == 0)
        {
            return right;
        }

        if (right.Length == 0 || left.EndsWith(right, StringComparison.Ordinal))
        {
            return left;
        }

        if (right.StartsWith(left, StringComparison.Ordinal))
        {
            return right;
        }

        return char.IsPunctuation(left[^1]) ? left + right : left + " " + right;
    }
}
