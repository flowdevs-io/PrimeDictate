using PrimeDictate.Core.Audio;

namespace PrimeDictate.Core.Pipeline;

/// <summary>A span of the 16 kHz mono timeline to send to a recognizer, with its absolute offset.</summary>
public sealed record AudioChunk(long StartSample, float[] Samples, bool ContainsSpeech)
{
    public TimeSpan Start => AudioFormat.SpeechTimeline.ToTime(this.StartSample);

    public TimeSpan Duration => AudioFormat.SpeechTimeline.ToTime(this.Samples.Length);
}

public sealed record SpeechChunkerOptions
{
    /// <summary>Longest chunk the recognizer accepts. Whisper models are trained on 30 s windows.</summary>
    public TimeSpan MaxChunk { get; init; } = TimeSpan.FromSeconds(28);

    /// <summary>Shortest chunk worth sending; shorter tails are merged into the previous chunk when possible.</summary>
    public TimeSpan MinChunk { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Energy analysis frame.</summary>
    public TimeSpan Frame { get; init; } = TimeSpan.FromMilliseconds(30);

    /// <summary>Frames below this RMS count as silence.</summary>
    public float SilenceRms { get; init; } = 0.008f;

    /// <summary>How far back from the maximum a cut may move to land in a quiet spot.</summary>
    public TimeSpan CutSearch { get; init; } = TimeSpan.FromSeconds(6);
}

/// <summary>
/// Splits a 16 kHz mono stream into chunks at the quietest point near the size limit. Chunks are
/// contiguous and non-overlapping, so every sample belongs to exactly one chunk and results can be
/// concatenated in time order without text de-duplication (repeated speech stays intact).
/// Chunks with no speech are reported with <see cref="AudioChunk.ContainsSpeech"/> false so the
/// caller can skip inference while the timeline still advances.
/// </summary>
public sealed class SpeechChunker
{
    private readonly SpeechChunkerOptions options;
    private readonly int frameSamples;
    private readonly int maxSamples;
    private readonly int minSamples;
    private readonly int searchSamples;
    private readonly List<float> buffer = [];
    private long bufferStart;

    public SpeechChunker(SpeechChunkerOptions? options = null)
    {
        this.options = options ?? new SpeechChunkerOptions();
        var format = AudioFormat.SpeechTimeline;
        this.frameSamples = (int)format.ToSampleOffset(this.options.Frame);
        this.maxSamples = (int)format.ToSampleOffset(this.options.MaxChunk);
        this.minSamples = (int)format.ToSampleOffset(this.options.MinChunk);
        this.searchSamples = (int)format.ToSampleOffset(this.options.CutSearch);
    }

    public long BufferedSamples => this.buffer.Count;

    public IReadOnlyList<AudioChunk> Add(ReadOnlySpan<float> samples)
    {
        this.buffer.AddRange(samples.ToArray());
        var chunks = new List<AudioChunk>();
        while (this.buffer.Count >= this.maxSamples + this.frameSamples)
        {
            chunks.Add(this.Cut(this.FindCut()));
        }

        return chunks;
    }

    /// <summary>Emits whatever remains at end of stream.</summary>
    public IReadOnlyList<AudioChunk> Flush()
    {
        var chunks = new List<AudioChunk>();
        while (this.buffer.Count > this.maxSamples)
        {
            chunks.Add(this.Cut(this.FindCut()));
        }

        if (this.buffer.Count > 0)
        {
            chunks.Add(this.Cut(this.buffer.Count));
        }

        return chunks;
    }

    private int FindCut()
    {
        // Quietest frame in the last CutSearch before the maximum.
        var end = this.maxSamples;
        var start = Math.Max(this.minSamples, end - this.searchSamples);
        var best = end;
        var bestEnergy = double.MaxValue;
        for (var position = start; position + this.frameSamples <= end; position += this.frameSamples)
        {
            var energy = 0d;
            for (var i = 0; i < this.frameSamples; i++)
            {
                var s = this.buffer[position + i];
                energy += s * s;
            }

            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                best = position + (this.frameSamples / 2);
            }
        }

        return best;
    }

    private AudioChunk Cut(int length)
    {
        var samples = this.buffer.GetRange(0, length).ToArray();
        this.buffer.RemoveRange(0, length);
        var chunk = new AudioChunk(this.bufferStart, samples, this.HasSpeech(samples));
        this.bufferStart += length;
        return chunk;
    }

    private bool HasSpeech(float[] samples)
    {
        var threshold = this.options.SilenceRms * this.options.SilenceRms;
        for (var position = 0; position + this.frameSamples <= samples.Length; position += this.frameSamples)
        {
            double energy = 0;
            for (var i = 0; i < this.frameSamples; i++)
            {
                energy += samples[position + i] * samples[position + i];
            }

            if (energy / this.frameSamples > threshold)
            {
                return true;
            }
        }

        // Tail shorter than one frame.
        return samples.Length > 0 && samples.Length < this.frameSamples && Rms(samples) > this.options.SilenceRms;
    }

    private static double Rms(float[] samples)
    {
        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        return Math.Sqrt(sum / samples.Length);
    }
}
