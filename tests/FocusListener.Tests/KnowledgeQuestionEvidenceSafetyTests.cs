namespace FocusListener.Tests;

public sealed class KnowledgeQuestionEvidenceSafetyTests
{
    [Fact]
    public void Rejects_personal_identifier_even_when_it_appears_only_in_evidence()
    {
        const string transcript = "示例账户的电子邮箱是 student@example.com，请记住这个格式。";
        var draft = new KnowledgeQuestionDraft(
            true,
            string.Empty,
            "其他",
            "definition",
            "zh",
            0.8,
            "email-format",
            "课堂提到的格式属于哪一类？",
            "联系格式",
            "时间格式",
            "距离格式",
            "A",
            transcript);

        var result = new KnowledgeQuestionPolicy(new IdentityShuffler()).Evaluate(
            draft,
            new TranscriptUnit(transcript, DateTimeOffset.UtcNow, TimeSpan.Zero),
            TriggerKind.Automatic);

        Assert.False(result.Accepted);
        Assert.Contains("个人识别信息", result.RejectionReason!);
    }

    private sealed class IdentityShuffler : IChoiceShuffler
    {
        public void Shuffle<T>(Span<T> values)
        {
        }
    }
}
