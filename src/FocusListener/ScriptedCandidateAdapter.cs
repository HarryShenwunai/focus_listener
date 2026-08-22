using System.Runtime.CompilerServices;

namespace FocusListener;

internal sealed class ScriptedCandidateAdapter(ISessionClock clock) : IQuestionCandidateSource
{
    private readonly ISessionClock _clock = clock;
    private int _latestTemplate = -1;
    private int _eligibleSequence;

    public async IAsyncEnumerable<ResetQuestionCandidate> AutomaticAsync(
        SessionStart start,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        await _clock.Delay(TimeSpan.FromSeconds(4), cancellation);
        var index = 0;
        while (!cancellation.IsCancellationRequested)
        {
            Volatile.Write(ref _latestTemplate, index);
            yield return Create(index, TriggerKind.Automatic);
            index = (index + 1) % 3;
            await _clock.Delay(TimeSpan.FromSeconds(14), cancellation);
        }
    }

    public ValueTask<ResetQuestionCandidate?> RequestManualAsync(SessionStart start, CancellationToken cancellation)
    {
        var index = Volatile.Read(ref _latestTemplate);
        return ValueTask.FromResult(index < 0 ? null : Create(index, TriggerKind.Manual));
    }

    private ResetQuestionCandidate Create(int index, TriggerKind trigger)
    {
        var questionId = QuestionId.New();
        var eligibleId = $"sim-unit-{Interlocked.Increment(ref _eligibleSequence)}";
        var recognizedAt = _clock.UtcNow;
        return index switch
        {
            0 => Candidate(
                eligibleId,
                recognizedAt,
                questionId,
                QuestionType.RelationshipRecognition,
                "相遇问题中，两人走过的路程合起来表示什么？",
                [
                    new QuestionChoice(new ChoiceId("a"), "两地之间的总路程"),
                    new QuestionChoice(new ChoiceId("b"), "两人的速度差"),
                    new QuestionChoice(new ChoiceId("c"), "较快者单独走的路程")
                ],
                new ChoiceId("a"),
                "相遇时，两人走过的路程合起来就是两地之间的总路程。",
                trigger),
            1 => Candidate(
                eligibleId,
                recognizedAt,
                questionId,
                QuestionType.TermDefinition,
                "“速度和”在相遇问题中指什么？",
                [
                    new QuestionChoice(new ChoiceId("a"), "两个人速度相加"),
                    new QuestionChoice(new ChoiceId("b"), "较快速度减较慢速度"),
                    new QuestionChoice(new ChoiceId("c"), "路程除以人数")
                ],
                new ChoiceId("a"),
                "相向而行时，把两个人的速度加起来，得到速度和。",
                trigger),
            _ => Candidate(
                eligibleId,
                recognizedAt,
                questionId,
                QuestionType.RelationshipRecognition,
                "相遇时间与总路程、速度和之间是什么关系？",
                [
                    new QuestionChoice(new ChoiceId("a"), "总路程除以速度和"),
                    new QuestionChoice(new ChoiceId("b"), "速度和除以总路程"),
                    new QuestionChoice(new ChoiceId("c"), "总路程加速度和")
                ],
                new ChoiceId("a"),
                "相遇时间等于总路程除以两个人的速度和。",
                trigger)
        };
    }

    private static ResetQuestionCandidate Candidate(
        string eligibleId,
        DateTimeOffset recognizedAt,
        QuestionId questionId,
        QuestionType type,
        string stem,
        IReadOnlyList<QuestionChoice> choices,
        ChoiceId correct,
        string evidence,
        TriggerKind trigger) => new(
            eligibleId,
            recognizedAt,
            new RestatementQuestion(questionId, type, stem, choices),
            correct,
            new LessonEvidence(evidence, TimeSpan.FromSeconds(-20)),
            trigger);
}
