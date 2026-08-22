namespace FocusListener.Tests;

public sealed class CandidateCorrectionTests
{
    [Fact]
    public void New_evidence_for_the_same_knowledge_key_replaces_an_older_candidate()
    {
        var epoch = new DateTimeOffset(2026, 8, 22, 13, 0, 0, TimeSpan.Zero);
        var timing = SessionTiming.Default with { Warmup = TimeSpan.FromMinutes(5) };
        var scheduler = new CandidateScheduler(epoch, timing);
        var original = Candidate(epoch, "课堂先说旧条件。", "旧条件");
        var corrected = Candidate(epoch.AddSeconds(10), "随后老师纠正为新条件。", "新条件");

        Assert.Equal(CandidateAdmissionKind.Added, scheduler.Admit(original, epoch).Kind);
        var replacement = scheduler.Admit(corrected, epoch.AddSeconds(10));

        Assert.Equal(CandidateAdmissionKind.Replaced, replacement.Kind);
        Assert.Equal(original.Question.Id, replacement.Removed!.Question.Id);
        var selected = scheduler.TakeManual(epoch.AddSeconds(11));
        Assert.Equal("随后老师纠正为新条件。", selected!.Evidence.Excerpt);
    }

    private static ResetQuestionCandidate Candidate(
        DateTimeOffset recognizedAt,
        string evidence,
        string answer) => new(
        $"unit-{recognizedAt.ToUnixTimeSeconds()}",
        recognizedAt,
        new RestatementQuestion(
            QuestionId.New(),
            QuestionType.RuleOrCondition,
            "课堂最后采用哪个条件？",
            [
                new QuestionChoice(new ChoiceId("A"), answer),
                new QuestionChoice(new ChoiceId("B"), "无条件"),
                new QuestionChoice(new ChoiceId("C"), "相反条件")
            ]),
        new ChoiceId("A"),
        new LessonEvidence(evidence, TimeSpan.Zero),
        TriggerKind.Automatic)
        {
            Subject = "测试",
            KnowledgeFingerprint = "same-topic",
            QualityScore = 0.9
        };
}
