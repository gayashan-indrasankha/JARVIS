namespace Jarvis.Core.Voice;

/// <summary>
/// Replaceable local wake-word boundary. Implementations must not persist dormant audio.
/// </summary>
public interface IWakeWordDetector : IAsyncDisposable
{
    public bool IsAvailable { get; }

    public IAsyncEnumerable<WakeWordDetection> ListenAsync(CancellationToken cancellationToken);
}

public sealed record WakeWordDetection
{
    public WakeWordDetection(
        DateTimeOffset detectedAt,
        TimeSpan processingLatency,
        double? confidence = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(processingLatency, TimeSpan.Zero);

        if (confidence.HasValue)
        {
            if (!double.IsFinite(confidence.Value))
            {
                throw new ArgumentOutOfRangeException(nameof(confidence));
            }

            ArgumentOutOfRangeException.ThrowIfLessThan(confidence.Value, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(confidence.Value, 1);
        }

        DetectedAt = detectedAt;
        ProcessingLatency = processingLatency;
        Confidence = confidence;
    }

    public DateTimeOffset DetectedAt { get; }

    public TimeSpan ProcessingLatency { get; }

    /// <summary>
    /// Optional provider-reported confidence. Null means that the detector exposes only a hit.
    /// </summary>
    public double? Confidence { get; }
}
