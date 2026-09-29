using PrimeDictate.Core.Coordination;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Providers;
using PrimeDictate.Core.Storage;
using PrimeDictate.Platforms.Input;
using PrimeDictate.Platforms.Speech;

namespace PrimeDictate.Platforms.Dictation;

/// <summary>
/// Composes dictation for the desktop shell: settings, the installed model, global hotkeys, typing and the
/// foreground guard around one <see cref="DictationController"/>. Holds no UI; the shell listens to the events.
/// </summary>
public sealed class DictationHost : IAsyncDisposable
{
    private readonly AppDataPaths paths;
    private readonly DictationSettingsStore store;
    private readonly IHotkeySource? hotkeys;
    private readonly IForegroundTargetGuard guard;
    private readonly object providerSync = new();
    private SherpaWhisperProvider? provider;
    private string? providerId;
    private bool disposed;

    public DictationHost(
        AppDataPaths paths,
        MicrophoneCoordinator microphone,
        IAudioSource? audio,
        IHotkeySource? hotkeys = null,
        ITextInjector? injector = null,
        IForegroundTargetGuard? guard = null,
        IVoiceCommandProcessor? voiceCommands = null)
    {
        this.paths = paths;
        this.store = new DictationSettingsStore(paths);
        this.hotkeys = hotkeys;
        this.guard = guard ?? PlatformInput.CreateForegroundGuard();
        var load = this.store.Load();
        this.Settings = load.Settings;
        this.StartupNotice = load.Warning;
        if (audio is null)
        {
            this.UnavailableReason = "No microphone capture is available on this system.";
            return;
        }

        this.Controller = new DictationController(
            audio,
            this.GetProvider,
            this.guard,
            injector ?? new SharpHookTextInjector(),
            microphone,
            voiceCommands);
        this.Controller.Notice += message => this.Notice?.Invoke(message);
        this.Controller.HistoryRequested += () => this.HistoryRequested?.Invoke();
        this.Controller.Options = this.Settings.ToOptions();
        if (this.hotkeys is not null)
        {
            this.hotkeys.SetBindings(this.Settings.ToBindings());
            this.hotkeys.Pressed += this.OnHotkey;
        }
    }

    public DictationSettings Settings { get; private set; }

    /// <summary>Null when the microphone is unavailable.</summary>
    public DictationController? Controller { get; }

    public string? UnavailableReason { get; }

    public string? StartupNotice { get; }

    public string? HotkeyUnavailableReason => this.hotkeys?.UnavailableReason;

    public bool FocusGuardAvailable => this.guard.IsAvailable;

    public string? FocusGuardNotice => this.guard.IsAvailable || this.Settings.TypeWithoutFocusGuard ? null : this.guard.UnavailableReason;

    public event Action<string>? Notice;

    public event Action? HistoryRequested;

    public IReadOnlyList<InstalledWhisperModel> InstalledModels() => WhisperOnnxModelLocator.Discover(this.paths.ModelsDirectory);

    /// <summary>Starts the global hotkey hook in the background. Failure (missing permission, Wayland) is reported, not thrown.</summary>
    public void StartHotkeys()
    {
        if (this.hotkeys is null)
        {
            return;
        }

        if (this.hotkeys.UnavailableReason is { } reason)
        {
            this.Notice?.Invoke(reason);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await this.hotkeys.RunAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.Notice?.Invoke(OperatingSystem.IsMacOS()
                    ? $"Global hotkeys need Accessibility permission (System Settings, Privacy and Security). {ex.Message}"
                    : $"Global hotkeys could not start: {ex.Message}");
            }
        });
    }

    public void ApplySettings(DictationSettings settings, bool persist = true)
    {
        this.Settings = settings;
        if (this.Controller is not null)
        {
            this.Controller.Options = settings.ToOptions();
        }

        this.hotkeys?.SetBindings(settings.ToBindings());
        if (persist)
        {
            this.store.Save(settings);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        if (this.hotkeys is not null)
        {
            this.hotkeys.Pressed -= this.OnHotkey;
            this.hotkeys.Dispose();
        }

        if (this.Controller is not null)
        {
            await this.Controller.DisposeAsync().ConfigureAwait(false);
        }

        SherpaWhisperProvider? old;
        lock (this.providerSync)
        {
            old = this.provider;
            this.provider = null;
        }

        if (old is not null)
        {
            await old.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnHotkey(HotkeyAction action)
    {
        var controller = this.Controller;
        if (controller is null)
        {
            return;
        }

        // Runs on the hook thread: offload everything.
        _ = Task.Run(async () =>
        {
            try
            {
                switch (action)
                {
                    case HotkeyAction.ToggleDictation:
                        await controller.ToggleAsync().ConfigureAwait(false);
                        break;
                    case HotkeyAction.EmergencyStop:
                        await controller.DiscardAsync().ConfigureAwait(false);
                        break;
                    case HotkeyAction.ShowHistory:
                        this.HistoryRequested?.Invoke();
                        break;
                }
            }
            catch (Exception ex)
            {
                this.Notice?.Invoke($"Hotkey action failed: {ex.Message}");
            }
        });
    }

    private ITranscriptionProvider? GetProvider()
    {
        var installed = this.InstalledModels();
        var wanted = this.Settings.ResolveModelId();
        var model = installed.FirstOrDefault(m => m.ModelId == wanted);
        if (model is null)
        {
            if (wanted is null && this.Settings.TranscriptionBackend != LegacyBackend.Whisper)
            {
                this.Notice?.Invoke($"The {this.Settings.TranscriptionBackend} backend is not in the new app yet. Pick a Whisper model in Settings.");
                return null;
            }

            model = installed.FirstOrDefault();
            if (model is null)
            {
                return null;
            }

            this.Notice?.Invoke($"No dictation model is selected; using {model.DisplayName}.");
        }

        lock (this.providerSync)
        {
            if (this.provider is null || this.providerId != model.ModelId)
            {
                _ = this.provider?.DisposeAsync();
                this.provider = new SherpaWhisperProvider(model);
                this.providerId = model.ModelId;
            }

            return this.provider;
        }
    }
}
