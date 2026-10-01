using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PrimeDictate.Platforms.Input;

/// <summary>
/// Inserts text straight into the focused edit control with EM_REPLACESEL (replaces the selection, so a caret
/// just inserts). Ported from the WPF app. Only windows whose class name contains "Edit" are touched; anything
/// else returns false so the caller falls back to keyboard simulation. No clipboard involved.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFocusedTextControl
{
    private const int EmReplaceSel = 0x00C2;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint ReplaceSelectionTimeoutMs = 1_000;

    /// <summary>Inserts into whatever control has focus in the foreground window.</summary>
    public static bool TryReplaceSelection(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        var foreground = Win32.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        var threadId = Win32.GetWindowThreadProcessId(foreground, out _);
        var info = new Win32.GuiThreadInfo { Size = Marshal.SizeOf<Win32.GuiThreadInfo>() };
        return Win32.GetGUIThreadInfo(threadId, ref info) && TryReplaceSelection(info.FocusWindow, text);
    }

    public static bool TryReplaceSelection(IntPtr focusedWindow, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (focusedWindow == IntPtr.Zero || !Win32.IsWindow(focusedWindow) || !IsEditLike(focusedWindow))
        {
            return false;
        }

        var delivered = Win32.SendMessageTimeout(
            focusedWindow, EmReplaceSel, new IntPtr(1), text, SmtoAbortIfHung, ReplaceSelectionTimeoutMs, out _);
        return delivered != IntPtr.Zero;
    }

    private static bool IsEditLike(IntPtr window)
    {
        var name = new StringBuilder(256);
        return Win32.GetClassName(window, name, name.Capacity) > 0 &&
            name.ToString().Contains("Edit", StringComparison.OrdinalIgnoreCase);
    }
}
