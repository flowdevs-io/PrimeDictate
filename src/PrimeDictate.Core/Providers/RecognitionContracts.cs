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

/// <summary>Update from a streaming recognizer. <paramref name="UtteranceId"/> is stable across partials.</summary>
public sealed record StreamingUpdate(string UtteranceId, RecognizedSegment Segment, bool IsFinal);

public interface IStreamingRecognitionSession : IAsyncDisposable
{
    /// <summary>Feeds audio on the session timeline. Frames must be in the provider's required format.</summary>
    ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken);

    /// <summary>Signals end of input; the update stream completes after the last final result.</summary>
    ValueTask CompleteAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<StreamingUpdate> ReadUpdatesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A loaded speech model. Instances are not assumed thread-safe; callers obtain one through a
/// model lease from the resource coordinator.
/// </summary>
public interface ITranscriptionProvider : IAsyncDisposable
{
    string ModelId { get; }

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
