using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PrimeDictate.Desktop.Dictation;

public enum TrayVisualState
{
    Ready,
    AlwaysListening,
    Recording,
    Processing,
    Error
}

/// <summary>
/// The WPF app's procedural tray icon (a harmonic "voice wire" ring with a chromatic offset and a glowing core),
/// redrawn with Avalonia so it works on every platform. Colors per state match the WPF palette.
/// </summary>
public static class TrayIconRenderer
{
    private const int Size = 64;

    private static readonly Dictionary<TrayVisualState, (WindowIcon Icon, RenderTargetBitmap Bitmap)> Cache = [];

    /// <summary>One icon per state, drawn the first time it is needed and reused after, as the WPF app keeps one icon per state. UI thread only.</summary>
    public static WindowIcon Create(TrayVisualState state)
    {
        if (!Cache.TryGetValue(state, out var entry))
        {
            var bitmap = Render(state);
            entry = (new WindowIcon(bitmap), bitmap);
            Cache[state] = entry;
        }

        return entry.Icon;
    }

    /// <summary>Releases the cached icons when the tray goes away.</summary>
    public static void DisposeCached()
    {
        foreach (var (_, bitmap) in Cache.Values)
        {
            bitmap.Dispose();
        }

        Cache.Clear();
    }

    public static RenderTargetBitmap Render(TrayVisualState state)
    {
        var (primary, chroma, glow) = state switch
        {
            TrayVisualState.AlwaysListening => (Color.FromRgb(255, 200, 0), Color.FromRgb(255, 90, 0), Color.FromRgb(255, 170, 0)),
            TrayVisualState.Recording => (Color.FromRgb(255, 0, 90), Color.FromRgb(180, 0, 255), Color.FromRgb(255, 40, 120)),
            TrayVisualState.Processing => (Color.FromRgb(0, 255, 160), Color.FromRgb(0, 180, 255), Color.FromRgb(0, 230, 190)),
            TrayVisualState.Error => (Color.FromRgb(255, 120, 0), Color.FromRgb(255, 0, 120), Color.FromRgb(255, 80, 0)),
            _ => (Color.FromRgb(0, 210, 255), Color.FromRgb(120, 80, 255), Color.FromRgb(0, 150, 255))
        };

        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using var context = bitmap.CreateDrawingContext();
        const double c = Size / 2.0;

        // Outer nebula wash.
        context.DrawEllipse(
            new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(70, glow.R, glow.G, glow.B), 0),
                    new GradientStop(Color.FromArgb(0, primary.R, primary.G, primary.B), 1)
                }
            },
            null, new Point(c, c), (Size / 2.0) - 2, (Size / 2.0) - 2);

        var r0 = Size * 0.32;
        var amplitude = Size * 0.05;
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(160, chroma.R, chroma.G, chroma.B)), 2.2), Ring(c, r0 + 1.5, amplitude, 5, 1.2, 0.15));
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(230, primary.R, primary.G, primary.B)), 2.5), Ring(c, r0, amplitude, 4, 0, 0));

        // Core.
        var core = Size * 0.14;
        context.DrawEllipse(
            new RadialGradientBrush
            {
                GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Color.FromArgb(180, primary.R, primary.G, primary.B), 1) }
            },
            new Pen(new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)), 1.2), new Point(c, c), core, core);
        return bitmap;
    }

    private static StreamGeometry Ring(double center, double radius, double amplitude, int lobes, double lobePhase, double angleShift)
    {
        const int points = 120;
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        for (var i = 0; i < points; i++)
        {
            var t = i * 2.0 * Math.PI / points;
            var r = radius + (amplitude * Math.Sin((lobes * t) + lobePhase));
            var p = new Point(center + (r * Math.Cos(t + angleShift)), center + (r * Math.Sin(t + angleShift)));
            if (i == 0)
            {
                g.BeginFigure(p, false);
            }
            else
            {
                g.LineTo(p);
            }
        }

        g.EndFigure(true);
        return geometry;
    }
}
