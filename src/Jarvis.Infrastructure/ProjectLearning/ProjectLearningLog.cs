using Microsoft.Extensions.Logging;

namespace Jarvis.Infrastructure.ProjectLearning;

internal static partial class ProjectLearningLog
{
    [LoggerMessage(
        EventId = 3400,
        Level = LogLevel.Information,
        Message = "Project learning model profile selected: {Profile}")]
    public static partial void ProfileSelected(ILogger logger, string profile);

    [LoggerMessage(
        EventId = 3401,
        Level = LogLevel.Warning,
        Message = "Project learning model profile fell back to FAST: {ReasonCode}")]
    public static partial void ProfileFallback(ILogger logger, string reasonCode);
}
