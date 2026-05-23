#Requires -Version 5.1
<#
.SYNOPSIS
  Builds WinGet multi-file manifests from release MSI artifacts.
#>
param(
    [string] $PackageIdentifier = "FlowDevs.PrimeDictate",
    [Parameter(Mandatory = $true)]
    [string] $PackageVersion,
    [string] $Repository = "flowdevs-io/PrimeDictate",
    [string] $InstallerDirectory,
    [string] $OutputRoot,
    [string] $DefaultLocale = "en-US",
    [string] $Publisher = "FlowDevs",
    [string] $PublisherUrl = "https://flowdevs.io",
    [string] $PublisherSupportUrl = "https://github.com/flowdevs-io/PrimeDictate/issues",
    [string] $Author = "Justin Trantham",
    [string] $PackageName = "PrimeDictate",
    [string] $PackageUrl = "https://github.com/flowdevs-io/PrimeDictate",
    [string] $Moniker = "primedictate",
    [string] $License = "Proprietary",
    [string] $ShortDescription = "Locally hosted global hotkey dictation for fast Windows desktop workflows.",
    [string] $Description = "PrimeDictate records your default Windows microphone, transcribes locally with on-device models, and types the final transcript into the active app."
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-MsiProperty {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [string] $PropertyName
    )

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $view = $null
    try {
        $database = $installer.OpenDatabase($Path, 0)
        $query = "SELECT `Value` FROM `Property` WHERE `Property`='$PropertyName'"
        $view = $database.OpenView($query)
        $view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) {
            throw "Property '$PropertyName' was not found in MSI: $Path"
        }

        $value = $record.StringData(1)
        if ([string]::IsNullOrWhiteSpace($value)) {
            throw "Property '$PropertyName' is empty in MSI: $Path"
        }

        return $value.Trim()
    }
    finally {
        if ($null -ne $view) {
            [void] [System.Runtime.InteropServices.Marshal]::ReleaseComObject($view)
        }
        if ($null -ne $database) {
            [void] [System.Runtime.InteropServices.Marshal]::ReleaseComObject($database)
        }
        [void] [System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$installerRoot = if ([string]::IsNullOrWhiteSpace($InstallerDirectory)) {
    Join-Path $repoRoot "artifacts\installer"
}
else {
    $InstallerDirectory
}
$manifestRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repoRoot "artifacts\winget"
}
else {
    $OutputRoot
}

if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    throw "PackageVersion is required."
}

if ($PackageVersion -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z\.-]*)?$') {
    throw "PackageVersion '$PackageVersion' is not a valid package version."
}

$releaseTag = "v$PackageVersion"
$x64AssetName = "PrimeDictate-Setup-$releaseTag-x64.msi"
$arm64AssetName = "PrimeDictate-Setup-$releaseTag-arm64.msi"
$x64ReleaseUrl = "https://github.com/$Repository/releases/download/$releaseTag/$x64AssetName"
$arm64ReleaseUrl = "https://github.com/$Repository/releases/download/$releaseTag/$arm64AssetName"

$x64MsiPath = Join-Path $installerRoot "PrimeDictate-$PackageVersion-Windows-x64-Online.msi"
$arm64MsiPath = Join-Path $installerRoot "PrimeDictate-$PackageVersion-Windows-arm64-Online.msi"

if (-not (Test-Path $x64MsiPath)) {
    throw "Expected x64 MSI not found: $x64MsiPath"
}
if (-not (Test-Path $arm64MsiPath)) {
    throw "Expected arm64 MSI not found: $arm64MsiPath"
}

$x64Hash = (Get-FileHash -Path $x64MsiPath -Algorithm SHA256).Hash.ToUpperInvariant()
$arm64Hash = (Get-FileHash -Path $arm64MsiPath -Algorithm SHA256).Hash.ToUpperInvariant()
$x64ProductCode = Get-MsiProperty -Path $x64MsiPath -PropertyName "ProductCode"
$arm64ProductCode = Get-MsiProperty -Path $arm64MsiPath -PropertyName "ProductCode"

$idParts = $PackageIdentifier.Split(".")
if ($idParts.Length -lt 2) {
    throw "PackageIdentifier '$PackageIdentifier' must use the Publisher.App format."
}

$partition = $PackageIdentifier.Substring(0, 1).ToLowerInvariant()
$publisherSegment = $idParts[0]
$packageSegment = ($idParts[1..($idParts.Length - 1)] -join ".")
$versionFolder = Join-Path $manifestRoot (Join-Path "manifests\$partition" (Join-Path $publisherSegment (Join-Path $packageSegment $PackageVersion)))
New-Item -ItemType Directory -Force -Path $versionFolder | Out-Null

$versionManifestPath = Join-Path $versionFolder "$PackageIdentifier.yaml"
$defaultLocaleManifestPath = Join-Path $versionFolder "$PackageIdentifier.locale.$DefaultLocale.yaml"
$installerManifestPath = Join-Path $versionFolder "$PackageIdentifier.installer.yaml"

$versionManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.10.0.schema.json
PackageIdentifier: $PackageIdentifier
PackageVersion: $PackageVersion
DefaultLocale: $DefaultLocale
ManifestType: version
ManifestVersion: 1.10.0
"@

$defaultLocaleManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.10.0.schema.json
PackageIdentifier: $PackageIdentifier
PackageVersion: $PackageVersion
PackageLocale: $DefaultLocale
Publisher: $Publisher
PublisherUrl: $PublisherUrl
PublisherSupportUrl: $PublisherSupportUrl
Author: $Author
PackageName: $PackageName
PackageUrl: $PackageUrl
License: $License
ShortDescription: $ShortDescription
Description: $Description
Moniker: $Moniker
Tags:
  - dictation
  - speech-to-text
  - transcription
  - productivity
ReleaseNotesUrl: https://github.com/$Repository/releases/tag/$releaseTag
ManifestType: defaultLocale
ManifestVersion: 1.10.0
"@

$installerManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.10.0.schema.json
PackageIdentifier: $PackageIdentifier
PackageVersion: $PackageVersion
InstallerType: wix
Scope: machine
InstallerSwitches:
  Custom: LAUNCHATLOGIN=0
UpgradeBehavior: install
Installers:
  - Architecture: x64
    InstallerUrl: $x64ReleaseUrl
    InstallerSha256: $x64Hash
    ProductCode: '$x64ProductCode'
  - Architecture: arm64
    InstallerUrl: $arm64ReleaseUrl
    InstallerSha256: $arm64Hash
    ProductCode: '$arm64ProductCode'
ManifestType: installer
ManifestVersion: 1.10.0
"@

Set-Content -Path $versionManifestPath -Value $versionManifest -Encoding UTF8
Set-Content -Path $defaultLocaleManifestPath -Value $defaultLocaleManifest -Encoding UTF8
Set-Content -Path $installerManifestPath -Value $installerManifest -Encoding UTF8

Write-Output $versionFolder
