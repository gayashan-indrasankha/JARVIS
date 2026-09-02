using Jarvis.Core.ProjectIntelligence;

namespace Jarvis.Core.Tools;

public sealed record AnalyzeProjectRequest(string RepositoryPath) : IToolRequest;

public sealed record AnalyzeProjectResponse(ProjectIndexReport Report) : IToolResponse;

public sealed record GetProjectOverviewRequest(string RepositoryPath) : IToolRequest;

public sealed record SearchProjectRequest(
    string RepositoryPath,
    string Query,
    int MaximumResults = 10) : IToolRequest;

public sealed record FindSymbolRequest(
    string RepositoryPath,
    string Symbol,
    int MaximumResults = 10) : IToolRequest;

public sealed record ExplainSymbolRequest(
    string RepositoryPath,
    string Symbol) : IToolRequest;

public sealed record FindReferencesRequest(
    string RepositoryPath,
    string Symbol,
    int MaximumResults = 20) : IToolRequest;

public sealed record TraceDependencyRequest(
    string RepositoryPath,
    string SourceSymbol,
    string? TargetSymbol = null,
    int MaximumDepth = 4) : IToolRequest;

public sealed record TraceRequestFlowRequest(
    string RepositoryPath,
    string Endpoint,
    int MaximumDepth = 6) : IToolRequest;

public sealed record ListApiEndpointsRequest(
    string RepositoryPath,
    int MaximumResults = 100) : IToolRequest;

public sealed record ListProjectDependenciesRequest(string RepositoryPath) : IToolRequest;

public sealed record ExplainArchitectureRequest(string RepositoryPath) : IToolRequest;

public sealed record ProjectAnswerResponse(GroundedProjectAnswer Answer) : IToolResponse;
