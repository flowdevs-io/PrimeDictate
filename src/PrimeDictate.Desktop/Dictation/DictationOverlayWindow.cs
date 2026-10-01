using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>
/// Live dictation preview: a small always-on-top window that never takes focus, so it cannot pull the caret away from the app being
/// dictated into. It has the WPF overlay's controls: a pin (keep it on screen), a copy button (copies the last transcript to the clipboard,
/// only when clicked; this is not text injection), a settings button, collapse and expand between the compact microphone and the full panel, close,
/// the elapsed time and a "Local only" badge. Compact mode is the microphone with the ripple animation; full mode adds the particle visualizer and a
/// text box with the latest words. Like the WPF overlay, the compact microphone stays on screen (see <see cref="OverlayRules.ShouldShow"/>).
/// Both modes keep one size while words arrive, so nothing jumps. It can be dragged anywhere (the spot is remembered). Live text is shown here and never typed.
/// </summary>
public sealed class DictationOverlayWindow : Window
{
    private const double CompactWidth = 350;
    private const double FullWidth = 460;
    private const int MaxShownCharacters = 900;
    private static readonly TimeSpan NoticeTime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan FinalTextTime = TimeSpan.FromSeconds(1.5);

    private readonly VisualizerControl visualizer = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly MicRippleControl mic = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock compactStatus = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock title = new() { Text = "Live transcript", FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock text = new() { FontSize = 15, LineHeight = 20, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock timerText = new() { Text = "00:00", FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ScrollViewer textView;
    private readonly Ellipse dot = new() { Width = 10, Height = 10, Fill = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border panel;
    private readonly Control fullHeader;
    private readonly Control compactHeader;
    private readonly Control footer;
    private readonly Control stateRow;
    private readonly ToggleButton pinFull;
    private readonly ToggleButton pinCompact;
    private readonly Button copyFull;
    private readonly Button copyCompact;
    private readonly Button collapse;
    private readonly DispatcherTimer hideTimer;
    private readonly DispatcherTimer clockTimer;
    private readonly DispatcherTimer anchorSaveTimer;
    private OverlayStyle style = OverlayStyle.CompactMicrophone;
    private bool? expandedOverride;
    private bool sticky;
    private bool hideCompactWhenIdle;
    private bool dismissed;
    private bool placing;
    private bool dragging;
    private bool moved;
    private bool clickCandidate;
    private PixelPoint pressedAt;
    private bool showingNotice;
    private bool wakeListening;
    private string backend = "Whisper ONNX";
    private string transcript = string.Empty;
    private string lastTranscript = string.Empty;
    private string notice = string.Empty;
    private DateTime listeningSince;
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
        this.title.Foreground = Brushes.White;
        this.compactStatus.Foreground = Brushes.White;
        this.text.Foreground = Brushes.White;
        this.timerText.Foreground = Brushes.White;
        this.textView = new ScrollViewer
        {
            Content = this.text,
            Height = 80, // four whole lines, so none is cut in half
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden
        };

        // None of the buttons can take keyboard focus, so pressing one never moves it away from the app being dictated into.
        this.pinFull = PinButton();
        this.pinCompact = PinButton();
        this.copyFull = CopyButton();
        this.copyCompact = CopyButton("📋");
        this.collapse = IconButton("—", "Back to the compact microphone");
        var expand = IconButton("⤢", "Show the full panel");
        var settings = IconButton("⚙", "Settings");
        var settingsCompact = IconButton("⚙", "Settings");
        var close = IconButton("✕", "Hide until the next dictation");
        var closeCompact = IconButton("✕", "Hide until the next dictation");
        foreach (var pin in new[] { this.pinFull, this.pinCompact })
        {
            pin.Click += (_, _) => this.SetPinned(pin.IsChecked == true, raise: true);
        }

        foreach (var copy in new[] { this.copyFull, this.copyCompact })
        {
            copy.Click += async (_, _) => await this.CopyLastTranscriptAsync();
        }

        this.collapse.Click += (_, _) => this.Collapse();
        expand.Click += (_, _) => this.Expand();
        settings.Click += (_, _) => this.SettingsRequested?.Invoke();
        settingsCompact.Click += (_, _) => this.SettingsRequested?.Invoke();
        close.Click += (_, _) => this.CloseClicked();
        closeCompact.Click += (_, _) => this.CloseClicked();

        var compactButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Children = { this.copyCompact, this.pinCompact, settingsCompact, expand, closeCompact } };
        this.compactHeader = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(compactButtons, Dock.Right);
        ((Panel)this.compactHeader).Children.Add(compactButtons);
        ((Panel)this.compactHeader).Children.Add(this.mic);
        ((Panel)this.compactHeader).Children.Add(this.compactStatus);
        this.mic.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(this.mic, Dock.Left);

        var fullButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Children = { this.pinFull, settings, this.collapse, close } };
        this.fullHeader = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(fullButtons, Dock.Right);
        ((Panel)this.fullHeader).Children.Add(fullButtons);
        ((Panel)this.fullHeader).Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "🎙", FontSize = 17, VerticalAlignment = VerticalAlignment.Center }, this.title } });

        this.stateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { this.dot, this.status } };

        var badge = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3),
            Child = new TextBlock { Text = "Local only", FontSize = 12, Foreground = Brushes.White, Opacity = 0.8 }
        };
        ToolTip.SetTip(badge, "Transcript preview stays on this device. Audio is not uploaded.");
        ToolTip.SetTip(this.timerText, "Elapsed dictation time");
        var footerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(this.timerText, 1);
        Grid.SetColumn(this.copyFull, 2);
        footerGrid.Children.Add(badge);
        footerGrid.Children.Add(this.timerText);
        footerGrid.Children.Add(this.copyFull);
        this.footer = footerGrid;

        this.panel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0D, 0x11, 0x17)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10, 8, 12),
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new StackPanel { Spacing = 8, Children = { this.compactHeader, this.fullHeader, this.visualizer, this.stateRow, this.textView, this.footer } }
        };
        this.Content = this.panel;

        this.hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        this.hideTimer.Tick += (_, _) =>
        {
            if (this.hideAtUtc != DateTime.MinValue && DateTime.UtcNow >= this.hideAtUtc)
            {
                // The notice or the final words have been shown long enough; what stays on screen goes back to its idle wording.
                this.hideAtUtc = DateTime.MinValue;
                this.showingNotice = false;
                this.notice = string.Empty;
                this.transcript = string.Empty;
                this.Refresh();
            }
        };
        this.hideTimer.Start();

        this.clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        this.clockTimer.Tick += (_, _) => this.timerText.Text = OverlayRules.Elapsed(DateTime.UtcNow - this.listeningSince);

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
                this.clickCandidate = true;
                this.pressedAt = this.Position;
                this.BeginMoveDrag(e);
                this.anchorSaveTimer.Stop();
                this.anchorSaveTimer.Start();
                // On Windows the drag runs inside BeginMoveDrag, so a press that never moved the window ends right here: that is a click.
                if (OperatingSystem.IsWindows() && this.clickCandidate && this.Position == this.pressedAt)
                {
                    this.clickCandidate = false;
                    this.OnPanelClicked();
                }
            }
        };
        this.PointerReleased += (_, _) =>
        {
            // Elsewhere the window manager reports the release when nothing was dragged.
            if (!OperatingSystem.IsWindows() && this.clickCandidate && this.Position == this.pressedAt)
            {
                this.clickCandidate = false;
                this.OnPanelClicked();
            }
        };
        this.PositionChanged += (_, _) =>
        {
            if (this.dragging && !this.placing)
            {
                this.clickCandidate = false;
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

    /// <summary>The pin was toggled; the app saves it as <c>IsOverlaySticky</c>.</summary>
    public event Action<bool>? PinChanged;

    /// <summary>The gear was clicked.</summary>
    public event Action? SettingsRequested;

    /// <summary>The effective look: the configured one, unless the user expanded or collapsed it for now.</summary>
    private OverlayStyle EffectiveStyle => this.expandedOverride switch
    {
        true => OverlayStyle.FullPanel,
        false => OverlayStyle.CompactMicrophone,
        _ => this.style
    };

    public void Configure(OverlayStyle style, bool sticky, bool hideCompactWhenIdle, int? anchorX, int? anchorY)
    {
        this.style = style;
        this.expandedOverride = null;
        this.hideCompactWhenIdle = hideCompactWhenIdle;
        this.SetPinned(sticky, raise: false);
        this.anchor = anchorX is { } x && anchorY is { } y ? new PixelPoint(x, y) : null;
        this.Refresh();
        if (this.IsVisible)
        {
            this.PlaceAtAnchor(this.Bounds.Size);
        }
    }

    /// <summary>The model family shown in the header and tooltip, such as "Whisper.net (GGML)".</summary>
    public void SetBackendLabel(string label)
    {
        this.backend = label;
        this.Refresh();
    }

    /// <summary>True while the wake word is listening on the idle microphone ("Wake listening", yellow).</summary>
    public void SetWakeListening(bool listening)
    {
        this.wakeListening = listening;
        this.Refresh();
    }

    public void SetState(DictationState state)
    {
        var before = this.state;
        this.state = state;
        if (state == DictationState.Listening)
        {
            this.dismissed = false;
            this.showingNotice = false;
            this.notice = string.Empty;
            this.transcript = string.Empty;
            this.lastTranscript = string.Empty;
            this.hideAtUtc = DateTime.MinValue;
            if (before != DictationState.Listening)
            {
                this.listeningSince = DateTime.UtcNow;
                this.timerText.Text = "00:00";
            }
        }
        else if (state == DictationState.Idle)
        {
            // Leave the final words readable for a moment before the overlay goes away, without cutting a notice short.
            var finalWords = DateTime.UtcNow + FinalTextTime;
            this.hideAtUtc = this.hideAtUtc > finalWords ? this.hideAtUtc : finalWords;
        }

        this.clockTimer.IsEnabled = state == DictationState.Listening;
        if (state == DictationState.Idle)
        {
            this.timerText.Text = "00:00";
        }

        this.Refresh();
    }

    public void SetTranscript(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            this.lastTranscript = value.Trim();
        }

        this.transcript = value.Length > MaxShownCharacters ? "…" + value[^MaxShownCharacters..].TrimStart() : value;
        this.Refresh();
    }

    /// <summary>Remembers the final transcript so the Copy button can still copy it after dictation ended. Kept in memory only.</summary>
    public void SetCommitted(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            this.lastTranscript = value.Trim();
            this.Refresh();
        }
    }

    public void SetLevel(double rms)
    {
        this.visualizer.SetLevel(rms);
        this.mic.SetLevel(rms);
    }

    public void SetNotice(string message)
    {
        // A notice is worth seeing even after the overlay was closed (for example why nothing was typed).
        this.dismissed = false;
        this.showingNotice = true;
        this.notice = message;
        this.hideAtUtc = DateTime.UtcNow + NoticeTime;
        this.Refresh();
    }

    private static Button IconButton(string glyph, string tip)
    {
        var button = new Button
        {
            Content = glyph,
            Focusable = false,
            FontSize = 13,
            Padding = new Thickness(7, 2),
            MinHeight = 0,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            Opacity = 0.75,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        return button;
    }

    private static ToggleButton PinButton()
    {
        var pin = new ToggleButton
        {
            Content = "📌",
            Focusable = false,
            FontSize = 13,
            Padding = new Thickness(7, 2),
            MinHeight = 0,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            Opacity = 0.75,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(pin, "Keep open on screen");
        return pin;
    }

    private static Button CopyButton(string? glyph = null)
    {
        var copy = IconButton(glyph ?? "📋  Copy", "Copy the last transcript");
        copy.IsEnabled = false;
        copy.BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255));
        copy.BorderThickness = glyph is null ? new Thickness(1) : new Thickness(0);
        return copy;
    }

    private void SetPinned(bool pinned, bool raise)
    {
        this.sticky = pinned;
        this.pinFull.IsChecked = pinned;
        this.pinCompact.IsChecked = pinned;
        var opacity = pinned ? 1.0 : 0.75;
        this.pinFull.Opacity = opacity;
        this.pinCompact.Opacity = opacity;
        if (raise)
        {
            this.PinChanged?.Invoke(pinned);
        }

        this.Refresh();
    }

    private void Expand()
    {
        this.expandedOverride = true;
        this.Refresh();
    }

    private void Collapse()
    {
        this.expandedOverride = false;
        this.Refresh();
    }

    /// <summary>A click (not a drag) on the compact microphone opens the full panel, as in the WPF overlay.</summary>
    private void OnPanelClicked()
    {
        if (this.EffectiveStyle == OverlayStyle.CompactMicrophone)
        {
            this.Expand();
        }
    }

    /// <summary>Like the WPF overlay, closing a panel that was opened from the compact microphone goes back to the microphone; otherwise it hides until the next dictation.</summary>
    private void CloseClicked()
    {
        if (this.expandedOverride == true && this.style == OverlayStyle.CompactMicrophone && !this.sticky)
        {
            this.Collapse();
            return;
        }

        this.dismissed = true;
        this.showingNotice = false;
        this.Refresh();
    }

    private async Task CopyLastTranscriptAsync()
    {
        if (!OverlayRules.IsCopyable(this.lastTranscript) || this.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(this.lastTranscript);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        {
            // The clipboard can be held by another program for a moment; the user can click again.
        }
    }

    private OverlayPhase Phase => this.state switch
    {
        DictationState.Listening => OverlayPhase.Listening,
        DictationState.Processing => OverlayPhase.Processing,
        _ => this.wakeListening ? OverlayPhase.WakeListening : OverlayPhase.Ready
    };

    private void Refresh()
    {
        var phase = this.Phase;
        var color = phase switch
        {
            OverlayPhase.Listening => Color.FromRgb(220, 53, 69),
            OverlayPhase.Processing => Color.FromRgb(255, 165, 0),
            OverlayPhase.WakeListening => Color.FromRgb(255, 214, 10),
            _ => Color.FromRgb(32, 164, 112)
        };
        this.dot.Fill = new SolidColorBrush(color);
        this.mic.SetStateColor(color);
        var effective = this.EffectiveStyle;
        var full = effective == OverlayStyle.FullPanel;
        var header = OverlayRules.Header(phase, this.backend);
        this.status.Text = header;
        // The compact microphone has room for one short word; the model family is in the tooltip.
        this.compactStatus.Text = phase switch
        {
            OverlayPhase.Listening => "Listening",
            OverlayPhase.Processing => "Transcribing",
            OverlayPhase.WakeListening => "Wake listening",
            _ => "Ready"
        };
        ToolTip.SetTip(this.panel, header);

        var shown = this.showingNotice
            ? this.notice
            : this.transcript.Length > 0 ? this.transcript : OverlayRules.Placeholder(phase, this.backend);
        this.text.Text = shown;
        Dispatcher.UIThread.Post(() => this.textView.ScrollToEnd(), DispatcherPriority.Background);
        var copyable = OverlayRules.IsCopyable(this.lastTranscript);
        foreach (var copy in new[] { this.copyFull, this.copyCompact })
        {
            copy.IsEnabled = copyable;
            ToolTip.SetTip(copy, copyable ? "Copy the last transcript" : "Nothing to copy yet: wait for a transcript");
        }

        this.panel.Width = full ? FullWidth : CompactWidth;
        this.panel.CornerRadius = new CornerRadius(full ? 12 : 30);
        this.fullHeader.IsVisible = full;
        this.compactHeader.IsVisible = !full;
        this.stateRow.IsVisible = full;
        this.visualizer.IsVisible = full;
        this.footer.IsVisible = full;
        // Full mode keeps its text box while shown, empty or not, so the size never changes; compact mode shows text only for a notice.
        this.textView.IsVisible = full || this.showingNotice;

        this.visualizer.SetRunning(full && phase == OverlayPhase.Listening);
        this.mic.SetRunning(!full && phase == OverlayPhase.Listening);

        var lingering = this.hideAtUtc != DateTime.MinValue && DateTime.UtcNow < this.hideAtUtc;
        var visible = OverlayRules.ShouldShow(this.dismissed, this.state != DictationState.Idle, lingering, this.sticky, this.style, this.hideCompactWhenIdle);
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
