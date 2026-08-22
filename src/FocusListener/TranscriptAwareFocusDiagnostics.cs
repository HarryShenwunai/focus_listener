namespace FocusListener;

public static class TranscriptAwareFocusDiagnosticsFactory
{
    public static IFocusDiagnostics Create(
        GeminiFocusOptions? gemini,
        string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var inner = FocusDiagnosticsFactory.Create(gemini, outputDirectory);
        IDiagnosticQuestionGenerator? generator = gemini is null
            ? null
            : new GeminiDiagnosticQuestionGenerator(gemini);
        return new TranscriptAwareFocusDiagnostics(inner, generator);
    }
}

internal interface IDiagnosticQuestionGenerator
{
    ValueTask<DiagnosticQuestionPreview?> TryGenerateAsync(
        TranscriptUnit input,
        CancellationToken cancellation);
}

internal sealed class GeminiDiagnosticQuestionGenerator(GeminiFocusOptions options)
    : IDiagnosticQuestionGenerator
{
    public async ValueTask<DiagnosticQuestionPreview?> TryGenerateAsync(
        TranscriptUnit input,
        CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var generator = new GeminiRestatementQuestionGenerator(options);
        var candidate = await generator.TryGenerateAsync(
            input,
            TriggerKind.Automatic,
            timeout.Token);
        return candidate is null
            ? null
            : new DiagnosticQuestionPreview(
                candidate.Question.Stem,
                candidate.Question.Choices
                    .Select(choice => $"{choice.Id.Value}  {choice.Text}")
                    .ToArray(),
                candidate.Evidence.Excerpt);
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
            "正在严格根据本次实时转写生成题目",
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
                var question = await questionGenerator!.TryGenerateAsync(input, cancellation);
                final = question is null
                    ? ReplaceQuestionItem(
                        working,
                        FocusDiagnosticState.Warning,
                        "本次转写不包含可独立复述的小学行程知识点，因此没有生成题目",
                        null,
                        false,
                        "检测完成：存在提醒")
                    : ReplaceQuestionItem(
                        working,
                        FocusDiagnosticState.Passed,
                        "已严格根据本次实时转写生成，并通过逐字证据校验",
                        question,
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
                    Detail = "等待使用本次实时转写生成题目",
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
