using PrimeDictate.Core.Audio;

namespace PrimeDictate.Core.Tests;

public class AudioTests
{
    [Fact]
    public void Sample_offsets_convert_to_exact_time()
    {
        var format = AudioFormat.SpeechTimeline;
        Assert.Equal(TimeSpan.FromSeconds(1), format.ToTime(16_000));
        Assert.Equal(TimeSpan.FromHours(3), format.ToTime(16_000L * 3 * 3600));
        Assert.Equal(16_000L * 7200, format.ToSampleOffset(TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Frame_owns_a_copy_of_callback_memory()
    {
        var pooled = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
        var frame = AudioFrame.CopyFrom(pooled, new AudioFormat(48_000, 2, AudioSampleFormat.Float32), 7, 96_000);
        Array.Clear(pooled);
        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f, 0.4f }, frame.Samples.ToArray());
        Assert.Equal(2, frame.SamplesPerChannel);
        Assert.Equal(TimeSpan.FromSeconds(2), frame.Start);
        Assert.Equal(96_002, frame.EndSampleOffset);
    }

    [Fact]
    public void Frame_rejects_partial_interleaved_frames()
    {
        Assert.Throws<ArgumentException>(() =>
            AudioFrame.CopyFrom(new float[3], new AudioFormat(48_000, 2, AudioSampleFormat.Float32), 0, 0));
    }

    [Fact]
    public void Pcm16_round_trips_through_float()
    {
        short[] source = [0, 1000, -1000, short.MaxValue, short.MinValue + 1];
        var bytes = new byte[source.Length * 2];
        Buffer.BlockCopy(source, 0, bytes, 0, bytes.Length);
        var floats = new float[source.Length];
        AudioConversion.Pcm16ToFloat(bytes, floats);
        var back = new byte[bytes.Length];
        AudioConversion.FloatToPcm16(floats, back);
        var result = new short[source.Length];
        Buffer.BlockCopy(back, 0, result, 0, back.Length);
        for (var i = 0; i < source.Length; i++)
        {
            Assert.InRange(result[i] - source[i], -1, 1);
        }
    }

    [Fact]
    public void Downmix_averages_and_channel_selection_is_explicit()
    {
        float[] stereo = [1f, 0f, 0.5f, -0.5f];
        Assert.Equal(new[] { 0.5f, 0f }, AudioConversion.DownmixToMono(stereo, 2));
        Assert.Equal(new[] { 0f, -0.5f }, AudioConversion.SelectChannel(stereo, 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioConversion.SelectChannel(stereo, 2, 2));
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    [InlineData(22_050)]
    [InlineData(8_000)]
    public void Resampler_output_length_matches_rate_ratio(int inputRate)
    {
        var resampler = new StreamingResampler(inputRate, 16_000);
        var input = new float[inputRate * 3 + 17];
        var produced = resampler.Process(input).Length + resampler.Flush().Length;
        var expected = (long)Math.Ceiling(input.Length * 16_000.0 / inputRate);
        Assert.Equal(expected, produced);
        Assert.Equal(expected, resampler.OutputSamplesProduced);
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    public void Resampler_result_does_not_depend_on_chunk_boundaries(int inputRate)
    {
        var input = Sine(inputRate, 440, inputRate * 2);
        var whole = new StreamingResampler(inputRate, 16_000);
        var expected = whole.Process(input).Concat(whole.Flush()).ToArray();

        var chunked = new StreamingResampler(inputRate, 16_000);
        var random = new Random(1234);
        var output = new List<float>();
        var position = 0;
        while (position < input.Length)
        {
            var size = Math.Min(random.Next(1, 3000), input.Length - position);
            output.AddRange(chunked.Process(input.AsSpan(position, size)));
            position += size;
        }

        output.AddRange(chunked.Flush());
        Assert.Equal(expected.Length, output.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], output[i], 5);
        }
    }

    [Fact]
    public void Resampler_keeps_speech_band_and_rejects_aliasing_tones()
    {
        const int inputRate = 48_000;
        var passband = Resample(Sine(inputRate, 1_000, inputRate), inputRate);
        var alias = Resample(Sine(inputRate, 12_000, inputRate), inputRate);

        // Skip filter warm-up and tail.
        Assert.InRange(Rms(passband.AsSpan(1000, 14_000)), 0.69, 0.72); // 1/sqrt(2)
        Assert.True(Rms(alias.AsSpan(1000, 14_000)) < 0.01, "12 kHz must not fold into the 16 kHz timeline.");
    }

    [Fact]
    public void Resampler_preserves_timing_of_an_impulse()
    {
        const int inputRate = 48_000;
        var input = new float[inputRate];
        input[24_000] = 1f; // exactly 0.5 s
        var output = Resample(input, inputRate);
        var peak = Array.IndexOf(output, output.Max());
        Assert.Equal(8_000, peak);
    }

    [Fact]
    public void Resampler_passthrough_when_rates_match()
    {
        var resampler = new StreamingResampler(16_000, 16_000);
        float[] input = [0.1f, 0.2f];
        Assert.Equal(input, resampler.Process(input));
        Assert.Empty(resampler.Flush());
    }

    [Fact]
    public void Paused_time_is_not_recorded_and_maps_back_to_wall_clock()
    {
        var timeline = new RecordedAudioTimeline(AudioFormat.SpeechTimeline);
        var t0 = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        timeline.BeginInterval(t0);
        Assert.Equal(0, timeline.Append(16_000 * 10));
        timeline.EndInterval(CaptureIntervalEnd.Paused);

        Assert.Throws<InvalidOperationException>(() => timeline.Append(1));

        var resumedAt = t0.AddMinutes(5);
        timeline.BeginInterval(resumedAt);
        Assert.Equal(16_000 * 10, timeline.Append(16_000 * 4));
        timeline.EndInterval(CaptureIntervalEnd.Stopped);

        Assert.Equal(TimeSpan.FromSeconds(14), timeline.RecordedDuration);
        Assert.Equal(t0.AddSeconds(3), timeline.ToWallClock(TimeSpan.FromSeconds(3)));
        Assert.Equal(resumedAt.AddSeconds(1), timeline.ToWallClock(TimeSpan.FromSeconds(11)));
        Assert.Null(timeline.ToWallClock(TimeSpan.FromSeconds(14)));
    }

    private static float[] Resample(float[] input, int inputRate)
    {
        var resampler = new StreamingResampler(inputRate, 16_000);
        return resampler.Process(input).Concat(resampler.Flush()).ToArray();
    }

    private static float[] Sine(int rate, double frequency, int count) =>
        Enumerable.Range(0, count).Select(i => (float)Math.Sin(2 * Math.PI * frequency * i / rate)).ToArray();

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        return Math.Sqrt(sum / samples.Length);
    }
}
