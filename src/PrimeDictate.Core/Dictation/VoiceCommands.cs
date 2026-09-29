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
