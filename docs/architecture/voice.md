# Voice architecture

## Scope

JARVIS supports its first realtime, interruptible voice conversation on Windows in version 0.1. The vertical slice contains provider-neutral Core orchestration, an OpenAI realtime WebSocket adapter, WinMM microphone/speaker adapters, server semantic VAD, push-to-talk, and console text input. It defines a wake-word port but intentionally contains no wake-word engine.

The design separates local audio/platform work, conversation semantics, and remote provider protocols so that device libraries and AI providers can be replaced independently.

## Experience goals

- Wake locally without continuously streaming room audio to a provider.
- Begin responding with low perceived latency.
- Allow natural barge-in: detected user speech stops playback and cancels or redirects the active response.
- Recover predictably from device changes, network loss, provider errors, and false wakes.
- Make capture, streaming, and retention state visible and controllable.
- Keep tool authorization separate from conversational fluency.

## 0.1 pipeline

```text
Windows capture adapter
  -> PCM16 mono 24 kHz frames in a bounded buffer
  -> voice-session coordinator
  -> provider-neutral realtime-session port
  -> OpenAI WebSocket adapter and bounded outbound queues

Provider audio/events
  -> size-bounded protocol parser
  -> voice-session coordinator
  -> bounded PCM playback buffer
  -> Windows playback adapter
```

OpenAI semantic VAD produces speech-start/speech-stop control events in the default mode. Push-to-talk disables server turn detection and commits the audio buffer explicitly. Control events travel separately from raw audio frames. Bounded channels provide backpressure; microphone/audio frames may be dropped under pressure rather than creating unbounded latency. Stale queued frames and turns are discarded on reconnect and never replayed automatically.

## Ownership boundaries

### Core

Core owns provider-neutral session events, turn state, interruption/cancellation semantics, data limits, and ports for realtime conversation, capture, playback, and wake detection. `RealtimeVoiceCoordinator` is tested with synthetic events and fake ports and has no audio SDK, provider SDK, logging package, Windows API, or network dependency.

### Infrastructure

Infrastructure owns the OpenAI JSON/WebSocket protocol, authentication, reconnect policy, Windows audio devices, buffers, and structured operational logs. NAudio and WebSocket types do not cross into Core. The disabled wake detector implements the Core wake port until a local engine is selected. Resampling and local wake/VAD engines remain future work.

### Host/UI

Host composes the adapters and exposes a console debugging surface with `/start`, `/stop`, `/ptt`, `/send`, `/interrupt`, `/quit`, and plain-text turns. The console displays state and assistant transcript deltas but contains no conversation rules. A future UI technology must not become a Core dependency.

## Session state

The 0.1 coordinator exposes:

- **Activating** — session and devices are being prepared.
- **Listening** — user audio is captured for the active session.
- **Awaiting response** — a committed user turn is being processed.
- **Speaking** — assistant audio is playing while input still monitors for barge-in according to policy.
- **Interrupted** — playback is stopped and the active provider response is cancelled/drained.
- **Recovering** — transient device or provider reconnection is bounded by deadline/retry policy.
- **Stopped** — capture, playback, provider stream, and buffers are closed.
- **Faulted** — a non-transient provider/audio boundary failed; the process remains alive so the user can stop and restart.

`Stopped` is also the dormant 0.1 state because wake inference is disabled. Audio callbacks only copy into a bounded channel; they never mutate conversation state or execute tools.

## Wake word and activation

- `IWakeWordDetector` is the replaceable Core-owned boundary; the 0.1 implementation reports unavailable and opens no dormant microphone.
- A future wake-word engine must run locally by default.
- Dormant audio is held only in a short in-memory rolling buffer required by detection and is not persisted.
- Activation has debounce/cooldown rules and a visible/audible indicator.
- The user starts an active session explicitly and can select push-to-talk as a fallback.
- False accept/reject rates, CPU use, supported sample formats, model provenance, and licensing are evaluated before choosing an implementation.
- Wake detection grants permission to begin a conversation, not to execute a tool.

## Barge-in and cancellation

When user speech is confidently detected during playback:

1. Playback stops immediately or fades over a very short bounded interval.
2. Buffered assistant audio is discarded.
3. With server VAD, OpenAI cancels the current response and JARVIS sends `conversation.item.truncate` at the locally measured audible position. Explicit and push-to-talk interruption sends `response.cancel` as well.
4. The new user audio becomes a new or amended turn according to provider capabilities.
5. Late provider audio/events from the cancelled generation are ignored using generation identifiers.
6. Tool execution is not automatically cancelled if a side effect has started; each tool reports its cancellation and partial-effect semantics.

Metrics should measure speech-detection-to-playback-stop latency. Echo cancellation, microphone/speaker coupling, false speech starts, and headphones/no-headphones behavior require device testing.

## Provider abstraction

Core expresses capabilities rather than OpenAI event names: open/end session, append/commit input audio, submit text, receive transcripts/audio/response events, cancel a generation, and truncate conversation at the audible playback cursor.

The OpenAI adapter owns authentication, JSON translation, event ordering, connection handshakes, and bounded retry. A disconnect creates a fresh session; 0.1 does not claim protocol resumption or conversation replay. The provider has no local tool registration/execution surface, and provider session instructions never become local policy.

## Audio format and buffering

The 0.1 Core session format is PCM16 mono at 24 kHz. WinMM is asked to capture this format directly; there is no resampler. Microphone callbacks copy owned frames into a bounded channel, provider messages are capped at 1 MiB, individual audio chunks are capped at 256 KiB, and playback buffering is capped at five seconds by default. Raw audio is never written to logs or disk.

Format negotiation/resampling, buffer pooling, WASAPI evaluation, and device-change handling remain open hardening work. The provider maps the Core audio value into wire JSON so no OpenAI type appears in Core.

## Privacy and security

- The console reports active session state; a persistent graphical capture indicator is not yet implemented.
- Muting closes or discards capture at the local adapter, not merely at the provider.
- Audio leaves the device only during an explicitly active remote session and according to configured provider policy.
- Recordings and diagnostic samples are off. Assistant transcripts appear in the interactive debug console but are not structured-log fields or persisted by JARVIS.
- Audio/transcripts passed to providers are minimized and have retention disclosure.
- Device names, transcripts, wake detections, and provider events are treated as potentially sensitive.
- Remote content and transcripts are untrusted input and cannot authorize tools.

## Reliability and observability

Measure without recording content:

- activation and first-audio latency;
- capture gaps, buffer depth, dropped frames, and playback underruns;
- speech-start to playback-stop latency;
- provider connection/reconnect duration and failure classes;
- turn duration, cancellations, false wakes, and session resource use;
- selected format and device-change events with sensitive names hashed/redacted where appropriate.

Reconnect uses a bounded exponential delay and stops after eight failures by default. It reports only fixed reason codes and never replays stale audio/control messages. Provider item identifiers suppress late audio after interruption. Full session/generation correlation telemetry and device-loss recovery remain future hardening work.

## 0.1 validation

- Automated tests cover provider-independent streaming, server-VAD and explicit interruption, push-to-talk, clean shutdown, protocol translation, malformed events, sanitized errors, credential-free payloads, endpoint validation, initial connection failure, and post-connect remote closure/reconnect.
- Automated tests require no API key, network, microphone, or speaker.
- Real provider access, device compatibility, audio quality/latency, acoustic echo, barge-in responsiveness, network toggling, and device release require the [manual voice smoke test](../testing/manual-voice-smoke-test.md).
- Device changes, sleep/resume, Bluetooth transitions, echo cancellation, and local wake-word quality are not release claims for 0.1.
