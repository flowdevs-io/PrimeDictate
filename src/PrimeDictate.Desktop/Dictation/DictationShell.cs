using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
    }

    public DictationHost Host => this.host;

    public void Start(Application app)
    {
        this.ConfigureOverlay();
        this.overlay.AnchorMoved += (x, y) => this.host.RememberOverlayAnchor(x, y);
        this.BuildTray(app);
        this.window.DictationHistoryRequested += this.ShowHistory;
        this.window.DictationStatsRequested += this.ShowStats;
        this.window.SettingsRequested += this.ShowSettings;
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
        var settingsItem = new NativeMenuItem("Settings…");
        settingsItem.Click += (_, _) => this.ShowSettings();
        var quitItem = new NativeMenuItem("Exit PrimeDictate");
        quitItem.Click += (_, _) => this.ExitRequested?.Invoke();
        this.tray.Menu = [this.toggleItem, this.recordItem, workspaceItem, historyItem, statsItem, settingsItem];
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
        this.tray.Clicked += (_, _) => this.showWorkspace();
        this.tray.Icon = TrayIconRenderer.Create(this.CurrentTrayState());
        this.tray.ToolTipText = "PrimeDictate: ready";
        TrayIcon.SetIcons(app, [this.tray]);
    }

    private TrayVisualState CurrentTrayState() =>
        this.errorUntilUtc > DateTime.UtcNow ? TrayVisualState.Error
        : this.lastState == DictationState.Listening || this.window.IsRecording ? TrayVisualState.Recording
        : this.lastState == DictationState.Processing ? TrayVisualState.Processing
        : this.host.Wake?.IsRunning == true ? TrayVisualState.AlwaysListening
        : TrayVisualState.Ready;

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
        this.overlay.SetState(state);
        this.RefreshTrayIcon();
        this.RefreshTooltip();
        this.toggleItem.Header = state == DictationState.Listening ? "Stop and type" : "Start dictation";
        this.RefreshHeader(refreshModel: false);
    }

    private void OnMeetingRecordingChanged()
    {
        this.recordItem.Header = this.window.IsRecording ? "Stop meeting recording" : "Record meeting";
        this.RefreshTrayIcon();
        this.RefreshTooltip();
    }

    /// <summary>Dictation states win over a meeting recording in the tooltip, since dictation is the short, active thing.</summary>
    private void RefreshTooltip() =>
        this.tray.ToolTipText = this.lastState switch
        {
            DictationState.Listening => this.host.Controller?.ActiveMicAccess switch
            {
                MicAccessMode.Exclusive => "PrimeDictate: listening [Exclusive]",
                MicAccessMode.Shared when OperatingSystem.IsWindows() => "PrimeDictate: listening [Shared]",
                _ => "PrimeDictate: listening"
            },
            DictationState.Processing => "PrimeDictate: transcribing",
            _ when this.window.IsRecording => $"PrimeDictate: recording meeting ({this.window.RecordingSourceLabel})",
            _ => "PrimeDictate: ready"
        };

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
        switch (commit.Status)
        {
            case DictationDeliveryStatus.SkippedFocusChanged:
            case DictationDeliveryStatus.SkippedNoFocusGuard:
            case DictationDeliveryStatus.FailedToInject:
                this.overlay.SetNotice($"Not typed: {commit.Error}\n\"{commit.Transcript}\"");
                this.errorUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(8);
                this.RefreshTrayIcon();
                DispatcherTimer.RunOnce(this.RefreshTrayIcon, TimeSpan.FromSeconds(8.5));
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
            this.RefreshHeader(refreshModel: true);
        };
        this.settingsWindow.Show();
    }

    private void ConfigureOverlay()
    {
        var settings = this.host.Settings;
        this.overlay.Configure(settings.OverlayMode, settings.IsOverlaySticky, settings.OverlayAnchorX, settings.OverlayAnchorY);
    }
}
