using System.Threading.Channels;

namespace FocusListener;

internal sealed class FocusSession : IFocusSession
{
    private readonly IQuestionCandidateSource _source;
    private readonly ISessionJournal _journal;
    private readonly ISessionClock _clock;
    private readonly SessionTiming _timing;
    private readonly Channel<SessionMessage> _mailbox;
    private readonly Dictionary<IntentId, IntentOutcome> _intentResults = [];

    private int _runState;
    private volatile bool _running;
    private SessionStart? _start;
    private SessionId _sessionId;
    private DateTimeOffset _startedAt;
    private IProgress<SessionView>? _views;
    private long _revision;
    private SessionSurfaceKind _surface;
    private SessionHealth _health = SessionHealth.Healthy;
    private CurrentQuestion? _current;
    private ResetQuestionCandidate? _queued;
    private string? _notice;
    private SessionSummary? _summary;
    private byte? _attentionRating;
    private int _questionsShown;
    private int _questionsQueued;
    private int _capacityDrops;
    private int _answers;
    private int _correctAnswers;
    private int _invalidQuestions;

    internal FocusSession(
        IQuestionCandidateSource source,
        ISessionJournal journal,
        ISessionClock clock,
        SessionTiming? timing = null)
    {
        _source = source;
        _journal = journal;
        _clock = clock;
        _timing = timing ?? SessionTiming.Default;
        _mailbox = Channel.CreateBounded<SessionMessage>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public async Task<SessionSummary> RunAsync(
        SessionStart start,
        IProgress<SessionView> views,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(views);
        if (start.PlannedDuration < TimeSpan.FromMinutes(10) || start.PlannedDuration > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(nameof(start), "A Focus Session must be planned for 10–15 minutes.");
        }

        if (Interlocked.CompareExchange(ref _runState, 1, 0) != 0)
        {
            throw new InvalidOperationException("This FocusSession instance can run only once.");
        }

        _start = start;
        _sessionId = SessionId.New();
        _startedAt = _clock.UtcNow;
        _views = views;
        _surface = SessionSurfaceKind.Listening;
        _notice = "正在监听课堂内容。";

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _running = true;

        try
        {
            await _journal.InitializeAsync(_sessionId, start, _startedAt, lifetime.Token);
            Publish();

            var automaticPump = PumpAutomaticCandidatesAsync(start, lifetime.Token);
            var timerPump = PumpTimerAsync(lifetime.Token);

            while (_surface is not SessionSurfaceKind.Completed and not SessionSurfaceKind.Failed)
            {
                var message = await _mailbox.Reader.ReadAsync(lifetime.Token);
                await HandleMessageAsync(message, lifetime.Token);
            }

            lifetime.Cancel();
            await IgnoreCancellationAsync(automaticPump, timerPump);
            return _summary ?? BuildSummary(_clock.UtcNow);
        }
        finally
        {
            _running = false;
            _mailbox.Writer.TryComplete();
        }
    }

    public async ValueTask<IntentOutcome> ApplyAsync(
        LearnerIntent intent,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (!_running)
        {
            return IntentOutcome.Reject("NotRunning", "课堂会话尚未开始或已经结束。");
        }

        var completion = new TaskCompletionSource<IntentOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _mailbox.Writer.WriteAsync(new IntentMessage(intent, completion), cancellation);
            return await completion.Task.WaitAsync(cancellation);
        }
        catch (ChannelClosedException)
        {
            return IntentOutcome.Reject("NotRunning", "课堂会话已经结束。");
        }
    }

    private async Task PumpAutomaticCandidatesAsync(SessionStart start, CancellationToken cancellation)
    {
        try
        {
            await foreach (var candidate in _source.AutomaticAsync(start, cancellation).WithCancellation(cancellation))
            {
                await _mailbox.Writer.WriteAsync(new CandidateMessage(candidate), cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _mailbox.Writer.WriteAsync(new SourceFaultMessage(exception), cancellation);
        }
    }

    private async Task PumpTimerAsync(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await _clock.Delay(_timing.TickInterval, cancellation);
                await _mailbox.Writer.WriteAsync(new TickMessage(_clock.UtcNow), cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private async ValueTask HandleMessageAsync(SessionMessage message, CancellationToken cancellation)
    {
        switch (message)
        {
            case CandidateMessage candidate:
                await AdmitAsync(candidate.Candidate, cancellation);
                break;
            case TickMessage tick:
                await HandleTickAsync(tick.Now, cancellation);
                break;
            case SourceFaultMessage fault:
                _health = SessionHealth.Degraded;
                _notice = "自动题源暂时不可用，课堂会话仍可结束。";
                await JournalAsync("CandidateSourceDegraded", new { fault.Exception.Message }, cancellation);
                Publish();
                break;
            case IntentMessage intent:
                await HandleIntentMessageAsync(intent, cancellation);
                break;
        }
    }

    private async ValueTask HandleIntentMessageAsync(IntentMessage message, CancellationToken cancellation)
    {
        if (_intentResults.TryGetValue(message.Intent.Id, out var previous))
        {
            message.Completion.TrySetResult(previous);
            return;
        }

        IntentOutcome outcome;
        try
        {
            outcome = await HandleIntentAsync(message.Intent, cancellation);
        }
        catch (Exception exception)
        {
            outcome = IntentOutcome.Reject("InternalError", "操作未完成，课堂会话仍在运行。");
            await JournalAsync("IntentFailed", new { Intent = message.Intent.GetType().Name, exception.Message }, cancellation);
        }

        _intentResults[message.Intent.Id] = outcome;
        message.Completion.TrySetResult(outcome);
    }

    private async ValueTask<IntentOutcome> HandleIntentAsync(LearnerIntent intent, CancellationToken cancellation)
    {
        switch (intent)
        {
            case RequestManualTrigger:
                return await HandleManualTriggerAsync(cancellation);
            case SelectAnswer answer:
                return await HandleAnswerAsync(answer, cancellation);
            case ExtendThinking extend:
                return await HandleExtendAsync(extend, cancellation);
            case OpenPending open:
                return await HandleOpenPendingAsync(open, cancellation);
            case CollapsePending collapse:
                return await HandleCollapsePendingAsync(collapse, cancellation);
            case ReportQuestionIssue issue:
                return await HandleQuestionIssueAsync(issue, cancellation);
            case EndSession:
                return await HandleEndSessionAsync(cancellation);
            case RateAttentionReset rating:
                return await HandleRatingAsync(rating.Rating, cancellation);
            case SkipAttentionRating:
                return await HandleRatingAsync(null, cancellation);
            default:
                return IntentOutcome.Reject("UnknownIntent", "未知操作。");
        }
    }

    private async ValueTask<IntentOutcome> HandleManualTriggerAsync(CancellationToken cancellation)
    {
        if (_surface is SessionSurfaceKind.AttentionRating or SessionSurfaceKind.Completed or SessionSurfaceKind.Failed)
        {
            return IntentOutcome.Reject("NotListening", "课堂收音已经停止。");
        }

        if (_current is not null && _queued is not null)
        {
            return IntentOutcome.Reject("QueueFull", "当前题和排队题均已占用，请稍后再试。");
        }

        var candidate = await _source.RequestManualAsync(_start!, cancellation);
        if (candidate is null)
        {
            _notice = "暂时没有适合的问题。";
            Publish();
            return IntentOutcome.Reject("NoEligibleUnit", _notice);
        }

        await AdmitAsync(candidate, cancellation);
        return IntentOutcome.Accept("ManualTriggerAccepted", "已根据最近的合格知识单元请求问题。");
    }

    private async ValueTask AdmitAsync(ResetQuestionCandidate candidate, CancellationToken cancellation)
    {
        if (_surface is SessionSurfaceKind.AttentionRating or SessionSurfaceKind.Completed or SessionSurfaceKind.Failed)
        {
            return;
        }

        if (_current is null && _surface == SessionSurfaceKind.Listening)
        {
            await ShowQuestionAsync(candidate, cancellation);
            return;
        }

        if (_queued is null)
        {
            _queued = candidate;
            _questionsQueued++;
            await JournalAsync("QuestionQueued", new
            {
                candidate.EligibleUnitId,
                Question = candidate.Question.Id.ToString(),
                candidate.Trigger,
                ExpiresAt = candidate.RecognizedAt + _timing.QueueLifetime
            }, cancellation);
            return;
        }

        _capacityDrops++;
        _notice = "新知识单元因题目容量已满而跳过。";
        await JournalAsync("CapacityDrop", new { candidate.EligibleUnitId, candidate.Trigger }, cancellation);
        Publish();
    }

    private async ValueTask ShowQuestionAsync(ResetQuestionCandidate candidate, CancellationToken cancellation)
    {
        var now = _clock.UtcNow;
        _current = new CurrentQuestion(candidate)
        {
            Phase = CurrentQuestionPhase.Active,
            ShownAt = now,
            Deadline = now + _timing.InitialAnswerWindow
        };
        _surface = SessionSurfaceKind.Question;
        _questionsShown++;
        _notice = candidate.Trigger == TriggerKind.Manual ? "手动复位题已生成。" : null;
        await JournalAsync("QuestionShown", new
        {
            candidate.EligibleUnitId,
            Question = candidate.Question.Id.ToString(),
            candidate.Trigger,
            _current.Deadline
        }, cancellation);
        Publish();
    }

    private async ValueTask<IntentOutcome> HandleAnswerAsync(SelectAnswer answer, CancellationToken cancellation)
    {
        if (_current is null || _current.Candidate.Question.Id != answer.Question)
        {
            return IntentOutcome.Reject("StaleQuestion", "这道题已经不是当前题。");
        }

        if (_current.Phase == CurrentQuestionPhase.Pending)
        {
            return IntentOutcome.Reject("OpenPendingFirst", "请先打开待答题。");
        }

        if (_current.Phase is not CurrentQuestionPhase.Active and not CurrentQuestionPhase.PendingOpen)
        {
            return IntentOutcome.Reject("NotAnswerable", "当前状态不能作答。");
        }

        var selected = _current.Candidate.Question.Choices.FirstOrDefault(choice => choice.Id == answer.Choice);
        if (selected is null)
        {
            return IntentOutcome.Reject("UnknownChoice", "该选项不属于当前题。");
        }

        var now = _clock.UtcNow;
        var isCorrect = answer.Choice == _current.Candidate.CorrectChoice;
        _answers++;
        if (isCorrect)
        {
            _correctAnswers++;
        }

        _current.Phase = CurrentQuestionPhase.Feedback;
        _current.FeedbackUntil = now + _timing.FeedbackDuration;
        _current.Feedback = new AnswerFeedback(
            isCorrect,
            _current.Candidate.CorrectChoice,
            _current.Candidate.Evidence);
        _surface = SessionSurfaceKind.Feedback;
        _notice = null;

        await JournalAsync("QuestionAnswered", new
        {
            Question = answer.Question.ToString(),
            Choice = answer.Choice.ToString(),
            IsCorrect = isCorrect,
            UsedExtension = _current.Extended,
            AnsweredWithinInitialWindow = now <= _current.ShownAt + _timing.InitialAnswerWindow,
            ElapsedMilliseconds = (long)(now - _current.ShownAt).TotalMilliseconds
        }, cancellation);
        Publish();
        return IntentOutcome.Accept("AnswerAccepted", isCorrect ? "回答正确。" : "回答错误。");
    }

    private async ValueTask<IntentOutcome> HandleExtendAsync(ExtendThinking extend, CancellationToken cancellation)
    {
        if (_current is null || _current.Candidate.Question.Id != extend.Question)
        {
            return IntentOutcome.Reject("StaleQuestion", "这道题已经不是当前题。");
        }

        if (_current.Phase != CurrentQuestionPhase.Active)
        {
            return IntentOutcome.Reject("NotActive", "只有活动答题卡可以延长。");
        }

        if (_current.Extended)
        {
            return IntentOutcome.Reject("AlreadyExtended", "每道题只能延长一次。");
        }

        _current.Extended = true;
        _current.Deadline = _current.ShownAt + _timing.ExtendedAnswerWindow;
        await JournalAsync("QuestionExtended", new { Question = extend.Question.ToString(), _current.Deadline }, cancellation);
        Publish();
        return IntentOutcome.Accept("Extended", "答题时间已延长 12 秒。");
    }

    private ValueTask<IntentOutcome> HandleOpenPendingAsync(OpenPending open, CancellationToken cancellation)
    {
        if (_current is null || _current.Candidate.Question.Id != open.Question || _current.Phase != CurrentQuestionPhase.Pending)
        {
            return ValueTask.FromResult(IntentOutcome.Reject("NoPendingQuestion", "当前没有这道待答题。"));
        }

        _current.Phase = CurrentQuestionPhase.PendingOpen;
        _surface = SessionSurfaceKind.Question;
        _notice = "待答题已打开，原失效时间不变。";
        Publish();
        return ValueTask.FromResult(IntentOutcome.Accept("PendingOpened", _notice));
    }

    private ValueTask<IntentOutcome> HandleCollapsePendingAsync(CollapsePending collapse, CancellationToken cancellation)
    {
        if (_current is null || _current.Candidate.Question.Id != collapse.Question || _current.Phase != CurrentQuestionPhase.PendingOpen)
        {
            return ValueTask.FromResult(IntentOutcome.Reject("NotPendingOpen", "当前没有已打开的待答题。"));
        }

        _current.Phase = CurrentQuestionPhase.Pending;
        _surface = SessionSurfaceKind.PendingBadge;
        _notice = null;
        Publish();
        return ValueTask.FromResult(IntentOutcome.Accept("PendingCollapsed", "待答题已折叠。"));
    }

    private async ValueTask<IntentOutcome> HandleQuestionIssueAsync(ReportQuestionIssue issue, CancellationToken cancellation)
    {
        if (_current is null || _current.Candidate.Question.Id != issue.Question ||
            _current.Phase is CurrentQuestionPhase.Feedback)
        {
            return IntentOutcome.Reject("StaleQuestion", "当前没有可报告的这道题。");
        }

        _invalidQuestions++;
        await JournalAsync("QuestionReportedInvalid", new { Question = issue.Question.ToString() }, cancellation);
        _current = null;
        await PromoteQueuedAsync(cancellation);
        return IntentOutcome.Accept("QuestionReportedInvalid", "已记录题目有误，不会重新生成。");
    }

    private async ValueTask<IntentOutcome> HandleEndSessionAsync(CancellationToken cancellation)
    {
        if (_surface is SessionSurfaceKind.AttentionRating or SessionSurfaceKind.Completed)
        {
            return IntentOutcome.Reject("AlreadyEnding", "课堂会话已经停止。");
        }

        _current = null;
        _queued = null;
        _surface = SessionSurfaceKind.AttentionRating;
        _notice = "请评价这些问题对拉回注意力的帮助。";
        await JournalAsync("CaptureStopped", null, cancellation);
        Publish();
        return IntentOutcome.Accept("SessionEnding", "收音已停止。");
    }

    private async ValueTask<IntentOutcome> HandleRatingAsync(byte? rating, CancellationToken cancellation)
    {
        if (_surface != SessionSurfaceKind.AttentionRating)
        {
            return IntentOutcome.Reject("NotAwaitingRating", "当前不需要注意力评价。");
        }

        if (rating is < 1 or > 5)
        {
            return IntentOutcome.Reject("InvalidRating", "评价必须在 1–5 之间。");
        }

        _attentionRating = rating;
        _summary = BuildSummary(_clock.UtcNow);
        _surface = SessionSurfaceKind.Completed;
        _notice = "课堂会话已完成。";
        await _journal.CompleteAsync(_summary, cancellation);
        Publish();
        return IntentOutcome.Accept("SessionCompleted", "课堂会话已完成。");
    }

    private async ValueTask HandleTickAsync(DateTimeOffset now, CancellationToken cancellation)
    {
        if (_queued is not null && _queued.RecognizedAt + _timing.QueueLifetime <= now)
        {
            await JournalAsync("QueuedQuestionExpired", new { Question = _queued.Question.Id.ToString() }, cancellation);
            _queued = null;
        }

        if (_current is null)
        {
            return;
        }

        if (_current.Phase == CurrentQuestionPhase.Active && _current.Deadline <= now)
        {
            _current.Phase = CurrentQuestionPhase.Pending;
            _current.PendingExpiresAt = now + _timing.PendingLifetime;
            _surface = SessionSurfaceKind.PendingBadge;
            _notice = "题目已折叠为待答徽标。";
            await JournalAsync("QuestionBecamePending", new
            {
                Question = _current.Candidate.Question.Id.ToString(),
                _current.PendingExpiresAt
            }, cancellation);
            Publish();
            return;
        }

        if (_current.Phase is CurrentQuestionPhase.Pending or CurrentQuestionPhase.PendingOpen &&
            _current.PendingExpiresAt <= now)
        {
            await JournalAsync("PendingQuestionExpired", new { Question = _current.Candidate.Question.Id.ToString() }, cancellation);
            _current = null;
            await PromoteQueuedAsync(cancellation);
            return;
        }

        if (_current.Phase == CurrentQuestionPhase.Feedback && _current.FeedbackUntil <= now)
        {
            _current = null;
            await PromoteQueuedAsync(cancellation);
        }
    }

    private async ValueTask PromoteQueuedAsync(CancellationToken cancellation)
    {
        if (_queued is null)
        {
            _surface = SessionSurfaceKind.Listening;
            _notice = "正在监听课堂内容。";
            Publish();
            return;
        }

        var candidate = _queued;
        _queued = null;
        if (candidate.RecognizedAt + _timing.QueueLifetime <= _clock.UtcNow)
        {
            await JournalAsync("QueuedQuestionExpired", new { Question = candidate.Question.Id.ToString() }, cancellation);
            _surface = SessionSurfaceKind.Listening;
            _notice = "排队题已过期，继续监听。";
            Publish();
            return;
        }

        await ShowQuestionAsync(candidate, cancellation);
    }

    private SessionSummary BuildSummary(DateTimeOffset completedAt) => new(
        _sessionId,
        _startedAt,
        completedAt,
        _questionsShown,
        _questionsQueued,
        _capacityDrops,
        _answers,
        _correctAnswers,
        _invalidQuestions,
        _attentionRating);

    private ValueTask JournalAsync(string type, object? data, CancellationToken cancellation) =>
        _journal.AppendAsync(new SessionEvent(_sessionId, _clock.UtcNow, type, data), cancellation);

    private void Publish()
    {
        var question = _current is null
            ? null
            : new QuestionCardView(
                _current.Candidate.Question.Id,
                _current.Candidate.Question.Type,
                _current.Candidate.Question.Stem,
                _current.Candidate.Question.Choices,
                _current.Candidate.Trigger);

        _views?.Report(new SessionView(
            _sessionId,
            Interlocked.Increment(ref _revision),
            _surface,
            _health,
            question,
            _current?.Feedback,
            _current?.Phase == CurrentQuestionPhase.Active ? _current.Deadline : null,
            _current?.Phase is CurrentQuestionPhase.Pending or CurrentQuestionPhase.PendingOpen
                ? _current.PendingExpiresAt
                : null,
            _current?.Phase == CurrentQuestionPhase.Active && !_current.Extended,
            _notice));
    }

    private static async Task IgnoreCancellationAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private abstract record SessionMessage;
    private sealed record CandidateMessage(ResetQuestionCandidate Candidate) : SessionMessage;
    private sealed record TickMessage(DateTimeOffset Now) : SessionMessage;
    private sealed record SourceFaultMessage(Exception Exception) : SessionMessage;
    private sealed record IntentMessage(LearnerIntent Intent, TaskCompletionSource<IntentOutcome> Completion) : SessionMessage;

    private enum CurrentQuestionPhase
    {
        Active,
        Pending,
        PendingOpen,
        Feedback
    }

    private sealed class CurrentQuestion(ResetQuestionCandidate candidate)
    {
        public ResetQuestionCandidate Candidate { get; } = candidate;
        public CurrentQuestionPhase Phase { get; set; }
        public DateTimeOffset ShownAt { get; set; }
        public DateTimeOffset Deadline { get; set; }
        public DateTimeOffset PendingExpiresAt { get; set; }
        public DateTimeOffset FeedbackUntil { get; set; }
        public bool Extended { get; set; }
        public AnswerFeedback? Feedback { get; set; }
    }
}
