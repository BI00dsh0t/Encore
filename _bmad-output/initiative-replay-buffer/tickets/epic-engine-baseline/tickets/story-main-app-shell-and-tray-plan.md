---
title: 'Main App shell and tray'
type: 'feature'
ticket: '7'
created: '2026-10-25'
status: 'built'
baseline_revision: '3751a79198b024f70888e6a8f9aff148ff25337d'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `ReplayBuffer.App` is an empty `Main`. Nothing shows the engine's state, nothing lets the builder quit the pair, and epics 2 and 3 have no Settings host to add pages to and no shared way to send `settings.update` (R3, R7, AD-4, AD-16).

**Approach:** The App starts in the tray (H.NotifyIcon.Wpf 2.4.1), connects to the engine over the 1.5 `EngineClient` (launching `ReplayBuffer.Engine.exe serve` when none answers) and renders `state.snapshot` in one Main App window. The window has a Status area and a Settings area: a page registry with navigation, shipped with one General page. A UI-free `SettingsSender` builds `settings.update` against a document revision and reports `settings-conflict` as a reload. Every App window is excluded from capture.

## Boundaries & Constraints

**Always:**
- The App only talks over IPC and never writes `settings.json` (AD-1, AD-4). Everything it renders comes from `EngineClient.State` (AD-3); the settings document already is a snapshot field.
- Tray menu: **Open**, **Start with Windows** (checkable; writes only `startWithWindows` through the sender), **Quit**. Quit sends `engine.shutdown`, waits up to 5 s for the connection to end, then exits the App (AD-16). Closing the window hides it.
- The sender sends `expectedRevision` = the revision of the document the caller rendered, never auto-retries (AD-3), and on `settings-conflict` returns a conflict result carrying the current document so the page reloads; other error codes come back as failures with their code.
- Every App window, and the tray context menu's popup, gets `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` once its HWND exists (AD-15).
- Settings pages are added by one line per epic area in a page registry (the `MessageRegistry` pattern); a page gets the current document and the sender.
- Minimal, functional WPF (no `bmad-ux` pass), hand-rolled MVVM with no new UI packages; 0 warnings; Contracts changes, if any, additive (AD-22).

**Never:**
- No restart of a crashed or hung engine, heartbeat, App relaunch, single-instance mutex or server image-path check (story 8). No Hotkeys (1.9). No Run key, install or publish layout (1.13).
- No P/Invoke in `ReplayBuffer.App` itself; no compile-time reference from the App to Core, Media, the engine or `Platform.*` other than `Platform.Desktop`.
- No Settings pages beyond General; no Overlay (epic 5); no Library (epic 7).

**Decisions (user, 2026-10-25):**
- The App's Win32 calls live in a new `ReplayBuffer.Platform.Desktop` (+ `ReplayBuffer.Platform.Desktop.Tests`) that references nothing; the graph gains `App → Platform.Desktop`, recorded in the spine's AD-2 on this branch. AD-2's P/Invoke rule stands as written.
- A dev build copies the engine's build output beside the App (a build-order-only reference with `ReferenceOutputAssembly=false`, plus a copy step), matching the one-folder install layout. The App launches `ReplayBuffer.Engine.exe` from its own folder.
- The plan is kept whole at about 2 200 tokens.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Start, engine up | App launched, engine serving | connects, renders snapshot, window shown (hidden with `--tray`) | — |
| Start, no engine | nothing on the pipe | launches `ReplayBuffer.Engine.exe serve` hidden, retries connect for 10 s | still none → status "Engine not running", tray Quit still works |
| Event | `settings.changed`, `warning.raised`, … | view refreshes from `State` on the UI thread | — |
| Update | sender, basis at current revision | reply `{ revision }`; `settings.changed` arrives; page shows new value | — |
| Stale update | basis revision < current | no change | `settings-conflict` → conflict result with current document; page reloads and says so |
| Other refusal | e.g. `setting-invalid-value`, `engine-disconnected` | page shows the code and message, value reverts to the document's | — |
| Close window | title-bar close | window hidden, App and engine keep running | — |
| Quit | tray Quit | `engine.shutdown` → engine exits → App exits; no process left | engine gone or no reply in 5 s → App exits anyway |
| Engine dies | connection ends unexpectedly | status "Engine disconnected"; tray stays | no restart (story 8) |

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.Contracts/Ipc/EngineClient.cs` -- reuse as is: `ConnectAsync(PipeName.ForCurrentUser(name), options)`, `State`, `SendAsync`, `Completion`; `OnEvent`/`OnDisconnected` run on the read loop, so marshal to the Dispatcher. Don't change.
- `src/ReplayBuffer.Contracts/Messages/Engine.cs` -- `StateSnapshot` (`Settings`, `Capture`, `Warnings`, `Pid`, `Seq`), `EngineMessages.Shutdown`, `Empty.Value`.
- `src/ReplayBuffer.Contracts/Messages/SettingsMessages.cs`, `Settings/SettingsDocument.cs` -- `SettingsMessages.Update`/`Changed`, `SettingsUpdate`, `SettingsOp.SetGlobal`; `SettingsDocument.Global` holds raw JSON per key.
- `src/ReplayBuffer.Contracts/Settings/Keys.cs` -- `Keys.StartWithWindows` (`SettingKey<bool>`, Global-only); `TryRead`/`ToJson`/`Default` read and write values. `Resolve` is in Core, which the App must not reference: Global values are read with `TryRead` falling back to `Default`.
- `src/ReplayBuffer.Contracts/Instance.cs` -- `Instance.Current.Name` for the pipe; engine and App beside each other resolve the same instance.
- `src/ReplayBuffer.Engine/Program.cs`, `CommandLine.cs` -- `serve [--instance ReplayBuffer-Test-*]`; prints `listening <pipe>`; a console exe, so launch with `CreateNoWindow`. Don't change.
- `tests/ReplayBuffer.Engine.Tests/EngineServeTests.cs` -- pattern for running a test-instance engine and cleaning its folders.
- `Directory.Build.props` -- the App is already excluded from the AOT analyzers; new projects inherit TFM and warnings-as-errors.
- `src/ReplayBuffer.Platform.Windows/` -- CsWin32 project pattern (`NativeMethods.txt/json`, `DisableRuntimeMarshalling`).

## Tasks & Acceptance

**Execution:**
- [ ] `Directory.Packages.props`, `src/ReplayBuffer.App/ReplayBuffer.App.csproj` -- add `H.NotifyIcon.Wpf` 2.4.1; reference Platform.Desktop; build-order-only engine reference and a copy of its output beside the App.
- [ ] `src/ReplayBuffer.Platform.Desktop/` (new, in `ReplayBuffer.slnx`) -- `CaptureExclusion.Apply(hwnd)` and `IsApplied(hwnd)` over CsWin32 (`SetWindowDisplayAffinity`, `GetWindowDisplayAffinity`); `tests/ReplayBuffer.Platform.Desktop.Tests/` checks it on a real window.
- [ ] `_bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md` -- AD-2 diagram and layer table gain `App → Platform.Desktop` and its role; Structural Seed lists the project.
- [ ] `src/ReplayBuffer.App/Engine/EngineConnection.cs` -- connect-or-launch, retry, state/disconnect notifications, `ShutdownAsync` (send, wait ≤ 5 s); implements `IEngineSession { State, SendAsync }`.
- [ ] `src/ReplayBuffer.App/Settings/SettingsSender.cs`, `SettingsValues.cs` -- `UpdateAsync(basis, ops)` → `Committed(revision) | Conflict(current) | Failed(code, message)`; typed `SetGlobal<T>(key, value)` op builder; `GlobalValue<T>(document, key)`.
- [ ] `src/ReplayBuffer.App/Settings/SettingsPages.cs`, `GeneralSettingsPage.*` -- `ISettingsPage { Title, CreateView(context) }`, registry list, the General page (Start with Windows checkbox).
- [ ] `src/ReplayBuffer.App/App.xaml(.cs)`, `Program.cs`, `MainWindow.xaml(.cs)`, `MainViewModel.cs`, `Tray*.cs` -- startup (`--tray`), window with Status and Settings navigation, close-hides, tray menu, Quit, capture exclusion on every window and the menu popup.
- [ ] `tests/ReplayBuffer.App.Tests/` (new, in `ReplayBuffer.slnx`) -- against a `serve --instance ReplayBuffer-Test-<guid>` engine: sender commit sees `settings.changed`; stale basis gets the conflict result with the current document; `EngineConnection.ShutdownAsync` leaves no engine process; on an STA thread, a shown `MainWindow` reports excluded from capture.

**Acceptance Criteria:**
- Given the App running from the tray, when the builder opens the Main App, closes its window, then chooses Quit, then the tray stays after the close, and after Quit neither `ReplayBuffer.App` nor `ReplayBuffer.Engine` is running.
- Given an engine at revision N, when the sender sends with basis N, then `settings.changed` with revision N+1 arrives and the reply is N+1; when it sends again with basis N, then it gets the conflict result holding revision N+1.
- Given the tray's Start with Windows item, when it is toggled, then the snapshot's `startWithWindows` flips and the item's check follows the document.
- Given the solution, when `dotnet build ReplayBuffer.slnx` and `dotnet test --solution ReplayBuffer.slnx` run, then 0 warnings and all tests pass.

## Implementation Notes

- Implemented directly, with no implementation subagent, as 1.5 was.
- **`IEngineSession`** (`Status`, `Problem`, `State`, `Changed`, `SendAsync`) is what the view models and the sender see; `EngineConnection` implements it, and the tests use a synchronous `FakeEngineSession`. Story 8's supervisor builds on `EngineConnection`.
- **The tray menu and the General page share one property,** `GeneralSettingsPage.StartWithWindows`: the menu item binds to it two-way, so both send through the sender and both follow the document.
- **The engine is copied beside the App** by `CopyEngineBesideApp` (an `MSBuild GetTargetPath` call plus a copy of the engine's output folder), after a build-order-only `ProjectReference`. `ReplayBuffer.App.Tests` references the engine project normally to get its exe, as `Engine.Tests` does.
- **The tray icon is a `GeneratedIconSource`** (a white "R" on red), so there's no icon asset. `ForceCreate(enablesEfficiencyMode: false)`.
- **The tray menu's popup is excluded from capture** through `PresentationSource.AddSourceChangedHandler`, as soon as it has a window. `TrayTests` opens the menu off screen and checks the affinity.
- The spine's AD-2, layer table and Structural Seed record `Platform.Desktop`.
- **Verified:**
  - `dotnet build ReplayBuffer.slnx` has 0 warnings.
  - `dotnet test --solution ReplayBuffer.slnx`, after the review patches: 234 passed, 0 failed and 8 skipped (the FFmpeg tests, because `native/ffmpeg` isn't fetched in this worktree), with no engine left running. App.Tests (17) passed 3 runs in a row before the patches.
  - A UI Automation smoke run of the real App: it launched the engine hidden and showed "Connected to the engine."; closing the window left both processes running; toggling Start with Windows twice took the settings revision to 2. The tray itself wasn't driven; that's the builder's hand check.

## Plan Change Log

## Review Triage Log

Lens `quick`, iteration 0, run inline against the full diff. Verdicts: high 0, medium 1, low 2, false 0, maybe-false 0.

| # | Finding | Verdict | Route | Evidence |
|---|---------|---------|-------|----------|
| 1 | `EngineConnection.ConnectAsync` left an engine it had launched running when the engine never answered within `LaunchTimeout`. With no client, Quit's `ShutdownAsync` returned at once, so the engine was orphaned. | medium | patch | It's reachable on a slow first start: the status is `NotRunning`, and `ShutdownAsync` sees no client. Fixed: on the timeout it kills the process it launched (it never served) and says why in `Problem`. |
| 2 | `EngineConnectionTests.DisposeAsync` killed recorded PIDs blindly. If a PID were reused, it would kill an unrelated process. | low | patch | Unlikely, but the fix is a single name check: it now kills only a `ReplayBuffer.Engine` process. |
| 3 | `GeneralSettingsPage`'s fire-and-forget send could leave an unobserved exception if the client were disposed mid-send (`ObjectDisposedException` from the client's write lock). | low | reject | It's only reachable during Quit, after the App has disposed the connection. An unobserved task exception doesn't crash .NET, and guarding it would add a branch for a path the user never sees. |

## Design Notes

- One window, two areas: Status (connection, pid, seq, capture state, warnings, settings revision) and Settings (page list + content). Fewer windows means one close-hides rule and one exclusion hook; epic 7's Library becomes a third area.
- "Reload" on conflict needs no fetch: the event that moved the revision reached the client before the conflict reply, so `State.Settings` is already current; the page re-renders from it and shows "Settings changed elsewhere; reloaded".
- `EngineConnection` owns the engine `Process` only when it launched it; story 8 adds supervision on top of this class.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx` -- expected: 0 warnings, 0 errors.
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass.

**Manual checks:**
- Run the App: open from tray, close (stays in tray), toggle Start with Windows (check follows), Quit; Task Manager shows no `ReplayBuffer.App`/`ReplayBuffer.Engine`.
