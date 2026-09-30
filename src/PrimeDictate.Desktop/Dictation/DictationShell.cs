using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PrimeDictate.Core.Dictation;
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
    private readonly TranscriptionWorkspaceService workspace;
    private readonly IClassicDesktopStyleApplicationLifetime lifetime;
    private readonly Action showWorkspace;
    private readonly DictationOverlayWindow overlay = new();
    private readonly TrayIcon tray = new();
    private readonly NativeMenuItem toggleItem = new("Start dictation");
    private readonly IAudioCuePlayer cues = new ProcessAudioCuePlayer();
    private DictationState lastState = DictationState.Idle;
    private DateTime errorUntilUtc;
    private DictationSettingsWindow? settingsWindow;
    private DictationHistoryWindow? historyWindow;
    private DictationStatsWindow? statsWindow;

    public DictationShell(IClassicDesktopStyleApplicationLifetime lifetime, TranscriptionWorkspaceService workspace, Action showWorkspace, DictationHost? host = null)
    {
        this.lifetime = lifetime;
        this.workspace = workspace;
        this.showWorkspace = showWorkspace;
        this.host = host ?? new DictationHost(workspace.Paths, workspace.Microphone, workspace.MicrophoneSource, new SharpHookHotkeySource());
    }

    public DictationHost Host => this.host;

    public void Start(Application app)
    {
        this.overlay.Configure(this.host.Settings.OverlayMode, this.host.Settings.IsOverlaySticky);
        this.BuildTray(app);
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
        var workspaceItem = new NativeMenuItem("Open workspace");
        workspaceItem.Click += (_, _) => this.showWorkspace();
        var historyItem = new NativeMenuItem("Dictation history...");
        historyItem.Click += (_, _) => this.ShowHistory();
        var statsItem = new NativeMenuItem("Dictation stats...");
        statsItem.Click += (_, _) => this.ShowStats();
        var settingsItem = new NativeMenuItem("Dictation settings...");
        settingsItem.Click += (_, _) => this.ShowSettings();
        var quitItem = new NativeMenuItem("Exit PrimeDictate");
        quitItem.Click += (_, _) => this.ExitRequested?.Invoke();
        this.tray.Menu = [this.toggleItem, workspaceItem, historyItem, statsItem, settingsItem, new NativeMenuItemSeparator(), quitItem];
        this.tray.Clicked += (_, _) => this.showWorkspace();
        this.tray.Icon = TrayIconRenderer.Create(this.CurrentTrayState());
        this.tray.ToolTipText = "PrimeDictate: ready";
        TrayIcon.SetIcons(app, [this.tray]);
    }

    private TrayVisualState CurrentTrayState() =>
        this.errorUntilUtc > DateTime.UtcNow ? TrayVisualState.Error
        : this.lastState == DictationState.Listening ? TrayVisualState.Recording
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

        this.lastState = state;
        this.overlay.SetState(state);
        this.RefreshTrayIcon();
        this.tray.ToolTipText = state switch
        {
            DictationState.Listening => "PrimeDictate: listening",
            DictationState.Processing => "PrimeDictate: transcribing",
            _ => "PrimeDictate: ready"
        };
        this.toggleItem.Header = state == DictationState.Listening ? "Stop and type" : "Start dictation";
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

        this.settingsWindow = new DictationSettingsWindow(this.host, this.workspace.MicrophoneSource);
        this.settingsWindow.Closed += (_, _) => this.overlay.Configure(this.host.Settings.OverlayMode, this.host.Settings.IsOverlaySticky);
        this.settingsWindow.Show();
    }
}
