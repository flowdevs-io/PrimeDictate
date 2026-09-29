using System.Diagnostics;
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
    UtteranceDetectorOptions? Detector = null,
    TranscriptSourceType Source = TranscriptSourceType.Microphone,
    /// <summary>Normalizes quiet audio before recognition (system audio and meetings only). The saved recording is never changed.</summary>
    bool AutoGain = true)
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

    private sealed record Samples(float[] Data, bool SyntheticSilence = false) : Item;

    private sealed record Flush : Item;

    private readonly IAudioSource audioSource;
    private readonly ITranscriptionProvider provider;
    private readonly MicrophoneCoordinator coordinator;
    private readonly ModelLeaseScheduler scheduler;
    private readonly ITranscriptionSessionStore store;
    private readonly LiveSessionOptions options;
    private readonly Func<DateTimeOffset> clock;
    // Unbounded on purpose: capture and the recording file must never wait for recognition. If the model is
    // slower than real time the audio queues here (about 64 KB per second) and Backlog reports it; nothing is
    // dropped and capture is never blocked. Recognition catches up when speech pauses or after Stop.
    private readonly Channel<Item> queue = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource stopSignal = new();
    private IAsyncDisposable? micLease;
    private IAudioCaptureLease? capture;
    private WavFileWriter? writer;
    private bool keepStereo;
    private RecordedAudioTimeline? timeline;
    private Task? captureTask;
    private Task? inferenceTask;
    private Exception? failure;
    private bool discard;
    private int stopped;
    private long backlogSamples;
    private long samplesSinceFlush;
    private AutoGain? gainLeft;
    private AutoGain? gainRight;
    private long streamedSamples;
    private bool frameSynthetic;

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
            SourceType = this.options.Source,
            CreatedAt = now,
            UpdatedAt = now,
            Status = TranscriptSessionStatus.Created
        };
        this.Host = new SessionDocumentHost(document, this.store, this.clock);

        // Suspends wake word and idle dictation, and refuses if dictation is mid-utterance.
        // System-audio-only capture never touches the microphone, so it must not pause the wake word
        // or dictation. Take the microphone lease only for sources that use the microphone.
        if (this.options.Source != TranscriptSourceType.SystemAudio)
        {
            this.micLease = await this.coordinator.AcquireAsync("Transcription", cancellationToken).ConfigureAwait(false);
        }

        try
        {
            this.capture = await this.audioSource.OpenAsync(this.options.Session.InputDeviceId, cancellationToken).ConfigureAwait(false);
            // A meeting keeps both channels (left local, right remote) so playback and speaker detection
            // can use the local-versus-remote split. Recognition always gets the mono mix.
            this.keepStereo = this.options.Source == TranscriptSourceType.Meeting && this.capture.Format.Channels == 2;
            var path = Path.Combine(this.options.MediaDirectoryFor(document.SessionId), this.keepStereo ? "recording-16k-stereo.wav" : "recording-16k-mono.wav");
            this.writer = new WavFileWriter(path, 16_000, this.keepStereo ? 2 : 1);
            this.timeline = new RecordedAudioTimeline(AudioFormat.SpeechTimeline);
            var run = new RecognitionRunInfo(
                1,
                this.provider.ModelId,
                this.options.Session.AsrModelRevision ?? this.provider.ModelInfo.AsrRevision,
                this.provider.ModelInfo.DiarizerModelId,
                this.provider.ModelInfo.DiarizerRevision,
                this.options.Session.Language,
                this.provider.Runtime.RuntimeName,
                this.provider.Runtime.RuntimeVersion,
                this.provider.Runtime.RequestedBackend,
                this.provider.Runtime.EffectiveBackend,
                now);
            this.Host.Modify(d => d with
            {
                Audio = [new AudioReference(AudioReferenceKind.Owned, path, null)],
                Media = new MediaMetadata(null, this.options.Source switch
                {
                    TranscriptSourceType.SystemAudio => "system-audio",
                    TranscriptSourceType.Meeting => "meeting",
                    _ => "microphone"
                }, null, this.capture.Format.SampleRate, this.capture.Format.Channels, null,
                this.keepStereo ? "left=microphone, right=system audio" : null, null)
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
            host.Apply(new SessionFailed(host.Document.SessionId, "live-failed", $"{this.failure.GetType().Name}: {this.failure.Message}", Recoverable: true));
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
        var rightResampler = this.keepStereo ? new StreamingResampler(lease.Format.SampleRate, AudioFormat.SpeechTimeline.SampleRate) : null;
        if (this.options.AutoGain && this.options.Source != TranscriptSourceType.Microphone)
        {
            // Left is the microphone in a meeting: a low ceiling, since room noise must not be amplified into speech.
            var systemGain = new AutoGainOptions();
            this.gainLeft = new AutoGain(this.options.Source == TranscriptSourceType.Meeting ? systemGain with { MaxGain = 4f } : systemGain);
            this.gainRight = this.keepStereo ? new AutoGain(systemGain) : null;
        }

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
                this.frameSynthetic = frame.IsSyntheticSilence;
                if (rightResampler is not null)
                {
                    var left = resampler.Process(AudioConversion.SelectChannel(frame.Samples.Span, 2, 0));
                    var right = rightResampler.Process(AudioConversion.SelectChannel(frame.Samples.Span, 2, 1));
                    await this.PublishStereoAsync(left, right).ConfigureAwait(false);
                    continue;
                }

                var mono = AudioConversion.DownmixToMono(frame.Samples.Span, frame.Format.Channels);
                await this.PublishMonoAsync(resampler.Process(mono)).ConfigureAwait(false);
            }

            this.frameSynthetic = false;
            if (rightResampler is not null)
            {
                await this.PublishStereoAsync(resampler.Flush(), rightResampler.Flush()).ConfigureAwait(false);
            }
            else
            {
                await this.PublishMonoAsync(resampler.Flush()).ConfigureAwait(false);
            }
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

    private Task PublishMonoAsync(float[] samples) =>
        this.gainLeft is null ? this.PublishAsync(samples) : this.PublishAsync(this.gainLeft.Process(samples), samples);

    private Task PublishStereoAsync(float[] left, float[] right)
    {
        var count = Math.Min(left.Length, right.Length);
        var interleaved = new float[count * 2];
        var mono = new float[count];
        for (var i = 0; i < count; i++)
        {
            interleaved[2 * i] = left[i];
            interleaved[(2 * i) + 1] = right[i];
            mono[i] = (left[i] + right[i]) * 0.5f;
        }

        if (this.gainLeft is not null && this.gainRight is not null)
        {
            // Per channel, so a quiet remote side is lifted without lifting the microphone. The sum keeps
            // one speaker at full level, and the limiter handles both talking at once.
            var l = this.gainLeft.Process(left.AsSpan(0, count));
            var r = this.gainRight.Process(right.AsSpan(0, count));
            for (var i = 0; i < count; i++)
            {
                mono[i] = AutoGain.Limit(l[i] + r[i]);
            }
        }

        return this.PublishAsync(mono, interleaved);
    }

    private async Task PublishAsync(float[] samples, float[]? fileSamples = null)
    {
        if (samples.Length == 0)
        {
            return;
        }

        this.writer!.Write(fileSamples ?? samples);
        // Keep the WAV header valid on disk, so a crash or kill leaves audio that can be played and repaired.
        if ((this.samplesSinceFlush += samples.Length) >= AudioFormat.SpeechTimeline.SampleRate)
        {
            this.samplesSinceFlush = 0;
            this.writer.Flush();
        }

        lock (this.timeline!)
        {
            if (this.timeline.IsCapturing)
            {
                this.timeline.Append(samples.Length);
            }
        }

        var peak = 0f;
        foreach (var s in fileSamples ?? samples)
        {
            peak = Math.Max(peak, Math.Abs(s));
        }

        this.Level = peak;
        this.LevelChanged?.Invoke(peak);
        Interlocked.Add(ref this.backlogSamples, samples.Length);
        await this.queue.Writer.WriteAsync(new Samples(samples, this.frameSynthetic)).ConfigureAwait(false);
    }

    private Task InferenceLoopAsync() =>
        this.provider.Capabilities.LiveMode == LiveRecognitionMode.NativeStreaming
            ? this.StreamingInferenceLoopAsync()
            : this.WindowedInferenceLoopAsync();

    /// <summary>
    /// Native streaming: audio flows to the provider continuously and the provider reports text as it is
    /// recognized. Silence ends an utterance (a commit), which is when the provider can report final text
    /// and, with speaker detection, who said it. One lease covers the whole session so file jobs wait.
    /// </summary>
    private async Task StreamingInferenceLoopAsync()
    {
        var host = this.Host!;
        var sessionId = host.Document.SessionId;
        var ct = this.stopSignal.Token;
        var detectorOptions = this.options.Detector ?? new UtteranceDetectorOptions
        {
            // Each commit is a chance for speaker identity to reset and every commit splits the text into a new
            // segment, so end utterances on real pauses only. 1.8 s rides over short breaths in a sentence.
            EndSilence = TimeSpan.FromMilliseconds(1800),
            MaxUtterance = TimeSpan.FromSeconds(30)
        };
        var detector = new UtteranceDetector(detectorOptions);
        var revisions = new Dictionary<string, long>(StringComparer.Ordinal);
        var diarize = this.provider.Capabilities.CombinedDiarization;

        using var lease = await this.scheduler.AcquireAsync(this.provider.ModelId, ModelLeasePriority.Live, ct).ConfigureAwait(false);
        await using var stream = await this.provider.StartStreamingAsync(this.options.Session.Language, diarize, ct).ConfigureAwait(false);
        if (stream is IStreamingNotices notices)
        {
            if (notices.StartupNotice is { } startup)
            {
                host.AddNote(startup);
                this.Error?.Invoke(startup);
            }

            notices.Notice += message =>
            {
                host.AddNote($"{TimeSpan.FromTicks(this.Elapsed.Ticks):hh\\:mm\\:ss}: {message}");
                this.Error?.Invoke(message);
            };
        }


        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var update in stream.ReadUpdatesAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    var id = "u" + update.UtteranceId;
                    var text = update.Segment.Text.Trim();
                    if (text.Length == 0)
                    {
                        if (update.IsFinal)
                        {
                            host.Apply(new SegmentRemoved(sessionId, id, 1));
                        }

                        continue;
                    }

                    var revision = revisions.GetValueOrDefault(id) + 1;
                    revisions[id] = revision;
                    var mapped = SegmentMapper.Map([update.Segment], "x", 0, TimeSpan.FromDays(365), 1, revision, update.IsFinal ? SegmentState.Final : SegmentState.Provisional)
                        .Select(m => m with { Id = id }).ToList();
                    host.EnsureSpeakers(mapped);
                    foreach (var segment in mapped)
                    {
                        host.Apply(update.IsFinal ? new SegmentFinalized(sessionId, segment) : new SegmentUpserted(sessionId, segment));
                    }

                    if (update.IsFinal)
                    {
                        await host.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The provider connection died; keep what is transcribed so far and stop safely.
                this.failure ??= ex;
                this.Error?.Invoke("Live transcription stopped: " + ex.Message);
            }
        }, CancellationToken.None);

        long sequence = 0;
        var format = AudioFormat.SpeechTimeline;
        try
        {
            await foreach (var item in this.queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                switch (item)
                {
                    case Samples s:
                        var frame = AudioFrame.CopyFrom(s.Data, format, sequence++, this.streamedSamples, s.SyntheticSilence);
                        this.streamedSamples += s.Data.Length;
                        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
                        if (detector.Add(s.Data).Any(e => e.Kind == UtteranceEventKind.Ended))
                        {
                            await stream.CommitAsync(ct).ConfigureAwait(false);
                        }

                        Interlocked.Add(ref this.backlogSamples, -s.Data.Length);
                        break;
                    case Flush:
                        // Pause or a gap in capture: finalize what was said so far.
                        detector.Flush();
                        await stream.CommitAsync(ct).ConfigureAwait(false);
                        break;
                }
            }

            if (!this.discard)
            {
                await stream.CommitAsync(ct).ConfigureAwait(false);
                await stream.CompleteAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (this.discard)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.failure ??= ex;
            this.Error?.Invoke("Live transcription stopped: " + ex.Message);
        }

        await reader.ConfigureAwait(false);
    }

    private async Task WindowedInferenceLoopAsync()
    {
        var host = this.Host!;
        var sessionId = host.Document.SessionId;
        var detector = new UtteranceDetector(this.options.Detector ?? new UtteranceDetectorOptions { MaxUtterance = TimeSpan.FromSeconds(15) });
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

        // Previews are best effort and must never delay finals. They run only when the queue is nearly drained,
        // and the gap between them grows with what the last one cost, so a slow model cannot fall further
        // behind because of its own previews.
        var lastPreviewEnd = long.MinValue;
        var lastPreviewCost = TimeSpan.Zero;

        bool PreviewAllowed()
        {
            if (Interlocked.Read(ref this.backlogSamples) > 16_000 * 0.6)
            {
                return false;
            }

            var gap = TimeSpan.FromTicks(Math.Max(this.options.PreviewInterval.Ticks, lastPreviewCost.Ticks * 2));
            return lastPreviewEnd == long.MinValue || Stopwatch.GetElapsedTime(lastPreviewEnd) >= gap;
        }

        async Task HandleAsync(IReadOnlyList<UtteranceEvent> events)
        {
            foreach (var e in events)
            {
                if (e.Kind == UtteranceEventKind.Ended)
                {
                    await RecognizeAsync(e, final: true).ConfigureAwait(false);
                }
                else if (PreviewAllowed() && e.Samples.Length >= 16_000 * 0.8)
                {
                    var started = Stopwatch.GetTimestamp();
                    await RecognizeAsync(e, final: false).ConfigureAwait(false);
                    lastPreviewEnd = Stopwatch.GetTimestamp();
                    lastPreviewCost = Stopwatch.GetElapsedTime(started, lastPreviewEnd);
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
                        await HandleAsync(detector.Add(s.Data, PreviewAllowed())).ConfigureAwait(false);
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
