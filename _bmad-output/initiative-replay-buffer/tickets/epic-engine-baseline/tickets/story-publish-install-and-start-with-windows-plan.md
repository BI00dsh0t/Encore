---
title: 'Publish, install and Start with Windows'
type: 'feature'
ticket: '13'
created: '2026-10-26'
status: 'in-progress'
baseline_revision: '8c4cffaa131b38e0e46e1b84db790c596c2eac48'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** There is no installable build: the engine publishes alone to `publish/engine/`, nothing copies a build to `%LOCALAPPDATA%\Programs\ReplayBuffer`, and the tray's Start with Windows item only saves `startWithWindows`; no Run key is ever written (R3, AD-16).

**Approach:** A publish script composes one folder: the framework-dependent WPF App plus the NativeAOT engine (story 3's `tools/publish-engine.ps1`) and the FFmpeg DLLs, trimmed of `ffmpeg.exe`, `ffplay.exe`, `ffprobe.exe` and PDBs. An install script mirrors it into the install folder and relaunches (update = tray Quit, run it again). The App keeps the HKCU Run value in step with the committed `startWithWindows`, in the installed instance only.

## Boundaries & Constraints

**Always:**
- The Run value is `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, name `ReplayBuffer`, data `"<install>\ReplayBuffer.App.exe" --tray`.
- The key follows the engine's committed settings (snapshot and `settings.changed`), not the optimistic toggle; a refused update leaves the key alone.
- Only `Instance.InstalledName` reads or writes the Run value. `ReplayBuffer-Dev` and test instances never touch it, on or off.
- Both processes stay unelevated; no HKLM, no scheduled task.
- The install refuses while an installed App or engine is running, and tells the user to Quit from the tray.

**Never:**
- No installer, MSIX, uninstaller or self-update (Structural Seed: no installer).
- Don't touch Core, Media or the engine's capture and session code (story 1.6 runs in parallel).
- Don't change `StartupApproved` (Task Manager's disable switch) entries.
- Tests never write the real Run key.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Turn on | installed instance, toggle on, engine commits | Run value written with the install path and `--tray` | — |
| Turn off | installed instance, toggle off, engine commits | Run value deleted | Already absent → no error |
| Start, setting on, key missing | installed App connects, snapshot `true` | Run value written | — |
| Start, setting off, stale key | installed App connects, snapshot `false` | Run value deleted | — |
| Dev instance | toggle either way | setting saved; Run value untouched | — |
| Refused update | engine rejects or conflicts | toggle reverts; Run value unchanged | — |
| Registry write fails | access denied / IO error | App keeps running; key left as is | caught, not thrown into the dispatcher |
| Install while running | installed App or engine process alive | nothing copied | script throws "Quit ReplayBuffer from the tray, then run again" |

</frozen-after-approval>

## Code Map

- `tools/publish-engine.ps1` -- story 3's AOT publish to `publish/engine/`; reuse as is. Its output holds the exe, FFmpeg DLLs, the three FFmpeg exes and PDBs (`Directory.Build.targets` `CopyFFmpegNative`). Engine tests use that folder via `REPLAYBUFFER_ENGINE_DIR`, so don't trim it there.
- `tools/fetch-ffmpeg.ps1` -- style reference (`#Requires -Version 7`, comment help, `$ErrorActionPreference = 'Stop'`, StrictMode).
- `src/ReplayBuffer.App/ReplayBuffer.App.csproj` -- `CopyEngineBesideApp` copies the JIT engine into `OutDir` after Build; publish output is unaffected.
- `src/ReplayBuffer.App/ShellApp.cs` -- `OnStartup` builds `EngineConnection` and `MainViewModel`; `instance` is in scope; `_engine.Changed` fires on background threads, so marshal with `Dispatcher.BeginInvoke`.
- `src/ReplayBuffer.App/Settings/GeneralSettingsPage.cs` -- `StartWithWindows` sends the setting; its doc says the Run key is story 13's. Update the comment only.
- `src/ReplayBuffer.App/Settings/SettingsSender.cs` -- `SettingsValues.Global(document, Keys.StartWithWindows)` reads the committed value.
- `src/ReplayBuffer.Contracts/Instance.cs` -- `Instance.InstalledName`; `Resolve` already makes only `%LOCALAPPDATA%\Programs\ReplayBuffer\` the installed instance.
- `src/ReplayBuffer.Engine/Supervision/AppSupervisor.cs` -- relaunches the App beside it with `--tray`; no change.
- `src/ReplayBuffer.Platform.Desktop/` -- the App's Win32 calls; home of the Run key wrapper. `IsAotCompatible` applies; `Microsoft.Win32.Registry` is fine.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Platform.Desktop/RunKey.cs` -- `RunKey(string subKey, string valueName)` over HKCU with `Read()`, `Write(string command)`, `Delete()` (missing value is fine); `RunKey.Startup` for the real path and name -- a testable seam that never needs the real key in tests.
- [x] `src/ReplayBuffer.App/StartupRegistration.cs` -- `StartupRegistration(Instance, RunKey, string appPath)`; `Sync(SettingsDocument)` applies the committed value when it differs from the last synced one (first call always reconciles); no-op unless installed; catches registry `UnauthorizedAccessException`/`IOException`/`SecurityException` -- the one place the Run key policy lives.
- [x] `src/ReplayBuffer.App/ShellApp.cs` -- create it with `RunKey.Startup` and `Environment.ProcessPath`; on `_engine.Changed`, `Dispatcher.BeginInvoke` a `Sync` of `_engine.State.Settings` when present.
- [x] `src/ReplayBuffer.App/Settings/GeneralSettingsPage.cs` -- fix the doc comment.
- [x] `tools/publish.ps1` -- `-Output` (default `publish/ReplayBuffer`): run `publish-engine.ps1`, clean `-Output`, `dotnet publish` the App `-c Release -r win-x64 --self-contained false`, copy `ReplayBuffer.Engine.exe` and `*.dll` from the engine output, delete `*.pdb`, then assert the layout (both exes, the 7 FFmpeg DLLs, no other `.exe`) and print it.
- [x] `tools/install.ps1` -- `-Source` (default `publish/ReplayBuffer`), `-Destination` (default `$env:LOCALAPPDATA\Programs\ReplayBuffer`), `-NoLaunch`. Check the source layout, refuse if a process image lives under the destination, `robocopy /MIR` (exit < 8 is success), then start `ReplayBuffer.App.exe`. The help text states the update procedure: tray Quit, `publish.ps1`, `install.ps1`.
- [x] `tests/ReplayBuffer.Platform.Desktop.Tests/RunKeyTests.cs` -- write/read/delete/delete-missing on `Software\ReplayBuffer-Test-<guid>\Run`, deleting the subkey after.
- [x] `tests/ReplayBuffer.App.Tests/StartupRegistrationTests.cs` -- the matrix rows for on, off, start reconcile both ways, dev and test instances leaving a pre-set value untouched, unchanged value not rewritten.

**Acceptance Criteria:**
- Given `pwsh tools/publish.ps1`, when it finishes, then `publish/ReplayBuffer/` holds `ReplayBuffer.App.exe`, `ReplayBuffer.Engine.exe` and the FFmpeg DLLs side by side, with no `ffmpeg.exe`, `ffplay.exe`, `ffprobe.exe` or `.pdb`.
- Given the installed folder and Start with Windows on, when the user signs out and back in, then `ReplayBuffer.App.exe` and `ReplayBuffer.Engine.exe` run from the install folder, with no window shown.
- Given the installed App, when Start with Windows is turned off, then `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v ReplayBuffer` finds nothing.
- Given a dev build (`ReplayBuffer-Dev`), when Start with Windows is toggled, then the Run key is unchanged.

## Implementation Notes

- `publish.ps1` passes `-p:ValidateExecutableReferencesMatchSelfContained=false` to the App's `dotnet publish`: with `-r win-x64` the engine reference (build order only) is a self-contained AOT exe, and the SDK otherwise fails with NETSDK1151.
- The App's publish also carries the JIT engine's content through that reference (`ffmpeg.exe`, `ffplay.exe`, `ffprobe.exe`, `ReplayBuffer.Engine.runtimeconfig.json`); `publish.ps1` deletes those after copying the AOT engine over, so the folder holds 19 files (217 MB).
- `StartupRegistration` leaves its last-synced value unset after a failed registry write, so the next committed settings retry it. Failures go to `Trace.TraceWarning`.
- `install.ps1` was exercised against a scratch destination: mirror (removes extra files), missing source, and refusal while a process runs from the destination. The real install, toggle and sign-in checks are the builder's.


## Plan Change Log

## Review Triage Log

## Design Notes

- The App, not the engine, owns the Run key: AD-16 has Start with Windows launch the App, the App is the supervisor, and the setting is `AppliesAt.Immediate`. Syncing from committed settings means a fresh install with the setting already on writes the key on first start, and an install moved elsewhere fixes its path.
- The engine's output is not trimmed in `publish/engine/`: the engine tests run ffprobe from there. The compose step copies only what the install needs.
- Claude's own processes see a virtualized `%LOCALAPPDATA%` and HKCU, so the builder runs `install.ps1`, the toggle and the sign-in check.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Debug` -- expected: 0 warnings, 0 errors.
- `dotnet test ReplayBuffer.slnx` -- expected: all pass.
- `pwsh tools/publish.ps1` -- expected: layout check passes and lists the folder.

**Manual checks (builder):**
- `pwsh tools/install.ps1`; toggle Start with Windows on in the tray; `reg query` shows the value; sign out and in; Task Manager shows both processes from the install folder and no window; toggle off; `reg query` finds nothing.
