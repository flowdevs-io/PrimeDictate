namespace PrimeDictate.Core.Transcripts;

/// <summary>
/// Pure functions that apply events and user edits to a <see cref="TranscriptDocument"/>.
/// Recognizer updates never overwrite user edits or speaker names.
/// </summary>
public static class TranscriptDocumentReducer
{
    public static TranscriptDocument Apply(TranscriptDocument document, TranscriptionEvent @event, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(@event);
        if (@event.SessionId != document.SessionId)
        {
            throw new ArgumentException("Event belongs to a different session.", nameof(@event));
        }

        return @event switch
        {
            SessionStarted started => StartRun(document, started.Run, now),
            SegmentUpserted upserted => UpsertSegment(document, upserted.Segment, now),
            SegmentFinalized finalized => UpsertSegment(document, finalized.Segment with { State = SegmentState.Final }, now),
            SpeakerUpdated speaker => UpsertSpeaker(document, speaker.Speaker, now),
            SessionCompleted completed => document with
            {
                Status = TranscriptSessionStatus.Completed,
                Duration = completed.Duration,
                UpdatedAt = now
            },
            SessionFailed => document with { Status = TranscriptSessionStatus.Failed, UpdatedAt = now },
            ProgressChanged => document,
            _ => throw new NotSupportedException($"Unknown event {@event.GetType().Name}.")
        };
    }

    public static TranscriptDocument EditSegment(TranscriptDocument document, string segmentId, string? editedText, DateTimeOffset now)
    {
        var index = FindSegment(document, segmentId, document.ActiveResultVersion);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Segment {segmentId} is not in the active result.");
        }

        var segments = document.Segments.ToList();
        var existing = segments[index];
        var normalized = editedText is not null && string.Equals(editedText, existing.RawText, StringComparison.Ordinal)
            ? null
            : editedText;
        segments[index] = existing with { EditedText = normalized };
        return document with { Segments = segments, UpdatedAt = now };
    }

    public static TranscriptDocument RenameSpeaker(TranscriptDocument document, string speakerId, string? displayName, DateTimeOffset now)
    {
        var speakers = document.Speakers.ToList();
        var index = speakers.FindIndex(s => s.Id == speakerId);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Speaker {speakerId} does not exist.");
        }

        speakers[index] = speakers[index] with
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim()
        };
        return document with { Speakers = speakers, UpdatedAt = now };
    }

    private static TranscriptDocument StartRun(TranscriptDocument document, RecognitionRunInfo run, DateTimeOffset now)
    {
        var runs = document.Runs.Where(r => r.ResultVersion != run.ResultVersion).Append(run).OrderBy(r => r.ResultVersion).ToList();
        return document with
        {
            Runs = runs,
            ActiveResultVersion = run.ResultVersion,
            Language = run.Language ?? document.Language,
            Status = TranscriptSessionStatus.Running,
            UpdatedAt = now
        };
    }

    private static TranscriptDocument UpsertSegment(TranscriptDocument document, TranscriptSegment incoming, DateTimeOffset now)
    {
        if (incoming.End < incoming.Start)
        {
            throw new ArgumentException($"Segment {incoming.Id} ends before it starts.");
        }

        var segments = document.Segments.ToList();
        var index = FindSegment(document, incoming.Id, incoming.ResultVersion);
        if (index < 0)
        {
            segments.Add(incoming with { EditedText = null });
            return document with { Segments = segments, UpdatedAt = now };
        }

        var existing = segments[index];
        if (incoming.Revision <= existing.Revision)
        {
            // Duplicate or out-of-order delivery.
            return document;
        }

        segments[index] = incoming with { EditedText = existing.EditedText };
        return document with { Segments = segments, UpdatedAt = now };
    }

    private static TranscriptDocument UpsertSpeaker(TranscriptDocument document, TranscriptSpeaker incoming, DateTimeOffset now)
    {
        var speakers = document.Speakers.ToList();
        var index = speakers.FindIndex(s => s.Id == incoming.Id);
        if (index < 0)
        {
            speakers.Add(incoming);
        }
        else
        {
            // Provider label changes never discard a name the user chose.
            speakers[index] = incoming with { DisplayName = speakers[index].DisplayName ?? incoming.DisplayName };
        }

        return document with { Speakers = speakers, UpdatedAt = now };
    }

    private static int FindSegment(TranscriptDocument document, string segmentId, int resultVersion)
    {
        for (var i = 0; i < document.Segments.Count; i++)
        {
            var segment = document.Segments[i];
            if (segment.ResultVersion == resultVersion && string.Equals(segment.Id, segmentId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
