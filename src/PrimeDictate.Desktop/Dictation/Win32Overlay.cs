using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrimeDictate.Desktop.Dictation;

[SupportedOSPlatform("windows")]
internal static class Win32Overlay
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;

    /// <summary>
    /// Never activated (clicking it leaves focus where it was) and kept off Alt+Tab. Not click-through, as in the WPF
    /// overlay, so it can be dragged and closed.
    /// </summary>
    public static void MakeNonActivating(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        _ = SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExToolWindow));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newLong);
}
