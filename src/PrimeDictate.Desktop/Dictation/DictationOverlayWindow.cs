using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PrimeDictate.Core.Dictation;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>
/// Live dictation preview: a small always-on-top window that never takes focus, so it cannot pull the caret away
/// from the app being dictated into. Compact mode is a microphone pill that grows to show text while listening;
/// full mode is a panel shown only while dictating (or always when pinned). Live text is shown here and never typed.
/// </summary>
public sealed class DictationOverlayWindow : Window
{
    private readonly Border levelBar = new() { Height = 4, Width = 0, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.LimeGreen };
    private readonly TextBlock status = new() { FontSize = 12, Opacity = 0.7 };
    private readonly TextBlock text = new() { FontSize = 15, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
    private readonly Ellipse dot = new() { Width = 10, Height = 10, Fill = Brushes.Gray };
    private OverlayStyle style = OverlayStyle.CompactMicrophone;
    private bool sticky;
    private DictationState state;
    private DateTime hideAtUtc = DateTime.MaxValue;
    private readonly DispatcherTimer hideTimer;

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
        this.Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x1E, 0x24)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { this.dot, this.status } },
                    this.text,
                    this.levelBar
                }
            }
        };
        this.status.Foreground = Brushes.White;
        this.text.Foreground = Brushes.White;
        this.hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        this.hideTimer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow >= this.hideAtUtc)
            {
                this.hideAtUtc = DateTime.MaxValue;
                this.Refresh();
            }
        };
        this.hideTimer.Start();
        this.Opened += (_, _) => this.ApplyNativeFlags();
    }

    public void Configure(OverlayStyle style, bool sticky)
    {
        this.style = style;
        this.sticky = sticky;
        this.Refresh();
    }

    public void SetState(DictationState state)
    {
        this.state = state;
        if (state == DictationState.Listening)
        {
            this.text.Text = string.Empty;
        }
        else if (state == DictationState.Idle)
        {
            // Leave the final text readable for a moment before the panel goes away.
            this.hideAtUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
        }

        this.Refresh();
    }

    public void SetTranscript(string transcript)
    {
        this.text.Text = transcript;
        this.Refresh();
    }

    public void SetLevel(double rms) =>
        this.levelBar.Width = Math.Clamp(Math.Sqrt(rms) * 6, 0, 1) * 200;

    public void SetNotice(string message)
    {
        this.text.Text = message;
        this.hideAtUtc = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        this.Refresh();
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

        var showText = !string.IsNullOrEmpty(this.text.Text) && (active || DateTime.UtcNow < this.hideAtUtc);
        this.text.IsVisible = showText && (this.style == OverlayStyle.FullPanel || active || DateTime.UtcNow < this.hideAtUtc);
        this.levelBar.IsVisible = this.state == DictationState.Listening;
        var visible = active || DateTime.UtcNow < this.hideAtUtc || this.sticky || this.style == OverlayStyle.CompactMicrophone;
        if (visible && !this.IsVisible)
        {
            this.Show();
            this.PositionLowerCenter();
        }
        else if (!visible && this.IsVisible)
        {
            this.Hide();
        }
        else if (visible)
        {
            this.PositionLowerCenter();
        }
    }

    private void PositionLowerCenter()
    {
        var screen = this.Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scale = screen.Scaling;
        var area = screen.WorkingArea;
        var width = (int)(this.Bounds.Width * scale);
        var height = (int)(this.Bounds.Height * scale);
        this.Position = new PixelPoint(area.X + ((area.Width - width) / 2), area.Bottom - height - (int)(48 * scale));
    }

    /// <summary>Windows: no-activate, tool window and click-through so the overlay can never steal focus or clicks.</summary>
    private void ApplyNativeFlags()
    {
        if (!OperatingSystem.IsWindows() || this.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero)
        {
            return;
        }

        Win32Overlay.MakeNonActivating(handle);
    }
}
