using Microsoft.Extensions.Logging;

internal static partial class HostLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Starting JARVIS local voice host {InstanceName} in {EnvironmentName}")]
    public static partial void Starting(
        ILogger logger,
        string instanceName,
        string environmentName);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Voice command failed with {ErrorType}")]
    public static partial void VoiceCommandFailed(ILogger logger, string errorType);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "JARVIS local voice host stopped cleanly")]
    public static partial void Stopped(ILogger logger);
}
