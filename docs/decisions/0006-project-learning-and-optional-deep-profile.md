# 0006 — Project learning and optional session-level DEEP profile

- Status: Accepted
- Date: 2026-09-02
- Owners: team
- Supersedes: roadmap ordering for the former 0.4/0.5 entries only

## Context

Project Intelligence now provides bounded local evidence. The requested next milestone is project tutoring and interviewing, while the existing roadmap placed controlled mutation first. The required target machine has 16 GB RAM and 4 GB VRAM: Qwen3-4B is stable enough to remain mandatory, while an 8B model may improve evaluation but cannot be assumed to fit in VRAM or even available system memory.

## Decision

Deliver Tutor/Interviewer as 0.4 and move approval-controlled mutation to a later milestone. Keep the four physical projects. Put provider/platform-neutral learning state and scoring in Core; implement evidence retrieval, local-model JSON adaptation, SQLite, and profile routing in Infrastructure. Expose learning through typed authorized tools so voice/text use the existing audited agent path.

FAST is Qwen3-4B Q4_K_M and is always sufficient. DEEP is the optional official Qwen3-8B Q4_K_M artifact, disabled and not downloaded by default. Route once at session start, maintain one managed llama-server, health-check after replacement, and fall back to FAST on missing assets, low memory, unsupported external mode, or bounded startup failure. Use no cloud fallback.

## Alternatives considered

- Load 4B and 8B simultaneously: rejected for RAM/VRAM stability.
- Switch models for every question: rejected for latency, fragmentation, and process churn.
- Make 8B mandatory: rejected because it violates the requested hardware/resource and graceful-degradation goals.
- Let the model score arbitrary numeric dimensions: rejected; trusted Core derives scores from bounded concept/quality signals.
- Add a new service/project or vector store: rejected; neither is a demonstrated boundary or retrieval need.
- Call learning services directly from the voice host: rejected because it would bypass typed validation, authorization, and audit.

## Consequences

FAST-only installations retain the complete feature. DEEP session startup can be slower because the existing process must stop and the larger model must load. A failed DEEP attempt adds bounded delay but returns to FAST. Persisted sessions contain sensitive local learning history and need future user-facing retention/deletion controls. Static repository evidence limits what JARVIS may claim; incomplete evidence must stay an inference/general principle.

Controlled mutation remains deferred; version 0.4 adds no write, delete, arbitrary shell, elevation, UI automation, or administrator capability.

## Validation

Domain tests cover tutor/interview transitions, targeted follow-ups, evidence, deterministic scoring, reports/revision, cancellation, and budgets. Infrastructure tests cover SQLite round-trip, strict JSON repair, missing/failed DEEP fallback, session reuse, FAST restoration, schemas, and authorization categories. Physical/model/offline behavior follows the manual smoke test.
