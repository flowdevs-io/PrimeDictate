using Avalonia.Threading;
using PrimeDictate.Platforms.Startup;
using PrimeDictate.Platforms.Updates;

namespace PrimeDictate.Desktop.Updates;

/// <summary>
/// What a tray (or any) menu needs from the updater: the item text and enabled state, a change event, and the command.
/// Kept small so whoever restructures the tray can re-home "Check for updates..." without touching the updater.
/// </summary>
public interface IUpdateMenu
{
    /// <summary>Text for the menu item: "Check for updates...", "Checking for updates...", "Downloading update... 40%".</summary>
    string MenuText { get; }

    bool MenuEnabled { get; }

    /// <summary>Raised on the UI thread when <see cref="MenuText"/> or <see cref="MenuEnabled"/> changed.</summary>
    event Action? MenuChanged;

    /// <summary>The user's "Check for updates" command: reports the result in a dialog, including "up to date".</summary>
    Task CheckNowAsync();
}

/// <summary>
/// Drives the update flow for the running app: an automatic check on launch (at most once a day, silent unless a newer release
/// exists), the manual check, asking before installing, and the handoff. The handoff leaves through <c>exit</c>, the app's clean exit
/// path, so a meeting recording is saved before Windows Installer replaces the files. Never logs anything but versions and file names.
/// </summary>
public sealed class UpdateController : IUpdateMenu, IDisposable
{
    private const string IdleText = "Check for updates...";
    private readonly GitHubUpdateService service;
    private readonly UpdateCheckState state;
    private readonly LaunchAtLogin launch;
    private readonly Func<Task> exit;
    private readonly Version current;
    private bool busy;

    public UpdateController(Version current, string downloadDirectory, UpdateCheckState state, LaunchAtLogin launch, Func<Task> exit)
    {
        this.current = current;
        this.state = state;
        this.launch = launch;
        this.exit = exit;
        this.service = new GitHubUpdateService(current, downloadDirectory, message => System.Diagnostics.Trace.TraceInformation(message));
    }

    public string MenuText { get; private set; } = IdleText;

    public bool MenuEnabled { get; private set; } = true;

    public event Action? MenuChanged;

    public void Dispose() => this.service.Dispose();

    /// <summary>Checks once after a short delay when the last check is over 24 hours old. A failure is only traced.</summary>
    public async Task CheckOnLaunchAsync()
    {
        if (!UpdateRules.IsCheckDue(this.state.LastCheckUtc(), DateTime.UtcNow))
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => this.RunAsync(interactive: false));
    }

    public Task CheckNowAsync() => this.RunAsync(interactive: true);

    private async Task RunAsync(bool interactive)
    {
        if (this.busy)
        {
            if (interactive)
            {
                await UpdateDialog.ShowAsync("Update in progress", "PrimeDictate is already checking for or installing an update.");
            }

            return;
        }

        this.busy = true;
        this.SetMenu("Checking for updates...", enabled: false);
        try
        {
            var update = await this.service.CheckForUpdateAsync(CancellationToken.None);
            this.state.Save(DateTime.UtcNow);
            if (update is null)
            {
                if (interactive)
                {
                    await UpdateDialog.ShowAsync("No update available", $"PrimeDictate is up to date.\n\nInstalled version: {this.current.ToString(3)}");
                }

                return;
            }

            await this.OfferAsync(update);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or IOException or TaskCanceledException or System.Text.Json.JsonException)
        {
            System.Diagnostics.Trace.TraceWarning($"Update check failed: {ex.Message}");
            if (interactive)
            {
                await UpdateDialog.ShowAsync("Update check failed", $"Update check failed: {ex.Message}");
            }
        }
        finally
        {
            this.busy = false;
            this.SetMenu(IdleText, enabled: true);
        }
    }

    private async Task OfferAsync(AppUpdateInfo update)
    {
        var published = update.PublishedAt is { } when ? $"\nPublished: {when.LocalDateTime:g}" : string.Empty;
        var install = await UpdateDialog.AskAsync(
            "PrimeDictate update available",
            $"PrimeDictate {update.DisplayVersion} is available.\n\nInstalled version: {this.current.ToString(3)}\nInstaller: {update.InstallerAssetName}{published}\n\n" +
            "Download, verify and start the installer now? PrimeDictate will close first. A meeting recording in progress is saved before it closes.",
            "Install now",
            "Not now");
        if (!install)
        {
            return;
        }

        var progress = new Progress<UpdateDownloadProgress>(p => this.SetMenu(p.Percent is { } percent ? $"Downloading update... {percent}%" : "Downloading update...", enabled: false));
        try
        {
            var path = await this.service.DownloadAndVerifyInstallerAsync(update, progress, CancellationToken.None);
            GitHubUpdateService.StartInstaller(path, keepInstallerStartupShortcut: this.launch.InstallerShortcutPresent);
            await this.exit();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or TaskCanceledException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // A failed attempt must not wait a day for the next automatic try.
            this.state.Save(null);
            await UpdateDialog.ShowAsync("Update failed", $"The update could not be installed: {ex.Message}");
        }
    }

    private void SetMenu(string text, bool enabled)
    {
        this.MenuText = text;
        this.MenuEnabled = enabled;
        this.MenuChanged?.Invoke();
    }
}
