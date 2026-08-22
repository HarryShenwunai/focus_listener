namespace FocusListener;

public enum FocusDiagnosticId
{
    MicrophoneLevel,
    SystemSoundLevel,
    AudioRoute,
    GeminiApiKey,
    GeminiLive,
    LiveTranscription,
    QuestionGeneration,
    SqliteWrite,
    CsvExport
}

public enum FocusDiagnosticState
{
    Waiting,
    Running,
    Passed,
    Warning,
    Failed,
    Skipped
}

public sealed record FocusDiagnosticItem(
    FocusDiagnosticId Id,
    string Title,
    FocusDiagnosticState State,
    string Detail,
    double? Level,
    string? Preview,
    DateTimeOffset UpdatedAt);

public sealed record DiagnosticQuestionPreview(
    string Stem,
    IReadOnlyList<string> Choices,
    string Evidence);

public sealed record FocusDiagnosticsView(
    long Revision,
    bool IsRunning,
    string Headline,
    IReadOnlyList<FocusDiagnosticItem> Items,
    string TranscriptPreview,
    DiagnosticQuestionPreview? Question);

public sealed record FocusDiagnosticsSummary(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int Passed,
    int Warnings,
    int Failed,
    FocusDiagnosticsView FinalView)
{
    public bool Succeeded => Failed == 0;
}

public interface IFocusDiagnostics
{
    Task<FocusDiagnosticsSummary> RunAsync(
        IProgress<FocusDiagnosticsView> views,
        CancellationToken cancellation = default);
}

internal sealed record FocusDiagnosticSignal(
    FocusDiagnosticId Id,
    FocusDiagnosticState State,
    string Detail,
    double? Level = null,
    string? Preview = null,
    DiagnosticQuestionPreview? Question = null);

internal interface IFocusDiagnosticRuntime
{
    Task RunAsync(
        IProgress<FocusDiagnosticSignal> progress,
        CancellationToken cancellation);
}

internal sealed class FocusDiagnostics(IFocusDiagnosticRuntime runtime) : IFocusDiagnostics
{
    private static readonly (FocusDiagnosticId Id, string Title)[] Definitions =
    [
        (FocusDiagnosticId.MicrophoneLevel, "麦克风音量"),
        (FocusDiagnosticId.SystemSoundLevel, "系统声音量"),
        (FocusDiagnosticId.AudioRoute, "当前采用音频"),
        (FocusDiagnosticId.GeminiApiKey, "Gemini Key"),
        (FocusDiagnosticId.GeminiLive, "Gemini Live"),
        (FocusDiagnosticId.LiveTranscription, "实时转写"),
        (FocusDiagnosticId.QuestionGeneration, "测试题生成"),
        (FocusDiagnosticId.SqliteWrite, "SQLite 写入"),
        (FocusDiagnosticId.CsvExport, "CSV 导出")
    ];

    private readonly object _gate = new();
    private readonly Dictionary<FocusDiagnosticId, FocusDiagnosticItem> _items = [];
    private long _revision;
    private int _running;
    private string _transcript = string.Empty;
    private DiagnosticQuestionPreview? _question;

    public async Task<FocusDiagnosticsSummary> RunAsync(
        IProgress<FocusDiagnosticsView> views,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(views);
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            throw new InvalidOperationException("A diagnostics run is already active.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        Reset(startedAt);
        views.Report(Snapshot(true, "准备检测…"));
        var bridge = new InlineProgress<FocusDiagnosticSignal>(signal => Apply(signal, views));

        try
        {
            await runtime.RunAsync(bridge, cancellation);
            CompleteUnfinished("检测未返回结果", FocusDiagnosticState.Warning);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            CompleteUnfinished("检测已停止", FocusDiagnosticState.Skipped);
        }
        catch (Exception exception)
        {
            CompleteUnfinished($"检测流程意外中断（{exception.GetType().Name}）", FocusDiagnosticState.Failed);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }

        var completedAt = DateTimeOffset.UtcNow;
        var final = Snapshot(false, FinalHeadline());
        views.Report(final);
        return new FocusDiagnosticsSummary(
            startedAt,
            completedAt,
            final.Items.Count(item => item.State == FocusDiagnosticState.Passed),
            final.Items.Count(item => item.State == FocusDiagnosticState.Warning),
            final.Items.Count(item => item.State == FocusDiagnosticState.Failed),
            final);
    }

    private void Reset(DateTimeOffset now)
    {
        lock (_gate)
        {
            _items.Clear();
            foreach (var definition in Definitions)
            {
                _items.Add(definition.Id, new FocusDiagnosticItem(
                    definition.Id,
                    definition.Title,
                    FocusDiagnosticState.Waiting,
                    "等待检测",
                    null,
                    null,
                    now));
            }

            _transcript = string.Empty;
            _question = null;
            _revision = 0;
        }
    }

    private void Apply(FocusDiagnosticSignal signal, IProgress<FocusDiagnosticsView> views)
    {
        FocusDiagnosticsView snapshot;
        lock (_gate)
        {
            var current = _items[signal.Id];
            _items[signal.Id] = current with
            {
                State = signal.State,
                Detail = signal.Detail,
                Level = signal.Level ?? current.Level,
                Preview = signal.Preview ?? current.Preview,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            if (signal.Id == FocusDiagnosticId.LiveTranscription && signal.Preview is { } transcript)
            {
                _transcript = transcript;
            }

            if (signal.Question is not null)
            {
                _question = signal.Question;
            }

            snapshot = SnapshotLocked(true, RunningHeadline(signal.Id));
        }

        views.Report(snapshot);
    }

    private void CompleteUnfinished(string detail, FocusDiagnosticState state)
    {
        lock (_gate)
        {
            foreach (var definition in Definitions)
            {
                var item = _items[definition.Id];
                if (item.State is FocusDiagnosticState.Waiting or FocusDiagnosticState.Running)
                {
                    _items[definition.Id] = item with
                    {
                        State = state,
                        Detail = detail,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                }
            }
        }
    }

    private FocusDiagnosticsView Snapshot(bool isRunning, string headline)
    {
        lock (_gate)
        {
            return SnapshotLocked(isRunning, headline);
        }
    }

    private FocusDiagnosticsView SnapshotLocked(bool isRunning, string headline)
    {
        var ordered = Definitions.Select(definition => _items[definition.Id]).ToArray();
        return new FocusDiagnosticsView(
            ++_revision,
            isRunning,
            headline,
            ordered,
            _transcript,
            _question);
    }

    private string FinalHeadline()
    {
        lock (_gate)
        {
            if (_items.Values.Any(item => item.State == FocusDiagnosticState.Failed))
            {
                return "检测完成：有项目需要处理";
            }

            if (_items.Values.Any(item => item.State is FocusDiagnosticState.Warning or FocusDiagnosticState.Skipped))
            {
                return "检测完成：存在提醒";
            }

            return "所有环节正常";
        }
    }

    private static string RunningHeadline(FocusDiagnosticId id) => id switch
    {
        FocusDiagnosticId.MicrophoneLevel or
        FocusDiagnosticId.SystemSoundLevel or
        FocusDiagnosticId.AudioRoute => "正在采集音频，请说出页面上的测试句…",
        FocusDiagnosticId.GeminiApiKey => "正在验证 Gemini Key…",
        FocusDiagnosticId.GeminiLive => "正在连接 Gemini Live…",
        FocusDiagnosticId.LiveTranscription => "正在等待实时转写…",
        FocusDiagnosticId.QuestionGeneration => "正在生成测试题…",
        FocusDiagnosticId.SqliteWrite or FocusDiagnosticId.CsvExport => "正在检查本地数据链路…",
        _ => "正在检测…"
    };

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
