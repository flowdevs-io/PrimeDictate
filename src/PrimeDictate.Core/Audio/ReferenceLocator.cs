using System.Numerics;

namespace PrimeDictate.Core.Audio;

/// <summary>Where a known signal was found in a recording.</summary>
/// <param name="SampleIndex">Recording sample at which the reference starts.</param>
/// <param name="Correlation">Normalized correlation at that position, 0 to 1.</param>
/// <param name="PeakToSidelobe">Peak divided by the median correlation elsewhere. Above about 6 the match is unambiguous.</param>
public sealed record ReferenceMatch(long SampleIndex, double Correlation, double PeakToSidelobe)
{
    public bool IsReliable => this.Correlation >= 0.05 && this.PeakToSidelobe >= 6;
}

/// <summary>
/// Finds a known test signal in a recording with a matched filter (FFT cross-correlation). Unlike
/// <see cref="ChannelSkew"/> it needs no clean echo: a faint copy of a noise train is still found.
/// </summary>
public static class ReferenceLocator
{
    public static ReferenceMatch Locate(ReadOnlySpan<float> recording, ReadOnlySpan<float> reference)
    {
        if (reference.Length == 0 || recording.Length < reference.Length)
        {
            return new ReferenceMatch(0, 0, 0);
        }

        var size = 1;
        while (size < recording.Length + reference.Length)
        {
            size <<= 1;
        }

        var a = new Complex[size];
        var b = new Complex[size];
        for (var i = 0; i < recording.Length; i++)
        {
            a[i] = recording[i];
        }

        double refEnergy = 0;
        for (var i = 0; i < reference.Length; i++)
        {
            b[i] = reference[i];
            refEnergy += reference[i] * (double)reference[i];
        }

        Fft(a, false);
        Fft(b, false);
        for (var i = 0; i < size; i++)
        {
            a[i] *= Complex.Conjugate(b[i]);
        }

        Fft(a, true);

        var prefix = new double[recording.Length + 1];
        for (var i = 0; i < recording.Length; i++)
        {
            prefix[i + 1] = prefix[i] + (recording[i] * (double)recording[i]);
        }

        var positions = recording.Length - reference.Length + 1;
        var ncc = new double[positions];
        var best = 0;
        for (var k = 0; k < positions; k++)
        {
            var energy = prefix[k + reference.Length] - prefix[k];
            var norm = Math.Sqrt(energy * refEnergy);
            ncc[k] = norm > 1e-12 ? Math.Abs(a[k].Real) / norm : 0;
            if (ncc[k] > ncc[best])
            {
                best = k;
            }
        }

        var sorted = (double[])ncc.Clone();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];
        var psr = Math.Min(999, ncc[best] / Math.Max(median, 1e-3));
        return new ReferenceMatch(best, ncc[best], psr);
    }

    /// <summary>A repeatable noise-burst train: <paramref name="bursts"/> bursts of white noise with silence between.</summary>
    public static float[] NoiseBurstTrain(int sampleRate, int bursts = 8, int burstMs = 60, int gapMs = 340, int seed = 12345)
    {
        var burst = sampleRate * burstMs / 1000;
        var gap = sampleRate * gapMs / 1000;
        var rng = new Random(seed);
        var x = new float[bursts * (burst + gap)];
        for (var n = 0; n < bursts; n++)
        {
            for (var i = 0; i < burst; i++)
            {
                var fade = Math.Min(1f, Math.Min(i, burst - 1 - i) / (sampleRate * 0.003f)); // 3 ms edges avoid a pop
                x[(n * (burst + gap)) + i] = ((float)(rng.NextDouble() * 2 - 1)) * fade;
            }
        }

        return x;
    }

    private static void Fft(Complex[] x, bool inverse)
    {
        var n = x.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (x[i], x[j]) = (x[j], x[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wl = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (var k = 0; k < len / 2; k++)
                {
                    var u = x[i + k];
                    var v = x[i + k + (len / 2)] * w;
                    x[i + k] = u + v;
                    x[i + k + (len / 2)] = u - v;
                    w *= wl;
                }
            }
        }

        if (inverse)
        {
            for (var i = 0; i < n; i++)
            {
                x[i] /= n;
            }
        }
    }
}
