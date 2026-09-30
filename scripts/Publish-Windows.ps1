#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes the self-contained Windows build of the Avalonia app (src\PrimeDictate.Desktop, output PrimeDictate.exe)
    to artifacts\<rid>\publish. The WPF app at the repo root is legacy and is not published here.
#>
param(
    [string] $Configuration = "Release",
        [ValidateSet("win-x64", "win-arm64")]
        [string] $RuntimeIdentifier = "win-x64",
    [string] $PackageVersion,
    [string] $AssemblyVersion,
    [string] $FileVersion,
    [string] $InformationalVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishDir = Join-Path $repoRoot (Join-Path "artifacts" (Join-Path $RuntimeIdentifier "publish"))

Push-Location $repoRoot
try {
    if (Test-Path $publishDir) {
        Remove-Item -Recurse -Force $publishDir
    }

    $msbuildProps = @()
    if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) {
        $msbuildProps += "-p:Version=$PackageVersion"
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

    dotnet publish .\src\PrimeDictate.Desktop\PrimeDictate.Desktop.csproj `
        -c $Configuration `
        -r $RuntimeIdentifier `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        $msbuildProps `
        -o $publishDir

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    Write-Host "Published to $publishDir"
}
finally {
    Pop-Location
}
