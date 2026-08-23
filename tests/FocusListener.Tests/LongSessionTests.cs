namespace FocusListener.Tests;

public sealed class LongSessionTests
{
    [Fact]
    public async Task Ninety_minutes_never_auto_stops_a_session_without_a_reminder()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = new ManualSessionClock(DateTimeOffset.UnixEpoch);
        var views = new ViewProbe();
        IFocusSession session = new FocusSession(
            new ControllableQuestionSource(),
            new InMemorySessionJournalAdapter(),
            clock,
            SessionTiming.Default with { Warmup = TimeSpan.Zero });
        var completion = session.RunAsync(new SessionStart(ClassroomKind.InPerson), views, timeout.Token);
        await views.WaitForAsync(view => view.Surface == SessionSurfaceKind.Listening, timeout.Token);

        clock.Advance(TimeSpan.FromMinutes(90));
        var outcome = await session.ApplyAsync(new RequestManualTrigger(IntentId.New()), timeout.Token);

        Assert.False(completion.IsCompleted);
        Assert.NotEqual("NotRunning", outcome.Code);
        await session.ApplyAsync(new EndSession(IntentId.New()), timeout.Token);
        await session.ApplyAsync(new SkipAttentionRating(IntentId.New()), timeout.Token);
        await completion.WaitAsync(timeout.Token);
    }
}
