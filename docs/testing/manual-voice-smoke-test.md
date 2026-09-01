# Local/offline voice smoke test

These tests require a Windows machine, local assets, physical audio devices, and (for GPU checks) NVIDIA tooling. Automated tests do not prove these behaviors. Record results separately; do not commit device identities, transcripts, logs, or hardware identifiers.

Use PowerShell 7 from the repository root. Unless a test says otherwise, set `Voice:Enabled=true`, `Voice:SpeechOutputEnabled=true`, use headphones, and leave managed mode on `127.0.0.1:18080`.

Version 0.1.1 wake activation has its own required [manual wake-word matrix](manual-wake-word-test-matrix.md). Complete that matrix in addition to this voice pipeline test; neither document authorizes an automated keyword-accuracy claim.

## 1. Local model installation

1. **Preconditions:** Internet connected for this explicit setup step; enough disk space; no model/runtime files inside the repository.
2. **Action:** Run `.\scripts\setup-local-ai.ps1 -DownloadRuntime -DownloadModels`, then run it again.
3. **Expected:** Required `%LOCALAPPDATA%\JARVIS` (or `JARVIS_HOME`) directories/assets exist; verified artifacts pass hashes; the second run safely reuses them; licenses are displayed; nothing is installed system-wide.
4. **Failure:** checksum/extraction error, duplicate/corrupt assets, repository model/binary files, global installation, or missing actionable warning for the checksum-less ASR archive.
5. **Inspect:** setup console, `config\local-model-manifest.json`, the configured JARVIS data root, and `git status --short`.
6. **Network:** Connected only for this explicit download workflow.
7. **Hardware:** No microphone/speaker/GPU required; sufficient local disk is required.

## 2. llama.cpp health

1. **Preconditions:** setup complete; port 18080 free.
2. **Action:** Start JARVIS, enter `/start`, then in another console run `.\scripts\diagnose-local-ai.ps1`.
3. **Expected:** session reaches `Listening`; diagnostics reports `llama-server health ... ready`; endpoint is `127.0.0.1` only.
4. **Failure:** startup crash/timeout, unhealthy server, wildcard/LAN listener, or non-actionable model-load error.
5. **Inspect:** JARVIS structured console events, diagnostic output, and `Get-NetTCPConnection -LocalPort 18080`.
6. **Network:** Internet is not required; loopback networking must be enabled.
7. **Hardware:** Installed local models/runtime; no microphone or speaker required.

## 3. GPU acceleration verification

1. **Preconditions:** CUDA runtime variant installed, compatible NVIDIA driver, JARVIS stopped.
2. **Action:** Run `nvidia-smi`, start `/start`, submit one text turn, then run `nvidia-smi` again while generation is active.
3. **Expected:** `llama-server.exe` uses GPU memory/compute without exhausting 4 GB VRAM; response completes. GPU layers may be less than all layers.
4. **Failure:** no GPU activity when configured, driver/native load failure, GPU OOM, host crash, or repeated restart.
5. **Inspect:** `nvidia-smi`, event 2300 tuning values, and sanitized diagnostic code. Lower `LocalAi:GpuLayers` or use CPU after recording failure.
6. **Network:** Internet is not required.
7. **Hardware:** Compatible NVIDIA GPU/driver and CUDA runtime required for this test.

## 4. Startup

1. **Preconditions:** installed assets; safe tracked defaults; no JARVIS/llama process running.
2. **Action:** Run `dotnet run --project src/Jarvis.Host/Jarvis.Host.csproj`, then `/start`.
3. **Expected:** help appears, host stays responsive, states progress `Activating` → `Listening`, and no API key prompt appears.
4. **Failure:** unhandled exception, automatic download, external connection request, credential request, or indefinite activation.
5. **Inspect:** console lifecycle/state events and Task Manager process list.
6. **Network:** Internet may be disconnected after setup.
7. **Hardware:** Local runtime/models required; microphone/speaker only if enabled.

## 5. Local text conversation

1. **Preconditions:** active `Listening` session; speech output may be disabled for isolation.
2. **Action:** Type `In one sentence, explain what a semaphore controls.`
3. **Expected:** a relevant local streaming text response appears and state returns to `Listening`.
4. **Failure:** no response, cloud/API error, hidden `<think>`/reasoning, tool JSON, or faulted host.
5. **Inspect:** interactive console and content-free first-token/end-to-end metric events.
6. **Network:** Internet must not be required.
7. **Hardware:** Local runtime/model required; no microphone or speaker required.

## 6. Microphone speech recognition

1. **Preconditions:** `Voice:Enabled=true`; working selected microphone; VAD mode; quiet room.
2. **Action:** `/start`, then clearly say “What is two plus two?” and stop speaking.
3. **Expected:** partial/final `[hearing]`/`[heard]` text appears and a matching answer begins. No transcript file is created.
4. **Failure:** capture error, no speech boundaries, empty/wrong final turn beyond reasonable acoustic/model limits, or persistent transcript artifact.
5. **Inspect:** console state/transcript, input device number, VAD settings, and JARVIS data `Logs/Data/Cache` for unexpected content.
6. **Network:** Internet must not be required.
7. **Hardware:** Microphone required; speaker is optional for isolating recognition.

## 7. Local spoken response

1. **Preconditions:** `Voice:SpeechOutputEnabled=true`; working output device/headphones; session active.
2. **Action:** Ask “Reply with one calm sentence.”
3. **Expected:** Kokoro `bm_george` audio is audible locally, intelligible, ordered, and followed by `Listening`.
4. **Failure:** silence, wrong/garbled format, reordering, generated audio file, native crash, or device remains blocked after stop.
5. **Inspect:** output device setting, console TTS failure code/first-audio metric, and JARVIS data directories for unexpected audio.
6. **Network:** Internet must not be required.
7. **Hardware:** Speaker/headphones required; microphone required only when the question is spoken.

## 8. Streaming response

1. **Preconditions:** speech output active.
2. **Action:** Ask for a spoken explanation of four short sentences and observe when audio starts relative to final text.
3. **Expected:** first sentence begins playing before the full response finishes generating; later sentences remain in order.
4. **Failure:** TTS waits for the entire response, overlaps/reorders segments, speaks markdown/code/tool metadata, or grows an unbounded delay.
5. **Inspect:** console transcript timing and first-token/TTS-first-audio/end-to-end metric events.
6. **Network:** Internet must not be required.
7. **Hardware:** Speaker/headphones required; local model/runtime required.

## 9. Barge-in

1. **Preconditions:** headphones; VAD and speech output active.
2. **Action:** Ask for a long answer; while JARVIS is speaking, say “Stop and tell me the time complexity of binary search.”
3. **Expected:** old playback stops promptly, new speech is recognized, the old response never resumes, and the new answer is produced.
4. **Failure:** old audio continues/resumes, stale text is synthesized, new speech is ignored, or session crashes/deadlocks.
5. **Inspect:** state transitions, barge-in playback-stop metric, `[heard]` text, and absence of old continuation.
6. **Network:** Internet must not be required.
7. **Hardware:** Microphone and headphones/speaker required; headphones are strongly preferred.

## 10. Push-to-talk

1. **Preconditions:** set `JARVIS_Voice__ActivationMode=PushToTalk`; restart and `/start`.
2. **Action:** Enter `/ptt`, speak one question, then enter `/send`.
3. **Expected:** capture occurs only between commands, final text is submitted once, and one response is produced.
4. **Failure:** capture before `/ptt`/after `/send`, duplicate turn, `/send` hangs, or VAD semantics are required.
5. **Inspect:** console command/state sequence and final `[heard]` line.
6. **Network:** Internet must not be required.
7. **Hardware:** Microphone required; speaker is optional.

## 11. Explicit `/interrupt`

1. **Preconditions:** active session and long spoken generation.
2. **Action:** Enter `/interrupt` while audio is playing.
3. **Expected:** generation/TTS are cancelled, hardware buffer clears, state becomes `Listening`, and no stale output resumes.
4. **Failure:** continued/resumed audio, stuck `Speaking`, unhandled cancellation, or host exits.
5. **Inspect:** state events and subsequent successful short text turn.
6. **Network:** Internet must not be required.
7. **Hardware:** Speaker/headphones required to confirm immediate audio stop; microphone is not required.

## 12. Network completely disconnected

1. **Preconditions:** setup already complete; active assets local; JARVIS stopped.
2. **Action:** disable Wi-Fi/Ethernet (not merely disconnect VPN), confirm public connectivity is absent, then start JARVIS and `/start`.
3. **Expected:** session reaches `Listening` without delay from external DNS/HTTP and all local engines load.
4. **Failure:** cloud/credential/DNS error, external connection attempt, automatic download, or inability to start solely because Internet is absent.
5. **Inspect:** console, Windows Resource Monitor/TCPView if available, and ensure only loopback port 18080 is used by JARVIS/llama-server.
6. **Network:** Wi-Fi, Ethernet, and all other Internet paths must be disconnected.
7. **Hardware:** Installed runtime/models and configured microphone/speaker required; GPU required only for CUDA-mode validation.

## 13. Repeated turns while offline

1. **Preconditions:** test 12 still offline and session active.
2. **Action:** complete at least ten mixed short text/voice turns, including one interruption.
3. **Expected:** ordered relevant responses, bounded/stable memory after warm-up, no external request, and no stale replay.
4. **Failure:** increasing latency/memory without bound, network retries, duplicate/missing turns, or crash.
5. **Inspect:** Task Manager working set/GPU memory, metrics, state events, and TCP connections.
6. **Network:** Must remain fully disconnected for the entire test.
7. **Hardware:** Microphone and speaker/headphones required; GPU only when CUDA mode is selected.

## 14. Clean `/quit`

1. **Preconditions:** active session with managed llama-server.
2. **Action:** enter `/quit` while idle, then repeat while a generation is active.
3. **Expected:** host exits promptly without unhandled errors; devices/native resources release; managed child terminates.
4. **Failure:** orphan process, hang, cancellation stack trace, or locked audio device.
5. **Inspect:** console exit, Task Manager, and `Get-Process llama-server -ErrorAction SilentlyContinue`.
6. **Network:** Connected or disconnected; repeat offline as part of the full gate.
7. **Hardware:** Local runtime/model required; microphone/speaker required for the active-generation variant.

## 15. Ctrl+C

1. **Preconditions:** active managed session.
2. **Action:** press Ctrl+C once while idle, then repeat a new run while speaking/generating.
3. **Expected:** Generic Host cancellation performs the same clean shutdown as `/quit`.
4. **Failure:** abrupt crash, second Ctrl+C required, orphan child, or device lock.
5. **Inspect:** final host lifecycle events, Task Manager/process command, and next startup.
6. **Network:** Connected or disconnected; repeat offline as part of the full gate.
7. **Hardware:** Local runtime/model required; microphone/speaker required for the speaking variant.

## 16. Audio-device release

1. **Preconditions:** another audio application available; JARVIS has captured/played audio.
2. **Action:** `/stop` or `/quit`, then immediately record/play audio in the other application; restart JARVIS afterward.
3. **Expected:** both devices are immediately usable and JARVIS can reacquire them.
4. **Failure:** busy/locked device, silence after restart, or disposed-object exception.
5. **Inspect:** JARVIS console failure class and Windows Sound settings/device test.
6. **Network:** Internet is not required.
7. **Hardware:** Microphone, speaker/headphones, and another audio application required.

## 17. llama-server process termination

1. **Preconditions:** managed session active; identify the `llama-server.exe` process by name only.
2. **Action:** `/stop`, `/quit`, and Ctrl+C in separate runs; after each, check the process list. Also terminate the child manually during one active turn.
3. **Expected:** normal shutdown leaves no child; manual child loss faults the session safely without crashing the host or entering a restart loop.
4. **Failure:** orphan, infinite restart, host crash, raw diagnostic leak, or stale response after child loss.
5. **Inspect:** Task Manager/`Get-Process`, events 2302/voice error code, and subsequent clean restart.
6. **Network:** Internet is not required.
7. **Hardware:** Local runtime/model required; GPU is optional and microphone/speaker are not required.

## 18. Privacy/log inspection

1. **Preconditions:** complete several distinctive text/voice turns and one failure; know the exact test phrase.
2. **Action:** search console-captured structured logs and `%LOCALAPPDATA%\JARVIS\{Logs,Data,Cache}` for the phrase, raw PCM/audio, `/no_think`, credential-like values, user/account identity, and native raw output.
3. **Expected:** interactive transcript is visible only where intentionally displayed; structured logs/metrics contain lifecycle, numeric timing, safe tuning, and failure codes only. No raw audio/transcript/prompt/response/credential is persisted by JARVIS.
4. **Failure:** content or ephemeral token in structured logs/files, generated audio/transcript file, hardware serial/user account persistence, or external telemetry.
5. **Inspect:** console destination, JARVIS data root, Process Explorer child environment only if securely available (do not save it), and active TCP connections.
6. **Network:** Prefer fully disconnected to make unexpected egress visible.
7. **Hardware:** Microphone and speaker/headphones required for voice-content checks; GPU is optional.

## 19. Approximate latency measurement

1. **Preconditions:** warmed model after one discarded turn; quiet room/headphones; stable tuning.
2. **Action:** perform five short voice turns and record the content-free metrics for ASR finalization, first token, TTS first audio, end-to-end turn, and tokens/second.
3. **Expected:** all metric categories appear, values are finite/non-negative, no text accompanies them, and median perceived delay is usable for conversation on the target laptop.
4. **Failure:** missing/negative/unbounded metrics, transcript content in metric events, multi-second regression inconsistent with resource diagnostics, or unstable OOM.
5. **Inspect:** structured metric events, `nvidia-smi`, task working set, and configuration. Report approximate medians with configuration—never device identifiers or transcript content.
6. **Network:** Internet must remain disconnected after setup.
7. **Hardware:** Microphone and speaker/headphones required; GPU required only when recording CUDA measurements.

## 20. End-to-end offline release gate

1. **Preconditions:** llama.cpp runtime, Qwen3 GGUF, Silero VAD, Zipformer ASR, Kokoro TTS, and their manifest-validated supporting files are installed outside Git; microphone and headphones/speaker work; JARVIS is stopped.
2. **Action:** Perform this sequence without omitting a step: (1) start Windows normally; (2) run `pwsh .\scripts\diagnose-local-ai.ps1`; (3) while setup is already complete and the network is available, start JARVIS; (4) `/start` and confirm local models load; (5) confirm local inference health; (6) `/quit`; (7) disable Wi-Fi; (8) disable Ethernet and any other Internet connectivity and confirm a public site cannot be reached; (9) start JARVIS again and `/start`; (10) complete a text turn; (11) complete microphone recognition; (12) receive the local LLM answer; (13) hear local TTS; (14) perform at least ten follow-up turns; (15) test `/interrupt`; (16) test spoken barge-in; (17) restart in `PushToTalk` mode and test `/ptt` then `/send`; (18) exit with `/quit`; (19) restart and exit with Ctrl+C; (20) verify microphone/speaker are immediately reusable; (21) verify no `llama-server` process remains; (22) inspect structured logs/data directories for the distinctive test phrase, raw audio, reasoning, or credentials; (23) inspect active connections and confirm no external network connection was required.
3. **Expected:** Every local text/voice stage works offline, streaming begins before long output completes, interruption never resumes stale audio, restarts and both shutdown paths are clean, content is not persisted in structured logs, and only loopback inference traffic exists.
4. **Failure:** Any cloud/DNS/API-key request, automatic download, missing local stage, crash/hang, orphan process, locked device, stale playback, transcript/raw-audio persistence, external connection, or unbounded resource growth.
5. **Inspect:** JARVIS JSON console events, `pwsh .\scripts\diagnose-local-ai.ps1`, Task Manager, `Get-NetTCPConnection`, `Get-Process llama-server -ErrorAction SilentlyContinue`, and the configured `JARVIS_HOME` `Logs`, `Data`, and `Cache` directories. Do not save transcripts or machine identifiers with the result.
6. **Network:** Connected only for the initial already-completed readiness confirmation; fully disconnected for steps 9–23.
7. **Hardware:** Microphone and speaker/headphones required. A GPU is required only if CUDA acceleration is being validated; otherwise use the CPU runtime.

## Result classification

The safe configuration, architecture boundaries, and default fake-based suite are **AUTOMATED VERIFIED** only after the commands in `AGENTS.md` pass. Physical audio, real native models, GPU behavior, latency, device/process cleanup, and disconnected operation are **USER MANUAL VERIFICATION REQUIRED** until the relevant steps above are performed and recorded.

Mark hardware/offline voice as **MANUALLY VERIFIED** only after all applicable steps pass. If an optional CUDA step fails but CPU mode passes, record that limitation explicitly. Do not convert a skipped physical test into an automated pass.
