# JARVIS

JARVIS is a Windows-first, local-first personal computing agent built with C# and .NET 10. It is intended to combine realtime conversation, controlled operating-system tools, project intelligence, and proactive assistance without giving an AI model direct access to the computer.

This repository is at **version 0.1 realtime voice foundation**. It provides an interruptible OpenAI realtime voice session, Windows microphone and speaker adapters, server voice-activity detection, push-to-talk and text-console fallbacks, bounded reconnect handling, and provider-neutral Core orchestration. Operating-system tools, indexing, memory, UI automation, interviews, wake-word inference, and hardware integrations are deliberately not implemented.

Codex may be used to develop this repository. It is not part of the JARVIS runtime architecture.

## Repository layout

```text
src/
  Jarvis.Core/            Provider- and platform-neutral domain contracts and policy
  Jarvis.Infrastructure/  OpenAI realtime, Windows audio, and configuration adapters
  Jarvis.Host/            Executable composition root and process lifetime
tests/
  Jarvis.Core.Tests/      Core behavior and architecture-boundary tests
  Jarvis.Infrastructure.Tests/  Protocol, reconnect, and configuration tests
docs/
  product/                Product intent and staged roadmap
  architecture/           Authoritative subsystem and security designs
  decisions/              Architecture decision process and index
```

Production dependencies point inward:

```text
Jarvis.Host ────────> Jarvis.Infrastructure ────────> Jarvis.Core
     └──────────────────────────────────────────────> Jarvis.Core

Jarvis.Core.Tests ─────────────────────────────────> Jarvis.Core
Jarvis.Infrastructure.Tests -> Jarvis.Infrastructure -> Jarvis.Core
```

`Jarvis.Core` must not reference the host, infrastructure, UI frameworks, AI SDKs, Windows APIs, databases, or other external implementation concerns. See [System overview](docs/architecture/system-overview.md) for the full boundary rules.

## Development setup

### Prerequisites

- Windows 10 or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), version 10.0.300 or a compatible later .NET 10 feature band
- PowerShell 7 is recommended; Windows PowerShell also works for the commands below

Confirm the SDK selected by `global.json`:

```powershell
dotnet --version
```

### Restore, build, and test

From the repository root:

```powershell
dotnet restore Jarvis.sln
dotnet build Jarvis.sln --no-restore
dotnet test Jarvis.sln --no-build
dotnet format Jarvis.sln --verify-no-changes --no-restore
```

Warnings are treated as errors. The .NET SDK analyzers run during builds, and the formatting command verifies repository code-style rules that are not compiler diagnostics.

### Configure realtime voice

The OpenAI API key must stay outside the repository. For local development, store it with .NET user secrets and enable voice:

```powershell
dotnet user-secrets set "Voice:OpenAI:ApiKey" "<your-api-key>" --project src/Jarvis.Host/Jarvis.Host.csproj
dotnet user-secrets set "Voice:Enabled" "true" --project src/Jarvis.Host/Jarvis.Host.csproj
```

The equivalent process-scoped environment variables are:

```powershell
$env:JARVIS_Voice__OpenAI__ApiKey = "<your-api-key>"
$env:JARVIS_Voice__Enabled = "true"
```

Do not put a real value in `appsettings.json`, a checked-in launch profile, a script, or a command transcript. The tracked default endpoint is restricted to `wss://api.openai.com/v1/realtime` so a configuration change cannot silently redirect the credential to another host.

The default activation mode is `ServerVoiceActivityDetection`. To use push-to-talk instead:

```powershell
dotnet user-secrets set "Voice:ActivationMode" "PushToTalk" --project src/Jarvis.Host/Jarvis.Host.csproj
```

### Run JARVIS

```powershell
dotnet run --project src/Jarvis.Host/Jarvis.Host.csproj
```

At the console, use:

- `/start` to establish the configured realtime session;
- speak normally in the default server-VAD mode;
- `/ptt` and `/send` to begin and end capture in push-to-talk mode;
- `/interrupt` to stop the current response explicitly;
- any other line to submit a text turn while retaining spoken output;
- `/stop` or `/quit` for clean session or process shutdown.

Assistant transcript deltas are shown only as interactive console output for debugging. Structured logs contain lifecycle, format, reconnect, drop-count, and sanitized error-class metadata—not API keys, transcript text, or raw audio. Pressing `Ctrl+C` also performs a clean shutdown.

## Configuration and secrets

Safe defaults belong in `src/Jarvis.Host/appsettings.json`. The Generic Host also supports environment-specific settings, .NET user secrets in Development, environment variables, and command-line arguments. JARVIS-specific environment variables use the `JARVIS_` prefix; nested keys use double underscores:

```powershell
$env:JARVIS_Jarvis__InstanceName = "DeveloperMachine"
```

Use .NET user secrets for local development credentials; JARVIS loads its optional user-secret store regardless of the host environment name, and the values remain outside the repository. `JARVIS_` environment variables and then non-secret command-line configuration take precedence. Environment variables are appropriate only when their process-disclosure risk is acceptable; never pass a credential on a command line. Never put credentials, tokens, private keys, connection strings containing passwords, or personal data in tracked configuration. Local configuration, key material, runtime data, logs, databases, and indexes are ignored by `.gitignore`. Production secret storage and credential rotation remain an explicit future decision.

## Documentation map

- [Vision](docs/product/vision.md)
- [Roadmap](docs/product/roadmap.md)
- [System overview](docs/architecture/system-overview.md)
- [Security model](docs/architecture/security.md)
- [Tool system](docs/architecture/tool-system.md)
- [Voice architecture](docs/architecture/voice.md)
- [Project intelligence](docs/architecture/project-intelligence.md)
- [Architecture decisions](docs/decisions/README.md)
- [Manual voice smoke test](docs/testing/manual-voice-smoke-test.md)

Contributor instructions are in [AGENTS.md](AGENTS.md).

## Current limitations

- Reconnect creates a fresh provider conversation; it does not replay stale audio or silently duplicate turns.
- The WinMM vertical slice uses the default devices unless numeric device indexes are configured; device switching and resampling are not yet implemented.
- Echo cancellation, acoustic tuning, wake-word inference, and a graphical capture indicator require later milestones and real-device validation.
- The console is a debugging surface, not the final JARVIS UI.

Follow [the manual voice smoke test](docs/testing/manual-voice-smoke-test.md) before considering a machine/provider combination validated.
