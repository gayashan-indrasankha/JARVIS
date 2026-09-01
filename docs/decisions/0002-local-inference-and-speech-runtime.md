# 0002 — Local inference and speech runtime

- Status: Accepted
- Date: 2026-08-31
- Owners: JARVIS maintainers
- Supersedes: [0001](0001-realtime-voice-transport-and-windows-audio.md)

## Context

JARVIS 0.1 originally depended on an external realtime voice provider. The permanent product direction requires normal operation with no paid API, cloud AI, credential, or Internet connection after explicit setup. Core must remain provider/platform neutral, native/model artifacts cannot enter Git, local inference must fit a Windows 11 laptop with 16 GB RAM and RTX 4050 Laptop GPU with 4 GB VRAM, and voice must retain streaming and barge-in.

One monolithic native engine does not provide all required language, streaming ASR, VAD, and TTS capabilities through a mature C# surface. Running a network service beyond the computer or adding a Python sidecar would expand operational/security dependencies.

## Decision

Use a modular local pipeline:

- Core defines `ILanguageModel`, VAD, streaming ASR, TTS, audio, metrics, and wake-word ports and owns cancellation/generation semantics.
- Infrastructure uses a pinned llama.cpp `llama-server` as a supervised child process. Its HTTP protocol is private to the adapter, binds exactly to `127.0.0.1`, uses a random managed-process credential, has bounded health/startup, and is terminated as a process tree.
- The default LLM is `Qwen/Qwen3-4B-GGUF`, `Q4_K_M`, with 8192 context, one 4096 fallback, tunable GPU layers/threads, and non-thinking mode. No automatic 32K attempt occurs.
- Infrastructure uses pinned `org.k2fsa.sherpa.onnx` for Silero VAD, a small int8 streaming English Zipformer ASR, and local Kokoro English TTS. Speech engines run on CPU initially.
- Keep NAudio WinMM device adapters behind Core audio ports.
- Store models/runtime/data under `%LOCALAPPDATA%\JARVIS` or validated `JARVIS_HOME`; track only a manifest.
- Downloads happen only through an explicit PowerShell setup workflow. Normal startup never downloads.
- Remove all cloud runtime/provider, endpoint, API-key, credential, reconnect, test, and active documentation paths.

## Alternatives considered

### Continue cloud realtime speech-to-speech

Rejected because it requires external connectivity, service credentials/cost, and transmission of private audio/conversation. It conflicts with the permanent runtime objective.

### Embed llama.cpp directly through a C# binding

Deferred. In-process bindings reduce IPC latency but place native crashes, ABI upgrades, GPU lifetime, and large native dependencies inside the host. A supervised helper creates a clean replaceable/process-lifetime boundary at the small cost of loopback serialization.

### Python speech/model sidecars

Rejected for 0.1 because Python environments and package/native compatibility add deployment and supervision burden when maintained .NET/native integrations exist.

### One model/runtime for ASR, LLM, and TTS

Rejected for the target 4 GB VRAM/16 GB RAM profile. Small dedicated CPU speech models allow the GPU budget and one large-model slot to prioritize conversation latency.

### Larger or thinking LLM/context

Rejected as the baseline. A 4B Q4 model and bounded context trade some quality/long-history capacity for startup, memory, and realtime stability. Replacement remains possible through Core ports and the manifest.

## Consequences

Benefits:

- normal runtime has no external AI-network or credential dependency;
- private voice/text remains local by architecture;
- provider/model/native details do not leak into Core;
- process and native components are independently replaceable and testable through fakes;
- model setup/storage/license pins are explicit and reviewable;
- stale speech is rejected deterministically with generation IDs.

Costs and risks:

- users explicitly download several large artifacts and compatible GPU runtime files;
- native/model supply chain remains trusted; one selected ASR archive lacks an authoritative upstream checksum;
- local quality/latency depend on hardware, drivers, acoustics, and tuning;
- loopback IPC and multiple native engines add memory/latency;
- managed authentication does not apply to an operator-owned external server in 0.1;
- Kokoro/native cancellation is cooperative and echo cancellation is deferred.

Artifact metadata is recorded in `config/local-model-manifest.json`; license sources and obligations, including GPL-3.0 eSpeak NG data and separate NVIDIA CUDA redistribution terms, are inventoried in `docs/security/third-party-licenses.md`. Distribution packaging must preserve exact upstream notices, generate a bill of materials, and repeat legal review.

## Validation

- architecture tests keep llama.cpp/sherpa/NAudio/HTTP/Windows types out of Core;
- configuration and endpoint tests reject non-loopback values;
- process tests cover startup/fallback/cancellation/cleanup/missing assets without native binaries;
- orchestration tests cover ordering, push-to-talk, barge-in, stale rejection, and shutdown;
- source/repository scans reject cloud endpoints, credentials, tracked weights/binaries, and external runtime paths;
- Debug/Release builds, all tests, and formatting must pass with no warnings;
- the manual smoke test validates installed models, GPU, physical audio, offline repeated turns, cleanup, privacy, and approximate latency.
