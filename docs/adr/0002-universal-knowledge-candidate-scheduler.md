# ADR 0002: Separate universal question policy from candidate scheduling

## Status

Accepted — 2026-08-22

## Decision

Replace the travel-problem-only policy with one universal `KnowledgeQuestionPolicy` shared by production classroom generation and one-click diagnostics. The policy owns structured model output validation, evidence locking, calculation-task rejection, safety checks, length limits, negative-question formatting, answer shuffling, and mapping to the six knowledge relationship types.

Introduce a separate deterministic `CandidateScheduler` behind the existing `FocusSession` interface. It owns the bounded candidate pool, semantic fingerprints, expiry, freshness/clarity ranking, warmup, automatic cooldown, manual safety gap, and consumption of displayed/older candidates. `FocusSession` remains the deep orchestration module and owns question lifecycle, learner intents, health notices, and journaling. WPF receives only view state plus simple subject/type metadata and a candidate-ready flag.

## Why

Eligibility and timing change for different reasons and need different tests. Keeping both inside the Gemini adapter would make network behavior, product cadence, and UI state inseparable. The split gives production and diagnostics one policy seam, gives timing a pure clock-driven seam, and keeps model payload details out of WPF.

## Consequences

- New subjects and knowledge types do not require WPF changes.
- Candidate timing can be tested without audio, Gemini, or wall-clock sleeps.
- Model quality scores rank only candidates that already passed hard validation.
- Existing SQLite databases receive nullable analytics columns through additive migration.
- Settings expose only learner-understandable interaction times; technical transcript/model window values remain implementation details.
