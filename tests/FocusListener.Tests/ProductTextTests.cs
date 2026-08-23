namespace FocusListener.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProductTextCollection
{
    public const string Name = "Product text";
}

[Collection(ProductTextCollection.Name)]
public sealed class ProductTextTests
{
    [Fact]
    public void English_language_localizes_core_audio_and_validation_text()
    {
        try
        {
            ProductText.Use(AppLanguage.English);

            Assert.Equal("Microphone only", AudioCaptureModeDisplay.Chinese(AudioCaptureMode.Microphone));
            var errors = (FocusInteractionSettings.Default with { SessionReminderMinutes = 12 }).Validate();
            Assert.Contains(errors, error => error.Contains("15, 30, 45, or 60", StringComparison.Ordinal));
        }
        finally
        {
            ProductText.Use(AppLanguage.ZhHans);
        }
    }

    [Theory]
    [InlineData(AppLanguage.ZhHans)]
    [InlineData(AppLanguage.English)]
    public void Explicit_language_values_resolve_without_environment_dependency(AppLanguage expected)
    {
        Assert.Equal(expected, ProductText.Resolve(expected));
    }
}
