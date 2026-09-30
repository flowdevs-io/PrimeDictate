using PrimeDictate.Core.Pipeline;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Core.Tests;

public sealed class EchoMatcherTests
{
    private static TranscriptSegment Line(string id, string speaker, double start, string text, double perWord = 0.35)
    {
        var words = text.Split(' ').Select((w, i) => new WordTiming(w, TimeSpan.FromSeconds(start + (i * perWord)), TimeSpan.FromSeconds(start + ((i + 1) * perWord)), null, TimingProvenance.Model, speaker)).ToList();
        var end = words[^1].End;
        return TestData.Segment(id, text, start, end.TotalSeconds) with
        {
            Words = words,
            Speakers = [new SpeakerAttribution(speaker, words[0].Start, end, null)]
        };
    }

    private const string Remote = "Like documentation to go with it to our end users that's like, hey, look, use this type of billing model as far as your credits go";

    [Fact]
    public void The_tail_of_the_speakers_words_is_dropped_and_the_users_own_words_stay()
    {
        var system = Line("us0.0", "speaker-1", 0, Remote, 0.35);
        var mic = Line("um0.0", "local", 8.0, "As far as your credits go, yeah now totally", 0.35);

        var verdict = EchoMatcher.Judge(mic, [system]);

        Assert.NotNull(verdict);
        Assert.False(verdict.HideAll);
        var trimmed = verdict.Trimmed!;
        Assert.Equal("yeah now totally", trimmed.RawText);
        Assert.Equal(3, trimmed.Words!.Count);
        Assert.Equal(TimeSpan.FromSeconds(8.0 + (6 * 0.35)), trimmed.Start);
        Assert.Equal(mic.End, trimmed.End);
        Assert.Equal(trimmed.Start, trimmed.Speakers[0].Start);
        Assert.True(trimmed.Revision > mic.Revision);
    }

    [Fact]
    public void A_line_that_is_only_echo_is_hidden_even_with_a_couple_of_misheard_words()
    {
        var system = Line("us0.0", "speaker-1", 0.5, Remote);
        var mic = Line("um0.0", "local", 0.6, "Like I can go with it to our end users that's like hey look use this type of billing model");

        Assert.True(EchoMatcher.Judge(mic, [system])!.HideAll);
    }

    [Fact]
    public void Single_shared_words_and_lines_far_from_the_system_audio_are_left_alone()
    {
        var system = Line("us0.0", "speaker-1", 0, Remote);

        Assert.Null(EchoMatcher.Judge(Line("m1", "local", 2, "yeah go for it"), [system]));
        Assert.Null(EchoMatcher.Judge(Line("m2", "local", 60, "as far as your credits go"), [system]));
        Assert.Null(EchoMatcher.Judge(Line("m3", "local", 2, "okay so what about the pricing") with { EditedText = "mine" }, [system]));
    }
}
