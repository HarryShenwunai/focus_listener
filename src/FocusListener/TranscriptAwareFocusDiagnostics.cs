namespace FocusListener;

public static class TranscriptAwareFocusDiagnosticsFactory
{
    public static IFocusDiagnostics Create(
        GeminiFocusOptions? gemini,
        string outputDirectory,
        AudioCaptureConfiguration? audio = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        return FocusDiagnosticsFactory.Create(gemini, outputDirectory, audio);
    }
}

internal sealed record DiagnosticQuestionGeneration(
    DiagnosticQuestionPreview? Question,
    string Detail)
{
    public bool Accepted => Question is not null;
}

internal interface IDiagnosticQuestionGenerator
{
    ValueTask<DiagnosticQuestionPreview?> TryGenerateAsync(
        TranscriptUnit input,
        CancellationToken cancellation);

    async ValueTask<DiagnosticQuestionGeneration> GenerateAsync(
        TranscriptUnit input,
        CancellationToken cancellation)
    {
        var question = await TryGenerateAsync(input, cancellation);
        return new DiagnosticQuestionGeneration(
            question,
            question is null ? "这段转写没有通过通用知识点规则" : "证据校验通过");
    }
}

internal sealed class GeminiDiagnosticQuestionGenerator(GeminiFocusOptions options)
    : IDiagnosticQuestionGenerator
{
    public async ValueTask<DiagnosticQuestionPreview?> TryGenerateAsync(
        TranscriptUnit input,
        CancellationToken cancellation) =>
        (await GenerateAsync(input, cancellation)).Question;

    public async ValueTask<DiagnosticQuestionGeneration> GenerateAsync(
        TranscriptUnit input,
        CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var generator = new GeminiRestatementQuestionGenerator(options);
        var evaluation = await generator.EvaluateAsync(
            input,
            Array.Empty<string>(),
            TriggerKind.Automatic,
            timeout.Token);
        if (evaluation.Candidate is not { } candidate)
        {
            return new DiagnosticQuestionGeneration(
                null,
                evaluation.RejectionReason ?? "这段转写没有通过通用知识点规则");
        }

        return new DiagnosticQuestionGeneration(
            new DiagnosticQuestionPreview(
                candidate.Question.Stem,
                candidate.Question.Choices
                    .Select(choice => $"{choice.Id.Value}  {choice.Text}")
                    .ToArray(),
                candidate.Evidence.Excerpt),
            $"{candidate.Subject} · {QuestionTypeDisplay.Chinese(candidate.Question.Type)} · 证据校验通过");
    }
}

internal sealed class TranscriptAwareFocusDiagnostics(
    IFocusDiagnostics inner,
    IDiagnosticQuestionGenerator? questionGenerator) : IFocusDiagnostics
{
    public async Task<FocusDiagnosticsSummary> RunAsync(
        IProgress<FocusDiagnosticsView> views,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(views);
        var bridge = new InlineProgress<FocusDiagnosticsView>(view =>
            views.Report(HideFixedProbeQuestion(view)));
        var innerResult = await inner.RunAsync(bridge, cancellation);
        var source = innerResult.FinalView;

        if (QuestionUsesLiveTranscript(source))
        {
            views.Report(source);
            return innerResult;
        }

        if (!CanGenerate(source))
        {
            var finalWithoutQuestion = source with
            {
                Revision = source.Revision + 1,
                Question = null
            };
            views.Report(finalWithoutQuestion);
            return BuildSummary(innerResult, finalWithoutQuestion);
        }

        var working = ReplaceQuestionItem(
            source,
            FocusDiagnosticState.Running,
            "正在用正式课堂规则分析本次实时转写",
            null,
            true,
            "正在根据本次实时转写生成题目…");
        views.Report(working);

        var input = DiagnosticQuestionInput.Create(
            source.TranscriptPreview,
            DateTimeOffset.UtcNow);
        FocusDiagnosticsView final;
        if (input is null)
        {
            final = ReplaceQuestionItem(
                working,
                FocusDiagnosticState.Warning,
                "本次未收到实时转写，因此没有生成题目；不会使用固定素材代替",
                null,
                false,
                "检测完成：存在提醒");
        }
        else
        {
            try
            {
                var generation = await questionGenerator!.GenerateAsync(input, cancellation);
                final = !generation.Accepted
                    ? ReplaceQuestionItem(
                        working,
                        FocusDiagnosticState.Warning,
                        $"本次转写未生成题目：{generation.Detail}",
                        null,
                        false,
                        "检测完成：存在提醒")
                    : ReplaceQuestionItem(
                        working,
                        FocusDiagnosticState.Passed,
                        $"已严格根据本次实时转写生成 · {generation.Detail}",
                        generation.Question,
                        false,
                        FinalHeadline(working.Items, FocusDiagnosticState.Passed));
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                final = ReplaceQuestionItem(
                    working,
                    FocusDiagnosticState.Failed,
                    "根据本次实时转写生成题目超时；请检查免费层配额后重试",
                    null,
                    false,
                    "检测完成：有项目需要处理");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                final = ReplaceQuestionItem(
                    working,
                    FocusDiagnosticState.Failed,
                    "根据本次实时转写生成题目失败；请检查网络、模型权限和配额",
                    null,
                    false,
                    "检测完成：有项目需要处理");
            }
        }

        views.Report(final);
        return BuildSummary(innerResult, final);
    }

    private bool CanGenerate(FocusDiagnosticsView view)
    {
        var keyPassed = view.Items.Any(item =>
            item.Id == FocusDiagnosticId.GeminiApiKey &&
            item.State == FocusDiagnosticState.Passed);
        return keyPassed && questionGenerator is not null;
    }

    private static FocusDiagnosticsView HideFixedProbeQuestion(FocusDiagnosticsView view)
    {
        var keyPassed = view.Items.Any(item =>
            item.Id == FocusDiagnosticId.GeminiApiKey &&
            item.State == FocusDiagnosticState.Passed);
        if (!keyPassed)
        {
            return view with { Question = null };
        }

        var items = view.Items
            .Select(item => item.Id == FocusDiagnosticId.QuestionGeneration &&
                            item.State != FocusDiagnosticState.Waiting
                ? item with
                {
                    State = FocusDiagnosticState.Running,
                    Detail = "等待使用本次实时转写和正式规则生成题目",
                    Preview = null,
                    UpdatedAt = DateTimeOffset.UtcNow
                }
                : item)
            .ToArray();
        return view with
        {
            IsRunning = true,
            Headline = view.IsRunning
                ? view.Headline
                : "正在根据本次实时转写生成题目…",
            Items = items,
            Question = null
        };
    }

    private static FocusDiagnosticsView ReplaceQuestionItem(
        FocusDiagnosticsView source,
        FocusDiagnosticState state,
        string detail,
        DiagnosticQuestionPreview? question,
        bool isRunning,
        string headline)
    {
        var items = source.Items
            .Select(item => item.Id == FocusDiagnosticId.QuestionGeneration
                ? item with
                {
                    State = state,
                    Detail = detail,
                    Preview = question?.Stem,
                    UpdatedAt = DateTimeOffset.UtcNow
                }
                : item)
            .ToArray();
        return source with
        {
            Revision = source.Revision + 1,
            IsRunning = isRunning,
            Headline = headline,
            Items = items,
            Question = question
        };
    }

    private static bool QuestionUsesLiveTranscript(FocusDiagnosticsView view)
    {
        if (view.Question is not { Evidence.Length: > 0 } question ||
            string.IsNullOrWhiteSpace(view.TranscriptPreview))
        {
            return false;
        }

        var transcript = KnowledgeQuestionPolicy.NormalizeForComparison(view.TranscriptPreview);
        var evidence = KnowledgeQuestionPolicy.NormalizeForComparison(question.Evidence);
        return evidence.Length > 0 && transcript.Contains(evidence, StringComparison.Ordinal);
    }

    private static FocusDiagnosticsSummary BuildSummary(
        FocusDiagnosticsSummary source,
        FocusDiagnosticsView final)
    {
        return new FocusDiagnosticsSummary(
            source.StartedAt,
            DateTimeOffset.UtcNow,
            final.Items.Count(item => item.State == FocusDiagnosticState.Passed),
            final.Items.Count(item => item.State == FocusDiagnosticState.Warning),
            final.Items.Count(item => item.State == FocusDiagnosticState.Failed),
            final);
    }

    private static string FinalHeadline(
        IReadOnlyList<FocusDiagnosticItem> currentItems,
        FocusDiagnosticState questionState)
    {
        var states = currentItems
            .Select(item => item.Id == FocusDiagnosticId.QuestionGeneration
                ? questionState
                : item.State)
            .ToArray();
        if (states.Any(state => state == FocusDiagnosticState.Failed))
        {
            return "检测完成：有项目需要处理";
        }

        return states.Any(state => state is FocusDiagnosticState.Warning or FocusDiagnosticState.Skipped)
            ? "检测完成：存在提醒"
            : "所有环节正常";
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
