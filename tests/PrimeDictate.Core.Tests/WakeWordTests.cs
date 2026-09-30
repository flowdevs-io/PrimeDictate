using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Coordination;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Providers;

namespace PrimeDictate.Core.Tests;

public sealed class WakeWordTests
{
    [Theory]
    [InlineData("Okay, computer.", "okay computer", true)]
    [InlineData("ok computer", "okay computer", true)]
    [InlineData("okay computer", "ok computer", true)]
    [InlineData("hey okay computer start", "okay computer", true)]
    [InlineData("computer okay", "okay computer", false)]
    [InlineData("Thanks!", "thank you", true)]
    [InlineData("thank u", "thanks", true)]
    [InlineData("", "okay computer", false)]
    [InlineData("okay computer", "", false)]
    public void Phrase_matching_follows_the_wpf_rules(string heard, string phrase, bool expected) =>
        Assert.Equal(expected, WakePhrase.Matches(heard, phrase));

    [Fact]
    public void Blank_phrase_falls_back_to_the_default()
    {
        Assert.Equal("okay computer", WakePhrase.Normalize("   "));
        Assert.Equal("hey bob", WakePhrase.Normalize("  hey \t bob "));
    }

    [Fact]
    public async Task Detection_releases_the_microphone_then_raises_once()
    {
        var source = new Source();
        await using var listener = new WakeWordListener(source, (_, _) => ValueTask.FromResult("okay computer"));
        var detected = new TaskCompletionSource();
        listener.WakeDetected += () => detected.TrySetResult();
        listener.Configure(true, "okay computer", null, 1.0);

        await listener.EnsureRunningAsync();
        Assert.True(listener.IsRunning);
        source.Lease!.Push(Tone(1.5));
        await detected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(source.Lease.Disposed);
        Assert.False(listener.IsRunning);
        Assert.True(listener.IsActive);
    }

    [Fact]
    public async Task Non_matching_speech_keeps_listening()
    {
        var source = new Source();
        var attempts = 0;
        await using var listener = new WakeWordListener(source, (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return ValueTask.FromResult("just talking");
        });
        var detected = false;
        listener.WakeDetected += () => detected = true;
        listener.Configure(true, "okay computer", null, 1.0);
        await listener.EnsureRunningAsync();
        source.Lease!.Push(Tone(1.5));
        await Task.Delay(1_800);
        Assert.True(attempts >= 1);
        Assert.False(detected);
        Assert.True(listener.IsRunning);
    }

    [Fact]
    public async Task Silence_never_reaches_the_recognizer()
    {
        var source = new Source();
        var attempts = 0;
        await using var listener = new WakeWordListener(source, (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return ValueTask.FromResult(string.Empty);
        });
        listener.Configure(true, "okay computer", null, 1.0);
        await listener.EnsureRunningAsync();
        source.Lease!.Push(new float[32_000]);
        await Task.Delay(1_200);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task Disabled_listener_never_opens_the_microphone()
    {
        var source = new Source();
        await using var listener = new WakeWordListener(source, (_, _) => ValueTask.FromResult(string.Empty));
        listener.Configure(false, null, null, 1.0);
        await listener.EnsureRunningAsync();
        Assert.Null(source.Lease);
        Assert.False(listener.IsActive);
    }

    [Fact]
    public async Task Coordinator_suspends_the_listener_for_a_session_and_resumes_it_after()
    {
        var source = new Source();
        var coordinator = new MicrophoneCoordinator();
        await using var listener = new WakeWordListener(source, (_, _) => ValueTask.FromResult(string.Empty));
        coordinator.Register(listener);
        listener.Configure(true, "okay computer", null, 1.0);
        await listener.EnsureRunningAsync();
        var first = source.Lease!;

        var lease = await coordinator.AcquireAsync("Dictation", default);
        Assert.False(listener.IsRunning);
        Assert.True(first.Disposed);
        await listener.EnsureRunningAsync();
        Assert.False(listener.IsRunning);

        await lease.DisposeAsync();
        Assert.True(listener.IsRunning);
        Assert.NotSame(first, source.Lease);
    }

    private static float[] Tone(double seconds)
    {
        var s = new float[(int)(16_000 * seconds)];
        for (var i = 0; i < s.Length; i++)
        {
            s[i] = 0.2f * MathF.Sin(i * 0.1f);
        }

        return s;
    }

    private sealed class Source : IAudioSource
    {
        public Lease? Lease { get; private set; }

        public ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AudioInputDevice>>([]);

        public ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAudioCaptureLease>(this.Lease = new Lease());
    }

    private sealed class Lease : IAudioCaptureLease
    {
        private readonly Channel<AudioFrame> frames = Channel.CreateUnbounded<AudioFrame>();
        private long offset;
        private long sequence;

        public AudioFormat Format => AudioFormat.SpeechTimeline;

        public string DeviceId => "fake";

        public string DeviceName => "Fake";

        public bool Disposed { get; private set; }

        public void Push(float[] samples)
        {
            this.frames.Writer.TryWrite(AudioFrame.CopyFrom(samples, this.Format, this.sequence++, this.offset));
            this.offset += samples.Length;
        }

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in this.frames.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
            }
        }

        public ValueTask PauseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            this.frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
