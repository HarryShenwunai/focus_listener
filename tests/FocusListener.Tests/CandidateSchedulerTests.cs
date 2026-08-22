namespace FocusListener.Tests;

public sealed class CandidateSchedulerTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Automatic_question_waits_for_warmup_then_uses_best_candidate()
    {
        var scheduler = new CandidateScheduler(Epoch, Timing());
        scheduler.Admit(Candidate("older-clear", Epoch.AddSeconds(5), 0.98), Epoch.AddSeconds(10));
        scheduler.Admit(Candidate("fresh", Epoch.AddSeconds(50), 0.80), Epoch.AddSeconds(50));

        Assert.Null(scheduler.TakeAutomatic(Epoch.AddSeconds(59)));
        var selected = scheduler.TakeAutomatic(Epoch.AddSeconds(60));

        Assert.NotNull(selected);
        Assert.Equal("fresh", selected.EligibleUnitId);
        Assert.Equal(TriggerKind.Automatic, selected.Trigger);
    }

    [Fact]
    public void Full_pool_replaces_only_a_lower_priority_candidate()
    {
        var timing = Timing() with { CandidateCapacity = 2, Warmup = TimeSpan.FromMinutes(5) };
        var scheduler = new CandidateScheduler(Epoch, timing);
        Assert.Equal(CandidateAdmissionKind.Added,
            scheduler.Admit(Candidate("low", Epoch, 0.20), Epoch).Kind);
        Assert.Equal(CandidateAdmissionKind.Added,
            scheduler.Admit(Candidate("high", Epoch, 0.90), Epoch).Kind);

        var rejected = scheduler.Admit(Candidate("lower", Epoch, 0.10), Epoch);
        var replaced = scheduler.Admit(Candidate("best", Epoch, 1.00), Epoch);

        Assert.Equal(CandidateAdmissionKind.LowerPriority, rejected.Kind);
        Assert.Equal(CandidateAdmissionKind.Replaced, replaced.Kind);
        Assert.Equal("low", replaced.Removed!.EligibleUnitId);
    }

    [Fact]
    public void Displayed_candidate_and_all_older_candidates_are_consumed()
    {
        var scheduler = new CandidateScheduler(Epoch, Timing() with { Warmup = TimeSpan.FromMinutes(5) });
        scheduler.Admit(Candidate("old", Epoch.AddSeconds(1), 1), Epoch.AddSeconds(1));
        scheduler.Admit(Candidate("middle", Epoch.AddSeconds(2), 1), Epoch.AddSeconds(2));
        scheduler.Admit(Candidate("new", Epoch.AddSeconds(3), 1), Epoch.AddSeconds(3));

        var manual = scheduler.TakeManual(Epoch.AddSeconds(4));

        Assert.Equal("new", manual!.EligibleUnitId);
        Assert.Equal(0, scheduler.Count);
        Assert.Equal(CandidateAdmissionKind.Duplicate,
            scheduler.Admit(Candidate("repeat", Epoch.AddSeconds(5), 1, manual.KnowledgeFingerprint), Epoch.AddSeconds(5)).Kind);
    }

    [Fact]
    public void Automatic_cooldown_starts_when_question_closes()
    {
        var scheduler = new CandidateScheduler(Epoch, Timing());
        scheduler.Admit(Candidate("first", Epoch.AddSeconds(1), 1), Epoch.AddSeconds(1));
        Assert.NotNull(scheduler.TakeAutomatic(Epoch.AddSeconds(60)));
        scheduler.MarkQuestionClosed(TriggerKind.Automatic, Epoch.AddSeconds(70));
        scheduler.Admit(Candidate("second", Epoch.AddSeconds(71), 1), Epoch.AddSeconds(71));

        Assert.Null(scheduler.TakeAutomatic(Epoch.AddSeconds(189)));
        Assert.NotNull(scheduler.TakeAutomatic(Epoch.AddSeconds(190)));
    }

    [Fact]
    public void Manual_close_adds_safety_gap_without_resetting_auto_schedule()
    {
        var scheduler = new CandidateScheduler(Epoch, Timing() with { Warmup = TimeSpan.Zero });
        scheduler.Admit(Candidate("manual", Epoch, 1), Epoch);
        Assert.NotNull(scheduler.TakeManual(Epoch));
        scheduler.MarkQuestionClosed(TriggerKind.Manual, Epoch.AddSeconds(10));
        scheduler.Admit(Candidate("automatic", Epoch.AddSeconds(11), 1), Epoch.AddSeconds(11));

        Assert.Null(scheduler.TakeAutomatic(Epoch.AddSeconds(39)));
        Assert.NotNull(scheduler.TakeAutomatic(Epoch.AddSeconds(40)));
    }

    [Fact]
    public void Expired_candidates_are_never_displayed()
    {
        var scheduler = new CandidateScheduler(Epoch, Timing() with { Warmup = TimeSpan.Zero });
        scheduler.Admit(Candidate("old", Epoch, 1), Epoch);

        Assert.Null(scheduler.TakeAutomatic(Epoch.AddSeconds(181)));
        Assert.Equal(0, scheduler.Count);
    }

    private static SessionTiming Timing() => new(
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(120),
        TimeSpan.FromSeconds(180),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromMilliseconds(100))
    {
        Warmup = TimeSpan.FromSeconds(60),
        AutoCooldown = TimeSpan.FromSeconds(120),
        CandidateLifetime = TimeSpan.FromSeconds(180),
        ManualSafetyGap = TimeSpan.FromSeconds(30),
        CandidateCapacity = 3
    };

    private static ResetQuestionCandidate Candidate(
        string id,
        DateTimeOffset recognized,
        double quality,
        string? fingerprint = null) => new(
        id,
        recognized,
        new RestatementQuestion(
            QuestionId.New(),
            QuestionType.TermDefinition,
            $"{id} 是什么？",
            [
                new QuestionChoice(new ChoiceId("A"), "正确"),
                new QuestionChoice(new ChoiceId("B"), "错误一"),
                new QuestionChoice(new ChoiceId("C"), "错误二")
            ]),
        new ChoiceId("A"),
        new LessonEvidence("课堂证据原话", TimeSpan.Zero),
        TriggerKind.Automatic)
        {
            Subject = "测试",
            KnowledgeFingerprint = fingerprint ?? id,
            QualityScore = quality
        };
}
