using System.Text.Json;
using Jarvis.Core.Tools;

namespace Jarvis.Infrastructure.Tools;

internal sealed class ToolDispatcher(
    ToolRegistry registry,
    IToolAuthorizationPolicy authorizationPolicy,
    IToolAuditSink auditSink,
    TimeProvider timeProvider) : IToolDispatcher
{
    public async ValueTask<ToolExecutionOutcome> ExecuteAsync(
        ToolCallProposal proposal,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);
        Guid invocationId = Guid.NewGuid();
        DateTimeOffset startedAt = timeProvider.GetUtcNow();
        ToolAuthorizationDecision decision = ToolAuthorizationDecision.NotEvaluated;

        if (!registry.TryGet(proposal.Name, out IRegisteredTool? registration) || registration is null)
        {
            return await CompleteAsync(
                ToolExecutionStatus.InvalidRequest,
                decision,
                "Unknown tool; no execution occurred.",
                "unknown_tool",
                fingerprint: null,
                truncated: false).ConfigureAwait(false);
        }

        object request;
        string fingerprint;
        try
        {
            request = registration.ValidateAndNormalize(proposal.ArgumentsJson);
            fingerprint = registration.CreateFingerprint(request);
        }
        catch (ToolValidationException exception)
        {
            return await CompleteAsync(
                ToolExecutionStatus.InvalidRequest,
                decision,
                "Tool arguments failed validation; no execution occurred.",
                exception.Code,
                fingerprint: null,
                truncated: false).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or JsonException)
        {
            return await CompleteAsync(
                ToolExecutionStatus.InvalidRequest,
                decision,
                "Tool arguments failed validation; no execution occurred.",
                "invalid_arguments",
                fingerprint: null,
                truncated: false).ConfigureAwait(false);
        }

        if (context.PreviousFingerprints.Contains(fingerprint))
        {
            return await CompleteAsync(
                ToolExecutionStatus.RepeatedCall,
                decision,
                "An identical tool call was already processed for this user request; no duplicate execution occurred.",
                "repeated_identical_call",
                fingerprint,
                truncated: false).ConfigureAwait(false);
        }

        ToolAuthorizationResult authorization;
        try
        {
            authorization = await authorizationPolicy.AuthorizeAsync(
                new ToolAuthorizationRequest(
                    invocationId,
                    context.UserRequestId,
                    registration.Definition.Name,
                    registration.Definition.AuthorizationCategory,
                    fingerprint),
                cancellationToken).ConfigureAwait(false);
            decision = authorization.Decision;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CompleteAsync(
                ToolExecutionStatus.Cancelled,
                decision,
                "Tool authorization was cancelled; no execution occurred.",
                "cancelled",
                fingerprint,
                truncated: false).ConfigureAwait(false);
        }

        if (decision != ToolAuthorizationDecision.Allowed)
        {
            ToolExecutionStatus status = decision is
                ToolAuthorizationDecision.ConfirmationRequired or
                ToolAuthorizationDecision.StrongConfirmationRequired
                ? ToolExecutionStatus.ConfirmationRequired
                : ToolExecutionStatus.Denied;
            return await CompleteAsync(
                status,
                decision,
                "Authorization did not permit execution.",
                authorization.ReasonCode,
                fingerprint,
                truncated: false).ConfigureAwait(false);
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        deadline.CancelAfter(registration.Definition.Timeout);
        try
        {
            object response = await registration.ExecuteAsync(request, deadline.Token)
                .ConfigureAwait(false);
            string observation = JsonSerializer.Serialize(
                response,
                response.GetType(),
                ToolJson.Options);
            observation = SanitizeObservation(observation);
            bool truncated = observation.Length > registration.Definition.MaximumResultCharacters;
            if (truncated)
            {
                observation = observation[..registration.Definition.MaximumResultCharacters];
            }

            return await CompleteAsync(
                ToolExecutionStatus.Success,
                decision,
                observation,
                "none",
                fingerprint,
                truncated).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CompleteAsync(
                ToolExecutionStatus.Cancelled,
                decision,
                "Tool execution was cancelled.",
                "cancelled",
                fingerprint,
                truncated: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return await CompleteAsync(
                ToolExecutionStatus.TimedOut,
                decision,
                "Tool execution exceeded its deadline.",
                "timeout",
                fingerprint,
                truncated: false).ConfigureAwait(false);
        }
        catch (ToolValidationException exception)
        {
            return await CompleteAsync(
                ToolExecutionStatus.Failed,
                decision,
                "The validated tool could not complete safely.",
                exception.Code,
                fingerprint,
                truncated: false).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return await CompleteAsync(
                ToolExecutionStatus.Failed,
                decision,
                "The tool failed without a confirmed effect.",
                "execution_failed",
                fingerprint,
                truncated: false).ConfigureAwait(false);
        }

        async ValueTask<ToolExecutionOutcome> CompleteAsync(
            ToolExecutionStatus status,
            ToolAuthorizationDecision authorizationDecision,
            string observation,
            string errorCategory,
            string? fingerprint,
            bool truncated)
        {
            ToolExecutionOutcome outcome = new(
                invocationId,
                context.UserRequestId,
                proposal.Name,
                status,
                authorizationDecision,
                observation,
                errorCategory,
                fingerprint,
                truncated);
            DateTimeOffset endedAt = timeProvider.GetUtcNow();
            await auditSink.RecordAsync(
                new ToolAuditEvent(
                    invocationId,
                    context.UserRequestId,
                    proposal.Name,
                    authorizationDecision,
                    startedAt,
                    endedAt,
                    status,
                    outcome.Succeeded,
                    errorCategory,
                    status == ToolExecutionStatus.TimedOut,
                    status == ToolExecutionStatus.Cancelled,
                    truncated),
                CancellationToken.None).ConfigureAwait(false);
            return outcome;
        }
    }

    private static string SanitizeObservation(string observation)
    {
        string withoutEscapedEscape = observation.Replace("\\u001B", string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.Create(
            withoutEscapedEscape.Length,
            withoutEscapedEscape,
            static (destination, source) =>
            {
                for (int index = 0; index < source.Length; index++)
                {
                    char character = source[index];
                    destination[index] = char.IsControl(character) ? '\uFFFD' : character;
                }
            });
    }
}
