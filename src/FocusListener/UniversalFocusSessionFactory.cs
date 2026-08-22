using System.Runtime.CompilerServices;

namespace FocusListener;

public static class UniversalFocusSessionFactory
{
    public static IFocusSession CreateProduction(
        GeminiFocusOptions options,
        string databasePath,
        FocusInteractionSettings settings,
        ClassroomExperienceControl experience)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(experience);
        options.Validate();
        var clock = new SystemSessionClock();
        return new FocusSession(
            new ClassroomQuestionCandidateAdapter(options, clock, experience),
            new SqliteSessionJournalAdapter(databasePath),
            clock,
            settings.ToSessionTiming());
    }

    public static IFocusSession CreateSimulation(
        string databasePath,
        FocusInteractionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var clock = new SystemSessionClock();
        return new FocusSession(
            new GenericScriptedCandidateAdapter(clock),
            new SqliteSessionJournalAdapter(databasePath),
            clock,
            settings.ToSessionTiming());
    }
}

internal sealed class GenericScriptedCandidateAdapter(ISessionClock clock) : IQuestionCandidateSource
{

    public async IAsyncEnumerable<ResetQuestionCandidate> AutomaticAsync(
        SessionStart start,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var samples = new[]
        {
            Create(
                "science-causality",
                QuestionType.Causality,
                "科学",
                "课堂上说，温度升高会使液体蒸发得更快。这里描述的是哪种关系？",
                "温度升高促进蒸发",
                "蒸发导致温度必然升高",
                "温度与蒸发没有关系",
                "A",
                "温度升高会使液体蒸发得更快。",
                0.91),
            Create(
                "history-comparison",
                QuestionType.ComparisonOrDistinction,
                "历史",
                "课堂怎样区分直接原因和根本原因？",
                "直接原因触发事件，根本原因解释深层条件",
                "两者只是不同说法，没有区别",
                "根本原因一定发生得更晚",
                "A",
                "直接原因触发事件，根本原因解释事件形成的深层条件。",
                0.87),
            Create(
                "language-definition",
                QuestionType.TermDefinition,
                "语言",
                "A metaphor 在课堂中的定义是什么？",
                "直接说一事物就是另一事物来建立联系",
                "只比较两个数字的大小",
                "按时间顺序列出所有事件",
                "A",
                "A metaphor directly says one thing is another to create a connection.",
                0.89)
        };

        foreach (var sample in samples)
        {
            await clock.Delay(TimeSpan.FromSeconds(18), cancellation);
            yield return sample with { RecognizedAt = clock.UtcNow };
        }
    }

    public ValueTask<ResetQuestionCandidate?> RequestManualAsync(
        SessionStart start,
        CancellationToken cancellation) =>
        ValueTask.FromResult<ResetQuestionCandidate?>(null);

    private ResetQuestionCandidate Create(
        string unit,
        QuestionType type,
        string subject,
        string stem,
        string a,
        string b,
        string c,
        string correct,
        string evidence,
        double quality)
    {
        return new ResetQuestionCandidate(
            unit,
            clock.UtcNow,
            new RestatementQuestion(
                QuestionId.New(),
                type,
                stem,
                [
                    new QuestionChoice(new ChoiceId("A"), a),
                    new QuestionChoice(new ChoiceId("B"), b),
                    new QuestionChoice(new ChoiceId("C"), c)
                ])
            {
                Subject = subject,
                Language = subject == "语言" ? "mixed" : "zh"
            },
            new ChoiceId(correct),
            new LessonEvidence(evidence, TimeSpan.Zero),
            TriggerKind.Automatic)
        {
            Subject = subject,
            Language = subject == "语言" ? "mixed" : "zh",
            KnowledgeFingerprint = unit,
            QualityScore = quality
        };
    }
}
