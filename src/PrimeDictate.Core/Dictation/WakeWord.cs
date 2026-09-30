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
public sealed class WakeWordListener : IMicrophoneConsumer, IAsyncDisposable
{
    private static readonly TimeSpan RollingBuffer = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SlideInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MinAudioForAttempt = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan[] OpenRetryDelays =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.5)];
    private const int SampleRate = 16_000;

    private readonly IAudioSource audioSource;
    private readonly Func<ReadOnlyMemory<float>, CancellationToken, ValueTask<string>> transcribe;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private bool enabled;
    private bool suspended;
    private string phrase = WakePhrase.Default;
    private string? deviceId;
    private double gain = 1.0;
    private Running? running;

    public WakeWordListener(IAudioSource audioSource, Func<ReadOnlyMemory<float>, CancellationToken, ValueTask<string>> transcribe)
    {
        this.audioSource = audioSource;
        this.transcribe = transcribe;
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

    public void Configure(bool enabled, string? wakePhrase, string? inputDeviceId, double inputGain)
    {
        lock (this.sync)
        {
            this.enabled = enabled;
            this.phrase = WakePhrase.Normalize(wakePhrase);
            this.deviceId = string.IsNullOrWhiteSpace(inputDeviceId) ? null : inputDeviceId;
            this.gain = DictationOptions.NormalizeGain(inputGain);
        }
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
            catch (AudioSourceException ex)
            {
                if (attempt >= OpenRetryDelays.Length)
                {
                    this.Notice?.Invoke($"Wake word listening could not open the microphone: {ex.Message}");
                    return;
                }

                // Common right after another user of the device releases it.
                await Task.Delay(OpenRetryDelays[attempt]).ConfigureAwait(false);
            }
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
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or AudioSourceException or ObjectDisposedException)
            {
            }
        });

        var nextSlide = DateTime.MinValue;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                if (now < nextSlide)
                {
                    continue;
                }

                float[] snapshot;
                lock (bufferLock)
                {
                    snapshot = [.. buffer];
                }

                if (snapshot.Length < MinAudioForAttempt.TotalSeconds * SampleRate || !SpeechActivityTracker.ContainsLikelySpeech(snapshot))
                {
                    continue;
                }

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
                    this.Notice?.Invoke($"Wake word transcription failed: {ex.Message}");
                    continue;
                }

                string current;
                lock (this.sync)
                {
                    current = this.phrase;
                }

                if (WakePhrase.Matches(transcript, current))
                {
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

    private sealed class Running(IAudioCaptureLease lease)
    {
        public IAudioCaptureLease Lease { get; } = lease;

        public CancellationTokenSource Cts { get; } = new();

        public Task Loop { get; set; } = Task.CompletedTask;
    }
}
