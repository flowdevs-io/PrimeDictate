using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace PrimeDictate.Desktop;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            if (desktop.Args is { Length: > 0 } args && args.Contains("--smoke-screenshot"))
            {
                var path = args.SkipWhile(a => a != "--smoke-screenshot").Skip(1).FirstOrDefault() ?? "smoke.png";
                window.Opened += async (_, _) =>
                {
                    await Task.Delay(1500);
                    await window.SaveScreenshotAsync(path);
                    desktop.Shutdown();
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
