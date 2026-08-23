namespace FocusListener;

public sealed record GeminiFocusOptions(string ApiKey)
{
    public string LiveModel { get; init; } = "gemini-3.1-flash-live-preview";
    public string QuestionModel { get; init; } = "gemini-3.5-flash-lite";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new ArgumentException("Gemini API key cannot be empty.", nameof(ApiKey));
        }

        if (string.IsNullOrWhiteSpace(LiveModel) || string.IsNullOrWhiteSpace(QuestionModel))
        {
            throw new ArgumentException("Gemini model names cannot be empty.");
        }
    }
}
