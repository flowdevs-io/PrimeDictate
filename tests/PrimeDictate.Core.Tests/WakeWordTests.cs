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
    public void Disable_marks_failure_once_and_turning_the_wake_word_off_clears_it()
    {
        var listener = new WakeWordListener(new Source(), (_, _) => ValueTask.FromResult(string.Empty));
        var changes = 0;
        var notices = new List<string>();
        listener.FailedChanged += () => changes++;
        listener.Notice += notices.Add;
        listener.Configure(true, "okay computer", null, 1.0);

        listener.Disable("no model");
        listener.Disable("no model");
        Assert.True(listener.HasFailed);
        Assert.False(listener.IsActive);
        Assert.Equal(1, changes);

        listener.Configure(false, "okay computer", null, 1.0);
        Assert.False(listener.HasFailed);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task A_recovering_start_clears_the_failure_and_raises_the_change()
    {
        var source = new Source();
        await using var listener = new WakeWordListener(source, (_, _) => ValueTask.FromResult(string.Empty));
        var changes = 0;
        listener.FailedChanged += () => changes++;
        listener.Configure(true, "okay computer", null, 1.0);
        listener.Disable("no model");
        listener.Configure(true, "okay computer", null, 1.0);
        await listener.EnsureRunningAsync();
        Assert.False(listener.HasFailed);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task A_failing_recognizer_is_reported_once_not_every_window()
    {
        var source = new Source();
        var notices = 0;
        var attempts = 0;
        await using var listener = new WakeWordListener(source, (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("no model");
        });
        listener.Notice += _ => Interlocked.Increment(ref notices);
        listener.Configure(true, "okay computer", null, 1.0);
        await listener.EnsureRunningAsync();
        source.Lease!.Push(Tone(1.5));
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(350);
            source.Lease!.Push(Tone(0.35));
        }

        Assert.True(attempts >= 2);
        Assert.Equal(1, notices);
    }

    [Fact]
    public async Task The_same_audio_is_not_transcribed_twice()
    {
        var source = new Source();
        var attempts = 0;
        await using var listener = new WakeWordListener(source, (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return ValueTask.FromResult("just talking");
        });
        listener.Configure(true, "okay computer", null, 1.0);
        await listener.EnsureRunningAsync();
        source.Lease!.Push(Tone(1.5));
        await Task.Delay(2_500);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task A_microphone_that_stops_sending_audio_is_reopened()
    {
        var source = new Source();
        await using var listener = new WakeWordListener(source, (_, _) => ValueTask.FromResult("just talking"), TimeSpan.FromMilliseconds(400));
        listener.Configure(true, "okay computer", null, 1.0);
        await listener.EnsureRunningAsync();
        var first = source.Lease!;
        first.Push(Tone(1.0));

        await WaitUntil(() => source.Opens >= 2 && listener.IsRunning, TimeSpan.FromSeconds(10));
        Assert.True(first.Disposed);
        Assert.NotSame(first, source.Lease);
        Assert.False(listener.HasFailed);
    }

    [Fact]
    public async Task A_microphone_stream_that_ends_is_reopened()
    {
        var source = new Source();
        await using var listener = new WakeWordListener(source, (_, _) => ValueTask.FromResult("just talking"));
        listener.Configure(true, "okay computer", null, 1.0);
        await listener.EnsureRunningAsync();
        var first = source.Lease!;

        first.End();
        await WaitUntil(() => source.Opens >= 2 && listener.IsRunning, TimeSpan.FromSeconds(4));
        Assert.True(first.Disposed);
    }

    [Fact]
    public async Task The_wake_phrase_is_heard_again_after_the_microphone_was_reopened()
    {
        var source = new Source();
        await using var listener = new WakeWordListener(
            source,
            (samples, _) => ValueTask.FromResult(Peak(samples.Span) > 0.4f ? "okay computer" : "just talking"),
            TimeSpan.FromMilliseconds(400));
        var detected = new TaskCompletionSource();
        listener.WakeDetected += () => detected.TrySetResult();
        listener.Configure(true, "okay computer", null, 1.0);
        await listener.EnsureRunningAsync();

        // Some speech, then the stream goes silent for good (the device stopped delivering).
        source.Lease!.Push(Tone(1.0, 0.2f));
        await WaitUntil(() => source.Opens >= 2, TimeSpan.FromSeconds(10));

        // Keep the reopened stream fed (into whichever lease is current) until the phrase is heard.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!detected.Task.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "The wake phrase was not heard after reopening.");
            source.Lease!.Push(Tone(0.5, 0.5f));
            await Task.WhenAny(detected.Task, Task.Delay(250));
        }
    }

    [Fact]
    public async Task A_stream_that_keeps_ending_is_not_reopened_in_a_tight_loop()
    {
        var source = new Source { EndImmediately = true };
        await using var listener = new WakeWordListener(source, (_, _) => ValueTask.FromResult(string.Empty), TimeSpan.FromMilliseconds(500));
        listener.Configure(true, "okay computer", null, 1.0);
        await listener.EnsureRunningAsync();

        // The first end reopens at once; after that each reopen waits like a silent stream (1 s, then 2 s).
        await Task.Delay(2_500);
        Assert.InRange(source.Opens, 2, 4);
    }

    [Fact]
    public async Task A_microphone_that_would_not_open_is_tried_again_later()
    {
        var source = new Source { FailingOpens = 6 };
        var notices = 0;
        await using var listener = new WakeWordListener(source, (_, _) => ValueTask.FromResult(string.Empty), retryAfterFailure: TimeSpan.FromMilliseconds(200));
        listener.Notice += _ => Interlocked.Increment(ref notices);
        listener.Configure(true, "okay computer", null, 1.0);

        await listener.EnsureRunningAsync();
        Assert.True(listener.HasFailed);
        Assert.False(listener.IsRunning);

        await WaitUntil(() => listener.IsRunning, TimeSpan.FromSeconds(5));
        Assert.False(listener.HasFailed);
        Assert.Equal(1, notices);
    }

    [Fact]
    public void Reopening_waits_longer_each_time_the_microphone_stays_silent()
    {
        var first = WakeWordListener.DefaultStallTimeout;
        Assert.Equal(TimeSpan.FromSeconds(5), WakeWordListener.StallTimeoutAfter(0, first));
        Assert.Equal(TimeSpan.FromSeconds(10), WakeWordListener.StallTimeoutAfter(1, first));
        Assert.Equal(TimeSpan.FromSeconds(40), WakeWordListener.StallTimeoutAfter(3, first));
        Assert.Equal(TimeSpan.FromMinutes(1), WakeWordListener.StallTimeoutAfter(4, first));
        Assert.Equal(TimeSpan.FromMinutes(1), WakeWordListener.StallTimeoutAfter(1_000, first));
    }

    [Fact]
    public async Task An_unexpected_error_opening_the_microphone_is_retried()
    {
        var source = new Source { FailingOpens = 2 };
        await using var listener = new WakeWordListener(source, (_, _) => ValueTask.FromResult(string.Empty));
        listener.Configure(true, "okay computer", null, 1.0);

        await listener.EnsureRunningAsync();

        Assert.Equal(3, source.Attempts);
        Assert.Equal(1, source.Opens);
        Assert.True(listener.IsRunning);
        Assert.False(listener.HasFailed);
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

    private static float[] Tone(double seconds, float amplitude = 0.2f)
    {
        var s = new float[(int)(16_000 * seconds)];
        for (var i = 0; i < s.Length; i++)
        {
            s[i] = amplitude * MathF.Sin(i * 0.1f);
        }

        return s;
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var s in samples)
        {
            peak = Math.Max(peak, Math.Abs(s));
        }

        return peak;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the listener.");
            await Task.Delay(20);
        }
    }

    private sealed class Source : IAudioSource
    {
        private int attempts;
        private int opens;

        /// <summary>The latest lease handed out.</summary>
        public Lease? Lease { get; private set; }

        /// <summary>Open calls, including failed ones.</summary>
        public int Attempts => Volatile.Read(ref this.attempts);

        /// <summary>Leases handed out; counted after <see cref="Lease"/> is set.</summary>
        public int Opens => Volatile.Read(ref this.opens);

        /// <summary>The first this many opens throw an exception other than <see cref="AudioSourceException"/>.</summary>
        public int FailingOpens { get; init; }

        /// <summary>Every stream ends as soon as it is opened (a device that opens but never records).</summary>
        public bool EndImmediately { get; init; }

        public ValueTask<IReadOnlyList<AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AudioInputDevice>>([]);

        public ValueTask<IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref this.attempts) <= this.FailingOpens)
            {
                throw new InvalidOperationException("driver hiccup");
            }

            var lease = new Lease();
            if (this.EndImmediately)
            {
                lease.End();
            }

            this.Lease = lease;
            Interlocked.Increment(ref this.opens);
            return ValueTask.FromResult<IAudioCaptureLease>(lease);
        }
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

        /// <summary>The device stops the stream on its own (not disposed by the listener).</summary>
        public void End() => this.frames.Writer.TryComplete();

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
