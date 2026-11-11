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

# Each required file's SHA-256, from the archive above, so a copy that came from anywhere but that
# archive (a cache, an old folder) is caught. Update these with $Sha256 when the build changes.
$FileSha256 = @{
    'avcodec-63.dll'   = '81ceccb166cb55f9a7a9520021a0f5d1dc3a0fa29923806a2e77e49609b28af2'
    'avdevice-63.dll'  = 'cb986ce64ce5df7259f3230905ec1f1d18f490896c47f98f2517f8e1ab9f271d'
    'avfilter-12.dll'  = '3e781d3f9c25441588a243f933ec259f5d22d4fa2f3bd654f5eee5115fbd4b9c'
    'avformat-63.dll'  = '728f3d39b2a6817e6df79f55509c806c1652d86c4fb1c5af5da4e8a44f56b3f2'
    'avutil-61.dll'    = 'c8e86e863d2c1b686e39d98e37e92ee68f00ab1108507087cbd4d360b9997945'
    'swresample-7.dll' = 'aa2084d3d789e0ceba536caec7cfb7d15bc00d237d2ebe5b6101f9be7270429d'
    'swscale-10.dll'   = '86c97ae5abbff3b72f6c58528a35d0f2ce553d4fd405167847ac76c8716cce4e'
    'ffmpeg.exe'       = '972c36ee5e1716c5b6610723e6a53f31b1cb51a09b48543c8885771de04975ab'
    'ffprobe.exe'      = '54926acffbdfce401d77cabab9013c7b05aa67050708eeef153d450735884f49'
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Target = Join-Path $RepoRoot 'native/ffmpeg'
$Stamp = Join-Path $Target '.sha256'

# The names of required files in $Folder that are missing or don't match their pinned SHA-256.
function Get-BadFiles([string] $Folder) {
    foreach ($name in $Required) {
        $path = Join-Path $Folder $name
        $isPresent = Test-Path -LiteralPath $path
        if (-not $isPresent -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $FileSha256[$name]) {
            $name
        }
    }
}

function Test-Complete {
    if (-not (Test-Path -LiteralPath $Stamp)) { return $false }
    if ((Get-Content -LiteralPath $Stamp -Raw).Trim() -ne $Sha256) { return $false }
    return @(Get-BadFiles $Target).Count -eq 0
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
    $bad = @(Get-BadFiles $Bin)
    if ($bad.Count -gt 0) {
        Write-Error "$Asset.zip's bin/ has missing or unpinned files: $($bad -join ', '). Update `$FileSha256 with `$Sha256." -ErrorAction Continue
        exit 1
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
