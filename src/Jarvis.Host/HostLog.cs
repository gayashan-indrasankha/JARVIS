using Microsoft.Extensions.Logging;

internal static partial class HostLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Starting JARVIS realtime voice host {InstanceName} in {EnvironmentName}")]
    public static partial void Starting(
        ILogger logger,
        string instanceName,
        string environmentName);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Voice command failed with {ErrorType}")]
    public static partial void VoiceCommandFailed(ILogger logger, string errorType);
}
