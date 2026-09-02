using Jarvis.Core.ProjectIntelligence;
using Jarvis.Core.ProjectLearning;

namespace Jarvis.Infrastructure.ProjectLearning;

internal sealed class ProjectLearningEvidenceSource(IProjectIntelligenceService intelligence) :
    IProjectLearningEvidenceSource
{
    public ValueTask<GroundedProjectAnswer> GetTutorEvidenceAsync(
        string repositoryPath,
        TutorLevel level,
        string topic,
        CancellationToken cancellationToken) => level switch
        {
            TutorLevel.Foundation => intelligence.GetOverviewAsync(repositoryPath, cancellationToken),
            TutorLevel.Architecture or TutorLevel.TradeOffs or TutorLevel.Scalability =>
                intelligence.ExplainArchitectureAsync(repositoryPath, cancellationToken),
            TutorLevel.Database => SearchAsync(repositoryPath, "DbContext database entity provider " + topic, cancellationToken),
            TutorLevel.Security => SearchAsync(repositoryPath, "authentication authorization security policy " + topic, cancellationToken),
            TutorLevel.Testing => SearchAsync(repositoryPath, "test xunit integration unit " + topic, cancellationToken),
            TutorLevel.FailureHandling => SearchAsync(repositoryPath, "exception cancellation timeout error " + topic, cancellationToken),
            _ => SearchAsync(repositoryPath, topic, cancellationToken),
        };

    public ValueTask<GroundedProjectAnswer> GetInterviewEvidenceAsync(
        string repositoryPath,
        InterviewDimension dimension,
        CancellationToken cancellationToken) => dimension switch
        {
            InterviewDimension.ProjectOverview => intelligence.GetOverviewAsync(repositoryPath, cancellationToken),
            InterviewDimension.Architecture or InterviewDimension.DesignTradeOffs or
                InterviewDimension.Scalability =>
                intelligence.ExplainArchitectureAsync(repositoryPath, cancellationToken),
            InterviewDimension.ApiDesign => SearchAsync(repositoryPath, "controller endpoint API route", cancellationToken),
            InterviewDimension.Database => SearchAsync(repositoryPath, "DbContext entity database provider", cancellationToken),
            InterviewDimension.Security => SearchAsync(repositoryPath, "authentication authorization security policy", cancellationToken),
            InterviewDimension.Testing => SearchAsync(repositoryPath, "test xunit integration unit", cancellationToken),
            InterviewDimension.ErrorHandling or InterviewDimension.FailureScenarios =>
                SearchAsync(repositoryPath, "exception cancellation timeout retry error", cancellationToken),
            InterviewDimension.Concurrency => SearchAsync(repositoryPath, "async await lock semaphore concurrency", cancellationToken),
            InterviewDimension.Performance => SearchAsync(repositoryPath, "cache allocation performance latency", cancellationToken),
            InterviewDimension.CSharpDotNet => SearchAsync(repositoryPath, "C# .NET interface record async dependency injection", cancellationToken),
            _ => SearchAsync(repositoryPath, "implementation service dependency flow", cancellationToken),
        };

    private ValueTask<GroundedProjectAnswer> SearchAsync(
        string repositoryPath,
        string query,
        CancellationToken cancellationToken) =>
        intelligence.SearchAsync(repositoryPath, query, ProjectLearningLimits.MaximumEvidenceItems, cancellationToken);
}
