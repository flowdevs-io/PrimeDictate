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
        this.Settings = load.Settings;
        this.StartupNotice = load.Warning;
        if (audio is null)
        {
            this.UnavailableReason = "No microphone capture is available on this system.";
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
        this.ConfigureWake();
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

    /// <summary>True until either app has saved settings, so a WPF user upgrading is not shown setup again.</summary>
    public bool IsFirstRun => !File.Exists(this.store.Path) && !File.Exists(this.store.WpfSettingsPath);

    public string ModelsFolder => System.IO.Path.Combine(this.paths.ModelsDirectory, "whisper");

    public DictationHistoryStore History => this.history;

    /// <summary>Raised after a commit has been written to history.</summary>
    public event Action<DictationHistoryEntry>? HistoryChanged;

    public string? HotkeyUnavailableReason => this.hotkeys?.UnavailableReason;

    public bool FocusGuardAvailable => this.guard.IsAvailable;

    public string? FocusGuardNotice => this.guard.IsAvailable || this.Settings.TypeWithoutFocusGuard ? null : this.guard.UnavailableReason;

    public event Action<string>? Notice;

    public event Action? HistoryRequested;

    public IReadOnlyList<InstalledSpeechModel> InstalledModels() => SpeechModelLocator.Discover(this.paths.ModelsDirectory);

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

        this.ConfigureWake();
        if (this.Wake is not null)
        {
            _ = settings.EnableWakeWord && this.Controller?.IsRecording != true ? this.Wake.EnsureRunningAsync() : this.Wake.StopAsync();
        }

        this.hotkeys?.SetBindings(settings.ToBindings());
        if (persist)
        {
            this.store.Save(settings);
        }
    }

    /// <summary>Saves where the user dragged the overlay (the new app's settings file only).</summary>
    public void RememberOverlayAnchor(int x, int y)
    {
        this.Settings.OverlayAnchorX = x;
        this.Settings.OverlayAnchorY = y;
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
            if (this.provider is null || this.providerId != model.ModelId)
            {
                _ = this.provider?.DisposeAsync();
                this.provider = CreateProvider(model);
                this.providerId = model.ModelId;
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

    private static ITranscriptionProvider CreateProvider(InstalledSpeechModel model) => model.Backend switch
    {
        LegacyBackend.WhisperNet => new WhisperNetProvider(model),
        LegacyBackend.Parakeet => new SherpaParakeetProvider(model),
        LegacyBackend.Moonshine => new SherpaMoonshineProvider(model),
        _ => new SherpaWhisperProvider(WhisperOnnxModelLocator.TryResolve(model.Directory, out var whisper)
            ? whisper
            : throw new FileNotFoundException($"The Whisper model folder is incomplete: {model.Directory}"))
    };

    private void ConfigureWake() =>
        this.Wake?.Configure(this.Settings.EnableWakeWord, this.Settings.WakeWordPhrase, this.Settings.SelectedInputDeviceId, this.Settings.InputGainMultiplier);

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

    /// <summary>Prefers a small model for idle listening (as the WPF app does) and falls back to the dictation model.</summary>
    private ITranscriptionProvider? GetWakeProvider()
    {
        var installed = this.InstalledModels();
        var small = new[] { "tiny.en", "base.en", "tiny", "base" }
            .Select(id => installed.FirstOrDefault(m => m.Backend == LegacyBackend.Whisper && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(m => m is not null);
        if (small is null)
        {
            return this.GetProvider();
        }

        lock (this.providerSync)
        {
            if (this.wakeProvider is null || this.wakeProviderId != small.ModelId)
            {
                _ = this.wakeProvider?.DisposeAsync();
                this.wakeProvider = CreateProvider(small);
                this.wakeProviderId = small.ModelId;
            }

            return this.wakeProvider;
        }
    }
}
