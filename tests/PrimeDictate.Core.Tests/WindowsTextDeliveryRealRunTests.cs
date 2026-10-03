using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Platforms.Input;

namespace PrimeDictate.Core.Tests;

/// <summary>
/// Real run on Windows: starts its own Notepad, types synthetic sentences into it by each delivery route and reads
/// the text back with WM_GETTEXT, then closes Notepad. Skipped unless PRIMEDICTATE_REAL_INPUT=1. Touches no other
/// window, but it does take focus for a few seconds, so do not type elsewhere while it runs.
/// </summary>
public sealed class WindowsTextDeliveryRealRunTests
{
    private const string FocusedSentence = "Direct insertion check, one two three.";
    private const string KeysSentence = "Keyboard route check café 4 5 6.";
    private const string FullPathSentence = "Full delivery path check seven eight nine.";

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Text_lands_in_notepad_by_every_windows_route()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("PRIMEDICTATE_REAL_INPUT") != "1")
        {
            return;
        }

        var before = TopLevel("Notepad");
        using var launcher = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        var window = WaitFor(() => TopLevel("Notepad").Except(before).FirstOrDefault(), TimeSpan.FromSeconds(15));
        Assert.NotEqual(IntPtr.Zero, window);
        _ = GetWindowThreadProcessId(window, out var pid);
        try
        {
            EnsureForeground(window);
            var guard = new WindowsForegroundGuard();
            var target = guard.Capture();
            Assert.NotNull(target);
            Assert.True(target!.IsStillForeground());

            var edit = FocusedEdit(window);
            Assert.NotEqual(IntPtr.Zero, edit);
            SetText(edit, string.Empty);

            // 1. Focused-control insertion, directly through the same call the target uses.
            Assert.True(WindowsFocusedTextControl.TryReplaceSelection(edit, FocusedSentence));
            Assert.Equal(FocusedSentence, GetText(edit));

            // 2. The target's direct injection while it is the foreground window (the WPF "return to start" shortcut).
            SetText(edit, string.Empty);
            Assert.True(target.TryInjectDirectly(FocusedSentence));
            Assert.Equal(FocusedSentence, GetText(edit));

            // 3. SendInput keyboard route.
            SetText(edit, string.Empty);
            EnsureForeground(window);
            WindowsSendInput.SendKeys(KeysSentence);
            _ = WaitFor(() => GetText(edit) == KeysSentence ? "ok" : null, TimeSpan.FromSeconds(5));
            Assert.Equal(KeysSentence, GetText(edit));

            // 3b. Coding-mode Enter through the same SendInput path: a line break follows the text, nothing else.
            WindowsSendInput.SendEnter();
            _ = WaitFor(() => TextLength(edit) > KeysSentence.Length ? "ok" : null, TimeSpan.FromSeconds(5));
            Assert.True(TextLength(edit) > KeysSentence.Length);
            Assert.Equal(KeysSentence, GetText(edit).TrimEnd('\n'));

            // 4. The full injector: must pick the focused-control route for an edit control.
            SetText(edit, string.Empty);
            EnsureForeground(window);
            new SharpHookTextInjector().TypeText(FullPathSentence);
            Assert.Equal(FullPathSentence, GetText(edit));

            // 5. Delivery with the guard and options as dictation uses them.
            SetText(edit, string.Empty);
            var result = TranscriptDelivery.Deliver(FullPathSentence, target, guard, new SharpHookTextInjector(), new DictationOptions { ReturnToStartTarget = true });
            Assert.Equal(DictationDeliveryStatus.Injected, result.Status);
            Assert.Equal(FullPathSentence, GetText(edit));

            SetText(edit, string.Empty);
        }
        finally
        {
            Close(window, (int)pid);
        }
    }

    private static void Close(IntPtr window, int pid)
    {
        if (IsWindow(window))
        {
            _ = PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.WaitForExit(3_000))
            {
                process.Kill();
            }
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    private static void EnsureForeground(IntPtr window)
    {
        for (var attempt = 0; attempt < 8 && GetForegroundWindow() != window; attempt++)
        {
            // A tap on Alt lets SetForegroundWindow through the foreground lock.
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            _ = SetForegroundWindow(window);
            Thread.Sleep(300);
        }

        Assert.Equal(window, GetForegroundWindow());
        Thread.Sleep(300);
    }

    private static IntPtr FocusedEdit(IntPtr window)
    {
        var thread = GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return WaitFor(
            () => GetGUIThreadInfo(thread, ref info) && info.FocusWindow != IntPtr.Zero && ClassOf(info.FocusWindow).Contains("Edit", StringComparison.OrdinalIgnoreCase)
                ? info.FocusWindow
                : IntPtr.Zero,
            TimeSpan.FromSeconds(10));
    }

    private static T? WaitFor<T>(Func<T?> probe, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var value = probe();
            if (value is not null && !value.Equals(default(T)))
            {
                return value;
            }

            Thread.Sleep(100);
        }

        return default;
    }

    private static List<IntPtr> TopLevel(string className)
    {
        var found = new List<IntPtr>();
        _ = EnumWindows(
            (hwnd, _) =>
            {
                if (ClassOf(hwnd) == className && IsWindowVisible(hwnd))
                {
                    found.Add(hwnd);
                }

                return true;
            },
            IntPtr.Zero);
        return found;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
    }

    private static string GetText(IntPtr edit)
    {
        var length = (int)SendMessage(edit, 0x000E, IntPtr.Zero, IntPtr.Zero); // WM_GETTEXTLENGTH
        var buffer = new StringBuilder(length + 2);
        _ = SendMessage(edit, 0x000D, new IntPtr(buffer.Capacity), buffer); // WM_GETTEXT
        return buffer.ToString().Replace("\r", string.Empty);
    }

    /// <summary>Length with line breaks as the control stores them (a bare CR counts).</summary>
    private static int TextLength(IntPtr edit) => (int)SendMessage(edit, 0x000E, IntPtr.Zero, IntPtr.Zero); // WM_GETTEXTLENGTH

    private static void SetText(IntPtr edit, string text) => SendMessage(edit, 0x000C, IntPtr.Zero, text); // WM_SETTEXT

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, string lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr ActiveWindow;
        public IntPtr FocusWindow;
        public IntPtr CaptureWindow;
        public IntPtr MenuOwnerWindow;
        public IntPtr MoveSizeWindow;
        public IntPtr CaretWindow;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
