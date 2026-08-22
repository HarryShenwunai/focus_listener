using System.Threading.Channels;

namespace FocusListener.Tests;

internal sealed class ManualSessionClock(DateTimeOffset initial) : ISessionClock
{
    private readonly object _gate = new();
    private readonly List<DelayWaiter> _waiters = [];
    private DateTimeOffset _now = initial;

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    public Task Delay(TimeSpan delay, CancellationToken cancellation)
    {
        lock (_gate)
        {
            if (cancellation.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellation);
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiter = new DelayWaiter(_now + delay, completion);
            _waiters.Add(waiter);
            cancellation.Register(() => completion.TrySetCanceled(cancellation));
            return completion.Task;
        }
    }

    public void Advance(TimeSpan duration)
    {
        List<DelayWaiter> due;
        lock (_gate)
        {
            _now += duration;
            due = _waiters.Where(waiter => waiter.DueAt <= _now).ToList();
            _waiters.RemoveAll(waiter => waiter.DueAt <= _now);
        }

        foreach (var waiter in due)
        {
            waiter.Completion.TrySetResult();
        }
    }

    private sealed record DelayWaiter(DateTimeOffset DueAt, TaskCompletionSource Completion);
}

internal sealed class ControllableQuestionSource : IQuestionCandidateSource
{
    private readonly Channel<ResetQuestionCandidate> _automatic = Channel.CreateUnbounded<ResetQuestionCandidate>();

    public ResetQuestionCandidate? ManualCandidate { get; set; }

    public IAsyncEnumerable<ResetQuestionCandidate> AutomaticAsync(
        SessionStart start,
        CancellationToken cancellation) =>
        _automatic.Reader.ReadAllAsync(cancellation);

    public ValueTask<ResetQuestionCandidate?> RequestManualAsync(
        SessionStart start,
        CancellationToken cancellation) =>
        ValueTask.FromResult(ManualCandidate);

    public void Emit(ResetQuestionCandidate candidate) =>
        _automatic.Writer.TryWrite(candidate);
}

internal sealed class ViewProbe : IProgress<SessionView>
{
    private readonly object _gate = new();
    private readonly List<SessionView> _views = [];
    private readonly Channel<bool> _updates = Channel.CreateUnbounded<bool>();

    public void Report(SessionView value)
    {
        lock (_gate)
        {
            _views.Add(value);
        }

        _updates.Writer.TryWrite(true);
    }

    public async Task<SessionView> WaitForAsync(
        Func<SessionView, bool> predicate,
        CancellationToken cancellation)
    {
        while (true)
        {
            lock (_gate)
            {
                var match = _views.LastOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }
            }

            await _updates.Reader.ReadAsync(cancellation);
        }
    }
}

internal static class TestQuestion
{
    public static ResetQuestionCandidate Create(
        ManualSessionClock clock,
        string eligibleUnit,
        TriggerKind trigger = TriggerKind.Automatic,
        string correct = "A")
    {
        var question = new RestatementQuestion(
            QuestionId.New(),
            QuestionType.RelationshipRecognition,
            $"{eligibleUnit} 中，相遇问题描述的是哪种关系？",
            [
                new QuestionChoice(new ChoiceId("A"), "两者共同走完同一段路程"),
                new QuestionChoice(new ChoiceId("B"), "只关心一个人的速度"),
                new QuestionChoice(new ChoiceId("C"), "比较两个图形的面积")
            ]);

        return new ResetQuestionCandidate(
            eligibleUnit,
            clock.UtcNow,
            question,
            new ChoiceId(correct),
            new LessonEvidence("相遇问题里，两个人走过的路程合起来就是总路程。", TimeSpan.FromSeconds(12)),
            trigger);
    }
}
