using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Jarvis.Infrastructure.Tools;

internal sealed class StructuredToolAuditSink(ILogger<StructuredToolAuditSink> logger) :
    IToolAuditSink
{
    public ValueTask RecordAsync(ToolAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ToolAuditLog.Terminal(
            logger,
            auditEvent.InvocationId,
            auditEvent.UserRequestId,
            auditEvent.ToolName,
            auditEvent.AuthorizationDecision,
            auditEvent.StartedAt,
            auditEvent.EndedAt,
            auditEvent.Status,
            auditEvent.Succeeded,
            auditEvent.ErrorCategory,
            auditEvent.TimedOut,
            auditEvent.Cancelled,
            auditEvent.ResultTruncated);
        return ValueTask.CompletedTask;
    }
}

internal static partial class ToolAuditLog
{
    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Information,
        Message = "Tool audit invocation {InvocationId} request {UserRequestId} tool {ToolName} authorization {AuthorizationDecision} start {StartedAt} end {EndedAt} status {Status} success {Succeeded} error {ErrorCategory} timeout {TimedOut} cancelled {Cancelled} truncated {ResultTruncated}")]
    public static partial void Terminal(
        ILogger logger,
        Guid invocationId,
        Guid userRequestId,
        string toolName,
        ToolAuthorizationDecision authorizationDecision,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        ToolExecutionStatus status,
        bool succeeded,
        string errorCategory,
        bool timedOut,
        bool cancelled,
        bool resultTruncated);
}
