# JARVIS roadmap

Milestones are capability gates, not calendar promises. Each increment must retain inward dependencies, local-first data handling, authorization boundaries, auditability, and deterministic tests.

## 0.0 — Foundation (complete)

- .NET 10 solution with Core, Infrastructure, Host, and tests;
- Generic Host, dependency injection, configuration, and structured logging;
- warnings-as-errors, analyzers, central package management, formatting rules;
- architecture, security, tool-system, voice, and project-intelligence designs.

## 0.1 — Local realtime voice foundation (complete in code)

- provider-neutral Core ports for language generation, VAD, ASR, TTS, audio, wake detection, metrics, and notifications;
- supervised pinned llama.cpp `llama-server`, fixed IPv4 loopback, bounded startup, health check, cancellation, clean process-tree termination, one 8192→4096 fallback;
- Qwen3-4B Q4_K_M non-thinking local conversation;
- sherpa-onnx Silero VAD, small streaming English Zipformer ASR, and Kokoro English TTS;
- Windows WinMM microphone and speaker adapters;
- incremental speech-safe segmentation, ordered bounded TTS, generation IDs, stale-output rejection, and barge-in;
- text and push-to-talk fallbacks, `/interrupt`, clean shutdown;
- explicit setup/diagnostic scripts, tracked model manifest, local-only metrics;
- no cloud provider, API key, external runtime request, telemetry, or startup download.

Exit requires automated gates plus the [manual local voice test](../testing/manual-voice-smoke-test.md) on target hardware. Physical voice/offline claims remain manual until executed.

## 0.1.1 — Local activation and realtime polish (complete in code)

- replaceable local open-vocabulary “Jarvis” keyword spotter with pinned sherpa-onnx GigaSpeech model;
- dormant/activating/listening/conversation lifecycle with cooldown and continuation window;
- push-to-talk and manual-start fallbacks, capture-state display, and content-free wake/latency metrics;
- automated state/cancellation/duplicate/timeout tests and a physical wake-word matrix.

Exit requires the automated gates plus the wake-word matrix on target hardware. Keyword accuracy, false-positive rate, speaker feedback, distance limits, and idle CPU/battery use remain explicitly unverified until then.

## 0.1.x — Further audio hardening

- acoustic/threshold profiling, device change behavior, and clearer device selection;
- benchmark alternative local ASR profiles for accents and noisy rooms;
- evaluate WASAPI and reliable local echo cancellation without weakening barge-in.

## 0.2 — Permission-controlled local computer agent (complete)

- strongly typed tool descriptions, requests, results, cancellation, and timeouts;
- exact trusted catalog, closed JSON schemas, validation before centralized authorization, and authorization before execution;
- append-oriented structured audit events with content minimization and request/invocation correlation IDs;
- approved-root bounded filesystem reads, visible file/folder open, fixed application launch, bounded process/system metrics, read-only Git status, and fixed diagnostic commands;
- schema-constrained local llama.cpp planning, one repair attempt, tool-step/duplicate/result limits, and untrusted-result labeling;
- no writes, deletion, credential access, arbitrary shell/arguments, elevation, UI automation, network tools, or model-to-OS bypass.

The originally planned harmless-kernel and Windows-adapter milestones are combined for the required acceptance demonstrations; [ADR 0004](../decisions/0004-permission-controlled-local-tool-kernel.md) records the constrained boundary. Exit requires automated gates plus the [manual tool smoke test](../testing/manual-tool-smoke-test.md). Real window opening and local-model selection remain manual until executed on the target desktop.

## 0.3 — Fully local Project Intelligence (complete)

- approved-root direct Git repository, `.sln`/`.slnx`, `.csproj`, source, and test discovery;
- static non-evaluating project metadata loading plus an in-memory Roslyn workspace;
- namespace/type/member, inheritance/interface/call, endpoint, DI, authentication, EF Core, project/package, test, and Git facts where statically observable;
- incremental SHA-256 snapshots, SQLite metadata/FTS5 index under `JARVIS_HOME`, and debounced file watching;
- eleven typed ProjectTools through the existing validation/authorization/audit boundary;
- exact-symbol/Roslyn/FTS evidence retrieval with a bounded 4B-model context and project-fact/inference/general-knowledge labels;
- no repository execution, package restore, source generator execution, cloud upload, vector database, or whole-repository prompt.

[ADR 0005](../decisions/0005-local-project-intelligence-index.md) records why Project Intelligence moved ahead of controlled mutation and why `MSBuildWorkspace` is not used for untrusted repositories. Exit requires automated gates plus the [manual Project Intelligence smoke test](../testing/manual-project-intelligence-smoke-test.md) with an installed local model and a disposable real repository.

## 0.4 — Fully local Project Tutor and Mock Interviewer (current)

- progressive evidence-grounded tutoring with Socratic, active-recall, self-explanation, recap, and revision interactions;
- adaptive project-specific interviews at Internship, Junior, and Mid-Level Stretch difficulty;
- deterministic ten-dimension rubric scoring, retained evidence/concepts/transcript/strengths/gaps, and structured session reports;
- persisted bounded local sessions with “teach my weaknesses” handoff;
- required FAST Qwen3-4B plus optional session-level Qwen3-8B DEEP profile with memory gating and graceful FAST fallback;
- complete text/local-voice path through the existing typed tool, authorization, and audit boundary.

[ADR 0006](../decisions/0006-project-learning-and-optional-deep-profile.md) records the user-requested resequencing and one-model session-level profile choice. Exit requires automated gates plus the [manual Project Learning smoke test](../testing/manual-project-learning-smoke-test.md). Physical model fit, learning quality, voice continuity, latency, and disconnected operation remain manual until executed.

## 0.5 — Approval surface and controlled mutation

- explicit and strong confirmation UI with exact-request grants and expiry;
- durable tamper-resistant audit storage, review, retention, and privacy controls;
- carefully scoped file writes/changes and additional process/application actions with preview/rollback where practical;
- no general administrator shell; destructive capabilities require separate decisions and safe test strategies.

## 0.6 — Deeper Project Intelligence

- grounded technical tutoring and project-specific interview generation/evaluation;
- richer cross-project flow analysis and measured local retrieval/answer quality;
- optional local semantic retrieval only after a concrete benchmark justifies embeddings.

## 0.7 — Memory, desktop understanding, and proactive events

- explicit memory categories, provenance, retention, correction, and deletion;
- consent-based screen/window understanding and Windows UI automation;
- bounded background events and notifications with quiet hours and authorization.

## Later

- pluggable hardware/IoT adapters behind the same typed-tool policy;
- optional additional local models selected through manifests and benchmarks;
- packaging/update strategy with signed artifacts and verified supply chain.

## Persistent risks

- 4 GB VRAM constrains context and offload; stability has priority over maximum context.
- Local model quality, ASR accuracy, and TTS latency vary with hardware and acoustics.
- Native runtime/model downloads are a supply-chain boundary; missing authoritative hashes must stay visible.
- Voice barge-in without echo cancellation works best with headphones.
- New OS tools expand impact and must reuse the typed dispatcher; model/runtime boundaries never authorize actions.
