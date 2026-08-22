namespace FocusListener.Tests;

public sealed class FocusInteractionSettingsTests
{
    [Fact]
    public void Defaults_match_confirmed_product_timing()
    {
        var settings = FocusInteractionSettings.Default;

        Assert.Equal(60, settings.WarmupSeconds);
        Assert.Equal(120, settings.AutoCooldownSeconds);
        Assert.Equal(180, settings.CandidateLifetimeSeconds);
        Assert.Equal(30, settings.ManualSafetySeconds);
        Assert.Equal(8, settings.InitialAnswerSeconds);
        Assert.Equal(20, settings.ExtendedAnswerSeconds);
        Assert.Equal(120, settings.PendingLifetimeSeconds);
        Assert.Equal(3, settings.FeedbackSeconds);
        Assert.True(settings.CandidateReadyAnimation);
        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void Cross_field_constraints_are_reported_in_user_language()
    {
        var settings = FocusInteractionSettings.Default with
        {
            AutoCooldownSeconds = 200,
            CandidateLifetimeSeconds = 210,
            InitialAnswerSeconds = 20,
            ExtendedAnswerSeconds = 20
        };

        var errors = settings.Validate();

        Assert.Contains(errors, error => error.Contains("至少要比自动提问间隔多 30 秒"));
        Assert.Contains(errors, error => error.Contains("必须大于初始答题时间"));
    }

    [Fact]
    public async Task Store_round_trips_valid_settings_and_falls_back_from_invalid_json()
    {
        var directory = Path.Combine(Path.GetTempPath(), "focus-listener-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new FocusInteractionSettingsStore(path);
            var expected = FocusInteractionSettings.Default with { WarmupSeconds = 90, CandidateReadyAnimation = false };
            await store.SaveAsync(expected);
            Assert.Equal(expected, store.Load());

            await File.WriteAllTextAsync(path, "{not-json");
            Assert.Equal(FocusInteractionSettings.Default, store.Load());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
