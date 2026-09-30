using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Core.Sessions;

/// <summary>Allowed status transitions for a transcription session.</summary>
public static class TranscriptionSessionStateMachine
{
    private static readonly Dictionary<TranscriptSessionStatus, TranscriptSessionStatus[]> Allowed = new()
    {
        [TranscriptSessionStatus.Created] = [TranscriptSessionStatus.Running, TranscriptSessionStatus.Canceled, TranscriptSessionStatus.Failed],
        [TranscriptSessionStatus.Running] =
        [
            TranscriptSessionStatus.Paused, TranscriptSessionStatus.Finalizing, TranscriptSessionStatus.Failed,
            TranscriptSessionStatus.Canceled, TranscriptSessionStatus.Interrupted
        ],
        [TranscriptSessionStatus.Paused] =
        [
            TranscriptSessionStatus.Running, TranscriptSessionStatus.Finalizing, TranscriptSessionStatus.Failed,
            TranscriptSessionStatus.Canceled, TranscriptSessionStatus.Interrupted
        ],
        [TranscriptSessionStatus.Finalizing] = [TranscriptSessionStatus.Completed, TranscriptSessionStatus.Failed, TranscriptSessionStatus.Interrupted, TranscriptSessionStatus.Canceled],
        // A rerun with another model starts a new result version on a finished session.
        [TranscriptSessionStatus.Completed] = [TranscriptSessionStatus.Running],
        [TranscriptSessionStatus.Failed] = [TranscriptSessionStatus.Running, TranscriptSessionStatus.Canceled],
        [TranscriptSessionStatus.Interrupted] = [TranscriptSessionStatus.Running, TranscriptSessionStatus.Finalizing, TranscriptSessionStatus.Canceled],
        [TranscriptSessionStatus.Canceled] = []
    };

    public static bool CanTransition(TranscriptSessionStatus from, TranscriptSessionStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static TranscriptSessionStatus Transition(TranscriptSessionStatus from, TranscriptSessionStatus to) =>
        CanTransition(from, to)
            ? to
            : throw new InvalidOperationException($"A session cannot move from {from} to {to}.");

    /// <summary>Statuses that mean work was in flight when the app last ran.</summary>
    public static bool NeedsRecovery(TranscriptSessionStatus status) =>
        status is TranscriptSessionStatus.Running or TranscriptSessionStatus.Paused or TranscriptSessionStatus.Finalizing;
}
