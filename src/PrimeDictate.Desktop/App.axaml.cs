using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using PrimeDictate.Core.Dictation;
using PrimeDictate.Core.Storage;
using PrimeDictate.Desktop.Dictation;
using PrimeDictate.Desktop.Updates;
using PrimeDictate.Platforms.Speech;
using PrimeDictate.Platforms.Startup;
using PrimeDictate.Platforms.Updates;

namespace PrimeDictate.Desktop;

public sealed class App : Application
{
    /// <summary>The single-instance guard of this process, set by <c>Program</c>; null for one-shot runs.</summary>
    internal static SingleInstance? Instance { get; set; }

    private int exiting;
    private UpdateController? updates;

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

            // ONNX Runtime is one library per process: the GPU build must be in place before any speech model is created.
            if (!IsSmokeRun(desktop.Args))
            {
                var savedSettings = new DictationSettingsStore(AppDataPaths.Default).Load().Settings;
                // A GPU or NPU choice copied from another PC is judged against this one before the runtime is picked (in memory only).
                HardwareNormalization.Normalize(savedSettings, MachineSupport.Current, AppDataPaths.Default.ModelsDirectory);
                OnnxRuntimeDevice.Configure(OnnxRuntimeDevice.Effective(savedSettings.OnnxDevice));
                // Whisper.net picks its native build once too, before the first model loads.
                WhisperNetRuntime.Configure(WhisperNetRuntime.Effective(savedSettings));
            }

            var window = new MainWindow();

            // The window is the default start, so --show and --workspace (the WPF app's flags) are accepted and mean the same.
            // --background starts in the tray only, where the tray exists (Windows, macOS).
            var background = IsBackgroundLaunch(launch) && !launch.Contains("--show") && !launch.Contains("--workspace") && CanHideToTray;
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

    /// <summary>
    /// <c>--background</c> is what the installer's Startup shortcut and the Run value pass. <c>--from-login</c> is what the 6.0.0 shortcut
    /// passed; it means the same, so a shortcut left over from 6.0.0 still starts in the tray.
    /// </summary>
    internal static bool IsBackgroundLaunch(string[] args) => args.Contains("--background") || args.Contains("--from-login");

    /// <summary>Windows only: the tray's "Check for updates..." item, the automatic check on launch, and the clean-up of 6.0.0 startup entries.</summary>
    private void StartUpdates(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, DictationShell shell)
    {
        // A run with its own data folder (PRIMEDICTATE_DATA_DIR, used for tests) is an isolated profile: it must not touch the real startup entries or update record.
        if (!OperatingSystem.IsWindows() || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PRIMEDICTATE_DATA_DIR")))
        {
            return;
        }

        var launch = new LaunchAtLogin();
        launch.MigrateLegacyEntries();
        var root = AppDataPaths.Default.Root;
        this.updates = new UpdateController(
            GitHubUpdateService.CurrentApplicationVersion(typeof(App).Assembly),
            Path.Combine(root, "updates"),
            new UpdateCheckState(Path.Combine(root, "update-check.json"), Path.Combine(root, "settings.json")),
            launch,
            () => this.ExitAsync(desktop, window, shell),
            () => shell.Host.Settings.CheckForUpdatesAutomatically);
        shell.UpdateMenu = this.updates;
        _ = this.updates.CheckOnLaunchAsync();
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
        var shell = new DictationShell(desktop, window, () => ShowWorkspace(window));
        this.StartUpdates(desktop, window, shell);
        shell.Start(this);
        shell.ExitRequested += () => _ = this.ExitAsync(desktop, window, shell);
        desktop.Exit += (_, _) => this.Cleanup(window, shell);
        Instance?.StartListening(command =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (command == SingleInstance.QuitCommand)
                {
                    _ = this.ExitAsync(desktop, window, shell);
                }
                else
                {
                    ShowWorkspace(window);
                }
            });
            return Task.CompletedTask;
        });

        // Dictation lives in the tray, so on Windows and macOS closing the window hides it. Linux desktops
        // without a tray host would leave no way back, so there closing quits.
        if (CanHideToTray)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.HidesOnClose = true;
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

    /// <summary>
    /// The one clean way out (tray Exit, <c>--quit</c>): an active meeting recording is stopped the normal way and saved, an active
    /// dictation is discarded (it must never type into another app while the app is leaving), then workers are stopped and the app ends.
    /// </summary>
    private async Task ExitAsync(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, DictationShell shell)
    {
        if (Interlocked.Exchange(ref this.exiting, 1) == 1)
        {
            return;
        }

        // Each step gets its own guard: leaving must not be blocked by a failing step, and a failure while preparing
        // the shell must not skip finalizing the recording.
        try
        {
            await shell.PrepareExitAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError($"Exit preparation failed: {ex}");
        }

        try
        {
            await window.Workspace.StopLiveForExitAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError($"Finalizing the recording at exit failed: {ex}");
        }

        desktop.Shutdown();
    }

    private void Cleanup(MainWindow window, DictationShell shell)
    {
        shell.DisposeAsync().AsTask().GetAwaiter().GetResult();
        // A window that was never shown never raises Closed, so the workspace (and with it the speech worker) is released here.
        window.Workspace.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
        this.updates?.Dispose();
        Instance?.Dispose();
    }

    private static void ShowWorkspace(MainWindow window)
    {
        window.Show();
        window.Activate();
    }
}
