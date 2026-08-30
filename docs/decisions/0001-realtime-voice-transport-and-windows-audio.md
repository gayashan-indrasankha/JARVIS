# 0001 — Realtime transport and Windows audio for 0.1

- Status: Accepted
- Date: 2026-08-30
- Owners: JARVIS maintainers
- Supersedes: the roadmap's earlier placement of realtime voice in 0.3

## Context

The 0.0 roadmap placed provider/audio work after a tool-safety slice. The product direction now requires version 0.1 to prove a realtime voice conversation first, without adding computer-control authority. This introduces a provider network protocol, credential handling, Windows audio dependency, interruption semantics, and a platform target that need an explicit decision.

OpenAI exposes its Realtime API over WebSocket for server-to-server applications. Events are JSON text and PCM audio is base64-encoded. JARVIS is a local desktop process, not a browser, and already owns its audio devices and cancellation lifecycle.

## Decision

- Keep realtime conversation, audio, wake-word, and interruption contracts in `Jarvis.Core`; transport and platform types remain outside Core.
- Implement the first provider with .NET `ClientWebSocket` against the official OpenAI realtime endpoint. Do not add an OpenAI SDK dependency for this vertical slice.
- Use one persistent connection with bounded exponential reconnect. Reconnect creates a fresh provider session and discards queued audio/control messages rather than replaying a possibly duplicated turn.
- Use PCM16 mono at 24 kHz for the Core session format and OpenAI wire format.
- Use the focused `NAudio.WinMM` package for default-device capture/playback. `Jarvis.Infrastructure`, `Jarvis.Host`, and infrastructure tests target `net10.0-windows`; Core remains `net10.0`.
- Implement server semantic VAD as the natural-conversation default. For server-detected barge-in, stop hardware playback and truncate the assistant item at the audible position. Keep explicit cancellation for push-to-talk and console interruption.
- Define a local wake-word interface but ship only a disabled implementation. No dormant microphone capture occurs in 0.1.
- Load credentials from .NET user secrets or environment configuration, restrict the credential-bearing endpoint to `api.openai.com`, and never log raw audio, transcripts, protocol payloads, or credentials.

## Alternatives considered

- **Official provider SDK:** rejected for this slice because the BCL WebSocket API covers the small required surface and avoids leaking a changing SDK model into design decisions. It can replace the adapter internals later without changing Core.
- **WebRTC:** deferred. It is strong for browser/client media transport, but adds negotiation and media dependencies that are not needed for this Windows-owned PCM prototype.
- **WASAPI first:** deferred in favor of the smaller WinMM vertical slice. WASAPI, resampling, modern device discovery, and device-change handling should be evaluated using measured latency/device evidence.
- **Custom P/Invoke:** rejected because it would create avoidable unmanaged resource and audio-buffer code already maintained by NAudio.
- **Unbounded reconnect or message replay:** rejected because it risks quota consumption, duplicate turns, stale audio, and unbounded resource use.

## Consequences

The provider and audio implementations are replaceable without changing Core orchestration. The additional infrastructure test project is justified by real protocol/reconnect behavior and does not create a production boundary.

WinMM may have higher latency and less reliable format support than modern WASAPI on some devices. There is no resampler, echo cancellation, device-change recovery, or conversation resumption in 0.1. A reconnect loses remote conversational state by design. These limitations require manual device/network testing and measured evidence before audio hardening.

`NAudio.WinMM` 3.0.1 is an MIT-licensed, Windows-only dependency. It is centrally pinned and should be included in dependency/vulnerability review.

## Validation

- Core tests use fake provider, capture, and playback ports to prove streaming, push-to-talk, barge-in, reconnect-state handling, and clean shutdown.
- Infrastructure tests prove session payloads, audio event decoding, error sanitization, credential-free payloads, endpoint/credential validation, initial recovery, and reconnect after remote close.
- Restore, Debug/Release builds, full tests, and formatting run without credentials or hardware.
- [The manual voice smoke test](../testing/manual-voice-smoke-test.md) validates real provider, microphone, speaker, interruption, disconnect, and shutdown behavior.

References: [OpenAI Realtime WebSocket guide](https://developers.openai.com/api/docs/guides/realtime-websocket), [OpenAI Realtime conversations guide](https://developers.openai.com/api/docs/guides/realtime-conversations), and [NAudio.WinMM package](https://www.nuget.org/packages/NAudio.WinMM/3.0.1).
