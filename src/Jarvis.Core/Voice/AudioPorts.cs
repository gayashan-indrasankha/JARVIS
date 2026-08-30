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
/// Plays assistant audio and reports how much was audible when interrupted.
/// </summary>
public interface IAudioPlayback : IAsyncDisposable
{
    public AudioFormat Format { get; }

    public ValueTask EnqueueAsync(AssistantAudioChunk chunk, CancellationToken cancellationToken);

    public ValueTask<PlaybackCursor?> InterruptAsync(CancellationToken cancellationToken);

    public ValueTask StopAsync(CancellationToken cancellationToken);
}

public sealed record AssistantAudioChunk
{
    public AssistantAudioChunk(byte[] data, string itemId, int contentIndex)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentOutOfRangeException.ThrowIfNegative(contentIndex);

        if (data.Length == 0 || data.Length > VoiceDataLimits.MaximumAudioChunkBytes)
        {
            throw new ArgumentException("Assistant audio has an invalid size.", nameof(data));
        }

        if (itemId.Length > VoiceDataLimits.MaximumItemIdCharacters ||
            itemId.Any(char.IsControl))
        {
            throw new ArgumentException("The provider item identifier is invalid.", nameof(itemId));
        }

        Data = data;
        ItemId = itemId;
        ContentIndex = contentIndex;
    }

    public byte[] Data { get; }

    public string ItemId { get; }

    public int ContentIndex { get; }
}

public sealed record PlaybackCursor
{
    public PlaybackCursor(string itemId, int contentIndex, TimeSpan playedDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentOutOfRangeException.ThrowIfNegative(contentIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(playedDuration, TimeSpan.Zero);

        if (itemId.Length > VoiceDataLimits.MaximumItemIdCharacters ||
            itemId.Any(char.IsControl))
        {
            throw new ArgumentException("The provider item identifier is invalid.", nameof(itemId));
        }

        ItemId = itemId;
        ContentIndex = contentIndex;
        PlayedDuration = playedDuration;
    }

    public string ItemId { get; }

    public int ContentIndex { get; }

    public TimeSpan PlayedDuration { get; }
}

public static class VoiceDataLimits
{
    public const int MaximumAudioChunkBytes = 256 * 1024;

    public const int MaximumTextCharacters = 32 * 1024;

    public const int MaximumItemIdCharacters = 256;

    public const int MaximumInstructionsCharacters = 16 * 1024;
}
