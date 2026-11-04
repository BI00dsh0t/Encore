---
title: 'Overlay over exclusive fullscreen and the presentation path'
type: 'feature'
ticket: '8'
created: '2026-11-03'
status: done
baseline_revision: 'edb82dafb05f69e35c9f8db6704679d546758640'
route: 'full'
route_source: 'auto'
risk: 'high'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: []
deferred: []
---

<intent-contract>

## Intent

**Problem:** Spike 5.2 measured the Overlay over exclusive fullscreen and in borderless with a stand-in window (`ReplayBuffer.Bench.Overlay.exe`), not the real App. It decided the Overlay shows the same over every game: no `QUNS_RUNNING_D3D_FULL_SCREEN` hide, and no borderless mitigation. Nothing yet checks that the real App's Overlay, fed by the real engine, keeps a legacy exclusive game in exclusive fullscreen and in focus through saved and failed notices, or keeps a borderless game in the same PresentMode.

**Approach:** `bench overlay` gains `--host app`. Bench runs the real `ReplayBuffer.App.exe --tray` on a throwaway test instance, which starts its engine. Bench drives the dot and the messages over the engine pipe with `dev.simulate`. A new, additive `SimulatedEvent.ClipSaved` raises the engine's saved notice. The report says, per mode, whether the load kept its PresentMode, its DXGI fullscreen state and the foreground. No App Overlay code changes: the spike's decision is that there is no fullscreen-state switch.

## Boundaries & Constraints

**Always:**
- Contracts change additively only (AD-22): one new enum member, `SimulatedEvent.ClipSaved`, appended last. `DevSimulator` gets a `saved` callback that calls `FeedbackRouter.ClipSaved(new ClipSaved(<placeholder path>))` only. It doesn't publish `clip.saved`, because no file exists. Dev and test instances only, as for the other events.
- Bench never compiles against the App (AD-2). It starts `ReplayBuffer.App.exe` as a process and talks to the engine with `Contracts.Ipc.EngineClient` on `PipeName.ForCurrentUser(<instance>)`.
- The instance is `ReplayBuffer-Test-Bench-<pid>`. Before the load starts, Bench sends one `settings.update` that creates the load exe's Game Profile and sets it `ignored`: `SetProfile(id, "name", "GPU load")`, then `SetProfile(id, "ignored", true)`. This stops Game Detection from starting a real capture, which would show the hollow dot in every phase. The id is AD-18's: `GetFinalPathNameByHandle(FILE_NAME_NORMALIZED | VOLUME_NAME_DOS)`, with `\\?\` stripped, lowercased. Bench copies the logic in a small P/Invoke, because it doesn't reference `Platform.Windows`.
- The phases with `--host app`:
  - off: `recordingStopped`, so there's no dot.
  - on: `recordingStarted` (the solid dot), then `clipSaved`, then `saveFailed`. These are sent before the settle, so the second message is on screen during PresentMon's window.
  - off-again: `recordingStopped`.
- Each mode's summary adds `ExclusiveKept`: the load never exited, it was in front before and after every phase, and it reported no DXGI fullscreen change. This applies to the fullscreen and exclusive modes. `IndependentFlipKept` and the PresentMode columns stay as they are. In borderless the check is that the PresentMode with the dot shown equals the PresentMode with it hidden.
- The Overlay window's visibility comes from the App process's top-level window whose extended style has `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` (`0x08000000 | 0x20 | 0x80`). Bench enumerates the windows each phase, because the window has no handle until it is first shown.
- Clean up on every path: `engine.shutdown`, after which the App quits by itself. Then kill whichever of the App and engine processes are still alive, and delete the instance's Roaming and Local folders.
- `--host stand-in` (the default) behaves exactly as now. `--host app` and `--overlay-exe` together are a usage error.
- Lean tests (CLAUDE.md "Tests"). Nothing new in the default run needs a GPU, a game window or focus.

**Never:**
- No change to `OverlayViewModel`, `OverlayWindow`, `Platform.Desktop` or any App code.
- No QUNS check anywhere in src.
- No real capture and no Feedback Sound (a test instance's sounds are already silent).
- Don't change the stand-in path's phases or report columns, beyond the added `ExclusiveKept`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Simulated save | test engine, `dev.simulate clipSaved` | an active `clip-saved` Info warning, "Clip saved", Overlay true, sound `clipSaved`; earlier `clip-failed` withdrawn | installed instance: `not-dev-instance` |
| App host | `bench overlay --host app --modes exclusive` | phases off/on/off-again; on phase: Overlay window visible; summary `ExclusiveKept` yes | App or engine never connects within 30 s → `BenchFailure`, processes stopped, folders deleted |
| Bad options | `--host app --overlay-exe x` or `--host foo` | usage error naming the option | exit 2 |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Messages/Dev.cs` -- append `ClipSaved` to `SimulatedEvent`. `CamelCaseEnumConverter` serialises it as `clipSaved`.
- `src/ReplayBuffer.Engine/Sessions/DevSimulator.cs` -- add an `Action saved` primary-constructor parameter, a `case SimulatedEvent.ClipSaved: saved();`, and the param doc.
- `src/ReplayBuffer.Engine/EngineHost.cs:477` -- pass `() => notices.ClipSaved(new ClipSaved("dev.simulate"))` (`ClipSaved(string Path)` is in `Contracts/Messages/Capture.cs:43`).
- `tests/ReplayBuffer.Engine.Tests/DevSimulateServeTests.cs:42-70` -- extend `EachSimulatedEventReachesTheSnapshot` with `ClipSaved` after `SaveFailed`: a `clip-saved` warning appears, and the `clip-failed` one goes. Fix the `new DevSimulator(` call at line 74 for the new parameter.
- `bench/ReplayBuffer.Bench/OverlayBenchmark.cs` -- `Run`:
  - Read `--host` (`stand-in`|`app`) and validate it.
  - Build an `IOverlayHost` once, before the mode loop for the App (the App and engine outlive the loads), or per mode for the stand-in, as now.
  - Each phase calls `host.Off()`/`host.On()` instead of `HideDot`/`ShowDot`, and reads `host.WindowVisible()`.
  - `OverlayModeSummary` gains `bool ExclusiveKept`, computed in `Summarize` from the phases (`LoadInForeground` on every phase, the phase count is 3, and `Error` is null on every phase) and `fullscreenChanges` (no entry starting `fullscreen`). The caller already passes the load's stderr lines prefixed `load: `, so only the load's `fullscreen <bool>` lines count.
  - `OverlayReport` gains `string Host`.
  - The markdown adds a "Fullscreen and focus kept" column and the host in the header line.
  - Reuse `BringToFront`, `RecordPresents`, `IsRemoteSession` and `PresentMonCsv` unchanged.
- `bench/ReplayBuffer.Bench/OverlayHostProcess.cs` -- implements `IOverlayHost` (`On` → `ShowDot`, `Off` → `HideDot`, `WindowVisible` → `IsWindowVisible(Hwnd)`). Keep `Resolve`, `ParseReady` and `Start` unchanged.
- `bench/ReplayBuffer.Bench/AppOverlayHost.cs` (new) -- `internal sealed class AppOverlayHost : IOverlayHost`:
  - `Resolve(BenchContext)`: `--app-exe`, else the newest `src/ReplayBuffer.App/bin/Release/*/ReplayBuffer.App.exe` with `ReplayBuffer.Engine.exe` beside it. Otherwise throw a `BenchFailure` naming the build command.
  - `Start(exe, ignoredExe)`: launch `--tray --instance <name>`. Connect `EngineClient` with retries until 30 s pass (the App starts the engine). Send `settings.update` (`ExpectedRevision` = the snapshot's settings document `Revision`, as `src/ReplayBuffer.App/Settings/SettingsSender.cs:35` does) with the two profile ops, using `JsonSerializer.SerializeToElement`.
  - `On`/`Off` send `dev.simulate` and wait for the reply. `On` sends `recordingStarted`, `clipSaved`, `saveFailed`.
  - `WindowVisible()`: `EnumWindows` over the App's pid with the style mask above.
  - `Dispose`: the cleanup above.
  - Put the canonicalize P/Invoke here: `File.OpenHandle` plus `GetFinalPathNameByHandleW`.
- `bench/ReplayBuffer.Bench/BenchOptions.cs:34-36,58-61,77` -- the usage text (`[--host stand-in|app] [--app-exe <ReplayBuffer.App.exe>]`, and one sentence on `--host app`) and the allowed options `host` and `app-exe`.
- `bench/ReplayBuffer.Bench/GpuLoadProcess.cs:79` -- `FullscreenChanges`: read only; its entries are what `ExclusiveKept` reads.
- `tests/ReplayBuffer.Bench.Tests/OverlayBenchmarkTests.cs:53` -- `TheSummaryGivesTheDotsCostAndWhetherItCanBeSeen`: extend the existing test, don't add a new one.
- `src/ReplayBuffer.App/ShellApp.cs` -- read only: `--tray`, and `--instance` for test names. The App quits when the engine ends cleanly.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/Messages/Dev.cs`, `src/ReplayBuffer.Engine/Sessions/DevSimulator.cs`, `src/ReplayBuffer.Engine/EngineHost.cs` -- `SimulatedEvent.ClipSaved` raises the saved notice -- the ticket's "saved simulator event".
- [x] `tests/ReplayBuffer.Engine.Tests/DevSimulateServeTests.cs` -- the `ClipSaved` step in the existing test, and the constructor fix.
- [x] `bench/ReplayBuffer.Bench/AppOverlayHost.cs`, `OverlayHostProcess.cs`, `OverlayBenchmark.cs`, `BenchOptions.cs` -- `--host app` and `ExclusiveKept`.
- [x] `tests/ReplayBuffer.Bench.Tests/OverlayBenchmarkTests.cs` -- `ExclusiveKept` true for clean phases, and false when the changes hold `fullscreen false`.
- [x] `tests/ReplayBuffer.Bench.Tests/OverlayBenchmarkTests.cs` -- `--host foo`, and `--host app` with `--overlay-exe`, each throw `BenchUsageException` before anything starts (the Bad options row).
- [x] `tests/ReplayBuffer.Bench.Tests/OverlayBenchmarkTests.cs` -- one `[Fact(Explicit = true)]`, `[Trait("Category", "Heavy")]` test, `TheRealAppsOverlayKeepsTheExclusiveLoadInFullscreen`. It runs `OverlayBenchmark.Run` with `--host app --modes exclusive --seconds 10 --settle 5` into a temp run folder and asserts: exit 0, `ExclusiveKept`, PresentMode Hardware: Legacy Flip in all three phases, and `OverlayWindowVisible` only in the on phase. Follow how the existing Heavy Bench tests build a `BenchContext`. It needs the GPU, PresentMon and the console, so it is opt-in (the App host row).

**Acceptance Criteria:**
- Given a test engine, when a client sends `dev.simulate clipSaved` after `saveFailed`, then the snapshot holds a `clip-saved` Info warning ("Clip saved", Overlay true) and no `clip-failed` warning.
- Given the GPU load in exclusive mode and the real App on a test instance, when `bench overlay --host app --modes exclusive` runs the off, on (solid dot, "Clip saved", "Clip not saved: save failed") and off-again phases, then `overlay.md` shows the load in Hardware: Legacy Flip in every phase, in front throughout, with no DXGI fullscreen change ("Fullscreen and focus kept: yes"), and the App's Overlay window visible in the on phase only.
- Given the GPU load in borderless with the real App, when the same run uses `--modes borderless`, then the PresentMode with the dot and messages shown equals the PresentMode with them hidden.
- Given the App's Overlay code, then nothing in `src` calls `SHQueryUserNotificationState`: the Overlay behaves the same in every fullscreen state, as spike 5.2 decided.

## Implementation Notes

- The coding subagent finished the Contracts, Engine and Engine test tasks before the desktop app restarted and stopped it. The parent session wrote the Bench part and its tests.
- `AppOverlayHost` gets the engine's process id as the pipe's server (`GetNamedPipeServerProcessId` in `VerifyServer`), so cleanup stops only this run's engine. The instance is `ReplayBuffer-Test-bench-overlay-<8 hex>`, not `-Bench-<pid>`: a random suffix can't collide with a leftover from an earlier run.
- First GPU run: `Task.Wait` wrapped the connect timeout in an `AggregateException`, so the retry never caught it, and the App and engine were left running. Those two processes were stopped by hand, and their folder was deleted. `Wait` now uses `WaitAsync(...).GetAwaiter().GetResult()`, and every failure in `Start` cleans up.
- The Heavy test went from `--seconds 5 --settle 3` to `--seconds 10 --settle 5`. The shown phase queues three 4 s messages, Recording started, saved and failed, and off-again adds Recording stopped, so shorter phases read the window as visible in off-again. Bench now refuses `--host app` below those values.
- Added `--dot-only` (`--host app` only), to tell the dot from the messages when borderless fell out of independent flip. Diagnostic only.
- **Finding: borderless falls out of independent flip on this PC now, with the stand-in too.** Run on 2026-11-03 at 20:12–20:30 at the console, `measurements/bench-5.8*` in this worktree:

  | Run | Borderless PresentMode, hidden / shown / hidden again | FPS cost |
  |---|---|---|
  | App, dot and messages | Independent Flip / Composed: Flip / Independent Flip | 7.57 % |
  | App, `--dot-only` | Independent Flip / Composed: Flip / Independent Flip | 7.85 % |
  | Spike's stand-in, re-run now | Independent Flip / Composed: Flip / Independent Flip | 7.39 % |

  The spike measured the same stand-in in independent flip with no cost at 13:12. The PC has rebooted since, at 20:01; NVIDIA Overlay, Discord and Steam started at login, and two monitors are attached. So this is the spike's "no free overlay plane" risk, not something the App's window adds: the App and the stand-in cost the same. FR-6's 3 s mitigation is a product deviation the builder must decide on, so it isn't built here. It goes to Decisions Awaiting Review and the Human Check Queue.
- Fullscreen (flip, with fullscreen optimizations) and legacy exclusive: the real App's Overlay kept every phase in the same PresentMode (Independent Flip and Legacy Flip), with the load in front and no DXGI fullscreen change. Cost: −0.2 % and 0.08 %.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 9 findings — high 1, medium 1, low 5, false 2, maybe-false 0
- findings:
  - `[high]` `[patch]` `ExclusiveKept` never sees a real fullscreen change, because the load's entries are `HH:mm:ss.fff fullscreen <bool>` and not `fullscreen ...` — fixed: every entry that isn't a `load: ` note counts, and the unit test now uses the real format plus a `load: ` note. The GPU runs recorded no change in any mode, so their "yes" holds.
  - `[medium]` `[patch]` Off-again is hidden only with enough settle and phase length (4 s messages) — fixed: `--host app` refuses `--settle` < 5 or `--seconds` < 10, and a theory case covers it.
  - `[low]` `[patch]` The Heavy test's values differ from the plan, and it builds `BenchContext` by hand — fixed: the test uses `_run.Context(...)`, and the change is logged in Implementation Notes.
  - `[false]` `[reject]` The instance name differs from the plan — the fix would be a plan edit. The name is a valid test name, and the reason is logged in Implementation Notes.
  - `[low]` `[reject]` `--dot-only` isn't in the plan — it is a diagnostic that found the borderless result. It is logged in Implementation Notes and already refused with the stand-in. Worth keeping for 5.12.
  - `[low]` `[patch]` `--app-exe` is silently ignored with the stand-in — fixed: it is now a usage error, with a theory case.
  - `[false]` `[reject]` `ExclusiveKept` is shown for borderless — the footnote defines the column as in front, never exited and no DXGI change, which holds for borderless too (focus).
  - `[low]` `[patch]` The `DevSimulator` and `EngineHost` summaries don't mention the saved notice — fixed.
  - `[low]` `[patch]` `AppOverlayHost.Start` resets the stack or drops the inner exception — fixed: `throw;` for `BenchFailure`, and other exceptions go into the message with their full text.

## Design Notes

- The ticket's verify line still names "automated tests cover the fullscreen-state switch in the view model". That line came from the epic's earlier default, the QUNS hide. The epic's 2026-11-03 Note replaced it after spike 5.2: "Story 8 drops the QUNS hide". The view model has no fullscreen input, so there is no switch to test. The last AC holds that decision instead.
- Why the profile is set Ignored rather than letting the Replay Buffer run: a real capture would keep the hollow dot up in every phase, so off would never be off. It would also measure capture as well as the Overlay, which is story 5.12's job.
- `clipSaved` goes to the notice only. Publishing `clip.saved` with no file behind it would invite consumers to read a path that doesn't exist.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors.
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build`, `dotnet test --project tests/ReplayBuffer.Bench.Tests -c Release --no-build`, `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build` -- expected: all pass.
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL warnings (engine code changed).
- GPU (the parent session runs it, holding `.bench-running` in the main folder): `bench/ReplayBuffer.Bench/bin/Release/<tfm>/ReplayBuffer.Bench.exe overlay --host app --modes borderless,fullscreen,exclusive --out measurements/bench-5.8` -- expected: exit 0, and the ACs above read from `overlay.md`.

**Manual checks (if no CLI):**
- Human Check Queue: watch the real App's Overlay over a real legacy exclusive-fullscreen game through a save and a failed save. The game stays fullscreen and focused, and the Feedback Sounds tell the two apart.

## Auto Run Result

- **Summary:**
  - `dev.simulate clipSaved` is a new, additive Contracts enum member (AD-22). It raises the engine's "Clip saved" notice and withdraws any `clip-failed` one.
  - `bench overlay --host app` runs the real `ReplayBuffer.App.exe --tray` on a throwaway test instance, with the load's Game Profile Ignored. It drives the solid dot plus the saved and failed messages through `dev.simulate`, and reports "Fullscreen and focus kept" per mode.
  - No App Overlay code changed: spike 5.2 decided there is no QUNS switch, and nothing in `src` reads the QUNS state.
- **Files:**
  - `Contracts/Messages/Dev.cs`: `ClipSaved`.
  - `Engine/Sessions/DevSimulator.cs`, `Engine/EngineHost.cs`: the saved callback.
  - `bench/.../AppOverlayHost.cs` (new): the App host and the AD-18 canonical id.
  - `OverlayHostProcess.cs`: `IOverlayHost`.
  - `OverlayBenchmark.cs`: `--host`, `ExclusiveKept`, the column and the guards.
  - `BenchOptions.cs`: the options and usage.
  - The tests: `DevSimulateServeTests` and `OverlayBenchmarkTests` (the summary, usage errors, and one Heavy test).
- **Review:** quick lens, 9 findings: 6 patched (1 high, 1 medium, 4 low) and 3 rejected (2 false, 1 low diagnostic kept). Nothing deferred.
- **Follow-up review recommended:** false. The patched high is covered by a unit test, and the GPU runs recorded no fullscreen change, so no unverified risk remains.
- **Verification:**
  - `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings.
  - Bench.Tests: 139 passed. Engine.Tests: 186 passed. Contracts.Tests: 118 passed.
  - `tools/publish-engine.ps1`: published, no IL warnings.
  - GPU, under `.bench-running`: `bench overlay --host app` in all three modes, `--dot-only` borderless, the stand-in borderless, and the Heavy test `TheRealAppsOverlayKeepsTheExclusiveLoadInFullscreen`. It passed twice after the timing fix. Once it failed on an unlabelled assert, most likely the foreground: other sessions were running tests on the PC.
- **Residual risks:**
  - AC 3 (borderless keeps its PresentMode) fails on this PC as it is now, for the stand-in too. It needs the builder's call on FR-6's mitigation, and a check with no other overlay apps running.
  - The Heavy test depends on nothing else taking the foreground.
