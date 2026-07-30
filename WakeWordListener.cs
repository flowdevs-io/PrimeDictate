using System.Text;

namespace PrimeDictate;

/// <summary>
/// Opt-in idle mic watcher: short rolling in-memory PCM buffer transcribed looking only for the
/// wake phrase. Consecutive attempts overlap so a multi-word phrase can span slides. Audio is
/// never written to disk.
/// </summary>
internal sealed class WakeWordListener : IAsyncDisposable
{
    /// <summary>How much recent PCM to retain and send to STT each attempt (~2s loop).</summary>
    private static readonly TimeSpan RollingBufferDuration = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Time between transcription attempts while speech is present. Kept below
    /// <see cref="RollingBufferDuration"/> so consecutive windows overlap for phrases like
    /// "okay computer".
    /// </summary>
    private static readonly TimeSpan SlideInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MinAudioForAttempt = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan[] MicStartRetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1_000),
        TimeSpan.FromMilliseconds(1_500),
        TimeSpan.FromMilliseconds(2_500),
    ];
    private const double MinSpeechRmsThreshold = 0.0018;

    private readonly DefaultMicrophoneRecorder recorder = new();
    private readonly Func<PcmAudioBuffer, CancellationToken, ValueTask<string>> transcribeAsync;
    private readonly object sync = new();

    private bool enabled;
    private string wakePhrase = AppSettings.DefaultWakeWordPhrase;
    private CancellationTokenSource? loopCts;
    private Task? loopTask;
    private int detectGate;

    public WakeWordListener(Func<PcmAudioBuffer, CancellationToken, ValueTask<string>> transcribeAsync)
    {
        this.transcribeAsync = transcribeAsync ?? throw new ArgumentNullException(nameof(transcribeAsync));
    }

    public event Action? WakeDetected;

    public bool IsRunning
    {
        get
        {
            lock (this.sync)
            {
                return this.loopTask is { IsCompleted: false };
            }
        }
    }

    public void ApplyConfiguration(
        bool enableWakeWord,
        string? wakeWordPhrase,
        string? selectedInputDeviceId,
        double inputGainMultiplier)
    {
        var phrase = NormalizePhrase(wakeWordPhrase);
        lock (this.sync)
        {
            this.enabled = enableWakeWord;
            this.wakePhrase = phrase;
        }

        if (!this.recorder.IsRecording)
        {
            this.recorder.UpdateInputDevice(selectedInputDeviceId);
            this.recorder.UpdateInputGain(inputGainMultiplier);
        }
    }

    /// <summary>
    /// Opens the shared-mode mic and starts the listen loop, retrying briefly on open failure
    /// (common right after exclusive-mode dictation releases the device).
    /// </summary>
    /// <param name="stillWanted">
    /// Optional gate checked between retries; when it returns false, start is abandoned without error.
    /// </param>
    /// <returns>
    /// true when the listen loop is running; false when abandoned because wake is no longer wanted.
    /// </returns>
    public async Task<bool> StartAsync(Func<bool>? stillWanted = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (stillWanted is not null && !stillWanted())
            {
                return false;
            }

            try
            {
                return this.TryStartOnce();
            }
            catch (Exception ex)
            {
                if (attempt >= MicStartRetryDelays.Length)
                {
                    AppLog.Error($"Wake word listener failed to start after {attempt + 1} attempts: {ex.Message}");
                    throw;
                }

                AppLog.Info(
                    $"Wake word mic start failed (attempt {attempt + 1}/{MicStartRetryDelays.Length + 1}): {ex.Message}. Retrying...");
                await Task.Delay(MicStartRetryDelays[attempt]).ConfigureAwait(false);

                lock (this.sync)
                {
                    if (!this.enabled)
                    {
                        return false;
                    }

                    if (this.loopTask is { IsCompleted: false })
                    {
                        return true;
                    }
                }
            }
        }
    }

    /// <returns>true when the listen loop is running after this call.</returns>
    private bool TryStartOnce()
    {
        lock (this.sync)
        {
            if (this.loopTask is { IsCompleted: false })
            {
                return true;
            }

            if (!this.enabled)
            {
                return false;
            }

            if (!this.recorder.IsRecording)
            {
                this.recorder.Start(exclusiveMode: false);
            }

            this.loopCts = new CancellationTokenSource();
            var token = this.loopCts.Token;
            this.loopTask = Task.Run(() => this.ListenLoopAsync(token), CancellationToken.None);
            AppLog.Info(
                $"Wake word listener started (phrase \"{this.wakePhrase}\", " +
                $"rolling ~{RollingBufferDuration.TotalSeconds:0.#}s buffer, " +
                $"~{SlideInterval.TotalMilliseconds:0}ms slide).");
            return true;
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (this.sync)
        {
            cts = this.loopCts;
            loop = this.loopTask;
            this.loopCts = null;
            this.loopTask = null;
        }

        if (cts is not null)
        {
            cts.Cancel();
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts?.Dispose();

        if (this.recorder.IsRecording)
        {
            _ = await this.recorder.StopAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
        this.recorder.Dispose();
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        var nextSlideAfterUtc = DateTime.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Keep a bounded rolling ring of recent PCM; do not clear between slides.
            this.recorder.TrimCapturedBuffer(RollingBufferDuration);

            var nowUtc = DateTime.UtcNow;
            if (nowUtc < nextSlideAfterUtc)
            {
                continue;
            }

            if (!this.recorder.TryGetPcm16KhzMonoSnapshot(out var snap, out _, RollingBufferDuration) ||
                snap.IsEmpty ||
                snap.Duration < MinAudioForAttempt ||
                !ContainsLikelySpeech(snap))
            {
                continue;
            }

            // Advance the slide clock from attempt start so STT latency does not stretch the hop
            // and shrink effective overlap between consecutive windows.
            nextSlideAfterUtc = nowUtc + SlideInterval;

            string transcript;
            try
            {
                transcript = await this.transcribeAsync(snap, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Wake word transcription failed: {ex.Message}");
                continue;
            }

            string phrase;
            lock (this.sync)
            {
                phrase = this.wakePhrase;
            }

            if (!MatchesWakePhrase(transcript, phrase))
            {
                continue;
            }

            if (Interlocked.CompareExchange(ref this.detectGate, 1, 0) != 0)
            {
                continue;
            }

            try
            {
                AppLog.Info($"Wake phrase detected: \"{phrase}\".");
                // Release the mic before dictation starts (exclusive mode especially).
                if (this.recorder.IsRecording)
                {
                    _ = await this.recorder.StopAsync().ConfigureAwait(false);
                }

                this.WakeDetected?.Invoke();
            }
            finally
            {
                Interlocked.Exchange(ref this.detectGate, 0);
            }

            break;
        }
    }

    private static string NormalizePhrase(string? phrase)
    {
        var normalized = CollapseWhitespace(phrase ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? AppSettings.DefaultWakeWordPhrase
            : normalized;
    }

    private static bool MatchesWakePhrase(string transcript, string phrase)
    {
        var text = NormalizeForMatch(transcript);
        var target = NormalizeForMatch(phrase);
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(target))
        {
            return false;
        }

        if (text.Contains(target, StringComparison.Ordinal))
        {
            return true;
        }

        var altTarget = target.Replace("okay", "ok", StringComparison.Ordinal);
        var altText = text.Replace("okay", "ok", StringComparison.Ordinal);
        return altText.Contains(target, StringComparison.Ordinal) ||
            altText.Contains(altTarget, StringComparison.Ordinal) ||
            text.Contains(altTarget, StringComparison.Ordinal);
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

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool ContainsLikelySpeech(PcmAudioBuffer audio)
    {
        var bytes = audio.Pcm16KhzMono;
        if (bytes.Length < 4)
        {
            return false;
        }

        const int frameSampleCount = 1_600;
        const int requiredSpeechFrames = 2;
        var sampleCount = bytes.Length / 2;
        var speechFrames = 0;
        for (var frameStart = 0; frameStart < sampleCount; frameStart += frameSampleCount)
        {
            var frameLength = Math.Min(frameSampleCount, sampleCount - frameStart);
            if (frameLength <= 0)
            {
                continue;
            }

            double sumSquares = 0;
            var byteStart = frameStart * 2;
            for (var i = 0; i < frameLength; i++)
            {
                var sample = BitConverter.ToInt16(bytes, byteStart + (i * 2));
                var normalized = sample / 32768.0;
                sumSquares += normalized * normalized;
            }

            var rms = Math.Sqrt(sumSquares / frameLength);
            if (rms < MinSpeechRmsThreshold)
            {
                continue;
            }

            speechFrames++;
            if (speechFrames >= requiredSpeechFrames)
            {
                return true;
            }
        }

        return false;
    }
}
