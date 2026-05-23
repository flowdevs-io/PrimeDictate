#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes the app and builds WiX online MSIs using only the .NET SDK.

.PARAMETER Installer
  Online (downloads models after install). The offline installer is currently not built by this helper.

.PARAMETER RuntimeIdentifier
  Windows runtime identifier(s) to build. Use win-x64, win-arm64, or all.

.PARAMETER SkipPublish
  Reuse existing artifacts\<rid>\publish directories without running dotnet publish.

.NOTES
  Requires .NET 8 SDK. WiX Toolset is restored via NuGet (WixToolset.Sdk); no separate WiX install needed.
#>
param(
    [ValidateSet("Online")]
    [string] $Installer = "Online",

    [ValidateSet("win-x64", "win-arm64", "all")]
    [string[]] $RuntimeIdentifier = @("all"),

    [switch] $SkipPublish,
    [string] $PackageVersion,
    [string] $AssemblyVersion,
    [string] $FileVersion,
    [string] $InformationalVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-RepoVersion {
    param([string] $RepoRoot)
    [xml] $doc = Get-Content (Join-Path $RepoRoot "Directory.Build.props")
    $pg = @($doc.Project.PropertyGroup) | Where-Object { $_.Version } | Select-Object -First 1
    if (-not $pg -or -not $pg.Version) {
        throw "Could not read Version from Directory.Build.props"
    }
    return $pg.Version.Trim()
}

function Get-MsiCompatibleVersion {
    param([string] $Version)

    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "Version is required to compute an MSI-compatible package version."
    }

    $normalizedVersion = $Version.Trim()
    $match = [regex]::Match($normalizedVersion, '^(?<core>\d+\.\d+\.\d+)')
    if (-not $match.Success) {
        throw "Version '$Version' does not start with a semver core like 1.2.3."
    }

    return $match.Groups['core'].Value
}

function Resolve-RuntimeIdentifiers {
    param([string[]] $RequestedRuntimeIdentifiers)

    if ($RequestedRuntimeIdentifiers -contains "all") {
        return @("win-x64", "win-arm64")
    }

    return @($RequestedRuntimeIdentifiers | Select-Object -Unique)
}

function Get-InstallerPlatform {
    param([string] $Rid)

    switch ($Rid) {
        "win-x64" { return "x64" }
        "win-arm64" { return "arm64" }
        default { throw "Unsupported runtime identifier '$Rid'." }
    }
}

function Test-SelfContainedPublishOutput {
    param([string] $PublishDir)

    $requiredFiles = @(
        "PrimeDictate.exe",
        "PrimeDictate.dll",
        "PrimeDictate.runtimeconfig.json",
        "hostfxr.dll",
        "hostpolicy.dll",
        "coreclr.dll",
        "System.Private.CoreLib.dll",
        "PresentationFramework.dll",
        "WindowsBase.dll"
    )

    foreach ($file in $requiredFiles) {
        $path = Join-Path $PublishDir $file
        if (-not (Test-Path $path)) {
            throw "Publish output is missing required self-contained runtime file: $path"
        }
    }

    $runtimeConfigPath = Join-Path $PublishDir "PrimeDictate.runtimeconfig.json"
    $runtimeConfig = Get-Content -Raw $runtimeConfigPath
    if ($runtimeConfig -notmatch '"includedFrameworks"') {
        throw "Publish output is framework-dependent. Expected includedFrameworks in $runtimeConfigPath"
    }
}

function Test-MsiContainsSelfContainedRuntime {
    param([string] $MsiPath)

    $requiredFiles = @(
        "hostfxr.dll",
        "hostpolicy.dll",
        "coreclr.dll",
        "System.Private.CoreLib.dll",
        "PresentationFramework.dll",
        "WindowsBase.dll"
    )

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $view = $null
    try {
        $database = $installer.OpenDatabase($MsiPath, 0)
        $view = $database.OpenView('SELECT `FileName` FROM `File`')
        $view.Execute()

        $fileNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        while ($record = $view.Fetch()) {
            $fileName = $record.StringData(1)
            if ($fileName.Contains("|")) {
                $fileName = $fileName.Split("|")[-1]
            }

            [void] $fileNames.Add($fileName)
        }

        foreach ($file in $requiredFiles) {
            if (-not $fileNames.Contains($file)) {
                throw "MSI payload is missing required self-contained runtime file: $file ($MsiPath)"
            }
        }
    }
    finally {
        if ($view -ne $null) {
            [void] [System.Runtime.InteropServices.Marshal]::ReleaseComObject($view)
        }

        if ($database -ne $null) {
            [void] [System.Runtime.InteropServices.Marshal]::ReleaseComObject($database)
        }

        [void] [System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$onlineProj = Join-Path $repoRoot "installer\wix\online\PrimeDictate.Online.wixproj"
$outDir = Join-Path $repoRoot "artifacts\installer"
$version = if ([string]::IsNullOrWhiteSpace($PackageVersion)) { Get-RepoVersion -RepoRoot $repoRoot } else { $PackageVersion }
$msiVersion = Get-MsiCompatibleVersion -Version $version
$requestedRids = Resolve-RuntimeIdentifiers -RequestedRuntimeIdentifiers $RuntimeIdentifier
$msbuildProps = @()
if (-not [string]::IsNullOrWhiteSpace($msiVersion)) {
    $msbuildProps += "-p:Version=$msiVersion"
}
if (-not [string]::IsNullOrWhiteSpace($AssemblyVersion)) {
    $msbuildProps += "-p:AssemblyVersion=$AssemblyVersion"
}
if (-not [string]::IsNullOrWhiteSpace($FileVersion)) {
    $msbuildProps += "-p:FileVersion=$FileVersion"
}
if (-not [string]::IsNullOrWhiteSpace($InformationalVersion)) {
    $msbuildProps += "-p:InformationalVersion=$InformationalVersion"
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

foreach ($rid in $requestedRids) {
    $installerPlatform = Get-InstallerPlatform -Rid $rid
    $publishDir = Join-Path $repoRoot (Join-Path "artifacts" (Join-Path $rid "publish"))

    if (-not $SkipPublish) {
        & (Join-Path $PSScriptRoot "Publish-Windows.ps1") `
            -RuntimeIdentifier $rid `
            -PackageVersion $PackageVersion `
            -AssemblyVersion $AssemblyVersion `
            -FileVersion $FileVersion `
            -InformationalVersion $InformationalVersion
    }

    if (-not (Test-Path (Join-Path $publishDir "PrimeDictate.exe"))) {
        throw "Publish output missing PrimeDictate.exe at $publishDir. Run without -SkipPublish."
    }

    Test-SelfContainedPublishOutput -PublishDir $publishDir
    $publishDirFull = (Resolve-Path $publishDir).Path

    Write-Host "Building online MSI for $rid..."
    dotnet build $onlineProj `
        -c Release `
        "-p:RuntimeIdentifier=$rid" `
        "-p:InstallerPlatform=$installerPlatform" `
        "-p:PublishDir=$publishDirFull" `
        $msbuildProps
    if ($LASTEXITCODE -ne 0) {
        throw "Online WiX build failed for $rid with exit code $LASTEXITCODE"
    }

    $builtOnlineMsi = Join-Path $repoRoot "installer\wix\online\bin\Release\PrimeDictate-$msiVersion-Windows-$installerPlatform-Online.msi"
    $publishedOnlineMsi = Join-Path $outDir "PrimeDictate-$version-Windows-$installerPlatform-Online.msi"
    if (-not (Test-Path $builtOnlineMsi)) {
        throw "Expected built MSI not found: $builtOnlineMsi"
    }

    Test-MsiContainsSelfContainedRuntime -MsiPath $builtOnlineMsi
    Copy-Item -Force $builtOnlineMsi $publishedOnlineMsi
}

Write-Host "Done. Artifacts available in: $outDir"
