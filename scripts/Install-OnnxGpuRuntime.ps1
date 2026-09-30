<#
.SYNOPSIS
  Installs the ONNX Runtime GPU build (CUDA 13) that lets PrimeDictate run Whisper and Parakeet on an NVIDIA GPU.

.DESCRIPTION
  Downloads Microsoft.ML.OnnxRuntime.Gpu.Windows from nuget.org, checks its SHA-256, and copies three DLLs into
  %LocalAppData%\PrimeDictate\gpu\onnxruntime-cuda13. Nothing is installed system-wide and the app's own files are not touched.
  It does NOT install CUDA or cuDNN. PrimeDictate also needs the CUDA 13 runtime (Toolkit 13.x) and cuDNN 9 for CUDA 13;
  the app names whatever is missing in its startup notice.

  Uninstall: delete the folder.
#>
[CmdletBinding()]
param(
    [string]$Version = '1.30.0',
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'PrimeDictate\gpu\onnxruntime-cuda13'),
    # SHA-256 of the 1.30.0 package as downloaded when this script was written. Other versions are not checked.
    [string]$ExpectedSha256 = $(if ($Version -eq '1.30.0') { 'f86f88d2dcdbc1411a5a4f345751c37a85f965300e150da6d882bea41ea7296d' } else { '' })
)

$ErrorActionPreference = 'Stop'
$id = 'microsoft.ml.onnxruntime.gpu.windows'
$url = "https://api.nuget.org/v3-flatcontainer/$id/$Version/$id.$Version.nupkg"
$package = Join-Path ([IO.Path]::GetTempPath()) "$id.$Version.nupkg"

Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $package -UseBasicParsing

if ($ExpectedSha256) {
    $actual = (Get-FileHash -Algorithm SHA256 -Path $package).Hash.ToLowerInvariant()
    if ($actual -ne $ExpectedSha256.ToLowerInvariant()) {
        Remove-Item $package -Force
        throw "SHA-256 mismatch: expected $ExpectedSha256 but got $actual. Nothing was installed."
    }
} else {
    Write-Warning "No expected hash for version $Version; the package was not verified."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$files = 'onnxruntime.dll', 'onnxruntime_providers_shared.dll', 'onnxruntime_providers_cuda.dll'
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    foreach ($name in $files) {
        $entry = $zip.GetEntry("runtimes/win-x64/native/$name")
        if (-not $entry) { throw "The package has no $name." }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $Destination $name), $true)
    }
} finally {
    $zip.Dispose()
    Remove-Item $package -Force
}

Write-Host "Installed ONNX Runtime $Version GPU into $Destination"
Write-Host 'Restart PrimeDictate. If CUDA cannot be used it says why in its startup notice and runs on the CPU.'
