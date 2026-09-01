# Project intelligence architecture

Project intelligence is a future capability. Version 0.1 does not scan repositories, build indexes, parse projects, or send project content to any model. This document fixes the intended boundary so voice and future tools do not grow an accidental repository-access path.

## Goal

Within user-approved roots, JARVIS should discover projects, understand C# symbols/dependencies with Roslyn, retrieve minimum relevant evidence, explain code with file/symbol citations, tutor against the actual project, and generate/evaluate project-specific interview questions.

## Proposed flow

```text
approved roots
  -> discovery and ignore policy
  -> metadata/content extraction
  -> Roslyn workspace + symbol/dependency graph
  -> local bounded index
  -> ranked query retrieval
  -> minimum evidence bundle
  -> provider-neutral local language model
  -> grounded answer with evidence/provenance
```

The local language model never opens repositories itself. Discovery/read/index operations will be platform adapters invoked through scoped typed capabilities. Retrieved snippets become untrusted model context, not instructions or authorization.

## Boundaries

Core should own provider-neutral query/evidence records, ranking/grounding policy, context budgets, and explanation outcomes. Infrastructure should own filesystem enumeration, encoding detection, Roslyn integration, local index persistence, and model adaptation. Host/UI owns root selection, consent, progress, evidence display, and cancellation.

No new project is justified until implementation size or dependency isolation demonstrates value. Roslyn and persistence packages must never leak into Core.

## Privacy and safety

- Discovery is opt-in and limited to canonical approved roots; no whole-drive scan.
- Ignore VCS/build/cache/generated/binary/secret patterns before reading content.
- Symlinks/reparse points, traversal, inaccessible files, size, encoding, and cancellation require explicit handling.
- Store indexes under `JARVIS_HOME\Data`, never beside repositories or in Git.
- Index only necessary content and attach source path, revision/hash, symbol, and extraction time.
- Keep retrieved context small and local. The normal runtime has no external AI endpoint, so project context is not uploaded.
- Future optional external providers would require a new explicit architecture/security decision and user consent; they must not silently reuse this pipeline.
- Treat comments, docs, generated text, and model output as untrusted prompt content.

## Grounded answers

An answer is grounded only when its claims reference retrieved evidence and the referenced file/symbol exists in the analyzed snapshot. The response must distinguish direct evidence, inference, and uncertainty. Stale indexes are invalidated by repository/file identity and revision changes; the user can inspect and delete local index data.

## Testing strategy

Use temporary synthetic repositories for discovery, traversal, ignores, cancellation, encoding, incremental invalidation, Roslyn graph extraction, ranking, and evidence citations. Tests must not rely on a user's real repository, downloaded model, network, or destructive filesystem operation.

## Open decisions

- supported project/repository types beyond C#;
- local search/index technology and encryption/retention;
- snapshot and invalidation granularity;
- evidence-bundle/token budgeting for the active local model;
- benchmark datasets for explanation and interview evaluation quality.
