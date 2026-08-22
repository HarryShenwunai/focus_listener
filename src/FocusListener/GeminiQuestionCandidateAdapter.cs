using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Google.GenAI;
using Google.GenAI.Types;
using GeminiSchemaType = Google.GenAI.Types.Type;

namespace FocusListener;

internal sealed class GeminiQuestionCandidateAdapter : IQuestionCandidateSource
{
    private readonly object _latestGate = new();
    private readonly GeminiFocusOptions _options;
    private readonly ISessionClock _clock;
    private ResetQuestionCandidate? _latest;

    public GeminiQuestionCandidateAdapter(GeminiFocusOptions options, ISessionClock clock)
    {
        _options = options;
        _clock = clock;
    }

    public async IAsyncEnumerable<ResetQuestionCandidate> AutomaticAsync(
        SessionStart start,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
    {
        var audio = new WindowsClassroomAudioAdapter();
        var transcriber = new GeminiLiveTranscriptionAdapter(_options, _clock);
        using var generator = new GeminiRestatementQuestionGenerator(_options);

        await foreach (var transcript in transcriber.TranscribeAsync(audio.CaptureAsync(cancellation), cancellation))
        {
            var candidate = await generator.TryGenerateAsync(transcript, TriggerKind.Automatic, cancellation);
            if (candidate is null)
            {
                continue;
            }

            lock (_latestGate)
            {
                _latest = candidate;
            }

            yield return candidate;
        }
    }

    public ValueTask<ResetQuestionCandidate?> RequestManualAsync(
        SessionStart start,
        CancellationToken cancellation)
    {
        lock (_latestGate)
        {
            return ValueTask.FromResult(_latest is null ? null : CloneForManualTrigger(_latest));
        }
    }

    private static ResetQuestionCandidate CloneForManualTrigger(ResetQuestionCandidate source)
    {
        var question = source.Question with { Id = QuestionId.New() };
        return source with { Question = question, Trigger = TriggerKind.Manual };
    }
}

internal sealed partial class GeminiRestatementQuestionGenerator : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GeminiFocusOptions _options;
    private readonly Client _client;

    public GeminiRestatementQuestionGenerator(GeminiFocusOptions options)
    {
        _options = options;
        _client = new Client(apiKey: options.ApiKey);
    }

    public async ValueTask<ResetQuestionCandidate?> TryGenerateAsync(
        TranscriptUnit unit,
        TriggerKind trigger,
        CancellationToken cancellation)
    {
        var response = await _client.Models.GenerateContentAsync(
            _options.QuestionModel,
            $"判断下面课堂转写是否包含一个可独立复述的小学行程问题知识点；若合格，生成一道三选一复述题。\n\n课堂转写：\n{unit.Text}",
            new GenerateContentConfig
            {
                Temperature = 0.1,
                MaxOutputTokens = 500,
                ResponseMimeType = "application/json",
                ResponseSchema = BuildSchema(),
                SystemInstruction = new Content
                {
                    Parts =
                    [
                        new Part
                        {
                            Text = "你是课堂注意力复位题生成器。只处理小学数学行程问题。题目只能考关系识别或术语定义，绝对不能要求计算、列式、求数值。三个选项必须短且互斥；evidence_excerpt 必须逐字摘自转写。若转写不完整、不是知识点、证据不足或无法避免计算，eligible=false，并将其余字符串置空。"
                        }
                    ]
                }
            },
            cancellation);

        CandidatePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CandidatePayload>(response.Text, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        return ValidateAndMap(payload, unit, trigger);
    }

    public void Dispose() => _client.Dispose();

    private static ResetQuestionCandidate? ValidateAndMap(
        CandidatePayload? payload,
        TranscriptUnit unit,
        TriggerKind trigger)
    {
        if (payload is null || !payload.Eligible ||
            string.IsNullOrWhiteSpace(payload.Stem) ||
            string.IsNullOrWhiteSpace(payload.ChoiceA) ||
            string.IsNullOrWhiteSpace(payload.ChoiceB) ||
            string.IsNullOrWhiteSpace(payload.ChoiceC) ||
            string.IsNullOrWhiteSpace(payload.EvidenceExcerpt))
        {
            return null;
        }

        var normalizedTranscript = NormalizeEvidence(unit.Text);
        var normalizedEvidence = NormalizeEvidence(payload.EvidenceExcerpt);
        if (normalizedEvidence.Length < 8 || !normalizedTranscript.Contains(normalizedEvidence, StringComparison.Ordinal))
        {
            return null;
        }

        var visibleQuestionText = string.Join(' ', payload.Stem, payload.ChoiceA, payload.ChoiceB, payload.ChoiceC);
        if (CalculationPattern().IsMatch(visibleQuestionText))
        {
            return null;
        }

        var choices = new[] { payload.ChoiceA.Trim(), payload.ChoiceB.Trim(), payload.ChoiceC.Trim() };
        if (choices.Distinct(StringComparer.Ordinal).Count() != 3)
        {
            return null;
        }

        var correct = payload.CorrectChoice switch
        {
            "A" => new ChoiceId("A"),
            "B" => new ChoiceId("B"),
            "C" => new ChoiceId("C"),
            _ => default
        };
        if (string.IsNullOrEmpty(correct.Value))
        {
            return null;
        }

        var type = payload.QuestionType switch
        {
            "term_definition" => QuestionType.TermDefinition,
            "relationship_recognition" => QuestionType.RelationshipRecognition,
            _ => (QuestionType?)null
        };
        if (type is null)
        {
            return null;
        }

        var question = new RestatementQuestion(
            QuestionId.New(),
            type.Value,
            payload.Stem.Trim(),
            [
                new QuestionChoice(new ChoiceId("A"), choices[0]),
                new QuestionChoice(new ChoiceId("B"), choices[1]),
                new QuestionChoice(new ChoiceId("C"), choices[2])
            ]);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(unit.Text)))[..12];
        return new ResetQuestionCandidate(
            $"unit-{hash}",
            unit.RecognizedAt,
            question,
            correct,
            new LessonEvidence(payload.EvidenceExcerpt.Trim(), unit.RelativeStart),
            trigger);
    }

    private static string NormalizeEvidence(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    private static Schema BuildSchema()
    {
        static Schema Text(string description, params string[] allowed) => new()
        {
            Type = GeminiSchemaType.String,
            Description = description,
            Enum = allowed.Length == 0 ? null : [.. allowed]
        };

        return new Schema
        {
            Type = GeminiSchemaType.Object,
            Properties = new Dictionary<string, Schema>
            {
                ["eligible"] = new() { Type = GeminiSchemaType.Boolean },
                ["question_type"] = Text("题型", "relationship_recognition", "term_definition"),
                ["stem"] = Text("不含计算任务的知识复述题干"),
                ["choice_a"] = Text("选项 A"),
                ["choice_b"] = Text("选项 B"),
                ["choice_c"] = Text("选项 C"),
                ["correct_choice"] = Text("正确选项", "A", "B", "C"),
                ["evidence_excerpt"] = Text("课堂转写中的逐字证据")
            },
            Required =
            [
                "eligible", "question_type", "stem", "choice_a", "choice_b", "choice_c",
                "correct_choice", "evidence_excerpt"
            ],
            PropertyOrdering =
            [
                "eligible", "question_type", "stem", "choice_a", "choice_b", "choice_c",
                "correct_choice", "evidence_excerpt"
            ]
        };
    }

    [GeneratedRegex(@"[0-9０-９]|多少|几(?:米|千米|分钟|小时)|求(?:出|得)?|计算|列式|等于|[+＋\-－×÷=＝]", RegexOptions.CultureInvariant)]
    private static partial Regex CalculationPattern();

    private sealed record CandidatePayload(
        [property: JsonPropertyName("eligible")] bool Eligible,
        [property: JsonPropertyName("question_type")] string QuestionType,
        [property: JsonPropertyName("stem")] string Stem,
        [property: JsonPropertyName("choice_a")] string ChoiceA,
        [property: JsonPropertyName("choice_b")] string ChoiceB,
        [property: JsonPropertyName("choice_c")] string ChoiceC,
        [property: JsonPropertyName("correct_choice")] string CorrectChoice,
        [property: JsonPropertyName("evidence_excerpt")] string EvidenceExcerpt);
}
