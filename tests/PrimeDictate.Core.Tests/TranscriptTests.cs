using PrimeDictate.Core.Sessions;
using PrimeDictate.Core.Transcripts;
using static PrimeDictate.Core.Tests.TestData;

namespace PrimeDictate.Core.Tests;

public class TranscriptTests
{
    [Fact]
    public void Upserts_are_idempotent_and_ignore_stale_revisions()
    {
        var doc = NewDocument();
        doc = TranscriptDocumentReducer.Apply(doc, new SessionStarted(doc.SessionId, Run()), Now);
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentUpserted(doc.SessionId, Segment("s1", "hel", 0, 1, 1, SegmentState.Provisional)), Now);
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentUpserted(doc.SessionId, Segment("s1", "hello", 0, 1.2, 2, SegmentState.Provisional)), Now);
        var afterDuplicate = TranscriptDocumentReducer.Apply(doc, new SegmentUpserted(doc.SessionId, Segment("s1", "hello", 0, 1.2, 2, SegmentState.Provisional)), Now);
        var afterStale = TranscriptDocumentReducer.Apply(afterDuplicate, new SegmentUpserted(doc.SessionId, Segment("s1", "he", 0, 1, 1, SegmentState.Provisional)), Now);

        Assert.Same(doc, afterDuplicate);
        Assert.Same(afterDuplicate, afterStale);
        var segment = Assert.Single(afterStale.Segments);
        Assert.Equal("hello", segment.RawText);
        Assert.Equal(2, segment.Revision);
    }

    [Fact]
    public void Finalizing_replaces_the_provisional_segment_in_place()
    {
        var doc = NewDocument();
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentUpserted(doc.SessionId, Segment("s1", "hello wor", 0, 1, 1, SegmentState.Provisional)), Now);
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentFinalized(doc.SessionId, Segment("s1", "hello world", 0, 1.5, 2, SegmentState.Provisional)), Now);
        var segment = Assert.Single(doc.Segments);
        Assert.Equal(SegmentState.Final, segment.State);
        Assert.Equal("hello world", segment.RawText);
    }

    [Fact]
    public void Edits_are_kept_separate_from_raw_text_and_survive_recognizer_updates()
    {
        var doc = NewDocument();
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentUpserted(doc.SessionId, Segment("s1", "my name is just in", 0, 2)), Now);
        doc = TranscriptDocumentReducer.EditSegment(doc, "s1", "My name is Justin.", Now);
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentFinalized(doc.SessionId, Segment("s1", "my name is justin", 0, 2, 2)), Now);

        var segment = Assert.Single(doc.Segments);
        Assert.Equal("my name is justin", segment.RawText);
        Assert.Equal("My name is Justin.", segment.EditedText);
        Assert.Equal("My name is Justin.", segment.DisplayText);
        Assert.True(segment.IsEdited);

        doc = TranscriptDocumentReducer.EditSegment(doc, "s1", "my name is justin", Now);
        Assert.Null(Assert.Single(doc.Segments).EditedText);
    }

    [Fact]
    public void Provider_cannot_supply_edited_text()
    {
        var doc = NewDocument();
        var sneaky = Segment("s1", "raw", 0, 1) with { EditedText = "injected" };
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentUpserted(doc.SessionId, sneaky), Now);
        Assert.Null(Assert.Single(doc.Segments).EditedText);
    }

    [Fact]
    public void Renaming_a_speaker_changes_the_mapping_not_segments()
    {
        var doc = NewDocument();
        doc = TranscriptDocumentReducer.Apply(doc, new SpeakerUpdated(doc.SessionId, new TranscriptSpeaker("spk0", "Speaker 1", null)), Now);
        var segment = Segment("s1", "hi", 0, 1) with { Speakers = [new SpeakerAttribution("spk0", TimeSpan.Zero, TimeSpan.FromSeconds(1), null)] };
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentUpserted(doc.SessionId, segment), Now);
        var segmentsBefore = doc.Segments;

        doc = TranscriptDocumentReducer.RenameSpeaker(doc, "spk0", "  Justin ", Now);
        Assert.Same(segmentsBefore, doc.Segments);
        Assert.Equal("Justin", Assert.Single(doc.Speakers).Name);

        // A later provider label refresh keeps the user's name.
        doc = TranscriptDocumentReducer.Apply(doc, new SpeakerUpdated(doc.SessionId, new TranscriptSpeaker("spk0", "Speaker 1", null)), Now);
        Assert.Equal("Justin", Assert.Single(doc.Speakers).Name);

        doc = TranscriptDocumentReducer.RenameSpeaker(doc, "spk0", "", Now);
        Assert.Equal("Speaker 1", Assert.Single(doc.Speakers).Name);
    }

    [Fact]
    public void A_rerun_adds_a_result_version_without_overwriting_the_old_one()
    {
        var doc = NewDocument();
        doc = TranscriptDocumentReducer.Apply(doc, new SessionStarted(doc.SessionId, Run(1)), Now);
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentFinalized(doc.SessionId, Segment("s1", "first model", 0, 1)), Now);
        doc = TranscriptDocumentReducer.EditSegment(doc, "s1", "First model, edited", Now);

        doc = TranscriptDocumentReducer.Apply(doc, new SessionStarted(doc.SessionId, Run(2, "parakeet")), Now);
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentFinalized(doc.SessionId, Segment("s1", "second model", 0, 1, resultVersion: 2)), Now);

        Assert.Equal(2, doc.ActiveResultVersion);
        Assert.Equal(2, doc.Runs.Count);
        Assert.Equal("second model", Assert.Single(doc.ActiveSegments).DisplayText);
        var old = doc.Segments.Single(s => s.ResultVersion == 1);
        Assert.Equal("First model, edited", old.EditedText);
    }

    [Fact]
    public void Repeated_legitimate_phrases_are_kept()
    {
        var doc = NewDocument();
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentFinalized(doc.SessionId, Segment("a", "thank you", 0, 1)), Now);
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentFinalized(doc.SessionId, Segment("b", "thank you", 1, 2)), Now);
        Assert.Equal(["thank you", "thank you"], doc.ActiveSegments.Select(s => s.DisplayText));
    }

    [Fact]
    public void Invalid_intervals_and_foreign_events_are_rejected()
    {
        var doc = NewDocument();
        Assert.Throws<ArgumentException>(() =>
            TranscriptDocumentReducer.Apply(doc, new SegmentUpserted(doc.SessionId, Segment("s1", "x", 2, 1)), Now));
        Assert.Throws<ArgumentException>(() =>
            TranscriptDocumentReducer.Apply(doc, new SegmentUpserted(Guid.NewGuid(), Segment("s1", "x", 0, 1)), Now));
    }

    [Fact]
    public void Nullable_timing_and_confidence_stay_null()
    {
        var doc = NewDocument();
        doc = TranscriptDocumentReducer.Apply(doc, new SegmentFinalized(doc.SessionId, Segment("s1", "text only", 0, 12)), Now);
        var segment = Assert.Single(doc.Segments);
        Assert.Null(segment.Confidence);
        Assert.Null(segment.Words);
        Assert.Equal(TimingProvenance.ApproximateChunk, segment.TimingProvenance);
    }

    [Theory]
    [InlineData(TranscriptSessionStatus.Created, TranscriptSessionStatus.Running, true)]
    [InlineData(TranscriptSessionStatus.Running, TranscriptSessionStatus.Paused, true)]
    [InlineData(TranscriptSessionStatus.Paused, TranscriptSessionStatus.Running, true)]
    [InlineData(TranscriptSessionStatus.Running, TranscriptSessionStatus.Completed, false)]
    [InlineData(TranscriptSessionStatus.Finalizing, TranscriptSessionStatus.Completed, true)]
    [InlineData(TranscriptSessionStatus.Canceled, TranscriptSessionStatus.Running, false)]
    [InlineData(TranscriptSessionStatus.Interrupted, TranscriptSessionStatus.Finalizing, true)]
    [InlineData(TranscriptSessionStatus.Completed, TranscriptSessionStatus.Running, true)]
    public void State_machine_allows_only_defined_transitions(TranscriptSessionStatus from, TranscriptSessionStatus to, bool allowed)
    {
        Assert.Equal(allowed, TranscriptionSessionStateMachine.CanTransition(from, to));
        if (!allowed)
        {
            Assert.Throws<InvalidOperationException>(() => TranscriptionSessionStateMachine.Transition(from, to));
        }
    }

    [Fact]
    public void Turns_join_close_lines_from_one_speaker_and_guess_for_live_lines()
    {
        var doc = NewDocument();
        foreach (var n in new[] { 1, 2, 3 })
        {
            doc = TranscriptDocumentReducer.Apply(doc, new SpeakerUpdated(doc.SessionId, new TranscriptSpeaker($"speaker-{n}", $"Speaker {n}", null)), Now);
        }

        TranscriptSegment Line(string id, double start, double end, string? speaker, SegmentState state = SegmentState.Final) =>
            Segment(id, id, start, end, state: state) with { Speakers = speaker is null ? [] : [new SpeakerAttribution(speaker, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), null)] };
        foreach (var segment in new[]
        {
            Line("a", 0, 5, "speaker-1"),
            Line("b", 6, 9, "speaker-1"),      // 1 s later, same speaker: same turn
            Line("c", 10, 12, "speaker-2"),     // different speaker: new turn
            Line("d", 13, 14, "speaker-3"),     // merged into speaker-1 below, but not next to it
            Line("e", 30, 33, "speaker-3"),     // same speaker but 16 s later: new turn
            Line("f", 34, 35, null, SegmentState.Provisional) // live, follows e closely: guessed
        })
        {
            doc = TranscriptDocumentReducer.Apply(doc, segment.State == SegmentState.Final ? new SegmentFinalized(doc.SessionId, segment) : new SegmentUpserted(doc.SessionId, segment), Now);
        }

        var turns = TranscriptTurns.Group(doc, TimeSpan.FromSeconds(5));
        Assert.Equal([["a", "b"], ["c"], ["d"], ["e"], ["f"]], turns.Select(t => t.Segments.Select(s => s.Id).ToArray()).ToArray());
        Assert.Equal("speaker-3", turns[^1].GuessedSpeakerId);
        Assert.All(turns.Take(4), t => Assert.Null(t.GuessedSpeakerId));

        // Merging speaker 3 into speaker 2 joins c and d (close together); e stays separate because of the gap.
        doc = TranscriptDocumentReducer.MergeSpeaker(doc, "speaker-3", "speaker-2", Now);
        turns = TranscriptTurns.Group(doc, TimeSpan.FromSeconds(5));
        Assert.Equal([["a", "b"], ["c", "d"], ["e"], ["f"]], turns.Select(t => t.Segments.Select(s => s.Id).ToArray()).ToArray());
        Assert.Equal("speaker-2", turns[^1].GuessedSpeakerId);
    }
}
