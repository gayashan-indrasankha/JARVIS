namespace Jarvis.Core.Voice;

/// <summary>
/// One owned buffer of microphone PCM data.
/// </summary>
public sealed record AudioFrame
{
    public AudioFrame(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length == 0)
        {
            throw new ArgumentException("An audio frame cannot be empty.", nameof(data));
        }

        if (data.Length > VoiceDataLimits.MaximumAudioChunkBytes)
        {
            throw new ArgumentException("An audio frame exceeds the size limit.", nameof(data));
        }

        Data = data;
    }

    public byte[] Data { get; }
}
