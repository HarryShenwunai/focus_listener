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
    private CandidateScheduler? _scheduler;
    private long _revision;
    private CancellationTokenSource? _captureLifetime;
    private SessionSurfaceKind _surface;
    private SessionHealth _health = SessionHealth.Healthy;
    private CurrentQuestion? _current;
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
        _scheduler = new CandidateScheduler(_startedAt, _timing);
        _views = views;
        _surface = SessionSurfaceKind.Listening;
        _notice = _timing.Warmup > TimeSpan.Zero
            ? $"正在监听课堂内容，约 {_timing.Warmup.TotalSeconds:0} 秒后开始自动提问。"
            : "正在监听课堂内容。";

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _running = true;
        using var captureLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _captureLifetime = captureLifetime;

        try
        {
            await _journal.InitializeAsync(_sessionId, start, _startedAt, lifetime.Token);
            Publish();

            var automaticPump = PumpAutomaticCandidatesAsync(start, captureLifetime.Token);
            var statusPump = PumpSourceStatusAsync(captureLifetime.Token);
            var timerPump = PumpTimerAsync(lifetime.Token);

            while (_surface is not SessionSurfaceKind.Completed and not SessionSurfaceKind.Failed)
            {
                var message = await _mailbox.Reader.ReadAsync(lifetime.Token);
                await HandleMessageAsync(message, lifetime.Token);
            }

            lifetime.Cancel();
            await IgnoreCancellationAsync(automaticPump, statusPump, timerPump);
            return _summary ?? BuildSummary(_clock.UtcNow);
        }
        finally
        {
            _running = false;
            _captureLifetime = null;
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

    private async Task PumpSourceStatusAsync(CancellationToken cancellation)
    {
        if (_source is not IQuestionCandidateSourceStatus statusSource)
        {
            return;
        }

        try
        {
            await foreach (var status in statusSource.StatusAsync(cancellation).WithCancellation(cancellation))
            {
                await _mailbox.Writer.WriteAsync(new SourceStatusMessage(status), cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
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
            case SourceStatusMessage status:
                _health = status.Status.Health;
                _notice = status.Status.Notice;
                await JournalAsync("CandidateSourceStatus", new
                {
                    status.Status.Code,
                    Health = status.Status.Health.ToString(),
                    status.Status.Notice
                }, cancellation);
                Publish();
                break;
            case SourceFaultMessage fault:
                _health = SessionHealth.Degraded;
                _notice = "题目生成暂不可用，监听仍在继续。";
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
        return intent switch
        {
            RequestManualTrigger => await HandleManualTriggerAsync(cancellation),
            SelectAnswer answer => await HandleAnswerAsync(answer, cancellation),
            ExtendThinking extend => await HandleExtendAsync(extend, cancellation),
            OpenPending open => HandleOpenPending(open),
            CollapsePending collapse => HandleCollapsePending(collapse),
            ReportQuestionIssue issue => await HandleQuestionIssueAsync(issue, cancellation),
            EndSession => await HandleEndSessionAsync(cancellation),
            RateAttentionReset rating => await HandleRatingAsync(rating.Rating, cancellation),
            SkipAttentionRating => await HandleRatingAsync(null, cancellation),
            _ => IntentOutcome.Reject("UnknownIntent", "未知操作。")
        };
    }

    private async ValueTask<IntentOutcome> HandleManualTriggerAsync(CancellationToken cancellation)
    {
        if (_surface is SessionSurfaceKind.AttentionRating or SessionSurfaceKind.Completed or SessionSurfaceKind.Failed)
        {
            return IntentOutcome.Reject("NotListening", "课堂收音已经停止。");
        }

        if (_current is not null)
        {
            _notice = "请先处理当前题目。";
            Publish();
            return IntentOutcome.Reject("ProcessingCurrentQuestion", _notice);
        }

        var candidate = _scheduler!.TakeManual(_clock.UtcNow);
        if (candidate is null)
        {
            candidate = await _source.RequestManualAsync(_start!, cancellation);
            if (candidate is not null)
            {
                candidate = candidate with
                {
                    Trigger = TriggerKind.Manual,
                    Question = candidate.Question with { Id = QuestionId.New() }
                };
            }
        }

        if (candidate is null)
        {
            _notice = "暂时没有完整、可复述的知识点。";
            Publish();
            return IntentOutcome.Reject("NoEligibleUnit", _notice);
        }

        await ShowQuestionAsync(candidate, cancellation);
        return IntentOutcome.Accept("ManualTriggerAccepted", "已根据最近的合格知识点生成问题。");
    }

    private async ValueTask AdmitAsync(ResetQuestionCandidate candidate, CancellationToken cancellation)
    {
        if (_surface is SessionSurfaceKind.AttentionRating or SessionSurfaceKind.Completed or SessionSurfaceKind.Failed)
        {
            return;
        }

        var now = _clock.UtcNow;
        var admission = _scheduler!.Admit(candidate, now);
        switch (admission.Kind)
        {
            case CandidateAdmissionKind.Added:
                if (_current is not null || _timing.Warmup > TimeSpan.Zero)
                {
                    _questionsQueued++;
                }
                await JournalAsync("CandidateAdmitted", CandidateAnalytics(candidate, now), cancellation);
                break;
            case CandidateAdmissionKind.Replaced:
                _capacityDrops++;
                await JournalAsync("CandidateEvicted", new
                {
                    Removed = admission.Removed?.EligibleUnitId,
                    Added = candidate.EligibleUnitId,
                    Reason = "HigherPriority"
                }, cancellation);
                break;
            case CandidateAdmissionKind.Duplicate:
                await JournalAsync("CandidateDropped", new { candidate.EligibleUnitId, Reason = "DuplicateKnowledge" }, cancellation);
                return;
            case CandidateAdmissionKind.LowerPriority:
                _capacityDrops++;
                _notice = "新知识点因候选容量已满而跳过。";
                await JournalAsync("CandidateDropped", new { candidate.EligibleUnitId, Reason = "LowerPriority" }, cancellation);
                Publish();
                return;
            case CandidateAdmissionKind.Expired:
                await JournalAsync("CandidateExpired", new { candidate.EligibleUnitId }, cancellation);
                return;
        }

        if (_current is null && _surface == SessionSurfaceKind.Listening &&
            await TryShowAutomaticAsync(now, cancellation))
        {
            return;
        }

        _notice = "题目已准备。";
        Publish();
    }

    private async ValueTask<bool> TryShowAutomaticAsync(DateTimeOffset now, CancellationToken cancellation)
    {
        if (_current is not null || _surface != SessionSurfaceKind.Listening)
        {
            return false;
        }

        var candidate = _scheduler!.TakeAutomatic(now);
        if (candidate is null)
        {
            return false;
        }

        await ShowQuestionAsync(candidate, cancellation);
        return true;
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
            Trigger = candidate.Trigger.ToString(),
            candidate.Subject,
            KnowledgeType = candidate.Question.Type.ToString(),
            candidate.QualityScore,
            PriorityScore = _scheduler!.Priority(candidate, now),
            candidate.Question.Stem,
            Choices = candidate.Question.Choices.Select(choice => choice.Text).ToArray(),
            CorrectChoice = candidate.CorrectChoice.Value,
            EvidenceExcerpt = candidate.Evidence.Excerpt,
            candidate.RecognizedAt,
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
        var added = Math.Max(0, (_timing.ExtendedAnswerWindow - _timing.InitialAnswerWindow).TotalSeconds);
        return IntentOutcome.Accept("Extended", $"答题时间已延长 {added:0} 秒。");
    }

    private IntentOutcome HandleOpenPending(OpenPending open)
    {
        if (_current is null || _current.Candidate.Question.Id != open.Question || _current.Phase != CurrentQuestionPhase.Pending)
        {
            return IntentOutcome.Reject("NoPendingQuestion", "当前没有这道待答题。");
        }

        _current.Phase = CurrentQuestionPhase.PendingOpen;
        _surface = SessionSurfaceKind.Question;
        _notice = "待答题已打开，原失效时间不变。";
        Publish();
        return IntentOutcome.Accept("PendingOpened", _notice);
    }

    private IntentOutcome HandleCollapsePending(CollapsePending collapse)
    {
        if (_current is null || _current.Candidate.Question.Id != collapse.Question || _current.Phase != CurrentQuestionPhase.PendingOpen)
        {
            return IntentOutcome.Reject("NotPendingOpen", "当前没有已打开的待答题。");
        }

        _current.Phase = CurrentQuestionPhase.Pending;
        _surface = SessionSurfaceKind.PendingBadge;
        _notice = null;
        Publish();
        return IntentOutcome.Accept("PendingCollapsed", "待答题已折叠。");
    }

    private async ValueTask<IntentOutcome> HandleQuestionIssueAsync(ReportQuestionIssue issue, CancellationToken cancellation)
    {
        if (_current is null || _current.Candidate.Question.Id != issue.Question ||
            _current.Phase is CurrentQuestionPhase.Feedback)
        {
            return IntentOutcome.Reject("StaleQuestion", "当前没有可报告的这道题。");
        }

        _invalidQuestions++;
        await JournalAsync("QuestionReportedInvalid", new
        {
            Question = issue.Question.ToString(),
            Subject = _current.Candidate.Subject,
            KnowledgeType = _current.Candidate.Question.Type.ToString()
        }, cancellation);
        await CloseCurrentAsync("题目有误已记录，继续监听。", cancellation);
        return IntentOutcome.Accept("QuestionReportedInvalid", "已记录题目有误，不会重新生成。");
    }

    private async ValueTask<IntentOutcome> HandleEndSessionAsync(CancellationToken cancellation)
    {
        if (_surface is SessionSurfaceKind.AttentionRating or SessionSurfaceKind.Completed)
        {
            return IntentOutcome.Reject("AlreadyEnding", "课堂会话已经停止。");
        }

        _captureLifetime?.Cancel();
        _current = null;
        _scheduler!.Clear();
        _surface = SessionSurfaceKind.AttentionRating;
        _notice = "请评价这些问题对拉回注意力的帮助。";
        await JournalAsync("CaptureStopped", new { RollingTranscriptCleared = true }, cancellation);
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
        foreach (var expired in _scheduler!.PurgeExpired(now))
        {
            await JournalAsync("CandidateExpired", new { expired.EligibleUnitId }, cancellation);
        }

        if (_current is null)
        {
            if (!await TryShowAutomaticAsync(now, cancellation))
            {
                Publish();
            }
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
            await CloseCurrentAsync("待答题已过期，继续监听。", cancellation);
            return;
        }

        if (_current.Phase == CurrentQuestionPhase.Feedback && _current.FeedbackUntil <= now)
        {
            await CloseCurrentAsync("正在监听课堂内容。", cancellation);
        }
    }

    private async ValueTask CloseCurrentAsync(string notice, CancellationToken cancellation)
    {
        if (_current is null)
        {
            return;
        }

        var trigger = _current.Candidate.Trigger;
        _current = null;
        _scheduler!.MarkQuestionClosed(trigger, _clock.UtcNow);
        _surface = SessionSurfaceKind.Listening;
        _notice = notice;
        if (!await TryShowAutomaticAsync(_clock.UtcNow, cancellation))
        {
            Publish();
        }
    }

    private object CandidateAnalytics(ResetQuestionCandidate candidate, DateTimeOffset now) => new
    {
        candidate.EligibleUnitId,
        candidate.Subject,
        KnowledgeType = candidate.Question.Type.ToString(),
        candidate.Language,
        candidate.QualityScore,
        PriorityScore = _scheduler!.Priority(candidate, now),
        Trigger = candidate.Trigger.ToString(),
        candidate.RecognizedAt
    };

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
                _current.Candidate.Trigger)
            {
                Subject = _current.Candidate.Subject,
                Language = _current.Candidate.Language
            };

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
            _notice)
        {
            CandidateReady = _scheduler?.HasReadyCandidate(_clock.UtcNow) == true
        });
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
    private sealed record SourceStatusMessage(QuestionSourceStatus Status) : SessionMessage;
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
