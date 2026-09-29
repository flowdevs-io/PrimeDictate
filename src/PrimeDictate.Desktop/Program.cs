using Avalonia;

namespace PrimeDictate.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // A crash must leave a trace: the app is a GUI, so nothing else would show why it disappeared.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => LogCrash("unobserved task", e.Exception);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

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
