using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Core.Tests;

public sealed class DictationReplacementTests
{
    [Fact]
    public void Longest_find_string_wins_over_a_shorter_overlap()
    {
        var rules = new[] { new ReplacementRule("new", "NEW"), new ReplacementRule("new york", "NYC") };
        Assert.Equal("I love NYC", TranscriptReplacements.Apply("I love new york", rules));
    }

    [Fact]
    public void Matching_ignores_case_and_replacement_is_literal()
    {
        var rules = new[] { new ReplacementRule("  Primed ictate ", "PrimeDictate $1") };
        Assert.Equal("use PrimeDictate $1 now", TranscriptReplacements.Apply("use primed ictate now", rules));
    }

    [Fact]
    public void Empty_input_empty_rules_and_blank_find_strings_change_nothing()
    {
        Assert.Equal(string.Empty, TranscriptReplacements.Apply(string.Empty, [new ReplacementRule("a", "b")]));
        Assert.Equal("text", TranscriptReplacements.Apply("text", []));
        Assert.Equal("text", TranscriptReplacements.Apply("text", [new ReplacementRule("  ", "x")]));
    }

    [Fact]
    public void Null_replacement_deletes_the_match()
    {
        Assert.Equal("a  b", TranscriptReplacements.Apply("a um b", [new ReplacementRule("um", null)]));
    }
}
