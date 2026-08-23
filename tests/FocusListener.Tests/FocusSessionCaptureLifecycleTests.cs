using System.Runtime.CompilerServices;

namespace FocusListener.Tests;

public sealed class FocusSessionCaptureLifecycleTests
{
    [Fact]
    public async Task Ending_classroom_cancels_audio_and_transcription_before_attention_rating()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = new ManualSessionClock(new DateTimeOffset(2026, 8, 22, 14, 0, 0, TimeSpan.Zero));
        var source = new CaptureAwareSource();
        var views = new ViewProbe();
        IFocusSession session = new FocusSession(
            source,
            new InMemorySessionJournalAdapter(),
            clock,
            SessionTiming.Default);
        var completion = session.RunAsync(
            new SessionStart(ClassroomKind.InPerson, TimeSpan.FromMinutes(12)),
            views,
            timeout.Token);
        await views.WaitForAsync(view => view.Surface == SessionSurfaceKind.Listening, timeout.Token);
        await source.Started.Task.WaitAsync(timeout.Token);

        var endingTask = session.ApplyAsync(new EndSession(IntentId.New()), timeout.Token).AsTask();
        await source.CancellationRequested.Task.WaitAsync(timeout.Token);
        await Task.Delay(100, timeout.Token);
        Assert.False(endingTask.IsCompleted);
        source.AllowStop.SetResult();
        var ending = await endingTask;
        await source.CaptureStopped.Task.WaitAsync(timeout.Token);

        Assert.True(ending.Accepted);
        var ratingView = await views.WaitForAsync(
            view => view.Surface == SessionSurfaceKind.AttentionRating,
            timeout.Token);
        Assert.Equal(SessionSurfaceKind.AttentionRating, ratingView.Surface);

        await session.ApplyAsync(new SkipAttentionRating(IntentId.New()), timeout.Token);
        await completion.WaitAsync(timeout.Token);
    }

    private sealed class CaptureAwareSource : IQuestionCandidateSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CaptureStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ResetQuestionCandidate> AutomaticAsync(
            SessionStart start,
            [EnumeratorCancellation] CancellationToken cancellation)
        {
            using var registration = cancellation.Register(CancellationRequested.SetResult);
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            await AllowStop.Task;
            CaptureStopped.SetResult();
            yield break;
        }

        public ValueTask<ResetQuestionCandidate?> RequestManualAsync(
            SessionStart start,
            CancellationToken cancellation) =>
            ValueTask.FromResult<ResetQuestionCandidate?>(null);
    }
}
