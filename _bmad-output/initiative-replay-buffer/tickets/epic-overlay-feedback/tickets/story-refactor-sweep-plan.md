---
title: 'Refactor sweep'
type: 'refactor'
ticket: '13'
created: '2026-11-04'
status: done
baseline_revision: 'e6e42457efe84fca9c63b7fb5780a003d10cbb70'
route: 'full'
route_source: 'auto'
risk: 'low'
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

**Problem:** Epic 5 (5.1–5.12, 5.15) deferred no review findings, but it left small duplicates and stale docs:
- a P/Invoke Bench re-declares;
- an unused CsWin32 entry;
- the "connected or nothing" rule written four times in the App;
- the monitor lookup written twice in `Monitors`;
- a label that hard-codes the 3 s dot window;
- doc comments that predate 5.15 and 5.12;
- public layout constants only the class uses;
- test helpers copied between App test classes;
- three default-run status-line tests serialized for one Explicit WPF test.

4.13 also left one item owned by epic 5's status line: withdrawing `memory-margin` warnings.

**Approach:** Fix those in place with no behaviour change. Record in `_bmad-output/deferred-work.md` what is left: the router and serve-test cleanups that clash with 6.8, the cross-assembly or behaviour-touching ones, and the memory-margin withdrawal.

## Boundaries & Constraints

**Always:**
- Behaviour-preserving: same window styles, placement, timing, settings, IPC, log and UI text. The one exception is the `OnChange` label, whose text stays "For 3 seconds after each change", now built from `OverlayViewModel.DotOnChangeDuration`.
- Keep the forward hooks:
  - `CaptureExclusion.VisibleWindows` / `IsApplied` / `NamesATopLevelWindow`
  - `OverlayWindowStyle.ForegroundWindow` / `ExtendedStyle`
  - the tray menu's explicit `CaptureExclusion.Apply`
  - `EffectiveSettings.OverlayDot`
  - the whole `dev.simulate` path, plus `AppOverlayHost.On` / `Message`
  - `OverlayViewModel.DisplaysChanged` and the `DisplaySettingsChanged` hook
  - the bench stand-in's own `DotSizeDip` / `InsetDip` (AD-2)
- Before removing or narrowing any member, grep `_bmad-output/initiative-replay-buffer/tickets` for its name. Keep it if an unbuilt ticket or the epic Notes name it.
- `OverlayViewModel.Refresh` keeps its order: hide, then move, then show.
- Keep each file's line endings, doc-comment style and file-scoped namespaces.

**Never:**
- Touch `FeedbackRouter.cs`, `DevSimulator.cs`, `SessionOrchestrator.cs`, `Warnings.cs`, `FeedbackRouterTests.cs`, `FeedbackSoundsTests.cs`, `DevSimulateServeTests.cs` or `FeedbackSoundServeTests.cs`. Story 6.8 is changing them.
- Touch `AppOverlayHost.cs`, `BenchOptions.cs` or `FpsBenchmark.cs`. 8.1 and the capture-cost investigation are changing them.
- Change `Contracts`.
- Touch `feature/5.8-overlay-perf-research`.
- Change the Explicit/Heavy tests' bodies, except where a moved helper is renamed.
- Add tests.

</intent-contract>

## Code Map

- `src/ReplayBuffer.Platform.Desktop/NativeMethods.txt:36` -- `DXGI_ERROR_NOT_FOUND`: no caller; `Monitors.OverlayPlanes` checks only `.Failed`. Its only ticket mention is GpuLoad's own list (1.14).
- `src/ReplayBuffer.Platform.Desktop/Monitors.cs` -- `ForProcess` (~27) and `OverlayPlanes` (~59) both do `LargestWindow` → `MonitorFromWindow`, else `MonitorFromPoint`. Extract one private helper. `ForProcess` keeps its fallback to the primary monitor when reading the window's monitor fails. Doc ~54: "only a free plane keeps independent flip, about 7.5 % lost" → attribute it to 5.8's measurement, and say 5.12 measured `always` without planes still in independent flip.
- `bench/ReplayBuffer.Bench/OverlayBenchmark.cs:440` -- private `GetForegroundWindow` DllImport, used at ~203, ~209, ~386. Replace the calls with `OverlayWindowStyle.ForegroundWindow()` (Platform.Desktop, already referenced). Match the return type; the DllImport returns `nint`.
- `bench/ReplayBuffer.Bench/OverlayHostProcess.cs:108` -- `ShowDot` / `HideDot` are called only by `On` / `Off`; fold them in.
- `src/ReplayBuffer.App/Engine/EngineConnection.cs` -- add `internal static` extension `ConnectedState(this IEngineSession)` for the repeated `IsConnected ? State : null` (or the equivalent shape). Use it at:
  - `Overlay/OverlayViewModel.cs:171`
  - `TrayCapture.cs:21`
  - `Status/StatusLine.cs:41,46`
  - `Settings/FeedbackSoundsPage.cs:84`

  Read each site first and skip any whose expression differs.
- `src/ReplayBuffer.App/Overlay/OverlayViewModel.cs:61` -- the `time` parameter doc should add the 3 s dot window and `UnknownPlanesRetry`. Doc ~26: the same 5.8/5.12 rewording as `Monitors`.
- `src/ReplayBuffer.App/Settings/GeneralSettingsPage.cs:16` -- build the OnChange label from `OverlayViewModel.DotOnChangeDuration` (whole seconds).
- `src/ReplayBuffer.App/Overlay/OverlayWindow.cs:29-42` -- make `IconSizeDip`, `GapDip`, `MessageWidthDip`, `MessageHeightDip` and `MessageGapDip` private (grep the tests first; `DotSizeDip` and `InsetDip` stay public). `Place` (~223) re-picks the size that `Update` (~185) chose; have both use one private helper.
- `tests/ReplayBuffer.App.Tests/Sta.cs` -- add a shared `Pump(TimeSpan)`. Remove the copies at `OverlayTests.cs:542` and `CaptureExclusionTests.cs:234` (same project). Leave Desktop.Tests' copies.
- `tests/ReplayBuffer.App.Tests/StatusLineTests.cs:14` -- the class is in `WpfCollection` only for its Explicit PNG test (~69). Move that test into a new `StatusLineRenderTests` class in the same file, in `WpfCollection`, and take the collection off the rest.
- `_bmad-output/deferred-work.md` -- add an epic 5 group in the existing `source_plan` / `summary` / `evidence` / `outcome` format.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Platform.Desktop/NativeMethods.txt`, `Monitors.cs` -- drop the unused entry; one monitor-lookup helper; reword the doc -- dead code, duplicate, stale doc
- [x] `bench/ReplayBuffer.Bench/OverlayBenchmark.cs`, `OverlayHostProcess.cs` -- use `OverlayWindowStyle.ForegroundWindow()`; fold `ShowDot`/`HideDot` -- duplicate P/Invoke, forward-only wrappers
- [x] `src/ReplayBuffer.App/Engine/EngineConnection.cs` + the four call sites -- `ConnectedState()` -- repeated rule
- [x] `src/ReplayBuffer.App/Overlay/OverlayViewModel.cs`, `OverlayWindow.cs`, `Settings/GeneralSettingsPage.cs` -- docs, private constants, one size helper, label from the constant -- stale docs, over-public members
- [x] `tests/ReplayBuffer.App.Tests/Sta.cs`, `OverlayTests.cs`, `CaptureExclusionTests.cs`, `StatusLineTests.cs` -- shared `Pump`; status-line tests out of the serial collection -- test duplicates, lean tests
- [x] `_bmad-output/deferred-work.md` -- epic 5 group:
  - (a) `memory-margin` withdrawal: still open. It changes the warnings' lifecycle, and the 5.10 status line shows them as they are. Owner: epic 8's hardening.
  - (b) FeedbackRouter's repeated disposed guard, sound names as strings, the inline `"replay"` source, the two fake notice sinks, and the shared serve-test harness. Open; owned by epic 6's sweep, after 6.8.
  - (c) `FeedbackSounds.IsOn`/`With` duplicated between `FeedbackSoundsPage` and `FeedbackSoundPlayer`. Open; it needs a Contracts addition. Epic 6's sweep.
  - (d) Bench's duplicates: the pipe-server P/Invoke, the overlay style mask, the dot-mode names, and the load-option parsing. Open; they are in files 8.1 and the capture-cost work are changing, and the timeout rules differ. Epic 8's sweep.
  - (e) `ManualTime` copied across test projects, and the duplicate Explicit screen-copy helpers. Open, low value.
  - (f) What 5.13 closed.

**Acceptance Criteria:**
- Given the sweep, when `dotnet build ReplayBuffer.slnx -c Release` runs, then it reports 0 warnings and 0 errors.
- Given the sweep, when the default runs of `ReplayBuffer.App.Tests`, `ReplayBuffer.Platform.Desktop.Tests` and `ReplayBuffer.Bench.Tests` run, then all pass. That includes the Overlay's saved and failed messages, the status line's capture states and Clip saved and failed notices, and the Feedback Sounds page.
- Given `grep -rn "GetForegroundWindow" bench/ReplayBuffer.Bench`, then nothing matches.
- Given `deferred-work.md`, then every item above is recorded with an owner.

## Implementation Notes

- `ConnectedState` has two forms. `OverlayViewModel` and `TrayCapture` read the session, so they use the extension. `StatusLine.Render` and `FeedbackSoundsPage.ShowState` take status and state as parameters, so they use the static `EngineSessions.ConnectedState(status, state)`, which gives the same result.
- `Monitors.ForProcess` reads the process's monitor and falls back to the primary. With no process window it reads the primary twice on failure. That is harmless, and the old order is kept.
- The Explicit PNG test moved, unchanged, into `StatusLineRenderTests`. It reaches the shared `Warning` builder through a forwarding helper, since `StatusLineTests.Warning` is now `internal static`.
- An implementation subagent made the edits while `.bench-running` was held. The build and tests ran after the lock cleared (see Verification).

## Plan Change Log

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 2 findings — high 0, medium 0, low 1, false 1, maybe-false 0
- findings:
  - `[low]` `[patch]` In `deferred-work.md`, item (e) (`ManualTime` and the screen-copy helpers) had no owner, against the AC "every item recorded with an owner". Fixed: owned by epic 8's sweep.
  - `[false]` `[reject]` The AC's `grep -rn "GetForegroundWindow" bench/ReplayBuffer.Bench` matches binaries under `bin/`. The AC is about source: `--include=*.cs` finds 0 matches, and the DLL hits are CsWin32's import in Platform.Desktop, which Bench now calls. The fix would be an edit to this build's plan.

## Design Notes

Ticket verify ("the Overlay, sounds and status line still report a saved and a failed Clip"): the sweep leaves the engine's router and sound player untouched, so the sounds' path is unchanged. The App's default tests cover the Overlay's messages and the status line. No engine code changes, so there is no NativeAOT publish and no Engine/Core run.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- expected: pass (likewise `Platform.Desktop.Tests`, `Bench.Tests`)

## Auto Run Result

Status: built

**Summary:** A behaviour-preserving sweep of epic 5's code.
- Removed an unused CsWin32 entry, and Bench's duplicate `GetForegroundWindow` P/Invoke.
- Folded `OverlayHostProcess`'s forwarding wrappers into their callers.
- Added one `ConnectedState` rule for the Overlay, tray, status line and Feedback Sounds page.
- Gave `Monitors` one monitor-lookup helper.
- Made the Overlay's layout constants private, behind one size helper.
- Built the OnChange label from `DotOnChangeDuration`.
- Updated the docs after 5.15 and 5.12.
- Shared `Sta.Pump` across the App tests.
- Moved the default status-line tests out of the serial WPF collection.
- Recorded what is left in `_bmad-output/deferred-work.md`, with owners. That includes the `memory-margin` withdrawal, now owned by epic 8's hardening.

**Files:**
- `src/ReplayBuffer.Platform.Desktop/NativeMethods.txt`, `Monitors.cs`: the entry dropped, the `MonitorOf` / `PrimaryMonitor` helpers, the doc.
- `bench/ReplayBuffer.Bench/OverlayBenchmark.cs`: uses `OverlayWindowStyle.ForegroundWindow()`.
- `bench/ReplayBuffer.Bench/OverlayHostProcess.cs`: `On` / `Off` send directly.
- `src/ReplayBuffer.App/Engine/EngineConnection.cs`: `EngineSessions.ConnectedState`.
- `OverlayViewModel.cs`, `TrayCapture.cs`, `StatusLine.cs`, `FeedbackSoundsPage.cs`: use it.
- `OverlayWindow.cs`: private constants and `ExtentDip`.
- `GeneralSettingsPage.cs`: the label comes from the constant.
- `tests/ReplayBuffer.App.Tests/Sta.cs`, `OverlayTests.cs`, `CaptureExclusionTests.cs`, `StatusLineTests.cs`: the shared `Pump`, and `StatusLineRenderTests`.
- `_bmad-output/deferred-work.md`: the epic 5 group.

**Review:** quick lens, 2 findings.
- 1 low, patched: item (e) now has an owner.
- 1 false, rejected: the grep matches build output, not source.
- Nothing deferred.

**Follow-up review recommended:** false (0 high and 0 medium patched).

**Verification:**
- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings, 0 errors.
- `ReplayBuffer.App.Tests`: 115 passed, 11 skipped (Explicit).
- `ReplayBuffer.Platform.Desktop.Tests`: 13 passed.
- `ReplayBuffer.Bench.Tests`: 135 passed, 10 skipped.
- These ran after the GPU lock cleared. The review patch changed only `deferred-work.md`.
- No engine code changed, so there was no NativeAOT publish. Core, Engine and Audio aren't touched; the router and sound player are unchanged.

**Residual risks:** none known. The router, serve-test and Bench duplicates wait for 6.8, 8.1 and the capture-cost work, as `deferred-work.md` records.
