using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PrimeDictate.Core.Transcripts;

namespace PrimeDictate.Desktop;

/// <summary>
/// Who spoke when: one lane per speaker, a colored bar per segment on a shared time axis. Provisional
/// (live, not yet final) bars are drawn translucent with a dashed outline, so a live label reads as
/// tentative and settles when the utterance is final. The same drawing serves live and saved sessions.
/// Mouse wheel pans, Ctrl+wheel zooms; while live the view follows the newest audio until the user pans away.
/// </summary>
public sealed class SpeakerTimelineControl : Control
{
    private const double LabelWidth = 96;
    private const double AxisHeight = 20;
    private const double LaneHeight = 26;
    private const double LaneGap = 4;

    private IReadOnlyList<Lane> lanes = [];
    private IReadOnlyList<Bar> bars = [];
    private double totalSeconds;
    private double viewSeconds = 120;
    private double viewStart;
    private bool follow = true;
    private bool live;
    private string? selectedSegmentId;

    private sealed record Lane(string SpeakerId, string Name, int Index);

    private sealed record Bar(string SegmentId, int Lane, double Start, double End, bool Provisional);

    /// <summary>Raised with a segment id when a bar is clicked.</summary>
    public event Action<string>? SegmentClicked;

    public string? SelectedSegmentId
    {
        get => this.selectedSegmentId;
        set
        {
            if (this.selectedSegmentId != value)
            {
                this.selectedSegmentId = value;
                this.InvalidateVisual();
            }
        }
    }

    /// <summary>Height the control needs for the current number of lanes.</summary>
    public double DesiredContentHeight => AxisHeight + (this.lanes.Count * (LaneHeight + LaneGap)) + 6;

    public void SetDocument(TranscriptDocument? document, bool isLive)
    {
        this.live = isLive;
        if (document is null || document.Speakers.Count == 0)
        {
            this.lanes = [];
            this.bars = [];
            this.totalSeconds = 0;
            this.InvalidateVisual();
            return;
        }

        var lanes = document.Speakers.Select((s, i) => new Lane(s.Id, s.Name, i)).ToList();
        var bars = new List<Bar>();
        double end = 0;
        foreach (var segment in document.ActiveSegments)
        {
            var provisional = segment.State != SegmentState.Final;
            foreach (var attribution in segment.Speakers.Count > 0 ? segment.Speakers : [])
            {
                var lane = lanes.FindIndex(l => l.SpeakerId == attribution.SpeakerId);
                if (lane < 0)
                {
                    continue;
                }

                var s = attribution.Start.TotalSeconds;
                var e = Math.Max(attribution.End.TotalSeconds, s + 0.05);
                bars.Add(new Bar(segment.Id, lane, s, e, provisional));
                end = Math.Max(end, e);
            }
        }

        this.lanes = lanes;
        this.bars = bars;
        this.totalSeconds = Math.Max(end, document.Duration?.TotalSeconds ?? 0);
        this.ClampView();
        this.InvalidateVisual();
    }

    private void ClampView()
    {
        if (!this.live && this.follow)
        {
            // A saved session opens showing all of it (up to a readable maximum) from the start.
            this.viewSeconds = Math.Clamp(this.totalSeconds, 10, 600);
            this.viewStart = 0;
            return;
        }

        if (this.follow)
        {
            this.viewStart = Math.Max(0, this.totalSeconds - this.viewSeconds + 5);
        }

        this.viewStart = Math.Clamp(this.viewStart, 0, Math.Max(0, this.totalSeconds - (this.viewSeconds * 0.2)));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (this.lanes.Count == 0)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var factor = e.Delta.Y > 0 ? 0.8 : 1.25;
            var pointer = e.GetPosition(this).X;
            var t = this.XToTime(pointer);
            this.viewSeconds = Math.Clamp(this.viewSeconds * factor, 5, 3600);
            this.viewStart = t - ((pointer - LabelWidth) / Math.Max(1, this.Bounds.Width - LabelWidth) * this.viewSeconds);
        }
        else
        {
            this.viewStart -= e.Delta.Y * this.viewSeconds * 0.1;
        }

        // Panning away from the newest audio stops following; panning back to the end resumes it.
        this.follow = false;
        this.ClampView();
        if (this.viewStart + this.viewSeconds >= this.totalSeconds)
        {
            this.follow = this.live;
        }

        this.InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        var t = this.XToTime(p.X);
        var lane = (int)((p.Y - AxisHeight) / (LaneHeight + LaneGap));
        var hit = this.bars.Where(b => b.Lane == lane && t >= b.Start && t <= b.End).OrderBy(b => b.End - b.Start).FirstOrDefault();
        if (hit is not null)
        {
            this.selectedSegmentId = hit.SegmentId;
            this.SegmentClicked?.Invoke(hit.SegmentId);
            this.InvalidateVisual();
        }
    }

    private double XToTime(double x) => this.viewStart + (Math.Max(0, x - LabelWidth) / Math.Max(1, this.Bounds.Width - LabelWidth) * this.viewSeconds);

    private double TimeToX(double t) => LabelWidth + ((t - this.viewStart) / this.viewSeconds * (this.Bounds.Width - LabelWidth));

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = this.Bounds.Width;
        if (this.lanes.Count == 0 || width <= LabelWidth + 10)
        {
            return;
        }

        var foreground = this.FindResource("SystemControlForegroundBaseHighBrush") as IBrush ?? Brushes.Gray;
        var faint = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
        var typeface = new Typeface(FontFamily.Default);

        // Time axis with ticks at a readable interval.
        var step = TickStep(this.viewSeconds / Math.Max(1, (width - LabelWidth) / 90));
        for (var t = Math.Ceiling(this.viewStart / step) * step; t <= this.viewStart + this.viewSeconds; t += step)
        {
            var x = this.TimeToX(t);
            context.DrawLine(new Pen(faint, 1), new Point(x, AxisHeight - 4), new Point(x, this.DesiredContentHeight));
            DrawText(context, FormatAxis(t), typeface, 10, foreground, new Point(x + 3, 1));
        }

        for (var i = 0; i < this.lanes.Count; i++)
        {
            var top = AxisHeight + (i * (LaneHeight + LaneGap));
            var color = SpeakerPalette.For(this.lanes[i].Index);
            context.DrawRectangle(faint, null, new Rect(LabelWidth, top, width - LabelWidth, LaneHeight), 3, 3);
            context.DrawRectangle(new SolidColorBrush(color), null, new Rect(2, top + 7, 10, 10), 2, 2);
            DrawText(context, this.lanes[i].Name, typeface, 12, foreground, new Point(18, top + 5), LabelWidth - 22);
        }

        foreach (var bar in this.bars)
        {
            var x1 = Math.Max(LabelWidth, this.TimeToX(bar.Start));
            var x2 = Math.Min(width, this.TimeToX(bar.End));
            if (x2 <= LabelWidth || x1 >= width)
            {
                continue;
            }

            var color = SpeakerPalette.For(this.lanes[bar.Lane].Index);
            var top = AxisHeight + (bar.Lane * (LaneHeight + LaneGap)) + 2;
            var selected = bar.SegmentId == this.selectedSegmentId;
            var fill = new SolidColorBrush(color, bar.Provisional ? 0.35 : 0.9);
            var pen = selected
                ? new Pen(foreground, 2)
                : bar.Provisional ? new Pen(new SolidColorBrush(color), 1.2, new DashStyle([3, 2], 0)) : null;
            context.DrawRectangle(fill, pen, new Rect(x1, top, Math.Max(2, x2 - x1), LaneHeight - 4), 3, 3);
        }
    }

    private static void DrawText(DrawingContext context, string text, Typeface typeface, double size, IBrush brush, Point origin, double maxWidth = double.PositiveInfinity)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        if (!double.IsInfinity(maxWidth))
        {
            formatted.MaxTextWidth = maxWidth;
        }
        context.DrawText(formatted, origin);
    }

    private static double TickStep(double roughSeconds)
    {
        double[] steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];
        return steps.FirstOrDefault(s => s >= roughSeconds, 3600);
    }

    private static string FormatAxis(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
