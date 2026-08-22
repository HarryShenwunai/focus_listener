# Focus Listener Product Specification v2

Status: expanded design confirmed on 2026-08-22. This document defines the first functional prototype; it does not claim that the prototype has already improved attention.

## Product outcome

Focus Listener is a personal Windows classroom companion for the Learner. Its sole primary outcome is an Attention Reset: a brief interaction that redirects attention to the lesson currently being taught. A Reset Question checks comprehension only as the mechanism for producing that reset; it is not an assessment, teacher tool, or engagement score.

## First test scope

- Subjects are unrestricted: mathematics, science, history, language, and other classroom knowledge may trigger.
- Eligible relationships are definitions, cause/effect, rules/conditions, processes/sequences, comparisons/distinctions, and classifications/examples.
- Input may be Chinese, English, or mixed; the question follows the classroom's dominant language and preserves original terms.
- Audio must contain everything needed to answer; the prototype does not read a board, slide, or screen.
- Formula, number, and variable relationships may trigger, but questions never require calculation, substitution, equation solving, or numeric evaluation.
- The first acceptance round runs with authorized material in a real room before any broader classroom trial.

## MVP contract

The Learner installs and operates the app personally. After one-time setup, starting a session takes one action. The app uses a small always-on-top overlay and does not require a forced window switch.

The MVP includes:

- Windows 10/11 desktop use;
- separately selectable microphone and system-output endpoints, remembered by stable Windows device ID;
- automatic, microphone-only, system-only, and smart-mix capture modes;
- independent realtime-transcription and topmost translucent-subtitle switches;
- automatic triggering as the primary path and bubble/global-hotkey triggering as the manual path;
- one listening bubble, one question card, and one pending badge;
- three short choices, immediate correctness feedback, and one short Lesson Evidence excerpt;
- a one-time Gemini key setup stored in Windows Credential Manager;
- local SQLite analysis data and CSV export;
- one five-level Attention Reset rating at the end of a session.

The MVP excludes:

- app accounts, teacher accounts, publishing, projection, classroom dashboards, or teacher analytics;
- persisted full transcripts, transcript history, model/provider dashboards, or a plugin marketplace in the Learner interface;
- editing, regenerating, or publishing questions;
- OCR, screen capture, board capture, or slide understanding;
- raw-audio persistence;
- causal claims about improving attention before a later efficacy study.

## Core flow

1. On first real use, the Learner selects the actual microphone, system-output endpoint, and capture mode; the app never silently replaces a missing selected device.
2. The Learner starts a 10–15 minute Focus Session.
3. The app opens the selected route or routes, time-aligns dual-route frames, and forwards one authoritative stream rather than naively adding both PCM streams.
4. Gemini Live streams temporary text to the optional subtitle window and produces completed transcript turns for question generation. Temporary text never enters the candidate pipeline.
5. Gemini Flash-Lite examines the recent final-transcript window and either rejects it or returns an Eligible Unit and a schema-valid Restatement Question in one structured result.
6. Trigger Admission decides whether that question can become the current or queued question.
7. An admitted current question is displayed as soon as generation finishes, even if the lesson has resumed.
8. The Learner answers, extends once, lets the card become pending, or reports that the question is wrong.
9. An answered question shows correctness and Lesson Evidence for three seconds. A still-valid queued question then appears automatically.
10. Ending the session stops capture, hides the subtitle, and asks for one five-level Attention Reset rating.

## Content eligibility and trigger admission

Eligibility and interaction capacity are deliberately separate concepts.

An Eligible Unit must satisfy all of the following content-quality rules:

- it expresses a new relationship or definition;
- its spoken content is semantically complete and self-sufficient without visual context;
- it supports exactly one unambiguous correct choice;
- it contains a short, citable Lesson Evidence excerpt;
- it can produce a relationship-recognition or term-definition question without calculation.

Trigger Admission applies session state after eligibility is known:

- one current slot may contain either an active question or a pending question;
- one additional question may be queued;
- if the current slot and queue are both occupied, the new candidate is dropped and logged as a capacity drop;
- Automatic Trigger respects the learner-configured warmup and cooldown; the default cooldown is 120 seconds after a question closes;
- Manual Trigger uses the most recent Eligible Unit, bypasses automatic scheduling, but never bypasses the content-quality rules;
- a Manual Trigger is rejected with a brief notice when no Eligible Unit exists or admission capacity is full.

## Question and timing rules

- A Restatement Question has exactly three distinct choices and exactly one correct choice.
- Before answering, the card does not expose the correct choice or Lesson Evidence.
- The initial response window is eight seconds.
- A secondary “think for 12 more seconds” action is available throughout the initial window and may be used once, making the maximum active-card window 20 seconds from first display.
- An unanswered active card becomes a Pending Question. It expires two minutes after being folded.
- Reopening a Pending Question does not reset its expiry.
- A Pending Question occupies the current slot and blocks the queued question until it is answered, reported wrong, or expires.
- A Queued Question expires two minutes after its Eligible Unit was recognized.
- After an answer, feedback and Lesson Evidence remain for three seconds; a valid queued question then appears automatically.
- “Question is wrong” records an invalid-question event and closes the question without regeneration.
- A newly admitted question is displayed immediately after generation; the app does not wait for another teacher pause.

## User-visible states

```text
Listening
   ├─ eligible + admitted ─> Question
   │                         ├─ answer ─> Feedback (3s) ─> queued Question or Listening
   │                         ├─ timeout ─> Pending Badge
   │                         │              ├─ answer ─> Feedback ─> queued Question or Listening
   │                         │              └─ expire/report wrong ─> queued Question or Listening
   │                         └─ report wrong ─> queued Question or Listening
   └─ end session ─> Attention Rating ─> Completed
```

Generating, audio degradation, model recovery, and queue contents are internal state. The overlay may show a concise health notice but does not expose the pipeline.

## Audio policy

The learner chooses a microphone, a system-output endpoint, and one of four modes: automatic, microphone only, system sound only, or smart mix.

- Stable endpoint IDs and friendly names are remembered. A missing saved endpoint remains visibly unavailable; it is not silently replaced.
- The selected endpoint may be changed during a session; capture and Gemini Live restart on the new configuration.
- Dual streams retain monotonic timestamps and are resampled before arbitration.
- Automatic mode selects the stronger route per aligned bucket. Smart mix uses active system sound and otherwise the microphone, avoiding duplicate speaker audio.
- A route explicitly required by microphone-only or system-only mode must open successfully; both unavailable ends the session with an audio-unavailable result.
- Audio callbacks write only to bounded memory buffers and never wait for Gemini or SQLite.
- Raw audio is never written to disk.
- One-click diagnostics run for 15 seconds, show independent meters, automatically play one low-volume system test tone, and report the route actually adopted.

## Realtime transcript and subtitle policy

Realtime transcription and subtitle visibility are independent controls.

- The subtitle is a separate bottom-centred, topmost, translucent window with three-line display, adjustable opacity/font, multi-monitor placement, and saved bounds.
- While locked it is click-through; while unlocked it can be dragged and resized. Default global shortcuts toggle visibility and lock state.
- Temporary Gemini text is lighter; confirmed text is normal. A displayed question temporarily highlights its exact Lesson Evidence while transcription continues in the background.
- Turning subtitles off does not stop transcription. Turning transcription off pauses automatic question generation and closes the active Live stream.
- Live reconnects at most three times automatically, then exposes an explicit retry action.
- Neither raw audio nor the full transcript is persisted.

## Model policy

One user-provided Gemini key configures two model roles:

- Gemini Live: realtime audio input and completed transcription turns;
- Gemini Flash-Lite: combined Eligible Unit judgement and structured Restatement Question generation.

The Live model is not trusted to produce the question schema. Flash-Lite output must pass local validation for three choices, one correct choice, allowed question type, no calculation, and locatable Lesson Evidence. An invalid result may receive one structured-repair attempt; otherwise the candidate is skipped.

Temporary Gemini failure keeps the session usable, performs up to three bounded Live reconnects, and never substitutes an ungrounded question. Diagnostics and production both generate only from the current final transcript; no transcript means no question. The first test uses Gemini’s free tier only with self-created, public, or otherwise authorized material played in a real room. Testing live classroom participants is deferred because free-tier submissions may be used by Google to improve its products.

## Local data and analysis

SQLite stores no raw audio. It records enough information to reproduce functional decisions:

- session identity, classroom kind, timestamps, app/model versions, and health events;
- relevant transcript excerpts and Knowledge Unit identifiers;
- eligibility result, admission result, trigger source, capacity drops, and model latency;
- question type, stem, choices, correct choice, Lesson Evidence, and validation result;
- the selected audio source associated with the final evidence-bearing transcript unit;
- shown, extended, folded, reopened, answered, expired, and reported-wrong events;
- selected choice, correctness, whether the answer arrived in the initial eight seconds, and whether extension was used;
- visible decision time and elapsed time from first display to final answer;
- the end-of-session five-level Attention Reset rating.

Correctness is an analysis variable, not the primary Attention Reset outcome. CSV export belongs to a separate Reporting Module that reads the local journal.

## Functional acceptance test

Use authorized 10–15-minute spoken lessons covering all six relationship types across mathematics, science, history, language, and other subjects in five conditions:

1. clear playback;
2. distant microphone;
3. background noise;
4. mixed Chinese and English;
5. long pauses.

The functional prototype passes when:

- at least four of five sessions finish without fatal interruption;
- every session produces at least one Automatic Trigger and one Manual Trigger;
- every displayed question is grounded in spoken content and contains no calculation;
- the median time from Eligible Unit recognition to card display is no more than eight seconds;
- microphone/system arbitration avoids obvious duplicate transcript content;
- all required interaction and timing data is present in SQLite;
- no raw audio file is created.

This phase validates the chain from audio to card. It does not validate annoyance, classroom benefit, or causal Attention Reset effectiveness.

## Architecture

The prototype uses .NET 10, C#, WPF, NAudio, the official Google.GenAI C# SDK, and SQLite in one desktop process. The WPF shell crosses one external seam: the deep FocusSession Module.

```csharp
public interface IFocusSession
{
    Task<SessionSummary> RunAsync(
        SessionStart start,
        IProgress<SessionView> views,
        CancellationToken cancellation);

    ValueTask<IntentOutcome> ApplyAsync(
        LearnerIntent intent,
        CancellationToken cancellation = default);
}
```

Interface contract:

- one module instance runs at most one session;
- `ApplyAsync` serializes idempotent intents through an internal mailbox;
- expected rejections are returned as `IntentOutcome`, not infrastructure exceptions;
- each `SessionView` has a monotonically increasing revision;
- local intents should appear in a new view within 100 ms, excluding WPF dispatcher delay;
- `RunAsync` returns a `SessionSummary` after capture stops and the rating is submitted or skipped.

Internal seams and adapters:

| Dependency | Category | Production adapter | Test adapter |
| --- | --- | --- | --- |
| Classroom audio | local-substitutable | `WindowsDualCaptureAdapter` | `ScriptedAudioAdapter` |
| Live transcription | true external | `GeminiLiveTranscriptAdapter` | `ScriptedTranscriptAdapter` |
| Eligibility and question reasoning | true external | `GeminiFlashLiteAdapter` | `ScriptedReasoningAdapter` |
| Time | local-substitutable | `SystemClockAdapter` | `ManualClockAdapter` |
| Journal | local-substitutable | `SqliteJournalAdapter` | `InMemoryJournalAdapter` |

Triggering, timing, admission, queuing, validation, and analysis calculations stay inside FocusSession. The design deliberately does not publish shallow `ITriggerStrategy`, `IQueueManager`, or `IQuestionGenerator` interfaces before multiple real implementations exist.

API-key setup and Windows Credential Manager access belong to a separate Setup Module. CSV export belongs to a separate Reporting Module.

## Implementation slices

1. **Deterministic session core**: implement FocusSession with manual clock, scripted transcript/reasoning, in-memory journal, and Interface-level tests for every timing and queue transition.
2. **WPF shell**: implement listening bubble, question card, pending badge, feedback, global hotkey, and five-level rating against scripted adapters.
3. **Local journal**: add SQLite migration, event persistence, and Reporting Module CSV export.
4. **Windows audio**: add dual WASAPI capture, timestamps, bounded buffers, arbitration, device-loss degradation, and scripted-audio comparison tests.
5. **Gemini transcription**: connect the authoritative audio stream to Gemini Live with completed-turn handling and bounded recovery.
6. **Gemini reasoning**: add Flash-Lite schema, eligibility/question prompt, local validation, and one repair attempt.
7. **Functional matrix**: run the five accepted 10–15 minute conditions and evaluate only the functional acceptance criteria above.

Do not add provider plugins, FastAPI, a local HTTP server, a teacher workflow, or a persisted transcript browser. They add interfaces without increasing leverage for the first product question.
