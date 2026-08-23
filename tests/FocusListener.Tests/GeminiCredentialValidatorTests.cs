namespace FocusListener.Tests;

public sealed class GeminiCredentialValidatorTests
{
    private static readonly GeminiFocusOptions Options = new("test-key-value");

    [Fact]
    public async Task Valid_probe_is_accepted()
    {
        var result = await GeminiCredentialValidator.ValidateAsync(Options, new StubProbe(true));

        Assert.Equal(GeminiCredentialState.Valid, result.State);
    }

    [Fact]
    public async Task Unauthorized_and_network_failures_have_distinct_recovery_states()
    {
        var unauthorized = await GeminiCredentialValidator.ValidateAsync(
            Options,
            new StubProbe(new InvalidOperationException("HTTP 401 API key rejected")));
        var network = await GeminiCredentialValidator.ValidateAsync(
            Options,
            new StubProbe(new HttpRequestException("offline")));

        Assert.Equal(GeminiCredentialState.InvalidOrUnauthorized, unauthorized.State);
        Assert.Equal(GeminiCredentialState.NetworkUnavailable, network.State);
    }

    private sealed class StubProbe : IGeminiCredentialProbe
    {
        private readonly bool _result;
        private readonly Exception? _exception;

        public StubProbe(bool result) => _result = result;
        public StubProbe(Exception exception) => _exception = exception;

        public ValueTask<bool> ProbeAsync(GeminiFocusOptions options, CancellationToken cancellation) =>
            _exception is null
                ? ValueTask.FromResult(_result)
                : ValueTask.FromException<bool>(_exception);
    }
}
