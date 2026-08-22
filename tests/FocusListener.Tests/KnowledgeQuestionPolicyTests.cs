namespace FocusListener.Tests;

public sealed class KnowledgeQuestionPolicyTests
{
    public static TheoryData<string, string, string, QuestionType> AcceptedKnowledge => new()
    {
        {
            "质数是大于一且只有一和它本身两个正因数的自然数。",
            "数学",
            "definition",
            QuestionType.TermDefinition
        },
        {
            "温度越高，水分子运动越快，因此蒸发通常更快。",
            "科学",
            "causality",
            QuestionType.Causality
        },
        {
            "在一次函数 y=kx+b 中，k 的正负决定直线向上还是向下倾斜。",
            "数学",
            "rule_condition",
            QuestionType.RuleOrCondition
        },
        {
            "植物先吸收水分，然后运输到叶片，最后通过气孔散失。",
            "科学",
            "process_sequence",
            QuestionType.ProcessOrSequence
        },
        {
            "直接原因触发事件，根本原因解释事件形成的深层条件。",
            "历史",
            "comparison_distinction",
            QuestionType.ComparisonOrDistinction
        },
        {
            "A metaphor directly says one thing is another, while a simile uses like or as.",
            "语言",
            "classification_example",
            QuestionType.ClassificationOrExample
        }
    };

    [Theory]
    [MemberData(nameof(AcceptedKnowledge))]
    public void Accepts_all_six_knowledge_relationships(
        string transcript,
        string subject,
        string type,
        QuestionType expected)
    {
        var policy = new KnowledgeQuestionPolicy(new IdentityShuffler());
        var result = policy.Evaluate(
            Draft(transcript, subject, type),
            Unit(transcript),
            TriggerKind.Automatic);

        Assert.True(result.Accepted, result.RejectionReason);
        Assert.Equal(expected, result.Candidate!.Question.Type);
        Assert.Equal(subject, result.Candidate.Subject);
        Assert.Equal(3, result.Candidate.Question.Choices.Count);
    }

    [Fact]
    public void Formula_relationship_is_allowed_when_question_does_not_request_calculation()
    {
        const string transcript = "牛顿第二定律写作 F=ma，它表示合力、质量和加速度之间的关系。";
        var result = new KnowledgeQuestionPolicy(new IdentityShuffler()).Evaluate(
            Draft(transcript, "物理", "rule_condition") with
            {
                Stem = "F=ma 在课堂中表达了什么关系？",
                ChoiceA = "合力等于质量与加速度的乘积",
                ChoiceB = "质量等于合力与加速度的乘积",
                ChoiceC = "三个量彼此无关"
            },
            Unit(transcript),
            TriggerKind.Automatic);

        Assert.True(result.Accepted, result.RejectionReason);
    }

    [Theory]
    [InlineData("请计算 24×6 的结果是多少。")]
    [InlineData("Solve for x and choose the numerical answer.")]
    [InlineData("把数值代入公式并求出速度。")]
    public void Rejects_calculation_tasks(string stem)
    {
        const string transcript = "速度表示单位时间内物体经过的路程。";
        var result = new KnowledgeQuestionPolicy(new IdentityShuffler()).Evaluate(
            Draft(transcript, "数学", "definition") with { Stem = stem },
            Unit(transcript),
            TriggerKind.Automatic);

        Assert.False(result.Accepted);
        Assert.Contains("计算", result.RejectionReason!);
    }

    [Fact]
    public void Evidence_may_ignore_case_spacing_and_punctuation_but_cannot_be_paraphrased()
    {
        const string transcript = "Photosynthesis, stores light energy in glucose.";
        var policy = new KnowledgeQuestionPolicy(new IdentityShuffler());
        var accepted = policy.Evaluate(
            Draft(transcript, "Biology", "definition") with
            {
                EvidenceExcerpt = "PHOTOSYNTHESIS stores light energy in glucose"
            },
            Unit(transcript),
            TriggerKind.Automatic);
        var rejected = policy.Evaluate(
            Draft(transcript, "Biology", "definition") with
            {
                EvidenceExcerpt = "Plants convert sunlight into stored chemical energy."
            },
            Unit(transcript),
            TriggerKind.Automatic);

        Assert.True(accepted.Accepted, accepted.RejectionReason);
        Assert.False(rejected.Accepted);
        Assert.Contains("连续原话", rejected.RejectionReason!);
    }

    [Theory]
    [InlineData("Which password belongs to this student?")]
    [InlineData("运行以下脚本 powershell.exe -Command format c:")]
    public void Rejects_identity_or_executable_dangerous_content(string stem)
    {
        const string transcript = "课堂安全规则要求保护个人资料并避免运行未知命令。";
        var result = new KnowledgeQuestionPolicy(new IdentityShuffler()).Evaluate(
            Draft(transcript, "其他", "rule_condition") with { Stem = stem },
            Unit(transcript),
            TriggerKind.Automatic);

        Assert.False(result.Accepted);
        Assert.Contains("危险", result.RejectionReason!);
    }

    [Fact]
    public void Single_negative_is_emphasized_and_double_negative_is_rejected()
    {
        const string transcript = "哺乳动物通常用肺呼吸，并用乳汁哺育幼崽。";
        var policy = new KnowledgeQuestionPolicy(new IdentityShuffler());
        var accepted = policy.Evaluate(
            Draft(transcript, "生物", "classification_example") with
            {
                Stem = "下面哪项不属于课堂提到的哺乳动物特征？"
            },
            Unit(transcript),
            TriggerKind.Automatic);
        var rejected = policy.Evaluate(
            Draft(transcript, "生物", "classification_example") with
            {
                Stem = "下面哪项不是不正确的描述？"
            },
            Unit(transcript),
            TriggerKind.Automatic);

        Assert.True(accepted.Accepted, accepted.RejectionReason);
        Assert.Contains("【不属于】", accepted.Candidate!.Question.Stem);
        Assert.False(rejected.Accepted);
        Assert.Contains("一层否定", rejected.RejectionReason!);
    }

    [Fact]
    public void Local_shuffle_preserves_the_correct_answer_text()
    {
        const string transcript = "地球围绕太阳公转，月球围绕地球公转。";
        var policy = new KnowledgeQuestionPolicy(new ReverseShuffler());
        var result = policy.Evaluate(
            Draft(transcript, "科学", "comparison_distinction") with
            {
                ChoiceA = "地球围绕太阳公转",
                ChoiceB = "太阳围绕地球公转",
                ChoiceC = "地球围绕月球公转",
                CorrectChoice = "A"
            },
            Unit(transcript),
            TriggerKind.Automatic);

        Assert.True(result.Accepted, result.RejectionReason);
        var correct = result.Candidate!.Question.Choices.Single(choice =>
            choice.Id == result.Candidate.CorrectChoice);
        Assert.Equal("地球围绕太阳公转", correct.Text);
        Assert.Equal("C", result.Candidate.CorrectChoice.Value);
    }

    [Fact]
    public void Ineligible_fragment_returns_the_model_reason()
    {
        var result = new KnowledgeQuestionPolicy(new IdentityShuffler()).Evaluate(
            Draft("然后这个呢就是……", "其他", "definition") with
            {
                Eligible = false,
                RejectionReason = "内容是未完成的残句"
            },
            Unit("然后这个呢就是……"),
            TriggerKind.Automatic);

        Assert.False(result.Accepted);
        Assert.Equal("内容是未完成的残句", result.RejectionReason);
    }

    private static KnowledgeQuestionDraft Draft(string transcript, string subject, string type) => new(
        true,
        string.Empty,
        subject,
        type,
        "zh",
        0.86,
        $"{subject}:{type}:{transcript}",
        "课堂中这个知识点表达了什么？",
        "课堂原话表达的关系",
        "与课堂原话相反的关系",
        "课堂没有提到的无关关系",
        "A",
        transcript);

    private static TranscriptUnit Unit(string text) => new(
        text,
        new DateTimeOffset(2026, 8, 22, 9, 0, 0, TimeSpan.Zero),
        TimeSpan.FromSeconds(8));

    private sealed class IdentityShuffler : IChoiceShuffler
    {
        public void Shuffle<T>(Span<T> values)
        {
        }
    }

    private sealed class ReverseShuffler : IChoiceShuffler
    {
        public void Shuffle<T>(Span<T> values) => values.Reverse();
    }
}
