namespace Jarvis.Core.Voice;

/// <summary>
/// Supplies microphone frames in the Core-owned session format.
/// </summary>
public interface IAudioCapture : IAsyncDisposable
{
    public AudioFormat Format { get; }

    public IAsyncEnumerable<AudioFrame> CaptureAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Plays generation-tagged assistant audio and can discard buffered output immediately.
/// </summary>
public interface IAudioPlayback : IAsyncDisposable
{
    public AudioFormat Format { get; }

    public ValueTask EnqueueAsync(AssistantAudioChunk chunk, CancellationToken cancellationToken);

    public ValueTask InterruptAsync(
        long invalidThroughGenerationId,
        CancellationToken cancellationToken);

    public ValueTask StopAsync(CancellationToken cancellationToken);
}

public sealed record AssistantAudioChunk
{
    public AssistantAudioChunk(byte[] data, long generationId)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationId);

        if (data.Length == 0 || data.Length > VoiceDataLimits.MaximumAudioChunkBytes)
        {
            throw new ArgumentException("Assistant audio has an invalid size.", nameof(data));
        }

        Data = data;
        GenerationId = generationId;
    }

    public byte[] Data { get; }

    public long GenerationId { get; }
}

public static class VoiceDataLimits
{
    public const int MaximumAudioChunkBytes = 256 * 1024;

    public const int MaximumTextCharacters = 32 * 1024;

    public const int MaximumInstructionsCharacters = 16 * 1024;

    public const int MaximumConversationMessages = 16;

    public const int MaximumSpeechSegmentCharacters = 320;
}
