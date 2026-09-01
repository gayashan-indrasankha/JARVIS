# 0001 — Cloud realtime transport and Windows audio

- Status: Superseded by [0002](0002-local-inference-and-speech-runtime.md)
- Date: 2026-08-30
- Superseded: 2026-08-31

## Historical decision

The first 0.1 prototype used a cloud realtime WebSocket provider plus WinMM audio to validate provider-neutral Core orchestration, interruption, push-to-talk, and device lifecycle. Those generic Core concepts and useful NAudio adapters were retained.

## Supersession

JARVIS permanently adopted a local-first, offline-capable runtime in ADR 0002. The cloud protocol, WebSocket transport, endpoint/API-key configuration, provider tests, and active operational guidance were removed. This record remains only to preserve decision history and must not be read as current architecture.
