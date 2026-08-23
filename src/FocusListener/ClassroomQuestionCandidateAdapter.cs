using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace FocusListener;

internal sealed class ClassroomQuestionCandidateAdapter(
    GeminiFocusOptions options,
    ISessionClock clock,
    ClassroomExperienceControl experience) : IQuestionCandidateSource, IQuestionCandidateSourceStatus
{
    private readonly Channel<QuestionSourceStatus> _status = Channel.CreateUnbounded<QuestionSourceStatus>();

    public async IAsyncEnumerable<ResetQuestionCandidate> AutomaticAsync(
        SessionStart start,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        using var generator = new GeminiRestatementQuestionGenerator(options);
        var input = Channel.CreateUnbounded<TranscriptUnit>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        var pump = PumpClassroomTranscriptionAsync(input.Writer, cancellation);
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
                var window = windows.Build(latest) with { AudioSource = latest.AudioSource };
                var evaluation = await GenerateWithRetryAsync(generator, window, usedConcepts, cancellation);
                if (evaluation?.Candidate is not { } candidate)
                {
                    if (!string.IsNullOrWhiteSpace(evaluation?.RejectionReason))
                    {
                        _status.Writer.TryWrite(new QuestionSourceStatus(
                            SessionHealth.Healthy,
                            T("继续监听，等待完整、可复述的课堂知识点。", "Listening continues while waiting for a complete, restatable knowledge point."),
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

    private async Task PumpClassroomTranscriptionAsync(
        ChannelWriter<TranscriptUnit> output,
        CancellationToken cancellation)
    {
        Exception? terminalFailure = null;
        var consecutiveFailures = 0;
        var pausedReported = false;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var snapshot = experience.Snapshot();
                if (!snapshot.TranscriptionEnabled)
                {
                    if (!pausedReported)
                    {
                        pausedReported = true;
                        experience.ReportTranscript(new LiveTranscriptPreview(
                            string.Empty,
                            string.Empty,
                            LiveTranscriptState.Paused,
                            T("实时转写已关闭", "Realtime transcription is off"),
                            clock.UtcNow));
                        _status.Writer.TryWrite(new QuestionSourceStatus(
                            SessionHealth.Degraded,
                            T("实时转写已关闭，自动出题已暂停。", "Realtime transcription is off; automatic questions are paused."),
                            "TranscriptionPaused"));
                    }

                    await clock.Delay(TimeSpan.FromMilliseconds(250), cancellation);
                    continue;
                }

                pausedReported = false;
                using var run = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellation,
                    snapshot.ConfigurationChanged);
                try
                {
                    var audio = new WindowsClassroomAudioAdapter(snapshot.Audio, experience);
                    var transcriber = new GeminiLiveTranscriptionAdapter(options, clock, experience);
                    await foreach (var unit in transcriber
                                       .TranscribeAsync(audio.CaptureAsync(run.Token), run.Token)
                                       .WithCancellation(run.Token))
                    {
                        await output.WriteAsync(unit, run.Token);
                    }

                    if (!run.IsCancellationRequested)
                    {
                        throw new IOException("Gemini Live 转写连接提前结束。");
                    }

                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (
                    snapshot.ConfigurationChanged.IsCancellationRequested && !cancellation.IsCancellationRequested)
                {
                    consecutiveFailures = 0;
                    experience.ReportTranscript(new LiveTranscriptPreview(
                        string.Empty,
                        string.Empty,
                        LiveTranscriptState.Connecting,
                        T("音频设置已更新，正在重新连接…", "Audio settings updated. Reconnecting…"),
                        clock.UtcNow));
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException && !cancellation.IsCancellationRequested)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures <= 3)
                    {
                        var seconds = Math.Min(4, 1 << (consecutiveFailures - 1));
                        experience.ReportTranscript(new LiveTranscriptPreview(
                            string.Empty,
                            string.Empty,
                            LiveTranscriptState.Reconnecting,
                            T($"转写连接中断，正在进行第 {consecutiveFailures}/3 次重连…", $"Transcription disconnected. Reconnecting {consecutiveFailures}/3…"),
                            clock.UtcNow));
                        _status.Writer.TryWrite(new QuestionSourceStatus(
                            SessionHealth.Degraded,
                            T($"转写暂时中断，正在自动重连（{consecutiveFailures}/3）。", $"Transcription is interrupted. Reconnecting automatically ({consecutiveFailures}/3)."),
                            "TranscriptionReconnecting"));
                        await clock.Delay(TimeSpan.FromSeconds(seconds), cancellation);
                        continue;
                    }

                    experience.ReportTranscript(new LiveTranscriptPreview(
                        string.Empty,
                        string.Empty,
                        LiveTranscriptState.Failed,
                        T("有声音但转写未返回；请点击“重试转写”", "Audio is present but no transcript returned. Choose Retry transcription."),
                        clock.UtcNow));
                    _status.Writer.TryWrite(new QuestionSourceStatus(
                        SessionHealth.Degraded,
                        T("有声音但转写未返回；请检查 Gemini、设备或点击重试。", "Audio is present but no transcript returned. Check Gemini and the selected device, or retry."),
                        "TranscriptionRetryRequired"));
                    consecutiveFailures = 0;
                    await WaitForRetryAsync(snapshot.ConfigurationChanged, cancellation);
                }
            }
        }
        catch (Exception exception)
        {
            terminalFailure = exception;
            throw;
        }
        finally
        {
            output.TryComplete(terminalFailure);
        }
    }

    private static async Task WaitForRetryAsync(
        CancellationToken retry,
        CancellationToken cancellation)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(retry, cancellation);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, wait.Token);
        }
        catch (OperationCanceledException) when (retry.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
        }
    }

    private static string T(string zh, string en) => ProductText.Choose(zh, en);

    private async ValueTask<TranscriptUnit> DebounceAsync(
        TranscriptUnit latest,
        TranscriptWindowBuffer windows,
        ChannelReader<TranscriptUnit> input,
        CancellationToken cancellation)
    {
        while (true)
        {
            var pause = clock.Delay(TimeSpan.FromSeconds(1.2), cancellation);
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
                    await clock.Delay(TimeSpan.FromSeconds(1), cancellation);
                }
            }
        }

        _status.Writer.TryWrite(new QuestionSourceStatus(
            SessionHealth.Degraded,
            T("题目生成暂不可用，字幕与监听仍在继续。", "Question generation is unavailable; subtitles and listening continue."),
            "GenerationPaused60Seconds"));
        await clock.Delay(TimeSpan.FromSeconds(60), cancellation);
        _status.Writer.TryWrite(new QuestionSourceStatus(
            SessionHealth.Healthy,
            T("已恢复题目生成，继续监听。", "Question generation recovered. Listening continues."),
            "GenerationResumed"));

        try
        {
            return await generator.EvaluateAsync(window, usedConcepts, TriggerKind.Automatic, cancellation);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return KnowledgeQuestionEvaluation.Transient(T("网络、配额或模型暂不可用", "The network, quota, or model is temporarily unavailable"));
        }
    }
}
