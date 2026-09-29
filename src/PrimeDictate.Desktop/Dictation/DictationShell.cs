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

        if (this.host.IsFirstRun)
        {
            new DictationOnboardingWindow(this.host, this.ShowSettings).Show();
        }

        this.host.StartHotkeys();
        this.host.StartWakeWord();
        this.overlay.SetState(DictationState.Idle);
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
        var settingsItem = new NativeMenuItem("Dictation settings...");
        settingsItem.Click += (_, _) => this.ShowSettings();
        var quitItem = new NativeMenuItem("Quit PrimeDictate");
        quitItem.Click += (_, _) => this.lifetime.Shutdown();
        this.tray.Menu = [this.toggleItem, workspaceItem, historyItem, settingsItem, new NativeMenuItemSeparator(), quitItem];
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
