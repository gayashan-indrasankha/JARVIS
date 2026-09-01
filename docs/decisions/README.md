# Architecture decisions

ADRs capture expensive, cross-module, or security-boundary choices. Small implementation details belong in code/tests. Never rewrite history to hide a changed decision; mark it superseded and link the replacement.

## Foundation decisions

| ID | Decision | Status |
| --- | --- | --- |
| F-001 | One modular local application; helper processes only for native isolation | Accepted |
| F-002 | .NET 10/C# 14, Windows-first | Accepted |
| F-003 | Core free of platform, provider/model, UI, network, and persistence dependencies | Accepted |
| F-004 | Generic Host for lifetime, DI, configuration, and structured logging | Accepted |
| F-005 | warnings as errors, SDK analyzers, central package versions | Accepted |
| F-006 | safe tracked defaults; runtime/model/data artifacts outside Git | Accepted |
| F-007 | add projects only for demonstrated cohesive boundaries | Accepted |

## Decision records

| ID | Decision | Status |
| --- | --- | --- |
| [0001](0001-realtime-voice-transport-and-windows-audio.md) | Cloud realtime transport prototype | Superseded by 0002 |
| [0002](0002-local-inference-and-speech-runtime.md) | Local llama.cpp and sherpa-onnx voice runtime | Accepted |
| [0003](0003-local-wake-word-activation.md) | Local open-vocabulary wake activation and continuation lifecycle | Accepted |

## ADR template

```markdown
# NNNN — Title

- Status: Proposed
- Date: YYYY-MM-DD
- Owners: team
- Supersedes: none

## Context
## Decision
## Alternatives considered
## Consequences
## Validation
```
