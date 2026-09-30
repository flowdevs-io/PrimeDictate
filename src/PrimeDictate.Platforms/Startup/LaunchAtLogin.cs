using System.Security;
using System.Text;

namespace PrimeDictate.Platforms.Startup;

/// <summary>
/// Starts PrimeDictate (tray only, <c>--background</c>) when the current user signs in. Current user only: no elevation and no
/// machine-wide entry. Windows uses the per-user Run key under its own value name, macOS a LaunchAgent, Linux an XDG autostart file.
/// The WPF app's startup shortcut is left alone, so a person running both would get both; turn one off.
/// </summary>
public sealed class LaunchAtLogin
{
    public const string WindowsValueName = "PrimeDictate.Desktop";
    public const string MacLabel = "io.flowdevs.primedictate.desktop";
    public const string BackgroundArgument = "--background";

    private readonly string home;
    private readonly string? xdgConfig;
    private readonly Func<string?> executablePath;
    private readonly IWindowsRunKey? runKey;

    public LaunchAtLogin(string? homeOverride = null, string? xdgConfigOverride = null, Func<string?>? executable = null, IWindowsRunKey? windowsRunKey = null)
    {
        this.home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        this.xdgConfig = xdgConfigOverride ?? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        this.executablePath = executable ?? (() => Environment.ProcessPath);
        this.runKey = windowsRunKey ?? (OperatingSystem.IsWindows() ? new RegistryRunKey() : null);
    }

    /// <summary>Which OS mechanism is used, for the settings text.</summary>
    public string Mechanism => OperatingSystem.IsWindows() ? "the Windows Run list" : OperatingSystem.IsMacOS() ? "a LaunchAgent" : "an autostart entry";

    public string? EntryPath =>
        OperatingSystem.IsMacOS() ? Path.Combine(this.home, "Library", "LaunchAgents", MacLabel + ".plist")
        : OperatingSystem.IsWindows() ? null
        : Path.Combine(string.IsNullOrWhiteSpace(this.xdgConfig) ? Path.Combine(this.home, ".config") : this.xdgConfig, "autostart", "primedictate.desktop");

    public bool IsEnabled => this.runKey is not null
        ? this.runKey.Get(WindowsValueName) is { Length: > 0 }
        : this.EntryPath is { } path && File.Exists(path);

    /// <summary>Returns null on success, else a sentence for the user.</summary>
    public string? Apply(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                this.Remove();
                return null;
            }

            var exe = this.executablePath();
            if (string.IsNullOrWhiteSpace(exe) || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return "PrimeDictate is running through the dotnet host, so there is no program to start at login. Install or publish the app and turn this on there.";
            }

            this.Write(exe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return $"Could not change launch at login: {ex.Message}";
        }
    }

    private void Remove()
    {
        if (this.runKey is not null)
        {
            this.runKey.Delete(WindowsValueName);
        }
        else if (this.EntryPath is { } path && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void Write(string exe)
    {
        if (this.runKey is not null)
        {
            this.runKey.Set(WindowsValueName, $"\"{exe}\" {BackgroundArgument}");
            return;
        }

        var path = this.EntryPath!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, OperatingSystem.IsMacOS() ? MacPlist(exe) : DesktopEntry(exe), new UTF8Encoding(false));
    }

    public static string DesktopEntry(string exe) =>
        "[Desktop Entry]\nType=Application\nName=PrimeDictate\nComment=Local dictation and transcription\n" +
        $"Exec={QuoteDesktopExec(exe)} {BackgroundArgument}\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";

    /// <summary>The Exec key needs double quotes around spaces, with \, ", ` and $ escaped, and % doubled.</summary>
    public static string QuoteDesktopExec(string path)
    {
        var escaped = path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%");
        return "\"" + escaped + "\"";
    }

    public static string MacPlist(string exe) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
        $"<plist version=\"1.0\">\n<dict>\n  <key>Label</key>\n  <string>{MacLabel}</string>\n  <key>ProgramArguments</key>\n  <array>\n" +
        $"    <string>{SecurityElement.Escape(exe)}</string>\n    <string>{BackgroundArgument}</string>\n  </array>\n  <key>RunAtLoad</key>\n  <true/>\n</dict>\n</plist>\n";
}

/// <summary>Seam over HKCU\Software\Microsoft\Windows\CurrentVersion\Run so the Windows branch can be tested without Windows.</summary>
public interface IWindowsRunKey
{
    string? Get(string name);

    void Set(string name, string value);

    void Delete(string name);
}

internal sealed class RegistryRunKey : IWindowsRunKey
{
    private const string Path = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Get(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Path, writable: false);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string value)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Path, writable: true);
        key.SetValue(name, value);
    }

    public void Delete(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
