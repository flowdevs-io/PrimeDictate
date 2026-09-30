namespace PrimeDictate.Core.Dictation;

public readonly record struct VoiceShellCommandResult(int? ProcessId);

/// <summary>
/// Starts a user-configured command. Implementations receive the command exactly as the user saved it in settings;
/// nothing spoken or typed is ever part of it.
/// </summary>
public interface IVoiceShellCommandRunner
{
    /// <exception cref="InvalidOperationException">The command is empty or the process did not start.</exception>
    VoiceShellCommandResult Run(VoiceShellCommand command);
}
