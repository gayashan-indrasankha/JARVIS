# Tool system architecture

## Purpose and invariant

Tools are the only route from local reasoning to operating-system observation or action. A model proposes a narrow JSON request; trusted application code decides whether anything runs. Catalog membership never implies permission, local inference never implies trust, and no model adapter owns an OS handle or executor.

Version 0.2 implemented the first bounded tool kernel and Windows adapter slice. Versions 0.3 and 0.4 reuse that same module for local Project Intelligence and Project Learning rather than creating a bypass, plugin shell, or microservice.

## Dependency and trust boundaries

`Jarvis.Core` owns provider-neutral immutable contracts, authorization categories, execution outcomes, audit events, and the bounded agent loop. `Jarvis.Infrastructure` owns strict JSON/schema translation, the trusted registry, approved-root policy, authorization implementation, audit logging, local llama.cpp planning, and operating-system adapters. `Jarvis.Host` binds configuration and composes the graph.

```text
untrusted user/ASR text
  -> local llama.cpp planner (schema-constrained JSON only)
  -> ToolCallProposal
  -> trusted registry exact-name lookup
  -> strict typed validation + normalization
  -> authorization
  -> typed executor with cancellation/deadline
  -> sanitized audit event + bounded observation
  -> local llama.cpp final answer
  -> console/TTS
```

The model receives tool names, descriptions, and closed JSON schemas. It does not receive an executor, `Process`, filesystem object, `IServiceProvider`, PowerShell, a native llama tool runner, or a way to register catalog entries. Provider response objects terminate in Infrastructure.

## Catalog through 0.4

The catalog is constructed from reviewed code and exposes exactly:

| Tool | Request boundary | Category |
| --- | --- | --- |
| `list_directory` | approved directory, bounded entries | `SAFE_READ` |
| `find_files` | approved root, simple pattern, bounded results/depth | `SAFE_READ` |
| `get_file_metadata` | approved file or directory | `SAFE_READ` |
| `open_file` | approved non-executable document | `SAFE_LOCAL_ACTION` |
| `open_folder` | approved directory | `SAFE_LOCAL_ACTION` |
| `read_text_file` | approved bounded text file | `SAFE_READ` |
| `launch_application` | fixed normal-application enum | `SAFE_LOCAL_ACTION` |
| `list_processes` | bounded name/PID/memory facts only | `SAFE_READ` |
| `get_system_metrics` | CPU and memory facts | `SAFE_READ` |
| `get_git_status` | approved repository; fixed read-only Git arguments | `SAFE_READ` |
| `execute_safe_command` | fixed diagnostic enum only | `SAFE_READ` |
| `analyze_project` | approved direct Git repository; bounded static local index | `SAFE_LOCAL_ACTION` |
| `get_project_overview` | previously indexed repository | `SAFE_READ` |
| `search_project` | bounded query/results against local symbol/FTS index | `SAFE_READ` |
| `find_symbol` | bounded exact/qualified symbol | `SAFE_READ` |
| `explain_symbol` | bounded symbol plus relationships | `SAFE_READ` |
| `find_references` | bounded symbol relationships | `SAFE_READ` |
| `trace_dependency` | bounded source/optional target/depth | `SAFE_READ` |
| `trace_request_flow` | bounded endpoint/depth | `SAFE_READ` |
| `list_api_endpoints` | bounded endpoint facts | `SAFE_READ` |
| `list_project_dependencies` | static project/package facts | `SAFE_READ` |
| `explain_architecture` | bounded project/DI/API/data/test evidence | `SAFE_READ` |
| `start_tutor_session` | create a grounded local tutor session | `SAFE_LOCAL_ACTION` |
| `continue_tutor_session` | progress the named tutor session | `SAFE_LOCAL_ACTION` |
| `start_interview_session` | create a grounded adaptive interview | `SAFE_LOCAL_ACTION` |
| `submit_interview_answer` | evaluate/store one answer and produce the next question | `SAFE_LOCAL_ACTION` |
| `end_learning_session` | complete a session and store/report results | `SAFE_LOCAL_ACTION` |
| `start_revision_session` | create a tutor session from latest interview weaknesses | `SAFE_LOCAL_ACTION` |

Contracts reject unknown members and enum values. The safe-command enum contains only `dotnet_info`, `dotnet_version`, and `git_version`. It has no program, argument, working-directory, environment, shell, or elevation field. Dedicated structured tools must be used when they exist.

Project and learning tools do not broaden the process boundary: analysis never evaluates MSBuild, restores/builds, runs generators/tests/scripts, loads repository binaries, or sends content over a network. Learning tools read evidence only through Project Intelligence, persist bounded private state under `JARVIS_HOME`, and use the supervised local model. Writes, repository mutation, deletion, termination, administrator shell, credential access, generic command execution, UI automation, network access, and arbitrary application paths are not implemented.

## Required execution order

`ToolDispatcher` enforces this order for every proposal:

1. Allocate a local invocation ID and timestamp.
2. Look up the exact name in the trusted registry. Unknown means no execution.
3. Strictly deserialize into the registered request type. Extra/missing/malformed values mean no execution.
4. Validate limits and normalize security-relevant values. Unsafe values mean no execution.
5. Compute a canonical fingerprint and reject a repeated identical call for the user request.
6. Ask the authorization policy using only the normalized request identity.
7. Execute only when the decision is `Allowed`, with linked user cancellation and a deadline.
8. Convert failures to stable sanitized categories; do not expose exception/path/process detail.
9. Serialize/sanitize/truncate the result and write one content-minimized terminal audit event.

Validation and duplicate rejection happen before authorization. Authorization happens before execution. Adapters also validate critical assumptions as defense in depth but cannot authorize themselves.

## Authorization

Core defines five categories:

- `SAFE_READ` — bounded local observation; allowed by the initial local policy.
- `SAFE_LOCAL_ACTION` — visible, non-destructive local action; allowed only when `Tools:AllowSafeLocalActions` is true.
- `CONFIRM_REQUIRED` — fails closed with `ConfirmationRequired` until an approval surface exists.
- `STRONG_CONFIRM_REQUIRED` — fails closed with `StrongConfirmationRequired` until stronger approval exists.
- `DENIED` — never executes.

`Tools:Enabled=false` denies the catalog. No initial tool is allowed to elevate privileges. JARVIS inherits only the interactive user's normal token.

## Path and data policy

Tracked defaults contain no approved filesystem root. A user explicitly configures one or more fully qualified, existing, non-root directories on local drives in `Tools:AllowedRoots`. The path policy canonicalizes with Windows case-insensitive comparison and requires the target to remain under an approved root. Relative paths are accepted only when there is exactly one root. UNC/network roots, existing reparse-point components, alternate data streams, Win32 trailing-dot/space aliases, and DOS short-name aliases are rejected conservatively. Git status supports a direct `.git` directory only, rejects indirection files, pins both `--git-dir` and `--work-tree` to validated paths, and ignores submodules so repository configuration cannot redirect the inspected work tree outside the approved root.

Credential-oriented directories and files are denied even under an approved root, including common SSH, cloud, container, key, token, environment-secret, certificate, and password-store locations/types. Directory/search results omit denied entries. Text reads reject binary/NUL content, invalid UTF-8, and oversized files. File open rejects executable, installer, script, shortcut, and URL types.

File content, filenames, Git output, process names, terminal output, documents, websites, and future external data are untrusted context. `open_file` therefore accepts only a conservative set of non-executable document extensions rather than every registered Windows association. Before every plan, Core adds a higher-priority policy stating that such content is data, cannot alter policy, and cannot authorize or invoke a tool. Observations are explicitly labeled `[UNTRUSTED_TOOL_RESULT]` and bounded before reuse.

## Command and process constraints

No initial executor invokes PowerShell, `cmd.exe`, a command interpreter, or a model-selected executable. The bounded process runner requires an existing fully qualified `.exe`, uses `UseShellExecute=false`, separate argument values, redirected bounded output, process-tree cancellation, and a minimal environment that excludes inherited credentials and model/tool settings. Fixed Git/.NET executable IDs are resolved from absolute `PATH` entries before launch; process creation never receives a bare name that could trigger current-directory search.

`get_git_status` uses fixed read-only Git arguments, disables prompts, hooks/config influence where practical, optional locks, fsmonitor, the untracked cache, and submodule traversal. `launch_application` maps a fixed enum to reviewed normal Windows applications. `open_file` and `open_folder` use the platform launcher only after validation and authorization.

## Loop and result controls

Each user request receives a correlation ID. The agent loop has a configured maximum of one to eight tool steps (default four). An identical normalized call is rejected within that request. Planner structured output is bounded and gets at most one repair request; a second malformed response stops without execution. Cancellation propagates across planning, authorization, executor, subprocess, final generation, TTS, and host shutdown.

Each tool has a timeout and result-character maximum. Traversal, item count, file size, total project text, project relationship depth, evidence context, process output, model history, and planner response size also have local caps. JARVIS reports success only when the typed outcome is `Success`. A denial, invalid input, duplicate, timeout, unavailable executor, or failure returns a deterministic non-success message without delegating wording to the model; caller cancellation stops the turn.

## Audit

Every terminal dispatcher path emits one event with:

- invocation ID and user request ID;
- exact tool name and authorization decision;
- start/end UTC timestamps;
- outcome status and success flag;
- timeout, cancellation, and truncation flags;
- stable sanitized error category.

The current sink writes structured operational logs. It never writes proposal JSON, normalized arguments, paths, file contents, process/command output, prompts, responses, or raw exception text. Durable tamper-resistant storage, retention, encryption, and audit querying are deferred and require a separate decision.

## Configuration

Safe tracked defaults are:

```json
{
  "Tools": {
    "Enabled": true,
    "AllowSafeLocalActions": true,
    "MaximumToolSteps": 4,
    "MaximumResultCharacters": 16384,
    "DefaultTimeoutSeconds": 10,
    "AllowedRoots": []
  }
}
```

Machine paths belong in `JARVIS_` environment variables or command-line configuration, never tracked settings. For one approved root in PowerShell:

```powershell
$env:JARVIS_Tools__AllowedRoots__0 = (Get-Location).Path
```

## Verification and deferred scope

Automated tests cover exact schemas/catalog, malformed/unknown proposals, ordering, approved roots, credential/reparse defenses, denial, success, duplicates, cancellation, timeout, truncation, fixed process mappings, temporary-directory filesystem/Git behavior, planner repair, audit completeness, and adapter separation. Real interactive window opening and a model-generated acceptance flow require the manual tool smoke test.

Deferred scope includes interactive approval grants, durable audit persistence, write/delete tools, arbitrary commands, administrator operations, richer process/application actions, UI automation, project mutation, and deeper runtime/semantic project analysis. These capabilities must reuse this dispatcher and cannot broaden an existing contract silently.
