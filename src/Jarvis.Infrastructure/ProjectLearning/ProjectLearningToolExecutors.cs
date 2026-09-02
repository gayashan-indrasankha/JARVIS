using Jarvis.Core.ProjectLearning;
using Jarvis.Core.Tools;
using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Tools;
using Microsoft.Data.Sqlite;

namespace Jarvis.Infrastructure.ProjectLearning;

internal sealed class ProjectLearningToolExecutors(Lazy<IProjectLearningService>? service = null) :
    IToolExecutor<StartTutorSessionRequest, ProjectLearningResponse>,
    IToolExecutor<ContinueTutorSessionRequest, ProjectLearningResponse>,
    IToolExecutor<StartInterviewSessionRequest, ProjectLearningResponse>,
    IToolExecutor<SubmitInterviewAnswerRequest, ProjectLearningResponse>,
    IToolExecutor<EndLearningSessionRequest, ProjectLearningResponse>,
    IToolExecutor<StartRevisionSessionRequest, ProjectLearningResponse>
{
    async ValueTask<ProjectLearningResponse> IToolExecutor<StartTutorSessionRequest, ProjectLearningResponse>.ExecuteAsync(
        StartTutorSessionRequest request,
        CancellationToken cancellationToken) => new(await RunAsync(
            token => Service.StartTutorAsync(
                request.RepositoryPath,
                request.Level,
                request.Topic,
                request.AskBeforeTell,
                request.Profile,
                token),
            cancellationToken).ConfigureAwait(false));

    async ValueTask<ProjectLearningResponse> IToolExecutor<ContinueTutorSessionRequest, ProjectLearningResponse>.ExecuteAsync(
        ContinueTutorSessionRequest request,
        CancellationToken cancellationToken) => new(await RunAsync(
            token => Service.ContinueTutorAsync(request.SessionId, request.Interaction, request.UserInput, token),
            cancellationToken).ConfigureAwait(false));

    async ValueTask<ProjectLearningResponse> IToolExecutor<StartInterviewSessionRequest, ProjectLearningResponse>.ExecuteAsync(
        StartInterviewSessionRequest request,
        CancellationToken cancellationToken) => new(await RunAsync(
            token => Service.StartInterviewAsync(
                request.RepositoryPath,
                request.Difficulty,
                request.QuestionCount,
                request.Profile,
                token),
            cancellationToken).ConfigureAwait(false));

    async ValueTask<ProjectLearningResponse> IToolExecutor<SubmitInterviewAnswerRequest, ProjectLearningResponse>.ExecuteAsync(
        SubmitInterviewAnswerRequest request,
        CancellationToken cancellationToken) => new(await RunAsync(
            token => Service.SubmitInterviewAnswerAsync(request.SessionId, request.Answer, token),
            cancellationToken).ConfigureAwait(false));

    async ValueTask<ProjectLearningResponse> IToolExecutor<EndLearningSessionRequest, ProjectLearningResponse>.ExecuteAsync(
        EndLearningSessionRequest request,
        CancellationToken cancellationToken) => new(await RunAsync(
            token => Service.EndSessionAsync(request.SessionId, token),
            cancellationToken).ConfigureAwait(false));

    async ValueTask<ProjectLearningResponse> IToolExecutor<StartRevisionSessionRequest, ProjectLearningResponse>.ExecuteAsync(
        StartRevisionSessionRequest request,
        CancellationToken cancellationToken) => new(await RunAsync(
            token => Service.StartRevisionAsync(request.RepositoryPath, request.Profile, token),
            cancellationToken).ConfigureAwait(false));

    private static async ValueTask<T> RunAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (ProjectLearningException exception)
        {
            throw new ToolValidationException(exception.Code);
        }
        catch (LocalComponentUnavailableException exception)
        {
            throw new ToolValidationException(exception.Code);
        }
        catch (SqliteException)
        {
            throw new ToolValidationException("learning_storage_failed");
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and not OutOfMemoryException and
                not StackOverflowException and not AccessViolationException and
                not System.Runtime.InteropServices.SEHException)
        {
            throw new ToolValidationException("project_learning_failed");
        }
    }

    private IProjectLearningService Service => service?.Value ??
        throw new ToolValidationException("project_learning_unavailable");
}
