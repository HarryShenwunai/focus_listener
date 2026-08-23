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
        Assert.Equal(AppLanguage.System, settings.AppLanguage);
        Assert.Null(settings.SessionReminderMinutes);
        Assert.Equal(30, settings.RetentionDays);
        Assert.False(settings.OnboardingCompleted);
        Assert.False(settings.UsageNoticeAccepted);
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
    public void Subtitle_shortcuts_cannot_replace_the_manual_question_hotkey()
    {
        var settings = FocusInteractionSettings.Default with
        {
            SubtitleToggleKey = " q "
        };

        Assert.Contains(settings.Validate(), error => error.Contains("Ctrl + Shift + Q"));
    }

    [Fact]
    public void Only_supported_session_reminders_are_valid()
    {
        Assert.Empty((FocusInteractionSettings.Default with { SessionReminderMinutes = 45 }).Validate());
        Assert.Contains(
            (FocusInteractionSettings.Default with { SessionReminderMinutes = 12 }).Validate(),
            error => error.Contains("15、30、45 或 60"));
    }

    [Fact]
    public async Task Concurrent_setting_changes_are_serialized_into_a_valid_atomic_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "focus-listener-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new FocusInteractionSettingsStore(path);
            var writes = Enumerable.Range(60, 20)
                .Select(seconds => store.SaveAsync(FocusInteractionSettings.Default with
                {
                    WarmupSeconds = seconds
                }))
                .ToArray();

            await Task.WhenAll(writes);

            Assert.InRange(store.Load().WarmupSeconds, 60, 79);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
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
