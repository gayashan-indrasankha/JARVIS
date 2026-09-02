# Project Tutor and Mock Interviewer architecture

Version 0.4 adds fully local, evidence-grounded learning sessions without adding a project or bypassing the tool kernel. Project Intelligence remains the sole authority for repository facts. The learning engine may explain or evaluate only the bounded claims and exact evidence returned by that service.

## Boundaries and flow

```text
text or local voice turn
  -> local agent planner
  -> typed ProjectLearning tool request
  -> schema validation + repository normalization
  -> SAFE_LOCAL_ACTION authorization + audit
  -> ProjectLearningService
       -> Project Intelligence bounded evidence
       -> session-level FAST/DEEP profile router
       -> provider-neutral IProjectLearningModel
       -> deterministic scoring/report state
       -> local SQLite session store
  -> bounded tool result
  -> local LLM final wording
  -> local TTS when voice is enabled
```

`Jarvis.Core.ProjectLearning` owns the state, levels, questions, scoring rubric records, reports, context accounting, and provider-neutral ports. Infrastructure owns Project Intelligence adaptation, llama.cpp prompting/parsing, memory-aware model routing, SQLite, configuration, and tool executors. Core contains no llama.cpp, HTTP, SQLite, Windows, logging, model-file, or native type.

The six public operations are `start_tutor_session`, `continue_tutor_session`, `start_interview_session`, `submit_interview_answer`, `end_learning_session`, and `start_revision_session`. They use closed schemas and the existing validation → authorization → bounded execution → content-minimized audit path. They are `SAFE_LOCAL_ACTION` because they can create/update private local session data and may replace the managed model process. A malformed/unknown/denied/timed-out request performs no learning operation.

## Grounding and untrusted input

Repository files, evidence excerpts, previous transcript, and user answers are untrusted data. The local model prompt explicitly prohibits treating them as instructions, requesting OS access, changing policy, inventing files/lines, or emitting tool calls. Model output is strict bounded JSON with unknown members rejected; one repair attempt is allowed and then the operation fails closed.

Learning statements use:

- `PROJECT FACT`: directly supported by one or more current `ProjectEvidence` records;
- `GENERAL PRINCIPLE`: transferable software-engineering knowledge, not asserted to exist in the repository;
- `DESIGN ALTERNATIVE`: an option or trade-off that is not claimed as current project behavior.

Tutor and interview generation require at least one Project Intelligence fact with evidence. Evidence indexes emitted by the model are range-checked and mapped to trusted records; invalid indexes cannot create evidence. Project-specific corrections without mapped evidence are not classified as project facts. Exact paths/lines originate only in Project Intelligence.

The context selector orders project facts first and enforces configured character/item caps. It sends several high-value excerpts, not the repository. Recent turns are capped and reduced before model serialization. Generated JSON, transcripts, and SQLite payloads are bounded.

## Tutor state

Tutor levels progress from Foundation through Architecture, Feature Flow, Implementation, Database, Security, Testing, Failure Handling, Scalability, Trade-offs, and Interview Defence. Interactions support explanation, deeper progression, Socratic/active-recall questions, self-explanation, evidence display, and recap. Ask-before-tell persists for the session. Each turn records strengths/gaps, concepts, evidence statements, and a context budget. A revision session starts in ask-before-tell mode from the highest-priority weakness in the latest completed interview for the same repository.

## Interview state and scoring

Interview difficulty is Internship, Junior, or Mid-Level Stretch. Questions advance across project overview, architecture, implementation, C#/.NET, API, database, security, testing, error handling, performance, concurrency, failure, scalability, and trade-offs. A weak/incorrect answer retains the current dimension and creates a targeted child question. Its correction remains stored with evidence but is withheld from the immediate tool result; the user is challenged first. It is presented when the follow-up sequence is complete or the session ends.

The model identifies demonstrated/incorrect expected-concept indexes and bounded qualitative flags. Trusted Core code converts those signals into deterministic 0–4 scores for project factual accuracy, depth, reasoning, trade-offs, C#/.NET, database, testing, security, communication, and confidence calibration. Wording is not compared literally. Each score retains its rubric and rationale; the question retains relevant evidence and expected concepts; the session retains transcript, strengths, gaps, and corrections.

Ending creates exactly these report categories: Project Knowledge, Architecture, Implementation, C#/.NET, Database, Testing, Security, Failure Handling, Tradeoffs, and Communication. It also returns strong/weak areas, poorly answered questions, revision topics, and suggested next difficulty.

## FAST and optional DEEP profiles

FAST remains the required Qwen3-4B Q4_K_M profile. The application is complete without DEEP. DEEP is the optional official Qwen3-8B Q4_K_M artifact pinned in the manifest and disabled by default.

Profile selection happens at session start and is reasserted only when resuming a persisted session. The managed supervisor runs one llama-server process. A profile change stops that process tree, starts the selected model on exactly `127.0.0.1`, applies its conservative context/offload/thread settings, and uses the existing bounded health check. DEEP is attempted only when enabled, in managed mode, its exact model file exists, and available physical memory meets the configured threshold. A missing/disabled/low-memory/start-failed DEEP profile falls back to FAST with a sanitized reason code; it never prevents a session. Session end returns to FAST. The router never disposes the shared supervisor; host shutdown retains lifecycle ownership.

Default DEEP settings are 6,144 context, 16 GPU layers, eight threads, and at least 7 GiB available physical memory. These are conservative starting values, not proof that the target GPU/RAM can run the profile. Setup requires the separate `-DownloadDeepModel` flag, so ordinary model setup does not download 5 GB unexpectedly.

## Persistence and privacy

When enabled, sessions live at `JARVIS_HOME\Data\ProjectLearning\project-learning.db`, outside repositories and Git. The store uses parameterized SQLite commands, a payload cap, bounded completed-session retention, and case-insensitive local repository lookup. `PersistSessions=false` uses bounded process-memory state instead. The database is private local data: it contains repository paths, user answers, questions, scores, and evidence-derived text. Stop JARVIS before deleting it.

Structured logs record only profile/reason codes and lifecycle counts. They do not record prompts, answers, transcripts, source excerpts, paths, raw model output, hidden reasoning, audio, or credentials. Tool audit events continue to omit arguments/results. Console output remains visible to the interactive user and can be captured if the user redirects it.

## Failure and resource behavior

Cancellation and the tool deadline flow through retrieval, profile startup, model generation, persistence, and the caller. Invalid model output, missing evidence, corrupt/oversized storage, unavailable local inference, and SQLite failures cross the tool boundary as stable sanitized categories. No model is downloaded at runtime. No profile has LAN binding, cloud fallback, or an API key.

Only one large LLM process is normally loaded. Context, output, transcript, turns, evidence, repair attempts, persisted payload, retained sessions, and operation duration are bounded. Automated tests use deterministic fakes and tiny temporary files; no real model, network, microphone, speaker, GPU, or repository execution is required.

Physical profile fit, local-answer quality, five-question voice continuity, and disconnected operation require the [manual Project Learning smoke test](../testing/manual-project-learning-smoke-test.md).
