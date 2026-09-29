namespace PrimeDictate.Core.Dictation;

public readonly record struct VisualizerParticle(double X, double Y, double Brightness, double Size, int ColorIndex);

/// <summary>
/// The overlay's motion, ported from the WPF particle overlay: a mirrored waveform that scrolls outward from the
/// microphone, and 150 smoke-like particles on a vector field that speed up with loudness. No UI types, so the
/// physics is testable and each shell only draws the numbers.
/// </summary>
public sealed class OverlayVisualizer
{
    public const int BarCount = 90;
    public const int ParticleCount = 150;
    public const int ColorCount = 5;

    private readonly Random random;
    private readonly double[] barTargets = new double[BarCount];
    private readonly double[] barCurrents = new double[BarCount];
    private readonly double[] x = new double[ParticleCount];
    private readonly double[] y = new double[ParticleCount];
    private readonly double[] life = new double[ParticleCount];
    private readonly double[] size = new double[ParticleCount];
    private double smoothedRms;
    private double targetRms;
    private double width = 1;
    private double height = 1;
    private bool seeded;

    public OverlayVisualizer(Random? random = null)
    {
        this.random = random ?? new Random();
        for (var i = 0; i < ParticleCount; i++)
        {
            this.size[i] = 1.8 + (this.random.NextDouble() * 2.4);
        }
    }

    /// <summary>Bar heights, center outward, already faded toward the edge.</summary>
    public IReadOnlyList<double> Bars { get; } = new double[BarCount];

    public IReadOnlyList<VisualizerParticle> Particles { get; } = new VisualizerParticle[ParticleCount];

    public void SetLevel(double rms) => this.targetRms = Math.Max(0, rms);

    public void Resize(double width, double height)
    {
        if (width <= 1 || height <= 1 || (width == this.width && height == this.height && this.seeded))
        {
            return;
        }

        this.width = width;
        this.height = height;
        for (var i = 0; i < ParticleCount; i++)
        {
            this.Reset(i, seedInFlight: true);
        }

        this.seeded = true;
    }

    /// <param name="seconds">Animation clock, used by the vector field.</param>
    public void Tick(double seconds)
    {
        // The target decays so silence settles to zero; the smoothed value follows it.
        this.targetRms *= 0.8;
        this.smoothedRms = (this.smoothedRms * 0.7) + (this.targetRms * 0.3);
        var intensity = this.smoothedRms * 400.0;

        for (var i = BarCount - 1; i > 0; i--)
        {
            this.barTargets[i] = this.barTargets[i - 1];
        }

        this.barTargets[0] = 3.0 + (intensity * (1.0 + (this.random.NextDouble() * 0.4)));
        var bars = (double[])this.Bars;
        for (var i = 0; i < BarCount; i++)
        {
            var fade = Math.Pow(1.0 - ((double)i / BarCount), 1.2);
            var speed = this.barTargets[i] > this.barCurrents[i] ? 0.8 : 0.4;
            this.barCurrents[i] += (this.barTargets[i] - this.barCurrents[i]) * speed;
            bars[i] = Math.Clamp(this.barCurrents[i] * fade, 3.0, Math.Max(3.0, 120.0 * fade));
        }

        var boost = intensity * 0.05;
        var particles = (VisualizerParticle[])this.Particles;
        const double k = 0.035;
        for (var i = 0; i < ParticleCount; i++)
        {
            this.x[i] += Math.Sin((this.y[i] * k) + (seconds * 2.2) + (i * 0.15)) * (0.85 + boost);
            this.y[i] += -0.4 - (Math.Abs(Math.Cos((this.x[i] * k) + (seconds * 1.6))) * (0.65 + boost));
            this.life[i] -= 0.008 + (boost * 0.004);
            if (this.life[i] <= 0 || this.y[i] < -20 || this.y[i] > this.height + 20 || this.x[i] < -20 || this.x[i] > this.width + 20)
            {
                this.Reset(i, seedInFlight: false);
            }

            particles[i] = new VisualizerParticle(
                this.x[i],
                this.y[i],
                Math.Clamp(this.life[i] * (0.4 + (intensity * 0.03)), 0.0, 1.0),
                this.size[i],
                i % ColorCount);
        }
    }

    private void Reset(int i, bool seedInFlight)
    {
        var centerY = this.height / 2.0;
        var travel = Math.Max(this.height * 0.46, 24.0);
        this.x[i] = this.random.NextDouble() * this.width;
        var up = this.random.NextDouble() > 0.5;
        if (seedInFlight)
        {
            var progress = this.random.NextDouble();
            this.y[i] = centerY + ((up ? -1.0 : 1.0) * progress * travel);
            this.life[i] = 0.2 + ((1.0 - progress) * 0.8);
        }
        else
        {
            this.y[i] = centerY + ((this.random.NextDouble() - 0.5) * 4.0);
            this.life[i] = 0.5 + (this.random.NextDouble() * 0.5);
        }
    }
}
