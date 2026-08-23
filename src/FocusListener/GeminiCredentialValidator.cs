using Google.GenAI;
using Google.GenAI.Types;

namespace FocusListener;

public enum GeminiCredentialState
{
    Valid,
    InvalidOrUnauthorized,
    RateLimitedOrUnavailable,
    NetworkUnavailable
}

public sealed record GeminiCredentialValidation(GeminiCredentialState State, string Model)
{
    public bool IsValid => State == GeminiCredentialState.Valid;
}

internal interface IGeminiCredentialProbe
{
    ValueTask<bool> ProbeAsync(GeminiFocusOptions options, CancellationToken cancellation);
}

internal sealed class GoogleGeminiCredentialProbe : IGeminiCredentialProbe
{
    public async ValueTask<bool> ProbeAsync(GeminiFocusOptions options, CancellationToken cancellation)
    {
        using var client = new Client(apiKey: options.ApiKey);
        var response = await client.Models.GenerateContentAsync(
            options.QuestionModel,
            "Reply only with OK",
            new GenerateContentConfig { Temperature = 0, MaxOutputTokens = 8 },
            cancellation);
        return !string.IsNullOrWhiteSpace(response.Text);
    }
}

public static class GeminiCredentialValidator
{
    public static Task<GeminiCredentialValidation> ValidateAsync(
        GeminiFocusOptions options,
        CancellationToken cancellation = default) =>
        ValidateAsync(options, new GoogleGeminiCredentialProbe(), cancellation);

    internal static async Task<GeminiCredentialValidation> ValidateAsync(
        GeminiFocusOptions options,
        IGeminiCredentialProbe probe,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(probe);
        options.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var valid = await probe.ProbeAsync(options, timeout.Token);
            return new GeminiCredentialValidation(
                valid ? GeminiCredentialState.Valid : GeminiCredentialState.RateLimitedOrUnavailable,
                options.QuestionModel);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new GeminiCredentialValidation(GeminiCredentialState.NetworkUnavailable, options.QuestionModel);
        }
        catch (HttpRequestException)
        {
            return new GeminiCredentialValidation(GeminiCredentialState.NetworkUnavailable, options.QuestionModel);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var text = exception.Message;
            var state = text.Contains("401", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("403", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("API key", StringComparison.OrdinalIgnoreCase)
                ? GeminiCredentialState.InvalidOrUnauthorized
                : GeminiCredentialState.RateLimitedOrUnavailable;
            return new GeminiCredentialValidation(state, options.QuestionModel);
        }
    }
}
