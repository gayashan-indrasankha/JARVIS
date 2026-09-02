using Jarvis.Core.ProjectIntelligence;

namespace Jarvis.Infrastructure.ProjectIntelligence;

internal enum IndexedFileKind
{
    Source,
    Project,
    Solution,
    Documentation,
    Configuration,
}

internal sealed record DiscoveredFile(
    string FullPath,
    string RelativePath,
    IndexedFileKind Kind,
    long Length,
    long LastWriteUtcTicks);

internal sealed record IndexedFile(
    string RelativePath,
    IndexedFileKind Kind,
    long Length,
    long LastWriteUtcTicks,
    string ContentHash,
    string Content);

internal sealed record StaticProject(
    string RelativePath,
    string Name,
    string AssemblyName,
    string RootNamespace,
    IReadOnlyList<string> TargetFrameworks,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<PackageReferenceInfo> PackageReferences,
    bool IsTestProject,
    string OutputType);

internal sealed record PackageReferenceInfo(string Name, string? Version);

internal sealed record IndexedSymbol(
    string Id,
    string RelativePath,
    string? ProjectPath,
    ProjectSymbolKind Kind,
    string Name,
    string QualifiedName,
    string Namespace,
    int StartLine,
    int EndLine,
    string Declaration,
    string ContentHash);

internal sealed record IndexedRelationship(
    string? SourceSymbolId,
    string? TargetSymbolId,
    string SourceName,
    string TargetName,
    ProjectRelationshipKind Kind,
    string RelativePath,
    int StartLine,
    int EndLine,
    string ContentHash);

internal sealed record IndexedFact(
    string Kind,
    string Name,
    string Detail,
    string RelativePath,
    int StartLine,
    int EndLine,
    string? Symbol,
    string Excerpt,
    string ContentHash);

internal sealed record ProjectAnalysisSnapshot(
    IReadOnlyList<StaticProject> Projects,
    IReadOnlyList<IndexedSymbol> Symbols,
    IReadOnlyList<IndexedRelationship> Relationships,
    IReadOnlyList<IndexedFact> Facts);

internal sealed record StoredFileState(
    string RelativePath,
    IndexedFileKind Kind,
    long Length,
    long LastWriteUtcTicks,
    string ContentHash,
    string Content);

internal sealed record RepositoryIndexState(
    string RepositoryId,
    string RepositoryPath,
    string SnapshotId,
    string? Branch,
    string? GitStatus,
    DateTimeOffset IndexedAtUtc,
    IReadOnlyDictionary<string, StoredFileState> Files);

internal sealed record StoredFileMetadata(
    string RelativePath,
    long Length,
    long LastWriteUtcTicks,
    string ContentHash);

internal sealed record RepositoryQueryState(
    string RepositoryPath,
    string SnapshotId,
    IReadOnlyDictionary<string, StoredFileMetadata> Files);

internal sealed record GitRepositoryMetadata(string? Branch, string StatusSummary);

internal sealed record ProjectSearchRow(
    string SourceKind,
    string SourceId,
    string RelativePath,
    string? Symbol,
    int StartLine,
    int EndLine,
    string Excerpt,
    string ContentHash,
    double Rank);
