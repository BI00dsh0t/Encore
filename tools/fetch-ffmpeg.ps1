#Requires -Version 7
<#
.SYNOPSIS
    Downloads the pinned FFmpeg shared build into native/ffmpeg/.

.DESCRIPTION
    Fetches BtbN's win64-gpl-shared-9.0 asset, verifies its SHA-256, and copies
    the archive's bin/* into native/ffmpeg/. On a hash mismatch the download is
    deleted, nothing is extracted, and the script exits non-zero. Running it
    again once native/ffmpeg/ is complete does nothing.
#>
[CmdletBinding()]
param(
    # Re-download and replace native/ffmpeg/ even when it is already complete.
    [switch] $Force,

    # Use this local copy of the archive instead of downloading it. It is still hash-checked.
    [string] $ArchivePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Release = 'autobuild-2026-09-30-13-08'
$Asset = 'ffmpeg-n9.0.2-17-g2a571b6068-win64-gpl-shared-9.0'
$Sha256 = '3da6c7b60bb9ccd73ec5b5e815ba804a0879eb362ba0e3beebce50174c022696'
$Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$Release/$Asset.zip"

# The libraries FFmpeg.AutoGen.Bindings.DynamicallyLinked 9.0.1.1 imports, plus ffprobe for the tests.
$Required = @(
    'avcodec-63.dll',
    'avdevice-63.dll',
    'avfilter-12.dll',
    'avformat-63.dll',
    'avutil-61.dll',
    'swresample-7.dll',
    'swscale-10.dll',
    'ffmpeg.exe',
    'ffprobe.exe'
)

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Target = Join-Path $RepoRoot 'native/ffmpeg'
$Stamp = Join-Path $Target '.sha256'

function Test-Complete {
    if (-not (Test-Path -LiteralPath $Stamp)) { return $false }
    if ((Get-Content -LiteralPath $Stamp -Raw).Trim() -ne $Sha256) { return $false }
    foreach ($name in $Required) {
        if (-not (Test-Path -LiteralPath (Join-Path $Target $name))) { return $false }
    }
    return $true
}

if (-not $Force -and (Test-Complete)) {
    Write-Host "FFmpeg $Asset is already in $Target."
    exit 0
}

$Work = Join-Path ([IO.Path]::GetTempPath()) ("fetch-ffmpeg-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $Work | Out-Null
try {
    $Zip = Join-Path $Work "$Asset.zip"
    if ($ArchivePath) {
        Copy-Item -LiteralPath $ArchivePath -Destination $Zip
    }
    else {
        Write-Host "Downloading $Url"
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $Url -OutFile $Zip
    }

    $actual = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256) {
        Remove-Item -LiteralPath $Zip -Force
        Write-Error "SHA-256 mismatch for $Asset.zip: expected $Sha256, got $actual. The download was deleted." -ErrorAction Continue
        exit 1
    }
    Write-Host "SHA-256 OK ($Sha256)"

    $Extracted = Join-Path $Work 'x'
    Expand-Archive -LiteralPath $Zip -DestinationPath $Extracted
    $Bin = Join-Path $Extracted "$Asset/bin"
    foreach ($name in $Required) {
        if (-not (Test-Path -LiteralPath (Join-Path $Bin $name))) {
            Write-Error "$Asset.zip has no bin/$name, which the FFmpeg.AutoGen bindings need." -ErrorAction Continue
            exit 1
        }
    }

    if (Test-Path -LiteralPath $Target) { Remove-Item -LiteralPath $Target -Recurse -Force }
    New-Item -ItemType Directory -Path $Target | Out-Null
    Copy-Item -Path (Join-Path $Bin '*') -Destination $Target
    Set-Content -LiteralPath $Stamp -Value $Sha256 -NoNewline
    Write-Host "FFmpeg $Asset is in $Target."
}
finally {
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
}
