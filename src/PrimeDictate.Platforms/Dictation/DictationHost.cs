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
    private readonly DictationHistoryStore history;
    private readonly DictationStatsStore stats;
    private readonly IHotkeySource? hotkeys;
    private readonly IForegroundTargetGuard guard;
    private readonly object providerSync = new();
    private ITranscriptionProvider? provider;
    private ITranscriptionProvider? wakeProvider;
    private string? wakeProviderId;
    private string? wakeKey;
    private string? providerId;
    private string? lastModelNotice;
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
        this.history = new DictationHistoryStore(paths);
        this.stats = new DictationStatsStore(paths);
        this.hotkeys = hotkeys;
        this.guard = guard ?? PlatformInput.CreateForegroundGuard();
        var load = this.store.Load();
        // A setup carried over from another PC (a Snapdragon laptop's Qualcomm model, a GPU choice here without a GPU) is made to fit this one, in memory only.
        var fitted = HardwareNormalization.Normalize(load.Settings, MachineSupport.Current, paths.ModelsDirectory);
        this.Settings = load.Settings;
        this.StartupNotice = load.Warning is null ? fitted : fitted is null ? load.Warning : $"{load.Warning} {fitted}";
        if (audio is null)
        {
            // As in the WPF app, hotkeys are bound without a microphone: the history hotkey still works and the dictation hotkey says why it cannot.
            this.UnavailableReason = "No microphone capture is available on this system.";
            this.BindHotkeys();
            return;
        }

        var microphoneSource = new DefaultMicrophoneFallback(audio, message => this.Notice?.Invoke(message));
        this.Controller = new DictationController(
            microphoneSource,
            this.GetProvider,
            this.guard,
            injector ?? new SharpHookTextInjector(),
            microphone,
            voiceCommands ?? new VoiceCommandProcessor(() => this.Settings.ToVoiceCommandOptions()),
            rewriter: new OllamaRewriter(() => this.Settings.ToOllamaOptions(), report: message => this.Notice?.Invoke(message)),
            shellRunner: new ProcessVoiceShellCommandRunner());
        this.Controller.Notice += message => this.Notice?.Invoke(message);
        this.Controller.HistoryRequested += () => this.HistoryRequested?.Invoke();
        this.Controller.Committed += this.OnCommitted;
        this.Controller.Options = this.Settings.ToOptions();
        this.Wake = new WakeWordListener(microphoneSource, this.TranscribeWakeAsync);
        microphone.Register(this.Wake);
        this.Wake.Notice += message => this.Notice?.Invoke(message);
        this.Wake.WakeDetected += () => _ = Task.Run(this.StartFromWakeAsync);
        this.wakeKey = WakeKey(this.Settings);
        this.ConfigureWake();
        this.BindHotkeys();
    }

    private void BindHotkeys()
    {
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

    /// <summary>Null when the microphone is unavailable.</summary>
    public WakeWordListener? Wake { get; }

    public string? StartupNotice { get; }

    /// <summary>True until setup was finished here or in the WPF app (its <c>FirstRunCompleted</c> is honoured), so a WPF user upgrading is not shown setup again.</summary>
    public bool IsFirstRun => this.Settings.FirstRunCompleted != true;

    public string ModelsFolder => System.IO.Path.Combine(this.paths.ModelsDirectory, "whisper");

    public DictationHistoryStore History => this.history;

    /// <summary>Raised after a commit has been written to history.</summary>
    public event Action<DictationHistoryEntry>? HistoryChanged;

    public string? HotkeyUnavailableReason => this.hotkeys?.UnavailableReason;

    public bool FocusGuardAvailable => this.guard.IsAvailable;

    public string? FocusGuardNotice => this.guard.IsAvailable || this.Settings.TypeWithoutFocusGuard ? null : this.guard.UnavailableReason;

    public event Action<string>? Notice;

    public event Action? HistoryRequested;

    public IReadOnlyList<InstalledSpeechModel> InstalledModels() => SpeechModelLocator.DiscoverFor(this.paths.ModelsDirectory, this.Settings);

    /// <summary>The model id dictation wants (the custom <c>ModelPath</c> model when it is valid, else the selected one).</summary>
    public string? WantedModelId() => SpeechModelLocator.WantedModelId(this.Settings);

    /// <summary>Raises <see cref="HistoryRequested"/> as the history hotkey and voice command do (the Settings window button).</summary>
    public void RequestHistory() => this.HistoryRequested?.Invoke();

    /// <summary>Downloads a catalog model into the shared managed folder (the same one the WPF app uses).</summary>
    public Task<string> DownloadModelAsync(ModelDownloadOption option, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken) =>
        new ModelDownloader().DownloadAsync(option, this.paths.ModelsDirectory, progress, cancellationToken);

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
                Core.Diagnostics.AppLog.Fault("hotkeys", ex);
                this.Notice?.Invoke(OperatingSystem.IsMacOS()
                    ? $"Global hotkeys need Accessibility permission (System Settings, Privacy and Security). {ex.Message}"
                    : $"Global hotkeys could not start: {ex.Message}");
            }
        });
    }

    /// <summary>Starts wake word listening if the user enabled it. Call after the shell is up.</summary>
    public void StartWakeWord() => _ = this.Wake?.EnsureRunningAsync();

    public void ApplySettings(DictationSettings settings, bool persist = true)
    {
        this.Settings = settings;
        Volatile.Write(ref this.lastModelNotice, null);
        if (this.Controller is not null)
        {
            this.Controller.Options = settings.ToOptions();
        }

        if (this.Wake is not null)
        {
            // As the WPF app does on save: stop, reconfigure, then start again, so a new microphone (or a different wake model) takes effect now.
            _ = this.ReconfigureWakeAsync();
        }

        this.hotkeys?.SetBindings(settings.ToBindings());
        if (persist)
        {
            this.store.Save(settings);
        }
    }

    /// <summary>
    /// What the wake listener was opened with: microphone, wake model and gain-independent model choice. The Settings window edits the live
    /// settings object in place, so the previous values are remembered as this key rather than compared against a copy.
    /// </summary>
    public static string WakeKey(DictationSettings settings) =>
        $"{(string.IsNullOrWhiteSpace(settings.SelectedInputDeviceId) ? string.Empty : settings.SelectedInputDeviceId)}|{settings.TranscriptionBackend}|{settings.ResolveModelId()}|{settings.ModelPath}";

    private async Task ReconfigureWakeAsync()
    {
        var wake = this.Wake;
        if (wake is null)
        {
            return;
        }

        try
        {
            var key = WakeKey(this.Settings);
            var restart = Interlocked.Exchange(ref this.wakeKey, key) != key;
            if (restart || !this.Settings.EnableWakeWord || this.Controller?.IsRecording == true)
            {
                await wake.StopAsync().ConfigureAwait(false);
            }

            this.ConfigureWake();
            if (this.Settings.EnableWakeWord && this.Controller?.IsRecording != true)
            {
                await wake.EnsureRunningAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Core.Diagnostics.AppLog.Fault("wake-word", ex);
        }
    }

    /// <summary>Saves where the user dragged the overlay (the new app's settings file only).</summary>
    public void RememberOverlayAnchor(int x, int y)
    {
        this.Settings.OverlayAnchorX = x;
        this.Settings.OverlayAnchorY = y;
        this.store.Save(this.Settings);
    }

    /// <summary>Saves the overlay's pin (keep it on screen), the same <c>IsOverlaySticky</c> setting the Settings checkbox changes.</summary>
    public void RememberOverlayPinned(bool pinned)
    {
        this.Settings.IsOverlaySticky = pinned;
        this.store.Save(this.Settings);
    }

    /// <summary>First step of leaving: no hotkey or wake word can start a new dictation while the app shuts down.</summary>
    public void StopListeningForExit()
    {
        if (this.hotkeys is not null)
        {
            this.hotkeys.Pressed -= this.OnHotkey;
        }

        this.Wake?.Configure(false, this.Settings.WakeWordPhrase, this.Settings.SelectedInputDeviceId, this.Settings.InputGainMultiplier);
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

        if (this.Wake is not null)
        {
            await this.Wake.DisposeAsync().ConfigureAwait(false);
        }

        ITranscriptionProvider?[] old;
        lock (this.providerSync)
        {
            old = [this.provider, this.wakeProvider];
            this.provider = null;
            this.wakeProvider = null;
        }

        foreach (var p in old)
        {
            if (p is not null)
            {
                await p.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void OnHotkey(HotkeyAction action)
    {
        var controller = this.Controller;

        // Runs on the hook thread: offload everything.
        _ = Task.Run(async () =>
        {
            try
            {
                switch (action)
                {
                    case HotkeyAction.ToggleDictation:
                        if (controller is null)
                        {
                            this.Notice?.Invoke(this.UnavailableReason ?? "Dictation is unavailable.");
                            break;
                        }

                        await controller.ToggleAsync().ConfigureAwait(false);
                        break;
                    case HotkeyAction.EmergencyStop:
                        if (controller is not null)
                        {
                            await controller.DiscardAsync().ConfigureAwait(false);
                        }

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
        var wanted = this.WantedModelId();
        var model = SpeechModelLocator.Resolve(installed, this.Settings);
        if (model is null)
        {
            var missing = $"The selected {this.Settings.TranscriptionBackend} model ({this.Settings.SelectedModelId}) is not installed";
            model = installed.FirstOrDefault();
            if (model is null)
            {
                if (wanted is not null)
                {
                    this.ModelNotice($"{missing}. Download it in Settings, or pick another.");
                }

                return null;
            }

            this.ModelNotice(wanted is null
                ? $"No dictation model is selected; using {model.DisplayName}."
                : $"{missing}, so {model.DisplayName} is used. Download it in Settings, or pick another.");
        }

        lock (this.providerSync)
        {
            // Keyed on id and folder: a custom ModelPath can point at another folder of the same model id.
            if (this.provider is null || this.providerId != model.ProviderKey)
            {
                _ = this.provider?.DisposeAsync();
                this.provider = CreateProvider(model);
                this.providerId = model.ProviderKey;
            }

            return this.provider;
        }
    }

    /// <summary>
    /// The model is looked up for every dictation and, with the wake word on, for every stretch of speech it checks, so a
    /// problem is said once (again after Settings change), not every second over the overlay.
    /// </summary>
    private void ModelNotice(string message)
    {
        if (Interlocked.Exchange(ref this.lastModelNotice, message) != message)
        {
            this.Notice?.Invoke(message);
        }
    }

    /// <summary>Lifetime totals (same <c>stats.json</c> as the WPF app). Built from history the first time.</summary>
    public DictationStatsState Stats() => this.stats.LoadOrCreate(() => this.history.List(limit: DictationHistoryStore.MaxEntries));

    private void OnCommitted(DictationCommit commit)
    {
        DictationHistoryEntry entry;
        try
        {
            entry = DictationHistoryEntry.From(commit);
            this.history.Add(entry);
            this.HistoryChanged?.Invoke(entry);
        }
        catch (Exception ex)
        {
            this.Notice?.Invoke($"Could not save dictation history: {ex.Message}");
            return;
        }

        try
        {
            foreach (var achievement in this.stats.Record(entry).NewAchievements)
            {
                this.Notice?.Invoke($"{achievement.Title}: {achievement.Message}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.Notice?.Invoke($"Could not save dictation stats: {ex.Message}");
        }
    }

    private static ITranscriptionProvider CreateProvider(InstalledSpeechModel model) => SpeechProviders.Create(model);

    private void ConfigureWake()
    {
        if (this.Wake is not { } wake)
        {
            return;
        }

        var settings = this.Settings;
        wake.Configure(settings.EnableWakeWord, settings.WakeWordPhrase, settings.SelectedInputDeviceId, settings.InputGainMultiplier);
        if (settings.EnableWakeWord && this.ResolveWakeModel() is null)
        {
            // Checked once here, as the WPF app did, instead of failing on every stretch of speech: no model means no listening and one notice.
            wake.Disable("Wake word listening needs a speech model. Download one in Settings.");
        }
    }

    private async Task StartFromWakeAsync()
    {
        var controller = this.Controller;
        if (controller is null || controller.IsRecording)
        {
            return;
        }

        try
        {
            await controller.ToggleAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.Notice?.Invoke($"Wake word could not start dictation: {ex.Message}");
        }

        if (!controller.IsRecording && this.Wake is not null)
        {
            // Dictation did not start (no model, busy microphone); keep listening.
            await this.Wake.EnsureRunningAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<string> TranscribeWakeAsync(ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
    {
        var wake = this.GetWakeProvider();
        if (wake is null)
        {
            throw new InvalidOperationException("No speech model is installed for wake word listening.");
        }

        var segments = await wake.RecognizeWindowAsync(samples, null, cancellationToken).ConfigureAwait(false);
        return string.Join(' ', segments.Select(s => s.Text.Trim()));
    }

    /// <summary>
    /// The model wake listening uses: a small one of the dictation model's family when installed (<see cref="WakeModelChooser"/>, as the WPF app does),
    /// else the dictation model, else null when nothing is installed.
    /// </summary>
    private InstalledSpeechModel? ResolveWakeModel()
    {
        var installed = this.InstalledModels();
        var dictation = SpeechModelLocator.Resolve(installed, this.Settings) ?? installed.FirstOrDefault();
        return (dictation is null ? null : WakeModelChooser.ChooseSmall(installed, dictation.Backend)) ?? dictation;
    }

    private ITranscriptionProvider? GetWakeProvider()
    {
        var model = this.ResolveWakeModel();
        if (model is null)
        {
            return null;
        }

        lock (this.providerSync)
        {
            if (this.wakeProvider is null || this.wakeProviderId != model.ProviderKey)
            {
                _ = this.wakeProvider?.DisposeAsync();
                this.wakeProvider = CreateProvider(model);
                this.wakeProviderId = model.ProviderKey;
            }

            return this.wakeProvider;
        }
    }
}
