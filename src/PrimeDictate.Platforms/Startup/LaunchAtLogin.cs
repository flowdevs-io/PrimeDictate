using System.Security;
using System.Text;

namespace PrimeDictate.Platforms.Startup;

/// <summary>
/// Starts PrimeDictate (tray only, <c>--background</c>) when the current user signs in. No elevation is ever needed.
/// On Windows there is one mechanism with two homes, never both at once: the MSI's all-users Startup shortcut
/// (<c>PrimeDictate.lnk</c> running <c>--background</c>), which this class turns on or off for the current user through the
/// Explorer "StartupApproved" switch (the one Task Manager's Startup tab uses), and, when the installer shortcut is absent
/// (installed with LAUNCHATLOGIN=0, or a portable copy), a per-user Run value named <c>PrimeDictate</c>. macOS uses a LaunchAgent,
/// Linux an XDG autostart file. <see cref="WindowsLoginDecision"/> holds the rules; <see cref="MigrateLegacyEntries"/> cleans up
/// what the 6.0.0 WPF app created.
/// </summary>
public sealed partial class LaunchAtLogin
{
    public const string WindowsValueName = "PrimeDictate";
    public const string MacLabel = "io.flowdevs.primedictate.desktop";
    public const string BackgroundArgument = "--background";

    private readonly string home;
    private readonly string? xdgConfig;
    private readonly Func<string?> executablePath;
    private readonly IWindowsRunKey? runKey;
    private readonly IWindowsStartupFolder? startupFolder;
    private readonly IWindowsMachineRunKey? machineRunKey;
    private readonly Func<bool> isAdministrator;
    private readonly Func<string, string?> elevate;

    public LaunchAtLogin(
        string? homeOverride = null,
        string? xdgConfigOverride = null,
        Func<string?>? executable = null,
        IWindowsRunKey? windowsRunKey = null,
        IWindowsStartupFolder? windowsStartupFolder = null,
        IWindowsMachineRunKey? windowsMachineRunKey = null,
        Func<bool>? isAdministrator = null,
        Func<string, string?>? elevate = null)
    {
        this.machineRunKey = windowsMachineRunKey ?? (OperatingSystem.IsWindows() ? new RegistryMachineRunKey() : null);
        this.isAdministrator = isAdministrator ?? WindowsElevation.IsAdministrator;
        this.elevate = elevate ?? this.RunElevatedHelper;
        this.home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        this.xdgConfig = xdgConfigOverride ?? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        this.executablePath = executable ?? (() => Environment.ProcessPath);
        this.runKey = windowsRunKey ?? (OperatingSystem.IsWindows() ? new RegistryRunKey() : null);
        this.startupFolder = windowsStartupFolder ?? (OperatingSystem.IsWindows() ? new RegistryStartupFolder() : null);
    }

    /// <summary>True when the MSI's Startup shortcut exists. The updater passes this on as LAUNCHATLOGIN so a reinstall keeps the choice.</summary>
    public bool InstallerShortcutPresent => this.startupFolder?.InstallerShortcutExists == true;

    /// <summary>Which OS mechanism is used, for the settings text.</summary>
    public string Mechanism => OperatingSystem.IsWindows() ? "the Windows startup list" : OperatingSystem.IsMacOS() ? "a LaunchAgent" : "an autostart entry";

    public string? EntryPath =>
        OperatingSystem.IsMacOS() ? Path.Combine(this.home, "Library", "LaunchAgents", MacLabel + ".plist")
        : OperatingSystem.IsWindows() ? null
        : Path.Combine(string.IsNullOrWhiteSpace(this.xdgConfig) ? Path.Combine(this.home, ".config") : this.xdgConfig, "autostart", "primedictate.desktop");

    public bool IsEnabled => this.runKey is not null
        ? WindowsLoginDecision.IsEnabled(this.WindowsState())
        : this.EntryPath is { } path && File.Exists(path);

    private WindowsLoginState WindowsState() => new(
        this.startupFolder?.InstallerShortcutExists == true,
        this.startupFolder?.InstallerShortcutDisabledForUser == true,
        this.runKey?.Get(WindowsValueName) is { Length: > 0 });

    /// <summary>
    /// Run once at startup on Windows. The 6.0.0 WPF app made a per-user Startup shortcut (<c>--from-login</c>) and, in older
    /// builds, a Run value with the same name; both would start this app (same exe name) but outside the one mechanism, so a
    /// user who had turned launch at login on keeps it (moved to the Run value when the MSI shortcut is absent) and the old
    /// entries go. A Run value pointing at a program that no longer exists is removed.
    /// </summary>
    public void MigrateLegacyEntries()
    {
        if (this.runKey is null)
        {
            return;
        }

        try
        {
            var exe = this.executablePath();
            var usable = !string.IsNullOrWhiteSpace(exe) && !Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
            var plan = WindowsLoginDecision.PlanMigration(
                this.startupFolder?.LegacyUserShortcutExists == true,
                this.runKey.Get(WindowsValueName),
                this.InstallerShortcutPresent,
                usable,
                File.Exists);
            if (plan.DeleteLegacyShortcut)
            {
                this.startupFolder!.DeleteLegacyUserShortcut();
            }

            if (plan.DeleteRunValue)
            {
                this.runKey.Delete(WindowsValueName);
            }

            if (plan.WriteRunValue)
            {
                this.runKey.Set(WindowsValueName, WindowsLoginDecision.RunCommand(exe!));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Cleanup is best effort; the worst case is an entry the user can still switch off in the settings.
        }
    }

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
            this.ApplyWindowsPlan(WindowsLoginDecision.Plan(this.WindowsState(), enable: false), null);
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
            this.ApplyWindowsPlan(WindowsLoginDecision.Plan(this.WindowsState(), enable: true), exe);
            return;
        }

        var path = this.EntryPath!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, OperatingSystem.IsMacOS() ? MacPlist(exe) : DesktopEntry(exe), new UTF8Encoding(false));
    }

    private void ApplyWindowsPlan(WindowsLoginPlan plan, string? exe)
    {
        if (plan.DeleteRunValue)
        {
            this.runKey!.Delete(WindowsValueName);
        }

        if (plan.WriteRunValue && exe is not null)
        {
            this.runKey!.Set(WindowsValueName, WindowsLoginDecision.RunCommand(exe));
        }

        if (plan.Shortcut != ShortcutApproval.Leave && this.startupFolder is not null)
        {
            this.startupFolder.SetInstallerShortcutEnabledForUser(plan.Shortcut == ShortcutApproval.Enable);
        }

        if (this.startupFolder?.LegacyUserShortcutExists == true)
        {
            this.startupFolder.DeleteLegacyUserShortcut();
        }
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

/// <summary>What the Windows startup mechanisms currently say.</summary>
public readonly record struct WindowsLoginState(bool InstallerShortcut, bool InstallerShortcutDisabledForUser, bool RunValuePresent);

public enum ShortcutApproval
{
    Leave,
    Enable,
    Disable
}

public sealed record WindowsLoginPlan(bool WriteRunValue, bool DeleteRunValue, ShortcutApproval Shortcut);

public sealed record WindowsMigrationPlan(bool DeleteLegacyShortcut, bool DeleteRunValue, bool WriteRunValue);

/// <summary>The rules for Windows launch at login, free of the registry so they can be tested.</summary>
public static class WindowsLoginDecision
{
    /// <summary>On when the installer shortcut is present and not switched off for this user, or the Run value exists.</summary>
    public static bool IsEnabled(WindowsLoginState state) =>
        (state.InstallerShortcut && !state.InstallerShortcutDisabledForUser) || state.RunValuePresent;

    /// <summary>
    /// With the installer shortcut present it is the only mechanism (the Run value is removed so the app never starts twice);
    /// without it the Run value is. Turning it off clears both.
    /// </summary>
    public static WindowsLoginPlan Plan(WindowsLoginState state, bool enable)
    {
        if (enable)
        {
            return state.InstallerShortcut
                ? new WindowsLoginPlan(WriteRunValue: false, DeleteRunValue: state.RunValuePresent, ShortcutApproval.Enable)
                : new WindowsLoginPlan(WriteRunValue: true, DeleteRunValue: false, ShortcutApproval.Leave);
        }

        return new WindowsLoginPlan(WriteRunValue: false, DeleteRunValue: state.RunValuePresent, state.InstallerShortcut ? ShortcutApproval.Disable : ShortcutApproval.Leave);
    }

    public static string RunCommand(string exe) => $"\"{exe}\" {LaunchAtLogin.BackgroundArgument}";

    /// <summary>
    /// Plans the one-time cleanup of the 6.0.0 entries. The per-user shortcut is always removed. With the installer shortcut present a
    /// leftover Run value is removed too (it would start a second copy). Without it, a user who had the shortcut keeps launch at login
    /// through the Run value; a Run value naming a missing program is deleted; one naming a present program without
    /// <c>--background</c> is rewritten to this app, tray only.
    /// </summary>
    public static WindowsMigrationPlan PlanMigration(bool legacyShortcut, string? runValue, bool installerShortcut, bool currentExeUsable, Func<string, bool> fileExists)
    {
        var hasRun = !string.IsNullOrWhiteSpace(runValue);
        if (installerShortcut)
        {
            return new WindowsMigrationPlan(legacyShortcut, DeleteRunValue: hasRun, WriteRunValue: false);
        }

        var target = hasRun ? ExecutableOf(runValue!) : null;
        var stale = hasRun && (target is null || !fileExists(target));
        var wantRun = legacyShortcut || (hasRun && !stale);
        var write = wantRun && currentExeUsable && (!hasRun || stale || !runValue!.Contains(LaunchAtLogin.BackgroundArgument, StringComparison.OrdinalIgnoreCase));
        return new WindowsMigrationPlan(legacyShortcut, DeleteRunValue: hasRun && stale && !write, WriteRunValue: write);
    }

    /// <summary>The program path of a Run command such as <c>"C:\Program Files\PrimeDictate\PrimeDictate.exe" --background</c>.</summary>
    public static string? ExecutableOf(string command)
    {
        var text = command.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }
}

/// <summary>Seam over the Startup folder shortcuts and their per-user "StartupApproved" switch, so the Windows branch is testable anywhere.</summary>
public interface IWindowsStartupFolder
{
    /// <summary>The all-users <c>PrimeDictate.lnk</c> the MSI installs.</summary>
    bool InstallerShortcutExists { get; }

    /// <summary>The per-user <c>PrimeDictate.lnk</c> the 6.0.0 WPF app created.</summary>
    bool LegacyUserShortcutExists { get; }

    void DeleteLegacyUserShortcut();

    bool InstallerShortcutDisabledForUser { get; }

    void SetInstallerShortcutEnabledForUser(bool enabled);

    /// <summary>
    /// Deletes the all-users <c>PrimeDictate.lnk</c> (needs administrator rights). Turning launch at login off for all users removes it, as the
    /// WPF app did, so the MSI's shortcut does not keep starting the app for everyone.
    /// </summary>
    void DeleteInstallerShortcut()
    {
    }
}

internal sealed class RegistryStartupFolder : IWindowsStartupFolder
{
    private const string ShortcutName = "PrimeDictate.lnk";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    public bool InstallerShortcutExists => OperatingSystem.IsWindows() && File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), ShortcutName));

    public bool LegacyUserShortcutExists => OperatingSystem.IsWindows() && File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName));

    public void DeleteLegacyUserShortcut()
    {
        if (OperatingSystem.IsWindows())
        {
            File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName));
        }
    }

    public void DeleteInstallerShortcut()
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), ShortcutName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public bool InstallerShortcutDisabledForUser
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ApprovedPath, writable: false);
            // Explorer writes 02 (or 06) for enabled and 03 for disabled in the first byte.
            return key?.GetValue(ShortcutName) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
        }
    }

    public void SetInstallerShortcutEnabledForUser(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (enabled)
        {
            using var existing = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ApprovedPath, writable: true);
            existing?.DeleteValue(ShortcutName, throwOnMissingValue: false);
            return;
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ApprovedPath, writable: true);
        var data = new byte[12];
        data[0] = 3;
        BitConverter.TryWriteBytes(data.AsSpan(4), DateTime.UtcNow.ToFileTimeUtc());
        key.SetValue(ShortcutName, data, Microsoft.Win32.RegistryValueKind.Binary);
    }
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
