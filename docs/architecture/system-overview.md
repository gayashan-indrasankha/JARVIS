# System overview

This is the authoritative boundary map for JARVIS through local version 0.1.1. JARVIS begins as one modular user-space .NET process. A supervised native inference child is an implementation detail, not a service tier or authority boundary.

## Physical projects

| Project | Owns | May depend on | Must not own |
| --- | --- | --- | --- |
| `Jarvis.Core` | domain values, provider/platform-neutral ports, voice orchestration, cancellation/generation semantics | .NET base class libraries | UI, HTTP, AI/model SDKs, Windows APIs, logging, persistence, native types |
| `Jarvis.Infrastructure` | local llama.cpp adapter/supervisor, sherpa-onnx adapters, Windows audio, configuration binding, structured metrics | Core and implementation packages | application composition, business authorization policy, UI |
| `Jarvis.Host` | Generic Host, configuration sources, DI composition, console commands, process lifetime | Core and Infrastructure | inference/audio protocol details, domain policy |
| test projects | behavior fakes, adapter tests, architecture checks | corresponding production layers | real destructive OS actions or mandatory model/network/device dependencies |

Dependency direction is fixed:

```text
Jarvis.Host ────────> Jarvis.Infrastructure ────────> Jarvis.Core
     └──────────────────────────────────────────────> Jarvis.Core
```

No production cycle is permitted. Adding another project requires a cohesive implemented boundary and a documented reason; conceptual modules remain namespaces/components until then.

## Runtime topology in 0.1

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
      +--> Conversation: audio, VAD, ASR, ILanguageModel, TTS, metrics
                 |
                 v
Jarvis.Infrastructure adapters
      |                 |                    |
      |                 |                    +--> NAudio WinMM devices
      |                 +--> in-process sherpa-onnx native runtime/models
      +--> supervised llama-server child -> http://127.0.0.1:<configured port>
                                                |
                                                v
                                      local Qwen3 GGUF weights
```

The model endpoint is fixed loopback HTTP with proxying, redirects, cookies, remote hosts, wildcard binds, query/user-info endpoints, and external WebSockets rejected. Managed mode starts one pinned `llama-server` process on demand and terminates its process tree at session/host disposal. External mode can use an explicitly started server only on the same fixed loopback address.

## Conceptual modules

These are architectural responsibilities, not present-day projects:

| Module | Responsibility | Status |
| --- | --- | --- |
| Conversation/Voice | dormant local activation, continuation window, local speech pipeline, generation lifetime, interruption | implemented 0.1.1 |
| Provider gateways | replaceable local language/speech implementations | initial adapters implemented |
| Tool kernel | typed invocation, validation, authorization, execution, audit | design only |
| Platform | Windows filesystem/process/shell/UI adapters behind ports | not implemented |
| Project intelligence | approved-root discovery, Roslyn analysis, retrieval/evidence | design only |
| Memory | provenance, retention, correction/deletion | not implemented |
| Events | bounded background triggers/notifications | not implemented |

## Control and data flow

User input enters through Host. Core coordinates state and calls capability ports. Infrastructure translates those calls to local engines or Windows audio. Results return as bounded domain records. No model implementation is allowed to call the OS tool layer. When tools arrive, model-proposed actions will become untrusted typed requests and must follow:

```text
proposal -> schema validation -> authorization -> approval if required
         -> execution adapter -> audit result -> model/user presentation
```

Voice inference is not a tool-execution bypass. The llama.cpp adapter has no filesystem, shell, process-control, UI, or authorization service reference and registers no model tools in 0.1.

## Configuration and storage

Tracked `appsettings.json` contains safe relative/logical defaults only. `JARVIS_` environment variables and command-line values can tune configuration; `JARVIS_HOME` selects a fully qualified non-root application-data directory. Default storage is `%LOCALAPPDATA%\JARVIS` with `Models`, `Runtime`, `Data`, `Logs`, and `Cache` subtrees. Model weights, native binaries, runtime records, logs, databases, and indexes are never tracked.

The tracked manifest identifies exact approved runtime/model artifacts. Setup is explicit. Normal application startup performs no installation or download and reports an actionable missing-component error.

## Cross-cutting rules

- Cancellation flows from host shutdown/commands through Core into wake capture, continuation timers, generation, native callbacks, audio queues, and the managed process lifetime.
- All live queues and history are bounded. Audio frames and generated response content have size limits.
- Logs/metrics contain lifecycle, numeric performance, safe configuration, and failure classes—not transcripts, prompts, responses, raw audio, hidden reasoning, or ephemeral credentials.
- Platform/provider-native objects are created, used, and disposed inside Infrastructure.
- Tests prove behavior at ports with fakes. Hardware/model integration remains an opt-in manual boundary.

Detailed subsystem rules: [voice](voice.md), [security](security.md), [tool system](tool-system.md), and [project intelligence](project-intelligence.md).
