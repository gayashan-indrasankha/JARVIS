# 0005 — Local Project Intelligence index

- Status: Accepted
- Date: 2026-09-02
- Owners: team
- Supersedes: roadmap ordering only; no prior architecture ADR

## Context

JARVIS must answer questions about real C#/.NET repositories using local file/symbol evidence without uploading source or giving the local model filesystem access. Repositories are untrusted: evaluating an MSBuild project can import files and execute custom tasks, while restoring/building can run generators, targets, package behavior, and binaries. The baseline Qwen 4B context and target laptop resources also prohibit whole-repository prompts or an unbounded in-memory index.

The earlier roadmap placed approval/mutation in 0.3 and Project Intelligence in 0.4. The requested milestone explicitly moves read-only Project Intelligence to 0.3. This changes sequencing, not the accepted local inference or tool-kernel boundaries; controlled mutation moves to 0.4.

## Decision

Implement Project Intelligence inside the existing modular application:

- Core owns provider/platform-neutral project, evidence, classification, context-budget, query, and typed tool contracts.
- Infrastructure owns safe discovery, static project XML parsing, a Roslyn `AdhocWorkspace`, SQLite/FTS5 persistence/retrieval, fixed Git metadata, watchers, and content-free metrics.
- Host remains composition/configuration only; no new physical project or service is justified.
- Every model-requested project operation uses the existing registry, closed JSON schema, validation, authorization, timeout, audit, loop, and result-size pipeline.
- `analyze_project` is `SAFE_LOCAL_ACTION`; the ten evidence queries are `SAFE_READ`.
- Do not use `MSBuildWorkspace` for untrusted input. Parse literal SDK-style metadata with DTD/entity resolution disabled, then construct the Roslyn workspace directly from accepted source text and in-repository project references.
- Persist one local SQLite database under `JARVIS_HOME\Data\ProjectIntelligence`; use FTS5 for lexical retrieval and relational tables for snapshots, symbols, relationships, and facts.
- Use metadata-assisted incremental SHA-256 snapshots. Reuse unchanged content/hash; rebuild the cross-file semantic graph when a snapshot changes; skip Roslyn when no file changed.
- Retrieve exact symbols first, then Roslyn relationships/framework facts, then FTS/context. Cap evidence for the local 4B model and never provide the whole repository.
- Defer vectors/embeddings until measured retrieval failures justify a local semantic-search abstraction.

## Alternatives considered

### `MSBuildWorkspace`

Rejected for this trust boundary. It provides richer evaluated project fidelity but can load untrusted imports/tasks and creates pressure to restore/build. A future opt-in trusted-repository mode would require a separate authorization/sandbox decision.

### Execute `dotnet build` and inspect compiler output

Rejected. It directly violates the no-repository-execution requirement and can run targets, generators, scripts, and package behavior.

### Regex-only analysis

Rejected. It cannot reliably distinguish declarations or provide semantic source relationships. Syntax fallbacks are allowed only where Roslyn cannot resolve an external type/member and must remain conservative.

### Vector database and embeddings

Deferred. Exact symbols, graph facts, FTS5, and bounded source context satisfy the first milestone with lower memory, dependency, privacy, and model-lifecycle cost.

### Separate indexing service/project

Rejected for 0.3. The implementation is cohesive within Infrastructure, uses the existing process lifetime, and does not justify a new deployment or authority boundary.

## Consequences

Benefits:

- repository content remains local and model access remains typed/non-bypassable;
- exact file/line/hash/snapshot evidence supports grounded answers;
- SQLite/FTS5 provides bounded persistent retrieval without loading an entire repository into model context;
- static parsing cannot execute hostile MSBuild targets;
- new packages remain isolated from Core.

Costs and limits:

- static project metadata does not evaluate conditions/imports or generated source;
- unresolved package types can reduce semantic call/type precision;
- changed snapshots rebuild the semantic graph rather than performing a risky partial cross-file update;
- the local index stores private source-derived text and needs an explicit future retention/deletion UI;
- FileSystemWatcher is best-effort and overflow triggers a full bounded refresh;
- runtime behavior, reflection, dynamic dispatch, configuration branches, and non-C# code require inference/manual verification.

## Validation

- architecture tests keep Roslyn/SQLite/Infrastructure out of Core and keep model adapters away from project/index/tool execution services;
- fixture tests cover solution/project/source discovery, literal references, test projects, namespaces/types/members, implementations/calls, endpoints, DI/authentication/EF clues, Git metadata, FTS evidence, context budgets, incremental snapshots, cancellation, and debounce;
- hostile fixture tests prove MSBuild `Exec` targets are inert, DTD/entity input fails closed, and generated/build/credential content is excluded;
- registry tests prove all eleven exact schemas/categories and the normal authorization/audit path;
- Debug/Release restore, build, tests, formatter, vulnerability scan, repository scans, and the manual real-repository/local-model test are release gates.
