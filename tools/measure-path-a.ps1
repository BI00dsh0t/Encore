#Requires -Version 7
<#
.SYNOPSIS
    Measures Path A (story 1.2): the game's FPS with and without the tracer capturing it.

.DESCRIPTION
    For each condition, records the game's presents with PresentMon for -Seconds. Capture
    conditions also run the engine's debug-capture with --stats (and --gpu-priority when the
    condition names one) and sample the engine's GPU engine use, by engine type, with
    Get-Counter. Conditions run in a balanced order (A B C D, then D C B A, ...) so thermal
    drift and order effects fall evenly on every condition.

    Condition names: baseline, or capture followed by any of these suffixes, in any order
    (e.g. capture-x2-fast-thread):
      baseline       no capture; the game alone
      capture        capture with the tracer's defaults
      -p<N>          --gpu-priority N   (e.g. capture-p7)
      -m<N>          --gpu-priority -N  (e.g. capture-m7)
      -copy          WGC plus the copy into the encoder pool, nothing encoded (--stages copy)
      -wgc           WGC frames taken and released, no copy, no encode (--stages wgc)
      -x2            BGRA -> NV12 on the D3D11 VideoProcessor (--color x2)
      -fast          --nvenc-preset p1 --nvenc-tune ll
      -thread        --encode-thread (no effect since story 1.11: the encoder thread is always on)

    Writes into -OutDir: per run a PresentMon CSV, and for capture runs the engine's stats
    JSON, MP4, console log and GPU counter samples; then summary.csv, summary.json and
    summary.md with every PLAN.md §6.3 metric per run and the mean per condition.

    The builder starts the game first and runs this from a normal (unelevated) terminal,
    as a member of "Performance Log Users" so PresentMon can trace without elevation. The
    script never starts the game and never downloads anything.

.EXAMPLE
    pwsh tools/measure-path-a.ps1 -PresentMon C:\tools\PresentMon-2.6.0-x64.exe -OutputDevice Game -InputDevice "BEACN Mic"

.EXAMPLE
    # The 30 min run of the chosen condition, for drops and GC, without the interleaved passes.
    pwsh tools/measure-path-a.ps1 -Repeats 0 -FinalCondition capture-p0 -FinalSeconds 1800
#>
[CmdletBinding()]
param(
    # The game's process name, without .exe.
    [string] $Game = 'Cyberpunk2077',

    # The conditions of one pass, in order.
    [string[]] $Conditions = @('baseline', 'capture-p0', 'capture-p7', 'capture-m7'),

    # Seconds PresentMon records per run (the engine's steady state covers the same span).
    [ValidateRange(10, 7200)]
    [int] $Seconds = 300,

    # Passes over -Conditions; even passes run in reverse order.
    [ValidateRange(0, 20)]
    [int] $Repeats = 2,

    # After the passes, one long run of this condition (e.g. capture-p0). Empty: none.
    [string] $FinalCondition = '',

    [ValidateRange(10, 14400)]
    [int] $FinalSeconds = 1800,

    # Pause before each run, so the GPU settles after the previous one.
    [ValidateRange(0, 600)]
    [int] $SettleSeconds = 20,

    # Passed to debug-capture as --output-device / --input-device when given.
    [string] $OutputDevice = '',
    [string] $InputDevice = '',

    # Passed to every capture condition's debug-capture as --size / --fps / --bitrate when given
    # (story 1.14 runs 1920x1080 at 60 fps, CBR 20000 kbps). Empty / 0: the engine's own defaults.
    [ValidatePattern('^(\d+x\d+)?$')]
    [string] $Size = '',

    [ValidateRange(0, 120)]
    [int] $Fps = 0,

    [ValidateRange(0, 1000000)]
    [int] $Bitrate = 0,

    # The PresentMon CLI. Default: the newest native/presentmon/PresentMon-*.exe under the repo root.
    [string] $PresentMon = '',

    # The engine executable. Default: the newest built ReplayBuffer.Engine.exe under src/ReplayBuffer.Engine/bin.
    [string] $Engine = '',

    # Where the run files and the summary go. Default: measurements/path-a-<timestamp> under the repo root.
    [string] $OutDir = '',

    # Skip the 1 s GPU Engine counter sampling: querying those counters can itself make a game stutter.
    [switch] $NoGpuSampler
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# pwsh -File passes "a,b" as one string: accept comma lists either way.
$Conditions = @($Conditions | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$WarmUpSeconds = 10   # CaptureStatsCollector.WarmUp: the engine leaves it out of its rates
$EngineTailSeconds = 5 # the engine keeps capturing a little past PresentMon's window

function Fail([string] $Message) {
    $host.UI.WriteErrorLine($Message)
    exit 1
}

function Resolve-Condition([string] $Name) {
    $condition = [pscustomobject]@{ Name = $Name; Capture = $false; Priority = $null; Stages = $null; EngineOptions = @() }
    if ($Name -eq 'baseline') { return $condition }

    $parts = $Name -split '-'
    if ($parts[0] -ne 'capture') {
        Fail "Unknown condition '$Name'. Use baseline, or capture with suffixes -p<0-7>, -m<0-7>, -copy, -wgc, -x2, -fast, -thread."
    }

    $condition.Capture = $true
    $seen = @{}
    foreach ($suffix in $parts | Select-Object -Skip 1) {
        $kind = if ($suffix -match '^[pm][0-7]$') { 'priority' } elseif ($suffix -in 'copy', 'wgc') { 'stages' } else { $suffix }
        if ($seen.ContainsKey($kind)) { Fail "Condition '$Name' gives '$($seen[$kind])' and '$suffix'; use one." }
        $seen[$kind] = $suffix

        switch -Regex ($suffix) {
            '^[pm][0-7]$' {
                $value = [int] $suffix.Substring(1)
                $condition.Priority = if ($suffix[0] -eq 'm') { -$value } else { $value }
            }
            '^(copy|wgc)$' { $condition.Stages = $suffix }
            '^x2$' { $condition.EngineOptions += @('--color', 'x2') }
            '^fast$' { $condition.EngineOptions += @('--nvenc-preset', 'p1', '--nvenc-tune', 'll') }
            '^thread$' { $condition.EngineOptions += @('--encode-thread') }
            default { Fail "Unknown suffix '$suffix' in condition '$Name'. Use -p<0-7>, -m<0-7>, -copy, -wgc, -x2, -fast or -thread." }
        }
    }

    return $condition
}

# Start-Process joins -ArgumentList with spaces; quote the arguments that contain any.
function Format-Arguments([string[]] $Arguments) {
    return $Arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
}

function Get-Percentile([double[]] $Sorted, [double] $P) {
    if ($Sorted.Count -eq 0) { return $null }
    $rank = [math]::Max(1, [math]::Ceiling($P / 100 * $Sorted.Count))
    return $Sorted[[int] $rank - 1]
}

# --- Preconditions -----------------------------------------------------------------------

$principal = [Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail 'This terminal is elevated. Run the measurement unelevated: the engine must run as it will in use (AD-7). Join "Performance Log Users" so PresentMon can trace without elevation.'
}

if (-not $PresentMon) {
    $found = Get-ChildItem -Path (Join-Path $RepoRoot 'native/presentmon') -Filter 'PresentMon-*.exe' -File -ErrorAction SilentlyContinue |
        Sort-Object { try { [version] ($_.BaseName -replace '^PresentMon-([\d.]+).*$', '$1') } catch { [version] '0.0' } }, LastWriteTime |
        Select-Object -Last 1
    if (-not $found) {
        Fail "PresentMon not found: no native/presentmon/PresentMon-*.exe under $RepoRoot. Download the PresentMon CLI there, or pass -PresentMon <path to PresentMon.exe>."
    }

    $PresentMon = $found.FullName
}
elseif (-not (Test-Path -LiteralPath $PresentMon -PathType Leaf)) {
    Fail "PresentMon not found at '$PresentMon'."
}

$plan = @()
for ($pass = 1; $pass -le $Repeats; $pass++) {
    $order = @($Conditions)
    if ($pass % 2 -eq 0) { [array]::Reverse($order) }
    foreach ($name in $order) {
        $plan += [pscustomobject]@{ Condition = (Resolve-Condition $name); Pass = $pass; Seconds = $Seconds }
    }
}

if ($FinalCondition) {
    $plan += [pscustomobject]@{ Condition = (Resolve-Condition $FinalCondition); Pass = 'final'; Seconds = $FinalSeconds }
}

if ($plan.Count -eq 0) {
    Fail 'Nothing to run: -Repeats is 0 and no -FinalCondition was given.'
}

$needsEngine = @($plan | Where-Object { $_.Condition.Capture }).Count -gt 0
if ($needsEngine) {
    if (-not $Engine) {
        # Only a Release build: Debug code skews the engine's CPU and allocation figures.
        $built = Get-ChildItem -Path (Join-Path $RepoRoot 'src/ReplayBuffer.Engine/bin/Release') -Filter 'ReplayBuffer.Engine.exe' -File -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime | Select-Object -Last 1
        if (-not $built) {
            Fail 'No Release engine build. Run dotnet build ReplayBuffer.slnx -c Release, or pass -Engine <path>.'
        }

        $Engine = $built.FullName
    }
    elseif (-not (Test-Path -LiteralPath $Engine -PathType Leaf)) {
        Fail "The engine is not at '$Engine'."
    }
}

$GameMissing = "The game process '$Game' is not running (or has no main window). Start the game, get to the test spot, then run this again."

function Get-GameProcess {
    return Get-Process -Name $Game -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
}

$gameProcess = Get-GameProcess
if (-not $gameProcess) { Fail $GameMissing }

if (-not $OutDir) {
    $OutDir = Join-Path $RepoRoot ('measurements/path-a-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$engineInfo = $null
if ($needsEngine) {
    $engineDir = Split-Path $Engine -Parent
    $engineInfo = [pscustomobject]@{
        Path = $Engine
        SizeBytes = (Get-Item $Engine).Length
        # A NativeAOT publish is one native exe; a normal build has the managed dll beside it.
        NativeAot = -not (Test-Path (Join-Path $engineDir 'ReplayBuffer.Engine.dll'))
    }
}

$interopLines = (Get-ChildItem -Path (Join-Path $RepoRoot 'src') -Directory -Filter 'ReplayBuffer.Platform.*' |
    Get-ChildItem -Filter '*.cs' -File -Recurse | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
    Get-Content | Measure-Object -Line).Lines

Write-Host "Game:       $($gameProcess.ProcessName) (pid $($gameProcess.Id), window 0x$('{0:X}' -f [int64] $gameProcess.MainWindowHandle))"
Write-Host "PresentMon: $PresentMon"
if ($engineInfo) { Write-Host "Engine:     $Engine (NativeAOT: $($engineInfo.NativeAot))" }
Write-Host "Runs:       $($plan.Count), output in $OutDir"

# --- One run -------------------------------------------------------------------------------

function Start-GpuSampler([int] $EnginePid, [int] $Samples) {
    Start-Job -ArgumentList $EnginePid, $Samples -ScriptBlock {
        param($EnginePid, $Samples)
        $ErrorActionPreference = 'Stop'
        try {
            $path = "\GPU Engine(pid_$($EnginePid)_*)\Utilization Percentage"
            Get-Counter -Counter $path -SampleInterval 1 -MaxSamples $Samples | ForEach-Object {
                $time = $_.Timestamp
                foreach ($sample in $_.CounterSamples) {
                    [pscustomobject]@{ Time = $time; Instance = $sample.InstanceName; Value = $sample.CookedValue }
                }
            }
        }
        catch {
            [pscustomobject]@{ Error = "$($_.Exception.Message)" }
        }
    }
}

function Measure-GpuSamples($Samples) {
    $errors = @($Samples | Where-Object { $_.PSObject.Properties['Error'] })
    $rows = @($Samples | Where-Object { $_.PSObject.Properties['Instance'] })
    if ($rows.Count -eq 0) {
        $why = if ($errors.Count) { $errors[0].Error } else { 'no GPU Engine counter instances for the engine process' }
        return [pscustomobject]@{ ByType = @{}; Error = $why }
    }

    # Per sample, sum the engines of one type; then the mean and max of those sums.
    $byType = @{}
    $rows | ForEach-Object {
        $type = if ($_.Instance -match 'engtype_(.+)$') { $Matches[1] } else { 'unknown' }
        [pscustomobject]@{ Time = $_.Time; Type = $type; Value = $_.Value }
    } | Group-Object Type | ForEach-Object {
        $sums = @($_.Group | Group-Object { $_.Time.Ticks } | ForEach-Object { ($_.Group | Measure-Object Value -Sum).Sum })
        $stats = $sums | Measure-Object -Average -Maximum
        $byType[$_.Name] = [pscustomobject]@{ MeanPercent = [math]::Round($stats.Average, 3); MaxPercent = [math]::Round($stats.Maximum, 3); Samples = $sums.Count }
    }

    return [pscustomobject]@{ ByType = $byType; Error = $null }
}

function Measure-PresentMon([string] $Csv) {
    if (-not (Test-Path $Csv)) { return [pscustomobject]@{ Frames = 0; AvgFps = $null; OnePercentLowFps = $null; Error = 'PresentMon wrote no CSV' } }

    $rows = Import-Csv $Csv
    if (-not $rows) { return [pscustomobject]@{ Frames = 0; AvgFps = $null; OnePercentLowFps = $null; Error = 'PresentMon recorded no presents' } }

    # PresentMon 2.x writes FrameTime; --v1_metrics and 1.x write MsBetweenPresents.
    $columns = $rows[0].PSObject.Properties.Name
    $column = @('MsBetweenPresents', 'FrameTime') | Where-Object { $columns -contains $_ } | Select-Object -First 1
    if (-not $column) { return [pscustomobject]@{ Frames = 0; AvgFps = $null; OnePercentLowFps = $null; Error = "no frame-time column in $($columns -join ',')" } }

    $times = [System.Collections.Generic.List[double]]::new()
    foreach ($row in $rows) {
        $value = 0.0
        if ([double]::TryParse($row.$column, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref] $value) -and $value -gt 0) {
            $times.Add($value)
        }
    }

    if ($times.Count -eq 0) { return [pscustomobject]@{ Frames = 0; AvgFps = $null; OnePercentLowFps = $null; Error = "no usable $column values" } }

    $sorted = [double[]] ($times | Sort-Object)
    $mean = ($times | Measure-Object -Average).Average
    return [pscustomobject]@{
        Frames = $times.Count
        AvgFps = [math]::Round(1000 / $mean, 2)
        # 1 % low: the FPS of the 99th-percentile frame time.
        OnePercentLowFps = [math]::Round(1000 / (Get-Percentile $sorted 99), 2)
        Error = $null
    }
}

function Invoke-Run($Run, [int] $Index) {
    $condition = $Run.Condition
    $tag = '{0:D2}-{1}-{2}' -f $Index, $condition.Name, $Run.Pass
    $engineProcess = $null
    $sampler = $null
    $pm = $null
    try {
        $process = Get-GameProcess
        if (-not $process) { throw "[$tag] $GameMissing" }
        Write-Host ''
        Write-Host "[$tag] settling $SettleSeconds s ..."
        Start-Sleep -Seconds $SettleSeconds

        $statsPath = Join-Path $OutDir "$tag.json"
        $csv = Join-Path $OutDir "$tag-presentmon.csv"
        if ($condition.Capture) {
            $engineArgs = @('debug-capture', '--hwnd', ('0x{0:X}' -f [int64] $process.MainWindowHandle),
                '--seconds', [string] ($WarmUpSeconds + $Run.Seconds + $EngineTailSeconds),
                '--output', (Join-Path $OutDir "$tag.mp4"), '--stats', $statsPath)
            if ($null -ne $condition.Priority) { $engineArgs += @('--gpu-priority', [string] $condition.Priority) }
            if ($condition.Stages) { $engineArgs += @('--stages', $condition.Stages) }
            $engineArgs += $condition.EngineOptions
            if ($OutputDevice) { $engineArgs += @('--output-device', $OutputDevice) }
            if ($InputDevice) { $engineArgs += @('--input-device', $InputDevice) }
            if ($Size) { $engineArgs += @('--size', $Size) }
            if ($Fps -gt 0) { $engineArgs += @('--fps', [string] $Fps) }
            if ($Bitrate -gt 0) { $engineArgs += @('--bitrate', [string] $Bitrate) }

            Write-Host "[$tag] engine $($engineArgs -join ' ')"
            $engineProcess = Start-Process -FilePath $Engine -ArgumentList (Format-Arguments $engineArgs) `
                -RedirectStandardOutput (Join-Path $OutDir "$tag-engine.out.log") -RedirectStandardError (Join-Path $OutDir "$tag-engine.err.log") `
                -NoNewWindow -PassThru
            $null = $engineProcess.Handle # keeps ExitCode readable after exit

            # The anchor is the first frame; past the engine's warm-up, its rates are steady state.
            Start-Sleep -Seconds ($WarmUpSeconds + 1)
            if ($engineProcess.HasExited) {
                $err = Get-Content (Join-Path $OutDir "$tag-engine.err.log") -Raw -ErrorAction SilentlyContinue
                throw "[$tag] the engine exited early with code $($engineProcess.ExitCode): $("$err".Trim())"
            }

            if (-not $NoGpuSampler) {
                $sampler = Start-GpuSampler -EnginePid $engineProcess.Id -Samples $Run.Seconds
            }
        }

        Write-Host "[$tag] PresentMon $($Run.Seconds) s on $($process.ProcessName).exe ..."
        $pmArgs = @('--process_name', "$($process.ProcessName).exe", '--output_file', $csv, '--timed', [string] $Run.Seconds,
            '--terminate_after_timed', '--no_console_stats', '--stop_existing_session', '--session_name', "ReplayBuffer-PathA-$PID")
        $pm = Start-Process -FilePath $PresentMon -ArgumentList (Format-Arguments $pmArgs) -NoNewWindow -PassThru `
            -RedirectStandardOutput (Join-Path $OutDir "$tag-presentmon.out.log") -RedirectStandardError (Join-Path $OutDir "$tag-presentmon.err.log")
        $null = $pm.Handle
        if (-not $pm.WaitForExit(($Run.Seconds + 60) * 1000)) {
            $pm.Kill()
            Write-Warning "[$tag] PresentMon did not stop on its own and was killed."
        }

        $gpu = [pscustomobject]@{ ByType = @{}; Error = $(if ($NoGpuSampler) { 'GPU sampler off' } else { 'baseline: no engine' }) }
        if ($sampler) {
            $samples = Receive-Job -Job $sampler -Wait -AutoRemoveJob
            $samples | Export-Csv -NoTypeInformation -Path (Join-Path $OutDir "$tag-gpu.csv")
            $gpu = Measure-GpuSamples $samples
        }

        $engineExit = $null
        if ($engineProcess) {
            if (-not $engineProcess.WaitForExit(($EngineTailSeconds + 60) * 1000)) {
                $engineProcess.Kill()
                Write-Warning "[$tag] the engine did not stop on its own and was killed."
            }

            $engineExit = $engineProcess.ExitCode
        }

        $stats = if ($condition.Capture -and (Test-Path $statsPath)) { Get-Content $statsPath -Raw | ConvertFrom-Json } else { $null }
        return [pscustomobject]@{
            Tag = $tag; Condition = $condition.Name; Pass = $Run.Pass; Seconds = $Run.Seconds
            Fps = Measure-PresentMon $csv; Gpu = $gpu; Stats = $stats; EngineExit = $engineExit
        }
    }
    catch {
        # Leave nothing running: a later run, or the builder, must not find a stray engine.
        if ($engineProcess -and -not $engineProcess.HasExited) { $engineProcess.Kill() }
        if ($pm -and -not $pm.HasExited) { $pm.Kill() }
        if ($sampler) { Remove-Job -Job $sampler -Force -ErrorAction SilentlyContinue }
        throw
    }
}

# --- Run them -----------------------------------------------------------------------------

# Keep the PC awake and the display on for the whole session: an idle timeout that sleeps,
# blanks or locks the PC stops the game presenting. ES_CONTINUOUS holds until it is reset.
Add-Type -Namespace ReplayBuffer -Name Power -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
public static extern uint SetThreadExecutionState(uint esFlags);
'@
$EsContinuous = [uint32] 2147483648 # 0x80000000; a bare hex literal is a negative Int32
$EsSystemRequired = [uint32] 1
$EsDisplayRequired = [uint32] 2
if ([ReplayBuffer.Power]::SetThreadExecutionState($EsContinuous -bor $EsSystemRequired -bor $EsDisplayRequired) -eq 0) {
    Write-Warning 'Could not keep the PC awake; set sleep and screen lock to Never for this session.'
}

function Test-Locked {
    # LogonUI runs while the lock screen is up.
    return [bool] (Get-Process -Name LogonUI -ErrorAction SilentlyContinue)
}

# A run that fails (game gone, engine exited early, PC locked) ends the session; the runs before it are kept.
$results = @()
$aborted = $null
try {
    for ($i = 0; $i -lt $plan.Count; $i++) {
        try {
            $run = Invoke-Run $plan[$i] ($i + 1)
            if (Test-Locked) {
                throw "[$($run.Tag)] the PC locked during the run, so its figures are not kept."
            }

            $results += $run
        }
        catch {
            $aborted = "$($_.Exception.Message) Skipped the remaining $($plan.Count - $i) run(s)."
            Write-Warning $aborted
            break
        }

        $last = $results[-1]
        Write-Host ("[{0}] avg {1} fps, 1% low {2} fps{3}" -f $last.Tag, $last.Fps.AvgFps, $last.Fps.OnePercentLowFps,
            $(if ($last.Fps.Error) { " ($($last.Fps.Error))" } else { '' }))
    }
}
finally {
    [void] [ReplayBuffer.Power]::SetThreadExecutionState($EsContinuous)
}

# --- Summary ------------------------------------------------------------------------------

$baselineFps = @($results | Where-Object { $_.Condition -eq 'baseline' -and $_.Fps.AvgFps } | ForEach-Object { $_.Fps.AvgFps })
$baselineMean = if ($baselineFps.Count) { ($baselineFps | Measure-Object -Average).Average } else { $null }
$gpuTypes = @($results | ForEach-Object { $_.Gpu.ByType.Keys } | Sort-Object -Unique)

function Get-Audio($Stats, [string] $Role) {
    if (-not $Stats) { return $null }
    return $Stats.audio | Where-Object { $_.role -eq $Role } | Select-Object -First 1
}

$rows = foreach ($r in $results) {
    $s = $r.Stats
    $steady = if ($s) { $s.resources.steady } else { $null }
    $e = if ($s -and $s.PSObject.Properties['encoder']) { $s.encoder } else { $null }
    $row = [ordered]@{
        run = $r.Tag
        condition = $r.Condition
        pass = $r.Pass
        seconds = $r.Seconds
        avgFps = $r.Fps.AvgFps
        onePercentLowFps = $r.Fps.OnePercentLowFps
        fpsLossPercent = if ($baselineMean -and $r.Fps.AvgFps) { [math]::Round((1 - $r.Fps.AvgFps / $baselineMean) * 100, 2) } else { $null }
        presents = $r.Fps.Frames
        engineExit = $r.EngineExit
        gpuPriority = if ($s -and $s.gpuPriority) { "$($s.gpuPriority.requested) $($s.gpuPriority.hresult) readback=$($s.gpuPriority.readBack)" } else { $null }
        encoderStages = if ($e) { $e.stages } else { $null }
        encoderPreset = if ($e) { $e.preset } else { $null }
        encoderTune = if ($e) { $e.tune } else { $null }
        encoderColor = if ($e) { $e.color } else { $null }
        encodeThread = if ($e) { $e.encodeThread } else { $null }
        queueDrops = if ($s -and $s.video.PSObject.Properties['queueDrops']) { $s.video.queueDrops } else { $null }
        framesArrived = if ($s) { $s.video.framesArrived } else { $null }
        framesEncoded = if ($s) { $s.video.framesEncoded } else { $null }
        duplicatePercent = if ($s) { [math]::Round($s.video.duplicatePercent, 4) } else { $null }
        missedSlots = if ($s) { $s.video.missedSlots } else { $null }
        missedSlotPercent = if ($s) { [math]::Round($s.video.missedSlotPercent, 4) } else { $null }
        captureFps = if ($s) { [math]::Round($s.video.captureFps, 2) } else { $null }
        latencyP50Ms = if ($s) { $s.latency.p50Ms } else { $null }
        latencyP99Ms = if ($s) { $s.latency.p99Ms } else { $null }
        latencyMaxMs = if ($s) { [math]::Round($s.latency.maxMs, 2) } else { $null }
        cpuPercentOneCore = if ($steady) { [math]::Round($steady.cpuPercentOfOneCore, 2) } else { $null }
        cpuPercentMachine = if ($steady) { [math]::Round($steady.cpuPercentOfMachine, 3) } else { $null }
        allocBytesPerSecond = if ($steady) { [math]::Round($steady.allocatedBytesPerSecond, 0) } else { $null }
        gcSteady = if ($steady) { "$($steady.gc.gen0)/$($steady.gc.gen1)/$($steady.gc.gen2)" } else { $null }
        gcRun = if ($s) { "$($s.resources.run.gc.gen0)/$($s.resources.run.gc.gen1)/$($s.resources.run.gc.gen2)" } else { $null }
        startupToFirstFrameMs = if ($s) { [math]::Round($s.resources.startupToAnchorMs, 0) } else { $null }
    }

    foreach ($type in $gpuTypes) {
        $g = $r.Gpu.ByType[$type]
        $row["gpu_${type}_meanPercent"] = if ($g) { $g.MeanPercent } else { $null }
        $row["gpu_${type}_maxPercent"] = if ($g) { $g.MaxPercent } else { $null }
    }

    $row['gpuNote'] = $r.Gpu.Error

    foreach ($role in 'output', 'input') {
        $a = Get-Audio $s $role
        $row["${role}Device"] = if ($a) { $a.device } else { $null }
        $row["${role}Packets"] = if ($a) { $a.packets } else { $null }
        $row["${role}StampErrors"] = if ($a) { "err=$($a.stamps.timestampErrors) zero=$($a.stamps.zero) back=$($a.stamps.backwards) disc=$($a.stamps.discontinuities)" } else { $null }
        $row["${role}JitterP99Ms"] = if ($a) { $a.stamps.jitter.p99Ms } else { $null }
        $row["${role}JitterMaxMs"] = if ($a) { [math]::Round($a.stamps.jitter.maxMs, 3) } else { $null }
        $row["${role}AgeP99Ms"] = if ($a) { $a.stamps.age.p99Ms } else { $null }
        $row["${role}DriftPpm"] = if ($a) { [math]::Round($a.stamps.drift.ppm, 2) } else { $null }
        $row["${role}DriftSegmentSeconds"] = if ($a) { [math]::Round($a.stamps.drift.segmentSeconds, 1) } else { $null }
        $row["${role}Corrections"] = if ($a) { "gaps=$($a.timeline.gapFills) trims=$($a.timeline.trims) resolved=$($a.timeline.resolvedStamps) idleFill=$($a.timeline.idleFillFrames)" } else { $null }
    }

    [pscustomobject] $row
}

if (@($rows).Count) {
    $rows | Export-Csv -NoTypeInformation -Path (Join-Path $OutDir 'summary.csv')
}
else {
    Set-Content -Path (Join-Path $OutDir 'summary.csv') -Value '' -Encoding utf8
}

# Mean per condition over its interleaved runs (the final long run is reported on its own).
$numeric = @('avgFps', 'onePercentLowFps', 'fpsLossPercent', 'duplicatePercent', 'missedSlotPercent', 'latencyP50Ms', 'latencyP99Ms',
    'latencyMaxMs', 'cpuPercentOneCore', 'cpuPercentMachine', 'allocBytesPerSecond') + @($gpuTypes | ForEach-Object { "gpu_${_}_meanPercent" })
$byCondition = $rows | Where-Object { $_.pass -ne 'final' } | Group-Object condition | ForEach-Object {
    $mean = [ordered]@{ condition = $_.Name; runs = $_.Count }
    foreach ($name in $numeric) {
        $values = @($_.Group | ForEach-Object { $_.$name } | Where-Object { $null -ne $_ })
        $mean[$name] = if ($values.Count) { [math]::Round(($values | Measure-Object -Average).Average, 3) } else { $null }
    }

    [pscustomobject] $mean
}

$summary = [ordered]@{
    created = (Get-Date).ToString('o')
    game = $Game
    presentMon = $PresentMon
    engine = $engineInfo
    interopLinesOfCode = $interopLines
    baselineAvgFps = $baselineMean
    aborted = $aborted
    byCondition = @($byCondition)
    runs = @($rows)
    notes = @(
        'avgFps is 1000 / mean frame time; onePercentLowFps is 1000 / the 99th-percentile frame time (PresentMon presents).'
        'fpsLossPercent is against the mean of the baseline runs in this invocation.'
        'Engine CPU, allocations and GC rates exclude the engine''s first 10 s (warm-up); frame counts include them.'
        'Missed slots are against 60 fps; judge them against the game''s own present rate (avgFps).'
        'GPU engine use is the engine process only, summed over engines of a type per 1 s sample (Get-Counter "\GPU Engine(pid_<engine>_*)\Utilization Percentage").'
        'NativeAOT: see engine.nativeAot and engine.sizeBytes; startupToFirstFrameMs is process start to the first captured frame.'
        'Interop debugging effort is qualitative and not measured here.'
    )
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $OutDir 'summary.json') -Encoding utf8

$md = [System.Text.StringBuilder]::new()
[void] $md.AppendLine("# Path A measurement, $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
[void] $md.AppendLine()
[void] $md.AppendLine("Game ``$Game``; baseline avg FPS $([math]::Round([double] $baselineMean, 2)); engine NativeAOT: $(if ($engineInfo) { $engineInfo.NativeAot } else { 'n/a' }), size $(if ($engineInfo) { '{0:N1} MB' -f ($engineInfo.SizeBytes / 1MB) } else { 'n/a' }); interop lines: $interopLines.")
[void] $md.AppendLine()
if ($aborted) {
    [void] $md.AppendLine("**Session ended early:** $aborted")
    [void] $md.AppendLine()
}
foreach ($table in @(@{ Title = 'Mean per condition'; Rows = @($byCondition) }, @{ Title = 'Runs'; Rows = @($rows) })) {
    if ($table.Rows.Count -eq 0) { continue }
    $names = $table.Rows[0].PSObject.Properties.Name
    [void] $md.AppendLine("## $($table.Title)")
    [void] $md.AppendLine()
    [void] $md.AppendLine('| ' + ($names -join ' | ') + ' |')
    [void] $md.AppendLine('|' + (($names | ForEach-Object { '---' }) -join '|') + '|')
    foreach ($row in $table.Rows) {
        [void] $md.AppendLine('| ' + (($names | ForEach-Object { "$($row.$_)" -replace '\|', '/' }) -join ' | ') + ' |')
    }

    [void] $md.AppendLine()
}

[void] $md.AppendLine('## Notes')
[void] $md.AppendLine()
foreach ($note in $summary.notes) { [void] $md.AppendLine("- $note") }
$md.ToString() | Set-Content -Path (Join-Path $OutDir 'summary.md') -Encoding utf8

Write-Host ''
$shown = @('condition', 'runs', 'avgFps', 'onePercentLowFps', 'fpsLossPercent', 'duplicatePercent', 'latencyP99Ms', 'cpuPercentOneCore', 'allocBytesPerSecond') +
    @($gpuTypes | Where-Object { $_ -in '3d', 'copy', 'videoencode' } | ForEach-Object { "gpu_${_}_meanPercent" })
$byCondition | Format-Table -AutoSize -Property $shown | Out-String -Width 220 | Write-Host
Write-Host "Summary: $(Join-Path $OutDir 'summary.md')"
