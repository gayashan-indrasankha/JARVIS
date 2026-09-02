namespace Jarvis.Core.ProjectIntelligence;

public enum ProjectKnowledgeClassification
{
    ProjectFact,
    Inference,
    GeneralSoftwareEngineeringKnowledge,
}

public enum ProjectSymbolKind
{
    Namespace,
    Class,
    Record,
    Interface,
    Struct,
    Enum,
    Delegate,
    Constructor,
    Method,
    Property,
    Field,
    Event,
}

public enum ProjectRelationshipKind
{
    Inherits,
    Implements,
    Calls,
    References,
}

public sealed record ProjectEvidence(
    string RelativePath,
    int StartLine,
    int EndLine,
    string? Symbol,
    string Excerpt,
    string ContentHash);

public sealed record ProjectClaim(
    ProjectKnowledgeClassification Classification,
    string Statement,
    IReadOnlyList<ProjectEvidence> Evidence);

public sealed record ProjectContextBudget(
    int MaximumCharacters,
    int UsedCharacters,
    int EstimatedTokens,
    int EvidenceItems,
    bool Truncated);

public sealed record ProjectQueryMetrics(
    long RetrievalMilliseconds,
    int CandidateCount,
    ProjectContextBudget ContextBudget);

public sealed record GroundedProjectAnswer(
    IReadOnlyList<ProjectClaim> Claims,
    ProjectQueryMetrics Metrics,
    string SnapshotId);

public sealed record ProjectIndexReport(
    string RepositoryName,
    string SnapshotId,
    string? Branch,
    string GitStatusSummary,
    int ProjectCount,
    int SourceFileCount,
    int AddedFiles,
    int ChangedFiles,
    int RemovedFiles,
    int UnchangedFiles,
    int SymbolCount,
    int RelationshipCount,
    long ElapsedMilliseconds,
    bool Incremental);

public interface IProjectIntelligenceService
{
    public ValueTask<ProjectIndexReport> AnalyzeAsync(
        string repositoryPath,
        CancellationToken cancellationToken);

    public ValueTask<GroundedProjectAnswer> GetOverviewAsync(
        string repositoryPath,
        CancellationToken cancellationToken);

    public ValueTask<GroundedProjectAnswer> SearchAsync(
        string repositoryPath,
        string query,
        int maximumResults,
        CancellationToken cancellationToken);

    public ValueTask<GroundedProjectAnswer> FindSymbolAsync(
        string repositoryPath,
        string symbol,
        int maximumResults,
        CancellationToken cancellationToken);

    public ValueTask<GroundedProjectAnswer> ExplainSymbolAsync(
        string repositoryPath,
        string symbol,
        CancellationToken cancellationToken);

    public ValueTask<GroundedProjectAnswer> FindReferencesAsync(
        string repositoryPath,
        string symbol,
        int maximumResults,
        CancellationToken cancellationToken);

    public ValueTask<GroundedProjectAnswer> TraceDependencyAsync(
        string repositoryPath,
        string sourceSymbol,
        string? targetSymbol,
        int maximumDepth,
        CancellationToken cancellationToken);

    public ValueTask<GroundedProjectAnswer> TraceRequestFlowAsync(
        string repositoryPath,
        string endpoint,
        int maximumDepth,
        CancellationToken cancellationToken);

    public ValueTask<GroundedProjectAnswer> ListApiEndpointsAsync(
        string repositoryPath,
        int maximumResults,
        CancellationToken cancellationToken);

    public ValueTask<GroundedProjectAnswer> ListDependenciesAsync(
        string repositoryPath,
        CancellationToken cancellationToken);

    public ValueTask<GroundedProjectAnswer> ExplainArchitectureAsync(
        string repositoryPath,
        CancellationToken cancellationToken);
}
