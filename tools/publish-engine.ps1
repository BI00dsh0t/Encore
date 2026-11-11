#Requires -Version 7
<#
.SYNOPSIS
    Publishes the engine with NativeAOT (win-x64, Release) into publish/engine/.

.DESCRIPTION
    Runs dotnet publish on Encore.Engine. Warnings are errors, and ILC
    inherits that, so any IL2xxx/IL3xxx trim or AOT warning fails the publish.
    The FFmpeg DLLs land beside the exe. The native linker step looks for
    vswhere.exe on PATH, so the Visual Studio Installer folder is added when it
    is missing. To run the engine tests against the published exe, set
    REPLAYBUFFER_ENGINE_DIR to the output folder.
#>
[CmdletBinding()]
param(
    # Where the published engine goes.
    [string] $Output = (Join-Path $PSScriptRoot '..' 'publish' 'engine'),

    # The version stamped on the engine; the release workflow passes the tag's.
    [string] $Version = '0.0.0-dev'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Project = Join-Path $Root 'src' 'Encore.Engine' 'Encore.Engine.csproj'

if (-not (Test-Path (Join-Path $Root 'native' 'ffmpeg' 'avcodec-63.dll'))) {
    throw "native/ffmpeg/ is missing; run 'pwsh tools/fetch-ffmpeg.ps1' first."
}

if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
    $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio' 'Installer'
    if (-not (Test-Path (Join-Path $installer 'vswhere.exe'))) {
        throw "vswhere.exe was not found on PATH or in '$installer'; NativeAOT needs the Visual Studio C++ build tools."
    }

    $env:PATH = "$env:PATH;$installer"
}

dotnet publish $Project -c Release -r win-x64 -o $Output -p:Version=$Version
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exe = Get-Item (Join-Path $Output 'Encore.Engine.exe')
Write-Host ("Published {0} ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB))
