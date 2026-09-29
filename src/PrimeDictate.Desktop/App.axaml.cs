using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using PrimeDictate.Desktop.Dictation;

namespace PrimeDictate.Desktop;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var launch = desktop.Args ?? [];
            if (launch.SkipWhile(a => a != "--render-tray-icons").Skip(1).FirstOrDefault() is { } iconDir)
            {
                RenderTrayIcons(iconDir);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.Shutdown());
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var window = new MainWindow();

            // The window is the default start, so --show and --workspace (the WPF app's flags) are accepted and mean the same.
            // --background starts in the tray only, where the tray exists (Windows, macOS).
            var background = launch.Contains("--background") && !launch.Contains("--show") && !launch.Contains("--workspace") && CanHideToTray;
            if (!background)
            {
                desktop.MainWindow = window;
            }

            if (!IsSmokeRun(desktop.Args))
            {
                this.StartDictation(desktop, window);
            }

            if (desktop.Args is { Length: > 0 } args && args.Contains("--smoke-screenshot"))
            {
                var path = args.SkipWhile(a => a != "--smoke-screenshot").Skip(1).FirstOrDefault() ?? "smoke.png";
                window.Opened += async (_, _) =>
                {
                    await Task.Delay(1500);
                    window.SelectFirstSession();
                    await Task.Delay(1000);
                    await window.SaveScreenshotAsync(path);
                    desktop.Shutdown();
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static bool CanHideToTray => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    private static void RenderTrayIcons(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var state in Enum.GetValues<TrayVisualState>())
        {
            using var bitmap = TrayIconRenderer.Render(state);
            bitmap.Save(Path.Combine(directory, $"tray-{state}.png"), new PngBitmapEncoderOptions());
        }
    }

    private static bool IsSmokeRun(string[]? args) => args is { Length: > 0 } && args.Contains("--smoke-screenshot");

    private void StartDictation(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        var shell = new DictationShell(desktop, window.Workspace, () => ShowWorkspace(window));
        shell.Start(this);
        desktop.Exit += (_, _) => shell.DisposeAsync().AsTask().GetAwaiter().GetResult();

        // Dictation lives in the tray, so on Windows and macOS closing the window hides it. Linux desktops
        // without a tray host would leave no way back, so there closing quits.
        if (CanHideToTray)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Closing += (_, e) =>
            {
                if (e.CloseReason == WindowCloseReason.WindowClosing)
                {
                    e.Cancel = true;
                    window.Hide();
                }
            };
        }
    }

    private static void ShowWorkspace(MainWindow window)
    {
        window.Show();
        window.Activate();
    }
}
