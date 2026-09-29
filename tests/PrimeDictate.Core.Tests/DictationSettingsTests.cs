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
    public void Non_whisper_backends_have_no_model_yet_and_invalid_hotkeys_fall_back()
    {
        var settings = new DictationSettings { TranscriptionBackend = LegacyBackend.Parakeet, SelectedModelId = "x" };
        Assert.Null(settings.ResolveModelId());
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
