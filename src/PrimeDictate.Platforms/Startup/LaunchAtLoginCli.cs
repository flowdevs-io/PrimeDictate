namespace PrimeDictate.Platforms.Startup;

/// <summary>What a headless launch-at-login command line asks for.</summary>
public sealed record LaunchAtLoginRequest(bool Enable, bool Disable, LoginScope? Scope);

/// <summary>
/// The WPF app's headless launch-at-login switches, so scripts (and the elevated helper) keep working: <c>--enable-launch-at-login</c>,
/// <c>--launch-at-login</c>, <c>/EnableLaunchAtLogin</c>; <c>--disable-launch-at-login</c>, <c>--no-launch-at-login</c>, <c>/DisableLaunchAtLogin</c>;
/// and the scope flags <c>--current-user</c>, <c>--scope=user</c>, <c>--scope=current-user</c>, <c>/CurrentUser</c>,
/// <c>--all-users</c>, <c>--scope=all-users</c>, <c>--scope=machine</c>, <c>/AllUsers</c>. The exit codes are the WPF app's:
/// 0 done, 1 failed, 2 conflicting switches. These commands never start the app.
/// </summary>
public static class LaunchAtLoginCli
{
    private static readonly string[] EnableSwitches = ["--enable-launch-at-login", "--launch-at-login", "/EnableLaunchAtLogin"];
    private static readonly string[] DisableSwitches = ["--disable-launch-at-login", "--no-launch-at-login", "/DisableLaunchAtLogin"];
    private static readonly string[] CurrentUserSwitches = ["--current-user", "--scope=current-user", "--scope=user", "/CurrentUser"];
    private static readonly string[] AllUsersSwitches = ["--all-users", "--scope=all-users", "--scope=machine", "/AllUsers"];

    /// <summary>Null when the arguments are not a launch-at-login command.</summary>
    public static LaunchAtLoginRequest? Parse(string[] args)
    {
        var enable = Has(args, EnableSwitches);
        var disable = Has(args, DisableSwitches);
        if (!enable && !disable)
        {
            return null;
        }

        // All users wins over current user if both are given, as the WPF check order did.
        LoginScope? scope = Has(args, AllUsersSwitches) ? LoginScope.AllUsers : Has(args, CurrentUserSwitches) ? LoginScope.CurrentUser : null;
        return new LaunchAtLoginRequest(enable, disable, scope);
    }

    /// <summary>Runs the command. Returns true when the arguments were one (the app must then exit with <paramref name="exitCode"/>).</summary>
    public static bool TryHandle(string[] args, LaunchAtLogin login, bool isAdministrator, out int exitCode)
    {
        exitCode = 0;
        var request = Parse(args);
        if (request is null)
        {
            return false;
        }

        if (request.Enable && request.Disable)
        {
            exitCode = 2;
            return true;
        }

        LoginScope wanted;
        if (request.Enable)
        {
            // Without a scope: all users when already elevated, else the current user (the WPF default).
            wanted = request.Scope ?? (isAdministrator && login.SupportsAllUsers ? LoginScope.AllUsers : LoginScope.CurrentUser);
        }
        else
        {
            // Disabling the current user leaves an all-users entry alone; disabling all users, or giving no scope, turns everything off.
            wanted = LoginScope.Off;
        }

        // Disabling all users first removes the machine entry and the MSI's all-users shortcut (elevating when needed); if that fails nothing else changes.
        var problem = request.Disable && request.Scope == LoginScope.AllUsers ? login.DisableAllUsers() : null;
        if (problem is null)
        {
            problem = request.Disable && request.Scope == LoginScope.CurrentUser ? login.Apply(false) : login.Apply(wanted);
        }

        exitCode = problem is null ? 0 : 1;
        return true;
    }

    private static bool Has(IEnumerable<string> args, string[] switches) =>
        args.Any(a => switches.Any(s => string.Equals(a, s, StringComparison.OrdinalIgnoreCase)));
}
