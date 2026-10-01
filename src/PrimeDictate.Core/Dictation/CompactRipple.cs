namespace PrimeDictate.Core.Dictation;

/// <summary>
/// The compact overlay's microphone animation, ported from the WPF overlay: three rings that grow outward from the middle of
/// the microphone and fade, brighter while there is voice, plus a glow inside the microphone that follows the level. No UI types,
/// so the numbers are testable and the shell only draws them. Call <see cref="SetLevel"/> as audio arrives and <see cref="Tick"/> at about 30 Hz.
/// </summary>
public sealed class CompactRipple
{
    public const int RingCount = 3;
    public const double SecondsPerCycle = 2.85;

    private double targetRms;
    private double smoothedRms;
    private double pulseEnvelope;
    private double surfaceEnvelope;

    /// <summary>0 to 1, how far the first ring is through its cycle.</summary>
    public double Phase { get; private set; }

    /// <summary>0.15 (silent) to 1 (loud): scales how visible the rings are.</summary>
    public double Intensity { get; private set; } = 0.15;

    /// <summary>The glow inside the microphone, 0 to 0.88.</summary>
    public double GlowOpacity { get; private set; }

    /// <summary>Size of the inner glow relative to the microphone, 0.78 to about 1.1.</summary>
    public double GlowScale { get; private set; } = 0.78;

    public void SetLevel(double rms) => this.targetRms = Math.Max(0, rms);

    public void Reset()
    {
        this.targetRms = 0;
        this.smoothedRms = 0;
        this.pulseEnvelope = 0;
        this.surfaceEnvelope = 0;
        this.Phase = 0;
        this.Intensity = 0.15;
        this.GlowOpacity = 0;
        this.GlowScale = 0.78;
    }

    public void Tick(double elapsedSeconds = 0.033)
    {
        // The target decays so a level that stops arriving fades out by itself; the smoothed value follows it.
        this.targetRms *= 0.8;
        this.smoothedRms = (this.smoothedRms * 0.7) + (this.targetRms * 0.3);
        var voice = Math.Min(1.0, this.smoothedRms * 34.0);
        var boost = Math.Min(0.85, this.smoothedRms * 5.0);

        // Fast attack and slow release: the pulse jumps up with speech and lingers instead of breathing in and out.
        this.pulseEnvelope = voice > this.pulseEnvelope ? this.pulseEnvelope + ((voice - this.pulseEnvelope) * 0.72) : this.pulseEnvelope * 0.965;
        this.surfaceEnvelope = voice > this.surfaceEnvelope ? this.surfaceEnvelope + ((voice - this.surfaceEnvelope) * 0.82) : this.surfaceEnvelope * 0.972;

        this.GlowScale = 0.78 + (this.pulseEnvelope * 0.22) + (boost * 0.12);
        this.GlowOpacity = Math.Clamp(0.12 + (this.surfaceEnvelope * 0.62) + (boost * 0.45), 0.0, 0.88);

        this.Phase += elapsedSeconds / SecondsPerCycle;
        if (this.Phase >= 1.0)
        {
            this.Phase -= Math.Floor(this.Phase);
        }

        this.Intensity = Math.Clamp(0.15 + (this.surfaceEnvelope * 0.95) + (boost * 0.35), 0.0, 1.0);
    }

    /// <summary>Scale and opacity of ring <paramref name="index"/> (0 to 2); the rings are a third of a cycle apart.</summary>
    public (double Scale, double Opacity) Ring(int index)
    {
        var phase = this.Phase + (index / (double)RingCount);
        return (Scale(phase), Opacity(phase, this.Intensity));
    }

    /// <summary>Starts small in the middle and grows to the edge of the microphone.</summary>
    public static double Scale(double phase) => 0.32 + (Fraction(phase) * 0.68);

    /// <summary>Fades as it grows; a louder voice makes the rings stronger, but never above 0.48.</summary>
    public static double Opacity(double phase, double voice) =>
        Math.Clamp(Math.Pow(1.0 - Fraction(phase), 1.85) * (0.08 + (voice * 0.5)), 0.0, 0.48);

    private static double Fraction(double phase) => phase - Math.Floor(phase);
}
