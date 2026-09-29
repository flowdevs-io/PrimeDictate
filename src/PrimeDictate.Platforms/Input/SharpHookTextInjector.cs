using PrimeDictate.Core.Dictation;
using SharpHook;
using SharpHook.Data;

namespace PrimeDictate.Platforms.Input;

/// <summary>
/// Types final text with unicode key events on Windows, macOS and X11. Never uses the clipboard: a paste-and-restore
/// races with the target's asynchronous paste delivery and can put the wrong text back.
/// </summary>
public sealed class SharpHookTextInjector : ITextInjector
{
    private readonly EventSimulator simulator = new();

    public void TypeText(string text)
    {
        var target = text.Trim();
        if (target.Length == 0)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            WindowsSendInput.SendText(target);
            return;
        }

        var result = this.simulator.SimulateTextEntry(target);
        if (result != UioHookResult.Success)
        {
            throw new InvalidOperationException($"Text injection failed with status {result}.");
        }
    }

    public void SendEnter()
    {
        var result = this.simulator.SimulateKeyStroke([KeyCode.VcEnter]);
        if (result != UioHookResult.Success)
        {
            throw new InvalidOperationException($"Enter key simulation failed with status {result}.");
        }
    }
}
