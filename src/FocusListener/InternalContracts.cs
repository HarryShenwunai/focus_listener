namespace FocusListener;

internal sealed record ResetQuestionCandidate(
    string EligibleUnitId,
    DateTimeOffset RecognizedAt,
    RestatementQuestion Question,
    ChoiceId CorrectChoice,
    LessonEvidence Evidence,
    TriggerKind Trigger)
{
    public string Subject { get; init; } = "其他";
    public string Language { get; init; } = "und";
    public string KnowledgeFingerprint { get; init; } = EligibleUnitId;
    public double QualityScore { get; init; } = 0.5;
}

internal interface IQuestionCandidateSource
{
    IAsyncEnumerable<ResetQuestionCandidate> AutomaticAsync(
        SessionStart start,
        CancellationToken cancellation);

    ValueTask<ResetQuestionCandidate?> RequestManualAsync(
        SessionStart start,
        CancellationToken cancellation);
}

internal sealed record QuestionSourceStatus(
    SessionHealth Health,
    string Notice,
    string Code);

internal interface IQuestionCandidateSourceStatus
{
    IAsyncEnumerable<QuestionSourceStatus> StatusAsync(CancellationToken cancellation);
}

internal interface ISessionClock
{
    DateTimeOffset UtcNow { get; }
    Task Delay(TimeSpan delay, CancellationToken cancellation);
}

internal sealed class SystemSessionClock : ISessionClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task Delay(TimeSpan delay, CancellationToken cancellation) => Task.Delay(delay, cancellation);
}

internal sealed record SessionEvent(
    SessionId SessionId,
    DateTimeOffset At,
    string Type,
    object? Data = null);

internal interface ISessionJournal
{
    ValueTask InitializeAsync(
        SessionId sessionId,
        SessionStart start,
        DateTimeOffset startedAt,
        CancellationToken cancellation);

    ValueTask AppendAsync(SessionEvent sessionEvent, CancellationToken cancellation);

    ValueTask CompleteAsync(SessionSummary summary, CancellationToken cancellation);
}

internal sealed record SessionTiming(
    TimeSpan InitialAnswerWindow,
    TimeSpan ExtendedAnswerWindow,
    TimeSpan PendingLifetime,
    TimeSpan QueueLifetime,
    TimeSpan FeedbackDuration,
    TimeSpan TickInterval)
{
    public TimeSpan Warmup { get; init; } = TimeSpan.Zero;
    public TimeSpan AutoCooldown { get; init; } = TimeSpan.Zero;
    public TimeSpan CandidateLifetime { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ManualSafetyGap { get; init; } = TimeSpan.Zero;
    public int CandidateCapacity { get; init; } = 1;

    public static SessionTiming Default { get; } = new(
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(3),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromMilliseconds(100))
    {
        Warmup = TimeSpan.FromSeconds(60),
        AutoCooldown = TimeSpan.FromSeconds(120),
        CandidateLifetime = TimeSpan.FromSeconds(180),
        ManualSafetyGap = TimeSpan.FromSeconds(30),
        CandidateCapacity = 3
    };
}

internal sealed class InMemorySessionJournalAdapter : ISessionJournal
{
    private readonly List<SessionEvent> _events = [];

    public IReadOnlyList<SessionEvent> Events => _events;
    public SessionSummary? Summary { get; private set; }

    public ValueTask InitializeAsync(SessionId sessionId, SessionStart start, DateTimeOffset startedAt, CancellationToken cancellation)
    {
        _events.Add(new SessionEvent(sessionId, startedAt, "SessionStarted", start));
        return ValueTask.CompletedTask;
    }

    public ValueTask AppendAsync(SessionEvent sessionEvent, CancellationToken cancellation)
    {
        _events.Add(sessionEvent);
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteAsync(SessionSummary summary, CancellationToken cancellation)
    {
        Summary = summary;
        _events.Add(new SessionEvent(summary.SessionId, summary.CompletedAt, "SessionCompleted", summary));
        return ValueTask.CompletedTask;
    }
}
