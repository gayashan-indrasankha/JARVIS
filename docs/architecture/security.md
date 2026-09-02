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
  -> typed proposal -> validation/normalization -> authorization -> bounded adapter -> audit
```

The local planner receives only reviewed tool descriptions and closed JSON schemas. It references no dispatcher, authorization/audit service, project index, or OS adapter. Core's agent loop translates a proposal into the single dispatcher path. Wake inference receives only transient microphone frames and has no LLM, network, filesystem, or tool reference. Voice and text input use the same agent boundary.

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

Project Intelligence stores its SQLite/FTS index under `JARVIS_HOME\Data\ProjectIntelligence`, never inside an analyzed repository. The index contains private source-derived text, hashes, paths relative to the approved repository, symbols, and relationships. It is local user data, is not logged or transmitted, and may be deleted while JARVIS is stopped. Repository roots remain opt-in and tracked configuration contains none.

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
- one-to-eight configured tool steps per user request, identical-call suppression, one structured-output repair attempt, per-tool deadlines, bounded traversal/output, and a central result cap.
- bounded local health, planning, and generation requests; a nonresponsive loopback server cannot hold a turn indefinitely.

Native model loading can still consume substantial memory and time. GPU OOM is reported with guidance to lower GPU layers/use CPU; it never justifies a wider network bind or a disabled safety check.

## Tool authorization invariant

Every OS observation or side effect enters one non-bypassable typed dispatcher. The trusted registry—not the model—selects the request type, schema, authorization category, executor, deadline, and result cap. Unknown or malformed calls stop before authorization; unsafe normalized values and duplicates stop before authorization; execution starts only after an `Allowed` decision.

The initial policy allows bounded `SAFE_READ` and optionally `SAFE_LOCAL_ACTION`. `Tools:Enabled=false` denies the catalog and `Tools:AllowSafeLocalActions=false` denies visible local actions. `CONFIRM_REQUIRED` and `STRONG_CONFIRM_REQUIRED` fail closed because 0.2 has no interactive grant surface. `DENIED` never runs. JARVIS neither requests elevation nor implements writes, deletion, termination, credential access, generic shell, network tools, or UI automation.

Filesystem tools require canonical targets below explicit existing approved local-drive roots; reject UNC/network roots, existing reparse points, credential-sensitive paths, alternate data streams, DOS short-name aliases, and trailing-dot/space aliases; and bound enumeration/file reads. Document opening uses a conservative non-executable extension allowlist so an arbitrary Windows association cannot become an execution path. Git status rejects `.git` indirection files, pins its Git directory and work tree to validated paths, and ignores submodules. Tracked configuration approves no root. The safe-command contract is a fixed diagnostic enum; executors resolve only reviewed executable IDs to fully qualified paths and never concatenate model strings, start PowerShell/`cmd.exe`, inherit the full environment, or accept a model-selected executable/argument.

Every dispatcher terminal path writes an audit event with IDs, tool, decision, timestamps, status, success, sanitized error class, and cancellation/timeout/truncation flags. It deliberately omits arguments, paths, file/process/command content, prompts, responses, and raw exceptions. Structured logs are not yet a durable tamper-resistant audit store.

Tool results are untrusted. Files, repositories, terminal output, websites, documents, and process names can contain prompt injection. Core labels observations as untrusted data and injects a policy that content cannot override JARVIS policy, authorize an action, or invoke a tool. Each subsequent proposal still traverses the full dispatcher. A local model is never trusted merely because it is local.

## Untrusted repository analysis

Project analysis reuses the exact dispatcher invariant. `analyze_project` is a `SAFE_LOCAL_ACTION`; every query is `SAFE_READ`. Validation canonicalizes a direct Git repository under an approved root before authorization. The model has no direct reference to discovery, Roslyn, SQLite, Git, the filesystem, or the watcher.

Discovery is bounded by file count, per-file bytes, total bytes, accepted text types, cancellation, and a no-reparse traversal. It excludes Git/build/generated/package/IDE/cache trees plus credential names/types and `.env*` before reading. Project XML prohibits DTDs and external entities. The implementation deliberately does not use `MSBuildWorkspace` or invoke MSBuild because evaluation could load hostile imports/tasks. It never restores, builds, runs generators/tests/scripts, loads repository binaries, or accepts a repository-selected process/argument.

Only a fixed read-only Git status invocation executes, through the existing direct executable resolver, minimal environment, fixed `--git-dir`/`--work-tree`, disabled prompt/config features, bounded output, and cancellation. Repository hooks and submodules are not run.

SQLite operations use parameterized values. FTS query syntax is built from at most eight bounded alphanumeric/underscore tokens; arbitrary query operators are not passed through. Transactions replace one repository snapshot atomically. Retrieval caps candidates, traversal depth, excerpts, total evidence characters, tool result characters, and local-model context. Returned source is explicitly untrusted and every project fact must cite an actual indexed file/line/hash/snapshot.

## Verification

Automated architecture tests reject outer/model/native/network/Roslyn/SQLite dependencies from Core and reject tool dispatcher/authorization/audit/project-index references from model adapters. Tool tests cover closed schemas, unknown/malformed proposals, validation/authorization order, denial, approved-root/credential/reparse defenses, fixed command mappings, duplicate calls, cancellation, timeout, truncation, audit completeness, prompt-injection labeling, and temporary-directory behavior. Project tests prove safe static loading, inert hostile targets, DTD denial, exclusions, exact evidence, incremental snapshots, retrieval/context limits, cancellation, and debounce. Configuration/path/loopback tests reject unsafe values. Supervisor and voice tests cover lifecycle, fallback, cancellation, barge-in, and stale output. Repository gates scan for secrets, external endpoint remnants, tracked model/runtime artifacts, and warnings.

Physical device release, GPU behavior, local process termination, offline operation, and privacy log inspection are explicitly manual in [the smoke test](../testing/manual-voice-smoke-test.md).

## Deferred decisions

- signed distribution and verified native/model update channel;
- crash-dump/diagnostic retention policy;
- audit persistence encryption and user deletion controls;
- Windows sandboxing/job-object hardening for native inference;
- external local-server authentication configuration;
- concrete authorization/approval UI for confirmation-class OS tools;
- durable tamper-resistant audit persistence, retention, encryption, and user review.
