using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Platforms.Input;

/// <summary>Windows foreground guard: remembers the window and process dictation started in, and can bring it back.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsForegroundGuard : IForegroundTargetGuard
{
    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public IForegroundTarget? Capture()
    {
        var handle = Win32.GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        _ = Win32.GetWindowThreadProcessId(handle, out var processId);
        return new WindowsTarget(handle, FocusedWindow(handle), processId, WindowTitle(handle), ProcessName(processId));
    }

    private static IntPtr FocusedWindow(IntPtr handle)
    {
        var threadId = Win32.GetWindowThreadProcessId(handle, out _);
        if (threadId == 0)
        {
            return IntPtr.Zero;
        }

        var info = new Win32.GuiThreadInfo { Size = Marshal.SizeOf<Win32.GuiThreadInfo>() };
        return Win32.GetGUIThreadInfo(threadId, ref info) ? info.FocusWindow : IntPtr.Zero;
    }

    private static string? WindowTitle(IntPtr handle)
    {
        var length = Win32.GetWindowTextLength(handle);
        if (length <= 0)
        {
            return null;
        }

        var title = new StringBuilder(length + 1);
        return Win32.GetWindowText(handle, title, title.Capacity) > 0 ? title.ToString() : null;
    }

    private static string? ProcessName(uint processId)
    {
        if (processId > int.MaxValue)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.IsNullOrWhiteSpace(process.ProcessName) ? null : process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed class WindowsTarget(IntPtr window, IntPtr focused, uint processId, string? title, string? processName) : IForegroundTarget
    {
        private const int SwRestore = 9;
        private static readonly TimeSpan ActivationTimeout = TimeSpan.FromMilliseconds(500);

        public string DisplayName => string.IsNullOrWhiteSpace(title) ? $"window 0x{window.ToInt64():X}" : $"{title} (0x{window.ToInt64():X})";

        public string? AppName => processName;

        public string? WindowTitle => title;

        public bool IsStillForeground()
        {
            var current = Win32.GetForegroundWindow();
            if (current != window)
            {
                return false;
            }

            _ = Win32.GetWindowThreadProcessId(current, out var currentPid);
            return currentPid == processId;
        }

        public bool TryInjectDirectly(string text)
        {
            if (focused == IntPtr.Zero ||
                !Win32.IsWindow(focused) ||
                (focused != window && !Win32.IsChild(window, focused)))
            {
                return false;
            }

            return WindowsFocusedTextControl.TryReplaceSelection(focused, text);
        }

        public bool TryRestore()
        {
            if (!Win32.IsWindow(window))
            {
                return false;
            }

            if (Win32.IsIconic(window))
            {
                _ = Win32.ShowWindowAsync(window, SwRestore);
            }

            var currentThread = Win32.GetCurrentThreadId();
            var foreground = Win32.GetForegroundWindow();
            var foregroundThread = foreground == IntPtr.Zero ? 0 : Win32.GetWindowThreadProcessId(foreground, out _);
            var targetThread = Win32.GetWindowThreadProcessId(window, out _);
            var attached = new List<uint>(2);
            try
            {
                Attach(currentThread, foregroundThread, attached);
                Attach(currentThread, targetThread, attached);
                _ = Win32.BringWindowToTop(window);
                _ = Win32.SetForegroundWindow(window);
                _ = Win32.SetActiveWindow(window);
                if (focused != IntPtr.Zero && Win32.IsWindow(focused) && (focused == window || Win32.IsChild(window, focused)))
                {
                    _ = Win32.SetFocus(focused);
                }
            }
            finally
            {
                foreach (var id in attached)
                {
                    _ = Win32.AttachThreadInput(currentThread, id, false);
                }
            }

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < ActivationTimeout)
            {
                if (this.IsStillForeground())
                {
                    return true;
                }

                Thread.Sleep(10);
            }

            return this.IsStillForeground();
        }

        private static void Attach(uint current, uint other, List<uint> attached)
        {
            if (other != 0 && other != current && !attached.Contains(other) && Win32.AttachThreadInput(current, other, true))
            {
                attached.Add(other);
            }
        }
    }
}
