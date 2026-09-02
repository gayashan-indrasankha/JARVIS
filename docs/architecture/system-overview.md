# System overview

This is the authoritative boundary map for JARVIS through local version 0.3. JARVIS is one modular user-space .NET process. A supervised native inference child is an implementation detail, not a service tier or authority boundary.

## Physical projects

| Project | Owns | May depend on | Must not own |
| --- | --- | --- | --- |
| `Jarvis.Core` | domain values, provider/platform-neutral ports, voice orchestration, typed tool contracts/agent loop, project evidence/query contracts, authorization categories, cancellation/generation semantics | .NET base class libraries | UI, HTTP, AI/model SDKs, Roslyn, SQLite, Windows APIs, logging, persistence, native types |
| `Jarvis.Infrastructure` | local llama.cpp adapter/supervisor/planner, sherpa-onnx, Windows audio/tools, safe repository discovery, static project parsing, Roslyn analysis, SQLite/FTS retrieval, trusted registry, validation/policy/audit, configuration, structured metrics | Core and implementation packages | application composition, UI, model-defined policy/catalog, repository code execution |
| `Jarvis.Host` | Generic Host, configuration sources, DI composition, console commands, process lifetime | Core and Infrastructure | inference/audio protocol details, domain policy |
| test projects | behavior fakes, adapter tests, architecture checks | corresponding production layers | real destructive OS actions or mandatory model/network/device dependencies |

Dependency direction is fixed:

```text
Jarvis.Host ────────> Jarvis.Infrastructure ────────> Jarvis.Core
     └──────────────────────────────────────────────> Jarvis.Core
```

No production cycle is permitted. Adding another project requires a cohesive implemented boundary and a documented reason; conceptual modules remain namespaces/components until then.

## Runtime topology in 0.3

```text
Interactive user
      |
      v
Jarvis.Host console + Generic Host lifetime
      |
      v
Jarvis.Core RealtimeVoiceCoordinator
      |
      +--> Sleeping: IWakeWordDetector + microphone only
      |
      +--> Conversation: audio, VAD, ASR, IAgentRuntime, TTS, metrics
                 |
                 v
Jarvis.Infrastructure adapters
      |                 |                    |                 |
      |                 |                    |                 +--> bounded Windows tool adapters
      |                 |                    +--> NAudio WinMM devices
      |                 +--> in-process sherpa-onnx native runtime/models
      +--> supervised llama-server child -> http://127.0.0.1:<configured port>
                                                |
                                                v
                                      local Qwen3 GGUF weights

Approved Git repository
      |
      v
bounded discovery -> static project XML -> Roslyn AdhocWorkspace
      |                                      |
      +--> SHA-256 snapshot                  +--> symbols/relationships/facts
                         \                   /
                          -> local SQLite/FTS5 index
                                      |
                                      v
                          bounded evidence ProjectTools
                                      |
                                      v
                           existing typed dispatcher -> local Qwen
```

The model endpoint is fixed loopback HTTP with proxying, redirects, cookies, remote hosts, wildcard binds, query/user-info endpoints, and external WebSockets rejected. Managed mode starts one pinned `llama-server` process on demand and terminates its process tree at session/host disposal. External mode can use an explicitly started server only on the same fixed loopback address.

## Conceptual modules

These are architectural responsibilities, not present-day projects:

| Module | Responsibility | Status |
| --- | --- | --- |
| Conversation/Voice | dormant local activation, continuation window, local speech pipeline, generation lifetime, interruption | implemented 0.1.1 |
| Provider gateways | replaceable local language/speech implementations | initial adapters implemented |
| Tool kernel | typed invocation, closed-schema validation, authorization, bounded execution, audit, loop protection | implemented 0.2 |
| Platform | approved-root filesystem, fixed application/process/system/Git/diagnostic adapters | initial bounded slice implemented 0.2; writes/destructive/UI absent |
| Project intelligence | approved-root static discovery, Roslyn analysis, SQLite/FTS retrieval/evidence | implemented 0.3 for C#/.NET |
| Memory | provenance, retention, correction/deletion | not implemented |
| Events | bounded background triggers/notifications | not implemented |

## Control and data flow

User input enters through Host. Core coordinates state and calls capability ports. Infrastructure translates those calls to local engines, Windows audio, or the tool boundary. Results return as bounded domain records. No model implementation is allowed to call the OS layer. Model-proposed actions are untrusted typed requests and follow:

```text
proposal -> exact catalog lookup -> schema validation/normalization
         -> authorization -> bounded execution adapter
         -> content-minimized audit -> untrusted result -> model/user presentation
```

Voice inference is not a tool-execution bypass. A separate llama.cpp planner can emit schema-constrained proposals but has no dispatcher, authorization, audit, filesystem, process, shell, or UI service. The provider-neutral Core agent loop is the only bridge to `IToolDispatcher`; final text is generated only after an actual typed outcome is available.

## Configuration and storage

Tracked `appsettings.json` contains safe relative/logical defaults only. `JARVIS_` environment variables and command-line values can tune configuration; `JARVIS_HOME` selects a fully qualified non-root application-data directory. Tool filesystem access has no tracked root and is disabled by absence until the user supplies `Tools:AllowedRoots`. Project indexes are created only after an authorized `analyze_project` call and live under `Data\ProjectIntelligence`. Default storage is `%LOCALAPPDATA%\JARVIS` with `Models`, `Runtime`, `Data`, `Logs`, and `Cache` subtrees. Model weights, native binaries, runtime records, logs, databases, and indexes are never tracked.

The tracked manifest identifies exact approved runtime/model artifacts. Setup is explicit. Normal application startup performs no installation or download and reports an actionable missing-component error.

## Cross-cutting rules

- Cancellation flows from host shutdown/commands through Core into wake capture, continuation timers, generation, native callbacks, audio queues, and the managed process lifetime.
- All live queues and history are bounded. Audio frames and generated response content have size limits.
- Logs/metrics contain lifecycle, numeric performance, safe configuration, counts, context size, and failure classes—not transcripts, prompts, responses, repository queries/content/paths, raw audio, hidden reasoning, or ephemeral credentials.
- Tool and project observations are bounded and explicitly labeled untrusted; tool audits omit arguments, paths, content, process output, queries, and exception text.
- Platform/provider-native objects are created, used, and disposed inside Infrastructure.
- Tests prove behavior at ports with fakes. Hardware/model integration remains an opt-in manual boundary.

Detailed subsystem rules: [voice](voice.md), [security](security.md), [tool system](tool-system.md), and [project intelligence](project-intelligence.md).
