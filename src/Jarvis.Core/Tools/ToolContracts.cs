namespace Jarvis.Core.Tools;

public static class ToolDataLimits
{
    public const int MaximumToolNameCharacters = 64;
    public const int MaximumArgumentsCharacters = 16 * 1024;
    public const int MaximumSchemaCharacters = 32 * 1024;
    public const int MaximumObservationCharacters = 32 * 1024;
    public const int MaximumPathCharacters = 2_048;
    public const int MaximumResultItems = 256;
}

public enum ToolAuthorizationCategory
{
    SafeRead,
    SafeLocalAction,
    ConfirmRequired,
    StrongConfirmRequired,
    Denied,
}

public enum ToolAuthorizationDecision
{
    NotEvaluated,
    Allowed,
    ConfirmationRequired,
    StrongConfirmationRequired,
    Denied,
}

public enum ToolExecutionStatus
{
    Success,
    InvalidRequest,
    Denied,
    ConfirmationRequired,
    RepeatedCall,
    Cancelled,
    TimedOut,
    Unavailable,
    Failed,
}

public sealed record ToolDefinition
{
    public ToolDefinition(
        string name,
        string description,
        string argumentsJsonSchema,
        ToolAuthorizationCategory authorizationCategory,
        TimeSpan timeout,
        int maximumResultCharacters)
    {
        ValidateIdentifier(name, nameof(name));
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentsJsonSchema);
        if (description.Length > 512 || description.Any(char.IsControl))
        {
            throw new ArgumentException("The tool description is invalid.", nameof(description));
        }

        if (argumentsJsonSchema.Length > ToolDataLimits.MaximumSchemaCharacters ||
            argumentsJsonSchema.Any(static character => character == '\0'))
        {
            throw new ArgumentException("The tool schema is invalid or too large.", nameof(argumentsJsonSchema));
        }

        if (!Enum.IsDefined(authorizationCategory))
        {
            throw new ArgumentOutOfRangeException(nameof(authorizationCategory));
        }

        if (timeout < TimeSpan.FromMilliseconds(100) || timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResultCharacters, 256);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumResultCharacters,
            ToolDataLimits.MaximumObservationCharacters);

        Name = name;
        Description = description;
        ArgumentsJsonSchema = argumentsJsonSchema;
        AuthorizationCategory = authorizationCategory;
        Timeout = timeout;
        MaximumResultCharacters = maximumResultCharacters;
    }

    public string Name { get; }

    public string Description { get; }

    public string ArgumentsJsonSchema { get; }

    public ToolAuthorizationCategory AuthorizationCategory { get; }

    public TimeSpan Timeout { get; }

    public int MaximumResultCharacters { get; }

    internal static void ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > ToolDataLimits.MaximumToolNameCharacters ||
            !char.IsAsciiLetter(value[0]) ||
            value.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException("The tool identifier is invalid.", parameterName);
        }
    }
}

public sealed record ToolCallProposal
{
    public ToolCallProposal(string name, string argumentsJson)
    {
        ToolDefinition.ValidateIdentifier(name, nameof(name));
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentsJson);
        if (argumentsJson.Length > ToolDataLimits.MaximumArgumentsCharacters ||
            argumentsJson.Any(static character => character == '\0'))
        {
            throw new ArgumentException("Tool arguments are invalid or too large.", nameof(argumentsJson));
        }

        Name = name;
        ArgumentsJson = argumentsJson;
    }

    public string Name { get; }

    public string ArgumentsJson { get; }
}

public sealed record ToolInvocationContext
{
    public ToolInvocationContext(
        Guid userRequestId,
        IReadOnlySet<string>? previousFingerprints = null)
    {
        if (userRequestId == Guid.Empty)
        {
            throw new ArgumentException("The user request identifier is required.", nameof(userRequestId));
        }

        UserRequestId = userRequestId;
        PreviousFingerprints = previousFingerprints ?? new HashSet<string>(StringComparer.Ordinal);
    }

    public Guid UserRequestId { get; }

    public IReadOnlySet<string> PreviousFingerprints { get; }
}

public sealed record ToolExecutionOutcome
{
    public ToolExecutionOutcome(
        Guid invocationId,
        Guid userRequestId,
        string toolName,
        ToolExecutionStatus status,
        ToolAuthorizationDecision authorizationDecision,
        string observation,
        string errorCategory,
        string? canonicalFingerprint = null,
        bool truncated = false)
    {
        if (invocationId == Guid.Empty || userRequestId == Guid.Empty)
        {
            throw new ArgumentException("Invocation and user request identifiers are required.");
        }

        ToolDefinition.ValidateIdentifier(toolName, nameof(toolName));
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (!Enum.IsDefined(authorizationDecision))
        {
            throw new ArgumentOutOfRangeException(nameof(authorizationDecision));
        }

        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCategory);
        if (observation.Length > ToolDataLimits.MaximumObservationCharacters ||
            errorCategory.Length > 64 ||
            errorCategory.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new ArgumentException("The tool outcome contains invalid bounded data.");
        }

        InvocationId = invocationId;
        UserRequestId = userRequestId;
        ToolName = toolName;
        Status = status;
        AuthorizationDecision = authorizationDecision;
        Observation = observation;
        ErrorCategory = errorCategory;
        CanonicalFingerprint = canonicalFingerprint;
        Truncated = truncated;
    }

    public Guid InvocationId { get; }

    public Guid UserRequestId { get; }

    public string ToolName { get; }

    public ToolExecutionStatus Status { get; }

    public ToolAuthorizationDecision AuthorizationDecision { get; }

    public string Observation { get; }

    public string ErrorCategory { get; }

    public string? CanonicalFingerprint { get; }

    public bool Truncated { get; }

    public bool Succeeded => Status == ToolExecutionStatus.Success;
}

public sealed record ToolAuthorizationRequest(
    Guid InvocationId,
    Guid UserRequestId,
    string ToolName,
    ToolAuthorizationCategory Category,
    string CanonicalFingerprint);

public sealed record ToolAuthorizationResult(
    ToolAuthorizationDecision Decision,
    string ReasonCode);

public sealed record ToolAuditEvent(
    Guid InvocationId,
    Guid UserRequestId,
    string ToolName,
    ToolAuthorizationDecision AuthorizationDecision,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    ToolExecutionStatus Status,
    bool Succeeded,
    string ErrorCategory,
    bool TimedOut,
    bool Cancelled,
    bool ResultTruncated);

public interface IToolCatalog
{
    public IReadOnlyList<ToolDefinition> Definitions { get; }

    public IReadOnlyList<string> ApprovedRoots { get; }
}

public interface IToolDispatcher
{
    public ValueTask<ToolExecutionOutcome> ExecuteAsync(
        ToolCallProposal proposal,
        ToolInvocationContext context,
        CancellationToken cancellationToken);
}

public interface IToolAuthorizationPolicy
{
    public ValueTask<ToolAuthorizationResult> AuthorizeAsync(
        ToolAuthorizationRequest request,
        CancellationToken cancellationToken);
}

public interface IToolAuditSink
{
    public ValueTask RecordAsync(ToolAuditEvent auditEvent, CancellationToken cancellationToken);
}

public interface IToolRequest;

public interface IToolResponse;

public interface IToolExecutor<in TRequest, TResponse>
    where TRequest : IToolRequest
    where TResponse : IToolResponse
{
    public ValueTask<TResponse> ExecuteAsync(
        TRequest request,
        CancellationToken cancellationToken);
}
