namespace FocusListener.Tests;

public sealed class FocusSessionBehaviorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 8, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly SessionTiming Timing = new(
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromMilliseconds(100));

    [Fact]
    public async Task Automatic_answer_is_idempotent_and_recorded_in_summary()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var setup = await StartAsync(timeout.Token);
        var candidate = TestQuestion.Create(setup.Clock, "unit-1");
        setup.Source.Emit(candidate);

        var question = await setup.Views.WaitForAsync(
            view => view.Surface == SessionSurfaceKind.Question,
            timeout.Token);
        var intent = new SelectAnswer(IntentId.New(), question.Question!.Id, new ChoiceId("A"));

        var first = await setup.Session.ApplyAsync(intent, timeout.Token);
        var repeated = await setup.Session.ApplyAsync(intent, timeout.Token);
        var feedback = await setup.Views.WaitForAsync(
            view => view.Surface == SessionSurfaceKind.Feedback,
            timeout.Token);

        Assert.True(first.Accepted);
        Assert.Equal(first, repeated);
        Assert.True(feedback.Feedback!.IsCorrect);
        Assert.Contains("总路程", feedback.Feedback.Evidence.Excerpt);

        var summary = await FinishAsync(setup, rating: 4, timeout.Token);
        Assert.Equal(1, summary.QuestionsShown);
        Assert.Equal(1, summary.Answers);
        Assert.Equal(1, summary.CorrectAnswers);
        Assert.Equal((byte)4, summary.AttentionRating);
    }

    [Fact]
    public async Task Manual_trigger_uses_the_latest_eligible_unit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var setup = await StartAsync(timeout.Token);
        setup.Source.ManualCandidate = TestQuestion.Create(setup.Clock, "manual-unit", TriggerKind.Manual);

        var outcome = await setup.Session.ApplyAsync(new RequestManualTrigger(IntentId.New()), timeout.Token);
        var question = await setup.Views.WaitForAsync(
            view => view.Surface == SessionSurfaceKind.Question,
            timeout.Token);

        Assert.True(outcome.Accepted);
        Assert.Equal(TriggerKind.Manual, question.Question!.Trigger);
        Assert.Contains("manual-unit", question.Question.Stem);

        await FinishAsync(setup, rating: null, timeout.Token);
    }

    [Fact]
    public async Task Eight_second_timeout_folds_to_pending_and_can_be_reopened()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var setup = await StartAsync(timeout.Token);
        setup.Source.Emit(TestQuestion.Create(setup.Clock, "pending-unit"));
        var active = await setup.Views.WaitForAsync(
            view => view.Surface == SessionSurfaceKind.Question,
            timeout.Token);

        var extension = await setup.Session.ApplyAsync(
            new ExtendThinking(IntentId.New(), active.Question!.Id),
            timeout.Token);
        var extended = await setup.Views.WaitForAsync(
            view => view.Surface == SessionSurfaceKind.Question && !view.CanExtend,
            timeout.Token);
        var duplicateExtension = await setup.Session.ApplyAsync(
            new ExtendThinking(IntentId.New(), active.Question.Id),
            timeout.Token);

        Assert.True(extension.Accepted);
        Assert.Equal(TimeSpan.FromSeconds(12), extended.Deadline - active.Deadline);
        Assert.Equal("AlreadyExtended", duplicateExtension.Code);

        setup.Clock.Advance(TimeSpan.FromSeconds(20));
        var pending = await setup.Views.WaitForAsync(
            view => view.Surface == SessionSurfaceKind.PendingBadge,
            timeout.Token);
        Assert.NotNull(pending.PendingExpiresAt);

        var opened = await setup.Session.ApplyAsync(
            new OpenPending(IntentId.New(), pending.Question!.Id),
            timeout.Token);
        var reopened = await setup.Views.WaitForAsync(
            view => view.Surface == SessionSurfaceKind.Question && view.PendingExpiresAt is not null,
            timeout.Token);

        Assert.True(opened.Accepted);
        Assert.Equal(pending.PendingExpiresAt, reopened.PendingExpiresAt);
        await FinishAsync(setup, rating: 3, timeout.Token);
    }

    [Fact]
    public async Task Capacity_is_one_current_plus_one_queued_and_invalid_promotes_queue()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var setup = await StartAsync(timeout.Token);
        var first = TestQuestion.Create(setup.Clock, "first-unit");
        var second = TestQuestion.Create(setup.Clock, "second-unit");
        var third = TestQuestion.Create(setup.Clock, "third-unit");
        setup.Source.Emit(first);

        var firstView = await setup.Views.WaitForAsync(
            view => view.Question?.Id == first.Question.Id,
            timeout.Token);
        setup.Source.Emit(second);
        setup.Source.Emit(third);

        await setup.Views.WaitForAsync(
            view => view.Notice?.Contains("容量") == true,
            timeout.Token);
        var report = await setup.Session.ApplyAsync(
            new ReportQuestionIssue(IntentId.New(), firstView.Question!.Id),
            timeout.Token);
        var promoted = await setup.Views.WaitForAsync(
            view => view.Question?.Id == second.Question.Id && view.Surface == SessionSurfaceKind.Question,
            timeout.Token);

        Assert.True(report.Accepted);
        Assert.Equal(second.Question.Id, promoted.Question!.Id);

        var summary = await FinishAsync(setup, rating: 5, timeout.Token);
        Assert.Equal(2, summary.QuestionsShown);
        Assert.Equal(1, summary.QuestionsQueued);
        Assert.Equal(1, summary.CapacityDrops);
        Assert.Equal(1, summary.InvalidQuestions);
    }

    private static async Task<RunningSession> StartAsync(CancellationToken cancellation)
    {
        var clock = new ManualSessionClock(Epoch);
        var source = new ControllableQuestionSource();
        var views = new ViewProbe();
        IFocusSession session = new FocusSession(source, new InMemorySessionJournalAdapter(), clock, Timing);
        var completion = session.RunAsync(
            new SessionStart(ClassroomKind.InPerson, TimeSpan.FromMinutes(12)),
            views,
            cancellation);
        await views.WaitForAsync(view => view.Surface == SessionSurfaceKind.Listening, cancellation);
        return new RunningSession(session, source, clock, views, completion);
    }

    private static async Task<SessionSummary> FinishAsync(
        RunningSession setup,
        byte? rating,
        CancellationToken cancellation)
    {
        var end = await setup.Session.ApplyAsync(new EndSession(IntentId.New()), cancellation);
        Assert.True(end.Accepted);
        var finish = rating is { } value
            ? await setup.Session.ApplyAsync(new RateAttentionReset(IntentId.New(), value), cancellation)
            : await setup.Session.ApplyAsync(new SkipAttentionRating(IntentId.New()), cancellation);
        Assert.True(finish.Accepted);
        return await setup.Completion.WaitAsync(cancellation);
    }

    private sealed record RunningSession(
        IFocusSession Session,
        ControllableQuestionSource Source,
        ManualSessionClock Clock,
        ViewProbe Views,
        Task<SessionSummary> Completion);
}
