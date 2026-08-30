# Project intelligence architecture

## Purpose

Project intelligence will help JARVIS discover opted-in repositories, understand C# structure and dependencies, answer with file/symbol evidence, and later support tutoring and project-specific interview practice. It must remain local-first and send only the minimum relevant context to a remote model.

This is a future subsystem. Version 0.0 does not discover repositories, read project contents, run Roslyn, build an index, or call an embedding/model provider.

## Design goals

- Operate only on roots the user has explicitly selected or approved.
- Understand C# at solution, project, syntax, symbol, reference, and dependency levels.
- Keep raw content and derived indexes local by default.
- Retrieve a small evidence set for each question instead of uploading a repository.
- Attach source identity and freshness to every factual claim.
- Treat repository content as untrusted data, including comments, build files, generated code, and filenames.
- Degrade clearly when a solution does not load or evidence is incomplete.

## Planned stages

```text
Approved roots
  -> repository and solution discovery
  -> file policy / ignore / sensitivity filtering
  -> metadata and content fingerprinting
  -> Roslyn workspace and build graph analysis
  -> local lexical, symbol, relation, and optional vector indexes
  -> query planning and bounded retrieval
  -> evidence bundle with freshness/provenance
  -> provider-neutral explanation or learning workflow
```

Indexing and retrieval are separate from explanation. A provider receives a curated evidence bundle, not a path it can browse or permission to request arbitrary local files.

## Root approval and discovery

- No drive-wide scan runs by default.
- The user adds an absolute root and sees what repository/solution discovery will cover.
- Roots have stable local identifiers, display labels, include/exclude rules, and revocation state.
- Discovery respects repository ignore files and JARVIS-specific exclusions, with hard limits for depth, file count, individual size, total bytes, and elapsed time.
- Reparse points, symlinks, network paths, removable media, submodules, nested repositories, and case normalization have explicit policies.
- Revoking a root stops watchers and removes derived index data under a documented deletion policy.

Discovery produces candidates; it does not run project code, restore packages, evaluate arbitrary scripts, or trust repository configuration automatically.

## Content policy

Files are classified before parsing or indexing. Initial defaults should exclude secrets/key material, binary files, build outputs, dependency caches, generated assets, user profiles, and oversized/minified content. Common secret filenames and high-entropy/key patterns trigger exclusion or explicit review, not upload.

Each indexed item records:

- approved root and normalized relative path;
- content fingerprint, size, timestamps, and encoding;
- language/content kind and generated/vendor classification;
- ignore/sensitivity decision and reason;
- parser/index schema and tool versions;
- last successful observation and any errors.

Indexes are disposable derivatives. Source files remain authoritative.

## C# and Roslyn analysis

Roslyn belongs in Infrastructure behind Core-owned project-analysis ports because it is an implementation library, not a domain dependency. Analysis should progress in layers:

1. Parse solution and project metadata without executing project output.
2. Build a project reference graph and target-framework/configuration view.
3. Create syntax trees and declarations for C# files.
4. Create compilations/semantic models where dependencies can be resolved safely.
5. Extract symbols, containment, inheritance, implementations, calls/references, attributes, diagnostics, and source locations.
6. Record confidence and failure information for projects that are only partially loadable.

MSBuild evaluation can execute imports/tasks or access feeds and therefore requires a security design. A safe first implementation may use static project parsing or an isolated, constrained analysis process before enabling full `MSBuildWorkspace`. Package restore and repository build commands are never implicit indexing steps.

Stable local symbol identities should include project/configuration plus Roslyn symbol identity and source locations, while tolerating file edits and overloads. Derived graph edges retain provenance to the source/compiler observation that produced them.

## Local index model

A useful index may combine:

- repository, solution, project, package, and project-reference metadata;
- path/file metadata and content fingerprints;
- lexical text search over selected source and documentation;
- symbol declarations, documentation, signatures, and source spans;
- structural edges such as contains, references, inherits, implements, constructs, and invokes;
- optional locally generated embeddings for semantic recall.

Do not choose SQLite, a vector database, or an embedding provider until measured query needs justify it. Persistence remains an Infrastructure detail. Schema versions, migrations, corruption recovery, rebuild, compaction, size limits, and deletion are required before an index becomes durable product state.

## Retrieval and evidence

Query planning should combine repository scope, exact path/name matches, lexical results, symbol graph traversal, diagnostics, and optional semantic retrieval. Results are reranked and deduplicated locally.

An evidence item contains:

- approved root/repository identity;
- normalized relative path;
- symbol identity and kind when applicable;
- precise line/span or metadata location;
- a bounded excerpt or structured fact;
- content fingerprint and observation/index time;
- retrieval reason and score/confidence;
- truncation, generated-code, and staleness flags.

The explanation layer cites these items for architectural and code claims. It distinguishes direct evidence from inference and asks for broader scope when the current evidence cannot answer. Source citations must remain useful after display without leaking absolute personal paths to a remote provider; path mapping can occur locally before/after the call.

## Context minimization

- Prefer signatures, summaries, selected spans, and structured relations over entire files.
- Apply per-item and total byte/token budgets before provider transmission.
- Keep retrieval expansion local and require a reason for each added neighbor/file.
- Redact/exclude secrets before context construction and record that evidence is incomplete.
- Send only repositories and branches relevant to the active user question.
- Do not use a remote provider to decide which unrestricted local paths to read.
- Cache remote-derived summaries only with provenance, provider/model identity, and invalidation rules.

## Freshness and change handling

Filesystem watchers are hints, not proof. Periodic/requery fingerprint checks reconcile missed or coalesced events. Index updates should be incremental, cancellable, prioritized below interactive work, and transactionally visible so queries do not mix incompatible schema/generation states.

Before answering, retrieval verifies important evidence against current fingerprints when practical. Git working tree, branch/commit, unsaved editor state, generated files, and build configuration are separate freshness dimensions and must not be conflated.

## Grounded explanations

Answers about existing projects should provide:

- the direct answer at the user's requested depth;
- supporting file and symbol references;
- relevant dependency/call flow when evidence supports it;
- the active solution/configuration and index freshness when material;
- explicit uncertainty, missing projects, load errors, or inferred relationships.

A generated explanation is not written back to the repository unless the user separately authorizes an editing workflow through tools.

## Tutoring and interview features

Tutoring and mock interviews consume the same evidence service rather than creating unrestricted repository access.

- Lessons map concepts to actual project symbols and code evidence.
- Questions are generated from verified architecture/implementation facts and tagged by skill and difficulty.
- Evaluations separate factual correctness, reasoning, communication, and project-specific evidence.
- Weakness tracking stores compact user-visible learning records with provenance and deletion controls, not hidden psychological profiles.
- Model grading is treated as advisory; rubrics and evidence make evaluation reviewable.

These features follow retrieval-quality evaluation because poor grounding would teach or grade against fabricated architecture.

## Security considerations

- Source comments and documentation may contain prompt injection; they remain quoted/labeled evidence, never instructions.
- Project files may attempt code execution during evaluation; analysis must avoid or contain it.
- Filenames, logs, `.env` files, certificates, package sources, and generated artifacts may reveal secrets.
- Repository membership does not imply permission to transmit every file.
- Index/search results are read tools and require data-scope policy and audit appropriate to their sensitivity.
- A remote provider never receives local filesystem credentials or direct index access.
- Absolute paths and developer identity information are minimized in logs and remote requests.

## Evaluation strategy

Build a small, consented fixture corpus with known answers and expected evidence. Measure:

- discovery and ignore-rule correctness;
- symbol/reference/project graph precision and recall;
- index freshness and update latency;
- retrieval recall at fixed context budgets;
- citation correctness and support for generated claims;
- secret/sensitive-file exclusion;
- behavior for broken solutions, generated code, conditional compilation, multi-targeting, and partial loads;
- resistance to prompt injection embedded in source and documentation;
- local resource use and cancellation responsiveness.

No repository-wide provider integration should ship until these local retrieval and privacy properties are observable.
