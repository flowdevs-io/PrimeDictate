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

    /// <summary>Set by <see cref="TypeText"/>: none after an edit-control insertion, else <see cref="EnterTiming.AfterKeystrokes"/>.</summary>
    public TimeSpan EnterDelay { get; private set; }

    public void TypeText(string text)
    {
        this.EnterDelay = TimeSpan.Zero;
        var target = text.Trim();
        if (target.Length == 0)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var route = WindowsSendInput.SendText(target);
            PrimeDictate.Core.Diagnostics.AppLog.Event("dictation", route);
            this.EnterDelay = route == WindowsTextEntry.FocusedControlRoute ? TimeSpan.Zero : EnterTiming.AfterKeystrokes(target.Length);
            return;
        }

        var result = this.simulator.SimulateTextEntry(target);
        if (result != UioHookResult.Success)
        {
            throw new InvalidOperationException($"Text injection failed with status {result}.");
        }

        this.EnterDelay = EnterTiming.AfterKeystrokes(target.Length);
    }

    public void SendEnter()
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsSendInput.SendEnter();
            PrimeDictate.Core.Diagnostics.AppLog.Event("dictation", $"Enter sent {this.EnterDelay.TotalMilliseconds:0} ms after the text.");
            return;
        }

        var result = this.simulator.SimulateKeyStroke([KeyCode.VcEnter]);
        if (result != UioHookResult.Success)
        {
            throw new InvalidOperationException($"Enter key simulation failed with status {result}.");
        }
    }
}
