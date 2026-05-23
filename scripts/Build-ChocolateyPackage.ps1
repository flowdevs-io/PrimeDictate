param(
  [Parameter(Mandatory = $true)]
  [string]$PackageVersion,

  [Parameter(Mandatory = $true)]
  [string]$ReleaseAssetDirectory,

  [string]$TemplateRoot = (Join-Path $PSScriptRoot "..\installer\chocolatey"),
  [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\artifacts\chocolatey"),
  [string]$Repository = "flowdevs-io/PrimeDictate"
)

$ErrorActionPreference = 'Stop'

function Get-RequiredAssetPath {
  param(
    [Parameter(Mandatory = $true)]
    [string]$BaseDirectory,

    [Parameter(Mandatory = $true)]
    [string]$RelativePath
  )

  $resolvedPath = Join-Path $BaseDirectory $RelativePath
  if (-not (Test-Path $resolvedPath)) {
    throw "Required release asset was not found: $resolvedPath"
  }

  return $resolvedPath
}

function Get-ReleaseSha256 {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ShaFilePath
  )

  $line = (Get-Content -Path $ShaFilePath -Raw).Trim()
  if ([string]::IsNullOrWhiteSpace($line)) {
    throw "SHA256 file is empty: $ShaFilePath"
  }

  $parts = $line -split '\s+', 2
  if ($parts.Length -lt 1 -or $parts[0] -notmatch '^[0-9a-fA-F]{64}$') {
    throw "SHA256 file did not start with a valid checksum: $ShaFilePath"
  }

  return $parts[0].ToLowerInvariant()
}

$releaseTag = "v$PackageVersion"
$x64ShaPath = Get-RequiredAssetPath -BaseDirectory $ReleaseAssetDirectory -RelativePath "PrimeDictate-Setup-$releaseTag-x64.msi.sha256"
$arm64ShaPath = Get-RequiredAssetPath -BaseDirectory $ReleaseAssetDirectory -RelativePath "PrimeDictate-Setup-$releaseTag-arm64.msi.sha256"

$x64Checksum = Get-ReleaseSha256 -ShaFilePath $x64ShaPath
$arm64Checksum = Get-ReleaseSha256 -ShaFilePath $arm64ShaPath

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("primedictate-choco-" + [Guid]::NewGuid().ToString('N'))
$stagingDir = Join-Path $stagingRoot "package"
New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null

Copy-Item -Path (Join-Path $TemplateRoot '*') -Destination $stagingDir -Recurse -Force

$repoUrl = "https://github.com/$Repository"
$rawContentUrl = "https://raw.githubusercontent.com/$Repository/main"
$nuspecPath = Join-Path $stagingDir "primedictate.nuspec"
$installScriptPath = Join-Path $stagingDir "tools\chocolateyInstall.ps1"

$nuspecContent = Get-Content -Path $nuspecPath -Raw
$nuspecContent = $nuspecContent.Replace('<version>0.0.0</version>', "<version>$PackageVersion</version>")
$nuspecContent = $nuspecContent.Replace('https://github.com/CakeRepository/PrimeDictate', $repoUrl)
$nuspecContent = $nuspecContent.Replace('https://raw.githubusercontent.com/CakeRepository/PrimeDictate/main', $rawContentUrl)
Set-Content -Path $nuspecPath -Value $nuspecContent -NoNewline

$installScriptContent = Get-Content -Path $installScriptPath -Raw
$installScriptContent = $installScriptContent.Replace("'4.4.0'", "'$PackageVersion'")
$installScriptContent = $installScriptContent.Replace('https://github.com/CakeRepository/PrimeDictate', $repoUrl)
$installScriptContent = $installScriptContent.Replace('__PRIMEDICTATE_X64_SHA256__', $x64Checksum)
$installScriptContent = $installScriptContent.Replace('__PRIMEDICTATE_ARM64_SHA256__', $arm64Checksum)
Set-Content -Path $installScriptPath -Value $installScriptContent -NoNewline

$verificationPath = Join-Path $stagingDir 'tools\VERIFICATION.txt'
$licensePath = Join-Path $stagingDir 'tools\LICENSE.txt'
foreach ($textFile in @($verificationPath, $licensePath)) {
  $content = Get-Content -Path $textFile -Raw
  $content = $content.Replace('https://github.com/CakeRepository/PrimeDictate', $repoUrl)
  Set-Content -Path $textFile -Value $content -NoNewline
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

Push-Location $stagingDir
try {
  choco pack .\primedictate.nuspec --outputdirectory $OutputDirectory
  if ($LASTEXITCODE -ne 0) {
    throw "choco pack failed with exit code $LASTEXITCODE"
  }
}
finally {
  Pop-Location
  Remove-Item -Path $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Join-Path $OutputDirectory "primedictate.$PackageVersion.nupkg"