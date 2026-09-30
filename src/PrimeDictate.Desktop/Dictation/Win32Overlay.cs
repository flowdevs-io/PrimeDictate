using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrimeDictate.Desktop.Dictation;

[SupportedOSPlatform("windows")]
internal static class Win32Overlay
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;

    public static void MakeNonActivating(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        _ = SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExToolWindow | WsExTransparent));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newLong);
}
