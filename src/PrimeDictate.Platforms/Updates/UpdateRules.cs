using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PrimeDictate.Platforms.Updates;

/// <summary>What a newer GitHub release offers for this machine.</summary>
public sealed record AppUpdateInfo(
    string TagName,
    Version Version,
    Uri ReleasePageUrl,
    Uri InstallerDownloadUrl,
    string InstallerAssetName,
    Uri Sha256DownloadUrl,
    long? InstallerSizeBytes,
    DateTimeOffset? PublishedAt)
{
    public string DisplayVersion => this.TagName.StartsWith('v') ? this.TagName[1..] : this.TagName;
}

public sealed record UpdateDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public int? Percent => this.TotalBytes is > 0 ? (int)Math.Clamp(this.BytesReceived * 100 / this.TotalBytes.Value, 0, 100) : null;
}

/// <summary>
/// The pure rules of the updater, kept apart from the network and the installer handoff so they can be tested: the same release
/// asset names and checksum format the WPF app used (and the release workflow produces), version comparison and the check interval.
/// </summary>
public static class UpdateRules
{
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);

    /// <summary>"x64" or "arm64", the architecture part of the release asset name.</summary>
    public static string ArchitectureName(Architecture architecture) => architecture == Architecture.Arm64 ? "arm64" : "x64";

    public static string InstallerAssetName(string tagName, string architecture) => $"PrimeDictate-Setup-{tagName}-{architecture}.msi";

    public static string ChecksumAssetName(string tagName, string architecture) => InstallerAssetName(tagName, architecture) + ".sha256";

    /// <summary>Reads "v6.1.0", "6.1.0-beta+abc" and similar as 6.1.0. False when there is no major.minor.build.</summary>
    public static bool TryParseReleaseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var versionText = text.Trim();
        if (versionText.StartsWith('v') || versionText.StartsWith('V'))
        {
            versionText = versionText[1..];
        }

        versionText = versionText.Split(['-', '+'], 2, StringSplitOptions.TrimEntries)[0];
        if (Version.TryParse(versionText, out var parsed) && parsed.Major >= 0 && parsed.Minor >= 0 && parsed.Build >= 0)
        {
            version = new Version(parsed.Major, parsed.Minor, parsed.Build);
            return true;
        }

        return false;
    }

    /// <summary>The running version from the informational version when present (it carries the release number), else the assembly version.</summary>
    public static Version CurrentVersion(string? informationalVersion, Version? assemblyVersion)
    {
        if (TryParseReleaseVersion(informationalVersion, out var parsed))
        {
            return parsed;
        }

        return assemblyVersion is null ? new Version(0, 0, 0) : new Version(Math.Max(0, assemblyVersion.Major), Math.Max(0, assemblyVersion.Minor), Math.Max(0, assemblyVersion.Build));
    }

    /// <summary>Extracts the first 64-hex-digit token of a .sha256 file ("hash  name" or just the hash), lower-cased; null when none.</summary>
    public static string? ParseSha256(string? checksumText)
    {
        if (string.IsNullOrWhiteSpace(checksumText))
        {
            return null;
        }

        foreach (var part in checksumText.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length == 64 && part.All(Uri.IsHexDigit))
            {
                return part.ToLowerInvariant();
            }
        }

        return null;
    }

    public static bool IsCheckDue(DateTime? lastCheckUtc, DateTime nowUtc) =>
        lastCheckUtc is not { } last || nowUtc - DateTime.SpecifyKind(last, DateTimeKind.Utc) >= AutomaticCheckInterval;

    /// <summary>
    /// Reads a GitHub "latest release" document. Null when it is a draft or prerelease or not newer than <paramref name="current"/>.
    /// Throws <see cref="InvalidOperationException"/> when a newer release lacks this machine's installer or its checksum file,
    /// because installing without a checksum is never offered.
    /// </summary>
    public static AppUpdateInfo? ReadRelease(string json, Version current, string architecture)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (IsTrue(root, "draft") || IsTrue(root, "prerelease"))
        {
            return null;
        }

        var tagName = RequiredString(root, "tag_name");
        if (!TryParseReleaseVersion(tagName, out var releaseVersion) || releaseVersion <= current)
        {
            return null;
        }

        var installerName = InstallerAssetName(tagName, architecture);
        if (!TryFindAsset(root, installerName, out var installer))
        {
            throw new InvalidOperationException($"The latest PrimeDictate release is {tagName}, but it does not include {installerName}.");
        }

        var checksumName = ChecksumAssetName(tagName, architecture);
        if (!TryFindAsset(root, checksumName, out var checksum))
        {
            throw new InvalidOperationException($"The latest PrimeDictate release is {tagName}, but it does not include {checksumName}.");
        }

        return new AppUpdateInfo(
            tagName,
            releaseVersion,
            new Uri(RequiredString(root, "html_url"), UriKind.Absolute),
            new Uri(RequiredString(installer, "browser_download_url"), UriKind.Absolute),
            installerName,
            new Uri(RequiredString(checksum, "browser_download_url"), UriKind.Absolute),
            installer.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : null,
            root.TryGetProperty("published_at", out var published) && published.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(published.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : null);
    }

    private static bool TryFindAsset(JsonElement root, string name, out JsonElement asset)
    {
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in assets.EnumerateArray())
            {
                if (string.Equals(OptionalString(candidate, "name"), name, StringComparison.OrdinalIgnoreCase))
                {
                    asset = candidate;
                    return true;
                }
            }
        }

        asset = default;
        return false;
    }

    private static bool IsTrue(JsonElement element, string name) => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) ?? throw new InvalidDataException($"GitHub release response did not include '{name}'.");
}
