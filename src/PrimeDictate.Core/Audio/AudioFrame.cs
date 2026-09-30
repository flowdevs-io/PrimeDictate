namespace PrimeDictate.Core.Audio;

/// <summary>
/// A block of interleaved audio samples on a source's timeline.
/// </summary>
/// <remarks>
/// A frame always owns its sample array. Capture adapters must copy out of pooled or
/// callback-owned buffers before creating a frame, so consumers can hold frames across
/// channel boundaries without reading memory that was returned to its owner.
/// </remarks>
public sealed class AudioFrame
{
    private readonly float[] samples;

    private AudioFrame(float[] samples, AudioFormat format, long sequenceNumber, long sampleOffset, bool isSyntheticSilence)
    {
        this.IsSyntheticSilence = isSyntheticSilence;
        this.samples = samples;
        this.Format = format;
        this.SequenceNumber = sequenceNumber;
        this.SampleOffset = sampleOffset;
    }

    public AudioFormat Format { get; }

    /// <summary>
    /// True when the capture layer produced these samples itself as silence to keep the timeline
    /// continuous (for example WASAPI loopback, which delivers nothing while nothing plays), not the
    /// device. Consumers that cannot cope with long exact-zero runs, such as a realtime recognizer,
    /// can skip these frames without re-detecting zeros. Offsets and sequence numbers still count them.
    /// </summary>
    public bool IsSyntheticSilence { get; }

    /// <summary>Monotonic per-source counter; a gap means frames were lost upstream.</summary>
    public long SequenceNumber { get; }

    /// <summary>Per-channel sample index of the first sample, counted from the start of the source.</summary>
    public long SampleOffset { get; }

    /// <summary>Interleaved samples in the range [-1, 1].</summary>
    public ReadOnlyMemory<float> Samples => this.samples;

    public int SamplesPerChannel => this.samples.Length / this.Format.Channels;

    public long EndSampleOffset => this.SampleOffset + this.SamplesPerChannel;

    public TimeSpan Start => this.Format.ToTime(this.SampleOffset);

    public TimeSpan End => this.Format.ToTime(this.EndSampleOffset);

    /// <summary>Creates a frame from a copy of <paramref name="interleaved"/>.</summary>
    public static AudioFrame CopyFrom(
        ReadOnlySpan<float> interleaved,
        AudioFormat format,
        long sequenceNumber,
        long sampleOffset,
        bool isSyntheticSilence = false)
    {
        Validate(interleaved.Length, format, sequenceNumber, sampleOffset);
        return new AudioFrame(interleaved.ToArray(), format with { SampleFormat = AudioSampleFormat.Float32 }, sequenceNumber, sampleOffset, isSyntheticSilence);
    }

    /// <summary>Creates a frame from little-endian PCM16 bytes, converting to float.</summary>
    public static AudioFrame FromPcm16(
        ReadOnlySpan<byte> pcm16,
        int sampleRate,
        int channels,
        long sequenceNumber,
        long sampleOffset)
    {
        if (pcm16.Length % 2 != 0)
        {
            throw new ArgumentException("PCM16 data must contain whole samples.", nameof(pcm16));
        }

        var floats = new float[pcm16.Length / 2];
        AudioConversion.Pcm16ToFloat(pcm16, floats);
        var format = new AudioFormat(sampleRate, channels, AudioSampleFormat.Float32);
        Validate(floats.Length, format, sequenceNumber, sampleOffset);
        return new AudioFrame(floats, format, sequenceNumber, sampleOffset, false);
    }

    private static void Validate(int sampleCount, AudioFormat format, long sequenceNumber, long sampleOffset)
    {
        format.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(sequenceNumber);
        ArgumentOutOfRangeException.ThrowIfNegative(sampleOffset);
        if (sampleCount % format.Channels != 0)
        {
            throw new ArgumentException("Interleaved sample count must be a multiple of the channel count.");
        }
    }
}
