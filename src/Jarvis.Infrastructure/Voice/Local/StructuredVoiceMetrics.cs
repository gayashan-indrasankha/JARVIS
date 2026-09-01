using Jarvis.Core.Voice;
using Microsoft.Extensions.Logging;

namespace Jarvis.Infrastructure.Voice.Local;

internal sealed class StructuredVoiceMetrics(ILogger<StructuredVoiceMetrics> logger) : IVoiceMetrics
{
    public void Record(VoiceMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        VoiceMetricsLog.Recorded(logger, metric.Kind, metric.Value);
    }
}

internal static partial class VoiceMetricsLog
{
    [LoggerMessage(
        EventId = 2500,
        Level = LogLevel.Information,
        Message = "Local voice metric {MetricKind}: {Value}")]
    public static partial void Recorded(
        ILogger logger,
        VoiceMetricKind metricKind,
        double value);
}
