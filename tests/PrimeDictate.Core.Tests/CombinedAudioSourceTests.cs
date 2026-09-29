using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Providers;

namespace PrimeDictate.Core.Tests;

public class CombinedAudioSourceTests
{
    internal sealed class FakeLease(int rate, string name, int channels = 1) : IAudioCaptureLease
    {
        private readonly Channel<AudioFrame> frames = Channel.CreateUnbounded<AudioFrame>();
        private long sequence;
        private long offset;

        public AudioFormat Format { get; } = new(rate, channels, AudioSampleFormat.Float32);

        public string DeviceId => name;

        public string DeviceName => name;

        public bool Paused { get; private set; }

        public void Push(float value, int samples, bool skipSequence = false, long skipSamples = 0, bool synthetic = false)
        {
            if (skipSequence)
            {
                this.sequence++;
                this.offset += skipSamples;
            }

            var data = new float[samples * this.Format.Channels];
            Array.Fill(data, value);
            this.frames.Writer.TryWrite(AudioFrame.CopyFrom(data, this.Format, this.sequence++, this.offset, synthetic));
            this.offset += samples;
        }

        public void PushSamples(float[] mono)
        {
            var data = new float[mono.Length * this.Format.Channels];
            for (var i = 0; i < mono.Length; i++)
            {
                for (var c = 0; c < this.Format.Channels; c++)
                {
                    data[(i * this.Format.Channels) + c] = mono[i];
                }
            }

            this.frames.Writer.TryWrite(AudioFrame.CopyFrom(data, this.Format, this.sequence++, this.offset));
            this.offset += mono.Length;
        }

        public void PushSine(float amplitude, int samples)
        {
            var data = new float[samples * this.Format.Channels];
            for (var i = 0; i < samples; i++)
            {
                var v = amplitude * MathF.Sin((this.offset + i) * 0.3f);
                for (var c = 0; c < this.Format.Channels; c++)
                {
                    data[(i * this.Format.Channels) + c] = v;
                }
            }

            this.frames.Writer.TryWrite(AudioFrame.CopyFrom(data, this.Format, this.sequence++, this.offset));
            this.offset += samples;
        }

        public void Complete() => this.frames.Writer.TryComplete();

        public IAsyncEnumerable<AudioFrame> ReadFramesAsync(CancellationToken cancellationToken) =>
            this.frames.Reader.ReadAllAsync(cancellationToken);

        public ValueTask PauseAsync(CancellationToken cancellationToken)
        {
            this.Paused = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask ResumeAsync(CancellationToken cancellationToken)
        {
            this.Paused = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            this.Complete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSource(FakeLease lease, int openDelayMs = 0) : IAudioSource
    {
        public ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AudioInputDevice>>([]);

        public async ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken)
        {
            if (openDelayMs > 0)
            {
                await Task.Delay(openDelayMs, cancellationToken);
            }

            return lease;
        }
    }

    private static async Task<(IAudioCaptureLease Lease, FakeLease Mic, FakeLease System)> OpenAsync(int micRate, int systemRate, CombinedAudioOptions? options = null)
    {
        var mic = new FakeLease(micRate, "mic");
        var system = new FakeLease(systemRate, "speakers");
        var lease = await new CombinedAudioSource(new FakeSource(mic), new FakeSource(system), options).OpenAsync(null, default);
        return (lease, mic, system);
    }

    private static async Task<List<AudioFrame>> ReadAllAsync(IAudioCaptureLease lease)
    {
        var frames = new List<AudioFrame>();
        await foreach (var f in Timed(lease.ReadFramesAsync(CancellationToken.None), TimeSpan.FromSeconds(10)))
        {
            frames.Add(f);
        }

        return frames;
    }

    private static async IAsyncEnumerable<T> Timed<T>(IAsyncEnumerable<T> source, TimeSpan timeout, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await foreach (var item in source.WithCancellation(cts.Token))
        {
            yield return item;
        }
    }

    [Fact]
    public async Task Stereo_output_keeps_microphone_left_and_system_audio_right_on_one_clock()
    {
        var (lease, mic, system) = await OpenAsync(16_000, 48_000);
        Assert.Equal(2, lease.Format.Channels);
        Assert.Equal("mic + speakers", lease.DeviceName);
        mic.Push(0.25f, 16_000);
        system.Push(-0.5f, 48_000);
        await lease.DisposeAsync();
        var frames = await ReadAllAsync(lease);

        var perChannel = frames.Sum(f => f.SamplesPerChannel);
        Assert.InRange(perChannel, 15_900, 16_000);
        Assert.Equal(0, frames[0].SampleOffset);
        for (var i = 1; i < frames.Count; i++)
        {
            Assert.Equal(frames[i - 1].SequenceNumber + 1, frames[i].SequenceNumber);
            Assert.Equal(frames[i - 1].EndSampleOffset, frames[i].SampleOffset);
        }

        var middle = frames[frames.Count / 2].Samples.Span;
        Assert.Equal(0.25f, middle[0], 3);
        Assert.Equal(-0.5f, middle[1], 2);
    }

    [Fact]
    public async Task Mixed_layout_sums_both_sides_into_one_channel()
    {
        var (lease, mic, system) = await OpenAsync(16_000, 16_000, new CombinedAudioOptions { Layout = CombinedChannelLayout.Mixed });
        Assert.Equal(1, lease.Format.Channels);
        mic.Push(0.25f, 1_600);
        system.Push(0.5f, 1_600);
        await lease.DisposeAsync();
        var frames = await ReadAllAsync(lease);
        Assert.Equal(0.75f, frames[0].Samples.Span[0], 3);
    }

    [Fact]
    public async Task Stalled_side_is_padded_with_silence_so_the_other_side_keeps_flowing()
    {
        var (lease, mic, system) = await OpenAsync(16_000, 16_000, new CombinedAudioOptions { MaxSkew = TimeSpan.FromMilliseconds(50) });
        mic.Push(0.25f, 3_200);
        // System audio delivers nothing (Windows loopback sends no packets during silence).
        var first = await Timed(lease.ReadFramesAsync(CancellationToken.None), TimeSpan.FromSeconds(5)).FirstAsync();
        Assert.Equal(0.25f, first.Samples.Span[0], 3);
        Assert.Equal(0f, first.Samples.Span[1]);
        await lease.DisposeAsync();
        Assert.True(((ICombinedAudioCaptureLease)lease).SystemAudioPaddedSamples > 0);
    }

    [Fact]
    public async Task Lost_device_samples_are_padded_and_the_combined_sequence_skips()
    {
        var (lease, mic, system) = await OpenAsync(16_000, 16_000);
        mic.Push(0.1f, 640);
        mic.Push(0.1f, 640);
        system.Push(0.2f, 640);
        system.Push(0.2f, 640, skipSequence: true, skipSamples: 640);
        await lease.DisposeAsync();
        var frames = await ReadAllAsync(lease);

        Assert.Equal(1_920, frames.Sum(f => f.SamplesPerChannel));
        Assert.Contains(frames.Zip(frames.Skip(1)), p => p.Second.SequenceNumber - p.First.SequenceNumber > 1);
        Assert.Equal(frames[^1].EndSampleOffset, frames.Sum(f => (long)f.SamplesPerChannel));
    }

    [Fact]
    public async Task Pause_pauses_both_devices_and_emits_the_shorter_sides_tail()
    {
        var (lease, mic, system) = await OpenAsync(16_000, 16_000, new CombinedAudioOptions { PauseDrainDelay = TimeSpan.FromMilliseconds(10) });
        mic.Push(0.3f, 1_000);
        system.Push(0.4f, 700);
        await lease.PauseAsync(CancellationToken.None);
        Assert.True(mic.Paused && system.Paused);
        await lease.ResumeAsync(CancellationToken.None);
        Assert.False(mic.Paused || system.Paused);
        await lease.DisposeAsync();
        var frames = await ReadAllAsync(lease);
        Assert.Equal(1_000, frames.Sum(f => f.SamplesPerChannel));
    }

    [Fact]
    public async Task Synthetic_silence_is_flagged_only_when_both_sides_are_filler()
    {
        var (lease, mic, system) = await OpenAsync(16_000, 16_000);
        mic.Push(0f, 3_200, synthetic: true);
        system.Push(0f, 3_200, synthetic: true);
        mic.Push(0.2f, 3_200);
        system.Push(0f, 3_200, synthetic: true);
        await lease.DisposeAsync();
        var frames = await ReadAllAsync(lease);
        var firstHalf = frames.Where(f => f.SampleOffset < 3_200).ToList();
        var secondHalf = frames.Where(f => f.SampleOffset >= 3_200).ToList();
        Assert.All(firstHalf, f => Assert.True(f.IsSyntheticSilence));
        Assert.All(secondHalf, f => Assert.False(f.IsSyntheticSilence));
    }

    private static int ClickIndex(IEnumerable<AudioFrame> frames, int channel)
    {
        var samples = frames.SelectMany(f => f.Samples.ToArray()).ToArray();
        for (var i = channel; i < samples.Length; i += 2)
        {
            if (Math.Abs(samples[i]) > 0.5f)
            {
                return i / 2;
            }
        }

        return -1;
    }

    private static float[] Click(int totalSamples, int at)
    {
        var data = new float[totalSamples];
        if (at >= 0) { data[at] = 1f; }
        return data;
    }

    [Fact]
    public async Task A_device_that_delivers_late_in_a_burst_is_not_shifted_later()
    {
        var mic = new FakeLease(16_000, "mic");
        var system = new FakeLease(16_000, "speakers");
        var lease = await new CombinedAudioSource(new FakeSource(mic), new FakeSource(system), new CombinedAudioOptions { MaxSkew = TimeSpan.FromSeconds(3) }).OpenAsync(null, default);

        // Both devices heard the same click 0.5 s in. The system side delivers in real time; the microphone
        // is stalled for 600 ms (start-up, busy CPU) and then delivers everything it captured at once.
        for (var i = 0; i < 10; i++)
        {
            system.PushSamples(Click(1_600, i == 5 ? 0 : -1));
            await Task.Delay(100);
        }

        for (var i = 0; i < 10; i++)
        {
            mic.PushSamples(Click(1_600, i == 5 ? 0 : -1));
        }

        await lease.DisposeAsync();
        var frames = await ReadAllAsync(lease);
        Assert.Equal(ClickIndex(frames, 1), ClickIndex(frames, 0));
        Assert.Equal(8_000, ClickIndex(frames, 1));
    }

    [Fact]
    public async Task A_device_opened_later_starts_later_on_the_shared_clock()
    {
        var mic = new FakeLease(16_000, "mic");
        var system = new FakeLease(16_000, "speakers");
        var lease = await new CombinedAudioSource(new FakeSource(mic), new FakeSource(system, openDelayMs: 300)).OpenAsync(null, default);

        // Each device hears a click 100 ms after it starts. The system device was opened 300 ms after the microphone.
        mic.PushSamples(Click(8_000, 1_600));
        system.PushSamples(Click(8_000, 1_600));
        await lease.DisposeAsync();
        var frames = await ReadAllAsync(lease);
        var micClick = ClickIndex(frames, 0);
        var systemClick = ClickIndex(frames, 1);
        Assert.Equal(1_600, micClick);
        Assert.InRange(systemClick - micClick, 4_000, 5_600); // 300 ms, allowing scheduler slack
    }
}
