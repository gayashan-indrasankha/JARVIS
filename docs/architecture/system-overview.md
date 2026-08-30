# System overview

## Status and scope

This document defines the intended architectural boundaries for JARVIS and the physical structure implemented through version 0.1. Realtime conversation and voice are now implemented; most other conceptual modules remain designs. They should initially become cohesive namespaces or components inside the existing projects; a new project or process requires demonstrated isolation value and an architecture decision.

## Architectural style

JARVIS begins as a modular, user-space .NET application running in the signed-in Windows user's session. It uses ports and adapters:

- Core owns domain policy, use cases, provider-neutral contracts, and value types.
- Infrastructure implements contracts using the operating system, providers, persistence, and external libraries.
- Host selects implementations, loads configuration, controls process lifetime, and exposes the eventual user experience.

The model is always outside the trusted execution boundary. Model output is untrusted input to orchestration and tool policy; it is never executable authority.

## Physical projects in 0.1

| Project | Responsibility | May reference | Must not reference |
| --- | --- | --- | --- |
| `Jarvis.Core` | Provider-neutral realtime/audio contracts, voice state and orchestration behavior | .NET base libraries and deliberately approved domain-only packages | Host, Infrastructure, AI SDKs, Windows APIs, UI, SQLite, logging providers |
| `Jarvis.Infrastructure` | Configuration, OpenAI realtime transport, Windows audio, and disabled wake-word adapter | Core and implementation-specific packages | Host or UI composition |
| `Jarvis.Host` | Executable composition root, console debug surface, DI, configuration, logging providers, lifetime | Core and Infrastructure | Conversation or interruption rules that belong in Core |
| `Jarvis.Core.Tests` | Core behavior and dependency-boundary tests | Core and test libraries | Production Infrastructure or Host behavior |
| `Jarvis.Infrastructure.Tests` | Provider protocol, reconnect, and configuration-boundary tests | Core, Infrastructure, and test libraries | Real credentials, network services, or audio devices |

The compile-time direction is:

```text
Jarvis.Host ────────> Jarvis.Infrastructure ────────> Jarvis.Core
     └──────────────────────────────────────────────> Jarvis.Core

Jarvis.Core.Tests ─────────────────────────────────> Jarvis.Core
Jarvis.Infrastructure.Tests -> Jarvis.Infrastructure -> Jarvis.Core
```

An outer layer may depend on an inner layer. The reverse is forbidden. Interfaces are owned by the layer that needs the behavior, normally Core; implementations live outward in Infrastructure. `Host -> Core` is allowed for composition and process-boundary translation.

Core now contains the provider-neutral voice contracts and coordinator. Its architecture test prevents references to outer application assemblies, and provider/Windows types remain in Infrastructure.

## Planned logical modules

These are cohesive responsibilities, not a mandate for separate projects:

| Module | Responsibility | Important boundaries |
| --- | --- | --- |
| Conversation orchestration | Turns, events, cancellation, context budgets, provider requests | Does not execute tools or know provider SDK types |
| Provider gateways | Realtime model, speech, embedding, or local-model adapters | Implements Core ports; owns SDK translation and resilience |
| Tool catalog | Typed tool metadata, validation, result contracts, capability discovery | No ambient OS access; catalog membership is not authorization |
| Authorization | User policy, risk classification, approval requirements, decision evidence | Evaluates before every side effect; defaults safely |
| Tool execution | Timeouts, cancellation, concurrency, output limits, adapter dispatch | Executes only an authorized immutable request |
| Audit | Attempt, decision, execution, result, actor, timing, and correlation records | Redacts secrets; failures are recorded; retention is explicit |
| Voice session | Wake word, capture, voice activity, provider stream, playback, barge-in | Audio and provider SDKs remain outside Core |
| Project intelligence | Approved-root discovery, local index, Roslyn analysis, minimal retrieval | Repository content is untrusted; evidence and freshness travel with answers |
| Memory | User-approved durable facts, provenance, retention, export, deletion | Separate from raw conversation logs; never silent or unbounded |
| Windows platform | Files, processes, applications, windows, accessibility, notifications | Behind Core-owned platform/tool ports; user-space only |
| Background events | Schedules, watchers, notification policy, quiet hours | Bounded, observable, cancellable, and permissioned |
| Hardware adapters | Optional device protocols and commands | Same typed-tool, authorization, and audit path as software actions |

## Primary runtime flow

A future user request follows one controlled path:

```text
Input event
  -> Conversation orchestrator
  -> Provider reasoning request (minimum required context)
  -> Plain response, or typed tool proposal
  -> Schema and semantic validation
  -> Authorization decision
  -> Optional user approval bound to the exact request
  -> Tool execution through a local adapter
  -> Audit result and state change
  -> Sanitized, bounded observation returned to orchestration
  -> User-visible response
```

Provider calls and tool calls are separate operations. A provider cannot bypass validation, authorize itself, expand an approval, load an implementation by name, or receive a raw OS handle.

## Process and privilege model

- JARVIS runs in user space under the current user's normal access token.
- It is not a Windows service, kernel driver, security boundary, or privilege-escalation mechanism.
- Administrator elevation is not a baseline assumption. A future elevated helper, if ever justified, needs a separate threat model, narrow protocol, signed binaries, explicit UX, and an ADR.
- Resource-intensive or crash-prone work may later move to a child process for containment. That is an implementation boundary, not a microservice strategy.
- Only one local user/instance is assumed initially. Multi-user behavior requires a new identity and data-isolation design.

## Data placement and movement

Local by default:

- preferences and policy;
- tool audit events;
- project indexes and Roslyn-derived metadata;
- wake-word and voice-activity processing where practical;
- memory and learning history;
- background schedules and operational state.

Remote only when a selected provider requires it:

- the active user request;
- the minimum retrieved project excerpts or tool observations needed for that request;
- audio frames explicitly participating in a remote realtime session;
- protocol metadata required to operate the provider.

Data movement must be observable, size-bounded, cancellable where possible, and governed by retention rules. Logs and traces must not become an accidental copy of sensitive prompts, audio, files, or secrets.

## Composition and configuration

`Jarvis.Host` is the only composition root. It uses the .NET Generic Host for:

- process lifetime and graceful cancellation;
- dependency injection;
- layered configuration and validation on startup;
- structured logging through `Microsoft.Extensions.Logging` abstractions.

Core does not use a service locator and should not take `IServiceProvider`. Dependencies are explicit constructor parameters. Infrastructure exposes narrowly scoped registration methods; Host chooses the concrete set.

Safe, non-sensitive defaults may be tracked in `appsettings.json`. Sensitive settings use user secrets during development and an approved external/local secret store later. See [security.md](security.md).

## Reliability principles

- All network, audio, indexing, and tool operations accept cancellation and have time budgets.
- Partial completion is explicit; a timeout is not reported as success.
- Retries are limited to safe, classified operations and include jitter and overall deadlines.
- Tool requests carry correlation identifiers across policy, execution, audit, and user feedback.
- Bounded queues provide backpressure for realtime and background event streams.
- Durable state includes a schema version and migration/backup strategy before it is relied upon.
- Startup validation fails early for invalid required configuration without printing secrets.

## Testing strategy

- Unit tests exercise Core decisions with deterministic clocks, identifiers, policies, and fake ports.
- Architecture tests enforce dependency rules and forbidden package categories.
- Contract tests verify each adapter against Core-owned semantics.
- Integration tests use temporary, narrowly scoped resources and do not require personal machine state.
- Security tests cover path confusion, argument injection, prompt injection, approval replay, output truncation, cancellation, and audit failure.
- Voice and orchestration tests use recorded/synthetic fixtures and virtual time before real devices or providers.

## Rules for adding modules

Add a new project only when at least one applies:

- a compile-time dependency must be kept out of another project;
- a component needs a different deployment, privilege, or process boundary;
- the module is cohesive and sufficiently implemented to justify independent ownership/testing;
- build performance or target-framework differences make the boundary useful.

Do not create projects solely to reserve names for roadmap items. Update this document and the [decision index](../decisions/README.md) when dependency direction or trust boundaries change.
