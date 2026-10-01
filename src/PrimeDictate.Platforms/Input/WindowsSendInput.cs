using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrimeDictate.Platforms.Input;

/// <summary>
/// Windows text entry in the WPF order: first insert straight into the focused edit control (EM_REPLACESEL); if
/// that is not an edit control, SendInput with real virtual-key strokes where the layout has them (so apps that
/// ignore synthetic unicode still work) and unicode events otherwise, after the hotkey modifiers are released.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsSendInput
{
    private const int InputKeyboard = 1;
    private const ushort VkShift = 0x10;
    private const ushort VkReturn = 0x0D;
    private const ushort VkTab = 0x09;
    private const int VkCapital = 0x14;
    private const uint KeyUp = 0x0002;
    private const uint KeyUnicode = 0x0004;
    private const int ShiftMod = 1;
    private const int CtrlMod = 2;
    private const int AltMod = 4;
    private const int MaxBatch = 128;
    private static readonly TimeSpan ModifierReleaseWait = TimeSpan.FromMilliseconds(750);

    /// <summary>Returns which route typed the text, for the log line (never the text itself).</summary>
    public static string SendText(string text) =>
        WindowsTextEntry.Enter(text, WindowsFocusedTextControl.TryReplaceSelection, SendKeys);

    /// <summary>Keyboard simulation only (the fallback route).</summary>
    public static void SendKeys(string text)
    {
        WaitForModifiersReleased();
        var layout = ForegroundLayout();
        var batch = new List<Win32.Input>(MaxBatch);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                continue;
            }

            AddCharacter(batch, text[i], layout);
            if (batch.Count >= MaxBatch)
            {
                Send(batch);
                batch.Clear();
                Thread.Sleep(1);
            }
        }

        Send(batch);
    }

    private static void AddCharacter(List<Win32.Input> inputs, char c, IntPtr layout)
    {
        if (c is '\r' or '\n')
        {
            AddStroke(inputs, VkReturn, false);
            return;
        }

        if (c == '\t')
        {
            AddStroke(inputs, VkTab, false);
            return;
        }

        var translated = Win32.VkKeyScanEx(c, layout);
        if (translated != -1)
        {
            var modifiers = (translated >> 8) & 0xFF;
            if ((modifiers & (CtrlMod | AltMod)) == 0)
            {
                var shift = (modifiers & ShiftMod) != 0;
                if (char.IsLetter(c) && (Win32.GetKeyState(VkCapital) & 1) != 0)
                {
                    shift = !shift;
                }

                AddStroke(inputs, (ushort)(translated & 0xFF), shift);
                return;
            }
        }

        inputs.Add(Unicode(c, false));
        inputs.Add(Unicode(c, true));
    }

    private static void AddStroke(List<Win32.Input> inputs, ushort vk, bool shift)
    {
        if (shift)
        {
            inputs.Add(Key(VkShift, false));
        }

        inputs.Add(Key(vk, false));
        inputs.Add(Key(vk, true));
        if (shift)
        {
            inputs.Add(Key(VkShift, true));
        }
    }

    private static void Send(List<Win32.Input> inputs)
    {
        if (inputs.Count == 0)
        {
            return;
        }

        var array = inputs.ToArray();
        var sent = Win32.SendInput((uint)array.Length, array, Marshal.SizeOf<Win32.Input>());
        if (sent != array.Length)
        {
            throw new InvalidOperationException(
                $"Text injection sent {sent:N0} of {array.Length:N0} input events. Win32 error: {Marshal.GetLastWin32Error()}.");
        }
    }

    private static Win32.Input Unicode(char c, bool up) => new()
    {
        Type = InputKeyboard,
        Union = new Win32.InputUnion { Keyboard = new Win32.KeyboardInput { Scan = c, Flags = KeyUnicode | (up ? KeyUp : 0) } }
    };

    private static Win32.Input Key(ushort vk, bool up) => new()
    {
        Type = InputKeyboard,
        Union = new Win32.InputUnion { Keyboard = new Win32.KeyboardInput { VirtualKey = vk, Flags = up ? KeyUp : 0 } }
    };

    private static void WaitForModifiersReleased()
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < ModifierReleaseWait && (Down(0x10) || Down(0x11) || Down(0x12) || Down(0x5B) || Down(0x5C)))
        {
            Thread.Sleep(10);
        }
    }

    private static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static IntPtr ForegroundLayout()
    {
        var window = Win32.GetForegroundWindow();
        var thread = window == IntPtr.Zero ? Win32.GetCurrentThreadId() : Win32.GetWindowThreadProcessId(window, out _);
        return Win32.GetKeyboardLayout(thread);
    }
}
