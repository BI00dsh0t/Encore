#Requires -Version 7
<#
.SYNOPSIS
    Composes the installable folder, publish/Encore/: the App, the NativeAOT engine and the
    FFmpeg DLLs side by side.

.DESCRIPTION
    Runs tools/publish-engine.ps1 into publish/engine/, then publishes the WPF App
    framework-dependent (Release, win-x64) into the output folder, copies Encore.Engine.exe
    and the DLLs beside it from the engine output, and deletes every .pdb. ffmpeg.exe and ffprobe.exe
    support clip creation; ffplay.exe is excluded. The layout is
    checked and listed at the end. Install the result with tools/install.ps1.
#>
[CmdletBinding()]
param(
    # Where the composed folder goes. It is emptied first.
    [string] $Output = (Join-Path $PSScriptRoot '..' 'publish' 'Encore')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = Resolve-Path (Join-Path $PSScriptRoot '..')
$AppProject = Join-Path $Root 'src' 'Encore.App' 'Encore.App.csproj'
$EngineOutput = Join-Path $Root 'publish' 'engine'
$Output = [IO.Path]::GetFullPath($Output)

# The libraries the engine loads (tools/fetch-ffmpeg.ps1).
$FFmpegDlls = @(
    'avcodec-63.dll',
    'avdevice-63.dll',
    'avfilter-12.dll',
    'avformat-63.dll',
    'avutil-61.dll',
    'swresample-7.dll',
    'swscale-10.dll'
)
$Exes = @('Encore.App.exe', 'Encore.Engine.exe', 'ffmpeg.exe', 'ffprobe.exe')

& (Join-Path $PSScriptRoot 'publish-engine.ps1') -Output $EngineOutput

if (Test-Path -LiteralPath $Output) {
    Remove-Item -LiteralPath $Output -Recurse -Force
}

# The App references the engine for build order only (AD-2); with a RuntimeIdentifier the engine is
# a self-contained AOT exe, which the SDK otherwise refuses beside a framework-dependent App (NETSDK1151).
dotnet publish $AppProject -c Release -r win-x64 --self-contained false -o $Output `
    -p:ValidateExecutableReferencesMatchSelfContained=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $EngineOutput 'Encore.Engine.exe') -Destination $Output
Copy-Item -Path (Join-Path $EngineOutput '*.dll') -Destination $Output
foreach ($tool in @('ffmpeg.exe', 'ffprobe.exe')) {
    Copy-Item -LiteralPath (Join-Path $EngineOutput $tool) -Destination $Output
}
# The JIT engine's content flows into the App's publish through the build-order reference: the
# Unused FFplay and JIT runtime files, which the AOT exe doesn't use.
foreach ($name in @('ffplay.exe', 'Encore.Engine.runtimeconfig.json', 'Encore.Engine.deps.json', 'Encore.Engine.dll')) {
    Remove-Item -LiteralPath (Join-Path $Output $name) -Force -ErrorAction SilentlyContinue
}
Get-ChildItem -LiteralPath $Output -Filter '*.pdb' -File -Recurse | Remove-Item -Force

$missing = @($Exes + $FFmpegDlls | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Output $_)) })
if ($missing.Count -gt 0) {
    throw "The published folder is missing: $($missing -join ', ')."
}

$extraExes = @(Get-ChildItem -LiteralPath $Output -Filter '*.exe' -File -Recurse |
    Where-Object { $_.DirectoryName -ne $Output.TrimEnd('\') -or $_.Name -notin $Exes })
if ($extraExes.Count -gt 0) {
    throw "The published folder has unexpected executables: $($extraExes.FullName -join ', ')."
}

$pdbs = @(Get-ChildItem -LiteralPath $Output -Filter '*.pdb' -File -Recurse)
if ($pdbs.Count -gt 0) {
    throw "The published folder still has PDBs: $($pdbs.FullName -join ', ')."
}

$files = Get-ChildItem -LiteralPath $Output -File -Recurse | Sort-Object FullName
foreach ($file in $files) {
    Write-Host ("{0,10:N0} KB  {1}" -f ($file.Length / 1KB), [IO.Path]::GetRelativePath($Output, $file.FullName))
}
Write-Host ("Published {0}: {1} files, {2:N1} MB" -f $Output, $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))
