namespace PrimeDictate.Core.Audio;

/// <summary>
/// Streaming mono resampler using a windowed-sinc polyphase filter.
/// </summary>
/// <remarks>
/// Output sample <c>n</c> corresponds exactly to input time <c>n * inputRate / outputRate</c>,
/// so offsets on the output timeline can be mapped back to the source without drift.
/// Chunk boundaries do not affect the result: feeding the same input in different chunk
/// sizes produces identical output. Call <see cref="Flush"/> once at end of stream to drain
/// the filter's look-ahead.
/// </remarks>
public sealed class StreamingResampler
{
    private const int BaseHalfTaps = 16;

    private readonly int upFactor;
    private readonly int downFactor;
    private readonly int halfTaps;
    private readonly float[][]? phases;
    private float[] buffer;
    private long bufferStart;
    private int bufferCount;
    private long nextOutput;
    private long totalInput;
    private bool flushed;

    public StreamingResampler(int inputRate, int outputRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(inputRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(outputRate, 0);
        this.InputRate = inputRate;
        this.OutputRate = outputRate;
        var gcd = Gcd(inputRate, outputRate);
        this.upFactor = outputRate / gcd;
        this.downFactor = inputRate / gcd;

        if (this.upFactor == this.downFactor)
        {
            this.halfTaps = 0;
            this.buffer = [];
            return;
        }

        var ratio = Math.Min(1.0, (double)this.upFactor / this.downFactor);
        this.halfTaps = (int)Math.Ceiling(BaseHalfTaps / ratio);
        // Cutoff in cycles per input sample, slightly below Nyquist of the lower rate.
        var cutoff = 0.5 * ratio * 0.94;
        this.phases = BuildPhases(this.upFactor, this.halfTaps, cutoff);

        // Leading zeros stand in for silence before the first sample.
        this.buffer = new float[Math.Max(1024, this.halfTaps * 4)];
        this.bufferCount = this.halfTaps - 1;
        this.bufferStart = -(this.halfTaps - 1);
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    /// <summary>Total output samples produced so far; also the offset of the next output sample.</summary>
    public long OutputSamplesProduced => this.nextOutput;

    public float[] Process(ReadOnlySpan<float> input)
    {
        if (this.flushed)
        {
            throw new InvalidOperationException("The resampler has been flushed.");
        }

        this.totalInput += input.Length;
        if (this.phases is null)
        {
            this.nextOutput += input.Length;
            return input.ToArray();
        }

        this.Append(input);
        return this.Drain(limit: long.MaxValue);
    }

    /// <summary>Drains the filter at end of stream; output length totals ceil(input * out / in).</summary>
    public float[] Flush()
    {
        if (this.flushed)
        {
            return [];
        }

        this.flushed = true;
        if (this.phases is null)
        {
            return [];
        }

        var expectedTotal = (long)Math.Ceiling((double)this.totalInput * this.upFactor / this.downFactor);
        Span<float> zeros = stackalloc float[this.halfTaps + 1];
        this.Append(zeros);
        return this.Drain(expectedTotal);
    }

    private float[] Drain(long limit)
    {
        var output = new List<float>();
        var available = this.bufferStart + this.bufferCount;
        while (this.nextOutput < limit)
        {
            var product = this.nextOutput * this.downFactor;
            var center = product / this.upFactor;
            if (center + this.halfTaps >= available)
            {
                break;
            }

            var phase = this.phases![(int)(product % this.upFactor)];
            var first = (int)(center - this.halfTaps + 1 - this.bufferStart);
            var sum = 0f;
            for (var k = 0; k < phase.Length; k++)
            {
                sum += phase[k] * this.buffer[first + k];
            }

            output.Add(sum);
            this.nextOutput++;
        }

        this.Trim();
        return output.ToArray();
    }

    private void Append(ReadOnlySpan<float> input)
    {
        if (this.bufferCount + input.Length > this.buffer.Length)
        {
            Array.Resize(ref this.buffer, Math.Max(this.buffer.Length * 2, this.bufferCount + input.Length));
        }

        input.CopyTo(this.buffer.AsSpan(this.bufferCount));
        this.bufferCount += input.Length;
    }

    private void Trim()
    {
        var nextCenter = this.nextOutput * this.downFactor / this.upFactor;
        var keepFrom = nextCenter - this.halfTaps + 1;
        var drop = (int)Math.Clamp(keepFrom - this.bufferStart, 0, this.bufferCount);
        if (drop == 0)
        {
            return;
        }

        this.buffer.AsSpan(drop, this.bufferCount - drop).CopyTo(this.buffer);
        this.bufferCount -= drop;
        this.bufferStart += drop;
    }

    private static float[][] BuildPhases(int phaseCount, int halfTaps, double cutoff)
    {
        var phases = new float[phaseCount][];
        var tapCount = halfTaps * 2;
        for (var p = 0; p < phaseCount; p++)
        {
            var coefficients = new double[tapCount];
            var fraction = (double)p / phaseCount;
            var sum = 0.0;
            for (var k = 0; k < tapCount; k++)
            {
                // Distance in input samples between this tap and the exact output position.
                var t = (k - halfTaps + 1) - fraction;
                var sincArg = 2.0 * cutoff * t;
                var sinc = Math.Abs(sincArg) < 1e-12 ? 1.0 : Math.Sin(Math.PI * sincArg) / (Math.PI * sincArg);
                var w = (t / halfTaps + 1.0) / 2.0;
                var window = Math.Abs(t) >= halfTaps
                    ? 0.0
                    : 0.42 - (0.5 * Math.Cos(2 * Math.PI * w)) + (0.08 * Math.Cos(4 * Math.PI * w));
                coefficients[k] = sinc * window;
                sum += coefficients[k];
            }

            phases[p] = coefficients.Select(c => (float)(c / sum)).ToArray();
        }

        return phases;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
