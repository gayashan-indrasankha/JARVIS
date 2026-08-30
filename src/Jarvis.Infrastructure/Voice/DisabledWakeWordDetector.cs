using Jarvis.Core.Voice;

namespace Jarvis.Infrastructure.Voice;

internal sealed class DisabledWakeWordDetector : IWakeWordDetector
{
    public bool IsAvailable => false;

    public IAsyncEnumerable<WakeWordDetection> ListenAsync(
        CancellationToken cancellationToken) =>
        Empty(cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async IAsyncEnumerable<WakeWordDetection> Empty(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }
}
