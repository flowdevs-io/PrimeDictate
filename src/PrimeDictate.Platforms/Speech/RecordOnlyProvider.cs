using PrimeDictate.Core.Providers;

namespace PrimeDictate.Platforms.Speech;

/// <summary>
/// Stands in for the live recognizer when a meeting is only recorded. It hears nothing, so no model runs during the call
/// and the final pass does all the transcription after Stop.
/// </summary>
public sealed class RecordOnlyProvider : ITranscriptionProvider
{
    public const string Id = "record-only";

    public string ModelId => Id;

    public TranscriptionProviderCapabilities Capabilities { get; } = new(
        SupportsFiles: false,
        LiveMode: LiveRecognitionMode.BufferedWindows,
        Timing: TimingCapabilities.SegmentTimestamps,
        CombinedDiarization: false,
        MaxSpeakers: null,
        MaxWindow: TimeSpan.FromSeconds(30),
        Languages: [],
        RequiredSampleRate: 16_000);

    public EffectiveRuntime Runtime { get; } = new("record-only", null, "none", "none", null);

    public ValueTask<IReadOnlyList<RecognizedSegment>> RecognizeWindowAsync(ReadOnlyMemory<float> samples, string? language, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<RecognizedSegment>>([]);

    public ValueTask<IStreamingRecognitionSession> StartStreamingAsync(string? language, bool diarize, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
