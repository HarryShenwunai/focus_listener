namespace FocusListener;

internal enum CandidateAdmissionKind
{
    Added,
    Replaced,
    Duplicate,
    LowerPriority,
    Expired
}

internal sealed record CandidateAdmission(
    CandidateAdmissionKind Kind,
    ResetQuestionCandidate? Removed = null);

internal sealed class CandidateScheduler
{
    private readonly SessionTiming _timing;
    private readonly List<ResetQuestionCandidate> _pool = [];
    private readonly Dictionary<string, string> _displayedEvidence = new(StringComparer.Ordinal);
    private DateTimeOffset _nextAutomaticAt;
    private DateTimeOffset _manualSafetyUntil;

    public CandidateScheduler(DateTimeOffset startedAt, SessionTiming timing)
    {
        _timing = timing;
        _nextAutomaticAt = startedAt + timing.Warmup;
        _manualSafetyUntil = startedAt;
    }

    public int Count => _pool.Count;

    public bool HasReadyCandidate(DateTimeOffset now)
    {
        PurgeExpired(now);
        return _pool.Count != 0;
    }

    public CandidateAdmission Admit(ResetQuestionCandidate candidate, DateTimeOffset now)
    {
        PurgeExpired(now);
        if (ExpiresAt(candidate) <= now)
        {
            return new CandidateAdmission(CandidateAdmissionKind.Expired);
        }

        var evidence = KnowledgeQuestionPolicy.NormalizeForComparison(candidate.Evidence.Excerpt);
        if (_displayedEvidence.TryGetValue(candidate.KnowledgeFingerprint, out var displayedEvidence) &&
            string.Equals(displayedEvidence, evidence, StringComparison.Ordinal))
        {
            return new CandidateAdmission(CandidateAdmissionKind.Duplicate);
        }

        var sameKnowledge = _pool.FirstOrDefault(existing => string.Equals(
            existing.KnowledgeFingerprint,
            candidate.KnowledgeFingerprint,
            StringComparison.Ordinal));
        if (sameKnowledge is not null)
        {
            var priorEvidence = KnowledgeQuestionPolicy.NormalizeForComparison(sameKnowledge.Evidence.Excerpt);
            if (string.Equals(priorEvidence, evidence, StringComparison.Ordinal))
            {
                return new CandidateAdmission(CandidateAdmissionKind.Duplicate);
            }

            _pool.Remove(sameKnowledge);
            _pool.Add(candidate);
            return new CandidateAdmission(CandidateAdmissionKind.Replaced, sameKnowledge);
        }

        if (_pool.Count < _timing.CandidateCapacity)
        {
            _pool.Add(candidate);
            return new CandidateAdmission(CandidateAdmissionKind.Added);
        }

        var lowest = _pool.MinBy(existing => Priority(existing, now))!;
        if (Priority(candidate, now) <= Priority(lowest, now))
        {
            return new CandidateAdmission(CandidateAdmissionKind.LowerPriority);
        }

        _pool.Remove(lowest);
        _pool.Add(candidate);
        return new CandidateAdmission(CandidateAdmissionKind.Replaced, lowest);
    }

    public ResetQuestionCandidate? TakeAutomatic(DateTimeOffset now)
    {
        PurgeExpired(now);
        if (now < _nextAutomaticAt || now < _manualSafetyUntil || _pool.Count == 0)
        {
            return null;
        }

        var selected = _pool
            .OrderByDescending(candidate => Priority(candidate, now))
            .ThenByDescending(candidate => candidate.RecognizedAt)
            .First();
        return Consume(selected, TriggerKind.Automatic);
    }

    public ResetQuestionCandidate? TakeManual(DateTimeOffset now)
    {
        PurgeExpired(now);
        if (_pool.Count == 0)
        {
            return null;
        }

        var selected = _pool.MaxBy(candidate => candidate.RecognizedAt)!;
        return Consume(selected, TriggerKind.Manual);
    }

    public void MarkQuestionClosed(TriggerKind trigger, DateTimeOffset now)
    {
        if (trigger == TriggerKind.Automatic)
        {
            _nextAutomaticAt = now + _timing.AutoCooldown;
        }
        else
        {
            _manualSafetyUntil = now + _timing.ManualSafetyGap;
        }
    }

    public IReadOnlyList<ResetQuestionCandidate> PurgeExpired(DateTimeOffset now)
    {
        var expired = _pool.Where(candidate => ExpiresAt(candidate) <= now).ToArray();
        foreach (var candidate in expired)
        {
            _pool.Remove(candidate);
        }

        return expired;
    }

    public void Clear() => _pool.Clear();

    internal double Priority(ResetQuestionCandidate candidate, DateTimeOffset now)
    {
        var age = Math.Max(0, (now - candidate.RecognizedAt).TotalSeconds);
        var lifetime = Math.Max(1, _timing.CandidateLifetime.TotalSeconds);
        var freshness = Math.Clamp(1 - age / lifetime, 0, 1);
        return freshness * 0.60 + Math.Clamp(candidate.QualityScore, 0, 1) * 0.40;
    }

    private ResetQuestionCandidate Consume(ResetQuestionCandidate selected, TriggerKind trigger)
    {
        _displayedEvidence[selected.KnowledgeFingerprint] =
            KnowledgeQuestionPolicy.NormalizeForComparison(selected.Evidence.Excerpt);
        _pool.RemoveAll(candidate => candidate.RecognizedAt <= selected.RecognizedAt);
        return selected with
        {
            Trigger = trigger,
            Question = trigger == TriggerKind.Manual
                ? selected.Question with { Id = QuestionId.New() }
                : selected.Question
        };
    }

    private DateTimeOffset ExpiresAt(ResetQuestionCandidate candidate) =>
        candidate.RecognizedAt + _timing.CandidateLifetime;
}
