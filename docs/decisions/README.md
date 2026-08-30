# Architecture decisions

Architecture decision records (ADRs) capture choices that are expensive to reverse, affect several modules, introduce a major dependency, or change a security or privacy boundary. Small implementation details belong in code and tests, not in ADRs.

## Process

1. Copy the template below to `NNNN-short-title.md` using the next number.
2. Open it as **Proposed** before or with the implementing change.
3. Record considered alternatives and concrete consequences, including security and migration impact.
4. Mark it **Accepted**, **Rejected**, or **Superseded** after review.
5. Never rewrite history to hide a changed decision; supersede the older record and link both records.

## Foundation decision index

The 0.0 decisions are summarized here because they establish repository policy rather than select a feature implementation. Future changes to them should receive individual ADR files.

| ID | Decision | Status | Rationale |
| --- | --- | --- | --- |
| F-001 | Begin as one modular local process | Accepted | Minimizes operational complexity while boundaries are still evolving. |
| F-002 | Use .NET 10, C# 14, and Windows as the first target | Accepted | Matches the product's Windows-first integrations and current LTS baseline. |
| F-003 | Keep Core free of platform, provider, UI, and persistence dependencies | Accepted | Preserves replacement, deterministic testing, and inward dependency flow. |
| F-004 | Use the .NET Generic Host for lifetime, DI, configuration, and logging | Accepted | Provides maintained application plumbing without a custom container or host framework. |
| F-005 | Treat warnings as errors and use SDK analyzers | Accepted | Prevents the initial quality baseline from silently degrading without adding an analyzer package. |
| F-006 | Manage package versions centrally | Accepted | Makes dependency review and upgrades visible in one small file across the solution. |
| F-007 | Keep safe defaults in tracked settings and secrets outside the repository | Accepted | Supports local development while reducing accidental credential exposure. |
| F-008 | Add projects only when a cohesive implemented module needs a boundary | Accepted | Avoids empty project sprawl and premature distribution decisions. |

## Decision records

| ID | Decision | Status |
| --- | --- | --- |
| [0001](0001-realtime-voice-transport-and-windows-audio.md) | Realtime WebSocket transport and WinMM audio for the 0.1 vertical slice | Accepted |

## ADR template

```markdown
# NNNN — Title

- Status: Proposed
- Date: YYYY-MM-DD
- Owners: names or team
- Supersedes: none

## Context

What forces, constraints, and evidence make a decision necessary?

## Decision

What will be done, including the scope and boundary?

## Alternatives considered

What credible options were considered and why were they not selected?

## Consequences

What becomes easier or harder? Include security, privacy, testing, operations,
migration, and dependency consequences where relevant.

## Validation

How will the decision be verified in code, tests, telemetry, or a prototype?
```
