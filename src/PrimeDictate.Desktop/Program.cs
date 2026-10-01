using Avalonia;
using PrimeDictate.Platforms.Startup;

namespace PrimeDictate.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // A crash must leave a trace: the app is a GUI, so nothing else would show why it disappeared.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => LogCrash("unobserved task", e.Exception);

        // The elevated helper the all-users launch-at-login choice starts to change one machine-wide registry value; it never starts the app.
        if (LaunchAtLogin.TryHandleMachineCommand(args, out var machineExit))
        {
            return machineExit;
        }

        // The WPF app's headless switches (--enable-launch-at-login, --disable-launch-at-login and the scope flags) still work and never start the app.
        var scriptedLogin = new LaunchAtLogin();
        if (LaunchAtLoginCli.TryHandle(args, scriptedLogin, WindowsElevationCheck(), out var loginExit))
        {
            return loginExit;
        }

        // --quit asks the running instance to shut down (stopping recordings and workers) and waits for it; it never starts the app.
        // A second normal launch brings the running instance forward instead of starting a second copy (two copies would both
        // register the global hotkey). The screenshot and icon-rendering runs are one-shot tools and skip this.
        if (!args.Contains("--smoke-screenshot") && !args.Contains("--render-tray-icons"))
        {
            var instance = new SingleInstance();
            if (args.Contains("--quit"))
            {
                if (!instance.SendAsync(SingleInstance.QuitCommand, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult())
                {
                    return 0; // Nothing was running.
                }

                return instance.WaitForPrimaryToExit(TimeSpan.FromSeconds(45)) ? 0 : 1;
            }

            if (!instance.TryBecomePrimary())
            {
                if (!App.IsBackgroundLaunch(args))
                {
                    instance.SendAsync(SingleInstance.ShowCommand, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                }

                return 0;
            }

            App.Instance = instance;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static bool WindowsElevationCheck() =>
        OperatingSystem.IsWindows() && new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    private static void LogCrash(string kind, Exception? ex)
    {
        try
        {
            var directory = Path.Combine(PrimeDictate.Core.Storage.AppDataPaths.Default.Root, "logs");
            PrimeDictate.Core.Storage.AppDataPaths.EnsurePrivateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "desktop-crash.log"), $"{DateTimeOffset.Now:O} {kind}: {ex}{Environment.NewLine}");
        }
        catch (Exception logFailure) when (logFailure is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done from a crash handler.
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
