# Tool system architecture

## Purpose

Tools are the only route from reasoning to local observation or action. They translate a narrow, typed request into deterministic code running under local policy. The tool system is not a generic plugin shell and catalog membership never implies permission.

This document is a design contract for a later milestone. Version 0.1 contains no operating-system tools or tool runtime.

## Design goals

- Prevent a model or untrusted content from directly invoking OS or external-service APIs.
- Make every request schema-valid, policy-evaluated, cancellable, bounded, and auditable.
- Give the user an accurate preview of security-relevant effects.
- Make adapters testable without a model or interactive desktop.
- Preserve model-runtime independence by translating model function formats at the edge.
- Support useful read operations without allowing bulk data collection.

## Core concepts

The eventual Core contracts should express concepts equivalent to:

- **Tool identifier and contract version** — stable local identity, never an arbitrary type name.
- **Descriptor** — user-facing purpose, argument/result schema, risk metadata, data classes, limits, and availability requirements.
- **Typed request** — immutable validated arguments plus correlation, origin, and session context.
- **Policy decision** — allow, require approval, or deny with a stable reason code.
- **Approval grant** — user authorization bound to the canonical exact request and limited scope.
- **Executor** — implementation that receives only an authorized request and cancellation/deadline context.
- **Typed result** — success, denial, invalid request, cancellation, timeout, unavailable, partial completion, or failure with bounded observations.
- **Audit event** — append-oriented record of each lifecycle transition.

Core contracts must not expose provider SDK request objects, `IServiceProvider`, raw process handles, database connections, UI automation objects, or loosely typed property bags. Serialization models belong at process/provider boundaries and are mapped to domain types after validation.

## Catalog and registration

The local catalog is constructed by trusted Host composition from reviewed implementations. It is not populated from model output or an untrusted directory scan.

Each descriptor declares:

- unique identifier, contract version, and implementation version;
- concise user-visible purpose and non-capabilities;
- closed argument schema with required fields and limits;
- result shape and maximum response size;
- risk class, reversibility, and affected resource categories;
- read/write/network/process/UI/hardware characteristics;
- default timeout, concurrency, and rate limits;
- platform, configuration, and session availability requirements;
- redaction rules for arguments and results.

Unknown fields and unknown enum values are rejected. Compatibility changes increment the contract version; behavior must not silently broaden behind the same approval identity.

## Execution pipeline

The required order is:

1. **Receive proposal** — translate a model-runtime-neutral tool proposal and assign local identifiers.
2. **Lookup** — resolve only a trusted catalog entry with an exact supported version.
3. **Deserialize and validate** — apply size, type, format, range, and closed-schema checks.
4. **Normalize and resolve** — canonicalize paths, identities, application targets, and units without causing the requested side effect.
5. **Classify and plan** — compute resource scope, sensitivity, reversibility, and a user-readable preview.
6. **Authorize** — evaluate local policy using normalized request and session context.
7. **Approve if required** — collect a user grant bound to the exact plan; revalidate after waiting.
8. **Record intent** — durably audit the authorized attempt when policy requires fail-closed logging.
9. **Execute** — call the adapter with deadline, cancellation, and resource limits.
10. **Record outcome** — capture success, partial effects, failure, denial, timeout, or cancellation.
11. **Sanitize observation** — redact, truncate, summarize, and label output before any model sees it.
12. **Respond** — present the effect and audit correlation to the user.

Validation, authorization, and audit are orchestration responsibilities. Individual adapters also enforce their own invariants as defense in depth, but an adapter cannot declare itself authorized.

## Request identity and approvals

Canonical request identity includes the tool identifier/version, normalized typed arguments, resolved target scope, local user/session, and relevant context such as active window identity. The approval layer hashes or otherwise binds this identity to the displayed preview.

After approval, any meaningful change produces a new request. In particular, no component may:

- add targets to an approved list;
- follow a newly introduced link/junction outside an approved path;
- switch an application/window/process identity;
- substitute a command, argument, working directory, or environment variable;
- reuse a one-shot approval after success, failure, timeout, or cancellation;
- convert a read approval into a write operation.

## Concurrency, cancellation, and idempotency

- Every execution receives a cancellation token and an absolute deadline.
- Cancellation is cooperative and the result reports whether an effect may already have occurred.
- Per-tool and global concurrency limits prevent resource exhaustion and conflicting actions.
- Operations declare whether they are naturally idempotent, require an idempotency key, or must never be retried automatically.
- Automatic retry is forbidden for destructive or externally visible actions unless the adapter proves safe deduplication.
- Long-running work reports bounded progress events and remains cancellable.
- The executor must not hold an interactive approval open indefinitely; grants expire.

## Results and model observations

Tool results distinguish operational status from domain data. A successful API call with a partially completed side effect is not `Success`. Errors have stable categories for orchestration and separate redacted messages for the user/model.

Before returning output to a model:

- enforce byte/item/token limits;
- strip terminal control sequences and reject unexpected binary data;
- redact secrets and policy-protected fields;
- preserve origin labels and mark content as untrusted data;
- include freshness, truncation, and partial-result metadata;
- prefer structured facts and selected excerpts over raw dumps.

Raw tool output may be retained locally only under an explicit data and retention policy.

## Audit event model

At minimum, events carry:

- event, correlation, conversation, request, and parent identifiers;
- monotonic sequence and UTC timestamp;
- local user/session and request origin;
- tool identifier, contract version, and implementation version;
- policy version, decision, reason code, and approval reference;
- canonical redacted request summary and affected resource identifiers;
- lifecycle state, start/end time, duration, and cancellation/timeout flags;
- result classification, partial-effect indicator, and bounded impact counts;
- audit schema version.

Audit writing is itself infrastructure behind a Core-owned port. The policy defines which operations fail closed if durable audit is unavailable. Logs are useful operational signals but are not a substitute for a purpose-built audit trail.

## Tool families and special constraints

### Filesystem

Use canonical absolute paths, approved roots, link/reparse-point policy, file-type and size limits, race-aware operations, and safe create/replace primitives. Reads can be sensitive. Writes require previews and conflict behavior; deletion should prefer recoverable mechanisms when practical.

### Process and application control

Resolve process identity using more than a reusable PID. Separate inspection, launch, focus, graceful close, and force termination into distinct tools and risk levels. Never infer approval for termination from approval to inspect.

### Shell and commands

Prefer dedicated tools or allowlisted executables with argument arrays. A general command interpreter multiplies injection and approval risks and needs a dedicated ADR. Never concatenate model strings into PowerShell, `cmd.exe`, or another shell.

### Windows UI automation

Prefer accessibility and application APIs over coordinate clicks. Bind approval to window/application identity and re-check focus/state immediately before action. Screen content and accessibility text are untrusted and potentially sensitive.

### Network and external services

Declare destination, method, data class, and external side effects. Egress policy and user consent apply separately from local read approval. Tool adapters never reuse inference-runtime credentials or authorization implicitly.

## Testing requirements

- Schema boundary tests for extra, missing, oversized, malformed, and adversarial input.
- Policy matrices for user intent, sensitivity, reversibility, origin, and scope.
- Approval replay and mutation tests.
- Adapter contract tests for timeout, cancellation, partial effects, and error mapping.
- Path traversal, reparse point, command injection, output-control-sequence, and prompt-injection tests where relevant.
- Audit completeness tests for every terminal path.
- Property/fuzz tests for parsers, canonicalization, and policy-critical value objects.

The first implementation should be one harmless, bounded read-only vertical slice. It should prove the entire pipeline before higher-risk tools are considered.
