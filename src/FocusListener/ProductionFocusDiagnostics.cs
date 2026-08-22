using System.Text;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Data.Sqlite;

namespace FocusListener;

public static class FocusDiagnosticsFactory
{
    public static IFocusDiagnostics Create(
        GeminiFocusOptions? gemini,
        string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        return new FocusDiagnostics(new WindowsGeminiDiagnosticRuntime(
            gemini,
            Path.GetFullPath(outputDirectory),
            TimeSpan.FromSeconds(10)));
    }
}

internal sealed class WindowsGeminiDiagnosticRuntime(
    GeminiFocusOptions? gemini,
    string outputDirectory,
    TimeSpan audioDuration) : IFocusDiagnosticRuntime
{
    private const string QuestionTestTranscript =
        "相遇时间是两个运动物体同时出发后，从开始运动到彼此相遇所经历的时间。";

    private readonly WindowsDiagnosticAudioSource _audio = new();

    public async Task RunAsync(
        IProgress<FocusDiagnosticSignal> progress,
        CancellationToken cancellation)
    {
        var keyValid = await ProbeGeminiKeyAsync(progress, cancellation);
        await ProbeAudioAndLiveAsync(progress, keyValid, cancellation);
        await ProbeQuestionGenerationAsync(progress, keyValid, cancellation);
        await ProbeStorageAsync(progress, cancellation);
    }

    private async Task<bool> ProbeGeminiKeyAsync(
        IProgress<FocusDiagnosticSignal> progress,
        CancellationToken cancellation)
    {
        progress.Report(new FocusDiagnosticSignal(
            FocusDiagnosticId.GeminiApiKey,
            FocusDiagnosticState.Running,
            "正在调用 Flash-Lite 验证凭据"));

        if (gemini is null || string.IsNullOrWhiteSpace(gemini.ApiKey))
        {
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.GeminiApiKey,
                FocusDiagnosticState.Failed,
                "尚未配置 Key；回到悬浮窗点击“模拟课堂”文字进行配置"));
            return false;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var client = new Client(apiKey: gemini.ApiKey);
            var response = await client.Models.GenerateContentAsync(
                gemini.QuestionModel,
                "只回复 OK",
                new GenerateContentConfig
                {
                    Temperature = 0,
                    MaxOutputTokens = 8
                },
                timeout.Token);
            if (string.IsNullOrWhiteSpace(response.Text))
            {
                progress.Report(new FocusDiagnosticSignal(
                    FocusDiagnosticId.GeminiApiKey,
                    FocusDiagnosticState.Failed,
                    "凭据请求已返回，但模型响应为空"));
                return false;
            }

            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.GeminiApiKey,
                FocusDiagnosticState.Passed,
                $"有效 · {gemini.QuestionModel}"));
            return true;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.GeminiApiKey,
                FocusDiagnosticState.Failed,
                "验证超时；检查网络后重试"));
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.GeminiApiKey,
                FocusDiagnosticState.Failed,
                "Key 无效、免费层配额不可用，或当前网络无法访问 Gemini"));
            return false;
        }
    }

    private async Task<string> ProbeAudioAndLiveAsync(
        IProgress<FocusDiagnosticSignal> progress,
        bool keyValid,
        CancellationToken cancellation)
    {
        var audio = _audio.CaptureAsync(progress, audioDuration, cancellation);
        if (!keyValid || gemini is null)
        {
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.GeminiLive,
                FocusDiagnosticState.Skipped,
                "先修复 Gemini Key，再测试 Live"));
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.LiveTranscription,
                FocusDiagnosticState.Skipped,
                "未连接 Live，仍会完成本地双路音频检测"));
            await DrainAudioAsync(audio, cancellation);
            return string.Empty;
        }

        progress.Report(new FocusDiagnosticSignal(
            FocusDiagnosticId.GeminiLive,
            FocusDiagnosticState.Running,
            $"正在连接 {gemini.LiveModel}"));
        progress.Report(new FocusDiagnosticSignal(
            FocusDiagnosticId.LiveTranscription,
            FocusDiagnosticState.Running,
            "连接后请朗读页面上的测试句"));

        AsyncSession session;
        using var client = new Client(apiKey: gemini.ApiKey);
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            session = await client.Live.ConnectAsync(
                gemini.LiveModel,
                BuildLiveConfig(),
                connectTimeout.Token);
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.GeminiLive,
                FocusDiagnosticState.Passed,
                $"已连接 · {gemini.LiveModel}"));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.GeminiLive,
                FocusDiagnosticState.Failed,
                "连接超时；检查网络、模型可用性和免费层配额"));
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.LiveTranscription,
                FocusDiagnosticState.Skipped,
                "Live 未连接，无法转写"));
            await DrainAudioAsync(audio, cancellation);
            return string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.GeminiLive,
                FocusDiagnosticState.Failed,
                "连接失败；检查 Live 模型权限、配额和网络"));
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.LiveTranscription,
                FocusDiagnosticState.Skipped,
                "Live 未连接，无法转写"));
            await DrainAudioAsync(audio, cancellation);
            return string.Empty;
        }

        await using (session)
        {
            var transcript = new StringBuilder();
            Exception? streamFailure = null;
            var sendTask = SendAudioAsync(session, audio, cancellation);
            DateTimeOffset? receiveTailEndsAt = null;

            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    if (sendTask.IsCompleted && receiveTailEndsAt is null)
                    {
                        receiveTailEndsAt = DateTimeOffset.UtcNow.AddSeconds(3);
                    }

                    if (receiveTailEndsAt is { } tail && DateTimeOffset.UtcNow >= tail)
                    {
                        break;
                    }

                    using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    receiveTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                    try
                    {
                        var message = await session.ReceiveAsync(receiveTimeout.Token);
                        if (message is null)
                        {
                            break;
                        }

                        var content = message.ServerContent;
                        var update = content?.InputTranscription?.Text;
                        if (!string.IsNullOrWhiteSpace(update))
                        {
                            MergeTranscript(transcript, update);
                            progress.Report(new FocusDiagnosticSignal(
                                FocusDiagnosticId.LiveTranscription,
                                FocusDiagnosticState.Running,
                                "正在接收文字",
                                Preview: transcript.ToString()));
                        }

                        if (sendTask.IsCompleted &&
                            (content?.InputTranscription?.Finished == true || content?.TurnComplete == true))
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                    {
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                streamFailure = exception;
            }

            try
            {
                await sendTask;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                streamFailure ??= exception;
            }

            cancellation.ThrowIfCancellationRequested();
            try
            {
                await session.CloseAsync();
            }
            catch (Exception exception)
            {
                streamFailure ??= exception;
            }

            if (streamFailure is not null)
            {
                progress.Report(new FocusDiagnosticSignal(
                    FocusDiagnosticId.GeminiLive,
                    FocusDiagnosticState.Warning,
                    "已连接，但音频流在检测途中中断"));
            }

            var finalTranscript = transcript.ToString().Trim();
            if (finalTranscript.Length == 0)
            {
                progress.Report(new FocusDiagnosticSignal(
                    FocusDiagnosticId.LiveTranscription,
                    FocusDiagnosticState.Warning,
                    "Live 已连接，但未收到文字；重试并清晰朗读测试句"));
            }
            else
            {
                progress.Report(new FocusDiagnosticSignal(
                    FocusDiagnosticId.LiveTranscription,
                    FocusDiagnosticState.Passed,
                    $"收到 {finalTranscript.Length} 个字符",
                    Preview: finalTranscript));
            }

            return finalTranscript;
        }
    }

    private async Task ProbeQuestionGenerationAsync(
        IProgress<FocusDiagnosticSignal> progress,
        bool keyValid,
        CancellationToken cancellation)
    {
        if (!keyValid || gemini is null)
        {
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.QuestionGeneration,
                FocusDiagnosticState.Skipped,
                "先修复 Gemini Key，再生成测试题"));
            return;
        }

        progress.Report(new FocusDiagnosticSignal(
            FocusDiagnosticId.QuestionGeneration,
            FocusDiagnosticState.Running,
            $"正在用固定知识点测试 {gemini.QuestionModel}"));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var generator = new GeminiRestatementQuestionGenerator(gemini);
            var candidate = await generator.TryGenerateAsync(
                new TranscriptUnit(QuestionTestTranscript, DateTimeOffset.UtcNow, TimeSpan.Zero),
                TriggerKind.Automatic,
                timeout.Token);
            if (candidate is null)
            {
                progress.Report(new FocusDiagnosticSignal(
                    FocusDiagnosticId.QuestionGeneration,
                    FocusDiagnosticState.Failed,
                    "模型有响应，但结果未通过三选一、无计算或课堂证据校验"));
                return;
            }

            var preview = new DiagnosticQuestionPreview(
                candidate.Question.Stem,
                candidate.Question.Choices
                    .Select(choice => $"{choice.Id.Value}  {choice.Text}")
                    .ToArray(),
                candidate.Evidence.Excerpt);
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.QuestionGeneration,
                FocusDiagnosticState.Passed,
                $"已生成并通过本地规则 · 正确选项 {candidate.CorrectChoice.Value}",
                Preview: candidate.Question.Stem,
                Question: preview));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.QuestionGeneration,
                FocusDiagnosticState.Failed,
                "生成超时；检查免费层配额后重试"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.QuestionGeneration,
                FocusDiagnosticState.Failed,
                "题目生成请求失败；检查网络、模型权限和配额"));
        }
    }

    private async Task ProbeStorageAsync(
        IProgress<FocusDiagnosticSignal> progress,
        CancellationToken cancellation)
    {
        progress.Report(new FocusDiagnosticSignal(
            FocusDiagnosticId.SqliteWrite,
            FocusDiagnosticState.Running,
            "正在写入隔离的诊断数据库"));
        progress.Report(new FocusDiagnosticSignal(
            FocusDiagnosticId.CsvExport,
            FocusDiagnosticState.Running,
            "等待 SQLite 写入完成"));

        Directory.CreateDirectory(outputDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var databasePath = Path.Combine(outputDirectory, $"system-check-{stamp}.db");
        var csvPath = Path.Combine(outputDirectory, $"system-check-{stamp}.csv");
        var sessionId = SessionId.New();
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            var journal = new SqliteSessionJournalAdapter(databasePath);
            await journal.InitializeAsync(
                sessionId,
                new SessionStart(ClassroomKind.InPerson, TimeSpan.FromSeconds(1)),
                startedAt,
                cancellation);
            await journal.AppendAsync(
                new SessionEvent(sessionId, DateTimeOffset.UtcNow, "SystemDiagnosticProbe", new { Result = "ok" }),
                cancellation);
            await journal.CompleteAsync(
                new SessionSummary(
                    sessionId,
                    startedAt,
                    DateTimeOffset.UtcNow,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    null),
                cancellation);

            var count = await CountDiagnosticRowsAsync(databasePath, sessionId, cancellation);
            if (count != 1)
            {
                throw new InvalidDataException("The diagnostic row could not be read back.");
            }

            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.SqliteWrite,
                FocusDiagnosticState.Passed,
                $"写入并回读 1 条事件 · {Path.GetFileName(databasePath)}",
                Preview: databasePath));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.SqliteWrite,
                FocusDiagnosticState.Failed,
                "SQLite 写入或回读失败；检查本地数据目录权限"));
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.CsvExport,
                FocusDiagnosticState.Skipped,
                "SQLite 未通过，未执行 CSV 导出"));
            return;
        }

        try
        {
            await SessionCsvExporter.ExportAsync(databasePath, csvPath, cancellation);
            var lines = await File.ReadAllLinesAsync(csvPath, cancellation);
            if (lines.Length < 2 || new FileInfo(csvPath).Length == 0)
            {
                throw new InvalidDataException("The exported CSV contains no diagnostic row.");
            }

            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.CsvExport,
                FocusDiagnosticState.Passed,
                $"导出 {lines.Length - 1} 条数据 · {Path.GetFileName(csvPath)}",
                Preview: csvPath));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            progress.Report(new FocusDiagnosticSignal(
                FocusDiagnosticId.CsvExport,
                FocusDiagnosticState.Failed,
                "CSV 导出或回读失败；检查本地数据目录权限"));
        }
    }

    private static LiveConnectConfig BuildLiveConfig() => new()
    {
        ResponseModalities = [Modality.Audio],
        InputAudioTranscription = new AudioTranscriptionConfig(),
        SystemInstruction = new Content
        {
            Parts =
            [
                new Part
                {
                    Text = "只监听并转写输入课堂音频，不向用户提问，也不要进行口头回复。保留中英文原词。"
                }
            ]
        }
    };

    private static async Task SendAudioAsync(
        AsyncSession session,
        IAsyncEnumerable<PcmAudioFrame> audio,
        CancellationToken cancellation)
    {
        await foreach (var frame in audio.WithCancellation(cancellation))
        {
            await session.SendRealtimeInputAsync(
                new LiveSendRealtimeInputParameters
                {
                    Audio = new Blob
                    {
                        Data = frame.Pcm16,
                        MimeType = "audio/pcm;rate=16000"
                    }
                },
                cancellation);
        }

        await session.SendRealtimeInputAsync(
            new LiveSendRealtimeInputParameters { AudioStreamEnd = true },
            cancellation);
    }

    private static async Task DrainAudioAsync(
        IAsyncEnumerable<PcmAudioFrame> audio,
        CancellationToken cancellation)
    {
        await foreach (var _ in audio.WithCancellation(cancellation))
        {
        }
    }

    private static void MergeTranscript(StringBuilder transcript, string update)
    {
        var normalized = update.Trim();
        var current = transcript.ToString();
        if (normalized.StartsWith(current, StringComparison.Ordinal))
        {
            transcript.Clear();
            transcript.Append(normalized);
        }
        else if (!current.EndsWith(normalized, StringComparison.Ordinal))
        {
            if (transcript.Length > 0 && !char.IsPunctuation(transcript[^1]))
            {
                transcript.Append(' ');
            }

            transcript.Append(normalized);
        }
    }

    private static async Task<long> CountDiagnosticRowsAsync(
        string databasePath,
        SessionId sessionId,
        CancellationToken cancellation)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM session_events WHERE session_id = $session AND event_type = 'SystemDiagnosticProbe';";
        command.Parameters.AddWithValue("$session", sessionId.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellation));
    }
}
