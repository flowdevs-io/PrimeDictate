using PrimeDictate.Core.Audio;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Core.Providers;

public enum LiveRecognitionMode
{
    /// <summary>Provider cannot run live.</summary>
    None = 0,

    /// <summary>Provider keeps streaming state and consumes new audio incrementally.</summary>
    NativeStreaming = 1,

    /// <summary>Provider recognizes bounded windows; shown to users as "Buffered Live".</summary>
    BufferedWindows = 2
}

[Flags]
public enum TimingCapabilities
{
    None = 0,
    SegmentTimestamps = 1,
    WordTimestamps = 2,
    Confidence = 4
}

public sealed record TranscriptionProviderCapabilities(
    bool SupportsFiles,
    LiveRecognitionMode LiveMode,
    TimingCapabilities Timing,
    bool CombinedDiarization,
    int? MaxSpeakers,
    TimeSpan? MaxWindow,
    IReadOnlyList<string> Languages,
    int RequiredSampleRate);

/// <summary>What actually ran, reported after load, not what was requested.</summary>
public sealed record EffectiveRuntime(string RuntimeName, string? RuntimeVersion, string RequestedBackend, string EffectiveBackend, string? FallbackReason);

/// <summary>A recognized span in a provider's own window, before session placement.</summary>
public sealed record RecognizedSegment(
    TimeSpan Start,
    TimeSpan End,
    string Text,
    IReadOnlyList<WordTiming>? Words,
    double? Confidence,
    string? SpeakerLabel,
    TimingProvenance Provenance);

/// <summary>
/// Update from a streaming recognizer. <paramref name="UtteranceId"/> names the segment and is stable across
/// partials and its final ("3.0", "3.1" for the second speaker turn of utterance 3). Times are on the session timeline.
/// </summary>
public sealed record StreamingUpdate(string UtteranceId, RecognizedSegment Segment, bool IsFinal);

/// <summary>
/// Optional: a streaming session that can recover on its own (for example by restarting a wedged worker connection)
/// tells the caller so the break in continuity can be shown to the user.
/// </summary>
public interface IStreamingNotices
{
    event Action<string>? Notice;

    /// <summary>Something worth telling the user that happened while the stream was being set up. Null if nothing.</summary>
    string? StartupNotice { get; }
}

public interface IStreamingRecognitionSession : IAsyncDisposable
{
    /// <summary>Feeds audio on the session timeline. Frames must be in the provider's required format.</summary>
    ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken);

    /// <summary>
    /// Finalizes what has been sent so far as one utterance. Audio written afterwards starts a new one.
    /// Speaker identity across utterances depends on the provider.
    /// </summary>
    ValueTask CommitAsync(CancellationToken cancellationToken);

    /// <summary>Signals end of input; the update stream completes after the last final result.</summary>
    ValueTask CompleteAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<StreamingUpdate> ReadUpdatesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A loaded speech model. Instances are not assumed thread-safe; callers obtain one through a
/// model lease from the resource coordinator.
/// </summary>
/// <summary>What a provider can say about the models it loaded, for the run record. Unknown parts stay null.</summary>
public sealed record ProviderModelInfo(string? AsrRevision, string? DiarizerModelId, string? DiarizerRevision);

public interface ITranscriptionProvider : IAsyncDisposable
{
    string ModelId { get; }

    /// <summary>Model revisions and the diarizer actually in use, recorded with each run.</summary>
    ProviderModelInfo ModelInfo => new(null, null, null);

    TranscriptionProviderCapabilities Capabilities { get; }

    EffectiveRuntime Runtime { get; }

    /// <summary>
    /// Recognizes one bounded mono window at <see cref="TranscriptionProviderCapabilities.RequiredSampleRate"/>.
    /// Offsets in the result are relative to the window start.
    /// </summary>
    ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken);

    /// <summary>Starts native streaming. Throws <see cref="NotSupportedException"/> unless LiveMode is NativeStreaming.</summary>
    ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken);
}

public sealed record SpeakerTurn(string SpeakerLabel, TimeSpan Start, TimeSpan End, double? Confidence);

public sealed record DiarizationCapabilities(int MaxSpeakers, bool SupportsStreaming, int RequiredSampleRate);

/// <summary>Finds anonymous speaker activity on the same timeline as recognition.</summary>
public interface IDiarizationProvider : IAsyncDisposable
{
    string ModelId { get; }

    DiarizationCapabilities Capabilities { get; }

    ValueTask<IReadOnlyList<SpeakerTurn>> DiarizeAsync(ReadOnlyMemory<float> samples, TimeSpan offset, CancellationToken cancellationToken);
}
