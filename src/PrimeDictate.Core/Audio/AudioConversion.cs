using System.Buffers.Binary;

namespace PrimeDictate.Core.Audio;

/// <summary>
/// Sample-format boundaries. Engines that need PCM16 convert here, once, at the point of use.
/// </summary>
public static class AudioConversion
{
    public static void Pcm16ToFloat(ReadOnlySpan<byte> pcm16, Span<float> destination)
    {
        var count = pcm16.Length / 2;
        if (destination.Length < count)
        {
            throw new ArgumentException("Destination is too small.", nameof(destination));
        }

        for (var i = 0; i < count; i++)
        {
            destination[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 2, 2)) / 32768f;
        }
    }

    public static void FloatToPcm16(ReadOnlySpan<float> samples, Span<byte> destination)
    {
        if (destination.Length < samples.Length * 2)
        {
            throw new ArgumentException("Destination is too small.", nameof(destination));
        }

        for (var i = 0; i < samples.Length; i++)
        {
            var scaled = Math.Clamp(samples[i], -1f, 1f) * 32767f;
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(i * 2, 2), (short)MathF.Round(scaled));
        }
    }

    /// <summary>
    /// Averages channels into mono. Downmixing is explicit so callers can instead pick
    /// one channel of a multi-track source.
    /// </summary>
    public static float[] DownmixToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        if (channels == 1)
        {
            return interleaved.ToArray();
        }

        var frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            var baseIndex = f * channels;
            for (var c = 0; c < channels; c++)
            {
                sum += interleaved[baseIndex + c];
            }

            mono[f] = sum / channels;
        }

        return mono;
    }

    /// <summary>Extracts one channel from interleaved audio.</summary>
    public static float[] SelectChannel(ReadOnlySpan<float> interleaved, int channels, int channel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, channels);
        var frames = interleaved.Length / channels;
        var result = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            result[f] = interleaved[(f * channels) + channel];
        }

        return result;
    }
}
