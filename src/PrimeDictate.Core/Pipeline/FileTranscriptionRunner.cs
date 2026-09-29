using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Coordination;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Sessions;
using PrimeDictate.Core.Storage;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Core.Pipeline;

public sealed record FileJobRequest(
    string SourcePath,
    string Title,
    TranscriptionSessionOptions Options,
    /// <summary>Which audio stream of the container to use (see <see cref="IAudioDecoder.ProbeAsync"/>).</summary>
    int StreamIndex,
    /// <summary>Copy of the source is never made; the session references the original in place.</summary>
    bool KeepWorkingAudio,
    string SessionMediaDirectory);

public sealed record ProviderRunInfo(string ModelId, string? ModelRevision, EffectiveRuntime Runtime);

/// <summary>
/// Transcribes one imported file. Decodes incrementally (never the whole file in memory), converts
/// to the 16 kHz mono timeline, splits at quiet points, recognizes each chunk under a model lease,
/// and checkpoints after every chunk. Text goes only into the session document: no logging, no
/// typing, no command matching.
/// </summary>
public sealed class FileTranscriptionRunner(ModelLeaseScheduler scheduler, ITranscriptionSessionStore store, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Creates a new session for a file. Returns the host so the UI can bind to it.</summary>
    public async ValueTask<SessionDocumentHost> CreateSessionAsync(
        FileJobRequest request,
        IAudioDecoder decoder,
        CancellationToken cancellationToken)
    {
        MediaProbeResult probe;
        try
        {
            probe = await decoder.ProbeAsync(request.SourcePath, cancellationToken).ConfigureAwait(false);
        }
        catch (MediaDecodeException)
        {
            throw;
        }

        var stream = probe.AudioStreams.FirstOrDefault(s => s.Index == request.StreamIndex)
            ?? throw new MediaDecodeException("no-such-stream", "The selected audio track does not exist in this file.");

        var now = this.clock();
        var document = new TranscriptDocument
        {
            SessionId = Guid.NewGuid(),
            Title = request.Title,
            SourceType = TranscriptSourceType.ImportedFile,
            CreatedAt = now,
            UpdatedAt = now,
            Duration = probe.Duration,
            Status = TranscriptSessionStatus.Created,
            Media = new MediaMetadata(
                Path.GetFileName(request.SourcePath),
                probe.ContainerFormat,
                stream.Codec,
                stream.SampleRate,
                stream.Channels,
                stream.Index,
                request.Options.Downmix == DownmixMode.SingleChannel ? $"channel {request.Options.SourceChannel}" : "average",
                probe.Duration),
            Audio = [new AudioReference(AudioReferenceKind.ExternalReference, request.SourcePath, null)]
        };
        var host = new SessionDocumentHost(document, store, this.clock);
        await host.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        return host;
    }

    /// <summary>
    /// Runs recognition into <paramref name="host"/>. A rerun on an existing session uses a new
    /// result version and leaves earlier results and their edits in place.
    /// </summary>
    public async Task RunAsync(
        SessionDocumentHost host,
        FileJobRequest request,
        IAudioDecoder decoder,
        ITranscriptionProvider provider,
        IProgress<ProgressChanged>? progress,
        CancellationToken cancellationToken)
    {
        var sessionId = host.Document.SessionId;
        var resultVersion = host.Document.Runs.Count == 0 ? 1 : host.Document.Runs.Max(r => r.ResultVersion) + 1;
        var run = new RecognitionRunInfo(
            resultVersion,
            provider.ModelId,
            request.Options.AsrModelRevision,
            null,
            null,
            request.Options.Language,
            provider.Runtime.RuntimeName,
            provider.Runtime.RuntimeVersion,
            provider.Runtime.RequestedBackend,
            provider.Runtime.EffectiveBackend,
            this.clock());

        host.SetStatus(TranscriptSessionStatus.Running);
        host.Apply(new SessionStarted(sessionId, run));
        var duration = host.Document.Duration;
        WavFileWriter? writer = null;
        try
        {
            if (request.KeepWorkingAudio && !host.Document.Audio.Any(a => a.Kind == AudioReferenceKind.Owned))
            {
                var path = Path.Combine(request.SessionMediaDirectory, "working-16k-mono.wav");
                writer = new WavFileWriter(path);
                host.Modify(d => d with { Audio = [.. d.Audio, new AudioReference(AudioReferenceKind.Owned, path, null)] });
            }

            var resampler = default(StreamingResampler);
            var chunker = new SpeechChunker(ChunkerFor(provider));
            var processed = TimeSpan.Zero;
            var chunkIndex = 0;
            var lastAudioEnd = TimeSpan.Zero;

            async Task RecognizeAsync(IReadOnlyList<AudioChunk> chunks)
            {
                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var index = chunkIndex++;
                    if (chunk.ContainsSpeech)
                    {
                        IReadOnlyList<RecognizedSegment> recognized;
                        using (await scheduler.AcquireAsync(provider.ModelId, ModelLeasePriority.Background, cancellationToken).ConfigureAwait(false))
                        {
                            recognized = await provider.RecognizeWindowAsync(chunk.Samples, request.Options.Language, cancellationToken).ConfigureAwait(false);
                        }

                        var mapped = SegmentMapper.Map(recognized, $"f{index}", chunk.StartSample, chunk.Duration, resultVersion, 1, SegmentState.Final).ToList();
                        host.EnsureSpeakers(mapped);
                        foreach (var segment in mapped)
                        {
                            host.Apply(new SegmentFinalized(sessionId, segment));
                        }
                    }

                    processed = chunk.Start + chunk.Duration;
                    await host.CheckpointAsync(cancellationToken).ConfigureAwait(false);
                    progress?.Report(new ProgressChanged(
                        sessionId,
                        TranscriptionPhase.Transcribing,
                        duration is { TotalSeconds: > 0 } d ? Math.Clamp(processed / d, 0, 1) : null,
                        processed,
                        null));
                }
            }

            progress?.Report(new ProgressChanged(sessionId, TranscriptionPhase.Decoding, 0, TimeSpan.Zero, null));
            await foreach (var frame in decoder.DecodeAsync(request.SourcePath, request.StreamIndex, cancellationToken).ConfigureAwait(false))
            {
                resampler ??= new StreamingResampler(frame.Format.SampleRate, AudioFormat.SpeechTimeline.SampleRate);
                var mono = request.Options.Downmix == DownmixMode.SingleChannel && request.Options.SourceChannel is { } channel && frame.Format.Channels > 1
                    ? AudioConversion.SelectChannel(frame.Samples.Span, frame.Format.Channels, channel)
                    : AudioConversion.DownmixToMono(frame.Samples.Span, frame.Format.Channels);
                var converted = resampler.Process(mono);
                lastAudioEnd = frame.End;
                if (converted.Length == 0)
                {
                    continue;
                }

                writer?.Write(converted);
                await RecognizeAsync(chunker.Add(converted)).ConfigureAwait(false);
            }

            if (resampler is null)
            {
                throw new MediaDecodeException("no-audio", "The file contains no decodable audio.");
            }

            progress?.Report(new ProgressChanged(sessionId, TranscriptionPhase.Finalizing, null, processed, null));
            var tail = resampler.Flush();
            if (tail.Length > 0)
            {
                writer?.Write(tail);
                await RecognizeAsync(chunker.Add(tail)).ConfigureAwait(false);
            }

            await RecognizeAsync(chunker.Flush()).ConfigureAwait(false);
            writer?.Flush();
            var total = duration ?? lastAudioEnd;
            host.SetStatus(TranscriptSessionStatus.Finalizing);
            host.Apply(new SessionCompleted(sessionId, total));
            await host.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
            if (request.Options.AudioRetention == AudioRetention.TranscriptOnly)
            {
                writer?.Dispose();
                writer = null;
                await store.DeleteOwnedAudioAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                host.Modify(d => d with { Audio = d.Audio.Where(a => a.Kind != AudioReferenceKind.Owned).ToList() });
                await host.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            writer?.Flush();
            host.SetStatus(TranscriptSessionStatus.Canceled);
            await host.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            writer?.Flush();
            var code = ex is MediaDecodeException m ? m.ErrorCode : "transcription-failed";
            // Message is technical. It never contains transcript text.
            host.Apply(new SessionFailed(sessionId, code, ex.GetType().Name, Recoverable: true));
            await host.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private static SpeechChunkerOptions ChunkerFor(ITranscriptionProvider provider)
    {
        var max = provider.Capabilities.MaxWindow ?? TimeSpan.FromSeconds(28);
        // Keep a margin under the model's limit.
        var window = max - TimeSpan.FromSeconds(2);
        return new SpeechChunkerOptions { MaxChunk = window < TimeSpan.FromSeconds(5) ? max : window };
    }
}
