using Jarvis.Core.ProjectIntelligence;
using Jarvis.Core.Tools;
using Jarvis.Infrastructure.Tools;
using Microsoft.Data.Sqlite;

namespace Jarvis.Infrastructure.ProjectIntelligence;

internal sealed class ProjectToolExecutors(Lazy<IProjectIntelligenceService>? service = null) :
    IToolExecutor<AnalyzeProjectRequest, AnalyzeProjectResponse>,
    IToolExecutor<GetProjectOverviewRequest, ProjectAnswerResponse>,
    IToolExecutor<SearchProjectRequest, ProjectAnswerResponse>,
    IToolExecutor<FindSymbolRequest, ProjectAnswerResponse>,
    IToolExecutor<ExplainSymbolRequest, ProjectAnswerResponse>,
    IToolExecutor<FindReferencesRequest, ProjectAnswerResponse>,
    IToolExecutor<TraceDependencyRequest, ProjectAnswerResponse>,
    IToolExecutor<TraceRequestFlowRequest, ProjectAnswerResponse>,
    IToolExecutor<ListApiEndpointsRequest, ProjectAnswerResponse>,
    IToolExecutor<ListProjectDependenciesRequest, ProjectAnswerResponse>,
    IToolExecutor<ExplainArchitectureRequest, ProjectAnswerResponse>
{
    async ValueTask<AnalyzeProjectResponse> IToolExecutor<AnalyzeProjectRequest, AnalyzeProjectResponse>.ExecuteAsync(
        AnalyzeProjectRequest request,
        CancellationToken cancellationToken) => new(await RunAsync(
            token => Service.AnalyzeAsync(request.RepositoryPath, token),
            cancellationToken).ConfigureAwait(false));

    async ValueTask<ProjectAnswerResponse> IToolExecutor<GetProjectOverviewRequest, ProjectAnswerResponse>.ExecuteAsync(
        GetProjectOverviewRequest request,
        CancellationToken cancellationToken) => new(await RunAsync(
            token => Service.GetOverviewAsync(request.RepositoryPath, token),
            cancellationToken).ConfigureAwait(false));

    async ValueTask<ProjectAnswerResponse> IToolExecutor<SearchProjectRequest, ProjectAnswerResponse>.ExecuteAsync(
        SearchProjectRequest request,
        CancellationToken cancellationToken) => new(await RunAsync(
            token => Service.SearchAsync(request.RepositoryPath, request.Query, request.MaximumResults, token),
            cancellationToken).ConfigureAwait(false));

    async ValueTask<ProjectAnswerResponse> IToolExecutor<FindSymbolRequest, ProjectAnswerResponse>.ExecuteAsync(
        FindSymbolRequest request,
        CancellationToken cancellationToken) => new(await RunAsync(
            token => Service.FindSymbolAsync(request.RepositoryPath, request.Symbol, request.MaximumResults, token),
            cancellationToken).ConfigureAwait(false));

    async ValueTask<ProjectAnswerResponse> IToolExecutor<ExplainSymbolRequest, ProjectAnswerResponse>.ExecuteAsync(
        ExplainSymbolRequest request,
        CancellationToken cancellationToken) => new(await RunAsync(
            token => Service.ExplainSymbolAsync(request.RepositoryPath, request.Symbol, token),
            cancellationToken).ConfigureAwait(false));

    async ValueTask<ProjectAnswerResponse> IToolExecutor<FindReferencesRequest, ProjectAnswerResponse>.ExecuteAsync(
        FindReferencesRequest request,
        CancellationToken cancellationToken) => new(await RunAsync(
            token => Service.FindReferencesAsync(request.RepositoryPath, request.Symbol, request.MaximumResults, token),
            cancellationToken).ConfigureAwait(false));

    async ValueTask<ProjectAnswerResponse> IToolExecutor<TraceDependencyRequest, ProjectAnswerResponse>.ExecuteAsync(
        TraceDependencyRequest request,
        CancellationToken cancellationToken) => new(await RunAsync(
            token => Service.TraceDependencyAsync(
                request.RepositoryPath,
                request.SourceSymbol,
                request.TargetSymbol,
                request.MaximumDepth,
                token),
            cancellationToken).ConfigureAwait(false));

    async ValueTask<ProjectAnswerResponse> IToolExecutor<TraceRequestFlowRequest, ProjectAnswerResponse>.ExecuteAsync(
        TraceRequestFlowRequest request,
        CancellationToken cancellationToken) => new(await RunAsync(
            token => Service.TraceRequestFlowAsync(
                request.RepositoryPath,
                request.Endpoint,
                request.MaximumDepth,
                token),
            cancellationToken).ConfigureAwait(false));

    async ValueTask<ProjectAnswerResponse> IToolExecutor<ListApiEndpointsRequest, ProjectAnswerResponse>.ExecuteAsync(
        ListApiEndpointsRequest request,
        CancellationToken cancellationToken) => new(await RunAsync(
            token => Service.ListApiEndpointsAsync(request.RepositoryPath, request.MaximumResults, token),
            cancellationToken).ConfigureAwait(false));

    async ValueTask<ProjectAnswerResponse> IToolExecutor<ListProjectDependenciesRequest, ProjectAnswerResponse>.ExecuteAsync(
        ListProjectDependenciesRequest request,
        CancellationToken cancellationToken) => new(await RunAsync(
            token => Service.ListDependenciesAsync(request.RepositoryPath, token),
            cancellationToken).ConfigureAwait(false));

    async ValueTask<ProjectAnswerResponse> IToolExecutor<ExplainArchitectureRequest, ProjectAnswerResponse>.ExecuteAsync(
        ExplainArchitectureRequest request,
        CancellationToken cancellationToken) => new(await RunAsync(
            token => Service.ExplainArchitectureAsync(request.RepositoryPath, token),
            cancellationToken).ConfigureAwait(false));

    private static async ValueTask<T> RunAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (ProjectIndexException exception)
        {
            throw new ToolValidationException(exception.Code);
        }
        catch (SqliteException)
        {
            throw new ToolValidationException("project_index_storage_failed");
        }
    }

    private IProjectIntelligenceService Service => service?.Value ??
        throw new ToolValidationException("project_intelligence_unavailable");
}
