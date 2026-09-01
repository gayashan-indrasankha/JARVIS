# 0004 — Permission-controlled local tool kernel

- Status: Accepted
- Date: 2026-09-01
- Owners: team
- Supersedes: none

## Context

JARVIS 0.2 must let a local language model request useful Windows observations and benign local actions without giving the model filesystem, process, shell, or Windows API access. The roadmap originally separated a harmless read-only kernel in 0.2 from all Windows adapters in 0.3. The accepted 0.2 demonstrations require system metrics, approved-root file search and folder opening, and Git status, so a tested end-to-end adapter slice is now necessary.

Local inference does not make model output trustworthy. Tool proposals can be malformed, repetitive, induced by prompt injection, or broader than the user's intent. Results can contain adversarial instructions. A generic shell or permissive path API would turn those failures into same-user operating-system access.

## Decision

Implement one in-process, non-bypassable tool pipeline:

```text
local model proposal
  -> trusted registry lookup
  -> closed-schema typed validation and canonicalization
  -> local authorization policy
  -> bounded executor
  -> content-minimized audit event
  -> bounded, untrusted observation
  -> local model final response
```

Provider/platform-neutral contracts, authorization categories, outcomes, and the agent loop live in `Jarvis.Core`. The trusted registry, strict JSON boundary, policy implementation, audit sink, llama.cpp schema adapter, and Windows implementations live in `Jarvis.Infrastructure`. `Jarvis.Host` remains the composition/configuration boundary. The llama.cpp adapter can propose only a catalog tool through schema-constrained output and holds no executor, authorization, or Windows service.

The initial catalog is fixed in reviewed code: `list_directory`, `find_files`, `get_file_metadata`, `open_file`, `open_folder`, `read_text_file`, `launch_application`, `list_processes`, `get_system_metrics`, `get_git_status`, and `execute_safe_command`. File tools require canonical paths within user-configured approved roots, reject credential-sensitive locations, reject existing reparse points, and bound traversal/data. Opening a file rejects executable/script/link types. Applications are a fixed enum. The safe-command tool is a fixed enum (`dotnet_info`, `dotnet_version`, `git_version`) executed directly with argument arrays, a minimal environment, timeout, and output cap. It cannot accept a program, command line, PowerShell, `cmd.exe`, arguments, elevation, or destructive operation from the model.

Authorization categories are `SAFE_READ`, `SAFE_LOCAL_ACTION`, `CONFIRM_REQUIRED`, `STRONG_CONFIRM_REQUIRED`, and `DENIED`. Initial tools are read or safe local action. Safe local actions can be globally disabled. Confirmation categories fail closed until a user approval surface is implemented. Writes, deletion, administrator commands, arbitrary shell, credential access, network tools, and UI automation are absent.

Each terminal invocation produces an audit event containing invocation/request IDs, tool name, authorization decision, timestamps, status, success, timeout/cancellation/truncation flags, and a stable sanitized error category. Arguments, paths, file contents, command output, prompts, and model text are not recorded by the audit sink. This milestone uses structured operational logging; durable tamper-resistant audit storage remains a later decision.

The agent permits at most a configured number of tool steps, blocks an identical canonical call within one user request, supports end-to-end cancellation/deadlines, caps each result, and allows the planner one repair attempt for malformed structured output. Tool results are labeled untrusted data and the system policy states that file, repository, terminal, website, and document content cannot override JARVIS policy.

## Alternatives considered

- **llama.cpp native tool execution:** rejected. Transport-specific tool objects would leak inward and could obscure the local validation/authorization boundary.
- **Generic shell with a deny list:** rejected. Shell parsing, expansion, and model-selected arguments are too broad for this milestone.
- **Read-only kernel with no acceptance-demo adapters:** rejected because it would not demonstrate the required local capability path.
- **Separate projects or microservice:** rejected. The boundary is cohesive within existing Core/Infrastructure modules and does not justify deployment or project complexity.

## Consequences

- The model can request capabilities but cannot execute them or broaden the trusted catalog.
- Tracked defaults approve no filesystem root, so path tools fail closed until the user opts in.
- Safe local actions are enabled by default but remain narrow and can be disabled independently.
- A malicious file can influence a later answer but cannot become policy or directly invoke a tool; repeated model turns still require the same dispatcher.
- Reparse-point handling is intentionally conservative and may reject legitimate linked directories.
- Durable audit persistence, interactive approvals, writes, deletion, general process control, and broader Windows automation remain deferred.

## Validation

- Architecture tests prove Core independence and that local model adapters do not hold dispatcher/authorization/audit services.
- Registry/schema tests prove an exact closed catalog and fixed enums.
- Dispatcher tests cover unknown/malformed/duplicate-property calls, validation-before-authorization, authorization-before-action, denial/confirmation, approved-root and credential rejection, duplicate invocations, cancellation, timeout, result truncation, confirmed/unconfirmed actions, and audit outcomes.
- Temporary-directory tests cover search, metadata, text reads, Git invocation shape, and safe-command mappings without destructive real-machine operations.
- Planner tests prove schema-constrained proposals, no native server tool registration, one repair attempt, and stable failure behavior.
