using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
namespace PrimeDictate;

internal enum TrayVisualState
{
    Ready,
    AlwaysListening,
    Recording,
    Processing,
    Error
}

internal static class AppIconProvider
{
    private const string IconFileName = "PrimeDictate.ico";

    public static Icon LoadWindowIcon()
    {
        foreach (var candidate in EnumerateIconCandidates())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                return new Icon(candidate);
            }
            catch (ArgumentException)
            {
            }
            catch (IOException)
            {
            }
        }

        return SystemIcons.Application;
    }

    public static Icon CreateTrayIcon(TrayVisualState state)
    {
        var color = state switch
        {
            TrayVisualState.Ready => Color.FromArgb(34, 122, 255),
            // Bright yellow so wake-on is obvious in the Windows tray (orange was too easy to miss).
            TrayVisualState.AlwaysListening => Color.FromArgb(255, 214, 10),
            TrayVisualState.Recording => Color.FromArgb(220, 53, 69),
            TrayVisualState.Processing => Color.FromArgb(32, 164, 112),
            TrayVisualState.Error => Color.FromArgb(245, 158, 11),
            _ => Color.FromArgb(34, 122, 255)
        };

        // 32px reads clearly on Win11 / high-DPI trays; Windows scales down as needed.
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var fillBrush = new SolidBrush(color);
            using var borderPen = new Pen(Color.FromArgb(40, 40, 40), 2f);
            using var centerBrush = new SolidBrush(Color.White);
            graphics.FillEllipse(fillBrush, 2, 2, size - 4, size - 4);
            graphics.DrawEllipse(borderPen, 2, 2, size - 4, size - 4);
            graphics.FillEllipse(centerBrush, 12, 12, 8, 8);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var unmanagedIcon = Icon.FromHandle(handle);
            return (Icon)unmanagedIcon.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    private static IEnumerable<string> EnumerateIconCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, IconFileName);
        yield return Path.Combine(Directory.GetCurrentDirectory(), IconFileName);
        yield return Path.Combine(Directory.GetCurrentDirectory(), "installer", "wix", "assets", IconFileName);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
