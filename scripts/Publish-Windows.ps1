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

    # Qualcomm NPU (QNN) on ARM64: the app project copies the ONNX Runtime QNN natives over sherpa-onnx's onnxruntime.dll after publish
    # (one process can load only one onnxruntime.dll). If that step silently stopped working, the ARM64 installer would ship without the
    # NPU and nobody would notice until a Snapdragon PC ran it, so fail the publish instead. x64 must not carry them.
    $qnnFiles = @("QnnHtp.dll", "QnnSystem.dll", "onnxruntime_providers_qnn.dll")
    if ($RuntimeIdentifier -eq "win-arm64") {
        $missingQnn = $qnnFiles | Where-Object { -not (Test-Path (Join-Path $publishDir $_)) }
        if ($missingQnn) {
            throw "The win-arm64 publish is missing the Qualcomm QNN natives: $($missingQnn -join ', ')."
        }
    }
    elseif (Test-Path (Join-Path $publishDir "QnnHtp.dll")) {
        throw "The $RuntimeIdentifier publish contains Qualcomm QNN natives, which belong only in win-arm64."
    }

    # Whisper.net ships its natives as content under runtimes\<rid> and runtimes\<variant>\<rid> for every OS, and
    # publish copies all of them. Keep only the folders for this runtime.
    $runtimesDir = Join-Path $publishDir "runtimes"
    if (Test-Path $runtimesDir) {
        Get-ChildItem $runtimesDir -Directory -Recurse |
            Where-Object { $_.Name -match '^(win|linux|linux-musl|osx|macos|maccatalyst|ios|android|browser)(-|$)' -and $_.Name -ne $RuntimeIdentifier } |
            Sort-Object { $_.FullName.Length } -Descending |
            ForEach-Object { if (Test-Path $_.FullName) { Remove-Item -Recurse -Force $_.FullName } }
        Get-ChildItem $runtimesDir -Directory | Where-Object { -not (Get-ChildItem $_.FullName -Recurse -File) } |
            ForEach-Object { Remove-Item -Recurse -Force $_.FullName }
    }

    Write-Host "Published to $publishDir"
}
finally {
    Pop-Location
}
