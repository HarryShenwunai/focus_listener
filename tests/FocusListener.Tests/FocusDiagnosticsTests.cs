namespace FocusListener.Tests;

public sealed class FocusDiagnosticsTests
{
    [Fact]
    public async Task RunAsync_StreamsOrderedViewsAndReturnsInterfaceLevelSummary()
    {
        var question = new DiagnosticQuestionPreview(
            "相遇时间表示什么？",
            ["从出发到相遇的时间", "一共走过的路程", "两人的速度差"],
            "相遇时间是从同时出发到彼此相遇所经历的时间。");
        var signals = Enum.GetValues<FocusDiagnosticId>()
            .SelectMany(id => new[]
            {
                new FocusDiagnosticSignal(id, FocusDiagnosticState.Running, "检测中"),
                new FocusDiagnosticSignal(
                    id,
                    FocusDiagnosticState.Passed,
                    "正常",
                    id is FocusDiagnosticId.MicrophoneLevel or FocusDiagnosticId.SystemSoundLevel ? 0.72 : null,
                    id == FocusDiagnosticId.LiveTranscription ? "这是实时转写预览。" : null,
                    id == FocusDiagnosticId.QuestionGeneration ? question : null)
            })
            .ToArray();
        IFocusDiagnostics diagnostics = new FocusDiagnostics(new ScriptedDiagnosticRuntime(signals));
        var views = new List<FocusDiagnosticsView>();

        var summary = await diagnostics.RunAsync(new InlineTestProgress<FocusDiagnosticsView>(views.Add));

        Assert.True(summary.Succeeded);
        Assert.Equal(9, summary.Passed);
        Assert.Equal(0, summary.Warnings);
        Assert.Equal(0, summary.Failed);
        Assert.False(summary.FinalView.IsRunning);
        Assert.Equal("所有环节正常", summary.FinalView.Headline);
        Assert.Equal("这是实时转写预览。", summary.FinalView.TranscriptPreview);
        Assert.Equal(question, summary.FinalView.Question);
        Assert.Equal(Enum.GetValues<FocusDiagnosticId>(), summary.FinalView.Items.Select(item => item.Id));
        Assert.True(views.Zip(views.Skip(1), (first, second) => second.Revision > first.Revision).All(value => value));
    }

    private sealed class ScriptedDiagnosticRuntime(
        IReadOnlyList<FocusDiagnosticSignal> signals) : IFocusDiagnosticRuntime
    {
        public Task RunAsync(IProgress<FocusDiagnosticSignal> progress, CancellationToken cancellation)
        {
            foreach (var signal in signals)
            {
                cancellation.ThrowIfCancellationRequested();
                progress.Report(signal);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InlineTestProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
