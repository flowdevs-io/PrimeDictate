using PrimeDictate.Platforms.Startup;

namespace PrimeDictate.Core.Tests;

public sealed class LaunchAtLoginTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "pd-login-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.home))
        {
            Directory.Delete(this.home, true);
        }
    }

    private sealed class FakeRunKey : IWindowsRunKey
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

    [Fact]
    public void Installer_shortcut_is_the_one_mechanism_the_checkbox_controls()
    {
        var key = new FakeRunKey();
        var startup = new FakeStartup { InstallerShortcutExists = true };
        var login = new LaunchAtLogin(this.home, executable: () => Exe, windowsRunKey: key, windowsStartupFolder: startup);

        Assert.True(login.IsEnabled);
        Assert.True(login.InstallerShortcutPresent);

        Assert.Null(login.Apply(false));
        Assert.False(login.IsEnabled);
        Assert.True(startup.InstallerShortcutDisabledForUser);
        Assert.Empty(key.Values);

        Assert.Null(login.Apply(true));
        Assert.True(login.IsEnabled);
        Assert.False(startup.InstallerShortcutDisabledForUser);
        Assert.Empty(key.Values); // never a Run value next to the shortcut: that would start the app twice
    }

    [Fact]
    public void Enabling_with_the_installer_shortcut_removes_a_stray_run_value()
    {
        var key = new FakeRunKey();
        key.Values["PrimeDictate"] = "\"" + Exe + "\"";
        var login = new LaunchAtLogin(this.home, executable: () => Exe, windowsRunKey: key, windowsStartupFolder: new FakeStartup { InstallerShortcutExists = true });
        Assert.Null(login.Apply(true));
        Assert.Empty(key.Values);
    }

    [Fact]
    public void Migration_moves_a_6_0_0_user_shortcut_to_the_run_value_when_there_is_no_installer_shortcut()
    {
        var key = new FakeRunKey();
        var startup = new FakeStartup { LegacyUserShortcutExists = true };
        var login = new LaunchAtLogin(this.home, executable: () => Exe, windowsRunKey: key, windowsStartupFolder: startup);
        login.MigrateLegacyEntries();
        Assert.False(startup.LegacyUserShortcutExists);
        Assert.Equal("\"" + Exe + "\" --background", key.Values["PrimeDictate"]);
        Assert.True(login.IsEnabled);
    }

    [Fact]
    public void Migration_just_deletes_old_entries_when_the_installer_shortcut_covers_launch_at_login()
    {
        var key = new FakeRunKey();
        key.Values["PrimeDictate"] = "\"" + Exe + "\"";
        var startup = new FakeStartup { LegacyUserShortcutExists = true, InstallerShortcutExists = true };
        new LaunchAtLogin(this.home, executable: () => Exe, windowsRunKey: key, windowsStartupFolder: startup).MigrateLegacyEntries();
        Assert.False(startup.LegacyUserShortcutExists);
        Assert.Empty(key.Values);
    }

    [Fact]
    public void Migration_does_nothing_when_nothing_was_set_up()
    {
        var key = new FakeRunKey();
        var login = new LaunchAtLogin(this.home, executable: () => Exe, windowsRunKey: key, windowsStartupFolder: new FakeStartup());
        login.MigrateLegacyEntries();
        Assert.Empty(key.Values);
        Assert.False(login.IsEnabled);
    }

    [Theory]
    [InlineData(false, null, false, false, false)]
    [InlineData(true, null, false, true, false)]
    [InlineData(true, null, true, false, false)]
    [InlineData(false, "\"C:\\gone\\PrimeDictate.exe\"", false, false, true)]
    [InlineData(false, "\"C:\\gone\\PrimeDictate.exe\" --background", false, false, true)]
    [InlineData(true, "\"C:\\gone\\PrimeDictate.exe\"", false, true, false)]
    [InlineData(false, "\"C:\\here\\PrimeDictate.exe\"", false, true, false)]
    [InlineData(false, "\"C:\\here\\PrimeDictate.exe\" --background", false, false, false)]
    [InlineData(false, "\"C:\\here\\PrimeDictate.exe\"", true, false, true)]
    public void Migration_plan_table(bool legacyShortcut, string? run, bool installerShortcut, bool expectWrite, bool expectDeleteRun)
    {
        var plan = WindowsLoginDecision.PlanMigration(legacyShortcut, run, installerShortcut, currentExeUsable: true, path => path.Contains(@"\here\"));
        Assert.Equal(legacyShortcut, plan.DeleteLegacyShortcut);
        Assert.Equal(expectWrite, plan.WriteRunValue);
        Assert.Equal(expectDeleteRun, plan.DeleteRunValue);
    }

    [Fact]
    public void Run_command_parsing_handles_quoted_and_bare_paths()
    {
        Assert.Equal(@"C:\Program Files\X\PrimeDictate.exe", WindowsLoginDecision.ExecutableOf("\"C:\\Program Files\\X\\PrimeDictate.exe\" --background"));
        Assert.Equal(@"C:\X\PrimeDictate.exe", WindowsLoginDecision.ExecutableOf(@"C:\X\PrimeDictate.exe --background"));
        Assert.Null(WindowsLoginDecision.ExecutableOf("  "));
    }

    [Fact]
    public void Enable_then_disable_round_trips_on_the_file_based_platforms()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var login = new LaunchAtLogin(this.home, executable: () => "/opt/Prime Dictate/PrimeDictate.Desktop");
        Assert.False(login.IsEnabled);
        Assert.Null(login.Apply(true));
        Assert.True(login.IsEnabled);
        var text = File.ReadAllText(login.EntryPath!);
        Assert.Contains("--background", text);
        Assert.Contains("/opt/Prime Dictate/PrimeDictate.Desktop", text);
        Assert.Null(login.Apply(false));
        Assert.False(login.IsEnabled);
        Assert.Null(login.Apply(false));
    }

    [Fact]
    public void Windows_branch_writes_a_quoted_background_command_when_the_installer_shortcut_is_absent()
    {
        var key = new FakeRunKey();
        var login = new LaunchAtLogin(this.home, executable: () => @"C:\Program Files\PrimeDictate\PrimeDictate.exe", windowsRunKey: key, windowsStartupFolder: new FakeStartup());
        Assert.Null(login.Apply(true));
        Assert.Equal("\"C:\\Program Files\\PrimeDictate\\PrimeDictate.exe\" --background", key.Values["PrimeDictate"]);
        Assert.True(login.IsEnabled);
        login.Apply(false);
        Assert.False(login.IsEnabled);
    }

    [Fact]
    public void Running_through_the_dotnet_host_is_refused_with_a_reason()
    {
        var login = new LaunchAtLogin(this.home, executable: () => "/usr/bin/dotnet", windowsRunKey: new FakeRunKey(), windowsStartupFolder: new FakeStartup());
        Assert.Contains("dotnet host", login.Apply(true));
        Assert.False(login.IsEnabled);
    }

    [Fact]
    public void Entries_are_escaped()
    {
        Assert.Equal("\"/a b/$x%\"".Replace("$x%", "\\$x%%"), LaunchAtLogin.QuoteDesktopExec("/a b/$x%"));
        Assert.Contains("&amp;", LaunchAtLogin.MacPlist("/Apps/A&B/PrimeDictate"));
    }

    [Fact]
    public void Xdg_config_home_is_respected()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return;
        }

        var login = new LaunchAtLogin(this.home, xdgConfigOverride: Path.Combine(this.home, "cfg"), executable: () => "/x/pd");
        Assert.Equal(Path.Combine(this.home, "cfg", "autostart", "primedictate.desktop"), login.EntryPath);
    }
}
