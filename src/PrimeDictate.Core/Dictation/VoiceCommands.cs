namespace PrimeDictate.Core.Dictation;

public sealed record VoiceCommandResult(string CleanedText, bool CommitRequested, bool StopRequested, bool HistoryRequested)
{
    public static VoiceCommandResult Passthrough(string text) => new(text, false, false, false);
}

/// <summary>
/// Spoken commands ("stop", "commit", history) recognized in dictation text. Only the dictation controller
/// uses this; transcription mode never matches commands and never types into other apps.
/// </summary>
public interface IVoiceCommandProcessor
{
    VoiceCommandResult Apply(string transcript);
}

public sealed class NoVoiceCommands : IVoiceCommandProcessor
{
    public static NoVoiceCommands Instance { get; } = new();

    public VoiceCommandResult Apply(string transcript) => VoiceCommandResult.Passthrough(transcript);
}

/// <summary>Spoken commands for dictation, using the WPF app's matcher and default phrases.</summary>
public sealed class VoiceCommandProcessor(Func<VoiceCommandOptions> options) : IVoiceCommandProcessor
{
    public const string DefaultDictationPhrase = "thank you";
    public const string DefaultStopPhrase = "potato farmer";
    public const string DefaultHistoryPhrase = "show me the money";

    public VoiceCommandResult Apply(string transcript)
    {
        var match = VoiceCommandMatcher.Apply(transcript, options());
        return new VoiceCommandResult(match.CleanedText, match.CommitRequested, match.StopRequested, match.HistoryRequested);
    }
}
