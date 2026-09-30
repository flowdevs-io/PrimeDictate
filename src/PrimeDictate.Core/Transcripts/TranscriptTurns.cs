namespace PrimeDictate.Core.Transcripts;

/// <summary>Consecutive lines shown together. <paramref name="GuessedSpeakerId"/> is set only for a live line without a speaker yet.</summary>
public sealed record TranscriptTurn(IReadOnlyList<TranscriptSegment> Segments, string? GuessedSpeakerId);

public static class TranscriptTurns
{
    /// <summary>Lines shorter than this fold into the same speaker's earlier line instead of getting a row of their own.</summary>
    public static readonly TimeSpan FragmentLength = TimeSpan.FromSeconds(0.5);

    private static string? SpeakerOf(TranscriptDocument document, TranscriptSegment segment) =>
        segment.Speakers.Count > 0 ? document.ResolveSpeakerId(segment.Speakers[0].SpeakerId) : null;

    /// <summary>
    /// Groups final lines from the same speaker (after merges) that are closer together than <paramref name="gap"/> into one
    /// turn, so short pauses do not start new rows. A live line without a speaker never joins a turn; it is guessed to
    /// continue the previous speaker when it follows closely. A result whose run already cut its lines into rows
    /// (<see cref="RecognitionRunInfo.SegmentsAreRows"/>) is shown one line per row in start order: joining lines again would
    /// undo those cuts, and a fragment folded into an earlier row would sit before a line said ahead of it.
    /// </summary>
    public static IReadOnlyList<TranscriptTurn> Group(TranscriptDocument document, TimeSpan gap)
    {
        if (document.Runs.FirstOrDefault(r => r.ResultVersion == document.ActiveResultVersion) is { SegmentsAreRows: true })
        {
            return document.ActiveSegments.Where(s => s.DisplayText.Length > 0).Select(s => new TranscriptTurn([s], null)).ToList();
        }

        var turns = new List<(List<TranscriptSegment> Segments, string? Guess)>();
        TranscriptSegment? previous = null;
        string? previousSpeaker = null;
        foreach (var segment in document.ActiveSegments.Where(s => s.DisplayText.Length > 0))
        {
            var speaker = segment.State == SegmentState.Final && segment.Speakers.Count > 0
                ? document.ResolveSpeakerId(segment.Speakers[0].SpeakerId)
                : null;
            var near = previous is not null && segment.Start - previous.End <= gap;
            var fragment = speaker is not null && segment.End - segment.Start < FragmentLength;
            var earlier = fragment ? turns.FindLastIndex(t => t.Segments[^1].State == SegmentState.Final && SpeakerOf(document, t.Segments[^1]) == speaker && segment.Start - t.Segments[^1].End <= gap) : -1;
            if (speaker is not null && speaker == previousSpeaker && near)
            {
                turns[^1].Segments.Add(segment);
            }
            else if (earlier >= 0)
            {
                // A tiny fragment ("Taxi") joins its speaker's earlier line even when the other side spoke in between.
                turns[earlier].Segments.Add(segment);
            }
            else
            {
                var guess = speaker is null && segment.State != SegmentState.Final && segment.Speakers.Count == 0 && near ? previousSpeaker : null;
                turns.Add(([segment], guess));
            }

            previous = segment;
            previousSpeaker = speaker;
        }

        return turns.Select(t => new TranscriptTurn(t.Segments, t.Guess)).ToList();
    }
}
