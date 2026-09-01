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

## 0.1.1 — Local activation and realtime polish (current)

- replaceable local open-vocabulary “Jarvis” keyword spotter with pinned sherpa-onnx GigaSpeech model;
- dormant/activating/listening/conversation lifecycle with cooldown and continuation window;
- push-to-talk and manual-start fallbacks, capture-state display, and content-free wake/latency metrics;
- automated state/cancellation/duplicate/timeout tests and a physical wake-word matrix.

Exit requires the automated gates plus the wake-word matrix on target hardware. Keyword accuracy, false-positive rate, speaker feedback, distance limits, and idle CPU/battery use remain explicitly unverified until then.

## 0.1.x — Further audio hardening

- acoustic/threshold profiling, device change behavior, and clearer device selection;
- benchmark alternative local ASR profiles for accents and noisy rooms;
- evaluate WASAPI and reliable local echo cancellation without weakening barge-in.

## 0.2 — Typed tool and authorization kernel

- strongly typed tool descriptions, requests, results, cancellation, and timeouts;
- centralized authorization decisions and explicit user approval surface;
- append-only structured audit events with redaction and correlation IDs;
- no shell/filesystem/process implementation until bypass-resistance is tested.

## 0.3 — Controlled Windows capabilities

- scoped filesystem, process/application, system information, and safe shell tools;
- path canonicalization, command injection defenses, least privilege, and dry-run/preview;
- Windows-specific code behind platform interfaces; destructive tests use fakes/temp resources.

## 0.4 — Project intelligence

- repository discovery within user-approved roots;
- local metadata/text indexes and Roslyn C# symbol/dependency analysis;
- evidence-ranked context retrieval and grounded file/symbol explanations;
- technical tutoring and project-specific interview generation/evaluation.

## 0.5 — Memory, desktop understanding, and proactive events

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
- Future OS tools expand impact and must never reuse the model/runtime boundary as authorization.
