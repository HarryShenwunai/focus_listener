namespace FocusListener.Tests;

public sealed class TranscriptAwareFocusDiagnosticsTests
{
    [Fact]
    public async Task RunAsync_replaces_the_fixed_probe_question_with_one_from_the_live_transcript()
    {
        const string transcript =
            "Photosynthesis is the process green plants use to turn sunlight into food.";
        var fixedQuestion = new DiagnosticQuestionPreview(
            "相遇时间是指什么？",
            ["A 彼此相遇", "B 停止运动", "C 到达终点"],
            "相遇时间是两个运动物体同时出发后到彼此相遇所经历的时间。");
        var baseView = BuildView(transcript, fixedQuestion);
        var replacement = new DiagnosticQuestionPreview(
            "What do green plants use to make food?",
            ["A Sunlight", "B Sound", "C Sand"],
            "green plants use to turn sunlight into food");
        var generator = new RecordingQuestionGenerator(replacement);
        IFocusDiagnostics diagnostics = new TranscriptAwareFocusDiagnostics(
            new StubDiagnostics(baseView),
            generator);
        var views = new List<FocusDiagnosticsView>();

        var result = await diagnostics.RunAsync(new InlineTestProgress<FocusDiagnosticsView>(views.Add));

        Assert.Equal(transcript, generator.Input?.Text);
        Assert.DoesNotContain(views, view => view.Question?.Evidence.Contains("相遇时间") == true);
        Assert.Equal(replacement, result.FinalView.Question);
        Assert.Equal(FocusDiagnosticState.Passed, result.FinalView.Items.Single(
            item => item.Id == FocusDiagnosticId.QuestionGeneration).State);
    }

    [Fact]
    public async Task RunAsync_does_not_show_an_unrelated_question_when_transcription_is_empty()
    {
        var baseView = BuildView(string.Empty, new DiagnosticQuestionPreview(
            "相遇时间是指什么？",
            ["A 彼此相遇", "B 停止运动", "C 到达终点"],
            "相遇时间是两个运动物体同时出发后到彼此相遇所经历的时间。"));
        var generator = new RecordingQuestionGenerator(null);
        IFocusDiagnostics diagnostics = new TranscriptAwareFocusDiagnostics(
            new StubDiagnostics(baseView),
            generator);

        var result = await diagnostics.RunAsync(
            new InlineTestProgress<FocusDiagnosticsView>(_ => { }));

        Assert.Null(generator.Input);
        Assert.Null(result.FinalView.Question);
        Assert.Equal(FocusDiagnosticState.Warning, result.FinalView.Items.Single(
            item => item.Id == FocusDiagnosticId.QuestionGeneration).State);
    }

    private static FocusDiagnosticsView BuildView(
        string transcript,
        DiagnosticQuestionPreview fixedQuestion)
    {
        var now = DateTimeOffset.UnixEpoch;
        var items = Enum.GetValues<FocusDiagnosticId>()
            .Select(id => new FocusDiagnosticItem(
                id,
                id.ToString(),
                FocusDiagnosticState.Passed,
                "正常",
                null,
                id == FocusDiagnosticId.LiveTranscription ? transcript : null,
                now))
            .ToArray();
        return new FocusDiagnosticsView(10, false, "所有环节正常", items, transcript, fixedQuestion);
    }

    private sealed class StubDiagnostics(FocusDiagnosticsView view) : IFocusDiagnostics
    {
        public Task<FocusDiagnosticsSummary> RunAsync(
            IProgress<FocusDiagnosticsView> views,
            CancellationToken cancellation = default)
        {
            views.Report(view);
            return Task.FromResult(new FocusDiagnosticsSummary(
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(1),
                view.Items.Count,
                0,
                0,
                view));
        }
    }

    private sealed class RecordingQuestionGenerator(DiagnosticQuestionPreview? result)
        : IDiagnosticQuestionGenerator
    {
        public TranscriptUnit? Input { get; private set; }

        public ValueTask<DiagnosticQuestionPreview?> TryGenerateAsync(
            TranscriptUnit input,
            CancellationToken cancellation)
        {
            Input = input;
            return ValueTask.FromResult(result);
        }
    }
}
