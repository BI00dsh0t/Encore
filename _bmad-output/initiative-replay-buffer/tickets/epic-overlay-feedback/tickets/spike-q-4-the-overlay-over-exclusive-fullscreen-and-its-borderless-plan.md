---
title: 'Spike Q-4: the Overlay over exclusive fullscreen and its borderless FPS cost'
type: 'chore'
ticket: '2'
created: '2026-11-03'
status: done
baseline_revision: '515a1e5'
route: 'spike'
risk: 'high'
---

# Spike 5.2: the Overlay over exclusive fullscreen and its borderless FPS cost

## Question

PRD Q-4: (a) what the Overlay does over an exclusive-fullscreen game, and (b) whether an always-shown dot keeps a borderless game in independent flip, the fastest presentation path, or costs it FPS.

## Answer

- **(a) The Overlay shows the same way over every game. It doesn't hide on `QUNS_RUNNING_D3D_FULL_SCREEN`.** The shell reports that state for both kinds of DXGI fullscreen. The common kind on Windows 11 is a flip swap chain promoted by fullscreen optimizations. There the dot shows, the game stays in independent flip, and it costs nothing. Hiding on the QUNS state would remove the dot from exactly the games where it works. In legacy exclusive fullscreen (a blt swap chain with fullscreen optimizations off) the dot can't be seen, because DWM isn't presenting. Showing it there still did no harm: the game stayed in Hardware: Legacy Flip, stayed DXGI fullscreen and kept its frame rate. The Feedback Sounds cover those games. This replaces the epic's unattended default (a).
- **(b) An always-shown dot keeps a borderless game in independent flip, at no measurable FPS cost.** The dot shown or hidden, the load presented as Hardware Composed: Independent Flip on 100 % of frames: Windows composes the dot on a hardware overlay plane (MPO). The FPS cost was 0.08 %, inside the 0.4 % spread between the two hidden phases. FR-6's always-shown dot stands as written. The 3-second mitigation and its deviation from FR-6 aren't needed, and story 5.8 builds no mitigation.

## Method

- `ReplayBuffer.Bench overlay` (new in this spike) runs the synthetic GPU load (story 1.14, calibrated to 80 fps at 2560x1440 on the RTX 5090) in three modes:
  - `borderless`: the 1.14 load, a borderless popup with a flip-discard swap chain and tearing.
  - `fullscreen`: the same flip swap chain made DXGI fullscreen with `SetFullscreenState`. Windows runs it with fullscreen optimizations, as it runs most games' "exclusive fullscreen" today.
  - `exclusive`: a blt (DISCARD) swap chain made DXGI fullscreen, started with `__COMPAT_LAYER=DisableDXMaximizedWindowedMode` (the "Disable fullscreen optimizations" compatibility setting). This gives legacy exclusive fullscreen, as older games and games with that setting have it.
- Beside the load, `ReplayBuffer.Bench.Overlay.exe` hosts story 5.1's Overlay window: the same WPF window properties, the same `CaptureExclusion` and `OverlayWindowStyle` calls from `Platform.Desktop`, the hollow dot top-right 24 px in, and extended style `0x80800A8`, excluded from capture. Bench doesn't compile against the App (AD-2). Story 5.12 measures the App itself.
- For each mode there are three phases: the dot hidden, shown, then hidden again. Each phase settles for 10 s, then PresentMon 2.6 traces the load for 60 s. Bench records the PresentMode share, the mean and 1 % low FPS, `SHQueryUserNotificationState`, whether the load is in front, and whether the dot's window is visible. The load prints any DXGI fullscreen change after it is ready. It also rebuilds its buffers and takes fullscreen back when a flip swap chain refuses `Present` after a transition, as a game does.
- "Dot on screen" means the dot's window is visible and the load's frames go through DWM (any PresentMode except Hardware: Legacy Flip or Legacy Copy).
- The session ran at the physical console: `query session` showed `>console Active`, `SM_REMOTESESSION` was 0 and `TerminalServerSession` was False. The numbers are representative.

## Results

Full run, 2026-11-03 12:48 to 13:01. The report is `measurements/bench-5.2/overlay.md` and `overlay.json` in this worktree, with each phase's PresentMon CSV under `overlay/`.

| Mode | PresentMode, hidden / shown / hidden again | FPS hidden (mean) | FPS shown | FPS cost | Independent flip kept | Dot on screen | QUNS |
|---|---|---|---|---|---|---|---|
| borderless | Hardware Composed: Independent Flip, all three | 82.41 | 82.34 | 0.08 % | yes | yes | `QUNS_BUSY` (2) |
| fullscreen | Hardware Composed: Independent Flip, all three | 80.66 | 80.85 | −0.24 % | yes | yes | `QUNS_RUNNING_D3D_FULL_SCREEN` (3) |
| exclusive | Hardware: Legacy Flip, all three | 80.44 | 80.43 | 0.01 % | n/a (legacy) | no | `QUNS_RUNNING_D3D_FULL_SCREEN` (3) |

- Every phase presented in a single PresentMode (100 %). Neither fullscreen load reported a DXGI fullscreen change after it was ready.
- 1 % lows: borderless 59.8 / 60.2 / 57.0, fullscreen 63.8 / 63.6 / 63.3, exclusive 65.2 / 64.9 / 65.1 fps (hidden / shown / hidden again).
- Caveats from the full run:
  - Someone was using the desktop (input idle 16 ms afterwards, the Claude window in front), so the borderless and fullscreen loads weren't the foreground window in their phases. The exclusive load was.
  - The borderless load closed itself (exit 0, nothing on stderr, as on Escape or `WM_CLOSE`) 43 s into its last phase. That phase has 3563 frames instead of about 4900.
  - Another session's `ReplayBuffer.Platform.FFmpeg.Tests` started at 13:01:30, after the last phase.
- Smoke run, 5 s per phase, earlier that day, with every load in front: the same PresentModes and QUNS states in every mode and phase. The FPS cost was borderless 0.73 %, fullscreen 0.47 % and exclusive 0.17 %, within a 5-second phase's noise. So foreground doesn't change the presentation path or the QUNS state here.
- The first full attempt ended in `fullscreen` mode: the load's `Present` returned `DXGI_ERROR_INVALID_CALL` after a fullscreen transition during calibration, most likely focus taken by the active desktop. The load now recovers from that, as described under Method.

## Consequences

- **Story 5.8 (Overlay over exclusive fullscreen and the presentation path):**
  - Drop the `QUNS_RUNNING_D3D_FULL_SCREEN` hide. The Overlay behaves the same whatever the shell reports.
  - Check with the real App that showing and hiding the dot over a legacy exclusive game (`bench overlay --modes exclusive`, or a real one) never takes the game out of exclusive fullscreen.
  - The Feedback Sounds (story 5.6) remain the feedback a legacy exclusive game gets.
- **FR-6 stands:** the dot shows for as long as the state lasts, with no deviation to put to the builder.
- **Story 5.12** reruns 1.14's benchmark with the real App's Overlay and capture on. It should check the borderless PresentMode stays Hardware Composed: Independent Flip.
- **Risk to watch:** keeping independent flip depends on Windows putting the dot on a hardware overlay plane. A PC with no free plane (another overlay app, some HDR or multi-monitor setups, a different GPU) would fall back to Composed: Flip. `bench overlay` shows which, on any PC.

## Human Check (for the Human Check Queue)

- **Ticket 5.2: which of your games run in exclusive fullscreen, and does the dot show over each?** In each game you play in "exclusive fullscreen" mode, run `ReplayBuffer.Engine serve` and the Main App with the game detected. The hollow dot shows top-right in games Windows runs with fullscreen optimizations, and not at all in legacy exclusive ones. A legacy exclusive game should never drop out of fullscreen or lose focus when the dot comes and goes. To tell the two kinds apart, run PresentMon on the game: `PresentMon-2.6.0-x64.exe --process_name <game>.exe --timed 20 --terminate_after_timed --output_file game.csv` reads Hardware: Legacy Flip only for legacy exclusive. The automated proxy is this spike's synthetic loads.
- **Ticket 5.2: see the dot by eye.** Watch the top-right corner of the primary monitor while running `ReplayBuffer.Bench overlay --modes borderless,fullscreen,exclusive --seconds 20 --settle 5`. The red ring should appear in each mode's middle phase over the borderless and fullscreen loads, and never over the exclusive one. The automated proxy is PresentMon's PresentMode plus the dot window's visibility.

## Code

All in the bench. No production Overlay code changed.

- `bench/ReplayBuffer.Bench.GpuLoad`:
  - `--mode borderless|fullscreen|exclusive`.
  - DXGI fullscreen on a flip or blt swap chain.
  - `fullscreen` and `mode` on the ready line, plus `fullscreen <bool>` lines when the state changes.
  - Buffer rebuild after a refused `Present`.
  - Takes the foreground through the foreground thread's input state.
- `bench/ReplayBuffer.Bench.Overlay` (new): the stand-in for 5.1's Overlay window, driven over stdin.
- `bench/ReplayBuffer.Bench`:
  - The `overlay` command: `OverlayBenchmark`, `OverlayHostProcess` and the `PresentMonCsv` analysis.
  - `GpuLoadProcess` passes the mode, sets the compatibility layer and keeps the fullscreen lines.
- `tests/ReplayBuffer.Bench.Tests/OverlayBenchmarkTests.cs`: three tests covering the mode on the command and ready lines, a PresentMon CSV, and the summary and report.

Verification:
- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings, 0 errors.
- `dotnet test --project tests/ReplayBuffer.Bench.Tests -c Release --no-build`: 126 passed, 8 explicit Heavy tests skipped, 0 failed.
- Only Bench changed, so the other test projects and the NativeAOT publish were skipped.
