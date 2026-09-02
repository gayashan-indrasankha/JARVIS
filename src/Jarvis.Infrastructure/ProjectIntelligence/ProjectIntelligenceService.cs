using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Jarvis.Core.ProjectIntelligence;
using Jarvis.Infrastructure.Configuration;
using Jarvis.Infrastructure.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Infrastructure.ProjectIntelligence;

internal sealed class ProjectIntelligenceService(
    ToolPathPolicy pathPolicy,
    SafeRepositoryDiscovery discovery,
    RoslynProjectAnalyzer analyzer,
    SqliteProjectIndexStore store,
    IGitRepositoryMetadataReader gitReader,
    ProjectWatchManager watchManager,
    IOptions<ProjectIntelligenceOptions> options,
    TimeProvider timeProvider,
    ILogger<ProjectIntelligenceService>? logger = null) : IProjectIntelligenceService, IAsyncDisposable
{
    private readonly ProjectIntelligenceOptions _options = options.Value;
    private readonly ILogger<ProjectIntelligenceService> _logger = logger ??
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectIntelligenceService>.Instance;
    private readonly SemaphoreSlim _indexLock = new(1, 1);

    public ValueTask<ProjectIndexReport> AnalyzeAsync(
        string repositoryPath,
        CancellationToken cancellationToken) => AnalyzeCoreAsync(
            repositoryPath,
            registerWatcher: true,
            cancellationToken);

    public async ValueTask<GroundedProjectAnswer> GetOverviewAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        QueryContext context = await GetQueryContextAsync(repositoryPath, cancellationToken)
            .ConfigureAwait(false);
        List<ProjectSearchRow> rows = [];
        foreach (string kind in new[] { "repository_documentation", "solution", "project", "test_project", "controller", "endpoint", "db_context", "database", "authentication" })
        {
            rows.AddRange(await store.FindFactsAsync(context.RepositoryId, kind, 40, cancellationToken)
                .ConfigureAwait(false));
        }

        if (rows.Count == 0)
        {
            rows.AddRange(await store.SearchAsync(context.RepositoryId, "README project", 12, cancellationToken)
                .ConfigureAwait(false));
        }

        return BuildAnswer(context, rows, "Project overview evidence", includeInference: true);
    }

    public async ValueTask<GroundedProjectAnswer> SearchAsync(
        string repositoryPath,
        string query,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        QueryContext context = await GetQueryContextAsync(repositoryPath, cancellationToken)
            .ConfigureAwait(false);
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<ProjectSearchRow> rows = [];
        rows.AddRange(await store.FindSymbolsAsync(context.RepositoryId, query, maximumResults, cancellationToken)
            .ConfigureAwait(false));
        if (rows.Count < maximumResults)
        {
            rows.AddRange(await store.SearchAsync(
                context.RepositoryId,
                query,
                maximumResults - rows.Count,
                cancellationToken).ConfigureAwait(false));
        }

        stopwatch.Stop();
        return BuildAnswer(context with { RetrievalMilliseconds = stopwatch.ElapsedMilliseconds }, rows, "Project search evidence");
    }

    public async ValueTask<GroundedProjectAnswer> FindSymbolAsync(
        string repositoryPath,
        string symbol,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        QueryContext context = await GetQueryContextAsync(repositoryPath, cancellationToken)
            .ConfigureAwait(false);
        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<ProjectSearchRow> rows = await store.FindSymbolsAsync(
            context.RepositoryId,
            symbol,
            maximumResults,
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        return BuildAnswer(context with { RetrievalMilliseconds = stopwatch.ElapsedMilliseconds }, rows, $"Declarations matching {symbol}");
    }

    public async ValueTask<GroundedProjectAnswer> ExplainSymbolAsync(
        string repositoryPath,
        string symbol,
        CancellationToken cancellationToken)
    {
        QueryContext context = await GetQueryContextAsync(repositoryPath, cancellationToken)
            .ConfigureAwait(false);
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<ProjectSearchRow> rows = [];
        rows.AddRange(await store.FindSymbolsAsync(context.RepositoryId, symbol, 8, cancellationToken)
            .ConfigureAwait(false));
        rows.AddRange(await store.FindRelationshipsAsync(context.RepositoryId, symbol, 24, cancellationToken)
            .ConfigureAwait(false));
        stopwatch.Stop();
        return BuildAnswer(context with { RetrievalMilliseconds = stopwatch.ElapsedMilliseconds }, rows, $"Symbol evidence for {symbol}");
    }

    public async ValueTask<GroundedProjectAnswer> FindReferencesAsync(
        string repositoryPath,
        string symbol,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        QueryContext context = await GetQueryContextAsync(repositoryPath, cancellationToken)
            .ConfigureAwait(false);
        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<ProjectSearchRow> rows = await store.FindRelationshipsAsync(
            context.RepositoryId,
            symbol,
            maximumResults,
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        return BuildAnswer(context with { RetrievalMilliseconds = stopwatch.ElapsedMilliseconds }, rows, $"References involving {symbol}");
    }

    public async ValueTask<GroundedProjectAnswer> TraceDependencyAsync(
        string repositoryPath,
        string sourceSymbol,
        string? targetSymbol,
        int maximumDepth,
        CancellationToken cancellationToken)
    {
        QueryContext context = await GetQueryContextAsync(repositoryPath, cancellationToken)
            .ConfigureAwait(false);
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<ProjectSearchRow> rows = [];
        Queue<(string Symbol, int Depth)> queue = new();
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue((sourceSymbol, 0));
        while (queue.Count > 0 && rows.Count < 64)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string current, int depth) = queue.Dequeue();
            if (!visited.Add(current) || depth >= maximumDepth)
            {
                continue;
            }

            IReadOnlyList<ProjectSearchRow> next = await store.FindRelationshipsAsync(
                context.RepositoryId,
                current,
                32,
                cancellationToken).ConfigureAwait(false);
            foreach (ProjectSearchRow row in next)
            {
                rows.Add(row);
                if (!string.IsNullOrWhiteSpace(row.Symbol))
                {
                    queue.Enqueue((row.Symbol, depth + 1));
                }

                if (targetSymbol is not null && row.Excerpt.Contains(targetSymbol, StringComparison.OrdinalIgnoreCase))
                {
                    queue.Clear();
                    break;
                }
            }
        }

        stopwatch.Stop();
        return BuildAnswer(context with { RetrievalMilliseconds = stopwatch.ElapsedMilliseconds }, rows, $"Dependency trace from {sourceSymbol}");
    }

    public async ValueTask<GroundedProjectAnswer> TraceRequestFlowAsync(
        string repositoryPath,
        string endpoint,
        int maximumDepth,
        CancellationToken cancellationToken)
    {
        QueryContext context = await GetQueryContextAsync(repositoryPath, cancellationToken)
            .ConfigureAwait(false);
        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<ProjectSearchRow> endpointRows = await store.FindFactsAsync(
            context.RepositoryId,
            "endpoint",
            256,
            cancellationToken).ConfigureAwait(false);
        ProjectSearchRow[] matches = endpointRows
            .Where(row => row.Excerpt.Contains(endpoint, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToArray();
        List<ProjectSearchRow> rows = [.. matches];
        foreach (ProjectSearchRow match in matches)
        {
            if (match.Symbol is null)
            {
                continue;
            }

            IReadOnlyList<ProjectSearchRow> relationships = await store.FindRelationshipsAsync(
                context.RepositoryId,
                match.Symbol,
                Math.Min(64, maximumDepth * 12),
                cancellationToken).ConfigureAwait(false);
            rows.AddRange(relationships);
        }

        stopwatch.Stop();
        return BuildAnswer(context with { RetrievalMilliseconds = stopwatch.ElapsedMilliseconds }, rows, $"Request-flow evidence for {endpoint}");
    }

    public async ValueTask<GroundedProjectAnswer> ListApiEndpointsAsync(
        string repositoryPath,
        int maximumResults,
        CancellationToken cancellationToken) => await FactAnswerAsync(
            repositoryPath,
            "endpoint",
            maximumResults,
            "Discovered API endpoints",
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<GroundedProjectAnswer> ListDependenciesAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        QueryContext context = await GetQueryContextAsync(repositoryPath, cancellationToken)
            .ConfigureAwait(false);
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<ProjectSearchRow> rows = [];
        rows.AddRange(await store.FindFactsAsync(context.RepositoryId, "project_reference", 128, cancellationToken)
            .ConfigureAwait(false));
        rows.AddRange(await store.FindFactsAsync(context.RepositoryId, "package_reference", 128, cancellationToken)
            .ConfigureAwait(false));
        stopwatch.Stop();
        return BuildAnswer(context with { RetrievalMilliseconds = stopwatch.ElapsedMilliseconds }, rows, "Project and package dependencies");
    }

    public async ValueTask<GroundedProjectAnswer> ExplainArchitectureAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        QueryContext context = await GetQueryContextAsync(repositoryPath, cancellationToken)
            .ConfigureAwait(false);
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<ProjectSearchRow> rows = [];
        foreach (string kind in new[] { "project", "project_reference", "controller", "di_registration", "db_context", "entity", "test_project" })
        {
            rows.AddRange(await store.FindFactsAsync(context.RepositoryId, kind, 64, cancellationToken)
                .ConfigureAwait(false));
        }

        stopwatch.Stop();
        return BuildAnswer(
            context with { RetrievalMilliseconds = stopwatch.ElapsedMilliseconds },
            rows,
            "Architecture evidence",
            includeInference: true);
    }

    public async ValueTask DisposeAsync()
    {
        await watchManager.DisposeAsync().ConfigureAwait(false);
        _indexLock.Dispose();
    }

    private async ValueTask<ProjectIndexReport> AnalyzeCoreAsync(
        string repositoryPath,
        bool registerWatcher,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new ProjectIndexException("project_intelligence_disabled");
        }

        string repository = pathPolicy.NormalizeProjectRepository(repositoryPath);
        string repositoryId = CreateRepositoryId(repository);
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            RepositoryIndexState? previous = await store.LoadStateAsync(repositoryId, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<DiscoveredFile> discovered = await discovery.DiscoverAsync(repository, cancellationToken)
                .ConfigureAwait(false);
            List<IndexedFile> files = [];
            int added = 0;
            int changed = 0;
            int unchanged = 0;
            foreach (DiscoveredFile file in discovered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (previous?.Files.TryGetValue(file.RelativePath, out StoredFileState? stored) == true &&
                    stored.Length == file.Length && stored.LastWriteUtcTicks == file.LastWriteUtcTicks &&
                    stored.Kind == file.Kind)
                {
                    if (SafeRepositoryDiscovery.ContainsLikelySecretConfiguration(file, stored.Content))
                    {
                        continue;
                    }

                    files.Add(new IndexedFile(
                        stored.RelativePath,
                        stored.Kind,
                        stored.Length,
                        stored.LastWriteUtcTicks,
                        stored.ContentHash,
                        stored.Content));
                    unchanged++;
                    continue;
                }

                string content;
                try
                {
                    content = await SafeRepositoryDiscovery.ReadTextAsync(
                        file,
                        _options.MaximumSourceFileBytes,
                        cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (ProjectIndexException) when (file.Kind is IndexedFileKind.Documentation or IndexedFileKind.Configuration)
                {
                    continue;
                }

                if (SafeRepositoryDiscovery.ContainsLikelySecretConfiguration(file, content))
                {
                    continue;
                }

                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
                files.Add(new IndexedFile(
                    file.RelativePath,
                    file.Kind,
                    file.Length,
                    file.LastWriteUtcTicks,
                    hash,
                    content));
                if (previous?.Files.ContainsKey(file.RelativePath) == true)
                {
                    changed++;
                }
                else
                {
                    added++;
                }
            }

            HashSet<string> currentPaths = files
                .Select(static file => file.RelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int removed = previous?.Files.Keys.Count(path => !currentPaths.Contains(path)) ?? 0;
            string snapshotId = CreateSnapshotId(files);
            GitRepositoryMetadata git = await gitReader.ReadAsync(repository, cancellationToken)
                .ConfigureAwait(false);
            bool incremental = previous is not null;
            int projectCount;
            int sourceCount;
            int symbolCount;
            int relationshipCount;
            if (previous is not null && added == 0 && changed == 0 && removed == 0 &&
                previous.SnapshotId.Equals(snapshotId, StringComparison.Ordinal))
            {
                await store.UpdateGitMetadataAsync(
                    repositoryId,
                    git,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                (projectCount, sourceCount, symbolCount, relationshipCount) = await store.GetCountsAsync(
                    repositoryId,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                IReadOnlyList<StaticProject> projects = StaticProjectLoader.Load(files);
                ProjectAnalysisSnapshot analysis = await analyzer.AnalyzeAsync(
                    projects,
                    files,
                    cancellationToken).ConfigureAwait(false);
                await store.SaveAsync(
                    repositoryId,
                    repository,
                    snapshotId,
                    git,
                    files,
                    analysis,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                projectCount = analysis.Projects.Count;
                sourceCount = files.Count(static file => file.Kind == IndexedFileKind.Source);
                symbolCount = analysis.Symbols.Count;
                relationshipCount = analysis.Relationships.Count;
            }

            stopwatch.Stop();
            ProjectIntelligenceLog.IndexCompleted(
                _logger,
                repositoryId,
                incremental,
                stopwatch.ElapsedMilliseconds,
                files.Count,
                added + changed + removed,
                symbolCount,
                relationshipCount);
            if (registerWatcher)
            {
                watchManager.EnsureWatching(
                    repository,
                    repositoryId,
                    token => AnalyzeCoreAsync(repository, registerWatcher: false, token).AsValueTask());
            }

            return new ProjectIndexReport(
                Path.GetFileName(repository),
                snapshotId,
                git.Branch,
                git.StatusSummary,
                projectCount,
                sourceCount,
                added,
                changed,
                removed,
                unchanged,
                symbolCount,
                relationshipCount,
                stopwatch.ElapsedMilliseconds,
                incremental);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async ValueTask<GroundedProjectAnswer> FactAnswerAsync(
        string repositoryPath,
        string kind,
        int maximumResults,
        string description,
        CancellationToken cancellationToken)
    {
        QueryContext context = await GetQueryContextAsync(repositoryPath, cancellationToken)
            .ConfigureAwait(false);
        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<ProjectSearchRow> rows = await store.FindFactsAsync(
            context.RepositoryId,
            kind,
            maximumResults,
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        return BuildAnswer(context with { RetrievalMilliseconds = stopwatch.ElapsedMilliseconds }, rows, description);
    }

    private async ValueTask<QueryContext> GetQueryContextAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        string repository = pathPolicy.NormalizeProjectRepository(repositoryPath);
        string repositoryId = CreateRepositoryId(repository);
        RepositoryQueryState? state = await store.LoadQueryStateAsync(repositoryId, cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
        {
            throw new ProjectIndexException("project_not_indexed");
        }

        if (!state.RepositoryPath.Equals(repository, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectIndexException("project_index_corrupt");
        }

        return new QueryContext(repositoryId, repository, state.SnapshotId, state.Files, 0);
    }

    private GroundedProjectAnswer BuildAnswer(
        QueryContext context,
        IReadOnlyList<ProjectSearchRow> candidates,
        string description,
        bool includeInference = false)
    {
        int used = 0;
        bool truncated = false;
        List<ProjectClaim> claims = [];
        foreach (ProjectSearchRow row in candidates
            .DistinctBy(static row => $"{row.RelativePath}|{row.StartLine}|{row.Excerpt}", StringComparer.Ordinal)
            .OrderBy(static row => row.Rank))
        {
            VerifyEvidenceCurrent(context, row);
            string excerpt = CreateEvidenceExcerpt(row);
            string statement = row.SourceKind switch
            {
                "symbol" => $"{row.Symbol ?? row.SourceId} is declared in {row.RelativePath}.",
                "relationship" => NormalizeSingleLine(row.Excerpt),
                _ => NormalizeSingleLine(row.Excerpt),
            };
            int cost = statement.Length + excerpt.Length + row.RelativePath.Length + 256;
            if (used + cost > _options.MaximumContextCharacters)
            {
                truncated = true;
                continue;
            }

            used += cost;
            claims.Add(new ProjectClaim(
                ProjectKnowledgeClassification.ProjectFact,
                statement,
                [new ProjectEvidence(
                    row.RelativePath,
                    row.StartLine,
                    row.EndLine,
                    row.Symbol,
                    excerpt,
                    row.ContentHash)]));
        }

        if (includeInference && claims.Count > 0)
        {
            string inference = $"{description} suggests an architecture based on the evidence above; verify behavior at runtime before treating this inference as exhaustive.";
            if (used + inference.Length <= _options.MaximumContextCharacters)
            {
                used += inference.Length;
                claims.Add(new ProjectClaim(
                    ProjectKnowledgeClassification.Inference,
                    inference,
                    claims.SelectMany(static claim => claim.Evidence).Take(3).ToArray()));
            }
        }

        ProjectContextBudget budget = new(
            _options.MaximumContextCharacters,
            used,
            (used + 3) / 4,
            claims.Sum(static claim => claim.Evidence.Count),
            truncated);
        ProjectIntelligenceLog.RetrievalCompleted(
            _logger,
            context.RepositoryId,
            context.RetrievalMilliseconds,
            candidates.Count,
            budget.UsedCharacters,
            budget.EstimatedTokens,
            budget.Truncated);
        return new GroundedProjectAnswer(
            claims,
            new ProjectQueryMetrics(context.RetrievalMilliseconds, candidates.Count, budget),
            context.SnapshotId);
    }

    private string CreateEvidenceExcerpt(ProjectSearchRow row)
    {
        string excerpt = row.Excerpt;
        if (row.SourceKind == "file")
        {
            string[] lines = excerpt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            excerpt = string.Join('\n', lines.Take(12));
        }

        excerpt = excerpt.Replace('\0', '\uFFFD').Trim();
        return excerpt.Length <= _options.MaximumExcerptCharacters
            ? excerpt
            : excerpt[.._options.MaximumExcerptCharacters];
    }

    private static string NormalizeSingleLine(string value)
    {
        string normalized = string.Join(' ', value.Split(
            ['\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= 512 ? normalized : normalized[..512];
    }

    private static string CreateRepositoryId(string repositoryPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repositoryPath.ToUpperInvariant())))[..32];

    private static string CreateSnapshotId(IReadOnlyList<IndexedFile> files)
    {
        IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (IndexedFile file in files.OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.RelativePath.ToUpperInvariant()));
            hash.AppendData(Convert.FromHexString(file.ContentHash));
        }

        return Convert.ToHexString(hash.GetHashAndReset())[..32];
    }

    private void VerifyEvidenceCurrent(QueryContext context, ProjectSearchRow row)
    {
        if (!context.Files.TryGetValue(row.RelativePath, out StoredFileMetadata? file))
        {
            throw new ProjectIndexException("project_index_stale");
        }

        string candidatePath = Path.GetFullPath(Path.Combine(
            context.RepositoryPath,
            row.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.TrimEndingDirectorySeparator(context.RepositoryPath) +
            Path.DirectorySeparatorChar;
        if (!candidatePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectIndexException("project_index_corrupt");
        }

        try
        {
            string fullPath = pathPolicy.NormalizeExistingFile(candidatePath);
            FileInfo current = new(fullPath);
            current.Refresh();
            if (!current.Exists || current.Length != file.Length ||
                current.LastWriteTimeUtc.Ticks != file.LastWriteUtcTicks)
            {
                throw new ProjectIndexException("project_index_stale");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ToolValidationException)
        {
            throw new ProjectIndexException("project_index_stale");
        }
    }

    private sealed record QueryContext(
        string RepositoryId,
        string RepositoryPath,
        string SnapshotId,
        IReadOnlyDictionary<string, StoredFileMetadata> Files,
        long RetrievalMilliseconds);
}

internal static class ValueTaskExtensions
{
    public static async ValueTask AsValueTask(this ValueTask<ProjectIndexReport> task) =>
        await task.ConfigureAwait(false);
}
