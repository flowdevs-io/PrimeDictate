using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MediaColor = System.Windows.Media.Color;

namespace PrimeDictate;

internal partial class TranscriptionOverlayWindow : Window
{
    private const double FullOverlayWidth = 500;
    private const double FullOverlayHeight = 400;
    private const double CompactOverlaySize = 140;
    private const double FullMicSize = 80;
    private const double CompactMicSize = 46;
    private const double FullGlowSize = 250;
    private const double CompactGlowSize = 140;
    private const double ParticleCanvasFallbackHeight = 120;
    private const int MaxDisplayedTranscriptChars = 900;
    private const int WaveformBarCount = 90;
    private const int ParticleCount = 150;
    private static readonly SolidColorBrush AccentBrush = CreateFrozenBrush(0, 122, 204);
    private static readonly SolidColorBrush ReadyBrush = CreateFrozenBrush(32, 164, 112);
    private static readonly SolidColorBrush WakeListeningBrush = CreateFrozenBrush(255, 214, 10);
    private static readonly SolidColorBrush RecordingBrush = CreateFrozenBrush(220, 53, 69);
    
    private readonly Random random = new Random();
    private readonly System.Windows.Shapes.Rectangle[] leftBars = new System.Windows.Shapes.Rectangle[WaveformBarCount];
    private readonly System.Windows.Shapes.Rectangle[] rightBars = new System.Windows.Shapes.Rectangle[WaveformBarCount];
    private readonly double[] barTargets = new double[WaveformBarCount];
    private readonly double[] barCurrents = new double[WaveformBarCount];
    
    private readonly System.Windows.Shapes.Ellipse[] pShapes = new System.Windows.Shapes.Ellipse[ParticleCount];
    private readonly double[] pX = new double[ParticleCount];
    private readonly double[] pY = new double[ParticleCount];
    private readonly double[] pVX = new double[ParticleCount];
    private readonly double[] pVY = new double[ParticleCount];
    private readonly double[] pLife = new double[ParticleCount];

    private readonly DispatcherTimer timer;
    private readonly DispatcherTimer visualizerTimer;
    private DateTime startTime;
    private double currentSmoothedRms;
    private double targetRms;
    private double compactPulseEnvelope;
    private double compactSurfaceEnvelope;
    private double compactRipplePhase;
    private bool particlesSeeded;
    private OverlayMode overlayMode = OverlayMode.CompactMicrophone;
    private SolidColorBrush currentStateBrush = ReadyBrush;

    public TranscriptionOverlayWindow()
    {
        InitializeComponent();
        this.SourceInitialized += this.OnSourceInitialized;
        this.Loaded += this.OnWindowLoaded;
        this.ParticleCanvas.SizeChanged += this.OnParticleCanvasSizeChanged;
        
        this.timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        this.timer.Tick += this.OnTimerTick;

        this.visualizerTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        this.visualizerTimer.Tick += this.OnVisualizerTick;

        this.InitializeWaveform();
        this.SetOverlayMode(OverlayMode.CompactMicrophone);
    }

    private void InitializeWaveform()
    {
        var gradientRight = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 0)
        };
        gradientRight.GradientStops.Add(new GradientStop(MediaColor.FromRgb(0, 122, 204), 0.0));
        gradientRight.GradientStops.Add(new GradientStop(MediaColor.FromRgb(163, 62, 255), 1.0));
        gradientRight.Freeze();
        
        var gradientLeft = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(1, 0),
            EndPoint = new System.Windows.Point(0, 0)
        };
        gradientLeft.GradientStops.Add(new GradientStop(MediaColor.FromRgb(0, 122, 204), 0.0));
        gradientLeft.GradientStops.Add(new GradientStop(MediaColor.FromRgb(163, 62, 255), 1.0));
        gradientLeft.Freeze();

        for (int i = 0; i < WaveformBarCount; i++)
        {
            // Drop opacity to 0.3 at edges instead of letting it fully disappear
            double opacity = 1.0 - (((double)i / WaveformBarCount) * 0.7);
            
            var leftBar = new System.Windows.Shapes.Rectangle
            {
                Width = 2,
                Height = 2,
                RadiusX = 1,
                RadiusY = 1,
                Fill = gradientLeft,
                Margin = new Thickness(0, 0, 2, 0),
                Opacity = opacity,
                VerticalAlignment = VerticalAlignment.Center
            };
            this.leftBars[i] = leftBar;
            this.LeftWaveform.Children.Add(leftBar);

            var rightBar = new System.Windows.Shapes.Rectangle
            {
                Width = 2,
                Height = 2,
                RadiusX = 1,
                RadiusY = 1,
                Fill = gradientRight,
                Margin = new Thickness(0, 0, 2, 0),
                Opacity = opacity,
                VerticalAlignment = VerticalAlignment.Center
            };
            this.rightBars[i] = rightBar;
            this.RightWaveform.Children.Add(rightBar);
        }

        var particleBrush = new SolidColorBrush(MediaColor.FromRgb(100, 200, 255));
        particleBrush.Freeze();
        for (int i = 0; i < ParticleCount; i++)
        {
            this.ResetParticle(i);
             
            this.pShapes[i] = new System.Windows.Shapes.Ellipse
            {
                Width = 2 + (this.random.NextDouble() * 2),
                Height = 2 + (this.random.NextDouble() * 2),
                Fill = particleBrush,
                Opacity = 0,
                IsHitTestVisible = false
            };
            // Start off-screen initially
            System.Windows.Controls.Canvas.SetLeft(this.pShapes[i], -100);
            System.Windows.Controls.Canvas.SetTop(this.pShapes[i], -100);
            this.ParticleCanvas.Children.Add(this.pShapes[i]);
        }
    }

    private void ResetParticle(int i, bool seedInFlight = false)
    {
        double frameWidth = this.GetParticleCanvasWidth();
        double frameHeight = this.GetParticleCanvasHeight();
        double centerY = frameHeight / 2.0;
        double travelRange = Math.Max(frameHeight * 0.46, 24.0);

        // Spread fully across the horizontal line width uniformly
        this.pX[i] = (this.random.NextDouble() * frameWidth);

        // Exact straight up/down
        this.pVX[i] = 0.0;
        
        // Shoot UP or DOWN
        bool goesUp = this.random.NextDouble() > 0.5;
        this.pVY[i] = (goesUp ? -1.0 : 1.0) * (0.8 + this.random.NextDouble() * 3.0);

        if (seedInFlight)
        {
            double progress = this.random.NextDouble();
            this.pY[i] = centerY + ((goesUp ? -1.0 : 1.0) * progress * travelRange);
            this.pLife[i] = 0.2 + ((1.0 - progress) * 0.8);
        }
        else
        {
            this.pY[i] = centerY + ((this.random.NextDouble() - 0.5) * 4.0);
            this.pLife[i] = 0.5 + (this.random.NextDouble() * 0.5);
        }
    }

    public void SetSticky(bool isSticky)
    {
        this.PinButton.IsChecked = isSticky;
    }

    public void SetOverlayMode(OverlayMode overlayMode)
    {
        this.overlayMode = overlayMode;
        bool isCompact = overlayMode == OverlayMode.CompactMicrophone;

        this.HeaderPanel.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        this.TranscriptCard.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        this.FooterPanel.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        this.CompactStatusBadge.Visibility = isCompact ? Visibility.Visible : Visibility.Collapsed;
        this.LeftWaveform.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        this.RightWaveform.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        this.ParticleCanvas.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        this.GlowEllipse.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        this.Ring1.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        this.Ring2.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        var rippleVisibility = isCompact ? Visibility.Visible : Visibility.Collapsed;
        this.RippleRing0.Visibility = rippleVisibility;
        this.RippleRing1.Visibility = rippleVisibility;
        this.RippleRing2.Visibility = rippleVisibility;
        this.PulseSurface.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        this.PulseCore.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        this.MicInteriorGlow.Visibility = isCompact ? Visibility.Visible : Visibility.Collapsed;
        this.MicFace.Visibility = Visibility.Visible;
        this.MicOutline.Visibility = Visibility.Visible;

        this.HeaderRow.Height = isCompact ? new GridLength(0) : GridLength.Auto;
        this.SpacerRow.Height = isCompact ? new GridLength(0) : new GridLength(12);
        this.FooterRow.Height = isCompact ? new GridLength(0) : GridLength.Auto;
        this.TranscriptRow.Height = isCompact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        this.VisualizationRow.Height = isCompact ? new GridLength(1, GridUnitType.Star) : new GridLength(120);

        this.ContentPanel.Margin = isCompact ? new Thickness(10) : new Thickness(16, 8, 16, 16);
        this.VisualizationPanel.Margin = isCompact ? new Thickness(0) : new Thickness(-16, 0, -16, 0);
        this.OverlayBorder.CornerRadius = isCompact ? new CornerRadius(0) : new CornerRadius(12);
        this.OverlayBorder.Background = isCompact ? System.Windows.Media.Brushes.Transparent : CreateFrozenBrush(13, 17, 23);
        this.OverlayBorder.BorderThickness = isCompact ? new Thickness(0) : new Thickness(1);
        this.OverlayBorder.BorderBrush = isCompact ? System.Windows.Media.Brushes.Transparent : CreateFrozenBrush(51, 51, 51);
        this.ResizeMode = isCompact ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        this.Cursor = isCompact ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;

        if (isCompact)
        {
            this.Width = CompactOverlaySize;
            this.Height = CompactOverlaySize;
            this.MinWidth = CompactOverlaySize;
            this.MaxWidth = CompactOverlaySize;
            this.MinHeight = CompactOverlaySize;
            this.MaxHeight = CompactOverlaySize;
        }
        else
        {
            this.Width = FullOverlayWidth;
            this.Height = FullOverlayHeight;
            this.MinWidth = 360;
            this.MaxWidth = double.PositiveInfinity;
            this.MinHeight = 280;
            this.MaxHeight = double.PositiveInfinity;
        }

        this.ConfigureMicVisuals(isCompact);
        this.ApplyStateBrush();
        this.UpdateVisualizerLoopState();

        if (isCompact)
        {
            this.PositionCompactInLowerRight();
        }
        else
        {
            this.Dispatcher.BeginInvoke(() => this.EnsureParticleLayout(forceRefresh: !this.particlesSeeded), DispatcherPriority.Loaded);
        }
    }

    public void PositionFullPanelInLowerCenter()
    {
        if (this.overlayMode != OverlayMode.FullPanel)
        {
            return;
        }

        this.Dispatcher.BeginInvoke(() =>
        {
            const double bottomMargin = 56;
            var workArea = SystemParameters.WorkArea;
            double overlayWidth = this.ActualWidth > 0 ? this.ActualWidth : this.Width;
            double overlayHeight = this.ActualHeight > 0 ? this.ActualHeight : this.Height;
            this.Left = workArea.Left + ((workArea.Width - overlayWidth) / 2.0);
            this.Top = workArea.Bottom - overlayHeight - bottomMargin;
        }, DispatcherPriority.Loaded);
    }

    public void SetReadyState(string backendLabel, bool isWakeListening)
    {
        var statusText = isWakeListening
            ? $"Wake listening [{backendLabel}]"
            : $"Ready [{backendLabel}]";
        this.HeaderText.Text = statusText;
        this.currentStateBrush = isWakeListening ? WakeListeningBrush : ReadyBrush;
        this.ApplyStateBrush();
        this.TranscriptText.Text = isWakeListening
            ? "Waiting for wake phrase..."
            : "Waiting for hotkey...";
        this.ToolTip = statusText;
        this.TimerText.Text = "00:00";
        this.timer.Stop();
        this.compactPulseEnvelope = 0;
        this.compactSurfaceEnvelope = 0;
        this.compactRipplePhase = 0;
        this.MicInteriorGlow.Opacity = 0;
        this.UpdateAudioLevel(0);
        this.UpdateVisualizerLoopState();
        this.OnVisualizerTick(null, EventArgs.Empty);
        this.UpdateCopyButtonState(hasCopyableTranscript: false);
    }

    public void UpdateAudioLevel(double rms)
    {
        if (!this.Dispatcher.CheckAccess())
        {
            _ = this.Dispatcher.BeginInvoke(() => this.UpdateAudioLevel(rms));
            return;
        }

        this.targetRms = rms;
    }

    private void OnVisualizerTick(object? sender, EventArgs e)
    {
        // Decay target so it naturally zeroes out
        this.targetRms *= 0.8;
        
        // Smooth out the animation
        this.currentSmoothedRms = (this.currentSmoothedRms * 0.7) + (this.targetRms * 0.3);
        
        // Base scales when silent
        double minScale = 1.0;
        double scaleMultiplier1 = this.overlayMode == OverlayMode.CompactMicrophone ? 1.0 : 4.0;
        double scaleMultiplier2 = this.overlayMode == OverlayMode.CompactMicrophone ? 1.8 : 8.0;
        double targetScale1 = minScale + (this.currentSmoothedRms * scaleMultiplier1);
        double targetScale2 = minScale + (this.currentSmoothedRms * scaleMultiplier2);
        
        double targetOpacity1 = this.currentSmoothedRms > 0.01 ? 0.6 : 0.0;
        double targetOpacity2 = this.currentSmoothedRms > 0.05 ? 0.4 : 0.0;

        this.Ring1Scale.ScaleX = targetScale1;
        this.Ring1Scale.ScaleY = targetScale1;
        this.Ring1.Opacity = targetOpacity1;

        this.Ring2Scale.ScaleX = targetScale2;
        this.Ring2Scale.ScaleY = targetScale2;
        this.Ring2.Opacity = targetOpacity2;

        // Compact mode gets a true center-out pulse inside the mic circle so movement is visible.
        bool isCompact = this.overlayMode == OverlayMode.CompactMicrophone;
        // Compact mic is small on screen: drive visibility from RMS without needing high capture gain.
        double voiceActivity = Math.Min(1.0, this.currentSmoothedRms * (isCompact ? 34.0 : 12.0));
        double rmsBoost = Math.Min(0.85, this.currentSmoothedRms * (isCompact ? 5.0 : 3.0));

        if (isCompact)
        {
            // Fast attack + slow release: pulse expands quickly and lingers instead of breathing in/out.
            this.compactPulseEnvelope = voiceActivity > this.compactPulseEnvelope
                ? this.compactPulseEnvelope + ((voiceActivity - this.compactPulseEnvelope) * 0.72)
                : this.compactPulseEnvelope * 0.965;
            this.compactSurfaceEnvelope = voiceActivity > this.compactSurfaceEnvelope
                ? this.compactSurfaceEnvelope + ((voiceActivity - this.compactSurfaceEnvelope) * 0.82)
                : this.compactSurfaceEnvelope * 0.972;
        }
        else
        {
            this.compactPulseEnvelope = voiceActivity;
            this.compactSurfaceEnvelope = voiceActivity;
        }

        double coreScale = (isCompact ? 0.1 : 0.2) + (this.compactPulseEnvelope * (isCompact ? 0.42 : 0.2)) + (rmsBoost * 0.25);
        this.PulseCoreScale.ScaleX = coreScale;
        this.PulseCoreScale.ScaleY = coreScale;
        this.PulseCore.Opacity = Math.Min(
            isCompact ? 0.26 : 0.18,
            (isCompact ? 0.01 : 0.02) + (this.compactPulseEnvelope * (isCompact ? 0.18 : 0.1)));

        if (isCompact)
        {
            // White “lit from inside” under the mic emoji — centered, calm but clearly visible when speaking.
            var glowBreath = 0.78 + (this.compactPulseEnvelope * 0.22) + (rmsBoost * 0.12);
            this.MicInteriorGlowScale.ScaleX = glowBreath;
            this.MicInteriorGlowScale.ScaleY = glowBreath;
            this.MicInteriorGlow.Opacity = Math.Clamp(
                0.12 + (this.compactSurfaceEnvelope * 0.62) + (rmsBoost * 0.45),
                0.0,
                0.88);
        }

        // Full panel: radial fill glow. Compact: ripple rings instead (lighter than cranking audio gain).
        if (!isCompact)
        {
            double surfaceScale = 0.06 + (this.compactSurfaceEnvelope * 0.78);
            this.PulseSurfaceScale.ScaleX = surfaceScale;
            this.PulseSurfaceScale.ScaleY = surfaceScale;
            this.PulseSurface.Opacity = Math.Min(
                0.22,
                0.04 + (this.compactSurfaceEnvelope * 0.16));
        }

        if (isCompact)
        {
            const double rippleSecondsPerCycle = 2.85;
            this.compactRipplePhase += visualizerTimer.Interval.TotalSeconds / rippleSecondsPerCycle;
            if (this.compactRipplePhase >= 1.0)
            {
                this.compactRipplePhase -= Math.Floor(this.compactRipplePhase);
            }

            var intensity = Math.Clamp(
                0.15 + (this.compactSurfaceEnvelope * 0.95) + (rmsBoost * 0.35),
                0.0,
                1.0);
            this.UpdateCompactRipples(intensity);
        }

        this.UpdateWaveformBounds();
    }

    private void UpdateWaveformBounds()
    {
        this.EnsureParticleLayout();
        double dbIntensity = this.currentSmoothedRms * 400.0;
        
        // Shift history directly to create an undeniable outward physical scroll
        // No sine waves or ripples that could cause optical illusions
        for (int i = WaveformBarCount - 1; i > 0; i--)
        {
            this.barTargets[i] = this.barTargets[i - 1];
        }
        
        // The newest sample is placed exactly at the microphone (center)
        this.barTargets[0] = 3.0 + (dbIntensity * (1.0 + (this.random.NextDouble() * 0.4)));

        for (int i = 0; i < WaveformBarCount; i++)
        {
            double normalized = (double)i / WaveformBarCount;
            // Smooth fade to the edges
            double fadeDist = Math.Pow(1.0 - normalized, 1.2);
            double maxDistHeight = 120.0 * fadeDist;

            // Extremely fast responsive rise, so the scrolling is very literal
            double moveSpeed = this.barTargets[i] > this.barCurrents[i] ? 0.8 : 0.4;
            this.barCurrents[i] += (this.barTargets[i] - this.barCurrents[i]) * moveSpeed;
            
            double finalHeight = this.barCurrents[i] * fadeDist;
            if (finalHeight > maxDistHeight) finalHeight = maxDistHeight;
            if (finalHeight < 3.0) finalHeight = 3.0;

            this.leftBars[i].Height = finalHeight;
            this.rightBars[i].Height = finalHeight;
        }

        double particleIntensityBoost = dbIntensity * 0.05;
        for (int i = 0; i < ParticleCount; i++)
        {
            // Pure vertical drift
            this.pY[i] += this.pVY[i] + (this.pVY[i] * particleIntensityBoost);
            
            this.pLife[i] -= 0.01 + (particleIntensityBoost * 0.005);
            if (this.pLife[i] <= 0 || this.pY[i] < -20 || this.pY[i] > this.ParticleCanvas.ActualHeight + 20)
            {
                this.ResetParticle(i);
            }

            System.Windows.Controls.Canvas.SetLeft(this.pShapes[i], this.pX[i]);
            System.Windows.Controls.Canvas.SetTop(this.pShapes[i], this.pY[i]);
            
            double brightness = this.pLife[i] * (0.3 + (dbIntensity * 0.02));
            if (brightness > 1.0) brightness = 1.0;
            this.pShapes[i].Opacity = brightness;
        }
    }

    public void UpdateTranscript(string transcript, bool isProcessing, string backendLabel)
    {
        var statusText = isProcessing
            ? $"Processing [{backendLabel}]"
            : $"Listening [{backendLabel}]";
        this.HeaderText.Text = statusText;
        this.currentStateBrush = isProcessing ? ReadyBrush : RecordingBrush;
        this.ApplyStateBrush();
        var displayTranscript = transcript.Trim();
        if (displayTranscript.Length > MaxDisplayedTranscriptChars)
        {
            displayTranscript = "..." + displayTranscript[^MaxDisplayedTranscriptChars..];
        }

        var hasCopyableTranscript = !string.IsNullOrWhiteSpace(displayTranscript);
        this.TranscriptText.Text = hasCopyableTranscript
            ? displayTranscript
            : $"Listening with {backendLabel}...";
        this.ToolTip = statusText;
        this.UpdateCopyButtonState(hasCopyableTranscript);

        if (!isProcessing && !this.timer.IsEnabled)
        {
            this.startTime = DateTime.Now;
            this.timer.Start();
        }
        else if (isProcessing && this.timer.IsEnabled)
        {
            this.timer.Stop();
            UpdateAudioLevel(0); // Zero out visualizer
        }

        this.UpdateVisualizerLoopState();
        this.OnVisualizerTick(null, EventArgs.Empty);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (this.overlayMode == OverlayMode.CompactMicrophone)
        {
            this.PositionCompactInLowerRight();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        this.timer.Stop();
        this.timer.Tick -= this.OnTimerTick;
        this.visualizerTimer.Stop();
        this.visualizerTimer.Tick -= this.OnVisualizerTick;
        this.SourceInitialized -= this.OnSourceInitialized;
        this.Loaded -= this.OnWindowLoaded;
        this.ParticleCanvas.SizeChanged -= this.OnParticleCanvasSizeChanged;
        base.OnClosed(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle);
        // Excluded: NativeMethods.WsExTransparent -> to allow hit testing (mouse clicks/drag)
        var newStyle = new IntPtr(style.ToInt64() |
            NativeMethods.WsExNoActivate |
            NativeMethods.WsExToolWindow);
        _ = NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, newStyle);

        if (this.overlayMode == OverlayMode.CompactMicrophone)
        {
            this.PositionCompactInLowerRight();
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.Now - this.startTime;
        this.TimerText.Text = $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        this.EnsureParticleLayout(forceRefresh: true);
        this.UpdateVisualizerLoopState();
    }

    private void OnParticleCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 1 || e.NewSize.Height <= 1)
        {
            return;
        }

        this.EnsureParticleLayout(forceRefresh: !this.particlesSeeded);
    }

    private void OnBorderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (this.overlayMode == OverlayMode.CompactMicrophone)
        {
            if (System.Windows.Application.Current is App app)
            {
                app.ExpandOverlayPanel();
            }

            return;
        }

        this.DragMove();
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        bool isPinned = this.PinButton.IsChecked == true;
        if (System.Windows.Application.Current is App app)
        {
            app.SaveStickyState(isPinned);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
        {
            app.ShowSettings();
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (this.ShouldCollapseToCompact())
        {
            return;
        }

        this.WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (this.ShouldCollapseToCompact())
        {
            return;
        }

        this.Hide();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (this.CopyTranscriptButton?.IsEnabled != true)
        {
            return;
        }

        var text = this.TranscriptText.Text;
        if (!IsCopyableTranscript(text))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch
        {
            // Ignore clipboard errors
        }
    }

    private void UpdateCopyButtonState(bool hasCopyableTranscript)
    {
        if (this.CopyTranscriptButton is null)
        {
            return;
        }

        this.CopyTranscriptButton.IsEnabled = hasCopyableTranscript;
        this.CopyTranscriptButton.ToolTip = hasCopyableTranscript
            ? "Copy the current transcript preview"
            : "Nothing to copy yet — wait for a transcript hypothesis";
    }

    private static bool IsCopyableTranscript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Equals("Waiting for hotkey...", StringComparison.Ordinal) ||
            text.Equals("Listening...", StringComparison.Ordinal) ||
            text.StartsWith("Listening with ", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private void ConfigureMicVisuals(bool isCompact)
    {
        double micSize = isCompact ? CompactMicSize : FullMicSize;
        double glowSize = isCompact ? CompactGlowSize : FullGlowSize;
        double glyphFontSize = isCompact ? 28 : 34;

        this.GlowEllipse.Width = glowSize;
        this.GlowEllipse.Height = glowSize;
        this.Ring1.Width = micSize;
        this.Ring1.Height = micSize;
        this.Ring2.Width = micSize;
        this.Ring2.Height = micSize;
        this.Ring1Scale.CenterX = micSize / 2.0;
        this.Ring1Scale.CenterY = micSize / 2.0;
        this.Ring2Scale.CenterX = micSize / 2.0;
        this.Ring2Scale.CenterY = micSize / 2.0;
        this.PulseCore.Width = micSize;
        this.PulseCore.Height = micSize;
        this.PulseCoreScale.CenterX = micSize / 2.0;
        this.PulseCoreScale.CenterY = micSize / 2.0;
        this.PulseSurface.Width = micSize;
        this.PulseSurface.Height = micSize;
        this.PulseSurfaceScale.CenterX = micSize / 2.0;
        this.PulseSurfaceScale.CenterY = micSize / 2.0;
        this.MicInteriorGlow.Width = micSize;
        this.MicInteriorGlow.Height = micSize;
        var glowHalf = micSize / 2.0;
        this.MicInteriorGlowScale.CenterX = glowHalf;
        this.MicInteriorGlowScale.CenterY = glowHalf;
        foreach (var ripple in new[] { this.RippleRing0, this.RippleRing1, this.RippleRing2 })
        {
            ripple.Width = micSize;
            ripple.Height = micSize;
        }

        var half = micSize / 2.0;
        this.RippleRing0Scale.CenterX = half;
        this.RippleRing0Scale.CenterY = half;
        this.RippleRing1Scale.CenterX = half;
        this.RippleRing1Scale.CenterY = half;
        this.RippleRing2Scale.CenterX = half;
        this.RippleRing2Scale.CenterY = half;
        // Ripples sit between MicFace and MicOutline in Z-order; do not Clip here — geometry Clip + ScaleTransform
        // can eliminate the stroke entirely in some layout/transform combinations.
        this.MicFace.Width = micSize;
        this.MicFace.Height = micSize;
        this.MicOutline.Width = micSize;
        this.MicOutline.Height = micSize;
        this.MicGlyph.Width = glyphFontSize;
        this.MicGlyph.Height = glyphFontSize;
        this.MicGlyph.Margin = isCompact ? new Thickness(0, 0, 0, 2) : new Thickness(0, 0, 0, 4);
    }

    private void ApplyStateBrush()
    {
        this.StateDot.Fill = this.currentStateBrush;
        this.CompactStateDot.Fill = this.currentStateBrush;

        var micBrush = this.overlayMode == OverlayMode.CompactMicrophone
            ? this.currentStateBrush
            : AccentBrush;
        this.MicOutline.Stroke = micBrush;
        this.MicGlyphBody.Fill = micBrush;
        this.MicGlyphSupport.Stroke = micBrush;
        this.MicGlyphStem.Stroke = micBrush;
        this.Ring1.Stroke = micBrush;
        this.Ring2.Stroke = micBrush;

        var c = micBrush.Color;
        var ripple0 = new SolidColorBrush(MediaColor.FromArgb(140, c.R, c.G, c.B));
        ripple0.Freeze();
        var ripple1 = new SolidColorBrush(MediaColor.FromArgb(115, c.R, c.G, c.B));
        ripple1.Freeze();
        var ripple2 = new SolidColorBrush(MediaColor.FromArgb(90, c.R, c.G, c.B));
        ripple2.Freeze();
        this.RippleRing0.Stroke = ripple0;
        this.RippleRing1.Stroke = ripple1;
        this.RippleRing2.Stroke = ripple2;
    }

    private void UpdateCompactRipples(double intensity)
    {
        static double RippleOpacity(double phase, double voice)
        {
            var t = phase - Math.Floor(phase);
            var fade = Math.Pow(1.0 - t, 1.85);
            // Softer rings — center glow carries most of the “voice” read.
            return Math.Clamp(fade * (0.08 + voice * 0.5), 0.0, 0.48);
        }

        static double RippleScale(double phase)
        {
            var t = phase - Math.Floor(phase);
            // Start small at center, expand outward (not a ring parked near the edge).
            return 0.32 + (t * 0.68);
        }

        var p0 = this.compactRipplePhase;
        var p1 = this.compactRipplePhase + (1.0 / 3.0);
        var p2 = this.compactRipplePhase + (2.0 / 3.0);

        var s0 = RippleScale(p0);
        var s1 = RippleScale(p1);
        var s2 = RippleScale(p2);

        this.RippleRing0Scale.ScaleX = s0;
        this.RippleRing0Scale.ScaleY = s0;
        this.RippleRing1Scale.ScaleX = s1;
        this.RippleRing1Scale.ScaleY = s1;
        this.RippleRing2Scale.ScaleX = s2;
        this.RippleRing2Scale.ScaleY = s2;

        this.RippleRing0.Opacity = RippleOpacity(p0, intensity);
        this.RippleRing1.Opacity = RippleOpacity(p1, intensity);
        this.RippleRing2.Opacity = RippleOpacity(p2, intensity);
    }

    private bool ShouldCollapseToCompact()
    {
        if (this.overlayMode != OverlayMode.FullPanel)
        {
            return false;
        }

        if (System.Windows.Application.Current is not App app)
        {
            return false;
        }

        app.CollapseOverlayPanel();
        return true;
    }

    private void UpdateVisualizerLoopState()
    {
        // Compact mode must animate while dictating even if the transcript timer is not running (e.g. empty transcript).
        bool compactListening =
            this.overlayMode == OverlayMode.CompactMicrophone &&
            ReferenceEquals(this.currentStateBrush, RecordingBrush);
        bool shouldRun =
            this.overlayMode == OverlayMode.FullPanel ||
            this.timer.IsEnabled ||
            compactListening;
        if (shouldRun)
        {
            if (!this.visualizerTimer.IsEnabled)
            {
                this.visualizerTimer.Start();
            }
        }
        else if (this.visualizerTimer.IsEnabled)
        {
            this.visualizerTimer.Stop();
        }
    }

    private void EnsureParticleLayout(bool forceRefresh = false)
    {
        if (!forceRefresh && this.particlesSeeded)
        {
            return;
        }

        if (this.GetParticleCanvasWidth() <= 1 || this.GetParticleCanvasHeight() <= 1)
        {
            return;
        }

        for (int i = 0; i < ParticleCount; i++)
        {
            this.ResetParticle(i, seedInFlight: true);
            System.Windows.Controls.Canvas.SetLeft(this.pShapes[i], this.pX[i]);
            System.Windows.Controls.Canvas.SetTop(this.pShapes[i], this.pY[i]);
            this.pShapes[i].Opacity = 0.08 + (this.random.NextDouble() * 0.18);
        }

        this.particlesSeeded = true;
    }

    private void PositionCompactInLowerRight()
    {
        if (this.overlayMode != OverlayMode.CompactMicrophone)
        {
            return;
        }

        const double margin = 24;
        var workArea = SystemParameters.WorkArea;
        this.Left = workArea.Right - this.Width - margin;
        this.Top = workArea.Bottom - this.Height - margin;
    }

    private double GetParticleCanvasWidth() =>
        this.ParticleCanvas.ActualWidth > 0
            ? this.ParticleCanvas.ActualWidth
            : Math.Max(this.VisualizationPanel.ActualWidth, FullOverlayWidth - 32);

    private double GetParticleCanvasHeight() =>
        this.ParticleCanvas.ActualHeight > 0
            ? this.ParticleCanvas.ActualHeight
            : ParticleCanvasFallbackHeight;

    private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(MediaColor.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
