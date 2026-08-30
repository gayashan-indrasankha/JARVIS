# Security architecture

## Security objective

JARVIS should be useful on a personal Windows machine without converting probabilistic model output, untrusted content, or a compromised provider into operating-system authority. Security depends on local deterministic controls, least privilege, explicit consent, traceability, and data minimization.

JARVIS is not an endpoint protection product and cannot grant stronger access control than Windows provides. It must still defend its own tool, provider, configuration, and data boundaries.

## Trust model

Trusted only to the degree required:

- reviewed local JARVIS binaries and policy code;
- the signed-in user for explicit approvals;
- Windows identity and access control for the current process;
- configured local stores after integrity and access checks.

Always untrusted input:

- model responses and tool arguments proposed by a model;
- remote providers and network responses;
- repository files, filenames, metadata, documents, web content, terminal output, window text, and accessibility trees;
- audio transcripts and wake-word detections;
- plugins, adapters, tool manifests, and serialized historical state until validated;
- environment variables, command-line arguments, and local configuration that can be modified outside the process.

Untrusted content can contain instructions designed to redirect the model. Such content is data, not authorization.

## Protected assets

- User files, credentials, tokens, private keys, browser/session data, messages, source code, and personal information.
- The user's Windows session, running applications, processes, input devices, network identity, and hardware.
- Authorization policy and approval history.
- Tool catalog integrity and executable adapter mappings.
- Audit completeness and integrity.
- Project indexes, memory, transcripts, recordings, and learning history.
- Provider account quotas and billable operations.

## Principal threats

1. A model fabricates or expands a tool request beyond the user's intent.
2. Prompt injection in a file, window, tool result, or retrieved document attempts to trigger actions or exfiltration.
3. Argument injection, path confusion, symlinks/junctions, shell metacharacters, or time-of-check/time-of-use changes escape an approved scope.
4. An approval is replayed or applied to modified arguments.
5. Sensitive content leaks through remote context, logs, traces, crash dumps, notifications, or audit records.
6. A compromised dependency, adapter, or update gains local process authority.
7. Denial of service consumes CPU, memory, storage, audio devices, provider quota, or attention.
8. Stale indexes or memory produce confident but incorrect actions.
9. Audit failure hides an attempted or completed side effect.
10. UI automation acts on a different window, desktop, or state than the one the user approved.

## Authorization model

Every side-effecting tool invocation passes through one authorization service. Read-only operations also require policy because reading can disclose sensitive data or incur cost.

A policy decision is one of:

- **Allow** — policy permits this exact request without an interactive prompt.
- **Require approval** — execution pauses until the user explicitly approves this exact request and scope.
- **Deny** — execution does not occur; the reason is safe to show and is audited.

Policy considers the authenticated local user, session state, tool identifier and version, normalized arguments, target resources, data sensitivity, reversibility, current application/window context, origin of the request, prior grants, rate limits, and runtime posture. Unknown tools, fields, enum values, paths, identities, or policy errors fail closed.

Indicative risk classes:

| Class | Examples | Default posture |
| --- | --- | --- |
| Local low-risk read | Public system version, opted-in project metadata | Allow only within configured scope and limits |
| Sensitive read | Personal files, screen contents, credentials-adjacent paths | Explicit scope; approval when context is not already user-selected |
| Reversible side effect | Launching an app, creating a new file in an approved workspace | Policy-limited; approval based on scope and frequency |
| Destructive or external side effect | Delete/overwrite, process termination, sending data, purchases, messages | Exact explicit approval; deny when safe preview or identity is unavailable |
| Privileged/security-sensitive | Elevation, credential access, security settings, persistence mechanisms | Deny by default; require a dedicated design before any implementation |

Approval fatigue is a security defect. Policies should support clear scoped grants, previews, and safe batch semantics without turning a past approval into ambient authority.

## Approval binding

An approval record must bind at least:

- user and interactive session;
- tool identifier and contract version;
- canonical normalized arguments or their cryptographic digest;
- target resource scope and sensitivity;
- human-readable preview shown to the user;
- issue and expiry time;
- one-shot or explicitly bounded reuse rules;
- correlation identifier and policy version.

Any material argument, target, context, or tool-version change invalidates the approval. A model cannot approve, dismiss, or synthesize approval. Approval UI must make cancellation and denial at least as accessible as confirmation.

## Tool execution controls

- Validate syntax and semantics before policy evaluation.
- Resolve canonical paths and identities as close to execution as possible, and re-check safety after resolution.
- Pass typed arguments directly to OS APIs. Do not concatenate shell command strings.
- Apply timeouts, cancellation, output limits, rate limits, and concurrency limits.
- Use the current user's least-privileged token and Windows ACLs; do not silently elevate.
- Prevent arbitrary adapter loading or tool registration from model-provided identifiers.
- Separate preview/planning from commit when an operation supports it.
- Return structured errors that do not disclose secrets or internal handles.

General shell execution is exceptionally high risk. A future design must prefer executable allowlists and structured argument arrays, define working-directory and environment rules, block interactive/elevation behavior, cap output and duration, and explicitly address PowerShell/cmd parsing. It must receive a dedicated ADR and threat review.

## Prompt-injection containment

- Label provider instructions, user instructions, retrieved content, and tool output by origin.
- Never treat content inside retrieved data as policy, consent, or a tool request.
- Tool requests must conform to the local catalog; extra fields and unknown tool names are rejected.
- Minimize tool output before returning it to a model and remove control sequences or unsafe binary content.
- Require authorization based on user intent and local policy, not the model's explanation of why an action is safe.
- Test with malicious filenames, source comments, documents, transcripts, and window text.

Prompt injection cannot be solved only with a system prompt. The security boundary is local validation, authorization, and limited execution.

## Audit requirements

Every tool attempt creates correlated records for proposal, validation, policy decision, approval, execution start, and terminal result. Denied, cancelled, timed-out, invalid, and failed requests are recorded as well as successes.

Audit events include timestamps, event and correlation identifiers, local actor/session, tool and policy versions, redacted/canonical argument summary, decision and reason code, approval reference, duration, result classification, and bounded resource-impact metadata.

Audit events must not include raw secrets, complete sensitive files, unrestricted screen text, full audio, or unbounded process output. Sensitive fields use explicit redaction or keyed digests. Access, retention, rotation, export, deletion, integrity protection, and behavior when the audit sink is unavailable must be decided before side-effecting tools ship. High-risk execution should fail closed if its required audit record cannot be durably written.

## Secrets and configuration

- Commit only non-sensitive defaults.
- Use .NET user secrets for local development; they live outside the repository and are not a production store.
- Use environment variables only when process/environment disclosure risk is understood.
- Never accept secrets through model context or log them during validation failures.
- Future provider credentials should use a Windows-protected or dedicated secret store, have least privilege, support rotation/revocation, and be separated by environment.
- Do not store credentials in SQLite, project indexes, memory records, transcripts, or audit payloads.
- Secret scanning belongs in local/CI validation before provider integration begins.

The repository ignores common local settings, key files, logs, databases, and indexes. Ignore rules reduce accidents but are not a security boundary.

## Local data protection

Before persistent personal data ships, define:

- the exact data classes and purpose;
- storage location and Windows ACL expectations;
- encryption requirements and key ownership;
- retention defaults and maximums;
- user view, export, correction, and deletion workflows;
- backup and deletion semantics;
- corruption recovery and schema migrations.

Memory must preserve provenance and uncertainty. Project indexes must be disposable and rebuildable. Deleting a source or revoking a root should remove derived data according to documented timing.

## Dependency and update security

Dependencies require a purpose, license review, maintained provenance, and a plan for vulnerability updates. Pin versions centrally and review transitive changes. Provider SDKs, parsers, audio codecs, and UI automation libraries are especially sensitive because they process hostile or complex input.

Binary update signing and delivery are not designed in 0.0. Auto-update must not be added casually; it changes the trust and code-execution boundary.

## 0.1 voice security posture

Version 0.1 adds an explicitly started remote voice session and therefore adds microphone, provider-network, credential, and transcript exposure. Its controls are:

- the credential is loaded from user secrets or `JARVIS_` environment configuration and is never part of a Core object, protocol payload, committed setting, exception display, or log field;
- the configured credential-bearing connection is restricted to the official secure OpenAI realtime endpoint;
- microphone frames leave the machine only after `/start` (or configured auto-start), and push-to-talk captures only between `/ptt` and `/send`;
- raw input/output audio and transcript text are not written to structured logs or storage by JARVIS;
- provider messages are size-bounded, parsed as untrusted data, and reduced to provider-neutral events with sanitized error codes;
- outbound queues and reconnect attempts are bounded, and stale audio/turn messages are discarded rather than replayed after reconnect;
- the provider adapter has no tool catalog or operating-system action authority. Version 0.1 contains no computer-control tools.

The debug console intentionally displays assistant transcript text to the interactive user. Redirecting console output can persist that text and is therefore a user-controlled privacy decision. OpenAI-side data handling is governed by the selected account and service terms; JARVIS does not make a local-retention claim about provider processing.
