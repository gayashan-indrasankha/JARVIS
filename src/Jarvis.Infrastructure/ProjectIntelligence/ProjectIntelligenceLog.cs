using Microsoft.Extensions.Logging;

namespace Jarvis.Infrastructure.ProjectIntelligence;

internal static partial class ProjectIntelligenceLog
{
    [LoggerMessage(
        EventId = 3301,
        Level = LogLevel.Information,
        Message = "Project index completed. RepositoryId={RepositoryId} Incremental={Incremental} ElapsedMs={ElapsedMs} Files={Files} Changed={Changed} Symbols={Symbols} Relationships={Relationships}")]
    public static partial void IndexCompleted(
        ILogger logger,
        string repositoryId,
        bool incremental,
        long elapsedMs,
        int files,
        int changed,
        int symbols,
        int relationships);

    [LoggerMessage(
        EventId = 3302,
        Level = LogLevel.Information,
        Message = "Project retrieval completed. RepositoryId={RepositoryId} ElapsedMs={ElapsedMs} Candidates={Candidates} ContextCharacters={ContextCharacters} EstimatedTokens={EstimatedTokens} Truncated={Truncated}")]
    public static partial void RetrievalCompleted(
        ILogger logger,
        string repositoryId,
        long elapsedMs,
        int candidates,
        int contextCharacters,
        int estimatedTokens,
        bool truncated);

    [LoggerMessage(
        EventId = 3304,
        Level = LogLevel.Warning,
        Message = "Project index watcher requires a full refresh. RepositoryId={RepositoryId}")]
    public static partial void WatcherOverflow(ILogger logger, string repositoryId);

    [LoggerMessage(
        EventId = 3305,
        Level = LogLevel.Warning,
        Message = "Project index refresh failed. RepositoryId={RepositoryId} ErrorCategory={ErrorCategory}")]
    public static partial void RefreshFailed(
        ILogger logger,
        string repositoryId,
        string errorCategory);
}
