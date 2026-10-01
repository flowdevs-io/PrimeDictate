using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using PrimeDictate.Core.Diagnostics;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Providers;
using PrimeDictate.Platforms;
using PrimeDictate.Platforms.Dictation;
using PrimeDictate.Platforms.Speech;
using PrimeDictate.Platforms.Input;

namespace PrimeDictate.Desktop.Dictation;

/// <summary>
/// Ties the dictation host to the tray icon, the overlay and the settings window. Everything here runs on the UI
/// thread; host events arrive on worker threads and are posted over.
/// </summary>
public sealed class DictationShell : IAsyncDisposable
{
    private readonly DictationHost host;
    private readonly MainWindow window;
    private readonly TranscriptionWorkspaceService workspace;
    private readonly IClassicDesktopStyleApplicationLifetime lifetime;
    private readonly Action showWorkspace;
    private readonly DictationOverlayWindow overlay = new();
    private readonly TrayIcon tray = new();
    private readonly NativeMenuItem toggleItem = new("Start dictation");
    private readonly NativeMenuItem recordItem = new("Record meeting");
    private string modelName = string.Empty;
    private readonly IAudioCuePlayer cues = new ProcessAudioCuePlayer();
    private DictationState lastState = DictationState.Idle;
    private DateTime errorUntilUtc;
    private bool wakeFailed;
    private readonly TrayClickDecider clickDecider;
    private DictationSettingsWindow? settingsWindow;
    private DictationHistoryWindow? historyWindow;
    private DictationStatsWindow? statsWindow;

    public DictationShell(IClassicDesktopStyleApplicationLifetime lifetime, MainWindow window, Action showWorkspace, DictationHost? host = null)
    {
        this.lifetime = lifetime;
        this.window = window;
        this.workspace = window.Workspace;
        this.showWorkspace = showWorkspace;
        this.host = host ?? new DictationHost(workspace.Paths, workspace.Microphone, workspace.MicrophoneSource, new SharpHookHotkeySource());
        this.clickDecider = new TrayClickDecider(() => this.host.Settings.TrayClickBehavior);
    }

    public DictationHost Host => this.host;

    public void Start(Application app)
    {
        this.ConfigureOverlay();
        this.overlay.AnchorMoved += (x, y) => this.host.RememberOverlayAnchor(x, y);
        this.overlay.PinChanged += pinned => this.host.RememberOverlayPinned(pinned);
        this.overlay.SettingsRequested += this.ShowSettings;
        ApplyTheme(this.host.Settings.Theme);
        // Any error-level log line turns the tray icon to "needs attention" for a while, as in the WPF app.
        AppLog.ErrorLogged += this.OnErrorLogged;
        if (this.host.Wake is { } wake)
        {
            wake.Notice += _ => Dispatcher.UIThread.Post(this.OnWakeNotice);
        }
        this.BuildTray(app);
        this.window.DictationHistoryRequested += this.ShowHistory;
        this.window.DictationStatsRequested += this.ShowStats;
        this.window.SettingsRequested += this.ShowSettings;
        this.window.DictationActivityRequested += this.ShowActivity;
        this.window.RecordingChanged += this.OnMeetingRecordingChanged;
        this.RefreshHeader(refreshModel: true);
        this.host.Notice += message => Dispatcher.UIThread.Post(() => this.OnNotice(message));
        this.host.HistoryRequested += () => Dispatcher.UIThread.Post(this.ShowHistory);
        if (this.host.Controller is { } controller)
        {
            controller.StateChanged += state => Dispatcher.UIThread.Post(() => this.OnState(state));
            controller.PartialTranscript += (_, text) => Dispatcher.UIThread.Post(() => this.overlay.SetTranscript(text));
            controller.LevelChanged += level => Dispatcher.UIThread.Post(() => this.overlay.SetLevel(level));
            controller.Committed += commit => Dispatcher.UIThread.Post(() => this.OnCommitted(commit));
        }

        if (this.host.StartupNotice is { } startup)
        {
            this.OnNotice(startup);
        }

        if (this.host.UnavailableReason is { } unavailable)
        {
            this.OnNotice(unavailable);
        }

        if (OnnxRuntimeDevice.FellBack || OnnxRuntimeDevice.IsCuda)
        {
            this.OnNotice(OnnxRuntimeDevice.Summary);
        }

        if (this.host.IsFirstRun)
        {
            new DictationOnboardingWindow(this.host, this.ShowSettings).Show();
        }

        this.host.StartHotkeys();
        this.host.StartWakeWord();
        this.overlay.SetState(DictationState.Idle);
    }

    /// <summary>The update command for the tray's "Check for updates..." item. Set before Start; null leaves the item out.</summary>
    public PrimeDictate.Desktop.Updates.IUpdateMenu? UpdateMenu { get; set; }

    /// <summary>Raised when the user picks Exit in the tray menu. The app decides how to leave (see <c>App.ExitAsync</c>).</summary>
    public event Action? ExitRequested;

    /// <summary>Stops listening and drops an in-progress dictation without typing it, so nothing reaches another app while exiting.</summary>
    public async Task PrepareExitAsync()
    {
        this.host.StopListeningForExit();
        if (this.host.Controller is { State: not DictationState.Idle } controller)
        {
            await Task.Run(controller.DiscardAsync);
        }
    }

    public ValueTask DisposeAsync()
    {
        AppLog.ErrorLogged -= this.OnErrorLogged;
        this.tray.IsVisible = false;
        this.overlay.Close();
        return this.host.DisposeAsync();
    }

    private void BuildTray(Application app)
    {
        this.toggleItem.Click += async (_, _) =>
        {
            if (this.host.Controller is { } controller)
            {
                await Task.Run(controller.ToggleAsync);
            }
        };
        // Start or stop a live recording in the workspace through the same code the Record and Stop buttons use, so either
        // one can end what the other started.
        this.recordItem.Click += async (_, _) =>
        {
            this.showWorkspace();
            if (this.window.IsRecording)
            {
                await this.window.StopMeetingRecordingAsync();
            }
            else
            {
                await this.window.StartMeetingRecordingAsync();
            }
        };
        var workspaceItem = new NativeMenuItem("Open PrimeDictate");
        workspaceItem.Click += (_, _) => this.showWorkspace();
        var historyItem = new NativeMenuItem("Dictation history…");
        historyItem.Click += (_, _) => this.ShowHistory();
        var statsItem = new NativeMenuItem("Stats…");
        statsItem.Click += (_, _) => this.ShowStats();
        var activityItem = new NativeMenuItem("Dictation activity…");
        activityItem.Click += (_, _) => this.ShowActivity();
        var settingsItem = new NativeMenuItem("Settings…");
        settingsItem.Click += (_, _) => this.ShowSettings();
        var quitItem = new NativeMenuItem("Exit PrimeDictate");
        quitItem.Click += (_, _) => this.ExitRequested?.Invoke();
        this.tray.Menu = [this.toggleItem, this.recordItem, workspaceItem, historyItem, statsItem, activityItem, settingsItem];
        if (this.UpdateMenu is { } updates)
        {
            var updateItem = new NativeMenuItem(updates.MenuText);
            updateItem.Click += async (_, _) => await updates.CheckNowAsync();
            updates.MenuChanged += () =>
            {
                updateItem.Header = updates.MenuText;
                updateItem.IsEnabled = updates.MenuEnabled;
            };
            this.tray.Menu.Items.Add(updateItem);
        }

        this.tray.Menu.Items.Add(new NativeMenuItemSeparator());
        this.tray.Menu.Items.Add(quitItem);
        // The tray reports single clicks only; a double click is two close together (see TrayClickDecider). "Open PrimeDictate" in the menu always works.
        this.tray.Clicked += (_, _) =>
        {
            if (this.clickDecider.OnClick(DateTime.UtcNow))
            {
                this.showWorkspace();
            }
        };
        this.tray.Icon = TrayIconRenderer.Create(this.CurrentTrayState());
        this.tray.ToolTipText = "PrimeDictate: ready";
        TrayIcon.SetIcons(app, [this.tray]);
    }

    /// <summary>The WPF order: recording, then processing, then "needs attention", then the wake word, then ready.</summary>
    private TrayVisualState CurrentTrayState() =>
        this.lastState == DictationState.Listening || this.window.IsRecording ? TrayVisualState.Recording
        : this.lastState == DictationState.Processing ? TrayVisualState.Processing
        : TrayAttention.IsActive(this.errorUntilUtc, DateTime.UtcNow, this.wakeFailed) ? TrayVisualState.Error
        : this.WakeListening ? TrayVisualState.AlwaysListening
        : TrayVisualState.Ready;

    /// <summary>Wake word on and nothing else going on, as in the WPF app (it shows even in the gap while the listener restarts).</summary>
    private bool WakeListening => this.host.Settings.EnableWakeWord && this.lastState == DictationState.Idle && !this.wakeFailed;

    private void OnErrorLogged() => Dispatcher.UIThread.Post(this.HoldAttention);

    private void HoldAttention()
    {
        this.errorUntilUtc = DateTime.UtcNow + TrayAttention.Hold;
        this.RefreshTrayIcon();
        this.RefreshTooltip();
        DispatcherTimer.RunOnce(() =>
        {
            this.RefreshTrayIcon();
            this.RefreshTooltip();
        }, TrayAttention.Hold + TimeSpan.FromSeconds(0.5));
    }

    /// <summary>The wake listener said something; when it has given up (the microphone would not open) the tray says "Wake listening failed".</summary>
    private void OnWakeNotice()
    {
        this.wakeFailed = this.host.Wake?.HasFailed == true;
        this.RefreshStatusSurfaces();
    }

    private void RefreshStatusSurfaces()
    {
        this.overlay.SetWakeListening(this.WakeListening);
        this.RefreshTrayIcon();
        this.RefreshTooltip();
    }

    private static void ApplyTheme(AppTheme theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }
    }

    private void RefreshTrayIcon() => this.tray.Icon = TrayIconRenderer.Create(this.CurrentTrayState());

    private void OnState(DictationState state)
    {
        if (this.host.Settings.PlayAudioCues)
        {
            if (state == DictationState.Listening)
            {
                this.cues.Play(DictationAudioCue.Start);
            }
            else if (this.lastState == DictationState.Listening)
            {
                this.cues.Play(DictationAudioCue.Stop);
            }
        }

        // As in WPF: Windows Mouse Sonar pulse when recording starts and when processing starts.
        if (state != this.lastState && state is DictationState.Listening or DictationState.Processing)
        {
            WindowsMousePointerIndicator.PulseSoon(message => AppLog.Event("dictation", message));
        }

        this.lastState = state;
        if (state == DictationState.Idle && this.wakeFailed && this.host.Wake?.HasFailed != true)
        {
            this.wakeFailed = false;
        }

        this.overlay.SetState(state);
        this.RefreshStatusSurfaces();
        this.toggleItem.Header = state == DictationState.Listening ? "Stop and type" : "Start dictation";
        this.RefreshHeader(refreshModel: false);
    }

    private void OnMeetingRecordingChanged()
    {
        this.recordItem.Header = this.window.IsRecording ? "Stop meeting recording" : "Record meeting";
        this.RefreshStatusSurfaces();
    }

    /// <summary>Dictation states win over a meeting recording in the tooltip, since dictation is the short, active thing (<see cref="OverlayRules.TrayTooltip"/>).</summary>
    private void RefreshTooltip() =>
        this.tray.ToolTipText = OverlayRules.TrayTooltip(
            this.lastState switch
            {
                DictationState.Listening => OverlayPhase.Listening,
                DictationState.Processing => OverlayPhase.Processing,
                _ => this.WakeListening ? OverlayPhase.WakeListening : OverlayPhase.Ready
            },
            this.BackendLabel,
            TrayAttention.IsActive(this.errorUntilUtc, DateTime.UtcNow, this.wakeFailed),
            this.wakeFailed,
            this.window.IsRecording ? this.window.RecordingSourceLabel : null,
            this.host.Settings.WakeWordPhrase) + this.MicAccessSuffix;

    /// <summary>While dictating on Windows, which microphone access was granted (as the WPF tooltip showed).</summary>
    private string MicAccessSuffix => this.lastState != DictationState.Listening ? ""
        : this.host.Controller?.ActiveMicAccess switch
        {
            MicAccessMode.Exclusive => " [Exclusive]",
            MicAccessMode.Shared when OperatingSystem.IsWindows() => " [Shared]",
            _ => ""
        };

    private string BackendLabel => OverlayRules.BackendLabel(this.host.Settings.TranscriptionBackend);

    /// <summary>The main window's header line: dictation state, the model dictation uses and the hotkey.</summary>
    private void RefreshHeader(bool refreshModel)
    {
        if (this.host.Controller is null)
        {
            this.window.SetDictationStatus("Dictation unavailable: " + (this.host.UnavailableReason ?? "no microphone"));
            return;
        }

        if (refreshModel)
        {
            var wanted = this.host.Settings.ResolveModelId();
            var installed = this.host.InstalledModels();
            this.modelName = installed.FirstOrDefault(m => m.ModelId == wanted)?.DisplayName
                ?? (installed.Count > 0 ? installed[0].DisplayName : "no model installed");
        }

        var state = this.lastState switch
        {
            DictationState.Listening => "Listening",
            DictationState.Processing => "Transcribing",
            _ => "Ready"
        };
        var hotkey = this.host.Settings.ToBindings()[HotkeyAction.ToggleDictation];
        this.window.SetDictationStatus($"Dictation: {state} · {this.modelName} · {hotkey}");
    }

    private void OnCommitted(DictationCommit commit)
    {
        // So the overlay's Copy button can still copy the last transcript once dictation is over (kept in memory, never logged).
        this.overlay.SetCommitted(commit.Transcript);
        switch (commit.Status)
        {
            case DictationDeliveryStatus.SkippedFocusChanged:
            case DictationDeliveryStatus.SkippedNoFocusGuard:
            case DictationDeliveryStatus.FailedToInject:
                this.overlay.SetNotice($"Not typed: {commit.Error}\n\"{commit.Transcript}\"");
                this.HoldAttention();
                break;
        }
    }

    private void OnNotice(string message) => this.overlay.SetNotice(message);

    private void ShowHistory()
    {
        if (this.historyWindow is { IsVisible: true })
        {
            this.historyWindow.Activate();
            return;
        }

        this.historyWindow = new DictationHistoryWindow(this.host);
        this.historyWindow.Show();
    }

    private DictationActivityWindow? activityWindow;

    private void ShowActivity()
    {
        if (this.activityWindow is { IsVisible: true })
        {
            this.activityWindow.Activate();
            return;
        }

        this.activityWindow = new DictationActivityWindow();
        this.activityWindow.Show();
    }

    private void ShowStats()
    {
        if (this.statsWindow is { IsVisible: true })
        {
            this.statsWindow.Activate();
            return;
        }

        this.statsWindow = new DictationStatsWindow(this.host);
        this.statsWindow.Show();
    }

    private void ShowSettings()
    {
        if (this.settingsWindow is { IsVisible: true })
        {
            this.settingsWindow.Activate();
            return;
        }

        this.settingsWindow = new DictationSettingsWindow(this.host, this.workspace.MicrophoneSource, this.window.Prefs);
        this.settingsWindow.Closed += (_, _) =>
        {
            this.ConfigureOverlay();
            ApplyTheme(this.host.Settings.Theme);
            this.RefreshHeader(refreshModel: true);
            this.RefreshStatusSurfaces();
        };
        this.settingsWindow.Show();
    }

    private void ConfigureOverlay()
    {
        var settings = this.host.Settings;
        this.overlay.SetBackendLabel(this.BackendLabel);
        this.overlay.Configure(settings.OverlayMode, settings.IsOverlaySticky, settings.HideOverlayWhenIdle, settings.OverlayAnchorX, settings.OverlayAnchorY);
        this.overlay.SetWakeListening(this.WakeListening);
    }
}
