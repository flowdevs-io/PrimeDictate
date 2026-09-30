using System.Diagnostics;
using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Platforms.Dictation;

/// <summary>
/// Runs a voice shell command through the platform's command interpreter (<c>cmd.exe /d /c</c> on Windows, <c>/bin/sh -c</c>
/// elsewhere) with no window, from the user's profile folder. The command is passed as one argument, exactly as saved.
/// </summary>
public sealed class ProcessVoiceShellCommandRunner : IVoiceShellCommandRunner
{
    public VoiceShellCommandResult Run(VoiceShellCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var text = command.Command.Trim();
        if (text.Length == 0)
        {
            throw new InvalidOperationException("Voice command has no command configured.");
        }

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (OperatingSystem.IsWindows())
        {
            var interpreter = Environment.GetEnvironmentVariable("ComSpec");
            startInfo.FileName = string.IsNullOrWhiteSpace(interpreter) ? "cmd.exe" : interpreter;
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
        }

        startInfo.ArgumentList.Add(text);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The system did not start the command process.");
        return new VoiceShellCommandResult(process.Id);
    }
}
