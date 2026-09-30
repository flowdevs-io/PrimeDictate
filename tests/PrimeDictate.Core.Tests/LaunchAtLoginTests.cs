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
    public void Windows_branch_writes_a_quoted_command_under_its_own_value_name()
    {
        var key = new FakeRunKey();
        var login = new LaunchAtLogin(this.home, executable: () => @"C:\Program Files\PrimeDictate\PrimeDictate.Desktop.exe", windowsRunKey: key);
        Assert.Null(login.Apply(true));
        Assert.Equal("\"C:\\Program Files\\PrimeDictate\\PrimeDictate.Desktop.exe\" --background", key.Values["PrimeDictate.Desktop"]);
        Assert.DoesNotContain("PrimeDictate", key.Values.Keys.Where(k => k == "PrimeDictate"));
        Assert.True(login.IsEnabled);
        login.Apply(false);
        Assert.False(login.IsEnabled);
    }

    [Fact]
    public void Running_through_the_dotnet_host_is_refused_with_a_reason()
    {
        var login = new LaunchAtLogin(this.home, executable: () => "/usr/bin/dotnet", windowsRunKey: new FakeRunKey());
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
