namespace Jarvis.Core.Voice;

/// <summary>
/// Replaceable local wake-word boundary. Implementations must not persist dormant audio.
/// </summary>
public interface IWakeWordDetector : IAsyncDisposable
{
    public bool IsAvailable { get; }

    public IAsyncEnumerable<WakeWordDetection> ListenAsync(CancellationToken cancellationToken);
}

public sealed record WakeWordDetection(
    DateTimeOffset DetectedAt,
    double Confidence);
