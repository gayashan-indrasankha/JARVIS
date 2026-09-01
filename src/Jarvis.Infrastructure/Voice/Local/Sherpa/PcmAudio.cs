using System.Buffers.Binary;

namespace Jarvis.Infrastructure.Voice.Local.Sherpa;

internal static class PcmAudio
{
    public static float[] Pcm16ToFloat(ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty || pcm.Length % sizeof(short) != 0)
        {
            throw new InvalidDataException("PCM16 audio must contain complete samples.");
        }

        float[] samples = new float[pcm.Length / sizeof(short)];
        for (int index = 0; index < samples.Length; index++)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(
                pcm.Slice(index * sizeof(short), sizeof(short)));
            samples[index] = sample / 32768.0F;
        }

        return samples;
    }

    public static byte[] FloatToPcm16(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            throw new InvalidDataException("Synthesized audio cannot be empty.");
        }

        byte[] pcm = new byte[checked(samples.Length * sizeof(short))];
        for (int index = 0; index < samples.Length; index++)
        {
            float value = samples[index];
            float bounded = float.IsFinite(value)
                ? Math.Clamp(value, -1.0F, 1.0F)
                : 0.0F;
            short sample = bounded <= -1.0F
                ? short.MinValue
                : (short)MathF.Round(bounded * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm.AsSpan(index * sizeof(short), sizeof(short)),
                sample);
        }

        return pcm;
    }
}
