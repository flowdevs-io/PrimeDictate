namespace PrimeDictate.Core.Audio;

public sealed record AutoGainOptions
{
    /// <summary>Level speech is brought to (RMS, full scale = 1). About -20 dBFS.</summary>
    public float TargetRms { get; init; } = 0.1f;

    /// <summary>Blocks quieter than this (about -48 dBFS) are treated as silence: they never raise the gain.</summary>
    public float GateRms { get; init; } = 0.004f;

    /// <summary>Largest boost. Keep low for a microphone, whose room noise would otherwise be amplified into "speech".</summary>
    public float MaxGain { get; init; } = 30f;

    public int SampleRate { get; init; } = 16_000;

    public TimeSpan Block { get; init; } = TimeSpan.FromMilliseconds(20);
}

/// <summary>
/// Light streaming level normalizer for the audio that feeds recognition. It never touches the saved
/// recording. Gain rises slowly toward <see cref="AutoGainOptions.TargetRms"/> while signal is present,
/// falls quickly when the signal is loud, is never below 1 (quiet is boosted, loud is left alone),
/// and holds during silence so digital silence stays silent. Output passes a soft limiter.
/// </summary>
public sealed class AutoGain
{
    private const float Ceiling = 0.8f;
    private readonly AutoGainOptions options;
    private readonly int blockSamples;
    private readonly float slew;
    private float held = 1f;
    private float current = 1f;
    private double sumSquares;
    private int inBlock;

    public AutoGain(AutoGainOptions? options = null)
    {
        this.options = options ?? new AutoGainOptions();
        this.blockSamples = Math.Max(1, (int)(this.options.SampleRate * this.options.Block.TotalSeconds));
        // Per-sample smoothing of the applied gain: about one block to settle, so gain changes never click.
        this.slew = 1f / this.blockSamples;
    }

    /// <summary>Gain currently applied, for diagnostics.</summary>
    public float CurrentGain => this.current;

    public float[] Process(ReadOnlySpan<float> input)
    {
        var output = new float[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            this.current += (this.held - this.current) * this.slew;
            var x = input[i];
            output[i] = Limit(x * this.current);
            this.sumSquares += (double)x * x;
            if (++this.inBlock == this.blockSamples)
            {
                this.EndBlock();
            }
        }

        return output;
    }

    /// <summary>Soft limiter: linear to 0.8, then a smooth knee toward 1.0, so boosted peaks never hard-clip.</summary>
    public static float Limit(float x)
    {
        var a = Math.Abs(x);
        if (a <= Ceiling)
        {
            return x;
        }

        var limited = Ceiling + ((1f - Ceiling) * MathF.Tanh((a - Ceiling) / (1f - Ceiling)));
        return MathF.CopySign(limited, x);
    }

    private void EndBlock()
    {
        var rms = (float)Math.Sqrt(this.sumSquares / this.inBlock);
        this.sumSquares = 0;
        this.inBlock = 0;
        if (rms < this.options.GateRms)
        {
            return;
        }

        var desired = Math.Clamp(this.options.TargetRms / rms, 1f, this.options.MaxGain);
        // Attack fast when the signal got louder, release slowly when it got quieter.
        this.held += (desired - this.held) * (desired < this.held ? 0.5f : 0.1f);
    }
}
