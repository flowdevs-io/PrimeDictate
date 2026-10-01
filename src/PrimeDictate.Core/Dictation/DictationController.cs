using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Coordination;
using PrimeDictate.Core.Providers;

namespace PrimeDictate.Core.Dictation;

public enum DictationState
{
    Idle = 0,
    Listening = 1,
    Processing = 2
}

public sealed record DictationCommit(
    Guid SessionId,
    DateTime TimestampUtc,
    string Transcript,
    DictationDeliveryStatus Status,
    string? TargetDisplayName,
    string? TargetAppName,
    string? TargetWindowTitle,
    string? Error,
    TimeSpan AudioDuration,
    bool EnterSent,
    string? OriginalTranscript = null,
    string? RewriteSystemPrompt = null);

/// <summary>
/// The dictation loop: hotkey toggles capture, a live preview goes to the overlay only, silence or a second
/// toggle commits, and one final transcript is typed after the foreground guard passes. Platform pieces (capture,
/// typing, the guard, the model) come in through interfaces so the loop runs and tests without an OS.
/// </summary>
/// <remarks>
/// Toggle, commit and discard all run under one gate, like the WPF controller, so a hotkey, a silence timer and a
/// voice command cannot interleave. Live text is never typed into the target and there is no clipboard path.
/// </remarks>
public sealed class DictationController : IAsyncDisposable
{
    public const string MicrophoneOwner = "Dictation";

    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(1_500);
    private static readonly TimeSpan PreviewMinAudio = TimeSpan.FromSeconds(0.55);
    private static readonly TimeSpan PreviewMaxAudio = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SpeechResumeWindow = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan ReaderDrainTimeout = TimeSpan.FromSeconds(2);
    private const int SampleRate = 16_000;

    private readonly IAudioSource audioSource;
    private readonly Func<ITranscriptionProvider?> providerSource;
    private readonly IForegroundTargetGuard guard;
    private readonly ITextInjector injector;
    private readonly MicrophoneCoordinator? microphone;
    private readonly IVoiceCommandProcessor voiceCommands;
    private readonly TimeProvider time;
    private readonly ITranscriptRewriter? rewriter;
    private readonly IVoiceShellCommandRunner? shellRunner;
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile DictationOptions options = new DictationOptions().Normalized();
    private Session? session;

    public DictationController(
        IAudioSource audioSource,
        Func<ITranscriptionProvider?> providerSource,
        IForegroundTargetGuard guard,
        ITextInjector injector,
        MicrophoneCoordinator? microphone = null,
        IVoiceCommandProcessor? voiceCommands = null,
        TimeProvider? time = null,
        ITranscriptRewriter? rewriter = null,
        IVoiceShellCommandRunner? shellRunner = null)
    {
        this.audioSource = audioSource;
        this.providerSource = providerSource;
        this.guard = guard;
        this.injector = injector;
        this.microphone = microphone;
        this.voiceCommands = voiceCommands ?? NoVoiceCommands.Instance;
        this.time = time ?? TimeProvider.System;
        this.rewriter = rewriter;
        this.shellRunner = shellRunner;
    }

    public event Action<DictationState>? StateChanged;

    /// <summary>Live hypothesis for the overlay. Never typed into the target.</summary>
    public event Action<Guid, string>? PartialTranscript;

    public event Action<DictationCommit>? Committed;

    public event Action<double>? LevelChanged;

    /// <summary>Something the user should see, such as no model installed or the microphone being busy.</summary>
    public event Action<string>? Notice;

    public event Action? HistoryRequested;

    public DictationState State { get; private set; }

    public bool IsRecording => Volatile.Read(ref this.session) is not null;

    /// <summary>How the microphone is open right now (Exclusive or Shared), or null when dictation is not recording.</summary>
    public MicAccessMode? ActiveMicAccess => Volatile.Read(ref this.session)?.Capture.AccessMode;

    public DictationOptions Options
    {
        get => this.options;
        set => this.options = (value ?? new DictationOptions()).Normalized();
    }

    /// <summary>Hotkey toggle: start when idle, commit when recording.</summary>
    public async Task ToggleAsync()
    {
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (this.session is null)
            {
                await this.StartCoreAsync().ConfigureAwait(false);
            }
            else
            {
                await this.StopCoreAsync("manual stop", commit: true).ConfigureAwait(false);
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>Emergency stop: discard the recording without transcribing or typing anything.</summary>
    public async Task DiscardAsync()
    {
        this.session?.RequestDiscard();
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (this.session is null)
            {
                return;
            }

            await this.StopCoreAsync("emergency stop", commit: false).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (this.session is not null)
            {
                this.session.RequestDiscard();
                await this.StopCoreAsync("shutdown", commit: false).ConfigureAwait(false);
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    private async Task StartCoreAsync()
    {
        var provider = this.providerSource();
        if (provider is null)
        {
            this.Notice?.Invoke("No speech model is selected. Choose one in Settings before dictating.");
            return;
        }

        var opts = this.options;
        IAsyncDisposable? micLease = null;
        IAudioCaptureLease? capture = null;
        try
        {
            if (this.microphone is not null)
            {
                micLease = await this.microphone.AcquireAsync(MicrophoneOwner, CancellationToken.None).ConfigureAwait(false);
            }

            capture = await this.audioSource.OpenAsync(
                opts.InputDeviceId,
                opts.ExclusiveMicAccess ? MicAccessMode.Exclusive : MicAccessMode.Shared,
                CancellationToken.None).ConfigureAwait(false);
            Diagnostics.AppLog.Event("dictation", $"Microphone opened ({capture.AccessMode.ToString().ToLowerInvariant()} access{(opts.ExclusiveMicAccess && capture.AccessMode == MicAccessMode.Shared ? ", exclusive was requested" : string.Empty)}).");
        }
        catch (Exception ex) when (ex is MicrophoneBusyException or AudioSourceException)
        {
            if (micLease is not null)
            {
                await micLease.DisposeAsync().ConfigureAwait(false);
            }

            Diagnostics.AppLog.Event("dictation", $"Dictation did not start: {ex.Message}");
            this.Notice?.Invoke(ex.Message);
            return;
        }

        var target = this.guard.IsAvailable ? this.guard.Capture() : null;
        var s = new Session(capture, micLease, provider, target, new SpeechActivityTracker(this.time), this.time.GetUtcNow().UtcDateTime);
        s.Tracker.LevelUpdated += level => this.LevelChanged?.Invoke(level);
        this.session = s;
        s.Reader = Task.Run(() => this.ReadLoopAsync(s));
        s.Preview = Task.Run(() => this.PreviewLoopAsync(s));
        this.SetState(DictationState.Listening);
    }

    private async Task StopCoreAsync(string reason, bool commit)
    {
        var s = this.session!;
        s.PreviewCts.Cancel();
        await this.StopCaptureAsync(s).ConfigureAwait(false);
        try
        {
            await s.Preview.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        this.session = null;
        try
        {
            if (!commit || s.DiscardRequested)
            {
                this.SetState(DictationState.Idle);
                return;
            }

            this.SetState(DictationState.Processing);
            await this.FinishAsync(s, reason).ConfigureAwait(false);
        }
        finally
        {
            if (this.State != DictationState.Idle)
            {
                this.SetState(DictationState.Idle);
            }

            s.PreviewCts.Dispose();
            s.ReaderCts.Dispose();
            if (s.MicLease is not null)
            {
                await s.MicLease.DisposeAsync().ConfigureAwait(false);
            }

            if (s.HistoryRequested)
            {
                this.HistoryRequested?.Invoke();
            }
        }
    }

    private static async Task StopReaderAsync(Session s)
    {
        try
        {
            await s.Reader.WaitAsync(ReaderDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            s.ReaderCts.Cancel();
            try
            {
                await s.Reader.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        catch (Exception)
        {
            // Device errors were already reported by the reader; the audio captured so far is still used.
        }
    }

    private async Task StopCaptureAsync(Session s)
    {
        // Disposing the lease ends the frame stream after buffered audio drains, so the last words are kept.
        await s.Capture.DisposeAsync().ConfigureAwait(false);
        await StopReaderAsync(s).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(Session s)
    {
        var resampler = new StreamingResampler(s.Capture.Format.SampleRate, SampleRate);
        var gain = (float)this.options.InputGain;
        try
        {
            await foreach (var frame in s.Capture.ReadFramesAsync(s.ReaderCts.Token).ConfigureAwait(false))
            {
                var mono = AudioConversion.DownmixToMono(frame.Samples.Span, frame.Format.Channels);
                var samples = resampler.Process(mono);
                if (gain != 1f)
                {
                    for (var i = 0; i < samples.Length; i++)
                    {
                        samples[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
                    }
                }

                s.Append(samples);
                s.Tracker.AddSamples(samples);
            }
        }
        catch (AudioSourceException ex)
        {
            this.Notice?.Invoke($"Microphone error: {ex.Message}");
            this.RunGated(() => this.session == s ? this.StopCoreAsync("device error", commit: true) : Task.CompletedTask);
        }
    }

    private async Task PreviewLoopAsync(Session s)
    {
        var token = s.PreviewCts.Token;
        var lastCount = 0;
        var nextPreview = DateTime.MinValue;
        while (true)
        {
            try
            {
                await Task.Delay(ProbeInterval, this.time, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var now = this.time.GetUtcNow().UtcDateTime;
            var lastSpeech = s.Tracker.LastSpeechUtc;
            if (lastSpeech is null || lastSpeech < s.StartedUtc)
            {
                continue;
            }

            if (now >= nextPreview)
            {
                nextPreview = now + PreviewInterval;
                var (snapshot, count) = s.Snapshot(PreviewMaxAudio);
                if (count != lastCount && snapshot.Length >= PreviewMinAudio.TotalSeconds * SampleRate)
                {
                    try
                    {
                        var text = await this.RecognizeAsync(s.Provider, snapshot, token).ConfigureAwait(false);
                        lastCount = count;
                        this.ApplyPreview(s, text);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        this.Notice?.Invoke($"Live preview failed: {ex.Message}");
                    }
                }
            }

            var silence = this.options.AutoCommitSilence;
            if (silence > TimeSpan.Zero &&
                s.Tracker.IsAutoCommitArmed(now - s.StartedUtc) &&
                now - lastSpeech.Value >= silence &&
                Interlocked.Exchange(ref s.AutoCommitRequested, 1) == 0)
            {
                this.RunGated(() => this.CommitAfterSilenceAsync(s));
            }
        }
    }

    private void ApplyPreview(Session s, string text)
    {
        var match = this.voiceCommands.Apply(text);
        if (!string.IsNullOrWhiteSpace(match.CleanedText) || match.CommitRequested || match.StopRequested || match.HistoryRequested)
        {
            this.PartialTranscript?.Invoke(s.Id, match.CleanedText);
        }

        if (match.HistoryRequested)
        {
            s.HistoryRequested = true;
        }

        if (match.StopRequested || match.HistoryRequested)
        {
            s.RequestDiscard();
            this.RunGated(() => this.session == s ? this.StopCoreAsync("voice stop command", commit: false) : Task.CompletedTask);
        }
        else if (match.CommitRequested && Interlocked.Exchange(ref s.VoiceCommitRequested, 1) == 0)
        {
            this.RunGated(() => this.session == s ? this.StopCoreAsync("voice command", commit: true) : Task.CompletedTask);
        }
    }

    private async Task CommitAfterSilenceAsync(Session s)
    {
        if (this.session != s)
        {
            return;
        }

        var now = this.time.GetUtcNow().UtcDateTime;
        var (recent, _) = s.Snapshot(SpeechResumeWindow);
        if ((s.Tracker.LastSpeechUtc is { } last && now - last < SpeechResumeWindow) ||
            SpeechActivityTracker.ContainsLikelySpeech(recent))
        {
            // Speech resumed while the commit was queued.
            Interlocked.Exchange(ref s.AutoCommitRequested, 0);
            return;
        }

        await this.StopCoreAsync("silence auto-commit", commit: true).ConfigureAwait(false);
    }

    private async Task FinishAsync(Session s, string reason)
    {
        var audio = s.SnapshotAll();
        var duration = TimeSpan.FromSeconds(audio.Length / (double)SampleRate);
        if (audio.Length == 0 || !s.Tracker.HasSpeechEvidence(audio))
        {
            return;
        }

        string transcript;
        try
        {
            var raw = await this.RecognizeAsync(s.Provider, audio, CancellationToken.None).ConfigureAwait(false);
            var spoken = TranscriptPostProcessor.RemoveTrailingSilenceArtifact(raw, reason.Contains("silence", StringComparison.Ordinal));
            // Shell commands are matched only here, in the final transcript of a dictation, and only when a runner exists.
            var match = this.shellRunner is null ? this.voiceCommands.Apply(spoken) : this.voiceCommands.ApplyFinal(spoken);
            if (match.Shell is { } shell && this.shellRunner is not null)
            {
                if (!await this.RunShellCommandAsync(s, shell, this.shellRunner, duration).ConfigureAwait(false))
                {
                    return;
                }
            }

            if (match.StopRequested)
            {
                return;
            }

            if (match.HistoryRequested)
            {
                s.HistoryRequested = true;
            }

            transcript = TranscriptReplacements.Apply(match.CleanedText, this.options.Replacements).Trim();
        }
        catch (Exception ex)
        {
            this.Notice?.Invoke($"Transcription failed: {ex.Message}");
            return;
        }

        if (s.DiscardRequested)
        {
            return;
        }

        if (transcript.Length == 0)
        {
            this.Notice?.Invoke("No text was recognized. Try raising input gain or choosing a different model.");
            return;
        }

        this.PartialTranscript?.Invoke(s.Id, transcript);
        string? original = null;
        string? rewritePrompt = null;
        if (this.rewriter is not null)
        {
            try
            {
                var rewrite = await this.rewriter.RewriteAsync(transcript, s.Target, CancellationToken.None).ConfigureAwait(false);
                rewritePrompt = rewrite.SystemPrompt;
                if (rewrite.Rewritten)
                {
                    original = transcript;
                    transcript = rewrite.Text;
                    this.PartialTranscript?.Invoke(s.Id, transcript);
                }
            }
            catch (Exception ex)
            {
                this.Notice?.Invoke($"Rewrite failed; typed as spoken: {ex.Message}");
            }

            if (s.DiscardRequested)
            {
                return;
            }
        }

        var result = TranscriptDelivery.Deliver(transcript, s.Target, this.guard, this.injector, this.options);
        if (result.Status != DictationDeliveryStatus.Injected)
        {
            // Why it was not typed (the guard's or the injector's reason); never the transcript itself.
            Diagnostics.AppLog.Event("dictation", $"Not typed ({result.Status}): {result.Error}");
        }

        this.Committed?.Invoke(new DictationCommit(
            s.Id,
            this.time.GetUtcNow().UtcDateTime,
            transcript,
            result.Status,
            s.Target?.DisplayName,
            s.Target?.AppName,
            s.Target?.WindowTitle,
            result.Error,
            duration,
            result.EnterSent,
            original,
            rewritePrompt));
    }

    private static readonly TimeSpan ShellTypeTargetWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShellTypePollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Runs the user's command for a matched phrase. The command string is the one saved in settings; nothing from the
    /// transcript reaches it. Returns true when the rest of the dictation should go on to be typed: only for
    /// <see cref="VoiceShellCommandCompletionBehavior.Continue"/> and only when words remain after the phrase was removed.
    /// </summary>
    private async Task<bool> RunShellCommandAsync(Session s, VoiceShellCommandInvocation invocation, IVoiceShellCommandRunner runner, TimeSpan duration)
    {
        var command = invocation.Command;
        var phrase = command.Phrase.Trim();
        var label = $"Voice command: {phrase}";
        DictationDeliveryStatus status;
        string? error = null;
        try
        {
            var result = runner.Run(command);
            Diagnostics.AppLog.Event("dictation", $"Voice command ran: \"{phrase}\" (pid {result.ProcessId?.ToString() ?? "unknown"}).");
            if (!string.IsNullOrWhiteSpace(invocation.TextToType))
            {
                if (!await this.WaitForShellTypeTargetAsync(s.Target).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Chained typing skipped because the command did not move focus away from the starting window.");
                }

                this.injector.TypeText(invocation.TextToType);
            }

            status = DictationDeliveryStatus.CommandExecuted;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            status = DictationDeliveryStatus.CommandFailed;
            error = ex.Message;
            Diagnostics.AppLog.Event("dictation", $"Voice command failed: \"{phrase}\": {ex.Message}");
            this.Notice?.Invoke($"Voice command \"{phrase}\" failed: {ex.Message}");
        }

        this.Committed?.Invoke(new DictationCommit(
            s.Id, this.time.GetUtcNow().UtcDateTime, label, status, "Command", null, null, error, duration, EnterSent: false));
        return status == DictationDeliveryStatus.CommandExecuted && command.CompletionBehavior == VoiceShellCommandCompletionBehavior.Continue;
    }

    private async Task<bool> WaitForShellTypeTargetAsync(IForegroundTarget? start)
    {
        if (start is null)
        {
            await Task.Delay(ShellTypePollInterval, this.time).ConfigureAwait(false);
            return true;
        }

        var deadline = this.time.GetUtcNow() + ShellTypeTargetWait;
        while (this.time.GetUtcNow() < deadline)
        {
            await Task.Delay(ShellTypePollInterval, this.time).ConfigureAwait(false);
            if (!start.IsStillForeground())
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<string> RecognizeAsync(ITranscriptionProvider provider, ReadOnlyMemory<float> samples, string? language, CancellationToken ct)
    {
        var window = provider.Capabilities.MaxWindow ?? TimeSpan.FromSeconds(30);
        var parts = new List<string>();
        foreach (var chunk in AudioChunker.Split(samples, window, SampleRate))
        {
            var segments = await provider.RecognizeWindowAsync(chunk, language, ct).ConfigureAwait(false);
            parts.AddRange(segments.Select(seg => seg.Text.Trim()).Where(t => t.Length > 0));
        }

        return string.Join(' ', parts);
    }

    private Task<string> RecognizeAsync(ITranscriptionProvider provider, ReadOnlyMemory<float> samples, CancellationToken ct) =>
        RecognizeAsync(provider, samples, this.options.Language, ct);

    /// <summary>Runs work under the toggle gate off the caller's thread, so hook and audio threads never block.</summary>
    private void RunGated(Func<Task> work) =>
        _ = Task.Run(async () =>
        {
            await this.gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.Notice?.Invoke($"Dictation error: {ex.Message}");
            }
            finally
            {
                this.gate.Release();
            }
        });

    private void SetState(DictationState state)
    {
        this.State = state;
        this.StateChanged?.Invoke(state);
    }

    private sealed class Session(
        IAudioCaptureLease capture,
        IAsyncDisposable? micLease,
        ITranscriptionProvider provider,
        IForegroundTarget? target,
        SpeechActivityTracker tracker,
        DateTime startedUtc)
    {
        private readonly object sync = new();
        private float[] buffer = new float[SampleRate * 30];
        private int count;
        private int discard;
        public int AutoCommitRequested;
        public int VoiceCommitRequested;

        public Guid Id { get; } = Guid.NewGuid();

        public IAudioCaptureLease Capture { get; } = capture;

        public IAsyncDisposable? MicLease { get; } = micLease;

        public ITranscriptionProvider Provider { get; } = provider;

        public IForegroundTarget? Target { get; } = target;

        public SpeechActivityTracker Tracker { get; } = tracker;

        public DateTime StartedUtc { get; } = startedUtc;

        public CancellationTokenSource PreviewCts { get; } = new();

        public CancellationTokenSource ReaderCts { get; } = new();

        public Task Reader { get; set; } = Task.CompletedTask;

        public Task Preview { get; set; } = Task.CompletedTask;

        public bool HistoryRequested { get; set; }

        public bool DiscardRequested => Volatile.Read(ref this.discard) == 1;

        public void RequestDiscard() => Volatile.Write(ref this.discard, 1);

        public void Append(float[] samples)
        {
            lock (this.sync)
            {
                if (this.count + samples.Length > this.buffer.Length)
                {
                    Array.Resize(ref this.buffer, Math.Max(this.buffer.Length * 2, this.count + samples.Length));
                }

                samples.CopyTo(this.buffer, this.count);
                this.count += samples.Length;
            }
        }

        public (float[] Samples, int TotalCount) Snapshot(TimeSpan maxDuration)
        {
            lock (this.sync)
            {
                var take = Math.Min(this.count, (int)(maxDuration.TotalSeconds * SampleRate));
                return (this.buffer.AsSpan(this.count - take, take).ToArray(), this.count);
            }
        }

        public float[] SnapshotAll()
        {
            lock (this.sync)
            {
                return this.buffer.AsSpan(0, this.count).ToArray();
            }
        }
    }
}
