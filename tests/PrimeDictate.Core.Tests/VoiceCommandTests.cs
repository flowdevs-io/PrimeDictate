using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Core.Tests;

public sealed class VoiceCommandTests
{
    private static readonly VoiceCommandOptions Options = new(true, "thank you", "potato farmer", "show me the money", []);

    [Theory]
    [InlineData("send the report thank you", "send the report")]
    [InlineData("send the report, thanks.", "send the report,")]
    [InlineData("Thank you send the report", "send the report")]
    public void Commit_phrase_and_its_variants_are_removed_and_request_a_commit(string spoken, string cleaned)
    {
        var m = VoiceCommandMatcher.Apply(spoken, Options);
        Assert.True(m.CommitRequested);
        Assert.Equal(cleaned, m.CleanedText);
    }

    [Fact]
    public void Ok_matches_okay_in_either_direction()
    {
        var opts = Options with { DictationPhrase = "okay computer" };
        Assert.True(VoiceCommandMatcher.Apply("run it ok computer", opts).CommitRequested);
        var opts2 = Options with { DictationPhrase = "ok computer" };
        Assert.True(VoiceCommandMatcher.Apply("run it okay computer", opts2).CommitRequested);
    }

    [Fact]
    public void Stop_and_history_phrases_are_recognized()
    {
        Assert.True(VoiceCommandMatcher.Apply("never mind potato farmer", Options).StopRequested);
        Assert.True(VoiceCommandMatcher.Apply("show me the money", Options).HistoryRequested);
    }

    [Fact]
    public void Disabled_commands_pass_text_through_untouched()
    {
        var m = VoiceCommandMatcher.Apply("thank you very much", Options with { Enabled = false });
        Assert.False(m.CommitRequested);
        Assert.Equal("thank you very much", m.CleanedText);
    }

    [Fact]
    public void Shell_phrases_are_only_matched_when_asked_and_chain_type_text()
    {
        var shell = new VoiceShellCommand { Phrase = "open notes", Command = "notepad" };
        var opts = Options with { ShellCommands = [shell] };
        Assert.Null(VoiceCommandMatcher.Apply("open notes", opts).ShellCommandInvocation);
        var m = VoiceCommandMatcher.Apply("open notes and then type hello", opts, includeShellCommands: true);
        Assert.Equal("hello", m.ShellCommandInvocation!.TextToType);
    }

    [Fact]
    public void Processor_drops_shell_invocations_so_dictation_never_runs_commands_yet()
    {
        var proc = new VoiceCommandProcessor(() => Options with { ShellCommands = [new VoiceShellCommand { Phrase = "format disk", Command = "x" }] });
        Assert.Equal("format disk", proc.Apply("format disk").CleanedText);
    }
}
