using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Diagnostics;
using PrimeDictate.Core.Storage;
using PrimeDictate.Platforms.Speech;
using PrimeDictate.Platforms.Startup;
using PrimeDictate.Platforms.Updates;

namespace PrimeDictate.Core.Tests;

/// <summary>Logic ported from the WPF app's shell: tray click, overlay rules and ripple, history filters, wake model choice, launch scope, update setting.</summary>
public sealed class ShellParityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pd-shell-" + Guid.NewGuid().ToString("N"));

    public ShellParityTests() => Directory.CreateDirectory(this.root);

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    // ---- tray click ----

    [Fact]
    public void Single_click_behavior_opens_on_every_click()
    {
        var decider = new TrayClickDecider(() => TrayClickBehavior.SingleClickOpensWorkspace);
        var now = DateTime.UtcNow;
        Assert.True(decider.OnClick(now));
        Assert.True(decider.OnClick(now.AddSeconds(5)));
    }

    [Fact]
    public void Double_click_behavior_needs_two_close_clicks_and_consumes_them()
    {
        var decider = new TrayClickDecider(() => TrayClickBehavior.DoubleClickOpensWorkspace, TimeSpan.FromMilliseconds(500));
        var now = DateTime.UtcNow;
        Assert.False(decider.OnClick(now));
        Assert.True(decider.OnClick(now.AddMilliseconds(300)));
        Assert.False(decider.OnClick(now.AddMilliseconds(400))); // a third click starts over
        Assert.False(decider.OnClick(now.AddSeconds(5))); // too slow
        Assert.True(decider.OnClick(now.AddSeconds(5).AddMilliseconds(100)));
    }

    [Fact]
    public void Click_does_nothing_when_chosen_and_the_setting_is_read_each_time()
    {
        var behavior = TrayClickBehavior.ClickDoesNothing;
        var decider = new TrayClickDecider(() => behavior);
        var now = DateTime.UtcNow;
        Assert.False(decider.OnClick(now));
        Assert.False(decider.OnClick(now.AddMilliseconds(10)));
        behavior = TrayClickBehavior.SingleClickOpensWorkspace;
        Assert.True(decider.OnClick(now.AddSeconds(1)));
    }

    // ---- settings ----

    [Fact]
    public void Wpf_shell_settings_are_read_including_the_old_tray_names()
    {
        File.WriteAllText(Path.Combine(this.root, "settings.json"), """
            { "FirstRunCompleted": true, "CheckForUpdatesAutomatically": false, "TrayClickBehavior": "SingleClickOpensSettings", "BaselineTypingSpeedWpm": 65, "IsOverlaySticky": true }
            """);
        var settings = new DictationSettingsStore(new AppDataPaths(this.root)).Load().Settings;
        Assert.False(settings.CheckForUpdatesAutomatically);
        Assert.Equal(TrayClickBehavior.SingleClickOpensWorkspace, settings.TrayClickBehavior);
        Assert.Equal(65, settings.BaselineTypingSpeedWpm);
        Assert.True(settings.IsOverlaySticky);
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.False(settings.HideOverlayWhenIdle);
    }

    [Fact]
    public void Defaults_match_the_wpf_app_and_an_unknown_value_does_not_break_the_file()
    {
        var defaults = new DictationSettings();
        Assert.True(defaults.CheckForUpdatesAutomatically);
        Assert.Equal(TrayClickBehavior.DoubleClickOpensWorkspace, defaults.TrayClickBehavior);

        File.WriteAllText(Path.Combine(this.root, "dictation-settings.json"), """
            { "TrayClickBehavior": "FutureValue", "Theme": "Neon", "AutoCommitSilenceSeconds": 7 }
            """);
        var load = new DictationSettingsStore(new AppDataPaths(this.root)).Load();
        Assert.Null(load.Warning);
        Assert.Equal(7, load.Settings.AutoCommitSilenceSeconds);
        Assert.Equal(TrayClickBehavior.DoubleClickOpensWorkspace, load.Settings.TrayClickBehavior);
        Assert.Equal(AppTheme.System, load.Settings.Theme);
    }

    [Fact]
    public void New_shell_settings_round_trip()
    {
        var store = new DictationSettingsStore(new AppDataPaths(this.root));
        store.Save(new DictationSettings { CheckForUpdatesAutomatically = false, TrayClickBehavior = TrayClickBehavior.ClickDoesNothing, Theme = AppTheme.Dark, HideOverlayWhenIdle = true });
        var loaded = store.Load().Settings;
        Assert.False(loaded.CheckForUpdatesAutomatically);
        Assert.Equal(TrayClickBehavior.ClickDoesNothing, loaded.TrayClickBehavior);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.True(loaded.HideOverlayWhenIdle);
    }

    // ---- stats baseline ----

    [Theory]
    [InlineData(20, 20)]
    [InlineData(120, 120)]
    [InlineData(65, 65)]
    [InlineData(19, 40)]
    [InlineData(121, 40)]
    [InlineData(0, 40)]
    public void Baseline_is_kept_in_range_else_the_default(int wpm, int expected) =>
        Assert.Equal(expected, DictationStatsStore.NormalizeBaselineWpm(wpm));

    [Fact]
    public void Time_saved_follows_the_baseline()
    {
        var state = new DictationStatsState { TotalWords = 1200, TotalAudioSeconds = 300 };
        Assert.Equal(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(300), state.TimeSaved(40));
        Assert.Equal(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(300), state.TimeSaved(120));
    }

    // ---- overlay rules ----

    [Theory]
    [InlineData(false, false, false, false, OverlayStyle.CompactMicrophone, false, true)] // the compact microphone stays on screen (WPF)
    [InlineData(false, false, false, false, OverlayStyle.CompactMicrophone, true, false)] // unless told to hide when idle
    [InlineData(false, false, false, false, OverlayStyle.FullPanel, false, false)] // the full panel only while there is something to show
    [InlineData(false, false, false, true, OverlayStyle.FullPanel, false, true)] // pinned stays
    [InlineData(false, false, false, true, OverlayStyle.CompactMicrophone, true, true)] // pinned beats hide-when-idle
    [InlineData(false, true, false, false, OverlayStyle.FullPanel, false, true)] // dictating
    [InlineData(false, false, true, false, OverlayStyle.FullPanel, false, true)] // a notice or the last words
    [InlineData(true, true, true, true, OverlayStyle.CompactMicrophone, false, false)] // closed hides it until the next dictation
    public void Overlay_visibility_follows_the_wpf_persistence_rule(bool dismissed, bool active, bool lingering, bool pinned, OverlayStyle style, bool hideIdle, bool expected) =>
        Assert.Equal(expected, OverlayRules.ShouldShow(dismissed, active, lingering, pinned, style, hideIdle));

    [Fact]
    public void Overlay_wording_names_the_backend_like_the_wpf_header()
    {
        Assert.Equal("Listening [Whisper.net (GGML)]", OverlayRules.Header(OverlayPhase.Listening, OverlayRules.BackendLabel(LegacyBackend.WhisperNet)));
        Assert.Equal("Processing [Parakeet ONNX]", OverlayRules.Header(OverlayPhase.Processing, OverlayRules.BackendLabel(LegacyBackend.Parakeet)));
        Assert.Equal("Wake listening [Moonshine ONNX]", OverlayRules.Header(OverlayPhase.WakeListening, OverlayRules.BackendLabel(LegacyBackend.Moonshine)));
        Assert.Equal("Ready [Whisper ONNX]", OverlayRules.Header(OverlayPhase.Ready, OverlayRules.BackendLabel(LegacyBackend.Whisper)));
        Assert.Equal("Waiting for hotkey...", OverlayRules.Placeholder(OverlayPhase.Ready, "x"));
        Assert.Equal("Waiting for wake phrase...", OverlayRules.Placeholder(OverlayPhase.WakeListening, "x"));
        Assert.Equal("Listening with Whisper ONNX...", OverlayRules.Placeholder(OverlayPhase.Listening, "Whisper ONNX"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Waiting for hotkey...", false)]
    [InlineData("Waiting for wake phrase...", false)]
    [InlineData("Listening...", false)]
    [InlineData("Listening with Whisper ONNX...", false)]
    [InlineData("hello there", true)]
    public void Copy_is_enabled_only_for_real_text(string? text, bool expected) => Assert.Equal(expected, OverlayRules.IsCopyable(text));

    [Fact]
    public void Elapsed_time_shows_minutes_and_seconds()
    {
        Assert.Equal("00:00", OverlayRules.Elapsed(TimeSpan.Zero));
        Assert.Equal("01:05", OverlayRules.Elapsed(TimeSpan.FromSeconds(65)));
        Assert.Equal("75:00", OverlayRules.Elapsed(TimeSpan.FromMinutes(75)));
        Assert.Equal("00:00", OverlayRules.Elapsed(TimeSpan.FromSeconds(-3)));
    }

    [Fact]
    public void Tray_tooltips_carry_the_backend_and_fit_the_windows_limit()
    {
        Assert.Equal("PrimeDictate: ready [Whisper ONNX]", OverlayRules.TrayTooltip(OverlayPhase.Ready, "Whisper ONNX", false, false, null, "okay computer"));
        Assert.Equal("PrimeDictate: listening [Parakeet ONNX]", OverlayRules.TrayTooltip(OverlayPhase.Listening, "Parakeet ONNX", true, false, "Microphone", "x"));
        Assert.Equal("PrimeDictate: recording meeting (Microphone)", OverlayRules.TrayTooltip(OverlayPhase.Ready, "x", false, false, "Microphone", "x"));
        Assert.Equal("PrimeDictate: needs attention", OverlayRules.TrayTooltip(OverlayPhase.Ready, "x", true, false, null, "x"));
        Assert.Equal("PrimeDictate: wake listening failed", OverlayRules.TrayTooltip(OverlayPhase.WakeListening, "x", false, true, null, "x"));
        Assert.Equal("PrimeDictate: wake listening (okay computer)", OverlayRules.TrayTooltip(OverlayPhase.WakeListening, "x", false, false, null, "okay computer"));
        var longPhrase = OverlayRules.TrayTooltip(OverlayPhase.WakeListening, "x", false, false, null, new string('a', 80));
        Assert.Equal("PrimeDictate: wake listening", longPhrase);
        Assert.True(longPhrase.Length <= 63);
    }

    [Fact]
    public void Tray_attention_holds_for_ten_seconds_and_while_the_wake_word_is_failed()
    {
        var now = DateTime.UtcNow;
        Assert.True(TrayAttention.IsActive(now.AddSeconds(10), now.AddSeconds(9), false));
        Assert.False(TrayAttention.IsActive(now.AddSeconds(10), now.AddSeconds(11), false));
        Assert.True(TrayAttention.IsActive(DateTime.MinValue, now, true));
        Assert.Equal(TimeSpan.FromSeconds(10), TrayAttention.Hold);
    }

    [Fact]
    public void Logging_an_error_raises_the_event_for_the_tray_and_carries_no_text()
    {
        var before = AppLog.Directory;
        AppLog.Directory = this.root;
        try
        {
            var raised = 0;
            void Handler() => raised++;
            AppLog.ErrorLogged += Handler;
            try
            {
                AppLog.Error("test", "something failed");
                AppLog.Fault("test", new InvalidOperationException("boom"));
                AppLog.Event("test", "just information");
            }
            finally
            {
                AppLog.ErrorLogged -= Handler;
            }

            Assert.Equal(2, raised);
        }
        finally
        {
            AppLog.Directory = before;
        }
    }

    // ---- compact ripple ----

    [Fact]
    public void Ripple_rings_are_a_third_of_a_cycle_apart_and_stay_in_range()
    {
        var ripple = new CompactRipple();
        for (var i = 0; i < 200; i++)
        {
            ripple.SetLevel(0.05);
            ripple.Tick();
            for (var ring = 0; ring < CompactRipple.RingCount; ring++)
            {
                var (scale, opacity) = ripple.Ring(ring);
                Assert.InRange(scale, 0.32, 1.0);
                Assert.InRange(opacity, 0.0, 0.48);
            }

            Assert.InRange(ripple.Phase, 0.0, 1.0);
        }

        var phase = new CompactRipple();
        var (s0, _) = phase.Ring(0);
        var (s1, _) = phase.Ring(1);
        Assert.Equal(0.32, s0, 3);
        Assert.Equal(0.32 + (0.68 / 3), s1, 3);
    }

    [Fact]
    public void Ripple_is_brighter_with_voice_and_settles_when_it_stops()
    {
        var quiet = new CompactRipple();
        var loud = new CompactRipple();
        for (var i = 0; i < 30; i++)
        {
            quiet.Tick();
            loud.SetLevel(0.08);
            loud.Tick();
        }

        Assert.True(loud.Intensity > quiet.Intensity);
        Assert.True(loud.GlowOpacity > quiet.GlowOpacity);
        Assert.True(loud.GlowScale > quiet.GlowScale);
        for (var i = 0; i < 400; i++)
        {
            loud.Tick();
        }

        Assert.True(loud.Intensity < 0.2);
        loud.Reset();
        Assert.Equal(0, loud.Phase);
        Assert.Equal(0, loud.GlowOpacity);
    }

    [Fact]
    public void Ripple_phase_wraps_once_per_cycle()
    {
        var ripple = new CompactRipple();
        ripple.Tick(CompactRipple.SecondsPerCycle - 0.1);
        Assert.InRange(ripple.Phase, 0.9, 1.0);
        ripple.Tick(0.2);
        Assert.InRange(ripple.Phase, 0.0, 0.1);
    }

    // ---- history filters ----

    private static DictationHistoryEntry Entry(string text, DictationDeliveryStatus status, string? app, string? window, string? original = null, string? display = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, text, status, display, app, window, null, 3.5, false, original, null);

    private static readonly DictationHistoryEntry[] Sample =
    [
        Entry("send the report today", DictationDeliveryStatus.Injected, "Code.exe", "main.cs - Visual Studio Code"),
        Entry("meeting at noon", DictationDeliveryStatus.Injected, "Code.exe", "notes.md - Visual Studio Code"),
        Entry("refused text", DictationDeliveryStatus.SkippedFocusChanged, "chrome.exe", "Inbox - Mail", original: "raw spoken words"),
        Entry("no target", DictationDeliveryStatus.Discarded, null, null),
        Entry("from display", DictationDeliveryStatus.FailedToInject, "notepad.exe", null, display: "Untitled - Notepad (0x1A2B)")
    ];

    [Fact]
    public void History_status_filter_splits_typed_from_not_typed()
    {
        var typed = DictationHistoryFilter.Apply(Sample, null, HistoryStatusFilter.Typed, DictationHistoryFilter.AllApps, DictationHistoryFilter.AllWindows);
        var notTyped = DictationHistoryFilter.Apply(Sample, null, HistoryStatusFilter.NotTyped, DictationHistoryFilter.AllApps, DictationHistoryFilter.AllWindows);
        Assert.Equal(2, typed.Count);
        Assert.Equal(3, notTyped.Count);
        Assert.Equal(5, DictationHistoryFilter.Apply(Sample, null, HistoryStatusFilter.All, DictationHistoryFilter.AllApps, DictationHistoryFilter.AllWindows).Count);
    }

    [Fact]
    public void History_search_needs_every_word_and_looks_in_the_original_too()
    {
        Assert.Single(DictationHistoryFilter.Apply(Sample, "REPORT today", HistoryStatusFilter.All, DictationHistoryFilter.AllApps, DictationHistoryFilter.AllWindows));
        Assert.Empty(DictationHistoryFilter.Apply(Sample, "report noon", HistoryStatusFilter.All, DictationHistoryFilter.AllApps, DictationHistoryFilter.AllWindows));
        Assert.Single(DictationHistoryFilter.Apply(Sample, "spoken words", HistoryStatusFilter.All, DictationHistoryFilter.AllApps, DictationHistoryFilter.AllWindows));
    }

    [Fact]
    public void History_app_and_window_options_list_known_values_then_unknown()
    {
        var apps = DictationHistoryFilter.AppOptions(Sample);
        Assert.Equal(["All apps", "chrome.exe", "Code.exe", "notepad.exe", "Unknown app"], apps.Select(a => a.DisplayName));

        var code = apps.Single(a => a.Value == "Code.exe");
        var windows = DictationHistoryFilter.WindowOptions(Sample, code);
        Assert.Equal(["All windows", "main.cs - Visual Studio Code", "notes.md - Visual Studio Code"], windows.Select(w => w.DisplayName));

        var notepad = apps.Single(a => a.Value == "notepad.exe");
        // The title comes out of the display name when the window title was not stored.
        Assert.Equal(["All windows", "Untitled - Notepad"], DictationHistoryFilter.WindowOptions(Sample, notepad).Select(w => w.DisplayName));

        var unknown = apps.Single(a => a.Kind == HistoryTargetKind.Unknown);
        Assert.Equal(["All windows", "Unknown window"], DictationHistoryFilter.WindowOptions(Sample, unknown).Select(w => w.DisplayName));
    }

    [Fact]
    public void History_app_and_window_filters_narrow_the_list()
    {
        var apps = DictationHistoryFilter.AppOptions(Sample);
        var code = apps.Single(a => a.Value == "Code.exe");
        var windows = DictationHistoryFilter.WindowOptions(Sample, code);
        var result = DictationHistoryFilter.Apply(Sample, null, HistoryStatusFilter.All, code, windows.Single(w => w.Value == "notes.md - Visual Studio Code"));
        Assert.Equal("meeting at noon", Assert.Single(result).Transcript);
        Assert.Equal("no target", Assert.Single(DictationHistoryFilter.Apply(Sample, null, HistoryStatusFilter.All, apps.Single(a => a.Kind == HistoryTargetKind.Unknown), DictationHistoryFilter.AllWindows)).Transcript);
    }

    [Fact]
    public void History_details_text_has_the_fields_the_wpf_copy_details_had()
    {
        var rewritten = Entry("final text", DictationDeliveryStatus.Injected, "Code.exe", "main.cs", original: "spoken text") with { Error = "none really", RewriteSystemPrompt = "be brief" };
        var text = DictationHistoryFilter.DetailsText(rewritten);
        Assert.Contains("Target app: Code.exe", text);
        Assert.Contains("Target window: main.cs", text);
        Assert.Contains("Audio seconds: 3.5", text);
        Assert.Contains("Error: none really", text);
        Assert.Contains("Original Transcript:", text);
        Assert.Contains("spoken text", text);
        Assert.Contains("be brief", text);
        Assert.EndsWith("final text" + Environment.NewLine, text);
        Assert.DoesNotContain("Original Transcript:", DictationHistoryFilter.DetailsText(Sample[0]));
    }

    // ---- wake model ----

    private static InstalledSpeechModel Model(LegacyBackend backend, string id) => new(backend, id, id, "dir", false);

    [Fact]
    public void Wake_model_for_whisper_net_is_a_small_ggml_file()
    {
        var installed = new[] { Model(LegacyBackend.WhisperNet, "large-v3-turbo"), Model(LegacyBackend.WhisperNet, "base.en"), Model(LegacyBackend.WhisperNet, "tiny.en") };
        Assert.Equal("tiny.en", WakeModelChooser.ChooseSmall(installed, LegacyBackend.WhisperNet)?.Id);
        var onlyBase = new[] { Model(LegacyBackend.WhisperNet, "large-v3-turbo"), Model(LegacyBackend.WhisperNet, "base.en") };
        Assert.Equal(LegacyBackend.WhisperNet, WakeModelChooser.ChooseSmall(onlyBase, LegacyBackend.WhisperNet)?.Backend);
    }

    [Fact]
    public void Wake_model_for_moonshine_prefers_tiny_v2_then_base()
    {
        var both = new[] { Model(LegacyBackend.Moonshine, "moonshine-base-en"), Model(LegacyBackend.Moonshine, "moonshine-tiny-v2-en") };
        Assert.Equal("moonshine-tiny-v2-en", WakeModelChooser.ChooseSmall(both, LegacyBackend.Moonshine)?.Id);
        Assert.Equal("moonshine-base-en", WakeModelChooser.ChooseSmall([both[0]], LegacyBackend.Moonshine)?.Id);
    }

    [Fact]
    public void Wake_model_for_whisper_onnx_is_the_smallest_download_among_the_small_ones()
    {
        var installed = new[] { Model(LegacyBackend.Whisper, "small.en"), Model(LegacyBackend.Whisper, "base.en"), Model(LegacyBackend.Whisper, "distil-small.en"), Model(LegacyBackend.Whisper, "tiny.en") };
        Assert.Equal("tiny.en", WakeModelChooser.ChooseSmall(installed, LegacyBackend.Whisper)?.Id);
        Assert.Equal("distil-small.en", WakeModelChooser.ChooseSmall([installed[0], installed[2]], LegacyBackend.Whisper)?.Id);
        Assert.Null(WakeModelChooser.ChooseSmall([installed[0]], LegacyBackend.Whisper)); // small.en is not a wake model
    }

    [Fact]
    public void Wake_model_for_parakeet_borrows_a_small_whisper_when_installed_else_uses_the_dictation_model()
    {
        var withTiny = new[] { Model(LegacyBackend.Parakeet, "parakeet-tdt-0.6b-v3"), Model(LegacyBackend.Whisper, "tiny.en") };
        Assert.Equal("tiny.en", WakeModelChooser.ChooseSmall(withTiny, LegacyBackend.Parakeet)?.Id);
        Assert.Null(WakeModelChooser.ChooseSmall([withTiny[0]], LegacyBackend.Parakeet));
    }

    [Fact]
    public void Wake_model_family_comes_before_the_whisper_fallback()
    {
        var installed = new[] { Model(LegacyBackend.Whisper, "tiny.en"), Model(LegacyBackend.Moonshine, "moonshine-base-en") };
        Assert.Equal("moonshine-base-en", WakeModelChooser.ChooseSmall(installed, LegacyBackend.Moonshine)?.Id);
    }

    // ---- launch at login scope ----

    private sealed class FakeRunKey : IWindowsRunKey, IWindowsMachineRunKey
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? Get(string name) => this.Values.GetValueOrDefault(name);

        public void Set(string name, string value) => this.Values[name] = value;

        public void Delete(string name) => this.Values.Remove(name);
    }

    private sealed class FakeStartup : IWindowsStartupFolder
    {
        public bool InstallerShortcutExists { get; set; }

        public bool LegacyUserShortcutExists { get; set; }

        public bool InstallerShortcutDisabledForUser { get; set; }

        public void DeleteLegacyUserShortcut() => this.LegacyUserShortcutExists = false;

        public void SetInstallerShortcutEnabledForUser(bool enabled) => this.InstallerShortcutDisabledForUser = !enabled;
    }

    private const string Exe = @"C:\Program Files\PrimeDictate\PrimeDictate.exe";

    private sealed record LoginRig(LaunchAtLogin Login, FakeRunKey User, FakeRunKey Machine, FakeStartup Startup, List<string> Elevated);

    private LoginRig Rig(bool admin, string? elevateResult = null)
    {
        var user = new FakeRunKey();
        var machine = new FakeRunKey();
        var startup = new FakeStartup();
        var elevated = new List<string>();
        var login = new LaunchAtLogin(
            this.root,
            executable: () => Exe,
            windowsRunKey: user,
            windowsStartupFolder: startup,
            windowsMachineRunKey: machine,
            isAdministrator: () => admin,
            elevate: argument =>
            {
                elevated.Add(argument);
                if (elevateResult is null)
                {
                    // The helper process would make the change as an administrator.
                    if (argument == LoginScopeDecision.MachineOnArgument)
                    {
                        machine.Set(LaunchAtLogin.WindowsValueName, WindowsLoginDecision.RunCommand(Exe));
                    }
                    else
                    {
                        machine.Delete(LaunchAtLogin.WindowsValueName);
                    }
                }

                return elevateResult;
            });
        return new LoginRig(login, user, machine, startup, elevated);
    }

    [Theory]
    [InlineData(false, false, LoginScope.Off)]
    [InlineData(false, true, LoginScope.CurrentUser)]
    [InlineData(true, false, LoginScope.AllUsers)]
    [InlineData(true, true, LoginScope.AllUsers)]
    public void Scope_is_all_users_when_the_machine_entry_is_there(bool machine, bool user, LoginScope expected) =>
        Assert.Equal(expected, LoginScopeDecision.ScopeOf(machine, user));

    [Fact]
    public void Machine_entry_is_planned_only_for_all_users_and_removed_by_every_other_scope()
    {
        Assert.Equal(MachineRunAction.Write, LoginScopeDecision.PlanMachine(LoginScope.AllUsers, false));
        Assert.Equal(MachineRunAction.None, LoginScopeDecision.PlanMachine(LoginScope.AllUsers, true));
        Assert.Equal(MachineRunAction.Delete, LoginScopeDecision.PlanMachine(LoginScope.CurrentUser, true));
        Assert.Equal(MachineRunAction.Delete, LoginScopeDecision.PlanMachine(LoginScope.Off, true));
        Assert.Equal(MachineRunAction.None, LoginScopeDecision.PlanMachine(LoginScope.CurrentUser, false));
    }

    [Fact]
    public void All_users_as_an_administrator_writes_the_machine_entry_directly_and_drops_this_users_own()
    {
        var rig = this.Rig(admin: true);
        rig.User.Values["PrimeDictate"] = "\"" + Exe + "\" --background";
        Assert.Null(rig.Login.Apply(LoginScope.AllUsers));
        Assert.Equal("\"" + Exe + "\" --background", rig.Machine.Values["PrimeDictate"]);
        Assert.Empty(rig.User.Values);
        Assert.Empty(rig.Elevated);
        Assert.Equal(LoginScope.AllUsers, rig.Login.Scope);
    }

    [Fact]
    public void All_users_without_administrator_rights_asks_for_elevation_once()
    {
        var rig = this.Rig(admin: false);
        Assert.Null(rig.Login.Apply(LoginScope.AllUsers));
        Assert.Equal([LoginScopeDecision.MachineOnArgument], rig.Elevated);
        Assert.Equal(LoginScope.AllUsers, rig.Login.Scope);

        // Already set: nothing more to ask.
        Assert.Null(rig.Login.Apply(LoginScope.AllUsers));
        Assert.Single(rig.Elevated);
    }

    [Fact]
    public void Declined_elevation_is_reported_and_nothing_else_changes()
    {
        var rig = this.Rig(admin: false, elevateResult: "Administrator approval is required to change startup for all users.");
        Assert.Equal("Administrator approval is required to change startup for all users.", rig.Login.Apply(LoginScope.AllUsers));
        Assert.Empty(rig.Machine.Values);
        Assert.Empty(rig.User.Values);
    }

    [Fact]
    public void Current_user_scope_removes_a_machine_entry_and_turns_the_users_own_on()
    {
        var rig = this.Rig(admin: true);
        rig.Machine.Values["PrimeDictate"] = "\"" + Exe + "\" --background";
        Assert.Null(rig.Login.Apply(LoginScope.CurrentUser));
        Assert.Empty(rig.Machine.Values);
        Assert.Equal(LoginScope.CurrentUser, rig.Login.Scope);
    }

    [Fact]
    public void Current_user_scope_needs_no_elevation_when_there_is_no_machine_entry()
    {
        var rig = this.Rig(admin: false);
        Assert.Null(rig.Login.Apply(LoginScope.CurrentUser));
        Assert.Empty(rig.Elevated);
        Assert.Equal(LoginScope.CurrentUser, rig.Login.Scope);
        Assert.Null(rig.Login.Apply(LoginScope.Off));
        Assert.Equal(LoginScope.Off, rig.Login.Scope);
        Assert.Empty(rig.Elevated);
    }

    [Fact]
    public void Off_removes_the_machine_entry_too()
    {
        var rig = this.Rig(admin: true);
        Assert.Null(rig.Login.Apply(LoginScope.AllUsers));
        Assert.Null(rig.Login.Apply(LoginScope.Off));
        Assert.Empty(rig.Machine.Values);
        Assert.Equal(LoginScope.Off, rig.Login.Scope);
    }

    [Fact]
    public void All_users_is_refused_where_there_is_no_machine_run_key()
    {
        var login = new LaunchAtLogin(this.root, xdgConfigOverride: Path.Combine(this.root, "xdg"), executable: () => "/usr/bin/primedictate");
        if (OperatingSystem.IsWindows())
        {
            return; // On Windows the real registry seam is present; this case is about the other systems.
        }

        Assert.False(login.SupportsAllUsers);
        Assert.NotNull(login.Apply(LoginScope.AllUsers));
    }

    // ---- launch at login command line ----

    [Theory]
    [InlineData("--enable-launch-at-login")]
    [InlineData("--launch-at-login")]
    [InlineData("/EnableLaunchAtLogin")]
    public void Enable_switches_are_recognized(string argument)
    {
        var request = LaunchAtLoginCli.Parse([argument]);
        Assert.NotNull(request);
        Assert.True(request!.Enable);
        Assert.False(request.Disable);
        Assert.Null(request.Scope);
    }

    [Theory]
    [InlineData("--disable-launch-at-login")]
    [InlineData("--no-launch-at-login")]
    [InlineData("/DisableLaunchAtLogin")]
    public void Disable_switches_are_recognized(string argument) => Assert.True(LaunchAtLoginCli.Parse([argument])!.Disable);

    [Theory]
    [InlineData("--current-user", LoginScope.CurrentUser)]
    [InlineData("--scope=user", LoginScope.CurrentUser)]
    [InlineData("--scope=current-user", LoginScope.CurrentUser)]
    [InlineData("/CurrentUser", LoginScope.CurrentUser)]
    [InlineData("--all-users", LoginScope.AllUsers)]
    [InlineData("--scope=all-users", LoginScope.AllUsers)]
    [InlineData("--scope=machine", LoginScope.AllUsers)]
    [InlineData("/AllUsers", LoginScope.AllUsers)]
    public void Scope_switches_are_recognized(string scopeSwitch, LoginScope expected) =>
        Assert.Equal(expected, LaunchAtLoginCli.Parse(["--enable-launch-at-login", scopeSwitch])!.Scope);

    [Fact]
    public void Other_command_lines_are_not_launch_at_login_commands()
    {
        Assert.Null(LaunchAtLoginCli.Parse([]));
        Assert.Null(LaunchAtLoginCli.Parse(["--background", "--all-users"]));
        Assert.False(LaunchAtLoginCli.TryHandle(["--show"], this.Rig(true).Login, true, out _));
    }

    [Fact]
    public void Conflicting_switches_exit_with_2_and_change_nothing()
    {
        var rig = this.Rig(admin: true);
        Assert.True(LaunchAtLoginCli.TryHandle(["--enable-launch-at-login", "--disable-launch-at-login"], rig.Login, true, out var exit));
        Assert.Equal(2, exit);
        Assert.Empty(rig.Machine.Values);
    }

    [Fact]
    public void Enable_without_a_scope_is_all_users_when_elevated_else_the_current_user()
    {
        var elevated = this.Rig(admin: true);
        Assert.True(LaunchAtLoginCli.TryHandle(["--enable-launch-at-login"], elevated.Login, true, out var exit));
        Assert.Equal(0, exit);
        Assert.Equal(LoginScope.AllUsers, elevated.Login.Scope);

        var normal = this.Rig(admin: false);
        Assert.True(LaunchAtLoginCli.TryHandle(["--enable-launch-at-login"], normal.Login, false, out exit));
        Assert.Equal(0, exit);
        Assert.Equal(LoginScope.CurrentUser, normal.Login.Scope);
        Assert.Empty(normal.Elevated);
    }

    [Fact]
    public void Disable_for_the_current_user_leaves_the_all_users_entry_alone_but_disable_alone_clears_both()
    {
        var rig = this.Rig(admin: true);
        Assert.Null(rig.Login.Apply(LoginScope.AllUsers));
        Assert.True(LaunchAtLoginCli.TryHandle(["--disable-launch-at-login", "--current-user"], rig.Login, true, out var exit));
        Assert.Equal(0, exit);
        Assert.Single(rig.Machine.Values);

        Assert.True(LaunchAtLoginCli.TryHandle(["--disable-launch-at-login"], rig.Login, true, out exit));
        Assert.Equal(0, exit);
        Assert.Empty(rig.Machine.Values);
    }

    [Fact]
    public void A_failed_change_exits_with_1()
    {
        var rig = this.Rig(admin: false, elevateResult: "declined");
        Assert.True(LaunchAtLoginCli.TryHandle(["--enable-launch-at-login", "--all-users"], rig.Login, false, out var exit));
        Assert.Equal(1, exit);
    }

    // ---- update setting and last check ----

    [Fact]
    public void The_wpf_last_update_check_stands_in_until_this_app_has_its_own_record()
    {
        var wpf = Path.Combine(this.root, "settings.json");
        var own = Path.Combine(this.root, "update-check.json");
        var when = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(wpf, $$"""{ "LastUpdateCheckUtc": "{{when:O}}" }""");
        var state = new UpdateCheckState(own, wpf);
        Assert.Equal(when, state.LastCheckUtc());
        Assert.False(UpdateRules.IsCheckDue(state.LastCheckUtc(), when.AddHours(3)));

        // Once this app wrote its own record it alone decides, and a failed update (null) means "check again".
        state.Save(null);
        Assert.Null(state.LastCheckUtc());
        var later = when.AddDays(3);
        state.Save(later);
        Assert.Equal(later, state.LastCheckUtc());
    }

    [Fact]
    public void Missing_files_mean_a_check_is_due()
    {
        var state = new UpdateCheckState(Path.Combine(this.root, "none.json"), Path.Combine(this.root, "none-settings.json"));
        Assert.Null(state.LastCheckUtc());
        Assert.True(UpdateRules.IsCheckDue(state.LastCheckUtc(), DateTime.UtcNow));
    }
}
