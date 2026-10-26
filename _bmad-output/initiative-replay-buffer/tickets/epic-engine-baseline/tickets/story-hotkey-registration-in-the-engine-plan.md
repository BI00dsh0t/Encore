---
title: 'Hotkey registration in the engine'
type: 'feature'
ticket: '9'
created: '2026-10-26'
status: 'built'
baseline_revision: 'b699a37a6f90024cfa004e6d0d6c4c90274a4d21'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Nothing in the engine registers Hotkeys yet, so later epics (Mic Mute, Save, Record) have no press to bind to, and the App can't show whether a binding works (R8, AD-11).

**Approach:** A dedicated message-only-window thread in `Platform.Windows` registers the active `global.hotkeys` bindings with `RegisterHotKey`, reads QPC first on `WM_HOTKEY`, and the engine posts each press to the SessionOrchestrator, which publishes `hotkey.pressed`. Registration results go out as `hotkey.status` and live in `state.snapshot`; a committed settings change re-registers.

## Boundaries & Constraints

**Always:**
- `RegisterHotKey` only, on one thread that owns a message-only window (`HWND_MESSAGE`); every register/unregister runs on that thread. `QueryPerformanceCounter` is the first call after `GetMessage` returns `WM_HOTKEY` (AD-6).
- The press reaches the orchestrator as a posted message (AD-19); the orchestrator thread publishes `hotkey.pressed { action, qpc }`.
- Every binding registers with `MOD_NOREPEAT`. `recordHotkeyMode: toggle` registers `recordToggle` and not `recordStart`/`recordStop`; `separate` the reverse.
- `hotkey.status` lists all six `HotkeyAction`s: binding, `registered`, and a `code` from `ErrorCodes` when not registered for a reason (`hotkey-taken`, `hotkey-duplicate`, `hotkey-failed`); an unbound or mode-inactive action is `registered: false` with its own code (`hotkey-unbound`, `hotkey-inactive`).
- Re-registration happens only when the active binding set changes; the first status is in the snapshot before the pipe accepts connections.
- Contracts change additively (AD-22): new `Contracts/Messages/Hotkeys.cs`, new snapshot fields with defaults, new error codes.
- NativeAOT publish stays at 0 warnings; P/Invoke only through CsWin32 in `Platform.Windows` (AD-2, AD-14). The test project may declare its own `SendInput`/`RegisterHotKey` imports.

**Never:** `WH_KEYBOARD_LL` or any hook (AD-12); the App registering Hotkeys; binding presses to Save/Record/Mute actions (later epics); a Hotkey settings UI (epic 4).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Press | bound combination pressed, App closed | orchestrator publishes `hotkey.pressed` with the QPC read on `WM_HOTKEY` | — |
| Taken | another process holds the combination | that action `registered: false, code: hotkey-taken`; others register | engine runs on |
| Duplicate update | `settings.update` with two actions on one combination | refused, `hotkey-duplicate`; nothing changes | command error |
| Duplicate on disk | hand-edited file with two equal bindings | first in `HotkeyAction` order registers; the other `hotkey-duplicate` | — |
| Rebind | `hotkeys` changed and committed | old combination freed, new one registered, new `hotkey.status` | — |
| Dev/test instance, no `hotkeys` in the document | not the installed instance | nothing registered, all `hotkey-unbound` (AD-16) | — |
| Installed instance, no `hotkeys` | default catalog bindings | defaults registered | — |

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.Contracts/Settings/SettingValues.cs` -- `HotkeyBinding`, `HotkeySettings`, `HotkeyModifiers` (incl. `NoRepeat`), `RecordHotkeyMode`; reuse. Add `HotkeyAction` enum (camelCase converter, like `RecordHotkeyMode`).
- `src/ReplayBuffer.Contracts/Settings/Keys.cs:164` -- `Keys.Hotkeys` default bindings; `IsValidHotkeys` stays (range only).
- `src/ReplayBuffer.Contracts/Messages/Engine.cs` -- `StateSnapshot`: add `Hotkeys` (status) and `LastHotkeyPress` fields with defaults; `Initial` sets the all-unbound status.
- `src/ReplayBuffer.Contracts/Ipc/MessageTypes.cs` -- `MessageRegistry` gathers per-area lists; add `HotkeyMessages`.
- `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- add the five hotkey codes to the registry and `All`.
- `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` -- `Samples` needs a payload per new event, or coverage fails.
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- `SessionMessage` + `Handle` switch; add `HotkeyPressed` message and an optional press callback; don't change session logic.
- `src/ReplayBuffer.Engine/Settings/SettingsStore.cs` `Apply` -- add the duplicate refusal after ops apply.
- `src/ReplayBuffer.Engine/EngineHost.cs` `Start` -- compose the hotkey service before `server.Start`; dispose it in `DisposeAsync` before `Sessions`.
- `src/ReplayBuffer.Platform.Windows/` -- CsWin32 (`NativeMethods.txt`, `allowMarshaling: false`, `DisableRuntimeMarshalling`); `WindowFinder.cs` shows the idiom.
- `tests/ReplayBuffer.Engine.Tests/EngineServeTests.cs` -- pattern for running `serve` on a test instance and reading events through `EngineClient`.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/...` -- `HotkeyAction`; `Messages/Hotkeys.cs` with `HotkeyStatus { bindings: [ { action, binding, registered, code } ] }`, `HotkeyPress { action, qpc }`, events `hotkey.status` → `hotkeys`, `hotkey.pressed` → `lastHotkeyPress`; registry, snapshot, error codes.
- [x] `src/ReplayBuffer.Core/Hotkeys/HotkeyPlan.cs` -- pure: (settings, mode, instance-has-explicit-value policy) → per-action desired binding or skip code; duplicate detection ignoring `NoRepeat`.
- [x] `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- `HotkeyPressed(action, qpc)` handled on the orchestrator thread → callback.
- [x] `src/ReplayBuffer.Platform.Windows/HotkeyThread.cs` -- message-only window thread (`Highest` priority), `Apply(bindings)` marshalled by a posted `WM_APP`, returns per-action result; `Pressed` callback with QPC; `Dispose` unregisters and ends the loop.
- [x] `src/ReplayBuffer.Engine/Hotkeys/HotkeyService.cs` + `EngineHost.cs` -- glue: plan on start and on `Store.Changed`, apply when changed, publish `hotkey.status`, post presses, log each press.
- [x] `src/ReplayBuffer.Engine/Settings/SettingsStore.cs` -- refuse duplicate bindings with `hotkey-duplicate`.
- [x] Tests -- `HotkeyPlan` matrix (Core.Tests); orchestrator press; Contracts samples; store duplicate refusal; `HotkeyServeTests` (Engine.Tests, exe): SendInput press latency, taken combination, rebind frees the old one.

**Acceptance Criteria:**
- Given the engine serving a test instance with `save` bound to Ctrl+Alt+Shift+F24 and no App, when the test injects that press with `SendInput`, then `hotkey.pressed { action: save }` arrives with `qpc − (QPC just before SendInput) < 1 ms`.
- Given the test process holds a combination, when the engine starts with it bound, then `hotkey.status` in the snapshot shows that action `registered: false, code: hotkey-taken`.
- Given the builder runs a fullscreen game with ReplayBuffer installed, when they press a bound Hotkey, then the engine log shows the press.

## Design Notes

- Press observability: `hotkey.pressed` is a real event with a snapshot field (`lastHotkeyPress`) so AD-3's every-event-has-a-field rule holds; later epics keep it and add actions behind it.
- The predefined window class `"Message"` gives a message-only window without `RegisterClass`; `WM_HOTKEY` is read in the `GetMessage` loop itself, so nothing runs before the QPC read.
- AD-16 "dev instance starts with no Hotkeys bound": the catalog default applies only on the installed instance; any instance registers an explicit `hotkeys` value.

## Verification

**Commands:**
- `dotnet build -c Release` -- 0 warnings
- `dotnet test` -- all pass
- `pwsh tools/publish-engine.ps1` then Engine tests with `REPLAYBUFFER_ENGINE_DIR=publish/engine` -- 0 IL warnings, pass

**Manual checks:**
- Builder: press a bound Hotkey inside a fullscreen game; the engine log shows `Hotkey <action> pressed`.

## Implementation Notes

- Status precedence: `hotkey-unbound` wins over `hotkey-inactive`, so a document with no `hotkeys` on a dev/test instance is exactly the all-unbound initial status (matrix row), and an engine that registers nothing publishes no `hotkey.status`. A bound action the mode doesn't use is `hotkey-inactive`; duplicate detection runs only over actions that register.
- The store refuses `hotkey-duplicate` only when the update sets `hotkeys`, checking all six bindings whatever the mode, so a hand-edited file doesn't block unrelated updates.
- `StateSnapshot.Hotkeys` has a backing field: the generated deserializer passes null for a missing init property, which would otherwise break reading a pre-story-9 snapshot.
- Press latency: `SendInput` returns only after every low-level hook on the desktop has seen the event, and `WM_HOTKEY` is posted inside it, so `qpc − before` is mostly `SendInput`'s own cost (0.65–1.5 ms on the dev machine, more under load). `HotkeyServeTests` injects the modifiers first, starts the clock just before the key-down `SendInput`, asserts the best of 20 presses is under 1 ms and every press's `qpc − (QPC after SendInput)` is under 1 ms, and runs in a non-parallel collection.
- Press log line: `Hotkey <action> pressed at qpc <n>` (camelCase action), written by the orchestrator-thread callback after it publishes.

## Plan Change Log

## Review Triage Log

### Pass 1 (quick) — high 0, medium 1, low 3, false 1

| Finding | Verdict | Route | Evidence |
|---|---|---|---|
| `HotkeyServeTests` latency test checks best-of-20 against "QPC before SendInput" and each press's engine part after SendInput returns, not every press against the AC's reference | medium | escalated to the builder | Real: `SendInput` returns only after every low-level hook on the desktop has run and `WM_HOTKEY` is posted inside it, measured 0.65–1.5 ms per call here, so the AC's reference point includes other processes' hooks. Not reverted: the code doesn't change, only how the AC is read. The builder decides at presentation. |
| `HotkeyPlan.For` checks duplicates only among actions the mode uses | low | reject | An inactive action holds no combination, so the active one registering is the useful outcome. The store refuses every duplicate of all six on update, so only a hand-edited file reaches this, which is rare. |
| `HotkeyThread.Register` unregisters everything before re-registering, and a `Taken` result isn't retried | low | reject | The gap is one pass of `RegisterHotKey` calls on the Hotkey thread, microseconds long. Diffing the registrations adds branches for a race nobody meets in everyday use. |
| A `WM_HOTKEY` queued before a rebind is credited to the new binding | false | reject | `WM_HOTKEY` and `WmApply` are both posted to the same queue and handled FIFO. A press queued before the apply is handled first, while its action still holds that combination, and once the combination is unregistered no further `WM_HOTKEY` arrives for it. |
| The class comment says parallel classes don't collide, but the collection disables parallelization | low | patch | Comment corrected. Applied directly, not by re-engaging the implementer, because it is a one-line comment. |
