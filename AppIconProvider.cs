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

    /// <summary>
    /// Renders a mathematical smoke & chroma voice-wire icon procedurally using harmonic curves,
    /// chromatic aberration offsets, and atmospheric gradient washes.
    /// </summary>
    public static Icon CreateTrayIcon(TrayVisualState state)
    {
        const int size = 64; // High DPI base resolution for crisp anti-aliasing
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            // Palette base definition
            var (primaryColor, chromaColor, glowColor) = state switch
            {
                TrayVisualState.Ready => (
                    Color.FromArgb(0, 210, 255),    // Cosmic Cyan
                    Color.FromArgb(120, 80, 255),   // Deep Violet
                    Color.FromArgb(0, 150, 255)
                ),
                TrayVisualState.AlwaysListening => (
                    Color.FromArgb(255, 200, 0),    // Solar Gold
                    Color.FromArgb(255, 90, 0),     // Plasma Amber
                    Color.FromArgb(255, 170, 0)
                ),
                TrayVisualState.Recording => (
                    Color.FromArgb(255, 0, 90),     // Pulse Crimson
                    Color.FromArgb(180, 0, 255),    // Neon Violet
                    Color.FromArgb(255, 40, 120)
                ),
                TrayVisualState.Processing => (
                    Color.FromArgb(0, 255, 160),    // Quantum Emerald
                    Color.FromArgb(0, 180, 255),    // Cyber Blue
                    Color.FromArgb(0, 230, 190)
                ),
                TrayVisualState.Error => (
                    Color.FromArgb(255, 120, 0),    // Supernova Orange
                    Color.FromArgb(255, 0, 120),    // Magenta Drift
                    Color.FromArgb(255, 80, 0)
                ),
                _ => (
                    Color.FromArgb(0, 210, 255),
                    Color.FromArgb(120, 80, 255),
                    Color.FromArgb(0, 150, 255)
                )
            };

            var cx = size / 2f;
            var cy = size / 2f;

            // 1. Outer Ethereal Smoke Nebula Wash
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(2, 2, size - 4, size - 4);
                using var pbr = new PathGradientBrush(path)
                {
                    CenterPoint = new PointF(cx, cy),
                    CenterColor = Color.FromArgb(70, glowColor.R, glowColor.G, glowColor.B),
                    SurroundColors = new[] { Color.FromArgb(0, primaryColor.R, primaryColor.G, primaryColor.B) }
                };
                g.FillPath(pbr, path);
            }

            // 2. Harmonic Chroma Lissajous Orbits (Voice Wire Math)
            // Parametric curves: r(theta) = R0 + A * sin(k * theta)
            int pointCount = 120;
            float r0 = size * 0.32f;
            float waveAmplitude = size * 0.05f;

            // Layer A: Secondary Chroma Shift (Violet/Plasma)
            PointF[] chromaPoints = new PointF[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                float t = (float)(i * 2.0 * Math.PI / pointCount);
                float r = r0 + waveAmplitude * (float)Math.Sin(5 * t + 1.2);
                chromaPoints[i] = new PointF(
                    cx + (r + 1.5f) * (float)Math.Cos(t + 0.15),
                    cy + (r + 1.5f) * (float)Math.Sin(t + 0.15)
                );
            }
            using (var penChroma = new Pen(Color.FromArgb(160, chromaColor.R, chromaColor.G, chromaColor.B), 2.2f))
            {
                g.DrawClosedCurve(penChroma, chromaPoints);
            }

            // Layer B: Primary Neon Voice Line
            PointF[] primaryPoints = new PointF[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                float t = (float)(i * 2.0 * Math.PI / pointCount);
                float r = r0 + waveAmplitude * (float)Math.Sin(4 * t);
                primaryPoints[i] = new PointF(
                    cx + r * (float)Math.Cos(t),
                    cy + r * (float)Math.Sin(t)
                );
            }
            using (var penPrimary = new Pen(Color.FromArgb(230, primaryColor.R, primaryColor.G, primaryColor.B), 2.5f))
            {
                g.DrawClosedCurve(penPrimary, primaryPoints);
            }

            // 3. Central Radiant Wire Singularity Core
            float coreRadius = size * 0.14f;
            using (var corePath = new GraphicsPath())
            {
                corePath.AddEllipse(cx - coreRadius, cy - coreRadius, coreRadius * 2, coreRadius * 2);
                using var coreBrush = new PathGradientBrush(corePath)
                {
                    CenterPoint = new PointF(cx, cy),
                    CenterColor = Color.FromArgb(255, 255, 255, 255),
                    SurroundColors = new[] { Color.FromArgb(180, primaryColor.R, primaryColor.G, primaryColor.B) }
                };
                g.FillPath(coreBrush, corePath);
            }

            // High-light core border
            using (var corePen = new Pen(Color.FromArgb(240, 255, 255, 255), 1.2f))
            {
                g.DrawEllipse(corePen, cx - coreRadius, cy - coreRadius, coreRadius * 2, coreRadius * 2);
            }
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
