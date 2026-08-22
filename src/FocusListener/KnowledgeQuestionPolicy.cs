using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FocusListener;

internal sealed record KnowledgeQuestionDraft(
    bool Eligible,
    string RejectionReason,
    string Subject,
    string KnowledgeType,
    string Language,
    double QualityScore,
    string KnowledgeKey,
    string Stem,
    string ChoiceA,
    string ChoiceB,
    string ChoiceC,
    string CorrectChoice,
    string EvidenceExcerpt);

internal sealed record KnowledgeQuestionEvaluation(
    ResetQuestionCandidate? Candidate,
    string? RejectionReason,
    bool IsTransientFailure = false)
{
    public bool Accepted => Candidate is not null;

    public static KnowledgeQuestionEvaluation Accept(ResetQuestionCandidate candidate) => new(candidate, null);
    public static KnowledgeQuestionEvaluation Reject(string reason) => new(null, reason);
    public static KnowledgeQuestionEvaluation Transient(string reason) => new(null, reason, true);
}

internal interface IChoiceShuffler
{
    void Shuffle<T>(Span<T> values);
}

internal sealed class SecureChoiceShuffler : IChoiceShuffler
{
    public static SecureChoiceShuffler Instance { get; } = new();

    public void Shuffle<T>(Span<T> values)
    {
        for (var index = values.Length - 1; index > 0; index--)
        {
            var target = RandomNumberGenerator.GetInt32(index + 1);
            (values[index], values[target]) = (values[target], values[index]);
        }
    }
}

internal sealed partial class KnowledgeQuestionPolicy(IChoiceShuffler? shuffler = null)
{
    private static readonly string[] SupportedTypes =
    [
        "definition",
        "causality",
        "rule_condition",
        "process_sequence",
        "comparison_distinction",
        "classification_example"
    ];

    private readonly IChoiceShuffler _shuffler = shuffler ?? SecureChoiceShuffler.Instance;

    public KnowledgeQuestionEvaluation Evaluate(
        KnowledgeQuestionDraft? draft,
        TranscriptUnit unit,
        TriggerKind trigger)
    {
        if (draft is null)
        {
            return KnowledgeQuestionEvaluation.Reject("模型未返回可读取的结构化结果");
        }

        var rejection = Validate(draft, unit.Text);
        if (rejection is not null)
        {
            return KnowledgeQuestionEvaluation.Reject(rejection);
        }

        var type = MapType(draft.KnowledgeType)!;
        var original = new[]
        {
            new DraftChoice("A", draft.ChoiceA.Trim()),
            new DraftChoice("B", draft.ChoiceB.Trim()),
            new DraftChoice("C", draft.ChoiceC.Trim())
        };
        _shuffler.Shuffle(original.AsSpan());

        ChoiceId correct = default;
        var choices = new QuestionChoice[3];
        for (var index = 0; index < original.Length; index++)
        {
            var visibleId = new ChoiceId(((char)('A' + index)).ToString());
            choices[index] = new QuestionChoice(visibleId, original[index].Text);
            if (string.Equals(original[index].OriginalId, draft.CorrectChoice, StringComparison.OrdinalIgnoreCase))
            {
                correct = visibleId;
            }
        }

        var subject = string.IsNullOrWhiteSpace(draft.Subject) ? "其他" : draft.Subject.Trim();
        var knowledgeKey = string.IsNullOrWhiteSpace(draft.KnowledgeKey)
            ? $"{draft.KnowledgeType}:{draft.EvidenceExcerpt}"
            : draft.KnowledgeKey;
        var fingerprint = Hash(NormalizeForComparison(knowledgeKey));
        var question = new RestatementQuestion(
            QuestionId.New(),
            type.Value,
            EmphasizeSingleNegation(draft.Stem.Trim()),
            choices)
        {
            Subject = subject,
            Language = string.IsNullOrWhiteSpace(draft.Language) ? "und" : draft.Language.Trim()
        };

        var candidate = new ResetQuestionCandidate(
            $"unit-{Hash(NormalizeForComparison(unit.Text))}",
            unit.RecognizedAt,
            question,
            correct,
            new LessonEvidence(draft.EvidenceExcerpt.Trim(), unit.RelativeStart),
            trigger)
        {
            Subject = subject,
            KnowledgeFingerprint = fingerprint,
            QualityScore = Math.Clamp(draft.QualityScore, 0, 1),
            Language = question.Language
        };
        return KnowledgeQuestionEvaluation.Accept(candidate);
    }

    private static string? Validate(KnowledgeQuestionDraft draft, string transcript)
    {
        if (!draft.Eligible)
        {
            return string.IsNullOrWhiteSpace(draft.RejectionReason)
                ? "这段内容不是完整、可独立复述的知识关系"
                : draft.RejectionReason.Trim();
        }

        if (!SupportedTypes.Contains(draft.KnowledgeType, StringComparer.Ordinal))
        {
            return "知识类型不在允许范围内";
        }

        if (new[] { draft.Stem, draft.ChoiceA, draft.ChoiceB, draft.ChoiceC, draft.CorrectChoice, draft.EvidenceExcerpt }
            .Any(string.IsNullOrWhiteSpace))
        {
            return "题目、选项、答案或课堂证据不完整";
        }

        var normalizedTranscript = NormalizeForComparison(transcript);
        var normalizedEvidence = NormalizeForComparison(draft.EvidenceExcerpt);
        if (normalizedEvidence.Length < 4 ||
            !normalizedTranscript.Contains(normalizedEvidence, StringComparison.Ordinal))
        {
            return "课堂证据不是转写中的连续原话";
        }

        if (draft.EvidenceExcerpt.Trim().Length > 240)
        {
            return "课堂证据过长，不适合快速反馈";
        }

        var choices = new[] { draft.ChoiceA.Trim(), draft.ChoiceB.Trim(), draft.ChoiceC.Trim() };
        if (choices.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
        {
            return "三个选项必须互不相同";
        }

        if (draft.CorrectChoice is not ("A" or "B" or "C"))
        {
            return "正确选项必须是 A、B 或 C";
        }

        if (TooLong(draft.Stem, 160, 40) || choices.Any(choice => TooLong(choice, 64, 16)))
        {
            return "题干或选项过长，不适合快速作答";
        }

        var visibleText = string.Join(' ', draft.Stem, draft.ChoiceA, draft.ChoiceB, draft.ChoiceC);
        if (CalculationTaskPattern().IsMatch(visibleText))
        {
            return "题目要求计算、代入或求解";
        }

        var safetyText = visibleText + ' ' + draft.EvidenceExcerpt;
        if (SensitiveIdentityPattern().IsMatch(safetyText) || DangerousInstructionPattern().IsMatch(safetyText))
        {
            return "题目包含个人识别信息或可执行的危险指令";
        }

        if (CountNegations(draft.Stem) > 1)
        {
            return "否定题只能包含一层否定";
        }

        return null;
    }

    internal static string NormalizeForComparison(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                builder.Append(rune.ToString().ToLowerInvariant());
            }
        }

        return builder.ToString();
    }

    private static QuestionType? MapType(string value) => value switch
    {
        "definition" => QuestionType.TermDefinition,
        "causality" => QuestionType.Causality,
        "rule_condition" => QuestionType.RuleOrCondition,
        "process_sequence" => QuestionType.ProcessOrSequence,
        "comparison_distinction" => QuestionType.ComparisonOrDistinction,
        "classification_example" => QuestionType.ClassificationOrExample,
        _ => null
    };

    private static bool TooLong(string value, int characters, int englishWords) =>
        value.Trim().Length > characters || EnglishWordPattern().Matches(value).Count > englishWords;

    private static int CountNegations(string value) => NegationPattern().Matches(value).Count;

    private static string EmphasizeSingleNegation(string stem)
    {
        if (stem.Contains('【'))
        {
            return stem;
        }

        var match = NegationPattern().Match(stem);
        if (!match.Success)
        {
            return stem;
        }

        return stem[..match.Index] + $"【{match.Value}】" + stem[(match.Index + match.Length)..];
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    [GeneratedRegex(@"(?:计算|求出|算出|列式|解方程|代入|得数|结果是多少|答案是多少|calculate|compute|solve|evaluate|work\s+out|what\s+is\s+the\s+(?:value|answer|result))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CalculationTaskPattern();

    [GeneratedRegex(@"(?:api[_ -]?key|password|密码|身份证|手机号|电子邮箱|\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b|\b1[3-9]\d{9}\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveIdentityPattern();

    [GeneratedRegex(@"(?:rm\s+-rf|format\s+[a-z]:|reg\s+delete|del\s+/[fsq]|powershell(?:\.exe)?\s+-|cmd(?:\.exe)?\s+/c|执行以下命令|运行以下脚本)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DangerousInstructionPattern();

    [GeneratedRegex(@"(?:不正确|不属于|不包括|不能|不是|错误的是|incorrect|except|not\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NegationPattern();

    [GeneratedRegex(@"\b[A-Za-z]+(?:['’-][A-Za-z]+)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishWordPattern();

    private sealed record DraftChoice(string OriginalId, string Text);
}
