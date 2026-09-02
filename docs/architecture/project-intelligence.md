# Project Intelligence architecture

Version 0.3 implements fully local, evidence-grounded analysis for approved C#/.NET Git repositories. Repository content never leaves the machine and never becomes an operating-system instruction. The local model can request ProjectTools, but only trusted code discovers files, builds the index, retrieves evidence, and enforces the existing authorization boundary.

## Runtime flow

```text
approved direct Git working tree
  -> safe bounded discovery
  -> static .sln/.slnx/.csproj metadata loading
  -> Roslyn AdhocWorkspace from discovered C# text
  -> symbols + relationships + framework facts
  -> incremental SHA-256 snapshot
  -> local SQLite metadata + FTS5
  -> exact symbol / Roslyn / FTS retrieval
  -> bounded evidence bundle
  -> existing local Qwen agent loop
  -> classified grounded answer
```

The project-intelligence components remain namespaces in the existing projects. Core owns only provider/platform-neutral project records, evidence/classification outcomes, context accounting, service ports, and typed ProjectTool contracts. Infrastructure owns filesystem discovery, static XML parsing, Roslyn, Git invocation, SQLite/FTS5, ranking, watching, and structured metrics. Host remains the composition/configuration boundary. Roslyn and SQLite assemblies do not appear in `Jarvis.Core`.

## Repository trust boundary

`analyze_project` accepts only a canonical direct Git working tree below a configured `Tools:AllowedRoots` entry. It rejects absent roots, `.git` indirection files, UNC/network paths, ambiguous Windows aliases, alternate data streams, existing reparse points, and credential-oriented paths. The discovery walker never follows a reparse point and excludes `.git`, `bin`, `obj`, build/test/coverage output, packages, IDE caches, `node_modules`, generated directories/source suffixes, binaries, model assets, `.env*`, secrets, credentials, keys, and other sensitive patterns before content is read.

The default limits are 20,000 accepted text files, 2 MiB per file, and 64 MiB total accepted text. Input is decoded as bounded text; invalid text is rejected for source/project metadata and skipped for optional documentation/configuration. Configuration files containing common credential-bearing property markers (for example connection strings, passwords, API keys, private keys, and refresh tokens) are excluded as a whole rather than partially indexed. Cancellation is checked during traversal, reads, analysis, persistence, and retrieval.

Repositories are untrusted input. JARVIS does not:

- invoke `dotnet`, MSBuild, a compiler, package manager, source generator, test runner, script host, or repository executable;
- evaluate imports, conditions, properties, targets, tasks, `Exec`, post-build events, package scripts, or launch settings;
- restore packages or load assemblies/native libraries from the repository;
- run Git hooks, submodules, credential prompts, or model-selected Git arguments;
- follow symlinks/junctions or read credential/secret stores merely because they are inside an approved root.

## Safe project and Roslyn loading

`.csproj` files are parsed as XML with DTD processing prohibited, entity resolution disabled, and a document-size cap. JARVIS reads literal SDK-style target frameworks, assembly/root namespace, output type, project references, package references, and test markers. It deliberately does not evaluate MSBuild expressions. `.sln` and `.slnx` files are discovered and indexed as evidence; project membership is derived from safely discovered `.csproj` files.

Infrastructure constructs a Roslyn `AdhocWorkspace` from accepted C# source text. It adds only .NET trusted-platform metadata references and statically discovered in-repository project references. It never uses `MSBuildWorkspace`, because opening an untrusted project through MSBuild evaluation can load imports/tasks and contradict the no-execution boundary. The workspace is in memory and disposed after each changed snapshot analysis.

Roslyn extraction records:

- namespaces; classes, records, interfaces, structs, enums, delegates; constructors, methods, properties, fields, and events;
- base-type and interface-implementation relationships;
- source-call relationships when semantic resolution succeeds, with a conservative name-only syntax edge when unresolved;
- controller conventions, controller/minimal-API endpoints, route/HTTP-method evidence;
- common dependency-injection registration methods;
- authentication/authorization attributes and pipeline registrations;
- EF Core `DbContext`, `DbSet<T>` entity, and common provider-registration clues where statically observable;
- project/package references, target frameworks, test projects, and solution presence.

Static analysis is not runtime truth. Reflection, convention libraries, dynamic dispatch, generated code, conditional MSBuild, runtime configuration, middleware branches, and unresolved packages can make a trace incomplete. Such conclusions must be labeled `INFERENCE`, not `PROJECT FACT`.

## Index and refresh

The single local database is `%LOCALAPPDATA%\JARVIS\Data\ProjectIntelligence\project-index.db`, or the equivalent validated `JARVIS_HOME` location. It never lives beside a repository and is ignored by Git. SQLite tables store repository snapshots, bounded source text/hash metadata, projects, symbols, relationships, and extracted facts. FTS5 indexes accepted local text and extracted evidence for lexical retrieval. The database therefore contains private source-derived data; it is local user data, should be protected by the user's account/filesystem, and can be deleted while JARVIS is stopped.

Each file records repository-relative path, kind, length, UTC modification ticks, SHA-256 content hash, and text. An unchanged length/timestamp reuses the prior content/hash. Added/changed files are read and hashed; removed files disappear in the next atomic transaction. A changed snapshot rebuilds the cross-file semantic graph to avoid stale relationships. A no-change refresh skips Roslyn and updates Git metadata only.

Before returning any evidence, retrieval validates the indexed row against the snapshot file metadata, normalizes the current path through the approved-root/reparse policy, and recomputes the current file's SHA-256 content hash. A same-length edit with a restored timestamp therefore fails as `project_index_stale` instead of returning obsolete evidence. Hashes are cached only for the duration of that bounded query.

After a successful authorized analysis, one bounded `FileSystemWatcher` per repository (maximum eight by default) observes changes. A capacity-one signal and 750 ms debounce coalesce bursts. Refreshes share one async index lock, are cancellable at shutdown, and use no retry storm. Watcher overflow requests one full refresh; failures log only repository ID and sanitized category.

Git branch/status uses the same direct, fully resolved Git executable and bounded non-interactive environment as `get_git_status`. It pins `--git-dir`/`--work-tree`, disables prompts/global/system configuration, ignores submodules, and executes no repository-selected arguments.

## Retrieval, grounding, and context budget

Retrieval order is:

1. exact/qualified symbol match;
2. Roslyn relationships and framework facts;
3. parameterized SQLite FTS5 lexical results;
4. a bounded exact line window from the matched source;
5. local-model reasoning over that evidence only.

There is no vector database or embedding service in 0.3. An embedding abstraction is intentionally deferred until a local benchmark demonstrates a retrieval gap that lexical/symbol search cannot solve.

The default evidence-content budget is 8,192 characters with 1,500 characters per excerpt. Budget accounting includes conservative per-claim serialization overhead so the complete JSON observation stays below the broker's 16,384-character default result cap. Queries load only snapshot and file metadata before running their bounded evidence query; they do not materialize every stored source file. Every response reports used characters, approximate four-characters-per-token count, evidence count, candidate count, retrieval milliseconds, and whether evidence was truncated. File results calculate one-based supporting lines from the actual indexed text. Evidence includes repository-relative path, start/end line, symbol when known, excerpt, content hash, and snapshot ID. The entire repository is never sent to Qwen.

Dependency and request-flow tools traverse the stored relationship graph breadth-first with a visited set, explicit depth limit, and candidate cap. Endpoint traversal starts only from discovered endpoint evidence. Roslyn relationship extraction groups declarations by source file rather than rescanning the complete symbol map for every document, keeping the relationship pass proportional to relevant declarations plus invocations.

Answer claims use exactly these meanings:

- `PROJECT FACT` — directly supported by returned snapshot evidence;
- `INFERENCE` — a bounded conclusion suggested by facts but not directly proven;
- `GENERAL SOFTWARE ENGINEERING KNOWLEDGE` — general explanation not asserted to describe this repository.

The agent policy requires a file/line reference for every project fact and forbids invented paths, symbols, relationships, or line ranges. File, documentation, terminal, and repository text is labeled untrusted data and cannot alter system policy, authorize another tool, or invoke a tool.

## ProjectTools and authorization

All eleven tools are registered in the existing trusted catalog:

| Tool | Category | Purpose |
| --- | --- | --- |
| `analyze_project` | `SAFE_LOCAL_ACTION` | Create/refresh the local index and watcher. |
| `get_project_overview` | `SAFE_READ` | Summarize README/solution/project/framework evidence. |
| `search_project` | `SAFE_READ` | Exact-symbol then FTS retrieval. |
| `find_symbol` | `SAFE_READ` | Locate declarations. |
| `explain_symbol` | `SAFE_READ` | Retrieve declaration and relationship evidence. |
| `find_references` | `SAFE_READ` | Retrieve call/inheritance/implementation/reference edges. |
| `trace_dependency` | `SAFE_READ` | Traverse a bounded relationship neighborhood. |
| `trace_request_flow` | `SAFE_READ` | Start at a discovered endpoint and retrieve bounded downstream edges. |
| `list_api_endpoints` | `SAFE_READ` | List controller and minimal API endpoint facts. |
| `list_project_dependencies` | `SAFE_READ` | List static project/package references. |
| `explain_architecture` | `SAFE_READ` | Retrieve bounded project/DI/API/data/test evidence plus explicit inference. |

Requests have closed JSON schemas, strict text/count/depth limits, canonical repository normalization, repeated-call protection, authorization, 120-second index/15-second query deadlines, result truncation, and content-minimized terminal audits. A malformed, unknown, unauthorized, timed-out, cancelled, or failed request cannot execute or be reported as success.

## Metrics and privacy

Structured project metrics contain only a one-way repository identifier, initial/incremental flag, elapsed milliseconds, file/change/symbol/relationship counts, retrieval latency, candidate count, context characters, estimated tokens, and truncation state. They do not contain repository paths, filenames, query text, source, excerpts, prompts, answers, branch/status content, or exception text. Existing local LLM metrics provide prompt processing, warm first-token, and generation rate; end-to-end voice metrics provide first-audio timing. Physical/model answer latency must be recorded by the manual test and is not inferred from unit tests.

## Verification and limitations

The synthetic fixture is copied to temporary directories and is never built. Tests cover initial/incremental indexing, exact evidence lines and current-content hashes, multi-hop request flow, symbol/implementation/call extraction, endpoints, DI/authentication/EF/package/test clues, FTS5, context limits, generated/credential exclusions, hostile DTDs, inert MSBuild `Exec` targets, cancellation, disabled configuration, watcher debounce/failure containment, closed schemas, categories, and regression boundaries. They require no network, model, microphone, GPU, real user repository, or destructive machine action.

Manual validation remains required for local-model tool selection, answer usefulness, a real repository with unresolved dependencies/conditional projects, physical voice, and latency measurements. See [the manual Project Intelligence smoke test](../testing/manual-project-intelligence-smoke-test.md).
