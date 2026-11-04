---
title: 'Adaptive Overlay dot'
type: 'feature'
ticket: '15'
created: '2026-11-04'
status: 'built'
baseline_revision: '54cf952'
route: 'full'
route_source: 'auto'
risk: 'medium'
context: ['{project-root}/CLAUDE.md', '{project-root}/_bmad-output/initiative-replay-buffer/research/overlay-presentation-options.md']
warnings: []
deferred: []
---

<intent-contract>

## Intent

**Problem:** Story 5.8 measured a borderless game dropping from independent flip to Composed: Flip (about 7.5 % FPS) while the Overlay's dot shows. The cause is that the PC has no hardware overlay planes now (`dxdiag` MPO MaxPlanes 1, `SupportsOverlays` false on both outputs). With no planes, DWM must compose anything drawn over a borderless game, whatever kind of window it is.

**Approach:** the builder's decision of 2026-11-04, the research's option E. Ask DXGI whether the Target Game's output has overlay planes. If it does, show the dot always (FR-6). If it doesn't, show the dot and the mic-muted icon for 3 s after each change. A Global `overlayDot` key (`auto | always | onChange`, default `auto`) lets the builder override this. The tray icon carries the capture state in every mode.

## Boundaries & Constraints

**Always:**
- The DXGI query lives in `Platform.Desktop` (AD-2): `Monitors.OverlayPlanes(pid)` finds the `IDXGIOutput` whose `Monitor` is the Target Game's monitor (the primary monitor when there's no Target) and returns `IDXGIOutput2::SupportsOverlays`, or null when DXGI can't say. Null is treated as planes present, which keeps FR-6.
- The view model asks once per Target Game process and again after `SystemEvents.DisplaySettingsChanged`, never on every refresh.
- `overlayDot` is additive (AD-22): an `OverlayDotMode` enum, a Global-only `Immediate` key at the catalog's end, and `EffectiveSettings.OverlayDot`. 5.9 and 5.11 are building at the same time; neither adds a key, so there should be no conflict. A merge only appends to `Keys.All` and the KeysTests list.
- Only the dot and the mic-muted icon follow the mode. The saving icon, the red X and the queued messages are unchanged.
- The tray icon is grey "R" while not capturing, red "R" while the Replay Buffer runs, and red "●" while recording. The tooltip names the state.

**Never:**
- No change to the Overlay window's styles, placement or capture exclusion.
- No GPU or real-window test in the default run.

## Code Map

- `src/ReplayBuffer.Contracts/Settings/SettingValues.cs`, `Keys.cs`, `SettingsJsonContext.cs`, `SettingsDocument.cs` -- `OverlayDotMode` and the `overlayDot` key.
- `src/ReplayBuffer.Core/Settings/SettingsResolver.cs` -- resolves `OverlayDot`.
- `src/ReplayBuffer.Platform.Desktop/Monitors.cs`, `NativeMethods.txt` -- `OverlayPlanes`.
- `src/ReplayBuffer.App/Overlay/OverlayViewModel.cs` -- `DotFor`, `DotAlways`, `DotOnChangeDuration`, `DisplaysChanged`, and the injected plane query.
- `src/ReplayBuffer.App/TrayCapture.cs` (new), `ShellApp.cs` -- the tray state, the plane query and the display-change hook.
- `src/ReplayBuffer.App/Settings/GeneralSettingsPage.cs`, `GeneralSettingsView.xaml` -- the "Show the Overlay's dot" choice.

## Tasks & Acceptance

**Acceptance Criteria:**
- Given the Target Game's output reports overlay planes and `overlayDot` is `auto`, when the Replay Buffer buffers, then the dot shows for as long as it lasts.
- Given it reports none, when the dot or the mute changes, then the dot and the mic icon show for 3 s and then hide.
- Given `always` or `onChange`, then the query is overridden.
- Given a display change, then the query is asked again.
- Given any mode, then the tray icon shows the capture state.
- The saving icon, the red X and the messages are unchanged: the existing Overlay tests pass.
- The General page sends `overlayDot`.
- The DXGI query answers for the primary monitor.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings.
- `dotnet test` on `ReplayBuffer.App.Tests`, `ReplayBuffer.Contracts.Tests`, `ReplayBuffer.Core.Tests` and `ReplayBuffer.Platform.Desktop.Tests`.
- `pwsh tools/publish-engine.ps1`: Contracts and Core changed.

**Human Check (queued in the state file):** check `SupportsOverlays`, in `dxdiag /t`'s MPO MaxPlanes or with the parked `ReplayBuffer.Bench.Overlay --probe` on `feature/5.8-overlay-perf-research`, while toggling one at a time:
- the second monitor;
- RTX HDR, RTX Video Super Resolution and NVIDIA Image Scaling;
- NVIDIA Overlay, Discord and Steam.

Watch the dot in each `overlayDot` mode over a borderless game, and confirm independent flip with `bench overlay --host app --modes borderless` where planes exist. This is the research's E1.

## Implementation Notes

- The code was first written on `feature/5.8-overlay-perf-research` (`3ad1f0b`) and cherry-picked here. The tray state and three tests were added here.
- The Bench prototype (MPO probe, alternative dot windows, a non-tearing load) is parked, unbuilt, on that branch at `225de31`. It isn't part of this story.
- A test expectation was wrong at first: the view model asks for "no Target" once at construction, before the Target appears. That behaviour is correct, so the test was fixed.

## Auto Run Result

- **Verification:**
  - Release build: 0 warnings.
  - App.Tests: 103 passed, 9 Heavy skipped.
  - Contracts.Tests: 118 passed.
  - Core.Tests: 111 passed.
  - Platform.Desktop.Tests: 12 passed.
  - NativeAOT publish: no IL warnings.
- **Skipped:** Engine, Media, Platform.Audio, Platform.Capture, FFmpeg, Windows and Bench tests. Their code didn't change; Engine only re-resolves one more key through Core, which Core.Tests cover. No GPU run: the presentation-path check is the Human Check and story 5.12.
- **Review:** no separate review pass in this session; QA reviews next.

## Review Triage Log

### 2026-11-04 — QA round 1 (FAIL, 2 findings)
- `[patch]` The 3 s timer could leave the dot on if it fired before the clock reached the window's end. Fixed: `Present` re-arms for what's left whenever the window is open and no timer is pending. A test fires the timer 1 ms early.
- `[patch]` (a) An unknown plane answer (null) was cached until a display or Target change. Fixed: null is asked again after `UnknownPlanesRetry` (10 s), with a test. (b) The query runs on the UI thread. Measured on the builder's PC: 13 ms on the first, cold call, then about 0.4 ms. It runs at most once per Target change, so it stays on the UI thread, as documented at `Planes`.
- Re-verified: Release build 0 warnings; App.Tests 104 passed, 9 Heavy skipped. Only App code changed, so Contracts, Core, Platform.Desktop and the publish weren't re-run.
