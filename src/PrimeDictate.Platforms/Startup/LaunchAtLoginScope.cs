using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace PrimeDictate.Platforms.Startup;

/// <summary>Who PrimeDictate starts for at sign-in. The WPF app's CurrentUser and AllUsers scopes; Off is its Disabled.</summary>
public enum LoginScope
{
    Off,
    CurrentUser,
    AllUsers
}

/// <summary>What to do to the machine-wide Run value.</summary>
public enum MachineRunAction
{
    None,
    Write,
    Delete
}

/// <summary>The rules for the all-users scope, free of the registry so they can be tested.</summary>
public static class LoginScopeDecision
{
    public const string MachineOnArgument = "--launch-at-login-machine=on";
    public const string MachineOffArgument = "--launch-at-login-machine=off";

    /// <summary>All users when the machine-wide Run value is there (it starts for everyone); else this user's own setting.</summary>
    public static LoginScope ScopeOf(bool machineRunPresent, bool userEnabled) =>
        machineRunPresent ? LoginScope.AllUsers : userEnabled ? LoginScope.CurrentUser : LoginScope.Off;

    /// <summary>Only the all-users scope keeps the machine-wide value; every other scope removes it, so Off really is off.</summary>
    public static MachineRunAction PlanMachine(LoginScope wanted, bool machineRunPresent) =>
        wanted == LoginScope.AllUsers
            ? (machineRunPresent ? MachineRunAction.None : MachineRunAction.Write)
            : (machineRunPresent ? MachineRunAction.Delete : MachineRunAction.None);

    /// <summary>The per-user part: this user's own mechanism is on only for CurrentUser (for AllUsers the machine value starts it, so a second start is not wanted).</summary>
    public static bool UserMechanismWanted(LoginScope wanted) => wanted == LoginScope.CurrentUser;
}

/// <summary>Seam over <c>HKLM\Software\Microsoft\Windows\CurrentVersion\Run</c>, which only an administrator can change.</summary>
public interface IWindowsMachineRunKey
{
    string? Get(string name);

    void Set(string name, string value);

    void Delete(string name);
}

internal sealed class RegistryMachineRunKey : IWindowsMachineRunKey
{
    private const string Path = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Get(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Path, writable: false);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string value)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(Path, writable: true);
        key.SetValue(name, value);
    }

    public void Delete(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

internal static class WindowsElevation
{
    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

public sealed partial class LaunchAtLogin
{
    /// <summary>True where the all-users scope exists (Windows).</summary>
    public bool SupportsAllUsers => this.machineRunKey is not null && this.runKey is not null;

    /// <summary>The scope that is in effect now.</summary>
    public LoginScope Scope => LoginScopeDecision.ScopeOf(this.MachineRunPresent, this.IsEnabled);

    private bool MachineRunPresent
    {
        get
        {
            try
            {
                return this.machineRunKey?.Get(WindowsValueName) is { Length: > 0 };
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Sets the scope. The all-users part is the machine-wide Run value; changing it needs an administrator, so without one the
    /// app starts itself again elevated (the Windows prompt, as the WPF app did) to make just that change. Returns null on success, else a sentence for the user.
    /// </summary>
    public string? Apply(LoginScope scope)
    {
        if (this.SupportsAllUsers)
        {
            var action = LoginScopeDecision.PlanMachine(scope, this.MachineRunPresent);
            if (action != MachineRunAction.None)
            {
                var problem = this.ApplyMachine(action);
                if (problem is not null)
                {
                    return problem;
                }
            }
        }
        else if (scope == LoginScope.AllUsers)
        {
            return "Starting for every user is only available on Windows.";
        }

        return this.Apply(LoginScopeDecision.UserMechanismWanted(scope));
    }

    /// <summary>
    /// The WPF <c>--disable-launch-at-login --scope=all-users</c>: removes the machine-wide Run value and the MSI's all-users Startup
    /// shortcut (elevating when needed), even when only the shortcut is there. Returns null on success, else a sentence for the user.
    /// </summary>
    public string? DisableAllUsers()
    {
        if (this.SupportsAllUsers && (this.MachineRunPresent || this.InstallerShortcutPresent))
        {
            return this.ApplyMachine(MachineRunAction.Delete);
        }

        return null;
    }

    private string? ApplyMachine(MachineRunAction action)
    {
        if (this.isAdministrator())
        {
            return this.WriteMachine(action == MachineRunAction.Write);
        }

        return this.elevate(action == MachineRunAction.Write ? LoginScopeDecision.MachineOnArgument : LoginScopeDecision.MachineOffArgument);
    }

    private string? WriteMachine(bool on)
    {
        try
        {
            if (!on)
            {
                this.machineRunKey!.Delete(WindowsValueName);
                // The WPF app's all-users off also deleted the MSI's shortcut in the all-users Startup folder.
                this.startupFolder?.DeleteInstallerShortcut();
                return null;
            }

            var exe = this.executablePath();
            if (string.IsNullOrWhiteSpace(exe) || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return "PrimeDictate is running through the dotnet host, so there is no program to start at login. Install or publish the app and turn this on there.";
            }

            this.machineRunKey!.Set(WindowsValueName, WindowsLoginDecision.RunCommand(exe));
            return null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return $"Could not change launch at login for all users: {ex.Message}";
        }
    }

    /// <summary>
    /// Handles the elevated helper's command line (<see cref="LoginScopeDecision.MachineOnArgument"/> or <see cref="LoginScopeDecision.MachineOffArgument"/>).
    /// Returns true when the arguments were one of them, with the exit code to end with; the app must not start.
    /// </summary>
    public static bool TryHandleMachineCommand(string[] args, out int exitCode)
    {
        exitCode = 0;
        var on = args.Contains(LoginScopeDecision.MachineOnArgument, StringComparer.OrdinalIgnoreCase);
        var off = args.Contains(LoginScopeDecision.MachineOffArgument, StringComparer.OrdinalIgnoreCase);
        if (on == off)
        {
            return false;
        }

        var login = new LaunchAtLogin();
        exitCode = login.SupportsAllUsers && login.WriteMachine(on) is null ? 0 : 1;
        return true;
    }

    private string? RunElevatedHelper(string argument)
    {
        var exe = this.executablePath();
        if (string.IsNullOrWhiteSpace(exe) || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return "PrimeDictate is running through the dotnet host, so it cannot ask for administrator permission. Install or publish the app and try there.";
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = argument,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
            });
            if (process is null)
            {
                return "Could not ask for administrator permission.";
            }

            process.WaitForExit();
            return process.ExitCode == 0 ? null : $"The change for all users failed (exit code {process.ExitCode}).";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Administrator approval is required to change startup for all users.";
        }
        catch (Win32Exception ex)
        {
            return $"Could not ask for administrator permission: {ex.Message}";
        }
    }
}
