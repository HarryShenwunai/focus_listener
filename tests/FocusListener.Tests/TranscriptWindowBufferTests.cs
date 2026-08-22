namespace FocusListener.Tests;

public sealed class TranscriptWindowBufferTests
{
    [Fact]
    public void Keeps_only_the_last_thirty_seconds_and_caps_context_length()
    {
        var epoch = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var buffer = new TranscriptWindowBuffer(TimeSpan.FromSeconds(30), 40);
        buffer.Add(new TranscriptUnit("too old", epoch, TimeSpan.Zero));
        buffer.Add(new TranscriptUnit("first current sentence", epoch.AddSeconds(31), TimeSpan.FromSeconds(31)));
        var latest = new TranscriptUnit(
            "second current sentence with a complete relationship",
            epoch.AddSeconds(45),
            TimeSpan.FromSeconds(45));
        buffer.Add(latest);

        var window = buffer.Build(latest);

        Assert.DoesNotContain("too old", window.Text);
        Assert.True(window.Text.Length <= 40);
        Assert.EndsWith("complete relationship", window.Text);
        Assert.Equal(TimeSpan.FromSeconds(31), window.RelativeStart);
    }
}
