using Jarvis.Infrastructure.Voice.Local.Sherpa;

namespace Jarvis.Infrastructure.Tests.Voice;

public sealed class PcmAudioTests
{
    [Fact]
    public void ConvertsPcm16AndFloatsWithBoundedQuantizationError()
    {
        float[] input = [-1.0F, -0.5F, 0.0F, 0.5F, 1.0F];

        byte[] pcm = PcmAudio.FloatToPcm16(input);
        float[] output = PcmAudio.Pcm16ToFloat(pcm);

        Assert.Equal(input.Length * sizeof(short), pcm.Length);
        Assert.Equal(input.Length, output.Length);
        for (int index = 0; index < input.Length; index++)
        {
            Assert.InRange(MathF.Abs(input[index] - output[index]), 0, 1.0F / short.MaxValue);
        }
    }

    [Fact]
    public void ClampsOutOfRangeSynthesizedSamples()
    {
        byte[] pcm = PcmAudio.FloatToPcm16([-2.0F, 2.0F]);

        float[] output = PcmAudio.Pcm16ToFloat(pcm);
        Assert.Equal(-1.0F, output[0]);
        Assert.InRange(output[1], 0.9999F, 1.0F);
    }

    [Fact]
    public void SilencesNonFiniteSynthesizedSamples()
    {
        byte[] pcm = PcmAudio.FloatToPcm16(
            [float.NaN, float.PositiveInfinity, float.NegativeInfinity]);

        float[] output = PcmAudio.Pcm16ToFloat(pcm);
        Assert.Equal([0.0F, 0.0F, 0.0F], output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void RejectsEmptyOrIncompletePcmFrames(int byteCount) =>
        Assert.Throws<InvalidDataException>(() => PcmAudio.Pcm16ToFloat(new byte[byteCount]));
}
