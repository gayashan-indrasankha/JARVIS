namespace Jarvis.Core.Voice;

/// <summary>
/// Describes uncompressed PCM audio without exposing a platform audio type.
/// </summary>
public sealed record AudioFormat
{
    public static AudioFormat Pcm16Mono24Khz { get; } = new(24_000, 1, 16);

    public AudioFormat(int sampleRateHz, int channelCount, int bitsPerSample)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitsPerSample);

        if (bitsPerSample % 8 != 0)
        {
            throw new ArgumentException("PCM sample width must be a whole number of bytes.", nameof(bitsPerSample));
        }

        SampleRateHz = sampleRateHz;
        ChannelCount = channelCount;
        BitsPerSample = bitsPerSample;
    }

    public int SampleRateHz { get; }

    public int ChannelCount { get; }

    public int BitsPerSample { get; }

    public int BytesPerSecond => checked(SampleRateHz * ChannelCount * (BitsPerSample / 8));
}
