using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>Draws <see cref="OverlayVisualizer"/>: waveform bars mirrored around the center and colored smoke particles.</summary>
public sealed class VisualizerControl : Control
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0, 210, 255), Color.FromRgb(157, 0, 255), Color.FromRgb(255, 0, 119), Color.FromRgb(255, 183, 0), Color.FromRgb(0, 255, 160)
    ];

    private static readonly IBrush[] ParticleBrushes = Palette.Select(c => (IBrush)new SolidColorBrush(c)).ToArray();
    private readonly OverlayVisualizer visualizer = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DateTime started = DateTime.UtcNow;
    private readonly IBrush barBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromRgb(0, 122, 204), 0), new GradientStop(Color.FromRgb(163, 62, 255), 1) }
    };

    public VisualizerControl()
    {
        this.Width = 300;
        this.Height = 90;
        this.IsHitTestVisible = false;
        this.timer.Tick += (_, _) =>
        {
            this.visualizer.Resize(this.Bounds.Width, this.Bounds.Height);
            this.visualizer.Tick((DateTime.UtcNow - this.started).TotalSeconds);
            this.InvalidateVisual();
        };
    }

    public void SetLevel(double rms) => this.visualizer.SetLevel(rms);

    public void SetRunning(bool running)
    {
        if (running)
        {
            this.timer.Start();
        }
        else
        {
            this.timer.Stop();
        }
    }

    public override void Render(DrawingContext context)
    {
        var w = this.Bounds.Width;
        var h = this.Bounds.Height;
        var mid = w / 2;
        var bars = this.visualizer.Bars;
        for (var i = 0; i < bars.Count; i++)
        {
            var barHeight = Math.Min(bars[i] * (h / 120.0), h);
            var offset = i * 2.0;
            using (context.PushOpacity(1.0 - ((double)i / bars.Count * 0.7)))
            {
                context.DrawRectangle(this.barBrush, null, new Rect(mid + offset, (h - barHeight) / 2, 1.5, barHeight), 1, 1);
                context.DrawRectangle(this.barBrush, null, new Rect(mid - offset - 2, (h - barHeight) / 2, 1.5, barHeight), 1, 1);
            }
        }

        foreach (var p in this.visualizer.Particles)
        {
            using (context.PushOpacity(p.Brightness))
            {
                context.DrawEllipse(ParticleBrushes[p.ColorIndex], null, new Point(p.X, p.Y), p.Size / 2, p.Size / 2);
            }
        }
    }
}
