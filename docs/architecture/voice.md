# Local voice architecture

Version 0.2 is a fully local, interruptible Windows voice vertical slice with dormant keyword activation and permission-controlled tools. Core owns orchestration, the provider-neutral agent loop, and tool contracts; Infrastructure owns llama.cpp, sherpa-onnx, NAudio, tool adapters, native lifetime, and local HTTP details. The wake-word and model ports remain replaceable.

## Pipeline

```text
Windows microphone (16 kHz PCM16 mono)
  -> while Sleeping: sherpa-onnx Zipformer KWS (one CPU thread)
  -> accepted "Jarvis" wake; release dormant capture
  -> initialize/warm conversation components
  -> bounded capture stream
  -> Silero VAD (CPU, active even while JARVIS speaks)
  -> streaming Zipformer ASR (CPU; partial and final text)
  -> IAgentRuntime
  -> llama.cpp schema-constrained plan (respond or typed tool proposal)
  -> ToolDispatcher: validation -> authorization -> bounded execution -> audit
  -> llama.cpp loopback streaming final generation (Qwen3 /no_think)
  -> incremental response segmenter
  -> Kokoro TTS (CPU, ordered bounded queue, 24 kHz PCM16 mono)
  -> Windows speaker
```

Text input bypasses capture/VAD/ASR but uses the same agent, tool, segmenter, and—unless disabled—TTS path. Push-to-talk starts capture only after `/ptt`, finalizes ASR at `/send`, and otherwise follows the same pipeline. `/start` bypasses wake detection for diagnostics. When push-to-talk is used while sleeping, Host starts a push-to-talk conversation without requiring the phrase.

## Wake and continuation lifecycle

```text
Stopped -> Sleeping -> Activating -> Listening <-> Conversation
                 ^                         |
                 +---- idle timeout -------+
```

`Sleeping` runs only microphone capture and the keyword spotter. A hit releases the microphone before Core enters `Activating`, preventing two capture owners. Core then initializes the LLM and required VAD/ASR/TTS components, enters `Listening`, starts conversation capture for VAD mode, and optionally speaks a fixed local acknowledgement. The acknowledgement uses TTS directly and never asks the LLM.

The continuation timer starts after activation and refreshes on speech, push-to-talk, text submission, interruption, and completed turns. It expires only while no speech, push-to-talk capture, or generation is active. Expiry stops conversation capture/playback, clears bounded history, transitions to `Sleeping`, and rearms keyword capture. Conversation engines may remain initialized until shutdown so a later wake is warm; initial sleep never initializes them.

Cooldown suppresses a new detection that arrives too soon after the last accepted wake. Suppressed duplicates increment only a numeric false-activation counter. `/falsewake` lets a tester label a false activation and rearm sleep without logging audio or recognized content.

## Core contracts and ownership

`IAgentRuntime`, `ILanguageModel`, `IAgentPlanner`, `IToolDispatcher`, `IVoiceActivityDetector`, `ISpeechRecognizer`, `ISpeechSynthesizer`, `IAudioCapture`, `IAudioPlayback`, `IWakeWordDetector`, and `IVoiceMetrics` expose bounded domain records and cancellation tokens. They contain no native handles, HTTP messages, llama.cpp request objects, sherpa-onnx types, NAudio types, or logging framework types.

`RealtimeVoiceCoordinator` owns:

- session states (`Stopped`, `Sleeping`, `Activating`, `Listening`, `AwaitingResponse`, `Speaking`, `Interrupted`, `Faulted`);
- bounded in-memory conversation history and response size limits;
- a ten-frame microphone pre-roll for retaining speech onset;
- VAD-to-ASR turn finalization and partial/final transcript notifications;
- correlated agent/tool generation and a single ordered synthesis consumer;
- generation IDs, linked cancellation, stale-output rejection, and playback interruption;
- structured content-free timing metrics.

The coordinator initializes only required components. Initial dormant mode does not load the LLM, VAD, ASR, or TTS. Text-only mode does not load speech models or open devices; speech output can be independently disabled.

## Local language inference

`LlamaCppLocalLanguageModel` implements Core's final language-generation port. The adapter requests a persistent supervised connection and streams server-sent completion deltas from fixed loopback HTTP. It adds Qwen's `/no_think` control to the user turn, accepts only visible `content`, ignores `reasoning_content`, and bounds each event and aggregate visible output.

`LlamaCppAgentPlanner` is a separate Infrastructure adapter. It requests non-streaming schema-constrained JSON with exactly one of `respond` or one reviewed tool contract. It registers no llama.cpp native executable tool and holds no executor/authorization/audit reference. Core's `ToolEnabledAgentRuntime` applies a maximum-step and identical-call policy, dispatches proposals through the tool kernel, labels successful results as untrusted data, then delegates confirmed history to the streaming language adapter. A malformed plan receives one constrained repair request; a second failure executes nothing. Failed, denied, invalid, repeated, timed-out, or unavailable outcomes use deterministic non-success wording instead of allowing the model to hallucinate completion.

Managed `LlamaServerSupervisor`:

1. validates the logical model and required runtime/model files under `JARVIS_HOME`;
2. generates a random ephemeral per-process credential;
3. starts `llama-server.exe` without a shell or visible window and with a minimal allowlisted child environment rather than inherited credentials;
4. passes model, fixed `127.0.0.1` bind, port, context, GPU layers, threads, offline mode, disabled reasoning/agent/tools/UI/MCP proxy/slots, restrictive CORS, and one parallel slot as separate argument-list values;
5. drains stdout/stderr but records only classified diagnostic codes;
6. polls `/health` within a bounded timeout;
7. retries at 4096 context once if the configured 8192 startup fails, with no restart loop;
8. detects unexpected exit and kills the entire process tree on cancellation/disposal, with bounded exit and diagnostic-drain waits.

External mode skips launch and requires an already healthy server at exactly `http://127.0.0.1:<port>/`. Remote or wildcard hosts are invalid. External-server authentication is not configured in 0.1; its operator is responsible for the explicitly started local process.

## Local speech implementations

`SherpaOnnxKeywordSpotter` uses the pinned 3.3M-parameter English Zipformer GigaSpeech keyword model with int8 encoder/decoder/joiner, BPE tokens `▁JA R VI S @JARVIS`, CPU provider, and one inference thread. The score, threshold, cooldown, continuation window, and enable flag are validated configuration. Each PCM frame is converted and decoded transiently; no dormant audio is written. Default score `1.5`, threshold `0.25`, cooldown `3 s`, and continuation `30 s` are provisional physical-test values, not accuracy claims.

`SherpaOnnxVoiceActivityDetector` uses the pinned Silero model with 512-sample windows at 16 kHz. Threshold, minimum speech/silence, and maximum speech duration are configured and validated. It buffers at most one partial native window plus the coordinator's bounded pre-roll.

`SherpaOnnxSpeechRecognizer` uses the small int8 streaming English Zipformer transducer. Audio is decoded incrementally; changed partial text can be displayed and final text is produced/reset at speech end. Transcripts are in memory only.

`SherpaOnnxKokoroSpeechSynthesizer` uses the English Kokoro ONNX package, built-in `bm_george` speaker ID 9, configurable speed, CPU provider, and native progress callback. Callback audio is copied into bounded 64 KiB-or-smaller chunks and a channel capacity of eight. Segments are never synthesized concurrently, so playback order cannot change. Cancellation stops the callback cooperatively; native objects and generated buffers are disposed.

NAudio WinMM adapters retain default/configured numeric input/output devices. Capture and playback validate the exact Core formats. Playback has a bounded buffer and interruption resets the output hardware buffer before accepting a newer generation.

## Segmentation and output safety

The segmenter consumes generation deltas and emits short speech units at sentence boundaries, then clause/maximum-size boundaries. It delays a small suffix so hidden-reasoning/code markers split across deltas cannot leak. Before user display/TTS it removes code fences and contents, `<think>`/`<analysis>` contents, inline formatting/control characters, and tool/function metadata-shaped segments. The adapter also discards model `reasoning_content`; defense therefore exists at both protocol and presentation boundaries.

No sanitizer is a general trust boundary. Model output and tool observations remain untrusted. Content from files, repositories, terminals, websites, and documents cannot change policy, grant permission, or invoke an action; every new proposal traverses the same validation/authorization dispatcher. JARVIS does not narrate an action as successful unless the typed executor outcome confirms success.

## Barge-in and cancellation invariant

Capture and VAD remain active while output plays. At speech start:

1. Core increments the generation ID before new work;
2. the old linked generation token is cancelled, stopping LLM streaming and pending/current TTS;
3. playback is interrupted and buffered hardware audio is cleared;
4. ASR resets and receives bounded pre-roll plus the new utterance;
5. late tokens/audio must match the current generation ID or are rejected;
6. finalized new speech creates the next generation.

`/interrupt` performs the same invalidation/cancellation/clear sequence without creating a user turn. A cancelled generation is never added as completed assistant history. `/stop`, `/quit`, EOF, and Ctrl+C cancel active pumps and generation, stop playback, dispose native engines/devices, and terminate the managed child.

## Resource and latency policy

The target profile is 16 GB RAM and RTX 4050 Laptop GPU with 4 GB VRAM. Defaults use one Qwen3-4B Q4_K_M model, context 8192, tunable 24-layer GPU offload, eight llama threads, one parallel request, CPU speech engines, bounded queues, and bounded history. The supervisor may fall back once to context 4096; it never attempts 32K automatically. Users can lower GPU layers or select the CPU runtime when memory/driver constraints demand it.

Metrics are keyword-frame detection latency, cumulative false activations, wake-to-listening latency, model readiness/load time, prompt processing time when reported, first-token/warm-first-token latency, server-reported tokens/second, ASR finalization, TTS first audio, barge-in playback stop, and end-to-end turn time. Values contain no transcript/audio content. “Warm first token” measures a generation request after the managed model endpoint is ready; it does not include cold model startup.

## Failure behavior and validation

Missing assets produce stable actionable component codes and instruct setup rather than crashing host startup. Model-load, GPU-OOM, and port-in-use failures retain distinct sanitized codes; model/port failures do not trigger a pointless context retry. Unhealthy server, capture/native, and invalid stream failures transition the active session to a controlled error or return a safe console message. There is no unbounded retry/replay.

Unit tests use fake ports/processes/HTTP handlers; they verify wake state, lazy initialization, cooldown, continuation, timeout, cancellation, push-to-talk coexistence, ordering, fallback, sanitization, and stale rejection without devices/models/network. Hardware, GPU, acoustic, latency, and disconnected-operation claims require the [voice smoke test](../testing/manual-voice-smoke-test.md) and [wake-word matrix](../testing/manual-wake-word-test-matrix.md).

## Deferred work

- echo cancellation, hot-plug/device switching, resampling, and WASAPI evaluation;
- physical wake threshold/distance/noise profiling and idle battery measurement;
- accent/noise benchmarks and alternate local ASR profiles;
- packaging/signed update workflow and an authoritative hash for every upstream archive;
- tool writes/deletion/interactive approvals, project indexing, memory, UI automation, and persistence.
