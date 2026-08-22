namespace FocusListener;

public readonly record struct SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct IntentId(Guid Value)
{
    public static IntentId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct QuestionId(Guid Value)
{
    public static QuestionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct ChoiceId(string Value)
{
    public override string ToString() => Value;
}

public enum ClassroomKind
{
    InPerson,
    ComputerPlayback
}

public enum TriggerKind
{
    Automatic,
    Manual
}

public enum QuestionType
{
    RelationshipRecognition,
    TermDefinition
}

public enum SessionSurfaceKind
{
    Listening,
    Question,
    PendingBadge,
    Feedback,
    AttentionRating,
    Completed,
    Failed
}

public enum SessionHealth
{
    Healthy,
    Degraded,
    Failed
}

public sealed record SessionStart(
    ClassroomKind ClassroomKind,
    TimeSpan PlannedDuration);

public sealed record QuestionChoice(
    ChoiceId Id,
    string Text);

public sealed record RestatementQuestion(
    QuestionId Id,
    QuestionType Type,
    string Stem,
    IReadOnlyList<QuestionChoice> Choices);

public sealed record LessonEvidence(
    string Excerpt,
    TimeSpan RelativeStart);

public sealed record QuestionCardView(
    QuestionId Id,
    QuestionType Type,
    string Stem,
    IReadOnlyList<QuestionChoice> Choices,
    TriggerKind Trigger);

public sealed record AnswerFeedback(
    bool IsCorrect,
    ChoiceId CorrectChoice,
    LessonEvidence Evidence);

public sealed record SessionView(
    SessionId SessionId,
    long Revision,
    SessionSurfaceKind Surface,
    SessionHealth Health,
    QuestionCardView? Question,
    AnswerFeedback? Feedback,
    DateTimeOffset? Deadline,
    DateTimeOffset? PendingExpiresAt,
    bool CanExtend,
    string? Notice);

public sealed record SessionSummary(
    SessionId SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int QuestionsShown,
    int QuestionsQueued,
    int CapacityDrops,
    int Answers,
    int CorrectAnswers,
    int InvalidQuestions,
    byte? AttentionRating);

public sealed record IntentOutcome(
    bool Accepted,
    string Code,
    string Message)
{
    public static IntentOutcome Accept(string code, string message) => new(true, code, message);
    public static IntentOutcome Reject(string code, string message) => new(false, code, message);
}

public abstract record LearnerIntent(IntentId Id);

public sealed record RequestManualTrigger(IntentId Id) : LearnerIntent(Id);
public sealed record SelectAnswer(IntentId Id, QuestionId Question, ChoiceId Choice) : LearnerIntent(Id);
public sealed record ExtendThinking(IntentId Id, QuestionId Question) : LearnerIntent(Id);
public sealed record OpenPending(IntentId Id, QuestionId Question) : LearnerIntent(Id);
public sealed record CollapsePending(IntentId Id, QuestionId Question) : LearnerIntent(Id);
public sealed record ReportQuestionIssue(IntentId Id, QuestionId Question) : LearnerIntent(Id);
public sealed record EndSession(IntentId Id) : LearnerIntent(Id);
public sealed record RateAttentionReset(IntentId Id, byte Rating) : LearnerIntent(Id);
public sealed record SkipAttentionRating(IntentId Id) : LearnerIntent(Id);

public interface IFocusSession
{
    Task<SessionSummary> RunAsync(
        SessionStart start,
        IProgress<SessionView> views,
        CancellationToken cancellation);

    ValueTask<IntentOutcome> ApplyAsync(
        LearnerIntent intent,
        CancellationToken cancellation = default);
}
