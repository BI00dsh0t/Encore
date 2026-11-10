#Requires -Version 7
<#
.SYNOPSIS
    Installs the folder tools/publish.ps1 composed into %LOCALAPPDATA%\Programs\Encore and
    starts the App.

.DESCRIPTION
    Checks the source layout, refuses while any process runs from the install folder, mirrors the
    source into it with robocopy /MIR (files the source doesn't have are removed), then starts
    Encore.App.exe from there. Nothing needs elevation: the folder and the Run value the App
    writes for Start with Windows are per user. There is no installer, uninstaller or self-update.

    To update an install:
      1. Quit Encore from the tray.
      2. pwsh tools/publish.ps1
      3. pwsh tools/install.ps1

    To uninstall, turn Start with Windows off, Quit from the tray, and delete the install folder.
#>
[CmdletBinding()]
param(
    # The folder tools/publish.ps1 composed.
    [string] $Source = (Join-Path $PSScriptRoot '..' 'publish' 'Encore'),

    # The install folder. The App is the installed instance only under %LOCALAPPDATA%\Programs\Encore.
    [string] $Destination = (Join-Path $env:LOCALAPPDATA 'Programs' 'Encore'),

    # Don't start the App after copying.
    [switch] $NoLaunch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Source = [IO.Path]::GetFullPath($Source)
$Destination = [IO.Path]::GetFullPath($Destination)
$App = 'Encore.App.exe'

# The layout tools/publish.ps1 checks: both exes and the FFmpeg DLLs the engine loads.
$Required = @(
    $App,
    'Encore.Engine.exe',
    'avcodec-63.dll',
    'avdevice-63.dll',
    'avfilter-12.dll',
    'avformat-63.dll',
    'avutil-61.dll',
    'swresample-7.dll',
    'swscale-10.dll'
)
$missing = @($Required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Source $_)) })
if ($missing.Count -gt 0) {
    throw "$Source is missing $($missing -join ', '); run 'pwsh tools/publish.ps1' first."
}

# robocopy /MIR deletes whatever the destination holds that the source doesn't; only mirror over an install.
if ((Test-Path -LiteralPath $Destination) -and
    @(Get-ChildItem -LiteralPath $Destination -Force).Count -gt 0 -and
    -not (Test-Path -LiteralPath (Join-Path $Destination $App))) {
    throw "$Destination is not empty and has no $App, so it is not a Encore install; nothing was copied."
}

$prefix = $Destination.TrimEnd('\') + '\'
$running = @(Get-Process | Where-Object {
        $path = $_.Path
        $path -and $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
    })
if ($running.Count -gt 0) {
    $list = ($running | ForEach-Object { "$($_.ProcessName) ($($_.Id))" }) -join ', '
    throw "Encore is running from $Destination ($list). Quit Encore from the tray, then run again."
}

robocopy $Source $Destination /MIR /NJH /NJS /NP /NDL
if ($LASTEXITCODE -ge 8) {
    throw "robocopy failed with exit code $LASTEXITCODE."
}
$global:LASTEXITCODE = 0

Write-Host "Installed $Destination."
if (-not $NoLaunch) {
    Start-Process -FilePath (Join-Path $Destination $App) -WorkingDirectory $Destination
    Write-Host "Started $App."
}
