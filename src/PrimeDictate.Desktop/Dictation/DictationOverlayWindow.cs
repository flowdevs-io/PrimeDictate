using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>
/// Live dictation preview: a small always-on-top window that never takes focus, so it cannot pull the caret away from
/// the app being dictated into. It shows only while dictating (and briefly for a notice or the final words), unless
/// pinned; the tray icon is there the rest of the time. Compact mode is a microphone pill with the level; full mode adds
/// a text box with the latest words. Both keep one size while words arrive, so nothing jumps. It can be dragged anywhere
/// (the spot is remembered) and closed until the next dictation. Live text is shown here and never typed.
/// </summary>
public sealed class DictationOverlayWindow : Window
{
    private const double CompactWidth = 300;
    private const double FullWidth = 460;
    private const int MaxShownCharacters = 900;
    private static readonly TimeSpan NoticeTime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan FinalTextTime = TimeSpan.FromSeconds(1.5);

    private readonly VisualizerControl visualizer = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock status = new() { FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock text = new() { FontSize = 15, LineHeight = 20, TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer textView;
    private readonly Ellipse dot = new() { Width = 10, Height = 10, Fill = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border panel;
    private readonly DispatcherTimer hideTimer;
    private readonly DispatcherTimer anchorSaveTimer;
    private OverlayStyle style = OverlayStyle.CompactMicrophone;
    private bool sticky;
    private bool dismissed;
    private bool placing;
    private bool dragging;
    private bool moved;
    private bool showingNotice;
    private DictationState state;
    private DateTime hideAtUtc = DateTime.MinValue;
    private PixelPoint? anchor;

    public DictationOverlayWindow()
    {
        this.WindowDecorations = WindowDecorations.None;
        this.Topmost = true;
        this.ShowInTaskbar = false;
        this.ShowActivated = false;
        this.Focusable = false;
        this.CanResize = false;
        this.SizeToContent = SizeToContent.WidthAndHeight;
        this.Background = Brushes.Transparent;
        this.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        this.status.Foreground = Brushes.White;
        this.text.Foreground = Brushes.White;
        this.textView = new ScrollViewer
        {
            Content = this.text,
            Height = 80, // four whole lines, so none is cut in half
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden
        };

        // Not focusable, so pressing it never moves keyboard focus away from the app being dictated into.
        var close = new Button
        {
            Content = "✕",
            Focusable = false,
            FontSize = 11,
            Padding = new Thickness(6, 0),
            MinHeight = 0,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            Opacity = 0.7,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(close, "Hide until the next dictation");
        close.Click += (_, _) => this.Dismiss();
        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { this.dot, this.status } });

        this.panel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x1E, 0x24)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 8, 8, 10),
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new StackPanel { Spacing = 6, Children = { header, this.textView, this.visualizer } }
        };
        this.Content = this.panel;

        this.hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        this.hideTimer.Tick += (_, _) =>
        {
            if (this.hideAtUtc != DateTime.MinValue && DateTime.UtcNow >= this.hideAtUtc)
            {
                // The notice or the final words have been shown long enough; a pinned overlay goes back to an empty "Ready".
                this.hideAtUtc = DateTime.MinValue;
                this.showingNotice = false;
                this.text.Text = string.Empty;
                this.Refresh();
            }
        };
        this.hideTimer.Start();

        // Only a move the user started counts: Windows runs the drag inside BeginMoveDrag, other systems after it returns.
        // Either way the drag is over once the overlay has been still for a moment, and then the new spot is saved.
        this.anchorSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        this.anchorSaveTimer.Tick += (_, _) =>
        {
            this.anchorSaveTimer.Stop();
            this.dragging = false;
            if (this.moved && this.anchor is { } spot)
            {
                this.moved = false;
                this.AnchorMoved?.Invoke(spot.X, spot.Y);
            }
        };

        this.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                this.dragging = true;
                this.BeginMoveDrag(e);
                this.anchorSaveTimer.Stop();
                this.anchorSaveTimer.Start();
            }
        };
        this.PositionChanged += (_, _) =>
        {
            if (this.dragging && !this.placing)
            {
                this.anchor = this.CurrentAnchor();
                this.moved = true;
                this.anchorSaveTimer.Stop();
                this.anchorSaveTimer.Start();
            }
        };
        // Growing or shrinking (a notice, a mode change) keeps the bottom edge where it was, so it never jumps sideways.
        this.SizeChanged += (_, e) =>
        {
            if (!this.dragging)
            {
                this.PlaceAtAnchor(e.NewSize);
            }
        };
        this.Opened += (_, _) => this.ApplyNativeFlags();
    }

    /// <summary>The user moved the overlay: the bottom-center point it now grows from, in screen pixels.</summary>
    public event Action<int, int>? AnchorMoved;

    public void Configure(OverlayStyle style, bool sticky, int? anchorX, int? anchorY)
    {
        this.style = style;
        this.sticky = sticky;
        this.anchor = anchorX is { } x && anchorY is { } y ? new PixelPoint(x, y) : null;
        this.Refresh();
        if (this.IsVisible)
        {
            this.PlaceAtAnchor(this.Bounds.Size);
        }
    }

    public void SetState(DictationState state)
    {
        this.state = state;
        if (state == DictationState.Listening)
        {
            this.dismissed = false;
            this.showingNotice = false;
            this.text.Text = string.Empty;
        }
        else if (state == DictationState.Idle)
        {
            // Leave the final words readable for a moment before the overlay goes away, without cutting a notice short.
            var finalWords = DateTime.UtcNow + FinalTextTime;
            this.hideAtUtc = this.hideAtUtc > finalWords ? this.hideAtUtc : finalWords;
        }

        this.Refresh();
    }

    public void SetTranscript(string transcript)
    {
        if (this.showingNotice)
        {
            return;
        }

        this.ShowText(transcript.Length > MaxShownCharacters ? "…" + transcript[^MaxShownCharacters..].TrimStart() : transcript);
        this.Refresh();
    }

    public void SetLevel(double rms) => this.visualizer.SetLevel(rms);

    public void SetNotice(string message)
    {
        // A notice is worth seeing even after the overlay was closed (for example why nothing was typed).
        this.dismissed = false;
        this.showingNotice = true;
        this.ShowText(message);
        this.hideAtUtc = DateTime.UtcNow + NoticeTime;
        this.Refresh();
    }

    private void Dismiss()
    {
        this.dismissed = true;
        this.showingNotice = false;
        this.Refresh();
    }

    private void ShowText(string value)
    {
        this.text.Text = value;
        Dispatcher.UIThread.Post(() => this.textView.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void Refresh()
    {
        var active = this.state != DictationState.Idle;
        this.dot.Fill = this.state switch
        {
            DictationState.Listening => Brushes.IndianRed,
            DictationState.Processing => Brushes.Orange,
            _ => Brushes.SeaGreen
        };
        this.status.Text = this.state switch
        {
            DictationState.Listening => "Listening",
            DictationState.Processing => "Transcribing",
            _ => "Ready"
        };

        var lingering = this.hideAtUtc != DateTime.MinValue && DateTime.UtcNow < this.hideAtUtc;
        var full = this.style == OverlayStyle.FullPanel;
        this.panel.Width = full ? FullWidth : CompactWidth;
        // Full mode keeps its text box while shown, empty or not, so the size never changes; compact mode shows text only for a notice.
        this.textView.IsVisible = full || this.showingNotice;
        this.visualizer.SetRunning(this.state == DictationState.Listening);
        var visible = !this.dismissed && (active || lingering || this.sticky);
        if (visible && !this.IsVisible)
        {
            // Placed before it appears (measured first), so it never flashes in a corner.
            this.panel.Measure(Size.Infinity);
            this.PlaceAtAnchor(this.panel.DesiredSize);
            this.Show();
        }
        else if (!visible && this.IsVisible)
        {
            this.Hide();
        }
    }

    /// <summary>The bottom-center point of the overlay on screen, in pixels.</summary>
    private PixelPoint CurrentAnchor()
    {
        var scale = this.DesktopScaling;
        return new PixelPoint(this.Position.X + (int)(this.Bounds.Width * scale / 2), this.Position.Y + (int)(this.Bounds.Height * scale));
    }

    /// <summary>
    /// Puts the overlay's bottom-center on its anchor: where the user dragged it, or a little above the bottom of the
    /// primary screen. An anchor on a screen that is gone falls back to the default, and the overlay stays inside the screen.
    /// </summary>
    private void PlaceAtAnchor(Size size)
    {
        var screen = this.anchor is { } saved ? this.Screens.ScreenFromPoint(new PixelPoint(saved.X, saved.Y - 1)) : null;
        var point = screen is null ? (PixelPoint?)null : this.anchor;
        screen ??= this.Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        point ??= new PixelPoint(area.X + (area.Width / 2), area.Bottom - (int)(48 * scale));
        var width = (int)(size.Width * scale);
        var height = (int)(size.Height * scale);
        var x = Math.Clamp(point.Value.X - (width / 2), area.X, Math.Max(area.X, area.Right - width));
        var y = Math.Clamp(point.Value.Y - height, area.Y, Math.Max(area.Y, area.Bottom - height));
        if (x == this.Position.X && y == this.Position.Y)
        {
            return;
        }

        this.placing = true;
        try
        {
            this.Position = new PixelPoint(x, y);
        }
        finally
        {
            this.placing = false;
        }
    }

    /// <summary>Windows: no-activate and tool window, so the overlay never steals focus; it still takes clicks, to be dragged or closed.</summary>
    private void ApplyNativeFlags()
    {
        if (!OperatingSystem.IsWindows() || this.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero)
        {
            return;
        }

        Win32Overlay.MakeNonActivating(handle);
    }
}
