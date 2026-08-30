# Working on JARVIS

JARVIS is a Windows-first, local-first C#/.NET 10 application. Codex is a development tool only and must never become a runtime dependency.

## Before changing code

1. Read [the system overview](docs/architecture/system-overview.md) and the relevant subsystem document.
2. Check [the decision index](docs/decisions/README.md); record consequential or hard-to-reverse choices.
3. Keep the change within the current milestone in [the roadmap](docs/product/roadmap.md).

## Invariants

- Dependencies point inward: `Host -> Infrastructure -> Core`; `Host -> Core` is allowed for composition.
- `Jarvis.Core` stays independent of UI, AI SDKs, Windows APIs, databases, and infrastructure packages.
- Models never access the OS directly. All side effects must use typed tools, authorization, and audit logging.
- Security-sensitive operations default to deny or explicit approval.
- Retrieve and transmit only the minimum context required.
- Never commit secrets, personal runtime data, generated indexes, logs, or local databases.
- Add tests for behavior and boundary changes; do not create empty projects for hypothetical modules.

## Validation

Run from the repository root:

```powershell
dotnet restore Jarvis.sln
dotnet build Jarvis.sln --no-restore
dotnet test Jarvis.sln --no-build
dotnet format Jarvis.sln --verify-no-changes --no-restore
```

Warnings are errors. Keep documentation aligned with architectural changes.

## Authoritative docs

- Product scope: [vision](docs/product/vision.md) and [roadmap](docs/product/roadmap.md)
- System and module boundaries: [system overview](docs/architecture/system-overview.md)
- Trust, authorization, secrets, and audit: [security](docs/architecture/security.md)
- Tool execution contract: [tool system](docs/architecture/tool-system.md)
- Realtime audio design: [voice](docs/architecture/voice.md)
- Repository analysis and grounded answers: [project intelligence](docs/architecture/project-intelligence.md)
