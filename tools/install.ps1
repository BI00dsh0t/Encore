#Requires -Version 7
<#
.SYNOPSIS
    Installs the folder tools/publish.ps1 composed into %LOCALAPPDATA%\Programs\ReplayBuffer and
    starts the App.

.DESCRIPTION
    Checks the source layout, refuses while any process runs from the install folder, mirrors the
    source into it with robocopy /MIR (files the source doesn't have are removed), then starts
    ReplayBuffer.App.exe from there. Nothing needs elevation: the folder and the Run value the App
    writes for Start with Windows are per user. There is no installer, uninstaller or self-update.

    To update an install:
      1. Quit ReplayBuffer from the tray.
      2. pwsh tools/publish.ps1
      3. pwsh tools/install.ps1

    To uninstall, turn Start with Windows off, Quit from the tray, and delete the install folder.
#>
[CmdletBinding()]
param(
    # The folder tools/publish.ps1 composed.
    [string] $Source = (Join-Path $PSScriptRoot '..' 'publish' 'ReplayBuffer'),

    # The install folder. The App is the installed instance only under %LOCALAPPDATA%\Programs\ReplayBuffer.
    [string] $Destination = (Join-Path $env:LOCALAPPDATA 'Programs' 'ReplayBuffer'),

    # Don't start the App after copying.
    [switch] $NoLaunch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Source = [IO.Path]::GetFullPath($Source)
$Destination = [IO.Path]::GetFullPath($Destination)
$App = 'ReplayBuffer.App.exe'

foreach ($name in @($App, 'ReplayBuffer.Engine.exe', 'avcodec-63.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $Source $name))) {
        throw "$Source has no $name; run 'pwsh tools/publish.ps1' first."
    }
}

$prefix = $Destination.TrimEnd('\') + '\'
$running = @(Get-Process | Where-Object {
        $path = $_.Path
        $path -and $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
    })
if ($running.Count -gt 0) {
    $list = ($running | ForEach-Object { "$($_.ProcessName) ($($_.Id))" }) -join ', '
    throw "ReplayBuffer is running from $Destination ($list). Quit ReplayBuffer from the tray, then run again."
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
