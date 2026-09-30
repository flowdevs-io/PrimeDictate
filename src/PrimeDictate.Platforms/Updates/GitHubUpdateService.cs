using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PrimeDictate.Platforms.Updates;

/// <summary>
/// Checks GitHub Releases, downloads the MSI and verifies its SHA-256 against the release's .sha256 asset, then hands off to
/// msiexec. Checking is read-only; the caller asks the user before downloading and installing. The installer handoff is Windows only.
/// </summary>
public sealed class GitHubUpdateService : IDisposable
{
    private const string Owner = "CakeRepository";
    private const string Repository = "PrimeDictate";
    private const int RetryCount = 40;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(750);
    private static readonly Uri LatestReleaseApiUrl = new($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");

    private readonly HttpClient http = new();
    private readonly Version current;
    private readonly string architecture;
    private readonly string downloadDirectory;
    private readonly Action<string> log;

    public GitHubUpdateService(Version current, string downloadDirectory, Action<string>? log = null, string? architecture = null)
    {
        this.current = current;
        this.downloadDirectory = downloadDirectory;
        this.log = log ?? (_ => { });
        this.architecture = architecture ?? UpdateRules.ArchitectureName(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture);
    }

    /// <summary>The version of the running app, from the assembly's informational version.</summary>
    public static Version CurrentApplicationVersion(Assembly assembly) =>
        UpdateRules.CurrentVersion(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, assembly.GetName().Version);

    public void Dispose() => this.http.Dispose();

    /// <summary>The newer release for this machine, or null when up to date (or the latest is a draft or prerelease).</summary>
    public async Task<AppUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        using var request = this.CreateRequest(LatestReleaseApiUrl);
        using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return UpdateRules.ReadRelease(json, this.current, this.architecture);
    }

    /// <summary>Downloads the MSI to the updates folder and returns its path only when the SHA-256 matches the release checksum.</summary>
    public async Task<string> DownloadAndVerifyInstallerAsync(AppUpdateInfo update, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var expected = await this.DownloadExpectedSha256Async(update, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(this.downloadDirectory);

        var installerPath = Path.Combine(this.downloadDirectory, update.InstallerAssetName);
        var unique = false;
        if (File.Exists(installerPath))
        {
            try
            {
                if (string.Equals(await ComputeSha256Async(installerPath, cancellationToken).ConfigureAwait(false), expected, StringComparison.OrdinalIgnoreCase))
                {
                    var length = new FileInfo(installerPath).Length;
                    progress?.Report(new UpdateDownloadProgress(length, length));
                    return installerPath;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unique = true;
                this.log($"Cached update installer is unavailable; downloading a fresh copy: {ex.Message}");
            }
        }

        var tempPath = Path.Combine(this.downloadDirectory, $"{Path.GetFileNameWithoutExtension(update.InstallerAssetName)}.{Environment.ProcessId}.{Guid.NewGuid():N}.download");
        using var request = this.CreateRequest(update.InstallerDownloadUrl);
        using var response = await this.http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? update.InstallerSizeBytes;
        string actual;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[1024 * 128];
            long received = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer.AsSpan(0, read));
                received += read;
                progress?.Report(new UpdateDownloadProgress(received, total));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(tempPath);
            throw new InvalidDataException($"Downloaded installer hash mismatch. Expected {expected}, got {actual}.");
        }

        var target = unique ? this.UniquePath(update.InstallerAssetName) : installerPath;
        try
        {
            File.Move(tempPath, target, overwrite: !unique);
        }
        catch (IOException ex)
        {
            // The canonical path is locked (Windows Installer or antivirus has it open): use a unique one, retrying while it is held.
            this.log($"Could not place the cached installer ('{ex.Message}'); retrying with a unique path.");
            target = unique ? target : this.UniquePath(update.InstallerAssetName);
            await MoveWithRetryAsync(tempPath, target, cancellationToken).ConfigureAwait(false);
        }

        await WaitForReadableAsync(target, cancellationToken).ConfigureAwait(false);
        return target;
    }

    /// <summary>
    /// Starts a hidden PowerShell that waits for this process to exit, then runs msiexec elevated on the MSI. Call it, then quit the app
    /// through its clean exit path. <paramref name="keepInstallerStartupShortcut"/> is passed to the MSI as LAUNCHATLOGIN.
    /// </summary>
    public static Process StartInstaller(string installerPath, bool keepInstallerStartupShortcut)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Installing an update through Windows Installer is only available on Windows.");
        }

        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("The update installer was not found.", installerPath);
        }

        var property = keepInstallerStartupShortcut ? "LAUNCHATLOGIN=1" : "LAUNCHATLOGIN=0";
        var directory = Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory;
        var scriptPath = Path.Combine(directory, $"PrimeDictate.StartUpdate.{Environment.ProcessId}.{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, InstallerLaunchScript, Encoding.UTF8);
        var arguments = string.Join(" ", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Quote(scriptPath), Environment.ProcessId.ToString(CultureInfo.InvariantCulture), Quote(installerPath), property);
        return Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory
        }) ?? throw new InvalidOperationException("The update handoff process did not start.");
    }

    private const string InstallerLaunchScript = """
        param(
            [int] $PrimeDictateProcessId,
            [string] $InstallerPath,
            [string] $LaunchAtLoginProperty
        )

        $ErrorActionPreference = 'Stop'

        try {
            Wait-Process -Id $PrimeDictateProcessId -Timeout 120 -ErrorAction SilentlyContinue
        } catch {
        }

        $deadline = (Get-Date).AddSeconds(90)
        while ($true) {
            try {
                $probe = [System.IO.File]::Open($InstallerPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
                $probe.Dispose()
                break
            } catch [System.IO.IOException] {
                if ((Get-Date) -ge $deadline) {
                    throw "Installer file remained locked and could not be opened for read access: $InstallerPath"
                }

                Start-Sleep -Milliseconds 750
            } catch [System.UnauthorizedAccessException] {
                if ((Get-Date) -ge $deadline) {
                    throw "Installer file could not be accessed before timeout: $InstallerPath"
                }

                Start-Sleep -Milliseconds 750
            }
        }

        $quotedInstallerPath = '"' + $InstallerPath.Replace('"', '\"') + '"'
        $installerArguments = "/i $quotedInstallerPath $LaunchAtLoginProperty"
        $msiexecPath = Join-Path $env:SystemRoot 'System32\msiexec.exe'
        Start-Process -FilePath $msiexecPath -ArgumentList $installerArguments -Verb RunAs

        try {
            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        } catch {
        }
        """;

    private string UniquePath(string assetName) =>
        Path.Combine(this.downloadDirectory, $"{Path.GetFileNameWithoutExtension(assetName)}.{DateTime.UtcNow:yyyyMMddHHmmss}.{Guid.NewGuid():N}.msi");

    private async Task<string> DownloadExpectedSha256Async(AppUpdateInfo update, CancellationToken cancellationToken)
    {
        using var request = this.CreateRequest(update.Sha256DownloadUrl);
        using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return UpdateRules.ParseSha256(text) ?? throw new InvalidDataException($"{update.InstallerAssetName}.sha256 did not contain a valid SHA256 checksum.");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static async Task MoveWithRetryAsync(string source, string target, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= RetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(source, target, overwrite: false);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException($"Unable to move the installer to '{target}' after {RetryCount} attempts.", last);
    }

    private static async Task WaitForReadableAsync(string path, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= RetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException($"Installer '{path}' remained locked after {RetryCount} attempts.", last);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .download file is harmless.
        }
    }

    private HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd($"PrimeDictate/{this.current.ToString(3)}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

/// <summary>Remembers when the last automatic check ran, in a small JSON file, so launches within 24 hours do not check again.</summary>
public sealed class UpdateCheckState(string path)
{
    public DateTime? LastCheckUtc()
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("lastCheckUtc", out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTime(out var when) ? when.ToUniversalTime() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(DateTime? whenUtc)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { lastCheckUtc = whenUtc }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing to remember only means the next launch checks again.
        }
    }
}
