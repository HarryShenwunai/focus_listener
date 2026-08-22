namespace FocusListener.Tests;

public sealed class KnowledgeQuestionLengthTests
{
    [Fact]
    public void Rejects_evidence_over_240_characters()
    {
        var transcript = new string('知', 241);
        var result = Evaluate(transcript, "课堂中这个知识点表达了什么？", transcript);

        Assert.False(result.Accepted);
        Assert.Contains("证据过长", result.RejectionReason!);
    }

    [Fact]
    public void Rejects_question_stem_over_160_characters()
    {
        const string transcript = "完整知识点表达了稳定的因果关系。";
        var result = Evaluate(transcript, new string('题', 161), transcript);

        Assert.False(result.Accepted);
        Assert.Contains("题干或选项过长", result.RejectionReason!);
    }

    private static KnowledgeQuestionEvaluation Evaluate(string transcript, string stem, string evidence)
    {
        var draft = new KnowledgeQuestionDraft(
            true,
            string.Empty,
            "其他",
            "causality",
            "zh",
            0.8,
            "length-test",
            stem,
            "关系一",
            "关系二",
            "关系三",
            "A",
            evidence);
        return new KnowledgeQuestionPolicy(new IdentityShuffler()).Evaluate(
            draft,
            new TranscriptUnit(transcript, DateTimeOffset.UtcNow, TimeSpan.Zero),
            TriggerKind.Automatic);
    }

    private sealed class IdentityShuffler : IChoiceShuffler
    {
        public void Shuffle<T>(Span<T> values)
        {
        }
    }
}
