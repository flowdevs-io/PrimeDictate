namespace PrimeDictate.Core.Audio;

public enum AudioSampleFormat
{
    Pcm16 = 0,
    Float32 = 1
}

/// <summary>Describes interleaved audio samples.</summary>
public readonly record struct AudioFormat(int SampleRate, int Channels, AudioSampleFormat SampleFormat)
{
    /// <summary>Timeline format used by every current speech engine: 16 kHz mono float.</summary>
    public static AudioFormat SpeechTimeline { get; } = new(16_000, 1, AudioSampleFormat.Float32);

    public int BytesPerSample => this.SampleFormat == AudioSampleFormat.Pcm16 ? 2 : 4;

    public void Validate()
    {
        if (this.SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(this.SampleRate), this.SampleRate, "Sample rate must be positive.");
        }

        if (this.Channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(this.Channels), this.Channels, "Channel count must be positive.");
        }
    }

    /// <summary>Converts a per-channel sample offset to exact time.</summary>
    public TimeSpan ToTime(long sampleOffset) =>
        TimeSpan.FromTicks(checked(sampleOffset * TimeSpan.TicksPerSecond / this.SampleRate));

    /// <summary>Converts time to the nearest preceding per-channel sample offset.</summary>
    public long ToSampleOffset(TimeSpan time) => time.Ticks * this.SampleRate / TimeSpan.TicksPerSecond;
}
