namespace FocusListener.Tests;

public sealed class SessionReminderTests
{
    [Fact]
    public async Task Reminder_is_shown_once_and_never_ends_capture()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = new ManualSessionClock(DateTimeOffset.UnixEpoch);
        var views = new ViewProbe();
        IFocusSession session = new FocusSession(
            new ControllableQuestionSource(),
            new InMemorySessionJournalAdapter(),
            clock,
            SessionTiming.Default with { Warmup = TimeSpan.Zero });
        var completion = session.RunAsync(
            new SessionStart(ClassroomKind.InPerson, TimeSpan.FromMinutes(15)),
            views,
            timeout.Token);
        await views.WaitForAsync(view => view.Surface == SessionSurfaceKind.Listening, timeout.Token);

        clock.Advance(TimeSpan.FromMinutes(15));
        var reminder = await views.WaitForAsync(
            view => view.Notice?.Contains("15 分钟", StringComparison.Ordinal) == true,
            timeout.Token);

        Assert.Equal(SessionSurfaceKind.Listening, reminder.Surface);
        Assert.False(completion.IsCompleted);
        await session.ApplyAsync(new EndSession(IntentId.New()), timeout.Token);
        await session.ApplyAsync(new SkipAttentionRating(IntentId.New()), timeout.Token);
        await completion.WaitAsync(timeout.Token);
    }
}
