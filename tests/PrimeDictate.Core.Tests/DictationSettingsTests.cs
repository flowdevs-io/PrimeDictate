using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Storage;
using PrimeDictate.Platforms.Input;

namespace PrimeDictate.Core.Tests;

public sealed class DictationSettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-dict-" + Guid.NewGuid().ToString("N"));

    public DictationSettingsTests() => Directory.CreateDirectory(this.root);

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private const string WpfJson = """
        {
          "FirstRunCompleted": true,
          "DictationHotkey": { "KeyCode": "VcF9", "Ctrl": true, "Shift": false, "Alt": true },
          "StopHotkey": { "KeyCode": "VcEnter", "Ctrl": true, "Shift": true, "Alt": false },
          "TranscriptionBackend": "Whisper",
          "SelectedModelId": "base.en",
          "InputGainMultiplier": 2.5,
          "AutoCommitSilenceSeconds": 5,
          "SendEnterAfterCommit": true,
          "OverlayMode": "FullPanel",
          "TranscriptReplacements": [ { "Find": "primed ictate", "Replace": "PrimeDictate" } ],
          "EnableWakeWord": true,
          "SomethingNewer": 1
        }
        """;

    [Fact]
    public void Wpf_settings_are_imported_read_only_and_never_rewritten()
    {
        var paths = new AppDataPaths(this.root);
        var wpf = Path.Combine(this.root, "settings.json");
        File.WriteAllText(wpf, WpfJson);
        var store = new DictationSettingsStore(paths);

        var load = store.Load();
        Assert.True(load.ImportedFromWpf);
        Assert.Equal("whisper-onnx:base.en", load.Settings.ResolveModelId());
        Assert.Equal(new HotkeyGesture("VcF9", true, false, true), load.Settings.ToBindings()[HotkeyAction.ToggleDictation]);
        var options = load.Settings.ToOptions();
        Assert.Equal(2.5, options.InputGain);
        Assert.Equal(TimeSpan.FromSeconds(5), options.AutoCommitSilence);
        Assert.True(options.SendEnterAfterCommit);
        Assert.Equal("PrimeDictate", Assert.Single(options.Replacements).Replace);
        Assert.Equal(OverlayStyle.FullPanel, load.Settings.OverlayMode);

        store.Save(load.Settings);
        Assert.Equal(WpfJson, File.ReadAllText(wpf));
        Assert.False(store.Load().ImportedFromWpf);
    }

    [Fact]
    public void Wpf_voice_shell_commands_are_imported_with_their_enabled_state_and_new_ones_round_trip()
    {
        var paths = new AppDataPaths(this.root);
        File.WriteAllText(Path.Combine(this.root, "settings.json"), """
            {
              "VoiceShellCommands": [
                { "Enabled": true, "Phrase": "open notes", "CompletionBehavior": "Continue", "Command": "notepad" },
                { "Enabled": false, "Phrase": "lock screen", "CompletionBehavior": "Stop", "Command": "rundll32 user32.dll,LockWorkStation" }
              ]
            }
            """);
        var store = new DictationSettingsStore(paths);
        var load = store.Load();
        var commands = load.Settings.VoiceShellCommands;
        Assert.Equal(2, commands.Count);
        Assert.True(commands[0].Enabled);
        Assert.Equal(VoiceShellCommandCompletionBehavior.Continue, commands[0].CompletionBehavior);
        Assert.False(commands[1].Enabled);
        Assert.Equal(2, load.Settings.ToVoiceCommandOptions().ShellCommands.Count);

        store.Save(load.Settings);
        var again = store.Load().Settings.VoiceShellCommands;
        Assert.Equal(["open notes", "lock screen"], again.Select(c => c.Phrase).ToArray());
        Assert.Equal([true, false], again.Select(c => c.Enabled).ToArray());
        Assert.Empty(new DictationSettings().VoiceShellCommands);
    }

    [Fact]
    public void Missing_or_broken_files_fall_back_to_defaults_without_touching_them()
    {
        var store = new DictationSettingsStore(new AppDataPaths(this.root));
        Assert.Equal(HotkeyGesture.Default, store.Load().Settings.ToBindings()[HotkeyAction.ToggleDictation]);

        File.WriteAllText(store.WpfSettingsPath, "{ not json");
        var load = store.Load();
        Assert.NotNull(load.Warning);
        Assert.Equal("{ not json", File.ReadAllText(store.WpfSettingsPath));
    }

    [Fact]
    public void Backends_map_to_their_own_model_ids_and_invalid_hotkeys_fall_back()
    {
        var settings = new DictationSettings { TranscriptionBackend = LegacyBackend.Parakeet, SelectedModelId = "x" };
        Assert.Equal("parakeet-onnx:x", settings.ResolveModelId());
        settings.DictationHotkey = new HotkeyDto { KeyCode = "VcSpace" };
        Assert.Equal(HotkeyGesture.Default, settings.ToBindings()[HotkeyAction.ToggleDictation]);
    }

    [Fact]
    public void Hotkey_matching_allows_extra_modifiers_and_prefers_stop_over_history_over_dictation()
    {
        var overlap = new HotkeyGesture("VcH", true, false, false);
        var bindings = new Dictionary<HotkeyAction, HotkeyGesture>
        {
            [HotkeyAction.ToggleDictation] = overlap,
            [HotkeyAction.ShowHistory] = overlap,
            [HotkeyAction.EmergencyStop] = new HotkeyGesture("VcEnter", true, true, false)
        };
        Assert.Equal(HotkeyAction.ShowHistory, HotkeyMatcher.Match("VcH", true, true, true, bindings));
        Assert.Equal(HotkeyAction.EmergencyStop, HotkeyMatcher.Match("VcEnter", true, true, false, bindings));
        Assert.Null(HotkeyMatcher.Match("VcEnter", true, false, false, bindings));
        Assert.Null(HotkeyMatcher.Match("VcX", true, true, true, bindings));
        Assert.Equal("Ctrl+Shift+Space", HotkeyGesture.Default.ToString());
    }

    [Fact]
    public void Wayland_without_xwayland_is_reported_instead_of_failing_silently()
    {
        static Func<string, string?> Env(params (string, string)[] pairs) => key => pairs.FirstOrDefault(p => p.Item1 == key).Item2;
        Assert.NotNull(SharpHookHotkeySource.DetectUnavailableReason(Env(("XDG_SESSION_TYPE", "wayland"), ("WAYLAND_DISPLAY", "wayland-0")), isLinux: true));
        Assert.Null(SharpHookHotkeySource.DetectUnavailableReason(Env(("XDG_SESSION_TYPE", "wayland"), ("DISPLAY", ":0")), isLinux: true));
        Assert.Null(SharpHookHotkeySource.DetectUnavailableReason(Env(("DISPLAY", ":0")), isLinux: true));
        Assert.NotNull(SharpHookHotkeySource.DetectUnavailableReason(Env(), isLinux: true));
        Assert.Null(SharpHookHotkeySource.DetectUnavailableReason(Env(), isLinux: false));
    }
}

public sealed class DictationHostTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-host-" + Guid.NewGuid().ToString("N"));

    public DictationHostTests() => Directory.CreateDirectory(this.root);

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private sealed class FakeHotkeys : IHotkeySource
    {
        public event Action<HotkeyAction>? Pressed;

        public string? UnavailableReason => null;

        public IReadOnlyDictionary<HotkeyAction, HotkeyGesture>? Bindings { get; private set; }

        public void SetBindings(IReadOnlyDictionary<HotkeyAction, HotkeyGesture> bindings) => this.Bindings = bindings;

        public Task RunAsync() => Task.CompletedTask;

        public void Raise(HotkeyAction action) => this.Pressed?.Invoke(action);

        public void Dispose()
        {
        }
    }

    private sealed class NoAudio : PrimeDictate.Core.Providers.IAudioSource
    {
        public ValueTask<IReadOnlyList<PrimeDictate.Core.Providers.AudioInputDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<PrimeDictate.Core.Providers.AudioInputDevice>>([]);

        public ValueTask<PrimeDictate.Core.Providers.IAudioCaptureLease> OpenAsync(string? deviceId, CancellationToken cancellationToken) =>
            throw new PrimeDictate.Core.Providers.AudioSourceException(PrimeDictate.Core.Providers.AudioSourceErrorKind.DeviceRemoved, "unplugged");
    }

    [Fact]
    public async Task Hotkey_press_reaches_the_controller_and_reports_a_missing_model()
    {
        var hotkeys = new FakeHotkeys();
        await using var host = new PrimeDictate.Platforms.Dictation.DictationHost(
            new AppDataPaths(this.root), new PrimeDictate.Core.Coordination.MicrophoneCoordinator(), new NoAudio(), hotkeys);
        var notice = new TaskCompletionSource<string>();
        host.Notice += m => notice.TrySetResult(m);
        Assert.Equal(HotkeyGesture.Default, hotkeys.Bindings![HotkeyAction.ToggleDictation]);

        hotkeys.Raise(HotkeyAction.ToggleDictation);
        Assert.Contains("model", await notice.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(host.Controller!.IsRecording);
    }

    [Fact]
    public async Task Applying_settings_rebinds_hotkeys_and_persists_only_the_new_file()
    {
        var hotkeys = new FakeHotkeys();
        await using var host = new PrimeDictate.Platforms.Dictation.DictationHost(
            new AppDataPaths(this.root), new PrimeDictate.Core.Coordination.MicrophoneCoordinator(), new NoAudio(), hotkeys);
        var settings = host.Settings;
        settings.DictationHotkey = HotkeyDto.From(new HotkeyGesture("VcF8", true, false, false));
        settings.AutoCommitSilenceSeconds = 0;
        host.ApplySettings(settings);

        Assert.Equal("VcF8", hotkeys.Bindings![HotkeyAction.ToggleDictation].Key);
        Assert.Equal(TimeSpan.Zero, host.Controller!.Options.AutoCommitSilence);
        Assert.True(File.Exists(Path.Combine(this.root, "dictation-settings.json")));
        Assert.False(File.Exists(Path.Combine(this.root, "settings.json")));
    }

    [Fact]
    public async Task Without_a_microphone_the_host_says_why_and_has_no_controller()
    {
        await using var host = new PrimeDictate.Platforms.Dictation.DictationHost(
            new AppDataPaths(this.root), new PrimeDictate.Core.Coordination.MicrophoneCoordinator(), audio: null);
        Assert.Null(host.Controller);
        Assert.NotNull(host.UnavailableReason);
    }
}
