---
title: 'Monitor Recording by its own Hotkey'
type: 'feature'
ticket: '9'
created: '2026-11-04'
status: 'in-review'
baseline_revision: '4390a0e6adab46146917f7229c6a8af52b186a87'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/CLAUDE.md'
warnings: ['oversized']
deferred:
  - summary: >-
      The Monitor session opens its own D3D11 device on its monitor's adapter, where AD-7 says the engine uses one device.
    evidence: |-
      CaptureSession.Open calls D3D11Device.CreateForMonitor for a Monitor session; sharing the game session's device across sessions is a larger change. The plan's Design Notes chose it, pending the builder's review.
    location: >-
      src/ReplayBuffer.Engine/Sessions/CaptureSession.cs (Open)
    severity: medium
---

<intent-contract>

## Intent

**Problem:** `monitorRecordToggle` is registered but does nothing: no session captures a monitor, the orchestrator holds one session, nothing counts encoder chains (AD-7), and there are no separate Monitor Start/Stop keys.

**Approach:** The SessionOrchestrator gains a second slot, the Monitor session: a `SessionKind.Monitor` session from the same factory, on the Global snapshot (profile null), Global Track Layout, capturing `monitorRecordingMonitor` (null = primary) with a WGC monitor item, whose only output is one Recording written by the existing `RecordingWriter` into `<Output Folder>\Monitor Recordings`. It runs beside the game session and its Recording, never touches the Target. The orchestrator counts chains (`ICaptureSession.Chains`) and refuses a third with `encoder-budget`; while a Monitor Recording runs, a Replay Buffer ChainKey change is not built in advance but switched at the next Reset by tearing down first (`ICaptureSession.SwitchChain`), raising `reset-gap`.

## Boundaries & Constraints

**Always:** Contracts change additively only (AD-22): `HotkeyAction.MonitorRecordStart`/`MonitorRecordStop` appended, `HotkeySettings` gains `MonitorRecordStart = null, MonitorRecordStop = null` as trailing optional params (old JSON still reads), `ErrorCodes.EncoderBudget = "encoder-budget"` (in `All`). Monitor keys follow `recordHotkeyMode` exactly as Record keys (`HotkeyPlan.IsActive`). Monitor Recording path: `Library.BeginWrite(path, null)`, `Open.Opened/Closed`, so crash recovery and `recording-interrupted` apply unchanged; it never starts by itself. `CaptureState.MonitorRecording` is Running from start to stop. Every Monitor path is on the orchestrator thread; writers post back.

**Never:** no DXGI Desktop Duplication fallback (6.10); no Recordings settings page or `monitors.list` (6.11); no Hotkeys page rows logic beyond labels/bindings for the two new actions; no whole-monitor capture in game sessions; no change to game Recording behaviour; no sound through speakers in the default run; real-capture tests only as `[Fact(Explicit = true)]` + `[Trait("Category","Heavy")]`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Toggle, no game | `monitorRecordToggle` press, toggle mode | Monitor session starts; at its anchor a Recording starts in `Monitor Recordings`; `MonitorRecording=Running`; `recording.started` | — |
| Toggle again | second press | writer stops at press, posts back, `MonitorRecording=Off`, `recording.stopped` `stopped`, Monitor session ended | — |
| Separate mode | `monitorRecordStart`/`Stop` | start only when none runs, stop only when one runs; toggle ignored (Info) | — |
| Press before anchor then stop | stop before first frame | session ended, no Recording file left, state Off | — |
| Beside game | game session + game Recording + Target switch | Monitor keeps running; Target switch is not held by it; game Recording unchanged | — |
| Budget | game session reports `Chains == 2` after dropping a prepared chain | Monitor start refused, `Recordings.Warn(encoder-budget, …)`, nothing started | — |
| Prepared chain | game session `Pending` at Monitor press | `PrepareChain(null)` first, then Monitor starts | — |
| ChainKey change during Monitor | `SettingsUpdated` needs another chain | no `PrepareChain(next)`; at the next Clip's Reset `SwitchChain(next)`; spec takes it; session raises `reset-gap` | refused switch retried at the next Reset |
| Disk full | Monitor writer fails disk-full | stopped `disk-full`, `DiskFull()` notice, session ended; game Replay Buffer untouched | — |
| Monitor session closes/fails | `Closed`/`Failed` from Monitor session | Recording stopped `session-ended` / failure reason, state Off | — |
| Shutdown | Dispose mid-Monitor | finished as `shutdown` within `ShutdownWait` like a game Recording | left open → interrupted next start |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- single `_active`, `_recording`, `_ending`. `Handle` routes Hotkeys (~342); `OnRecordPress`/`BeginRecording`/`OnRecordingWritten`/`OnDiskFull` are the game Recording path to mirror (reuse `RecordingRun`, add a `Monitor` flag; `OnRecordingWritten` must skip game-only work — pending Target, `ApplySettings`, `OnDiskFull`'s ring logic — for a Monitor run). `ApplySettings` (~979) and `OnClipWritten` (~937) are where the in-advance vs at-Reset switch is decided. `Run` (~266) shutdown waits on `_ending`/`_recording`: add the Monitor run and end the Monitor session with `Shutdown`. `ToggleMicMute` also sets the Monitor session. `Events` class tags posts by session id: the Monitor session gets its own id; its `Closed`/`Failed`/`Anchored` must route to the Monitor slot.
- `src/ReplayBuffer.Core/Sessions/CaptureSessionPorts.cs` -- add default members: `ICaptureSession.Chains => 1` (live encoder chains, excluding ones being torn down), `ICaptureSession.SwitchChain(SessionSpec next) => false` (tear down then build, at a Reset), `ISessionEvents.Anchored(long qpc) { }` (first frame; Record can now succeed).
- `src/ReplayBuffer.Core/Sessions/TargetGames.cs:132` -- `Recordings` record: add optional `MonitorStarted`, `MonitorStopped` (fall back to `Started`/`Stopped` when null) and `Warn` (`Action<string,string>?` code, message). `ChainKey` (:193) already keys on Kind+ProcessId.
- `src/ReplayBuffer.Core/Output/OutputPathAllocator.cs` -- add `AllocateForMonitor(string outputFolder, long qpc)` → `Reserve(Path.Combine(outputFolder, ProfileFolders.MonitorRecordings), "Monitor", LocalTimeOf(qpc))`.
- `src/ReplayBuffer.Core/Hotkeys/HotkeyPlan.cs:80` -- `IsActive`: `MonitorRecordToggle` toggle-only, `MonitorRecordStart/Stop` separate-only.
- `src/ReplayBuffer.Core/Feedback/FeedbackRouter.cs` (:114, :141, :165) -- `_replacing` is the game Recording's: add `MonitorRecordingStarted/Stopped` that raise the same codes and sounds, worded "Monitor Recording …", never touching `_replacing`; disk-full stop silent as now.
- `src/ReplayBuffer.Contracts/Settings/SettingValues.cs` (:81–113), `Keys.cs:271` (`IsValidHotkeys` covers the two new), `ErrorCodes.cs` (add + `All`). Check `Json.HotkeySettings` (source-gen) reads JSON lacking the new fields.
- `src/ReplayBuffer.App/Settings/HotkeysPage.cs` (:222–257) -- rows come from `Enum.GetValues<HotkeyAction>()`: add `Label` and `with` arms for the two new actions.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- `Open` (:249) uses `WindowFinder.FromHandle` and `D3D11Device.Create(hwnd)`; `NewCapture` (:763) builds `WindowCapture`; `StartOutput` (:858) is the anchor (raise `_events.Anchored`); `Record` (:525) requires `_options.Replay` — allow a Monitor session; `Save`/`PrepareChain`/`RestartChain` return null/false for Monitor; `Retarget` throws for Monitor. `RestartChain`/`Restart` (:620–760) are the tear-down-then-build machinery: `SwitchChain(next)` reuses it with `next`'s spec into a **fresh ring** (`restartOf: null`, old ring disposed) and raises `ResetGap` with the gap, posting no `ChainRestarted`. `Chains`: count `_current`, `_armed`, `_retiring`, `_prepared`, a build running, under `_chainLock`.
- `src/ReplayBuffer.Engine/Sessions/EncoderChain.cs:192` -- `Build`: a Monitor spec gets `_tap = new RecordingTap(new NullPacketSink())`, no ring, no muxer.
- `src/ReplayBuffer.Platform.FFmpeg/RecordingTap.cs` -- takes any `PacketSink`; add an internal/public `NullPacketSink` (discards) in Platform.FFmpeg.
- `src/ReplayBuffer.Platform.Capture/WindowCapture.cs` -- `CreateItem(hwnd)` (:287) uses `IGraphicsCaptureItemInterop.CreateForWindow`; add a monitor constructor/factory using `CreateForMonitor(HMONITOR)`, sized from the monitor rect, without the `IsWindow` watchdog (item `Closed` only). `D3D11Device.Create(hwnd)` (:51, :248) picks the adapter via `MonitorFromWindow`: add an HMONITOR overload. New `Platform.Capture` `MonitorFinder.Find(string? deviceName)` (EnumDisplayMonitors + GetMonitorInfo `MONITORINFOEX.szDevice`; null or unknown → primary, unknown logged); add the Win32 names to NativeMethods.txt.
- `src/ReplayBuffer.Engine/EngineHost.cs` (~348–453) -- wire `MonitorStarted/Stopped` (publish the same `RecordingMessages.Started/Stopped`, call the router's Monitor methods) and `Warn` (`warnings.Raise(code, Warning, "recordings", message, overlay: true)`).
- Tests: `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- `NewRecordingOrchestrator` (:1857), `FakeFactory` (:1932), `FakeSession` (:2065), `FakeRecording` (:2002), `FakeLibraryWrites` (:2032), `Press` (:1846). `tests/ReplayBuffer.Engine.Tests/RecordingCaptureTests.cs` rig (~440–524), `SessionTestKit` (`OpenWindow`, `Probe`, `AssertDecodesCleanly`), `ToneHub`.

## Tasks & Acceptance

**Execution:**
- [x] Contracts (`SettingValues.cs`, `Keys.cs`, `ErrorCodes.cs`) -- the additive actions, bindings and `encoder-budget`.
- [x] `HotkeyPlan.cs`, `HotkeysPage.cs` -- mode gating; labels "Monitor Recording (start)"/"(stop)" and bindings.
- [x] `CaptureSessionPorts.cs`, `TargetGames.cs` (`Recordings`), `OutputPathAllocator.cs` -- ports and the Monitor path.
- [x] `SessionOrchestrator.cs` -- the Monitor slot per the matrix: press handling (live mode), start (budget check after dropping a prepared chain; Global snapshot; `SessionAudio.FromLayout`; `CaptureTarget(0, 0, null)`), Recording at `Anchored`, stop, post-back, session end, Closed/Failed, mic mute, shutdown; `ApplySettings`/`OnClipWritten` switch-at-Reset while it runs; update the class doc.
- [x] `FeedbackRouter.cs` -- Monitor start/stop methods.
- [x] Engine: `CaptureSession.cs`, `EncoderChain.cs`, `NullPacketSink`, `WindowCapture.cs` monitor item, `D3D11Device.cs` overload, `MonitorFinder`, `EngineHost.cs` wiring.
- [x] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` (+ a small `MonitorRecording…` section or file) -- one test each: toggle with no game writes in `Monitor Recordings` with Global audio and `MonitorRecording` Running→Off; beside a game Recording, a Target switch neither stops nor waits for it; separate mode Start/Stop; third chain refused with `encoder-budget`; ChainKey change during Monitor → `SwitchChain` at the Reset, never `PrepareChain(next)`; disk full stops it with `DiskFull`; Open/Closed called (crash path). `HotkeyPlan` mode test; `FeedbackRouter` Monitor notices test; Contracts round-trip of old `hotkeys` JSON.
- [x] `tests/ReplayBuffer.Engine.Tests/MonitorRecordingCaptureTests.cs` -- Heavy/Explicit: a toggle with no game writes a file that ffprobe reads with every Global Track; a monitor capture with a full-screen magenta window marked `WDA_EXCLUDEFROMCAPTURE` has no magenta; a Monitor Recording beside a game Recording on the synthetic window runs two chains.

**Acceptance Criteria:**
- Given no game, when the Monitor Record toggle is pressed twice, then a playable file in `Monitor Recordings` holds video and every Global Track, and `capture.stateChanged` reported `monitorRecording` running between.
- Given a Monitor Recording beside a Replay Buffer and game Recording, when the Target switches, then the Monitor Recording neither stops nor delays the switch, and at most two chains ran.
- Given a game session already using two chains, when a Monitor Recording is asked for, then it is refused with `encoder-budget`.
- Given a Monitor Recording, when a ChainKey-changing setting meets the next Reset, then the old chain is torn down before the new one is built, and `reset-gap` is raised.
- Given a Monitor Recording, when the disk fills, the engine is killed, or the engine restarts, then the file plays, `disk-full`/`recording-interrupted` are raised as for Recordings, and no Monitor Recording resumes.
- Given the Main App and Overlay open, when the monitor is recorded, then none of their pixels appear (5.9's `WDA_EXCLUDEFROMCAPTURE`; builder check in the Human Check Queue).

## Implementation Notes

- `CaptureState.MonitorRecording` goes Running at the press (the Monitor session's start), not at the anchor; `recording.started` follows at the anchor. A stop before the anchor ends the session with no file and no events.
- The encoder budget is `SessionOrchestrator.MaxChains = 2`: a Monitor press is refused when the game session's `Chains + 1` exceeds it, after dropping a prepared chain.
- `CaptureSession.Chains` counts `_retiring` too (as the plan lists), so a press within the moment after a save switch can be refused; harmless, retryable.
- `SwitchChain` reuses `Restart` with the new spec into a fresh ring (old ring disposed with its chain) and always raises `reset-gap` with the gap from the last Reset to the new chain's first keyframe. A failed rebuild fails the session, as a failed forced restart does.
- A failed Monitor session (`Failed`) stops its Recording as `session-ended`; a writer error, if any, gives its own reason.

## Plan Change Log

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 8 findings — high 0, medium 4, low 3, false 0, maybe-false 0 (plus 1 already patched)
- findings:
  - `[medium]` `[patch]` `CaptureSession.Chains` counts an in-advance build PrepareChain(null) just superseded, so a Monitor press right after dropping a building chain is refused with encoder-budget — confirmed: `_building` stays true until the build thread ends. Fixed: a build counts only while still wanted.
  - `[medium]` `[patch]` `StartMonitor` drops the game session's prepared chain before the budget check, so a refused press delays the profile change a Clip — confirmed. Fixed: a refused press re-runs `ApplySettings`.
  - `[medium]` `[patch]` `ReportSwitchGap` keeps waiting after `End` set `_stopped`, so End blocks up to 1 s and raises a false reset-gap — confirmed: the loop only watches flags set after the join. Fixed: the loop also exits, and reports nothing, once stopped. The sub-claim that a second switch in the report window overwrites `_restartThread` is `[low]` `[reject]`: the older thread only logs by then, and the window needs two switches within a second.
  - `[medium]` `[defer]` the Monitor session creates its own D3D11 device, against AD-7's "one D3D11 device" — real; the plan chose it deliberately because sharing one device across sessions is a larger change. Deferred to the builder as a decision awaiting review.
  - `[medium]` `[patch]` AC 4 (teardown before build, reset-gap) is verified only against a Core fake — confirmed. Fixed: a Heavy engine test drives a real switch at a Reset during a Monitor Recording.
  - `[low]` `[reject]` AC 2's Heavy test samples Chains once and doesn't switch the Target — the Target switch is covered in Core; lean tests (builder decision 2026-11-03).
  - `[low]` `[reject]` AC 5 kill/restart isn't tested for a Monitor path — the crash recovery is path-agnostic (`IOpenRecordings`, covered by 6.2), and the Core test checks Opened/Closed; nothing starts a Monitor Recording without a press. Lean tests.
  - `[medium]` `[patch]` a press after the Monitor session closed but before its writer posted back started a second session that never recorded — found while reading the diff and fixed in the orchestrator before the review; the reviewer saw it uncommitted. Committed with the patches.

## Design Notes

- The Monitor session uses its own `D3D11Device` on its monitor's adapter, not the game's single device (AD-7 says one device). Sharing one device across sessions is a larger change; logged as a decision for review.
- The Recording starts at the Monitor session's anchor (its first frame), not at the press, since WGC delivers the first frame within a frame or two.
- `Chains` excludes chains already being torn down; a prepared chain dropped at the Monitor press is torn down within milliseconds, before the Monitor chain builds at its first frame.
- File stem `Monitor_<yyyy-MM-dd_HH-mm-ss>.mp4`.

## Verification

**Guard:** never build or test while `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` exists; wait for its gaps. Heavy tests take the lock themselves.

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- all pass
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build` -- all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- all pass
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- all pass
- `pwsh tools/publish-engine.ps1` -- 0 IL warnings
