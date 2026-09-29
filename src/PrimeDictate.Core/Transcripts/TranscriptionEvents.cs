namespace PrimeDictate.Core.Transcripts;

/// <summary>
/// Application-owned session events. Providers' own event names are mapped onto these.
/// </summary>
public abstract record TranscriptionEvent(Guid SessionId);

public sealed record SessionStarted(Guid SessionId, RecognitionRunInfo Run) : TranscriptionEvent(SessionId);

/// <summary>Inserts or replaces a segment. Idempotent by (segment ID, revision).</summary>
public sealed record SegmentUpserted(Guid SessionId, TranscriptSegment Segment) : TranscriptionEvent(SessionId);

public sealed record SegmentFinalized(Guid SessionId, TranscriptSegment Segment) : TranscriptionEvent(SessionId);

public sealed record SpeakerUpdated(Guid SessionId, TranscriptSpeaker Speaker) : TranscriptionEvent(SessionId);

public enum TranscriptionPhase
{
    LoadingModel = 0,
    Decoding = 1,
    Transcribing = 2,
    DetectingSpeakers = 3,
    Finalizing = 4
}

/// <summary>Progress; <paramref name="Fraction"/> is null for live sessions with no known end.</summary>
public sealed record ProgressChanged(
    Guid SessionId,
    TranscriptionPhase Phase,
    double? Fraction,
    TimeSpan ProcessedAudio,
    TimeSpan? Backlog) : TranscriptionEvent(SessionId);

public sealed record SessionCompleted(Guid SessionId, TimeSpan Duration) : TranscriptionEvent(SessionId);

/// <summary>Failure. The message is technical and must never contain transcript text.</summary>
public sealed record SessionFailed(Guid SessionId, string ErrorCode, string Message, bool Recoverable) : TranscriptionEvent(SessionId);
