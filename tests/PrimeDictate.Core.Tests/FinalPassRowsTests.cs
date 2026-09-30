using PrimeDictate.Core.Transcripts;
using PrimeDictate.Platforms.Nemotron;

namespace PrimeDictate.Core.Tests;

/// <summary>How the after-Stop pass cuts each channel's words into rows (<see cref="MeetingFinalPass.BuildRows"/>).</summary>
public sealed class FinalPassRowsTests
{
    /// <summary>Word times from <paramref name="from"/> up to <paramref name="to"/>, one every <paramref name="step"/> seconds.</summary>
    private static (double Start, double End)[] Talk(double from, double to, double step = 0.3, double length = 0.2) =>
        Enumerable.Range(0, (int)Math.Ceiling(((to - from) / step) - 1e-9)).Select(i => (from + (i * step), from + (i * step) + length)).ToArray();

    /// <summary>A recognized line; microphone lines are "local", system lines have no speaker until the diarizer gives one.</summary>
    private static TranscriptSegment Line(string id, bool microphone, (double Start, double End)[] words, string[]? texts = null)
    {
        var timings = words.Select((w, i) => new WordTiming(texts?[i] ?? $"{(microphone ? 'm' : 's')}{i}", TimeSpan.FromSeconds(w.Start), TimeSpan.FromSeconds(w.End), 0.9, TimingProvenance.Model, microphone ? "local" : null)).ToList();
        return new TranscriptSegment
        {
            Id = id,
            Start = timings[0].Start,
            End = timings[^1].End,
            RawText = string.Join(' ', timings.Select(t => t.Text)),
            State = SegmentState.Final,
            Revision = 1,
            ResultVersion = 2,
            TimingProvenance = TimingProvenance.Model,
            Words = timings,
            Speakers = microphone ? [new SpeakerAttribution("local", timings[0].Start, timings[^1].End, null)] : []
        };
    }

    private static (List<TranscriptSegment> Microphone, List<TranscriptSegment> System, HashSet<string> EchoLines) Build(
        TranscriptSegment[] microphone, TranscriptSegment[] system, params DiarizationSegment[] diar) =>
        MeetingFinalPass.BuildRows(microphone, system, [], diar);

    private static double[] Starts(IEnumerable<TranscriptSegment> rows) => rows.Select(r => Math.Round(r.Start.TotalSeconds, 2)).ToArray();

    [Fact]
    public void A_You_line_that_starts_inside_a_remote_row_cuts_it_there()
    {
        var rows = Build([Line("m", true, [(15.0, 15.2), (15.3, 15.6)])], [Line("s", false, Talk(10, 20))], new DiarizationSegment("speaker_1", 9.9, 20.1));

        Assert.Equal([10.0, 15.1], Starts(rows.System));
        Assert.All(rows.System, r => Assert.Equal("speaker-1", r.Speakers[0].SpeakerId));
        Assert.True(rows.System[0].End <= TimeSpan.FromSeconds(15.0));
        Assert.Equal([15.0], Starts(rows.Microphone));
        Assert.Equal(Talk(10, 20).Length, rows.System.Sum(r => r.Words!.Count));
    }

    [Fact]
    public void A_remote_line_that_starts_inside_a_You_row_cuts_the_You_row_there()
    {
        var rows = Build([Line("m", true, Talk(10, 20))], [Line("s", false, [(15.05, 15.3), (15.4, 15.7)])], new DiarizationSegment("speaker_2", 15.0, 15.8));

        Assert.Equal([10.0, 15.1], Starts(rows.Microphone));
        Assert.Equal([15.05], Starts(rows.System));
        Assert.Equal("speaker-2", rows.System[0].Speakers[0].SpeakerId);
    }

    [Fact]
    public void A_word_said_just_as_the_other_side_starts_or_stops_leaves_no_fragment_row()
    {
        // The You words start 0.2 s after the remote row starts and 0.2 s before it ends: a cut would leave one-word pieces.
        var rows = Build(
            [Line("m1", true, [(10.2, 10.35), (10.4, 10.9)]), Line("m2", true, [(19.75, 19.85), (19.9, 20.4)])],
            [Line("s", false, Talk(10, 20))],
            new DiarizationSegment("speaker_1", 9.9, 20.1));

        Assert.Single(rows.System);
        Assert.Equal(2, rows.Microphone.Count);
    }

    [Fact]
    public void Talking_over_each_other_cuts_each_row_once_not_word_by_word()
    {
        var rows = Build([Line("m", true, Talk(10, 20))], [Line("s", false, Talk(12.15, 22))], new DiarizationSegment("speaker_1", 12.0, 22.2));

        // The You row is cut where the remote person started; the remote row has no You start inside it, so it stays whole.
        Assert.Equal([10.0, 12.4], Starts(rows.Microphone));
        Assert.Single(rows.System);
    }

    [Fact]
    public void Diarizer_turns_of_one_speaker_a_second_or_more_apart_are_separate_rows_even_without_a_pause_in_the_words()
    {
        var words = Talk(0, 10);

        var apart = Build([], [Line("s", false, words)], new DiarizationSegment("speaker_1", 0, 4.9), new DiarizationSegment("speaker_1", 6.0, 10.1));
        var close = Build([], [Line("s", false, words)], new DiarizationSegment("speaker_1", 0, 4.9), new DiarizationSegment("speaker_1", 5.5, 10.1));

        Assert.Equal(2, apart.System.Count);
        Assert.InRange(apart.System[0].End.TotalSeconds, 4.9, 6.0);
        Assert.InRange(apart.System[1].Start.TotalSeconds, 4.9, 6.0);
        Assert.Single(close.System);
        Assert.All(apart.System.Concat(close.System), r => Assert.Equal("speaker-1", r.Speakers[0].SpeakerId));
    }

    [Fact]
    public void A_pause_of_a_second_ends_a_row_and_a_shorter_one_does_not()
    {
        // No diarizer: every word is "remote", so only the pauses cut. The 1.3 s gap after 2.9 s cuts; the 0.9 s one after 5.9 s does not.
        var rows = Build([], [Line("s", false, [.. Talk(0, 3), .. Talk(4.2, 6), .. Talk(6.8, 8)])]);

        Assert.Equal([0.0, 4.2], Starts(rows.System));
        Assert.All(rows.System, r => Assert.Equal("remote", r.Speakers[0].SpeakerId));
    }

    [Fact]
    public void Speaker_changes_still_cut_rows_and_each_word_keeps_its_diarizer_speaker()
    {
        var rows = Build([], [Line("s", false, Talk(0, 10))], new DiarizationSegment("speaker_1", 0, 5), new DiarizationSegment("speaker_2", 5, 10));

        Assert.Equal(["speaker-1", "speaker-2"], rows.System.Select(r => r.Speakers[0].SpeakerId));
        Assert.All(rows.System, r => Assert.All(r.Words!, w => Assert.Equal(r.Speakers[0].SpeakerId, w.SpeakerId)));
    }

    [Fact]
    public void A_lone_reply_between_another_speakers_turns_keeps_its_own_speaker()
    {
        // Three recognizer windows: speaker 1, a one-word reply by speaker 2 alone in its window, speaker 1 again.
        var rows = Build(
            [],
            [Line("s0", false, Talk(0, 10)), Line("s1", false, [(40.0, 40.3)]), Line("s2", false, Talk(80, 90))],
            new DiarizationSegment("speaker_1", 0, 10.2), new DiarizationSegment("speaker_2", 39.9, 40.4), new DiarizationSegment("speaker_1", 79.9, 90.2));

        Assert.Equal(["speaker-1", "speaker-2", "speaker-1"], rows.System.Select(r => r.Speakers[0].SpeakerId));
    }

    [Fact]
    public void A_one_word_flicker_inside_continuous_speech_is_still_repaired()
    {
        // The diarizer gives the word at 5.1 s to speaker 2, between two stretches of speaker 1 with no pause around it.
        var rows = Build(
            [],
            [Line("s", false, Talk(0, 10))],
            new DiarizationSegment("speaker_1", 0, 5.05), new DiarizationSegment("speaker_2", 5.05, 5.35), new DiarizationSegment("speaker_1", 5.35, 10.2));

        Assert.Equal("speaker-1", Assert.Single(rows.System).Speakers[0].SpeakerId);
    }

    [Fact]
    public void A_You_line_that_only_repeats_the_speakers_is_hidden_and_cuts_nothing()
    {
        string[] said = ["alpha", "bravo", "charlie", "delta"];
        var withTimes = Line("m0", true, Talk(10.1, 11.3), said);
        var textOnly = new TranscriptSegment
        {
            Id = "m1",
            Start = TimeSpan.FromSeconds(10.1),
            End = TimeSpan.FromSeconds(11.3),
            RawText = string.Join(' ', said),
            State = SegmentState.Final,
            ResultVersion = 2,
            TimingProvenance = TimingProvenance.ApproximateChunk,
            Speakers = [new SpeakerAttribution("local", TimeSpan.FromSeconds(10.1), TimeSpan.FromSeconds(11.3), null)]
        };

        var rows = Build([withTimes, textOnly], [Line("s", false, Talk(9, 13), [.. Enumerable.Range(0, 3).Select(i => $"x{i}"), .. said, .. Enumerable.Range(0, 7).Select(i => $"y{i}")])], new DiarizationSegment("speaker_1", 8.9, 13.2));

        // With word times every word is hidden and kept; without them the line is listed to be hidden whole.
        var hidden = rows.Microphone.Single(r => r.Words is not null);
        Assert.All(hidden.Words!, w => Assert.True(w.Hidden));
        Assert.Equal(string.Empty, hidden.DisplayText);
        Assert.Equal(["m1"], rows.EchoLines);
        Assert.Single(rows.System);
    }

    [Fact]
    public void Lines_redone_by_diarizer_turn_are_cut_at_a_pause_too()
    {
        var turn = Line("ft0.0", false, [.. Talk(2, 4), .. Talk(5.5, 7)]) with { Speakers = [new SpeakerAttribution("speaker-3", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(7), null)] };

        var rows = MeetingFinalPass.BuildRows([], [], [turn], [new DiarizationSegment("speaker_3", 1.9, 7.1)]);

        Assert.Equal([2.0, 5.5], Starts(rows.System));
        Assert.All(rows.System, r => Assert.Equal("speaker-3", r.Speakers[0].SpeakerId));
        Assert.All(rows.System, r => Assert.StartsWith("ft0.0.", r.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void Several_You_lines_inside_one_remote_row_cut_it_where_each_leaves_real_pieces()
    {
        // You lines start at 10.2 (would leave a 0.2 s first piece), 15.0 and 20.0 (cut), and 29.8 (would leave 0.2 s at the end).
        var microphone = new[] { 10.2, 15.0, 20.0, 29.8 }.Select((t, i) => Line($"m{i}", true, [(t, t + 0.25), (t + 0.3, t + 0.6)])).ToArray();

        var rows = Build(microphone, [Line("s", false, Talk(10, 30))], new DiarizationSegment("speaker_1", 9.9, 30.1));

        Assert.Equal([10.0, 15.1, 20.2], Starts(rows.System));
        Assert.Equal(4, rows.Microphone.Count);
        var ids = rows.Microphone.Concat(rows.System).Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void A_row_runs_on_across_the_recognizer_windows()
    {
        // The chunker cut the audio at 28 s, in a 0.1 s gap between words: that is not a turn.
        var rows = Build(
            [Line("m0", true, Talk(1, 27.9)), Line("m1", true, Talk(28.0, 31))],
            [Line("s0", false, Talk(40, 55.8)), Line("s1", false, Talk(55.9, 60))],
            new DiarizationSegment("speaker_1", 39.9, 60.2));

        Assert.Single(rows.Microphone);
        Assert.Single(rows.System);
        Assert.Equal(Talk(40, 55.8).Length + Talk(55.9, 60).Length, rows.System[0].Words!.Count);
    }

    [Fact]
    public void Echo_words_get_a_hidden_row_of_their_own_so_a_shown_You_row_starts_with_the_users_own_words()
    {
        string[] said = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot"];
        var system = Line("s", false, Talk(10, 11.8), said);
        var microphone = Line("m", true, [.. Talk(10.1, 11.9), (12.0, 12.2), (12.3, 12.5)], [.. said, "mine", "too"]);

        var rows = Build([microphone], [system], new DiarizationSegment("speaker_1", 9.9, 12));

        Assert.Equal(2, rows.Microphone.Count);
        Assert.All(rows.Microphone[0].Words!, w => Assert.True(w.Hidden));
        Assert.Equal(string.Empty, rows.Microphone[0].DisplayText);
        Assert.Equal("mine too", rows.Microphone[1].DisplayText);
        Assert.Equal(rows.Microphone[1].DisplayStart, rows.Microphone[1].Start);
        Assert.Single(rows.System);
    }
}
