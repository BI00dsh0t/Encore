---
title: 'Tracer: capture state to the Overlay dot'
type: 'feature'
ticket: '1'
created: '2026-11-03'
status: 'built'
baseline_revision: 'cc6c2d180eb8ff3bac681ef97cf4626d70b9acf3'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** The engine already publishes `capture.stateChanged` from story 4.1's replay session, and the App folds it into `IEngineSession.State`, but nothing in the game's view shows it: the user can't tell whether the Replay Buffer is running without opening the Main App.

**Approach:** Add an Overlay to the App: an `OverlayViewModel` that derives a dot (none, hollow, solid) from `IEngineSession.State.Capture`, and an `OverlayWindow` (a `ShellWindow`, so capture-excluded) that is topmost, click-through, never activated, a tool window, and placed top-right on the Target Game's monitor (else the primary) 24 px in at that monitor's DPI. The Win32 calls (extended styles, no-activate show, monitor and DPI lookup, foreground check) live in `ReplayBuffer.Platform.Desktop`.

## Boundaries & Constraints

**Always:**
- The dot reads only `IEngineSession.State` and `Status` (AD-3, AD-15): solid red when `Recording` or `MonitorRecording` is `Running`; else hollow red when `Replay` is `Buffering` or `Draining`; else none. None too whenever `Status` isn't `Connected` (a stale snapshot shows nothing).
- Extended styles `WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`; shown with no activation; `WDA_EXCLUDEFROMCAPTURE` (via `ShellWindow`).
- The window is hidden when the dot is none, shown (without activation) otherwise; it updates on each `Changed` posted to the dispatcher, well within 1 s.
- Placement: `rcMonitor` top-right of the monitor holding the Target Game's largest visible top-level window (found by process id), else the primary monitor; 24 DIP in from the top and right, scaled by that monitor's effective DPI; positioned in physical pixels with `SetWindowPos`. The App declares PerMonitorV2 DPI awareness in an `app.manifest`.
- The Overlay closes with the App (Quit).
- Win32 in `Platform.Desktop` through CsWin32 (`NativeMethods.txt`, `allowMarshaling: false`); no callbacks needed (enumerate top-level windows with `FindWindowEx`).
- Lean tests (CLAUDE.md "Tests"). Tests that need a visible desktop (foreground/focus, screen pixels) are `[Fact(Explicit = true)]` + `[Trait("Category", "Heavy")]`.

**Never:**
- No Contracts or engine change; no saving icon, messages, mic icon or red X (later stories); no settings key for the position.
- Nothing drawn in, injected into or hooked onto the game process (NFR-3).
- The App never takes focus for the Overlay (no `Activate`, no `SetForegroundWindow` on it).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Buffering | `Replay = Buffering`, recordings off | hollow dot, window shown | No error expected |
| Draining | `Replay = Draining` | hollow dot | No error expected |
| Recording | `Recording` or `MonitorRecording` = `Running` | solid dot | No error expected |
| All off | `CaptureState.Initial` | no dot, window hidden | No error expected |
| Disconnected | `Status != Connected`, last snapshot buffering | no dot | No error expected |
| No Target Game / its window gone | `TargetGame = null`, or pid has no visible window | placed on the primary monitor | falls back, never throws |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Messages/Capture.cs` -- `CaptureState`, `ReplayState`, `RecordingState`; read only.
- `src/ReplayBuffer.Contracts/Messages/Game.cs` -- `TargetGame(ProfileId, Name, ProcessId)`: the only Target info; no window handle, so the App finds the window by pid.
- `src/ReplayBuffer.App/Engine/EngineConnection.cs:25` -- `IEngineSession` (`Status`, `State`, `Changed` on a background thread). Reuse; don't change.
- `src/ReplayBuffer.App/MainViewModel.cs` -- pattern: constructor takes `IEngineSession` and `Action<Action> post`, `engine.Changed += () => post(Refresh)`; `Observable` base for `Set`.
- `src/ReplayBuffer.App/ShellWindow.cs` -- base window applying `CaptureExclusion` in `OnSourceInitialized`; the Overlay derives from it.
- `src/ReplayBuffer.App/ShellApp.cs` -- `OnStartup` builds the model and window; `QuitAsync` closes them. Wire the Overlay here.
- `src/ReplayBuffer.App/ReplayBuffer.App.csproj` -- add `<ApplicationManifest>app.manifest</ApplicationManifest>`.
- `src/ReplayBuffer.Platform.Desktop/CaptureExclusion.cs` -- style to follow: `public static unsafe class`, `PInvoke.*`, `Win32Exception` on failure, XML doc citing AD.
- `src/ReplayBuffer.Platform.Desktop/NativeMethods.txt` -- add `GetWindowLongPtr`, `SetWindowLongPtr`, `SetWindowPos`, `ShowWindow`, `FindWindowEx`, `GetWindowThreadProcessId`, `IsWindowVisible`, `GetWindowRect`, `MonitorFromWindow`, `MonitorFromPoint`, `GetMonitorInfo`, `GetDpiForMonitor`, `GetForegroundWindow`, `WINDOW_EX_STYLE` as needed. (On x86/x64 CsWin32 maps `*LongPtr`; the build is x64.)
- `tests/ReplayBuffer.App.Tests/FakeEngineSession.cs` -- fake session: set `State`/`Status`, call `RaiseChanged()`.
- `tests/ReplayBuffer.App.Tests/Sta.cs`, `MainWindowTests.cs` -- `[Collection(nameof(WpfCollection))]`, `Sta.Run`, an off-screen `ShowActivated = false` window, `EnsureHandle`.
- `tests/ReplayBuffer.App.Tests/SupervisionTests.cs:95` -- the Heavy/Explicit attribute pattern.
- `tests/ReplayBuffer.Platform.Desktop.Tests/CaptureExclusionTests.cs` -- Desktop test style.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Platform.Desktop/OverlayWindowStyle.cs` (+ `NativeMethods.txt`) -- `Apply(hwnd)` ORs the five extended styles into `GWL_EXSTYLE`; `ExtendedStyle(hwnd)` reads them; `ShowWithoutActivation(hwnd, x, y, width, height)` via `SetWindowPos(HWND_TOPMOST, …, SWP_NOACTIVATE | SWP_SHOWWINDOW)`; `Hide(hwnd)` via `ShowWindow(SW_HIDE)`; `ForegroundWindow()` -- Win32 stays in Platform.Desktop (AD-2).
- [x] `src/ReplayBuffer.Platform.Desktop/Monitors.cs` -- `MonitorArea ForProcess(int? processId)`: largest visible top-level window of the pid → `MonitorFromWindow(NEAREST)`, else `MonitorFromPoint(0,0, PRIMARY)`; returns `rcMonitor` (physical px) and effective DPI; `static (int X, int Y, int Size) TopRight(MonitorArea, double sizeDip, double insetDip)` pure placement maths.
- [x] `src/ReplayBuffer.App/Overlay/OverlayViewModel.cs` -- `OverlayDot { None, Hollow, Solid }`; `Dot`, `TargetProcessId`; refreshes on `Changed` via `post` -- the dot rule above.
- [x] `src/ReplayBuffer.App/Overlay/OverlayWindow.cs` -- code-only `ShellWindow`: `WindowStyle.None`, `AllowsTransparency`, transparent background, `ShowActivated = false`, `ShowInTaskbar = false`, `Topmost`, not hit-testable; content a `Viewbox` with an `Ellipse` (hollow: red stroke; solid: red fill); applies `OverlayWindowStyle.Apply` in `OnSourceInitialized`; on model change: none → hide, else place with `Monitors` and show without activation; re-place on `DpiChanged`.
- [x] `src/ReplayBuffer.App/app.manifest`, `ReplayBuffer.App.csproj` -- PerMonitorV2 DPI awareness.
- [x] `src/ReplayBuffer.App/ShellApp.cs` -- create the Overlay model and window at startup; close it in `QuitAsync`.
- [x] `tests/ReplayBuffer.App.Tests/OverlayTests.cs` -- default: (1) a fake session driven off → buffering → draining → recording running → off (and disconnected) moves `Dot` through none, hollow, hollow, solid, none; (2) the created window has the five styles and `CaptureExclusion.IsApplied`. Heavy/Explicit: (3) a shown, activated helper window stays foreground (and keeps keyboard focus) while the Overlay is shown and updated; (4) a screen copy of the Overlay's rectangle over a green helper window has no red pixels (exclusion proxy for the WGC monitor capture).
- [x] `tests/ReplayBuffer.Platform.Desktop.Tests/MonitorsTests.cs` -- `TopRight` at 144 DPI on a 1920×1080 monitor; `ForProcess(null)` is the primary monitor.

**Acceptance Criteria:**
- Given the App connected to an engine, when `capture.stateChanged` moves the Replay Buffer to buffering or draining, then the Overlay shows a hollow red dot within 1 s; when a Recording or Monitor Recording runs, a solid red dot; when all are off, nothing.
- Given the Overlay window, when its handle exists, then it has `WS_EX_TOPMOST`, `WS_EX_TRANSPARENT`, `WS_EX_LAYERED`, `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW` and `WDA_EXCLUDEFROMCAPTURE`.
- Given another window in the foreground with focus, when the Overlay shows and updates, then that window keeps the foreground and focus (Heavy).
- Given the Overlay showing a dot, when the screen under it is captured, then none of its pixels appear (Heavy).
- Given a Target Game on a monitor, when the dot shows, then it sits in that monitor's top-right corner 24 DIP in at its DPI; with no Target Game, on the primary monitor's.

## Implementation Notes

- Visibility goes through WPF's own `Show()` (with `ShowActivated = false`, so `SW_SHOWNA`) and `Hide()`, so WPF's visibility state and render target follow the window. `OverlayWindowStyle.ShowWithoutActivation` places the window and raises it in the topmost band (`SWP_NOACTIVATE | SWP_SHOWWINDOW`) before `Show()`, so it never flashes at a stale spot. There's no `OverlayWindowStyle.Hide`, and so no `ShowWindow` in `NativeMethods.txt`.
- The dot is 16 DIP (`OverlayWindow.DotSizeDip`); the inset is 24 DIP (`InsetDip`). The hollow dot is a 2 DIP stroke.
- `DpiChanged` re-places through `Dispatcher.BeginInvoke`, not inside WPF's `WM_DPICHANGED` handling.
- `tests/ReplayBuffer.App.Tests` sets `UseWindowsForms` (with its global usings removed) for `System.Drawing`'s `CopyFromScreen` in Heavy test (4).
- I checked by hand that the dot renders: a throwaway test cleared the display affinity and found 194 of 256 pixels red after show, hide and show again. I removed the test afterwards. Without that check, Heavy test (4) would also pass if the dot never drew.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 6 findings — high 0, medium 1, low 4, false 1, maybe-false 0
- findings:
  - `[medium]` `[patch]` Refresh set TargetProcessId before Dot, so a snapshot that clears the Target and stops the Replay Buffer together (a game exit) placed and showed the window on the primary monitor before hiding it — fixed: `Refresh` sets `Dot` first when it goes to none and `TargetProcessId` first otherwise.
  - `[low]` `[reject]` The dot is placed only on a model or DPI change, so it doesn't follow a game moved to another monitor — real but rare; detection needs the game's window, so it exists when the Target is set; following moves needs a timer or a WinEvent hook. Left for the Overlay lane's later stories.
  - `[low]` `[reject]` A `Win32Exception` from `Monitors.ForProcess` or `SetWindowPos` would reach the dispatcher unhandled — only when the primary monitor can't be read or the App's own window is gone; the fix is a new guard.
  - `[low]` `[reject]` A minimized game window ranks by its tiny iconic rect — the dot's monitor barely matters while the game is minimized; the fix is a new branch.
  - `[false]` `[reject]` The default style test shows a topmost window and so needs a visible desktop — showing a window needs no foreground, focus or visible desktop; it only reads styles and `IsVisible`, and passed in the default run. The foreground and pixel checks are Heavy.
  - `[low]` `[patch]` Nothing tested that `TargetProcessId` follows `State.TargetGame` and clears when disconnected — fixed: two asserts added to `TheDotFollowsTheCaptureState`.

## Design Notes

The window is hidden rather than drawn empty when the dot is none, so no topmost layered window sits over a game with nothing to show (helps independent flip; spike 5.2 measures it). Placement uses `SetWindowPos` in physical pixels with a `Viewbox` filling the window, so WPF's DIP conversion across mixed-DPI monitors doesn't matter. The ticket's WGC monitor capture check uses a GDI screen copy instead: no WGC monitor capture exists in the repo, and both go through DWM's display-affinity exclusion.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Platform.Desktop.Tests -c Release --no-build` -- expected: all pass

**Manual checks (if no CLI):**
- Builder: play a borderless game; the hollow dot appears top-right within 1 s of the Replay Buffer starting, focus stays in the game, the dot goes when it stops.

## Auto Run Result

- **Summary:** The App gets an Overlay. `OverlayViewModel` derives none, hollow or solid from `IEngineSession.State.Capture`, and none whenever the App isn't connected. `OverlayWindow`, a `ShellWindow` and so capture-excluded, is topmost, click-through, never activated and a tool window. It sits top-right on the Target Game's monitor (else the primary), 24 DIP in, at that monitor's DPI, and is hidden when there is no dot. The Win32 calls are in Platform.Desktop. The App declares PerMonitorV2.
- **Files:**
  - `src/ReplayBuffer.Platform.Desktop/OverlayWindowStyle.cs`: the extended styles, the no-activate place-and-show, the foreground read.
  - `src/ReplayBuffer.Platform.Desktop/Monitors.cs`: the Target's monitor and DPI, and the top-right placement maths.
  - `src/ReplayBuffer.Platform.Desktop/NativeMethods.txt`: the new Win32 entries.
  - `src/ReplayBuffer.App/Overlay/OverlayViewModel.cs`: the dot rule.
  - `src/ReplayBuffer.App/Overlay/OverlayWindow.cs`: the window.
  - `src/ReplayBuffer.App/app.manifest` and `ReplayBuffer.App.csproj`: PerMonitorV2.
  - `src/ReplayBuffer.App/ShellApp.cs`: creates the Overlay and closes it on Quit.
  - `tests/ReplayBuffer.App.Tests/OverlayTests.cs`: 2 default tests and 2 Heavy.
  - `tests/ReplayBuffer.App.Tests/ReplayBuffer.App.Tests.csproj`: `UseWindowsForms` for `System.Drawing`.
  - `tests/ReplayBuffer.Platform.Desktop.Tests/MonitorsTests.cs`: placement and the primary-monitor fallback.
- **Review:** 2 patches applied (1 medium, 1 low), applied in the main session rather than by the implementer. Nothing deferred. 4 rejected (3 low, 1 false), with reasons in the Review Triage Log.
- **Follow-up review recommended:** false. Only 1 medium was patched.
- **Verification:**
  - `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings.
  - App.Tests: 67 passed, 7 Heavy skipped.
  - Platform.Desktop.Tests: 11 of 11 passed.
  - The implementer ran the two Heavy Overlay tests (`--explicit only`), and both passed.
  - No engine code changed, so there was no NativeAOT publish.
- **Residual risks:**
  - The dot doesn't follow a game moved to another monitor.
  - The mixed-DPI re-place after WPF's own DPI resize is untested.
  - A real borderless game, focus and the visual check are for the builder.
