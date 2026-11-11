#Requires -Version 7
<#
.SYNOPSIS
    Runs every test project's default suite, one project at a time, on a Release build.

.DESCRIPTION
    Build first: dotnet build Encore.slnx -c Release. Explicit (Heavy) tests never run here.
    A test that needs hardware the machine lacks (a GPU, an audio device, an installed
    player) skips itself, so the same suites run on a developer's PC and on a hosted runner.
    Every project runs even when one fails; the script fails at the end if any did. With
    -ResultsDirectory, each project writes a TRX report there.
#>
[CmdletBinding()]
param(
    # Where each project's TRX report goes; none are written when it is empty.
    [string] $ResultsDirectory = '',

    # How long one project's suite may run before it is stopped.
    [string] $Timeout = '10m'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = Resolve-Path (Join-Path $PSScriptRoot '..')
$projects = Get-ChildItem (Join-Path $Root 'tests') -Directory -Filter 'Encore.*.Tests' | Sort-Object Name
$failed = @()
foreach ($project in $projects) {
    $arguments = @('test', '--project', $project.FullName, '-c', 'Release', '--no-build', '--no-progress', '--timeout', $Timeout)
    if ($ResultsDirectory) {
        $arguments += '--results-directory', (Join-Path $ResultsDirectory $project.Name), '--report-xunit-trx'
    }

    Write-Host "`n$($project.Name)"
    dotnet @arguments
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Write-Host "$($project.Name) failed with exit code $code."
        $failed += $project.Name
    }
}

if ($failed.Count -gt 0) {
    throw "These test projects failed: $($failed -join ', ')."
}
