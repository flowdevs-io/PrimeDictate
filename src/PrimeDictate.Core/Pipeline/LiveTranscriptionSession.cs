using System.Threading.Channels;
using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Coordination;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Sessions;
using PrimeDictate.Core.Storage;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Core.Pipeline;

public sealed record LiveSessionOptions(
    TranscriptionSessionOptions Session,
    string Title,
    Func<Guid, string> MediaDirectoryFor,
    /// <summary>How often a still-growing utterance is re-recognized for the provisional preview.</summary>
    TimeSpan PreviewInterval,
    UtteranceDetectorOptions? Detector = null)
{
    public static readonly TimeSpan DefaultPreviewInterval = TimeSpan.FromSeconds(1.5);
}

/// <summary>
/// Live microphone transcription with a buffered recognizer ("Buffered Live"). Capture runs into a
/// bounded channel; inference consumes it. Each utterance is bounded (see
/// <see cref="UtteranceDetectorOptions.MaxUtterance"/>), so the recognizer never reprocesses the
/// session. Silence ends an utterance, never the session. Audio is written to the session's working
/// WAV as it arrives, so an interrupted session keeps everything captured so far.
/// </summary>
/// <remarks>
/// Native streaming providers will get their own session type. Transcript text stays in the
/// session document: no logging, no typing, no voice commands.
/// </remarks>
public sealed class LiveTranscriptionSession : IAsyncDisposable
{
    private abstract record Item;

    private sealed record Samples(float[] Data) : Item;

    private sealed record Flush : Item;

    private readonly IAudioSource audioSource;
    private readonly ITranscriptionProvider provider;
    private readonly MicrophoneCoordinator coordinator;
    private readonly ModelLeaseScheduler scheduler;
    private readonly ITranscriptionSessionStore store;
    private readonly LiveSessionOptions options;
    private readonly Func<DateTimeOffset> clock;
    private readonly Channel<Item> queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource stopSignal = new();
    private IAsyncDisposable? micLease;
    private IAudioCaptureLease? capture;
    private WavFileWriter? writer;
    private RecordedAudioTimeline? timeline;
    private Task? captureTask;
    private Task? inferenceTask;
    private Exception? failure;
    private bool discard;
    private int stopped;
    private long backlogSamples;

    public LiveTranscriptionSession(
        IAudioSource audioSource,
        ITranscriptionProvider provider,
        MicrophoneCoordinator coordinator,
        ModelLeaseScheduler scheduler,
        ITranscriptionSessionStore store,
        LiveSessionOptions options,
        Func<DateTimeOffset>? clock = null)
    {
        this.audioSource = audioSource;
        this.provider = provider;
        this.coordinator = coordinator;
        this.scheduler = scheduler;
        this.store = store;
        this.options = options;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public SessionDocumentHost? Host { get; private set; }

    public RecordedAudioTimeline? Timeline => this.timeline;

    /// <summary>Recorded audio so far. Pause adds nothing.</summary>
    public TimeSpan Elapsed => this.timeline?.RecordedDuration ?? TimeSpan.Zero;

    /// <summary>Audio waiting for inference; a growing value means the model is slower than real time.</summary>
    public TimeSpan Backlog => AudioFormat.SpeechTimeline.ToTime(Interlocked.Read(ref this.backlogSamples));

    /// <summary>Peak level of the last audio block, 0 to 1, for a level meter.</summary>
    public float Level { get; private set; }

    public event Action<float>? LevelChanged;

    /// <summary>Raised when the device stops delivering audio for a reason the user should see.</summary>
    public event Action<string>? Error;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var now = this.clock();
        var document = new TranscriptDocument
        {
            SessionId = Guid.NewGuid(),
            Title = this.options.Title,
            SourceType = TranscriptSourceType.Microphone,
            CreatedAt = now,
            UpdatedAt = now,
            Status = TranscriptSessionStatus.Created
        };
        this.Host = new SessionDocumentHost(document, this.store, this.clock);

        // Suspends wake word and idle dictation, and refuses if dictation is mid-utterance.
        this.micLease = await this.coordinator.AcquireAsync("Transcription", cancellationToken).ConfigureAwait(false);
        try
        {
            this.capture = await this.audioSource.OpenAsync(this.options.Session.InputDeviceId, cancellationToken).ConfigureAwait(false);
            var path = Path.Combine(this.options.MediaDirectoryFor(document.SessionId), "recording-16k-mono.wav");
            this.writer = new WavFileWriter(path);
            this.timeline = new RecordedAudioTimeline(AudioFormat.SpeechTimeline);
            var run = new RecognitionRunInfo(
                1,
                this.provider.ModelId,
                this.options.Session.AsrModelRevision,
                null,
                null,
                this.options.Session.Language,
                this.provider.Runtime.RuntimeName,
                this.provider.Runtime.RuntimeVersion,
                this.provider.Runtime.RequestedBackend,
                this.provider.Runtime.EffectiveBackend,
                now);
            this.Host.Modify(d => d with
            {
                Audio = [new AudioReference(AudioReferenceKind.Owned, path, null)],
                Media = new MediaMetadata(null, "microphone", null, this.capture.Format.SampleRate, this.capture.Format.Channels, null, null, null)
            });
            this.Host.SetStatus(TranscriptSessionStatus.Running);
            this.Host.Apply(new SessionStarted(document.SessionId, run));
            await this.Host.CheckpointAsync(cancellationToken).ConfigureAwait(false);
            this.timeline.BeginInterval(now);
            this.inferenceTask = Task.Run(this.InferenceLoopAsync, CancellationToken.None);
            this.captureTask = Task.Run(this.CaptureLoopAsync, CancellationToken.None);
        }
        catch
        {
            await this.ReleaseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Stops recording. No audio is captured while paused.</summary>
    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        var host = this.Host ?? throw new InvalidOperationException("The session has not started.");
        if (host.Document.Status != TranscriptSessionStatus.Running)
        {
            return;
        }

        await this.capture!.PauseAsync(cancellationToken).ConfigureAwait(false);
        lock (this.timeline!)
        {
            if (this.timeline.IsCapturing)
            {
                this.timeline.EndInterval(CaptureIntervalEnd.Paused);
            }
        }

        // Finalize whatever was being said before the pause.
        await this.queue.Writer.WriteAsync(new Flush(), cancellationToken).ConfigureAwait(false);
        host.SetStatus(TranscriptSessionStatus.Paused);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        var host = this.Host ?? throw new InvalidOperationException("The session has not started.");
        if (host.Document.Status != TranscriptSessionStatus.Paused)
        {
            return;
        }

        await this.capture!.ResumeAsync(cancellationToken).ConfigureAwait(false);
        lock (this.timeline!)
        {
            this.timeline.BeginInterval(this.clock());
        }

        host.SetStatus(TranscriptSessionStatus.Running);
    }

    /// <summary>
    /// Stops capture, drains queued audio, flushes the resampler and the last utterance, saves the
    /// final checkpoint, and only then reports Completed.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref this.stopped, 1) == 1)
        {
            return;
        }

        var host = this.Host ?? throw new InvalidOperationException("The session has not started.");
        if (host.Document.Status is TranscriptSessionStatus.Running or TranscriptSessionStatus.Paused)
        {
            host.SetStatus(TranscriptSessionStatus.Finalizing);
        }

        await this.StopCaptureAsync().ConfigureAwait(false);
        await this.captureTask!.ConfigureAwait(false);
        this.queue.Writer.TryComplete();
        await this.inferenceTask!.ConfigureAwait(false);

        this.writer?.Flush();
        var duration = this.Elapsed;
        if (this.failure is not null)
        {
            host.Apply(new SessionFailed(host.Document.SessionId, "capture-failed", this.failure.GetType().Name, Recoverable: true));
        }
        else
        {
            host.Apply(new SessionCompleted(host.Document.SessionId, duration));
        }

        host.Modify(d => d with { Duration = duration });
        await host.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        if (this.options.Session.AudioRetention == AudioRetention.TranscriptOnly && this.failure is null)
        {
            this.writer?.Dispose();
            this.writer = null;
            await this.store.DeleteOwnedAudioAsync(host.Document.SessionId, cancellationToken).ConfigureAwait(false);
            host.Modify(d => d with { Audio = [] });
            await host.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        }

        await this.ReleaseAsync().ConfigureAwait(false);
    }

    /// <summary>Stops immediately and marks the session canceled. Captured audio and text stay unless the caller deletes the session.</summary>
    public async Task DiscardAsync()
    {
        this.discard = true;
        if (Interlocked.Exchange(ref this.stopped, 1) == 1)
        {
            return;
        }

        await this.stopSignal.CancelAsync().ConfigureAwait(false);
        await this.StopCaptureAsync().ConfigureAwait(false);
        if (this.captureTask is not null)
        {
            await this.captureTask.ConfigureAwait(false);
        }

        this.queue.Writer.TryComplete();
        if (this.inferenceTask is not null)
        {
            await this.inferenceTask.ConfigureAwait(false);
        }

        this.writer?.Flush();
        if (this.Host is { } host)
        {
            if (TranscriptionSessionStateMachine.CanTransition(host.Document.Status, TranscriptSessionStatus.Canceled))
            {
                host.SetStatus(TranscriptSessionStatus.Canceled);
            }

            await host.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await this.ReleaseAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref this.stopped) == 0 && this.Host is not null)
        {
            await this.DiscardAsync().ConfigureAwait(false);
        }
        else
        {
            await this.ReleaseAsync().ConfigureAwait(false);
        }

        this.stopSignal.Dispose();
    }

    private async Task StopCaptureAsync()
    {
        var lease = Interlocked.Exchange(ref this.capture, null);
        if (lease is not null)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ReleaseAsync()
    {
        await this.StopCaptureAsync().ConfigureAwait(false);
        this.writer?.Dispose();
        this.writer = null;
        var mic = Interlocked.Exchange(ref this.micLease, null);
        if (mic is not null)
        {
            await mic.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task CaptureLoopAsync()
    {
        var lease = this.capture!;
        var resampler = new StreamingResampler(lease.Format.SampleRate, AudioFormat.SpeechTimeline.SampleRate);
        long expectedSequence = 0;
        try
        {
            await foreach (var frame in lease.ReadFramesAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (frame.SequenceNumber != expectedSequence)
                {
                    // Samples were lost upstream. Close the interval so playback and exports show a gap
                    // rather than fabricated continuity.
                    lock (this.timeline!)
                    {
                        if (this.timeline.IsCapturing)
                        {
                            this.timeline.EndInterval(CaptureIntervalEnd.Gap);
                            this.timeline.BeginInterval(this.clock());
                        }
                    }

                    await this.queue.Writer.WriteAsync(new Flush()).ConfigureAwait(false);
                }

                expectedSequence = frame.SequenceNumber + 1;
                var mono = AudioConversion.DownmixToMono(frame.Samples.Span, frame.Format.Channels);
                await this.PublishAsync(resampler.Process(mono)).ConfigureAwait(false);
            }

            await this.PublishAsync(resampler.Flush()).ConfigureAwait(false);
        }
        catch (AudioSourceException ex)
        {
            this.failure = ex;
            this.Error?.Invoke(ex.Message);
        }
        catch (IOException ex)
        {
            // Disk full or file lock. Keep the transcript so far and stop safely.
            this.failure = ex;
            this.Error?.Invoke("Recording stopped because the audio file could not be written.");
        }
        finally
        {
            lock (this.timeline!)
            {
                if (this.timeline.IsCapturing)
                {
                    this.timeline.EndInterval(this.failure is null ? CaptureIntervalEnd.Stopped : CaptureIntervalEnd.Gap);
                }
            }
        }
    }

    private async Task PublishAsync(float[] samples)
    {
        if (samples.Length == 0)
        {
            return;
        }

        this.writer!.Write(samples);
        lock (this.timeline!)
        {
            if (this.timeline.IsCapturing)
            {
                this.timeline.Append(samples.Length);
            }
        }

        var peak = 0f;
        foreach (var s in samples)
        {
            peak = Math.Max(peak, Math.Abs(s));
        }

        this.Level = peak;
        this.LevelChanged?.Invoke(peak);
        Interlocked.Add(ref this.backlogSamples, samples.Length);
        // A full queue makes capture wait here, and the capture lease's own queue absorbs the wait.
        // If that fills too, the lease skips sequence numbers and the loop above marks a gap. Nothing is
        // dropped silently.
        await this.queue.Writer.WriteAsync(new Samples(samples)).ConfigureAwait(false);
    }

    private async Task InferenceLoopAsync()
    {
        var host = this.Host!;
        var sessionId = host.Document.SessionId;
        var detector = new UtteranceDetector(this.options.Detector);
        var lastPreview = DateTimeOffset.MinValue;
        var previewRevision = new Dictionary<int, long>();
        var ct = this.stopSignal.Token;

        async Task RecognizeAsync(UtteranceEvent utterance, bool final)
        {
            IReadOnlyList<RecognizedSegment> recognized;
            using (await this.scheduler.AcquireAsync(this.provider.ModelId, ModelLeasePriority.Live, ct).ConfigureAwait(false))
            {
                recognized = await this.provider.RecognizeWindowAsync(utterance.Samples, this.options.Session.Language, ct).ConfigureAwait(false);
            }

            var revision = previewRevision.GetValueOrDefault(utterance.UtteranceIndex) + 1;
            previewRevision[utterance.UtteranceIndex] = revision;
            var duration = AudioFormat.SpeechTimeline.ToTime(utterance.Samples.Length);
            var segments = SegmentMapper.Map(recognized, $"u{utterance.UtteranceIndex}", utterance.StartSample, duration, 1, revision, final ? SegmentState.Final : SegmentState.Provisional).ToList();
            if (final && segments.Count == 0)
            {
                return;
            }

            // One segment per utterance keeps the preview replacing itself in place.
            if (segments.Count > 0)
            {
                var first = segments[0];
                var merged = first with
                {
                    Id = $"u{utterance.UtteranceIndex}",
                    Start = segments.Min(s => s.Start),
                    End = segments.Max(s => s.End),
                    RawText = string.Join(" ", segments.Select(s => s.RawText)),
                    Words = null
                };
                host.Apply(final ? new SegmentFinalized(sessionId, merged) : new SegmentUpserted(sessionId, merged));
                if (final)
                {
                    await host.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        async Task HandleAsync(IReadOnlyList<UtteranceEvent> events)
        {
            foreach (var e in events)
            {
                if (e.Kind == UtteranceEventKind.Ended)
                {
                    await RecognizeAsync(e, final: true).ConfigureAwait(false);
                }
                else if (this.clock() - lastPreview >= this.options.PreviewInterval && e.Samples.Length >= 16_000 * 0.8)
                {
                    lastPreview = this.clock();
                    await RecognizeAsync(e, final: false).ConfigureAwait(false);
                }
            }
        }

        try
        {
            await foreach (var item in this.queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                switch (item)
                {
                    case Samples s:
                        await HandleAsync(detector.Add(s.Data)).ConfigureAwait(false);
                        Interlocked.Add(ref this.backlogSamples, -s.Data.Length);
                        break;
                    case Flush:
                        await HandleAsync(detector.Flush()).ConfigureAwait(false);
                        break;
                }
            }

            // Stop: the channel is complete, so the last audio has been consumed. Finalize the open utterance.
            if (!this.discard)
            {
                await HandleAsync(detector.Flush()).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (this.discard)
        {
        }
    }
}
