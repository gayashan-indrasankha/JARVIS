# Security and privacy architecture

JARVIS treats model output, speech recognition, configuration, local files, future tool proposals, and native-process diagnostics as untrusted input. Local execution improves privacy and availability but does not make a model authoritative or a native runtime harmless.

## Assets and adversaries

Protected assets include user files, credentials, private audio/transcripts, project source, device control, future memory/audit records, local model/runtime integrity, and the user's trust in reported actions. Relevant threats include malicious model output, prompt injection, unsafe future tools, path/command injection, hostile local processes connecting to a listening inference server, native/model supply-chain compromise, secret/log leakage, accidental data retention, and resource exhaustion.

JARVIS runs as the interactive user, never in the kernel and never as an administrator by default. It does not claim isolation from malware already executing as that same user. Loopback binding reduces network exposure but is not authentication against all same-user local processes; managed-mode ephemeral authentication adds defense in depth.

## Trust boundaries

```text
User/audio/text
  -> Host input validation
  -> provider-neutral Core orchestration
  -> Infrastructure native/model adapters
       -> in-process sherpa-onnx
       -> supervised llama-server on authenticated 127.0.0.1 HTTP

Model output (untrusted)
  -X-> direct filesystem/process/shell/UI/network access
  -> future typed tool proposal -> authorization -> approval -> adapter -> audit
```

The 0.1.1 model request has no tool definitions and the model adapter references no OS action service. Wake inference receives only transient microphone frames and has no LLM, network, filesystem, or tool reference. Voice inference cannot bypass the future authorization kernel.

## Offline/network policy

The normal production runtime is offline-capable after explicit installation:

- no external HTTP or WebSocket endpoint;
- no cloud AI SDK or credential;
- no telemetry, analytics, crash upload, updater, or background download;
- no transcript, prompt, response, raw audio, file, screenshot, repository, or tool-result upload;
- local language-model traffic is constrained to exact `http://127.0.0.1:<validated-port>/`;
- HTTP proxy use, redirects, cookies, user-info, queries, fragments, wildcard binds, remote hosts, and hostname resolution are disabled/rejected.

The guarantee covers JARVIS production paths. `scripts/setup-local-ai.ps1` is an explicitly invoked external-download workflow and NuGet restore is a development/build workflow. The native artifacts and operating system have their own trust boundaries. Disconnecting the network during the manual test verifies behavior, not immunity from compromised dependencies.

## Local inference process

Managed llama.cpp launches directly with `UseShellExecute=false`, a separate argument list, redirected diagnostics, no visible window, fixed loopback bind, offline mode, disabled agent/tools/UI/MCP proxy, restrictive CORS, and no model-controlled arguments. The inherited environment is discarded; the child receives only required Windows runtime/temp paths plus its cryptographically random llama credential. It therefore does not inherit unrelated cloud, source-control, proxy, model-router, prompt-log, MCP, media, or tool credentials/settings. The configured model ID selects one known relative path; arbitrary executable/model paths are not accepted. The child credential exists only in its environment and the local HttpClient header. It is not placed on the process command line, persisted, or logged.

Health checks and startup are bounded. There is at most one context fallback and no automatic restart storm. Cancellation and shutdown kill the entire managed process tree. Raw child output is drained to avoid deadlock but only recognized classes (`gpu_out_of_memory`, `model_load_failed`, `port_in_use`) enter JARVIS logs. Prompts, responses, hidden reasoning, command lines, environment blocks, and raw native diagnostics are excluded.

External mode is explicit and loopback-only. Because 0.1 has no configuration for an external server credential, users should not expose or share that process; JARVIS validates health but does not own its lifecycle.

## Paths, configuration, and artifacts

`JARVIS_HOME` must be fully qualified, contain no control character, be outside Git, not traverse an existing reparse point, and not be a filesystem root. Asset resolution rejects rooted relative values, canonicalizes under the expected subtree, and rejects existing reparse-point components, preventing lexical or junction/symlink escape. Tracked configuration stores logical IDs and numeric safe defaults, not absolute machine paths or identity.

Model weights, ONNX files, native runtime archives/binaries, local installation metadata, logs, caches, databases, indexes, and environment-specific settings are ignored and belong under `%LOCALAPPDATA%\JARVIS` (or explicit `JARVIS_HOME`). The tracked manifest pins upstream URLs, versions, names, licenses, and authoritative SHA-256 values where available. Setup verifies known hashes, cleans failed partial downloads, validates archive paths/types, extracts model archives into a private staging directory before moving them into place, and explicitly warns for the Zipformer archive whose upstream authoritative hash is unavailable. Setup installs nothing globally and never runs during app startup.

No API key or user secret is part of runtime configuration. Environment variables may reveal non-secret configuration to same-user processes and must not be repurposed for future secrets without a dedicated design.

## Private data and logging

Raw microphone frames—including audio captured while sleeping—synthesized audio, ASR transcripts, prompts, model responses, hidden reasoning, and conversation history are transient memory only by default. The keyword spotter converts each bounded frame for immediate in-memory inference, retains no recording buffer beyond native streaming state, and releases dormant capture before conversation capture begins. Structured logs and `IVoiceMetrics` contain event IDs, component/failure codes, safe numeric tuning, durations, rates, and a cumulative false-activation count. They exclude content and native diagnostic lines. Console transcript display is an intentional interactive debugging feature; shell redirection can persist it and is a user privacy choice.

Error messages shown to users are stable/actionable but do not expose stack traces, raw model responses, environment variables, child command lines, or local personal paths. Development logs and crash dumps can still capture process memory; production packaging must document and minimize dump collection.

## Resource and denial-of-service controls

- exact audio formats and maximum frame/text/instruction/event sizes;
- bounded notification, speech-segment, callback-audio, and playback queues;
- bounded conversation message/character history and maximum output tokens;
- single ordered TTS producer and one llama parallel request;
- bounded native startup/health polling and linked cancellation;
- tunable context, GPU layers, threads, audio buffers, VAD limits, and TTS speed;
- one keyword-spotter inference thread, configurable score/threshold, cooldown suppression, and a bounded continuation timer.

Native model loading can still consume substantial memory and time. GPU OOM is reported with guidance to lower GPU layers/use CPU; it never justifies a wider network bind or a disabled safety check.

## Future tool authorization invariant

When computer-control tools arrive, every side effect must enter one non-bypassable typed dispatcher. Policy considers tool identity, normalized target, arguments, sensitivity, user/session context, and requested scope. Outcomes are allow, deny, or explicit approval. Execution occurs only after allow/approval, and every attempt/result is audited with content minimization. Models never receive direct shell, filesystem, process, Windows UI, database, or hardware handles.

## Verification

Automated architecture tests reject outer/model/native/network dependencies from Core. Configuration/path/loopback tests reject unsafe values. Supervisor tests cover lifecycle, fallback, cancellation, and missing assets without real processes. Orchestration tests cover barge-in and stale output. Repository gates scan for secrets, external endpoint remnants, tracked model/runtime artifacts, and warnings.

Physical device release, GPU behavior, local process termination, offline operation, and privacy log inspection are explicitly manual in [the smoke test](../testing/manual-voice-smoke-test.md).

## Deferred decisions

- signed distribution and verified native/model update channel;
- crash-dump/diagnostic retention policy;
- audit persistence encryption and user deletion controls;
- Windows sandboxing/job-object hardening for native inference;
- external local-server authentication configuration;
- concrete authorization/approval UI for OS tools.
