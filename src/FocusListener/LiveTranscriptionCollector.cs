using System.Text;

namespace FocusListener;

internal sealed record LiveTranscriptionEvent(
    string? InterimText,
    string? FinalText,
    bool FinalFinished,
    bool ModelTurnComplete);

internal sealed class LiveTranscriptionCollector
{
    private readonly StringBuilder _text = new();

    public string Text => _text.ToString().Trim();
    public bool IsFinished { get; private set; }

    public bool Apply(LiveTranscriptionEvent update)
    {
        var before = Text;
        var candidate = !string.IsNullOrWhiteSpace(update.FinalText)
            ? update.FinalText
            : update.InterimText;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            Merge(candidate);
        }

        if (update.FinalFinished)
        {
            IsFinished = true;
        }

        return !string.Equals(before, Text, StringComparison.Ordinal);
    }

    private void Merge(string update)
    {
        var normalized = update.Trim();
        var current = _text.ToString();
        if (normalized.StartsWith(current, StringComparison.Ordinal))
        {
            _text.Clear();
            _text.Append(normalized);
            return;
        }

        if (current.StartsWith(normalized, StringComparison.Ordinal) ||
            current.EndsWith(normalized, StringComparison.Ordinal))
        {
            return;
        }

        if (_text.Length > 0 && !char.IsPunctuation(_text[^1]))
        {
            _text.Append(' ');
        }

        _text.Append(normalized);
    }
}
