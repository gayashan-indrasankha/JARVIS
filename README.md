# JARVIS

JARVIS is a Windows-first, local-first personal computing agent built with C# and .NET 10. Codex is used to develop the repository and is never a JARVIS runtime dependency.

Version **0.2 permission-controlled local computer agent** runs its conversation pipeline on the user's computer: a dormant sherpa-onnx “Jarvis” keyword spotter, Windows audio, Silero VAD, streaming Zipformer speech recognition, a Qwen3 model served by a supervised llama.cpp child process, typed permission-controlled tools, Kokoro speech synthesis, and speaker playback. Text debugging, push-to-talk, explicit interruption, voice barge-in, and tool requests share the same local orchestration. File writes/deletion, arbitrary shell, administrator operations, credential access, project indexing, memory, UI automation, interviews, and IoT are not implemented.

After the user explicitly installs the pinned runtime and model assets, normal JARVIS operation requires no API key, paid API, cloud AI service, telemetry, model download, or Internet connection.

## Repository and dependency layout

```text
src/
  Jarvis.Core/            Provider/platform-neutral conversation, voice, and tool contracts/orchestration
  Jarvis.Infrastructure/  local AI/audio plus validated, authorized Windows tool adapters
  Jarvis.Host/            .NET Generic Host composition root and console lifecycle
tests/
  Jarvis.Core.Tests/      Orchestration, wake lifecycle, cancellation, segmentation, and boundary tests
  Jarvis.Infrastructure.Tests/  Local adapter, process, path, and configuration tests
config/
  local-model-manifest.json     Tracked pins, checksums, licenses, and hardware guidance
scripts/
  setup-local-ai.ps1            Explicit installation workflow
  diagnose-local-ai.ps1         Local-only readiness diagnostics
docs/                           Product, architecture, decisions, and manual verification
```

Production dependencies point inward:

```text
Jarvis.Host ────────> Jarvis.Infrastructure ────────> Jarvis.Core
     └──────────────────────────────────────────────> Jarvis.Core
```

`Jarvis.Core` has no llama.cpp, sherpa-onnx, NAudio, Windows, HTTP, model, database, logging, or UI implementation dependency. Local HTTP and native types terminate inside Infrastructure.

## Development setup

Prerequisites:

- Windows 11 x64 (Windows 10 is expected to build but is not the validation target);
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), selected by `global.json`;
- PowerShell 7 for setup and diagnostics;
- `tar.exe`, included with supported Windows 11 installations;
- for the CUDA runtime, a compatible NVIDIA driver. CPU-only llama.cpp is also supported.

From the repository root:

```powershell
dotnet restore Jarvis.sln
dotnet build Jarvis.sln --no-restore
dotnet test Jarvis.sln --no-build
dotnet format Jarvis.sln --verify-no-changes --no-restore
```

Warnings are errors. Default tests use fakes and temporary directories. They require no microphone, speaker, GPU, model weights, native inference process, external network, administrator access, or destructive machine changes.

## Explicit local-AI setup

Model files and native binaries live outside Git under `%LOCALAPPDATA%\JARVIS` by default. To use another fully qualified local directory, set `JARVIS_HOME` before setup and runtime:

```powershell
$env:JARVIS_HOME = "D:\LocalApps\JARVIS"
```

Inspect what will be used without downloading anything:

```powershell
.\scripts\setup-local-ai.ps1
```

Explicitly download the pinned CUDA llama.cpp runtime and all approved models:

```powershell
.\scripts\setup-local-ai.ps1 -DownloadRuntime -DownloadModels
.\scripts\diagnose-local-ai.ps1
```

Use `-RuntimeVariant cpu` when CUDA is unavailable. The setup is user-scoped and idempotent; it installs nothing system-wide. It verifies the authoritative checksums recorded in [the model manifest](config/local-model-manifest.json) when one is available. The wake-word archive is pinned by SHA-256 and byte length. The selected ASR Zipformer archive does not publish an authoritative checksum, so the script prominently reports that remaining supply-chain limitation.

The application never downloads a missing component. `/start` instead reports: `Local model not installed. Run scripts/setup-local-ai.ps1.`

## Run JARVIS

Tracked defaults start in safe text-debug mode: local AI enabled, microphone and speech output disabled, no automatic session start. Run:

```powershell
dotnet run --project src/Jarvis.Host/Jarvis.Host.csproj
```

At the console:

- saying “Jarvis” starts the conversational pipeline when always-listening is enabled;
- `/start` starts the configured local session and supervised llama-server without a wake phrase;
- any other line submits a local text turn;
- `/ptt` and `/send` delimit capture in push-to-talk mode;
- `/interrupt` invalidates the current generation, cancels generation/TTS, and clears playback;
- `/falsewake` records a content-free false-activation metric and returns to sleep;
- `/stop` stops the session; `/quit` shuts down the host and managed child process.

Ordinary spoken or typed requests can select the 0.2 tool catalog through the local model. The model can propose only closed-schema requests; trusted code performs exact lookup, validation, path normalization, authorization, bounded execution, and content-minimized audit before any OS operation. Tool results are labeled untrusted data and are passed back to the local model only after execution. An action is never presented as successful until the executor confirms it.

To enable microphone VAD and spoken output for a process:

```powershell
$env:JARVIS_Voice__Enabled = "true"
$env:JARVIS_Voice__SpeechOutputEnabled = "true"
dotnet run --project src/Jarvis.Host/Jarvis.Host.csproj
```

To stay dormant until the local “Jarvis” phrase is detected, also set:

```powershell
$env:JARVIS_Voice__WakeWord__AlwaysListeningEnabled = "true"
```

The console shows `[capture: WakeWord]`, `[capture: Conversation]`, `[capture: PushToTalk]`, or `[capture: Off]`. After a wake, the default 30-second continuation window allows immediate follow-ups without repeating “Jarvis”; activity refreshes that window. Initial sleep loads only the small keyword spotter. The LLM, VAD, ASR, and TTS initialize after the first accepted wake and may remain warm in memory until shutdown to reduce later latency.

For deterministic push-to-talk, also set:

```powershell
$env:JARVIS_Voice__ActivationMode = "PushToTalk"
```

Configuration uses tracked safe defaults plus `JARVIS_`-prefixed environment variables and command-line overrides. `LocalAi:Host` accepts exactly `127.0.0.1`; remote hosts, `0.0.0.0`, `localhost`, and IPv6 endpoints are rejected. Local planning and generation have a bounded `LocalAi:GenerationTimeoutSeconds` (300 by default). Wake tuning keys are `Voice:WakeWord:KeywordScore` (1.5), `KeywordThreshold` (0.25), `CooldownSeconds` (3), and `ContinuationWindowSeconds` (30). These are provisional starting values and require physical testing before being called accurate. Other useful keys are `LocalAi:ContextSize` (8192, with one managed fallback to 4096), `LocalAi:GpuLayers`, `LocalAi:Threads`, `LocalAi:Port`, `Voice:TtsVoice`, `Voice:TtsSpeed`, and VAD thresholds in `appsettings.json`. Do not put machine-specific absolute paths in tracked settings.

Filesystem tools start with no approved root. Opt in for the current PowerShell process, then run JARVIS:

```powershell
$env:JARVIS_Tools__AllowedRoots__0 = (Get-Location).Path
dotnet run --project src/Jarvis.Host/Jarvis.Host.csproj
```

Use additional zero-based entries for more roots. `JARVIS_Tools__Enabled=false` disables the catalog; `JARVIS_Tools__AllowSafeLocalActions=false` keeps bounded reads but denies file/folder opening and application launch. Approved roots must be existing, fully qualified, non-root directories on a local drive; UNC/network roots, existing reparse points, ambiguous Win32 aliases, alternate data streams, and credential-oriented files/directories are rejected. Never commit local approved paths.

The initial tools are `list_directory`, `find_files`, `get_file_metadata`, `open_file`, `open_folder`, `read_text_file`, `launch_application`, `list_processes`, `get_system_metrics`, `get_git_status`, and `execute_safe_command`. `open_file` uses a conservative non-executable document allowlist rather than trusting arbitrary Windows file associations. Git status rejects `.git` indirection files. The last tool accepts only fixed diagnostics (`dotnet_info`, `dotnet_version`, `git_version`); their executables are resolved to direct paths before launch, and it cannot accept a command, arbitrary arguments, PowerShell, `cmd.exe`, or elevation.

No secret is required. A managed llama-server receives a random per-process credential through its child environment for loopback defense in depth; it is never stored, placed on a command line, or logged.

## Storage and privacy

The data-root layout is:

```text
%LOCALAPPDATA%\JARVIS\
  Models\{Llm,Speech,Tts,Vad,WakeWord}\
  Runtime\LlamaCpp\
  Data\
  Logs\
  Cache\
```

Normal runtime makes only fixed `http://127.0.0.1:<port>` llama-server requests. It contains no external HTTP/WebSocket client path, cloud AI SDK, telemetry, updater, or background downloader. Setup is the sole intentional external-download workflow. Structured metrics include timing, rate, lifecycle, and sanitized failure classes; raw audio, transcripts, prompts, responses, hidden reasoning, credentials, and machine identity are not logged or persisted by default. Console transcripts are visible to the interactive user and can be persisted if that user redirects console output.

See [security](docs/architecture/security.md), [tool architecture](docs/architecture/tool-system.md), [voice architecture](docs/architecture/voice.md), [ADR 0002](docs/decisions/0002-local-inference-and-speech-runtime.md), [ADR 0003](docs/decisions/0003-local-wake-word-activation.md), and [ADR 0004](docs/decisions/0004-permission-controlled-local-tool-kernel.md) for the exact guarantees and trust boundaries.

Third-party model/runtime licenses and redistribution obligations are inventoried in [third-party licenses](docs/security/third-party-licenses.md). The current repository downloads assets for local use and does not itself redistribute them; packaging requires a fresh bill-of-materials and legal review.

## Current limitations

- Hardware voice, wake-word accuracy/false-positive rate, GPU offload, and fully disconnected operation require the documented manual tests; automated tests do not claim to validate physical devices or keyword accuracy.
- WinMM uses configured numeric/default devices. Hot-plug, resampling, and echo cancellation are not implemented; use headphones for barge-in validation.
- The initial ASR profile is small and English-only. Accent accuracy must be benchmarked before broader claims.
- Kokoro synthesis runs on CPU and cancellation is cooperative at its native callback boundary.
- Conversation/tool history is bounded in memory and is not persisted. Tool audit events currently use content-minimized structured logs rather than durable tamper-resistant storage.
- Confirmation-class policies fail closed because 0.2 has no interactive grant surface. The implemented catalog contains only bounded reads and optional safe local actions; writes, deletion, arbitrary commands, elevation, credentials, and UI automation are absent.
- Wake-word capture uses one CPU inference thread, but actual idle CPU/battery use must be measured on target laptops. The assistant speaker may retrigger or mask the microphone because echo cancellation is not implemented; headphones are the reliable baseline.

Use the [manual local voice smoke test](docs/testing/manual-voice-smoke-test.md), [wake-word matrix](docs/testing/manual-wake-word-test-matrix.md), and [manual tool smoke test](docs/testing/manual-tool-smoke-test.md) before declaring a machine validated. Contributor instructions are in [AGENTS.md](AGENTS.md).

## Documentation map

- [Vision](docs/product/vision.md)
- [Roadmap](docs/product/roadmap.md)
- [System overview](docs/architecture/system-overview.md)
- [Security model](docs/architecture/security.md)
- [Tool system](docs/architecture/tool-system.md)
- [Voice architecture](docs/architecture/voice.md)
- [Project intelligence](docs/architecture/project-intelligence.md)
- [Architecture decisions](docs/decisions/README.md)
