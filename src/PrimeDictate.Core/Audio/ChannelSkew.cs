namespace PrimeDictate.Core.Audio;

/// <summary>Result of <see cref="ChannelSkew.Estimate"/>.</summary>
/// <param name="MicrophoneLagMs">Positive: the same sound is heard on the microphone channel this many ms after the system channel.</param>
/// <param name="Correlation">Normalized correlation at the best lag, 0 to 1. Below about 0.3 the estimate is not trustworthy.</param>
public sealed record ChannelSkewResult(double MicrophoneLagMs, double Correlation);

/// <summary>
/// Measures the offset between the two channels of a meeting recording by correlating their loudness
/// envelopes. It needs the microphone to have heard the speakers (echo, or a loopback cable); an
/// acoustic path adds only a few milliseconds, so anything larger is skew in capture.
/// </summary>
public static class ChannelSkew
{
    public static ChannelSkewResult Estimate(ReadOnlySpan<float> microphone, ReadOnlySpan<float> system, int sampleRate, int maxLagMs = 3000)
    {
        var block = Math.Max(1, sampleRate / 1000); // 1 ms envelope
        var mic = Envelope(microphone, block);
        var sys = Envelope(system, block);
        var n = Math.Min(mic.Length, sys.Length);
        double best = double.MinValue;
        var bestLag = 0;
        for (var lag = -maxLagMs; lag <= maxLagMs; lag++)
        {
            double sum = 0, sm = 0, ss = 0;
            for (var t = Math.Max(0, lag); t < Math.Min(n, n + lag); t++)
            {
                var a = mic[t];
                var b = sys[t - lag];
                sum += a * b;
                sm += a * a;
                ss += b * b;
            }

            var norm = Math.Sqrt(sm * ss);
            var c = norm > 0 ? sum / norm : 0;
            if (c > best)
            {
                best = c;
                bestLag = lag;
            }
        }

        return new ChannelSkewResult(bestLag, Math.Max(0, best));
    }

    private static float[] Envelope(ReadOnlySpan<float> x, int block)
    {
        var env = new float[x.Length / block];
        for (var i = 0; i < env.Length; i++)
        {
            float sum = 0;
            for (var j = 0; j < block; j++)
            {
                sum += Math.Abs(x[(i * block) + j]);
            }

            env[i] = sum / block;
        }

        return env;
    }
}
