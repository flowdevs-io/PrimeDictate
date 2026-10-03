using System.Text;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Coordination;
using PrimeDictate.Core.Providers;

namespace PrimeDictate.Core.Dictation;

/// <summary>Wake phrase matching ported from the WPF app, including "ok"/"okay" and "thanks"/"thank you" variants.</summary>
public static class WakePhrase
{
    public const string Default = "okay computer";

    public static string Normalize(string? phrase)
    {
        var collapsed = string.Join(' ', (phrase ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? Default : collapsed;
    }

    public static bool Matches(string transcript, string phrase)
    {
        var text = NormalizeForMatch(transcript);
        var target = NormalizeForMatch(phrase);
        if (text.Length == 0 || target.Length == 0)
        {
            return false;
        }

        var altText = text.Replace("okay", "ok", StringComparison.Ordinal);
        var altTarget = target.Replace("okay", "ok", StringComparison.Ordinal);
        if (text.Contains(target, StringComparison.Ordinal) ||
            altText.Contains(target, StringComparison.Ordinal) ||
            altText.Contains(altTarget, StringComparison.Ordinal) ||
            text.Contains(altTarget, StringComparison.Ordinal))
        {
            return true;
        }

        var targetIsThanks = target is "thankyou" or "thanks" or "thank you" or "thank u";
        var textHasThanks = text.Contains("thankyou") || text.Contains("thanks") || text.Contains("thank you") || text.Contains("thank u");
        return targetIsThanks && textHasThanks;
    }

    private static string NormalizeForMatch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(ch);
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// Opt-in idle microphone watcher: a short rolling in-memory buffer is transcribed looking only for the wake phrase.
/// Consecutive attempts overlap so a multi-word phrase can span windows. Audio is never written to disk.
/// Registers as a <see cref="IMicrophoneConsumer"/> so a transcription session or a dictation takes the microphone
/// and this resumes afterwards.
/// </summary>
/// <remarks>
/// The listener keeps one microphone stream open for hours, so it watches that stream. A microphone that is recording
/// delivers audio every few milliseconds, silence included, but a stream can stop without any error (a wireless headset
/// that powers off, a driver reset). Without audio for <see cref="DefaultStallTimeout"/>, or when the stream ends, the
/// microphone is reopened; before this the listener kept checking its last two seconds and never heard the phrase again
/// until a dictation reopened the microphone.
/// </remarks>
public sealed class WakeWordListener : IMicrophoneConsumer, IAsyncDisposable
{
    /// <summary>No audio at all for this long means the stream died, not that the room is quiet.</summary>
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan MaxStallTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RollingBuffer = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SlideInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MinAudioForAttempt = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan[] OpenRetryDelays =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.5)];
    private const int SampleRate = 16_000;

    private readonly IAudioSource audioSource;
    private readonly Func<ReadOnlyMemory<float>, CancellationToken, ValueTask<string>> transcribe;
    private readonly TimeSpan stallTimeout;
    private readonly TimeSpan retryAfterFailure;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private bool enabled;
    private bool suspended;
    private string phrase = WakePhrase.Default;
    private string? deviceId;
    private double gain = 1.0;
    private Running? running;

    /// <summary>Reopenings in a row that brought no lasting audio; each one doubles the wait before the next.</summary>
    private int stalls;

    private int openFailureReported;
    private int retryPending;

    /// <param name="stallTimeout">No audio for this long reopens the microphone (<see cref="DefaultStallTimeout"/>).</param>
    /// <param name="retryAfterFailure">When the microphone would not open at all, how long until the next try (30 s).</param>
    public WakeWordListener(
        IAudioSource audioSource,
        Func<ReadOnlyMemory<float>, CancellationToken, ValueTask<string>> transcribe,
        TimeSpan? stallTimeout = null,
        TimeSpan? retryAfterFailure = null)
    {
        this.audioSource = audioSource;
        this.transcribe = transcribe;
        this.stallTimeout = stallTimeout ?? DefaultStallTimeout;
        this.retryAfterFailure = retryAfterFailure ?? TimeSpan.FromSeconds(30);
    }

    public string Name => "Wake word listening";

    /// <summary>The listener is only ever mid-attempt, never mid-utterance, so it can always be interrupted.</summary>
    public bool IsBusy => false;

    public bool IsActive
    {
        get
        {
            lock (this.sync)
            {
                return this.enabled;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (this.sync)
            {
                return this.running is not null;
            }
        }
    }

    /// <summary>Raised once per detection, off the listen loop. The loop has already released the microphone.</summary>
    public event Action? WakeDetected;

    public event Action<string>? Notice;

    /// <summary>Raised whenever <see cref="HasFailed"/> changes, so the tray can follow it.</summary>
    public event Action? FailedChanged;

    /// <summary>True after the listener gave up (the microphone would not open, no model) until it next starts listening or is disabled. The tray shows it as "Wake listening failed".</summary>
    public bool HasFailed => Volatile.Read(ref this.failed) != 0;

    private int failed;
    private int transcribeFailureReported;

    private void SetFailed(bool value)
    {
        if (Interlocked.Exchange(ref this.failed, value ? 1 : 0) != (value ? 1 : 0))
        {
            this.FailedChanged?.Invoke();
        }
    }

    public void Configure(bool enabled, string? wakePhrase, string? inputDeviceId, double inputGain)
    {
        lock (this.sync)
        {
            this.enabled = enabled;
            this.phrase = WakePhrase.Normalize(wakePhrase);
            this.deviceId = string.IsNullOrWhiteSpace(inputDeviceId) ? null : inputDeviceId;
            this.gain = DictationOptions.NormalizeGain(inputGain);
        }

        if (!enabled)
        {
            // Turned off: nothing is failing any more.
            this.SetFailed(false);
        }

        Volatile.Write(ref this.transcribeFailureReported, 0);
    }

    /// <summary>The listener cannot work (for example no speech model): stops it from running, marks it failed and says why once.</summary>
    public void Disable(string reason)
    {
        lock (this.sync)
        {
            this.enabled = false;
        }

        this.SetFailed(true);
        this.Notice?.Invoke(reason);
    }

    /// <summary>Starts listening when enabled and not suspended. Safe to call repeatedly.</summary>
    public async Task EnsureRunningAsync()
    {
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await this.StartLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await this.StopLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask SuspendAsync(string reason, CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (this.sync)
            {
                this.suspended = true;
            }

            await this.StopLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask ResumeAsync(CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (this.sync)
            {
                this.suspended = false;
            }

            await this.StartLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (this.sync)
        {
            this.enabled = false;
        }

        await this.StopAsync().ConfigureAwait(false);
    }

    private async Task StartLockedAsync()
    {
        bool wanted;
        string? device;
        lock (this.sync)
        {
            wanted = this.enabled && !this.suspended && this.running is null;
            device = this.deviceId;
        }

        if (!wanted)
        {
            return;
        }

        IAudioCaptureLease? lease = null;
        for (var attempt = 0; lease is null; attempt++)
        {
            try
            {
                lease = await this.audioSource.OpenAsync(device, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // AudioSourceException is the usual failure, but a driver can throw others; any of them escaping would
                // leave the listener off with nothing saying so (and break the resume after a dictation).
                if (attempt >= OpenRetryDelays.Length)
                {
                    // Said once, not again on each later retry while the microphone stays unavailable.
                    if (Interlocked.Exchange(ref this.openFailureReported, 1) == 0)
                    {
                        Diagnostics.AppLog.Error("wake-word", $"Stopped listening: the microphone did not open after {attempt + 1} tries: {ex.Message}");
                        this.Notice?.Invoke($"Wake word listening could not open the microphone: {ex.Message}");
                    }

                    this.SetFailed(true);
                    this.RetryLater();
                    return;
                }

                // Common right after another user of the device releases it.
                await Task.Delay(OpenRetryDelays[attempt]).ConfigureAwait(false);
            }
        }

        this.SetFailed(false);
        if (Interlocked.Exchange(ref this.openFailureReported, 0) == 1)
        {
            Diagnostics.AppLog.Event("wake-word", "The microphone opened again; listening for the wake phrase.");
        }

        var run = new Running(lease);
        run.Loop = Task.Run(() => this.ListenAsync(run));
        lock (this.sync)
        {
            this.running = run;
        }
    }

    private async Task StopLockedAsync()
    {
        Running? run;
        lock (this.sync)
        {
            run = this.running;
            this.running = null;
        }

        if (run is null)
        {
            return;
        }

        run.Cts.Cancel();
        await run.Lease.DisposeAsync().ConfigureAwait(false);
        try
        {
            await run.Loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ListenAsync(Running run)
    {
        var token = run.Cts.Token;
        var buffer = new List<float>(SampleRate * 3);
        var bufferLock = new object();
        var maxSamples = (int)(RollingBuffer.TotalSeconds * SampleRate);
        var reader = Task.Run(async () =>
        {
            var resampler = new StreamingResampler(run.Lease.Format.SampleRate, SampleRate);
            try
            {
                await foreach (var frame in run.Lease.ReadFramesAsync(token).ConfigureAwait(false))
                {
                    var samples = resampler.Process(AudioConversion.DownmixToMono(frame.Samples.Span, frame.Format.Channels));
                    double g;
                    lock (this.sync)
                    {
                        g = this.gain;
                    }

                    lock (bufferLock)
                    {
                        foreach (var s in samples)
                        {
                            buffer.Add(Math.Clamp((float)(s * g), -1f, 1f));
                        }

                        if (buffer.Count > maxSamples)
                        {
                            buffer.RemoveRange(0, buffer.Count - maxSamples);
                        }

                        run.Heard(samples.Length);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or AudioSourceException or ObjectDisposedException)
            {
                // The stream is over. Unless this run is being stopped, the loop below notices and reopens the microphone.
            }
        });

        var nextSlide = DateTime.MinValue;
        var transcribedAt = -1L;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
                if (this.StreamProblem(run, reader) is { } problem)
                {
                    // Off this loop: reopening stops this run and waits for the loop to finish.
                    _ = Task.Run(() => this.ReopenAsync(run, problem));
                    return;
                }

                var now = DateTime.UtcNow;
                if (now < nextSlide)
                {
                    continue;
                }

                float[] snapshot;
                long heard;
                lock (bufferLock)
                {
                    snapshot = [.. buffer];
                    heard = run.Samples;
                }

                // Only new audio is worth a look: the window repeats only when nothing arrived since the last attempt.
                if (heard == transcribedAt ||
                    snapshot.Length < MinAudioForAttempt.TotalSeconds * SampleRate ||
                    !SpeechActivityTracker.ContainsLikelySpeech(snapshot))
                {
                    continue;
                }

                transcribedAt = heard;

                // Advance from the attempt start so recognition latency does not shrink the overlap between windows.
                nextSlide = now + SlideInterval;
                string transcript;
                try
                {
                    transcript = await this.transcribe(snapshot, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Said once, not on every stretch of speech; reset by the next success or reconfiguration.
                    if (Interlocked.Exchange(ref this.transcribeFailureReported, 1) == 0)
                    {
                        this.Notice?.Invoke($"Wake word transcription failed: {ex.Message}");
                    }

                    continue;
                }

                Volatile.Write(ref this.transcribeFailureReported, 0);

                string current;
                lock (this.sync)
                {
                    current = this.phrase;
                }

                if (WakePhrase.Matches(transcript, current))
                {
                    Diagnostics.AppLog.Event("wake-word", "Wake phrase heard; starting dictation.");

                    // Release the microphone before dictation opens it, then hand off without blocking this loop.
                    _ = Task.Run(async () =>
                    {
                        await this.StopAsync().ConfigureAwait(false);
                        this.WakeDetected?.Invoke();
                    });
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await reader.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How long without audio counts as a dead stream: <paramref name="first"/>, doubled for each reopening in a row that
    /// brought no lasting audio (a headset left switched off), up to a minute.
    /// </summary>
    internal static TimeSpan StallTimeoutAfter(int stalls, TimeSpan first)
    {
        var cap = first > MaxStallTimeout ? first : MaxStallTimeout;
        var limit = first;
        for (var i = 0; i < stalls && limit < cap; i++)
        {
            limit *= 2;
        }

        return limit < cap ? limit : cap;
    }

    /// <summary>Why the run's stream looks dead, or null while audio arrives. Also notices when a reopened stream is healthy again.</summary>
    private string? StreamProblem(Running run, Task reader)
    {
        var stallsNow = Volatile.Read(ref this.stalls);
        var quiet = run.SinceLastAudio;
        var limit = StallTimeoutAfter(stallsNow, this.stallTimeout);

        // An ended stream is reopened at once the first time; a device whose stream keeps ending waits like a silent one.
        if (reader.IsCompleted && (stallsNow == 0 || quiet >= limit))
        {
            return "the microphone stream ended";
        }

        if (quiet >= limit)
        {
            return $"no audio from the microphone for {quiet.TotalSeconds:0} s";
        }

        if (stallsNow > 0 && run.Samples >= this.stallTimeout.TotalSeconds * SampleRate && Interlocked.Exchange(ref this.stalls, 0) > 0)
        {
            Diagnostics.AppLog.Event("wake-word", "Microphone audio is arriving again; listening for the wake phrase.");
        }

        return null;
    }

    /// <summary>Reopens the microphone for a run whose stream died, unless that run was stopped or replaced meanwhile.</summary>
    private async Task ReopenAsync(Running dead, string problem)
    {
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (this.sync)
            {
                if (!ReferenceEquals(this.running, dead))
                {
                    return;
                }
            }

            // Said once per outage, not on every retry while a headset stays off.
            if (Interlocked.Increment(ref this.stalls) == 1)
            {
                Diagnostics.AppLog.Event("wake-word", $"Reopening the microphone: {problem}.", Diagnostics.ActivityLevel.Warning);
            }

            await this.StopLockedAsync().ConfigureAwait(false);
            await this.StartLockedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Closing the dead stream threw. The listener is stopped by now, so say so instead of looking alive, and try later.
            Diagnostics.AppLog.Fault("wake-word", ex);
            this.SetFailed(true);
            this.Notice?.Invoke($"Wake word listening stopped: {ex.Message}");
            this.RetryLater();
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>
    /// After the microphone would not open (or the dead stream would not close): try again later, so a microphone that comes
    /// back brings the wake word back without a dictation or a Settings change. One retry is pending at a time; starting
    /// does nothing while the wake word is off, suspended or already running.
    /// </summary>
    private void RetryLater()
    {
        if (Interlocked.Exchange(ref this.retryPending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(this.retryAfterFailure).ConfigureAwait(false);
            Volatile.Write(ref this.retryPending, 0);
            await this.EnsureRunningAsync().ConfigureAwait(false);
        });
    }

    private sealed class Running(IAudioCaptureLease lease)
    {
        private long lastAudio = Environment.TickCount64;
        private long samples;

        public IAudioCaptureLease Lease { get; } = lease;

        public CancellationTokenSource Cts { get; } = new();

        public Task Loop { get; set; } = Task.CompletedTask;

        /// <summary>16 kHz samples received since this run opened the microphone.</summary>
        public long Samples => Interlocked.Read(ref this.samples);

        /// <summary>Time since audio last arrived, or since the microphone opened when none has yet.</summary>
        public TimeSpan SinceLastAudio => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref this.lastAudio));

        public void Heard(int count)
        {
            Interlocked.Add(ref this.samples, count);
            Interlocked.Exchange(ref this.lastAudio, Environment.TickCount64);
        }
    }
}
