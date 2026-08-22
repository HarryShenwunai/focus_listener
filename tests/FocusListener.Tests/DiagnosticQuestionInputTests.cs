namespace FocusListener.Tests;

public sealed class DiagnosticQuestionInputTests
{
    [Fact]
    public void Create_uses_the_current_live_transcript_as_the_question_source()
    {
        const string spoken =
            "Photosynthesis is the process green plants use to turn sunlight into food.";
        var recognizedAt = new DateTimeOffset(2026, 8, 22, 1, 0, 0, TimeSpan.Zero);

        var input = DiagnosticQuestionInput.Create(spoken, recognizedAt);

        Assert.NotNull(input);
        Assert.Equal(spoken, input.Text);
        Assert.Equal(recognizedAt, input.RecognizedAt);
        Assert.DoesNotContain("相遇时间", input.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_skips_question_generation_when_live_transcription_is_empty(string? spoken)
    {
        var input = DiagnosticQuestionInput.Create(spoken, DateTimeOffset.UnixEpoch);

        Assert.Null(input);
    }
}
