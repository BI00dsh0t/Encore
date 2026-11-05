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
      app            no engine from this script: the caller (bench fps --recording, story 8.7) turns
                     the real App's capture on while the run settles; PresentMon only
      capture        capture with the tracer's defaults
      -p<N>          --gpu-priority N   (e.g. capture-p7)
      -m<N>          --gpu-priority -N  (e.g. capture-m7)
      -copy          WGC plus the copy into the encoder pool, nothing encoded (--stages copy)
      -wgc           WGC frames taken and released, no copy, no encode (--stages wgc)
      -x2            BGRA -> NV12 on the D3D11 VideoProcessor (--color x2)
      -fast          --nvenc-preset p1 --nvenc-tune ll
      -thread        --encode-thread (no effect since story 1.11: the encoder thread is always on)
      -mon<mode>     spike 11.1: a second engine records a Monitor Recording beside the capture,
                     debug-capture --monitors, for the same span, at -Fps; <mode> is one of
                       one     the primary monitor alone, H.264 at its size (today's Monitor Recording)
                       hevc    every monitor composited, HEVC at the full canvas size
                       av1     every monitor composited, AV1 at the full canvas size
                       h264    every monitor composited, H.264 fitted to 4096 px a side
                       dd      as hevc, every monitor through Desktop Duplication
                     The GPU sampler then sums both engines.

    Writes into -OutDir: per run a PresentMon CSV, and for capture runs the engine's stats
    JSON, MP4, console log and GPU counter samples; then summary.csv, summary.json and
    summary.md with every PLAN.md §6.3 metric per run and the mean per condition.

    The builder starts the game first and runs this from a normal (unelevated) terminal,
    as a member of "Performance Log Users" so PresentMon can trace without elevation. The
    script never starts the game and never downloads anything.

    PresentMon traces in an ETW session named ReplayBuffer-PathA-<pid of this script>. Before
    its setup checks the script stops any leftover ReplayBuffer-PathA-<pid> session whose pid
    has no running process (a killed earlier run's, which would otherwise make PresentMon lose
    events and write no CSV), and leaves a live run's session alone with a warning. It stops
    its own session after a killed PresentMon, a failed run, and at the end.

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
    [switch] $NoGpuSampler,
    # Spike 11.1 (-mon<mode>): the second engine's bitrate for a composited canvas.
    [int] $MonitorBitrate = 40000
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# pwsh -File passes "a,b" as one string: accept comma lists either way.
$Conditions = @($Conditions | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'measure-gpu.ps1')
$WarmUpSeconds = 10   # CaptureStatsCollector.WarmUp: the engine leaves it out of its rates
$EngineTailSeconds = 5 # the engine keeps capturing a little past PresentMon's window

function Fail([string] $Message) {
    $host.UI.WriteErrorLine($Message)
    exit 1
}

# Spike 11.1: the second engine's debug-capture options for a -mon<mode> suffix.
function Resolve-MonitorOptions([string] $Mode, [string] $Name) {
    $all = Get-MonitorCanvas
    $bitrate = @('--bitrate', [string] $MonitorBitrate)
    switch ($Mode) {
        'one' { return @('--monitors', $all.Primary, '--codec', 'h264') }
        'hevc' { return @('--monitors', 'all', '--codec', 'hevc', '--size', $all.Size) + $bitrate }
        'av1' { return @('--monitors', 'all', '--codec', 'av1', '--size', $all.Size) + $bitrate }
        'h264' { return @('--monitors', 'all', '--codec', 'h264') + $bitrate }
        'dd' { return @('--monitors', 'all', '--monitor-api', 'dd', '--codec', 'hevc', '--size', $all.Size) + $bitrate }
        default { Fail "Unknown monitor mode '$Mode' in condition '$Name'. Use -monone, -monhevc, -monav1, -monh264 or -mondd." }
    }
}

# Spike 11.1: the primary monitor's device name and the physical bounding box of every monitor
# (even sides), from each display's current mode, whatever this process's DPI awareness.
function Get-MonitorCanvas {
    if (-not ('ReplayBuffer.Displays' -as [type])) {
        Add-Type -Namespace ReplayBuffer -Name Displays -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public struct DEVMODE {
    [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
    public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
    public int dmPositionX, dmPositionY; public int dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
    public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
}
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE devMode);
'@
    }

    Add-Type -AssemblyName System.Windows.Forms
    $left = [int]::MaxValue; $top = [int]::MaxValue; $right = [int]::MinValue; $bottom = [int]::MinValue
    $primary = $null
    foreach ($screen in [System.Windows.Forms.Screen]::AllScreens) {
        $mode = New-Object ReplayBuffer.Displays+DEVMODE
        $mode.dmSize = [Runtime.InteropServices.Marshal]::SizeOf($mode)
        if (-not [ReplayBuffer.Displays]::EnumDisplaySettings($screen.DeviceName, -1, [ref] $mode)) { continue }
        if ($screen.Primary) { $primary = $screen.DeviceName }
        $left = [math]::Min($left, $mode.dmPositionX); $top = [math]::Min($top, $mode.dmPositionY)
        $right = [math]::Max($right, $mode.dmPositionX + $mode.dmPelsWidth); $bottom = [math]::Max($bottom, $mode.dmPositionY + $mode.dmPelsHeight)
    }

    return [pscustomobject]@{ Primary = $primary; Size = ('{0}x{1}' -f (($right - $left) -band -2), (($bottom - $top) -band -2)) }
}

function Resolve-Condition([string] $Name) {
    $condition = [pscustomobject]@{ Name = $Name; Capture = $false; Priority = $null; Stages = $null; EngineOptions = @(); MonitorOptions = $null }
    if ($Name -in 'baseline', 'app') { return $condition }

    $parts = $Name -split '-'
    if ($parts[0] -ne 'capture') {
        Fail "Unknown condition '$Name'. Use baseline, app, or capture with suffixes -p<0-7>, -m<0-7>, -copy, -wgc, -x2, -fast, -thread."
    }

    $condition.Capture = $true
    $seen = @{}
    foreach ($suffix in $parts | Select-Object -Skip 1) {
        $kind = if ($suffix -match '^[pm][0-7]$') { 'priority' } elseif ($suffix -in 'copy', 'wgc') { 'stages' } elseif ($suffix -like 'mon*') { 'monitor' } else { $suffix }
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
            '^mon' { $condition.MonitorOptions = Resolve-MonitorOptions $suffix.Substring(3) $Name }
            default { Fail "Unknown suffix '$suffix' in condition '$Name'. Use -p<0-7>, -m<0-7>, -copy, -wgc, -x2, -fast, -thread or -mon<mode>." }
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

# --- ETW sessions --------------------------------------------------------------------------

$SessionPrefix = 'ReplayBuffer-PathA-'
$OwnSession = "$SessionPrefix$PID"

# The running ReplayBuffer-PathA-<pid> sessions, from logman query -ets.
function Get-PathASessions {
    $lines = try { @(& logman query -ets 2>&1) } catch { @("$_") }
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not list ETW sessions, so leftover $($SessionPrefix)<pid> sessions were not checked: $("$lines".Trim())"
        return
    }
    foreach ($line in $lines) {
        if ("$line" -match "^\s*($([regex]::Escape($SessionPrefix))(\d+))\s") {
            [pscustomobject]@{ Name = $Matches[1]; Pid = [int] $Matches[2] }
        }
    }
}

function Stop-EtwSession([string] $Name) {
    $stopped = $false
    $out = try {
        $lines = & logman stop $Name -ets 2>&1
        $stopped = $LASTEXITCODE -eq 0
        $lines
    }
    catch { "$_" }
    return [pscustomobject]@{ Stopped = $stopped; Output = "$out".Trim() }
}

# A killed run leaves its session running, and PresentMon's --stop_existing_session only clears a
# session of its own name: stop the ones whose script is gone, and leave a live run's alone.
function Clear-StaleSessions {
    foreach ($session in @(Get-PathASessions)) {
        if ($session.Pid -ne $PID -and (Get-Process -Id $session.Pid -ErrorAction SilentlyContinue)) {
            Write-Warning "ETW session $($session.Name) belongs to running process $($session.Pid); left alone. If that is another measurement, PresentMon runs may lose events."
            continue
        }

        $result = Stop-EtwSession $session.Name
        if ($result.Stopped) {
            Write-Host "Stopped stale ETW session $($session.Name) (no process $($session.Pid))."
        }
        else {
            Write-Warning "Could not stop stale ETW session $($session.Name): $($result.Output)"
        }
    }
}

# Best effort: this run's own session, if it is still running (a killed PresentMon leaves it).
function Stop-OwnSession {
    try {
        if (@(Get-PathASessions | Where-Object { $_.Name -eq $OwnSession }).Count) {
            $null = Stop-EtwSession $OwnSession
        }
    }
    catch {
    }
}

Clear-StaleSessions

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

function Start-GpuSampler([int[]] $EnginePid, [int] $Samples) {
    Start-Job -ArgumentList $EnginePid, $Samples -ScriptBlock {
        param($EnginePid, $Samples)
        $ErrorActionPreference = 'Stop'
        try {
            $path = @($EnginePid | ForEach-Object { "\GPU Engine(pid_$($_)_*)\Utilization Percentage" })
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
    $monitorProcess = $null
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

            # Spike 11.1: the Monitor Recording's engine, beside the capture, ending with it.
            if ($condition.MonitorOptions) {
                $monitorArgs = @('debug-capture') + $condition.MonitorOptions + @(
                    '--seconds', [string] ($Run.Seconds + $EngineTailSeconds),
                    '--output', (Join-Path $OutDir "$tag-monitors.mp4"), '--stats', (Join-Path $OutDir "$tag-monitors.json"))
                if ($OutputDevice) { $monitorArgs += @('--output-device', $OutputDevice) }
                if ($InputDevice) { $monitorArgs += @('--input-device', $InputDevice) }
                if ($Fps -gt 0) { $monitorArgs += @('--fps', [string] $Fps) }
                Write-Host "[$tag] monitor engine $($monitorArgs -join ' ')"
                $monitorProcess = Start-Process -FilePath $Engine -ArgumentList (Format-Arguments $monitorArgs) `
                    -RedirectStandardOutput (Join-Path $OutDir "$tag-monitors.out.log") -RedirectStandardError (Join-Path $OutDir "$tag-monitors.err.log") `
                    -NoNewWindow -PassThru
                $null = $monitorProcess.Handle
                Start-Sleep -Seconds 3
                if ($monitorProcess.HasExited) {
                    $err = Get-Content (Join-Path $OutDir "$tag-monitors.err.log") -Raw -ErrorAction SilentlyContinue
                    throw "[$tag] the monitor engine exited early with code $($monitorProcess.ExitCode): $("$err".Trim())"
                }
            }

            if (-not $NoGpuSampler) {
                $pids = @($engineProcess.Id) + @(if ($monitorProcess) { $monitorProcess.Id })
                $sampler = Start-GpuSampler -EnginePid $pids -Samples $Run.Seconds
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
            Stop-OwnSession
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

        $monitorExit = $null
        if ($monitorProcess) {
            if (-not $monitorProcess.WaitForExit(($EngineTailSeconds + 60) * 1000)) {
                $monitorProcess.Kill()
                Write-Warning "[$tag] the monitor engine did not stop on its own and was killed."
            }

            $monitorExit = $monitorProcess.ExitCode
        }

        $stats = if ($condition.Capture -and (Test-Path $statsPath)) { Get-Content $statsPath -Raw | ConvertFrom-Json } else { $null }
        return [pscustomobject]@{
            Tag = $tag; Condition = $condition.Name; Pass = $Run.Pass; Seconds = $Run.Seconds
            Fps = Measure-PresentMon $csv; Gpu = $gpu; Stats = $stats; EngineExit = $engineExit; MonitorExit = $monitorExit
        }
    }
    catch {
        # Leave nothing running: a later run, or the builder, must not find a stray engine.
        if ($engineProcess -and -not $engineProcess.HasExited) { $engineProcess.Kill() }
        if ($monitorProcess -and -not $monitorProcess.HasExited) { $monitorProcess.Kill() }
        if ($pm -and -not $pm.HasExited) { $pm.Kill() }
        if ($sampler) { Remove-Job -Job $sampler -Force -ErrorAction SilentlyContinue }
        Stop-OwnSession
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
    Stop-OwnSession
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
        'GPU engine use is the engine processes only (game engine, plus the monitor engine in -mon conditions): per 1 s sample, the maximum over each process''s engines of a type, summed over processes (a process gets three identical video-encode instances; before 2026-11-04 they were summed too, 3x too high) (Get-Counter "\GPU Engine(pid_<engine>_*)\Utilization Percentage").'
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
