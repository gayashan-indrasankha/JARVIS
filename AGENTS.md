# Working on JARVIS

JARVIS is a Windows-first, local-first C#/.NET 10 application. Codex is a development tool only and must never become a runtime dependency.

## Before changing code

1. Read [the system overview](docs/architecture/system-overview.md) and the relevant subsystem document.
2. Check [the decision index](docs/decisions/README.md); record consequential or hard-to-reverse choices.
3. Keep the change within the current milestone in [the roadmap](docs/product/roadmap.md).

## Invariants

- Dependencies point inward: `Host -> Infrastructure -> Core`; `Host -> Core` is allowed for composition.
- `Jarvis.Core` stays independent of UI, AI/model SDKs, Windows APIs, network clients, databases, and infrastructure packages.
- Normal runtime is offline-capable. External downloads occur only through explicit setup/update workflows; local inference binds exactly to `127.0.0.1`.
- Models never access the OS directly. Every observation/action uses the typed tool dispatcher, validation, authorization, and content-minimized audit logging.
- Never commit secrets, model weights, native runtime binaries, personal runtime data, logs, databases, or generated indexes.
- Add behavior and boundary tests; do not create hypothetical empty projects or silently download runtime assets.

## Validation

Run from the repository root:

```powershell
dotnet restore Jarvis.sln
dotnet build Jarvis.sln --no-restore
dotnet test Jarvis.sln --no-build
dotnet format Jarvis.sln --verify-no-changes --no-restore
```

Warnings are errors. Keep detailed design in [architecture docs](docs/architecture/system-overview.md), product scope in [the roadmap](docs/product/roadmap.md), and security rules in [security.md](docs/architecture/security.md).
