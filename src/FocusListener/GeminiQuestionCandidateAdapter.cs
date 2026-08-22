using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Google.GenAI;
using Google.GenAI.Types;
using GeminiSchemaType = Google.GenAI.Types.Type;

namespace FocusListener;

internal sealed class GeminiQuestionCandidateAdapter : IQuestionCandidateSource, IQuestionCandidateSourceStatus
{
    private readonly GeminiFocusOptions _options;
    private readonly ISessionClock _clock;
    private readonly Channel<QuestionSourceStatus> _status = Channel.CreateUnbounded<QuestionSourceStatus>();

    public GeminiQuestionCandidateAdapter(GeminiFocusOptions options, ISessionClock clock)
    {
        _options = options;
        _clock = clock;
    }

    public async IAsyncEnumerable<ResetQuestionCandidate> AutomaticAsync(
        SessionStart start,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var audio = new WindowsClassroomAudioAdapter();
        var transcriber = new GeminiLiveTranscriptionAdapter(_options, _clock);
        using var generator = new GeminiRestatementQuestionGenerator(_options);
        var input = Channel.CreateUnbounded<TranscriptUnit>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        var pump = PumpTranscriptsAsync(
            transcriber.TranscribeAsync(audio.CaptureAsync(cancellation), cancellation),
            input.Writer,
            cancellation);
        var windows = new TranscriptWindowBuffer(TimeSpan.FromSeconds(30), 600);
        var usedConcepts = new Queue<string>();

        try
        {
            while (await input.Reader.WaitToReadAsync(cancellation))
            {
                TranscriptUnit? latest = null;
                while (input.Reader.TryRead(out var available))
                {
                    latest = available;
                    windows.Add(available);
                }

                if (latest is null)
                {
                    continue;
                }

                latest = await DebounceAsync(latest, windows, input.Reader, cancellation);
                var window = windows.Build(latest);
                var evaluation = await GenerateWithRetryAsync(generator, window, usedConcepts, cancellation);
                if (evaluation?.Candidate is not { } candidate)
                {
                    if (!string.IsNullOrWhiteSpace(evaluation?.RejectionReason))
                    {
                        _status.Writer.TryWrite(new QuestionSourceStatus(
                            SessionHealth.Healthy,
                            "正在监听课堂内容。",
                            $"CandidateRejected:{evaluation.RejectionReason}"));
                    }
                    continue;
                }

                var concept = $"{candidate.Subject} / {QuestionTypeDisplay.Chinese(candidate.Question.Type)} / {candidate.Evidence.Excerpt}";
                usedConcepts.Enqueue(concept);
                while (usedConcepts.Count > 8)
                {
                    usedConcepts.Dequeue();
                }

                yield return candidate;
            }
        }
        finally
        {
            try
            {
                await pump;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            _status.Writer.TryComplete();
        }
    }

    public ValueTask<ResetQuestionCandidate?> RequestManualAsync(
        SessionStart start,
        CancellationToken cancellation) => ValueTask.FromResult<ResetQuestionCandidate?>(null);

    public IAsyncEnumerable<QuestionSourceStatus> StatusAsync(CancellationToken cancellation) =>
        _status.Reader.ReadAllAsync(cancellation);

    private async ValueTask<TranscriptUnit> DebounceAsync(
        TranscriptUnit latest,
        TranscriptWindowBuffer windows,
        ChannelReader<TranscriptUnit> input,
        CancellationToken cancellation)
    {
        while (true)
        {
            var pause = _clock.Delay(TimeSpan.FromSeconds(1.2), cancellation);
            var more = input.WaitToReadAsync(cancellation).AsTask();
            var completed = await Task.WhenAny(pause, more);
            if (ReferenceEquals(completed, pause))
            {
                await pause;
                return latest;
            }

            if (!await more)
            {
                await pause;
                return latest;
            }

            while (input.TryRead(out var available))
            {
                latest = available;
                windows.Add(available);
            }
        }
    }

    private async ValueTask<KnowledgeQuestionEvaluation?> GenerateWithRetryAsync(
        GeminiRestatementQuestionGenerator generator,
        TranscriptUnit window,
        IReadOnlyCollection<string> usedConcepts,
        CancellationToken cancellation)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await generator.EvaluateAsync(window, usedConcepts, TriggerKind.Automatic, cancellation);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (attempt == 0)
                {
                    await _clock.Delay(TimeSpan.FromSeconds(1), cancellation);
                }
            }
        }

        _status.Writer.TryWrite(new QuestionSourceStatus(
            SessionHealth.Degraded,
            "题目生成暂不可用，监听仍在继续。",
            "GenerationPaused60Seconds"));
        await _clock.Delay(TimeSpan.FromSeconds(60), cancellation);
        _status.Writer.TryWrite(new QuestionSourceStatus(
            SessionHealth.Healthy,
            "已恢复题目生成，继续监听。",
            "GenerationResumed"));

        try
        {
            return await generator.EvaluateAsync(window, usedConcepts, TriggerKind.Automatic, cancellation);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return KnowledgeQuestionEvaluation.Transient("网络、配额或模型暂不可用");
        }
    }

    private static async Task PumpTranscriptsAsync(
        IAsyncEnumerable<TranscriptUnit> source,
        ChannelWriter<TranscriptUnit> output,
        CancellationToken cancellation)
    {
        Exception? failure = null;
        try
        {
            await foreach (var unit in source.WithCancellation(cancellation))
            {
                await output.WriteAsync(unit, cancellation);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            output.TryComplete(failure);
        }
    }
}

internal sealed class TranscriptWindowBuffer(TimeSpan lookback, int maximumCharacters)
{
    private readonly List<TranscriptUnit> _units = [];

    public void Add(TranscriptUnit unit)
    {
        if (string.IsNullOrWhiteSpace(unit.Text))
        {
            return;
        }

        _units.Add(unit with { Text = unit.Text.Trim() });
        var cutoff = unit.RecognizedAt - lookback;
        _units.RemoveAll(item => item.RecognizedAt < cutoff);
    }

    public TranscriptUnit Build(TranscriptUnit latest)
    {
        var cutoff = latest.RecognizedAt - lookback;
        var available = _units.Where(unit => unit.RecognizedAt >= cutoff).ToArray();
        var text = string.Join(' ', available.Select(unit => unit.Text));
        if (text.Length > maximumCharacters)
        {
            text = text[^maximumCharacters..].TrimStart();
        }

        var relativeStart = available.Length == 0 ? latest.RelativeStart : available[0].RelativeStart;
        return new TranscriptUnit(text, latest.RecognizedAt, relativeStart);
    }
}

internal sealed class GeminiRestatementQuestionGenerator : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GeminiFocusOptions _options;
    private readonly Client _client;
    private readonly KnowledgeQuestionPolicy _policy;

    public GeminiRestatementQuestionGenerator(GeminiFocusOptions options)
    {
        _options = options;
        _client = new Client(apiKey: options.ApiKey);
        _policy = new KnowledgeQuestionPolicy();
    }

    public async ValueTask<ResetQuestionCandidate?> TryGenerateAsync(
        TranscriptUnit unit,
        TriggerKind trigger,
        CancellationToken cancellation)
    {
        var result = await EvaluateAsync(unit, Array.Empty<string>(), trigger, cancellation);
        return result.Candidate;
    }

    public async ValueTask<KnowledgeQuestionEvaluation> EvaluateAsync(
        TranscriptUnit unit,
        IReadOnlyCollection<string> usedConcepts,
        TriggerKind trigger,
        CancellationToken cancellation)
    {
        var used = usedConcepts.Count == 0
            ? "（本会话尚无已用知识点）"
            : string.Join("\n- ", usedConcepts.Select(value => value.Length <= 120 ? value : value[..120]));
        var response = await _client.Models.GenerateContentAsync(
            _options.QuestionModel,
            $"""
            从下面最多约 30 秒的课堂转写中，只选择一个证据最完整、最适合快速复述的知识点。
            已用知识点（同义重复应拒绝，出现新条件、新关系或纠正时才可再次使用）：
            - {used}

            课堂转写：
            {unit.Text}
            """,
            new GenerateContentConfig
            {
                Temperature = 0.1,
                MaxOutputTokens = 800,
                ResponseMimeType = "application/json",
                ResponseSchema = BuildSchema(),
                SystemInstruction = new Content
                {
                    Parts =
                    [
                        new Part
                        {
                            Text = """
                            你是课堂注意力复位题生成器，不是外部知识纠错器。只依据转写判断和出题。
                            合格内容必须表达完整、可独立复述的知识关系，类型限于：定义、因果、规则/条件、过程/顺序、比较/区分、分类/举例。
                            学科不限；无法判断学科时 subject=其他。题目语言跟随课堂主导语言，保留原有外语术语和双语表达。
                            只生成一道三选一快速复述题，恰好一个正确答案。可以识别公式、数字或变量之间的关系，但绝不能要求代入、计算、列式、求值或解题。
                            干扰项要可信、形式与长度相近，并能被课堂证据排除；禁止玩笑项、以上皆是、明显异类。
                            evidence_excerpt 必须是转写中的连续原话，可忽略大小写、空格和标点，但不得释义改写。
                            题干最多约 160 字符或 40 个英文单词；每个选项最多约 64 字符或 16 个英文单词；证据最多约 240 字符。
                            否定题只允许一层否定，并在否定词两侧使用【】强调。不要利用外部知识纠正教师；转写内部矛盾、歧义、残句、无证据、个人识别信息或可执行危险指令时 eligible=false。
                            quality_score 只评价证据完整度与表达清晰度，范围 0 到 1。knowledge_key 用简短、语言无关或稳定的方式表示本题实际考查关系，供本会话去重。
                            不合格时写明 rejection_reason，并把题目相关字符串置空。
                            """
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
            return KnowledgeQuestionEvaluation.Reject("模型返回的结构化结果无法解析");
        }

        var draft = payload is null ? null : new KnowledgeQuestionDraft(
            payload.Eligible,
            payload.RejectionReason ?? string.Empty,
            payload.Subject ?? string.Empty,
            payload.KnowledgeType ?? string.Empty,
            payload.Language ?? string.Empty,
            payload.QualityScore,
            payload.KnowledgeKey ?? string.Empty,
            payload.Stem ?? string.Empty,
            payload.ChoiceA ?? string.Empty,
            payload.ChoiceB ?? string.Empty,
            payload.ChoiceC ?? string.Empty,
            payload.CorrectChoice ?? string.Empty,
            payload.EvidenceExcerpt ?? string.Empty);
        return _policy.Evaluate(draft, unit, trigger);
    }

    public void Dispose() => _client.Dispose();

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
                ["rejection_reason"] = Text("不合格原因；合格时为空"),
                ["subject"] = Text("简短学科名；无法判断时为其他"),
                ["knowledge_type"] = Text("知识关系类型", "definition", "causality", "rule_condition", "process_sequence", "comparison_distinction", "classification_example"),
                ["language"] = Text("课堂主导语言或混合语言标记"),
                ["quality_score"] = new() { Type = GeminiSchemaType.Number, Description = "0 到 1 的清晰度与证据完整度" },
                ["knowledge_key"] = Text("用于本会话语义去重的稳定短键"),
                ["stem"] = Text("不要求计算的快速复述题干"),
                ["choice_a"] = Text("选项 A"),
                ["choice_b"] = Text("选项 B"),
                ["choice_c"] = Text("选项 C"),
                ["correct_choice"] = Text("正确选项", "A", "B", "C"),
                ["evidence_excerpt"] = Text("课堂转写中的连续原话")
            },
            Required =
            [
                "eligible", "rejection_reason", "subject", "knowledge_type", "language",
                "quality_score", "knowledge_key", "stem", "choice_a", "choice_b", "choice_c",
                "correct_choice", "evidence_excerpt"
            ],
            PropertyOrdering =
            [
                "eligible", "rejection_reason", "subject", "knowledge_type", "language",
                "quality_score", "knowledge_key", "stem", "choice_a", "choice_b", "choice_c",
                "correct_choice", "evidence_excerpt"
            ]
        };
    }

    private sealed record CandidatePayload(
        [property: JsonPropertyName("eligible")] bool Eligible,
        [property: JsonPropertyName("rejection_reason")] string? RejectionReason,
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("knowledge_type")] string? KnowledgeType,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("quality_score")] double QualityScore,
        [property: JsonPropertyName("knowledge_key")] string? KnowledgeKey,
        [property: JsonPropertyName("stem")] string? Stem,
        [property: JsonPropertyName("choice_a")] string? ChoiceA,
        [property: JsonPropertyName("choice_b")] string? ChoiceB,
        [property: JsonPropertyName("choice_c")] string? ChoiceC,
        [property: JsonPropertyName("correct_choice")] string? CorrectChoice,
        [property: JsonPropertyName("evidence_excerpt")] string? EvidenceExcerpt);
}
