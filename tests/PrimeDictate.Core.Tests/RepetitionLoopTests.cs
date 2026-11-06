using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Core.Tests;

public sealed class RepetitionLoopTests
{
    [Fact]
    public void A_word_stuck_on_repeat_collapses_to_one()
    {
        var text = "so the first image is like " + string.Join(' ', Enumerable.Repeat("like,", 20)) + " like.";
        Assert.True(RepetitionLoops.HasLoop(text));
        Assert.Equal("so the first image is like.", RepetitionLoops.Collapse(text));
    }

    [Fact]
    public void A_repeated_phrase_collapses_and_the_text_after_it_stays()
    {
        var text = "open the app " + string.Join(' ', Enumerable.Repeat("in the room", 6)) + " and close it";
        Assert.Equal(15, RepetitionLoops.LoopedWordCount(text));
        Assert.Equal("open the app in the room and close it", RepetitionLoops.Collapse(text));
    }

    [Fact]
    public void Ordinary_repetition_is_not_a_loop()
    {
        foreach (var text in new[] { "no no no no", "I really, really, really like it", "that that is is fine", "bye bye, bye bye, bye bye", "you know, you know, you know, you know" })
        {
            Assert.False(RepetitionLoops.HasLoop(text));
            Assert.Equal(text, RepetitionLoops.Collapse(text));
        }
    }

    [Fact]
    public void Case_and_punctuation_do_not_hide_a_loop()
    {
        Assert.Equal("ok then Like, done", RepetitionLoops.Collapse("ok then Like, like LIKE like. like, like like like like done"));
        Assert.Equal(string.Empty, RepetitionLoops.Collapse(string.Empty));
    }
}
