<#
.SYNOPSIS
    Reduces "\GPU Engine(pid_<pid>_*)\Utilization Percentage" samples to per-type use. Dot-sourced
    by measure-path-a.ps1, and by Encore.Bench.Tests to check the reduction.

.DESCRIPTION
    A process gets one counter instance per (luid, phys, eng) engine. On the RTX 5090 a process
    gets three identical engtype_VideoEncode instances (eng_6 to eng_8), each reporting the same
    use, so summing them counted NVENC 3x (found by spike 11.1). Per sample, this takes the
    maximum over a process's instances of one type, as Task Manager does, then sums over
    processes; then the mean and max of those per-sample values.

    Each sample row has Time, Instance and Value; a row with an Error property is the sampler's
    failure.
#>

function Measure-GpuSamples($Samples) {
    $errors = @($Samples | Where-Object { $_.PSObject.Properties['Error'] })
    $rows = @($Samples | Where-Object { $_.PSObject.Properties['Instance'] })
    if ($rows.Count -eq 0) {
        $why = if ($errors.Count) { $errors[0].Error } else { 'no GPU Engine counter instances for the engine process' }
        return [pscustomobject]@{ ByType = @{}; Error = $why }
    }

    $byType = @{}
    $rows | ForEach-Object {
        $type = if ($_.Instance -match 'engtype_(.+)$') { $Matches[1] } else { 'unknown' }
        $process = if ($_.Instance -match '^pid_(\d+)_') { $Matches[1] } else { $_.Instance }
        [pscustomobject]@{ Time = ([datetime] $_.Time).Ticks; Process = $process; Type = $type; Value = [double] $_.Value }
    } | Group-Object Type | ForEach-Object {
        $perSample = @($_.Group | Group-Object Time | ForEach-Object {
                ($_.Group | Group-Object Process | ForEach-Object { ($_.Group | Measure-Object Value -Maximum).Maximum } |
                    Measure-Object -Sum).Sum
            })
        $stats = $perSample | Measure-Object -Average -Maximum
        $byType[$_.Name] = [pscustomobject]@{ MeanPercent = [math]::Round($stats.Average, 3); MaxPercent = [math]::Round($stats.Maximum, 3); Samples = $perSample.Count }
    }

    return [pscustomobject]@{ ByType = $byType; Error = $null }
}
