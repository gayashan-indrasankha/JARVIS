# Product roadmap

The roadmap is capability- and evidence-driven, not date-driven. A milestone advances only after its safety, testability, and observability gates pass. Names and ordering may change through architecture decisions as prototypes produce evidence.

## 0.0 — Foundation

**Goal:** create a buildable, understandable base without prematurely implementing assistant capabilities.

Included:

- .NET 10 solution with Core, Infrastructure, Host, and Core test projects;
- inward dependency direction and an automated Core boundary check;
- Generic Host composition, dependency injection, validated configuration, and structured logging;
- nullable reference types, warnings-as-errors, SDK analyzers, central package versions, editor policy, and secret-safe ignores;
- product, security, tool, voice, and project-intelligence architecture documents.

Explicitly excluded: AI APIs, audio, tools, indexing, UI automation, memory, interviews, background agents, and IoT.

Exit gate:

- restore, build, and tests pass from a clean checkout;
- no secrets or generated personal data are tracked;
- a new developer can explain current dependencies and the intended trust boundaries from the docs.

## 0.1 — Realtime voice foundation

**Goal:** prove an end-to-end, interruptible Windows voice conversation while preserving provider and platform boundaries.

Included:

- provider-neutral realtime, audio, wake-word, cancellation, and session-state contracts in Core;
- OpenAI realtime WebSocket adapter with persistent connection and bounded reconnect;
- Windows PCM microphone capture and speaker playback;
- streamed audio, server-VAD barge-in, push-to-talk, and text-console fallback;
- secret-safe configuration, structured content-free operational logging, and graceful shutdown;
- deterministic orchestration, protocol, reconnect, and configuration tests plus a real-device smoke plan.

Explicitly excluded: computer-control tools, wake-word inference, project indexing, UI automation, memory, and IoT. The milestone reorder is recorded in [ADR 0001](../decisions/0001-realtime-voice-transport-and-windows-audio.md).

## 0.2 — Tool safety vertical slice

**Goal:** validate one narrowly scoped, low-risk local tool end to end.

Candidate scope:

- typed tool catalog, argument validation, policy decisions, approval binding, execution, and audit;
- a read-only system-information tool with explicit output limits;
- policy and prompt-injection adversarial tests;
- local audit persistence and a basic review/export path.

Filesystem mutation and general shell execution remain out of scope until this model is demonstrated.

## 0.3 — Voice hardening and local activation

**Goal:** harden the 0.1 vertical slice using measured device and latency evidence.

Candidate scope:

- local audio device abstraction, buffering, and device-change handling;
- local wake-word and voice-activity components;
- a selected replaceable local wake-word engine;
- resampling, modern device discovery/change handling, and echo/acoustic evaluation;
- latency instrumentation and broader cancellation/race tests;
- latency, failure, privacy, and reconnect telemetry.

The provider adapter must not gain tool execution authority.

## 0.4 — Controlled Windows capabilities

**Goal:** add useful OS actions one capability at a time.

Candidate scope:

- constrained filesystem read and later write tools with canonical path policy;
- process and application inspection before control operations;
- allowlisted command execution without a general-purpose model-owned shell;
- Windows implementations behind platform interfaces;
- approval UX, rate limits, timeouts, output limits, and recovery tests.

## 0.5 — Project intelligence

**Goal:** answer questions about opted-in C# repositories with traceable evidence.

Candidate scope:

- repository discovery limited to user-approved roots;
- local metadata and content index with ignore, size, and sensitivity rules;
- Roslyn solution, project, syntax, symbol, reference, and dependency analysis;
- retrieval that returns minimal relevant excerpts and file/symbol citations;
- freshness detection and grounded explanation evaluation.

Tutoring and interviews follow only after core retrieval quality is measured.

## Later horizons

- technical tutoring, project-specific interviews, answer evaluation, and weakness tracking;
- screen/window understanding and accessibility-first Windows UI automation;
- user-owned long-term memory with provenance, retention, export, and deletion;
- bounded background events and proactive notification controls;
- optional hardware and IoT adapters with the same authorization and audit model.

## Cross-cutting gates for every milestone

- Core remains provider-, platform-, UI-, and persistence-neutral.
- New side effects have explicit authorization and complete audit coverage.
- Sensitive inputs and outputs have redaction, retention, and size policies.
- Provider failures, cancellation, timeouts, and partial completion are tested.
- Relevant architecture docs and decisions are updated in the same change.
- Dependency additions have a clear owner, purpose, license review, and maintenance plan.

## Known risks

- Realtime latency may conflict with local processing and safety checks.
- Approval prompts can become either unsafe through fatigue or unusable through excess friction.
- Windows UI automation is brittle across application, DPI, accessibility, and session changes.
- Repository content and tool output can contain prompt injection or secrets.
- Long-term memory can create privacy, staleness, and incorrect-inference problems.
- Provider APIs and local model capabilities will change; boundaries must be tested, not merely documented.
