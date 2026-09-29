using PrimeDictate.Core.Sessions;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Core.Pipeline;

/// <summary>
/// Holds the live copy of one session's document. Applies events and user edits through the pure
/// reducer, raises <see cref="Changed"/> for the UI, and persists checkpoints.
/// </summary>
public sealed class SessionDocumentHost
{
    private readonly object sync = new();
    private readonly ITranscriptionSessionStore store;
    private readonly Func<DateTimeOffset> clock;
    private TranscriptDocument document;

    public SessionDocumentHost(TranscriptDocument document, ITranscriptionSessionStore store, Func<DateTimeOffset>? clock = null)
    {
        this.document = document;
        this.store = store;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised after every change, on the thread that made it.</summary>
    public event Action<TranscriptDocument>? Changed;

    public TranscriptDocument Document
    {
        get
        {
            lock (this.sync)
            {
                return this.document;
            }
        }
    }

    public void Apply(TranscriptionEvent @event) => this.Update(d => TranscriptDocumentReducer.Apply(d, @event, this.clock()));

    public void Edit(string segmentId, string? text) =>
        this.Update(d => TranscriptDocumentReducer.EditSegment(d, segmentId, text, this.clock()));

    public void RenameSpeaker(string speakerId, string? name) =>
        this.Update(d => TranscriptDocumentReducer.RenameSpeaker(d, speakerId, name, this.clock()));

    public void SetStatus(TranscriptSessionStatus status) =>
        this.Update(d => d with
        {
            Status = TranscriptionSessionStateMachine.Transition(d.Status, status),
            UpdatedAt = this.clock()
        });

    /// <summary>Sets fields the reducer does not own, such as audio references and duration.</summary>
    public void Modify(Func<TranscriptDocument, TranscriptDocument> change) =>
        this.Update(d => change(d) with { UpdatedAt = this.clock() });

    public async ValueTask CheckpointAsync(CancellationToken cancellationToken = default)
    {
        await this.store.SaveCheckpointAsync(this.Document, cancellationToken).ConfigureAwait(false);
    }

    private void Update(Func<TranscriptDocument, TranscriptDocument> change)
    {
        TranscriptDocument updated;
        lock (this.sync)
        {
            updated = change(this.document);
            if (ReferenceEquals(updated, this.document))
            {
                return;
            }

            this.document = updated;
        }

        this.Changed?.Invoke(updated);
    }
}
