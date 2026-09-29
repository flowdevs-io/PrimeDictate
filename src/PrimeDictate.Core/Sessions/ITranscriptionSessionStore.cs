using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Core.Sessions;

public sealed record TranscriptSessionSummary(
    Guid SessionId,
    string Title,
    TranscriptSourceType SourceType,
    TranscriptSessionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    TimeSpan? Duration,
    int SegmentCount);

public sealed record SessionDeletionResult(bool Existed, IReadOnlyList<string> DeletedFiles, IReadOnlyList<string> KeptExternalFiles)
{
    /// <summary>Files that could not be removed (locked, permissions). The session row is already gone; retry later.</summary>
    public IReadOnlyList<string> FailedFiles { get; init; } = [];
}

/// <summary>
/// Versioned local persistence for transcription sessions. Separate from dictation history.
/// </summary>
public interface ITranscriptionSessionStore : IAsyncDisposable
{
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Persists metadata, runs, speakers, audio references, and final segments in one
    /// transaction. Provisional segments are not persisted; after a crash they are recomputed
    /// from retained audio.
    /// </summary>
    ValueTask SaveCheckpointAsync(TranscriptDocument document, CancellationToken cancellationToken);

    ValueTask<TranscriptDocument?> LoadAsync(Guid sessionId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<TranscriptSessionSummary>> ListAsync(int skip, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Call once at startup: sessions left running, paused, or finalizing are marked
    /// <see cref="TranscriptSessionStatus.Interrupted"/> and returned for recovery.
    /// </summary>
    ValueTask<IReadOnlyList<TranscriptSessionSummary>> MarkInterruptedSessionsAsync(CancellationToken cancellationToken);

    /// <summary>Deletes owned audio and keeps the transcript (transcript-only retention).</summary>
    ValueTask<IReadOnlyList<string>> DeleteOwnedAudioAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the session's records and the audio it owns. Referenced originals are never deleted.
    /// </summary>
    ValueTask<SessionDeletionResult> DeleteAsync(Guid sessionId, CancellationToken cancellationToken);
}
