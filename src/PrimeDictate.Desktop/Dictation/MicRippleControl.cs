using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>
/// The compact overlay's microphone: a dark disc with the microphone glyph in the state color, a white glow inside that follows the
/// voice level, and three rings that grow outward and fade (the WPF compact-mode ripple, driven by <see cref="CompactRipple"/>).
/// The animation timer only runs while it is told the microphone is live; a still frame is drawn otherwise.
/// </summary>
public sealed class MicRippleControl : Control
{
    private const double Size = 46;
    private static readonly Geometry Body = Geometry.Parse("M12,3 C9.8,3 8,4.8 8,7 L8,12 C8,14.2 9.8,16 12,16 C14.2,16 16,14.2 16,12 L16,7 C16,4.8 14.2,3 12,3 Z");
    private static readonly Geometry Cradle = Geometry.Parse("M6,11 L6,12 C6,15.31 8.69,18 12,18 C15.31,18 18,15.31 18,12 L18,11");
    private static readonly Geometry Stem = Geometry.Parse("M12,18 L12,21 M9,21 L15,21");
    private static readonly IBrush Face = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x26));
    private static readonly byte[] RingAlpha = [140, 115, 90];

    private readonly CompactRipple ripple = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private Color state = Color.FromRgb(32, 164, 112);

    public MicRippleControl()
    {
        this.Width = Size;
        this.Height = Size;
        this.IsHitTestVisible = false;
        this.timer.Tick += (_, _) =>
        {
            this.ripple.Tick();
            this.InvalidateVisual();
        };
    }

    public void SetLevel(double rms) => this.ripple.SetLevel(rms);

    /// <summary>Ready green, wake yellow, listening red, processing green, as in the WPF overlay.</summary>
    public void SetStateColor(Color color)
    {
        this.state = color;
        this.InvalidateVisual();
    }

    /// <summary>Animates while the microphone is live; stopping resets the rings so an idle microphone sits still.</summary>
    public void SetRunning(bool running)
    {
        if (running)
        {
            this.timer.Start();
            return;
        }

        this.timer.Stop();
        this.ripple.Reset();
        this.InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var center = new Point(Size / 2, Size / 2);
        var radius = Size / 2;
        var ink = new SolidColorBrush(this.state);
        context.DrawEllipse(Face, null, center, radius, radius);

        // The glow is lit from inside, so it is clipped to the disc.
        using (context.PushClip(new Rect(0, 0, Size, Size)))
        {
            var glow = this.ripple.GlowScale * radius;
            var brush = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.42, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.42, RelativeUnit.Relative),
                RadiusX = RelativeScalar.Parse("38%"),
                RadiusY = RelativeScalar.Parse("38%"),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xF2, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(0x66, 255, 255, 255), 0.35),
                    new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 1)
                }
            };
            using (context.PushOpacity(this.ripple.GlowOpacity))
            {
                context.DrawEllipse(brush, null, center, glow, glow);
            }

            for (var i = 0; i < CompactRipple.RingCount; i++)
            {
                var (scale, opacity) = this.ripple.Ring(i);
                var ring = new Pen(new SolidColorBrush(Color.FromArgb(RingAlpha[i], this.state.R, this.state.G, this.state.B)), 1.5);
                using (context.PushOpacity(opacity))
                {
                    context.DrawEllipse(null, ring, center, radius * scale * 0.92, radius * scale * 0.92);
                }
            }
        }

        context.DrawEllipse(null, new Pen(ink, 2), center, radius - 1, radius - 1);

        // The glyph is drawn on a 24x24 grid, scaled to 28 px (as in the WPF overlay) and nudged up a little.
        const double glyph = 28;
        var scaleFactor = glyph / 24.0;
        var offset = new Vector((Size - glyph) / 2, ((Size - glyph) / 2) - 1);
        using (context.PushTransform(Matrix.CreateScale(scaleFactor, scaleFactor) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            context.DrawGeometry(ink, null, Body);
            var line = new Pen(ink, 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(null, line, Cradle);
            context.DrawGeometry(null, line, Stem);
        }
    }
}
