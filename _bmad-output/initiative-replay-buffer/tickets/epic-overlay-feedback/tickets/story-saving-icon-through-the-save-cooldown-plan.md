---
title: 'Saving icon through the Save Cooldown'
type: 'feature'
ticket: '3'
created: '2026-11-03'
status: 'built'
baseline_revision: 'e8e59c94bf92794535088c6e77165ecb12999350'
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

**Problem:** An accepted Save press already publishes `capture.stateChanged.cooldownUntil` (story 4.4), but nothing says a Clip is still being written, and the Overlay shows nothing for a save: the user can't see that a press was taken (R2).

**Approach:** Add an additive `clipsWriting` count to `CaptureState` that the SessionOrchestrator sets as each write starts and ends. The Overlay view model shows a green saving icon beside the dot while `cooldownUntil` is in the future or `clipsWriting > 0`, timing the cooldown's end itself with a `TimeProvider` timer.

## Boundaries & Constraints

**Always:**
- `CaptureState` gains a last positional parameter `int ClipsWriting = 0` (AD-22, additive): JSON `clipsWriting`, and JSON without it reads as 0. `CaptureState.Initial` keeps 0.
- The orchestrator is the only producer (AD-19): after an accepted press's write starts (`_writing++`), `SetState(_state with { ClipsWriting = _writing })`; in `OnClipWritten`, after `_writing--`, the same. The existing `CooldownUntil` publish at the press stays where it is. Both happen synchronously on the orchestrator thread, so the event leaves well inside 250 ms of the press.
- A press inside the cooldown publishes no state (unchanged 4.4 behaviour).
- `OverlayViewModel.Saving` is true only when connected and `Capture.ClipsWriting > 0 || Capture.CooldownUntil > time.GetUtcNow()`. When the cooldown is still ahead, a one-shot `TimeProvider.CreateTimer` for the remaining time posts `Refresh`; each `Refresh` disposes the previous timer first. The constructor takes an optional `TimeProvider? time` (default `TimeProvider.System`).
- A snapshot read at reconnect (or a fresh view model) is treated the same as an event: the icon follows whatever `State.Capture` holds.
- The window shows while `Dot != None || Saving`; the icon sits left of the dot (16 DIP, 6 DIP gap), the dot stays where story 5.1 put it, and the dot's ellipse is hidden (not collapsed) when `Dot == None`.
- Lean tests (CLAUDE.md "Tests"); no real-time waits in the default run (manual time drives the cooldown).

**Never:** No `clip.saving` handling in the App (the icon reads snapshot state, epic Notes). No messages, mic icon or red X (5.5, 5.7). No change to how saves, cooldowns, retries or the writer limit work. No settings key for the icon. No Platform.Desktop or EngineHost change.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Accepted press, slow writer | Save press, write gated | a published state with `CooldownUntil` set and `ClipsWriting = 1`, within 250 ms of the press QPC; `ClipsWriting = 0` after the write ends | none |
| Press inside the cooldown | second press 1 s later | no new published state | none |
| Write outlasts cooldown | `ClipsWriting = 1`, cooldown passed | `Saving` true until `ClipsWriting = 0` | none |
| Cooldown outlasts write | `ClipsWriting = 0`, `CooldownUntil` now + 5 s | `Saving` true, false once manual time passes 5 s (timer posts Refresh) | none |
| Reconnect mid-cooldown | new view model over a snapshot with `CooldownUntil` ahead | `Saving` true | none |
| Disconnected | `Status != Connected` | `Saving` false | none |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Messages/Capture.cs` -- `CaptureState` record; add `int ClipsWriting = 0` with a doc line. `CaptureJsonContext` needs no change.
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs:659-735` -- `SaveClip`: cooldown check (673), `SetState(... CooldownUntil ...)` (683), `_writing++` (726). `OnClipWritten` (~760) `_writing--`. `SetState` (809) publishes on change only. Update the class summary sentence about `CooldownUntil` to mention `ClipsWriting`.
- `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- `PressesInsideTheSaveCooldownAreIgnoredAndOneAfterItSaves` (626) is the pattern: `InFolder(folder)` (default 5 s cooldown), `FakeFactory { Save = ... }`, `FakeWrite(writes, gate: ...)` (989), `NewGate()` (851), `NextAsync` (870), `published` queue.
- `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs:96` -- `CaptureStateHasTheSharedShape`: expected JSON gains `"clipsWriting":0` at the end (or set a non-zero count); add one assert that JSON without `clipsWriting` deserializes to 0.
- `src/ReplayBuffer.App/Overlay/OverlayViewModel.cs` -- add `Saving`, the `TimeProvider` and the timer; keep 5.1's hide-before-move / move-before-show ordering for `Dot` and `TargetProcessId`.
- `src/ReplayBuffer.App/Overlay/OverlayWindow.cs` -- widen to icon + gap + dot (`IconSizeDip = 16`, `GapDip = 6`); the icon is a green (e.g. `#43A047`) glyph, say a downward arrow over a bar as a `Path`, left of the dot in a horizontal layout; `Update` shows/hides on `Dot != None || Saving`; `Place` puts the dot at `Monitors.TopRight(...)` and moves the window's x left by the icon and gap, scaled by `size / DotSizeDip`. `ShowWithoutActivation(hwnd, x, y, width, height)` takes the wider width.
- `src/ReplayBuffer.App/ShellApp.cs:64` -- constructs the view model; no change needed with the optional parameter.
- `tests/ReplayBuffer.App.Tests/OverlayTests.cs` -- `Shift`, `After`, `FakeEngineSession` (`State`, `Status`, `RaiseChanged`). The Heavy screen-copy test copies only the dot's square at `TopRight(...)`, which must stay the dot's position.
- `tests/ReplayBuffer.Core.Tests/FeedbackRouterTests.cs:64` -- a `ManualTime : TimeProvider` with `GetUtcNow` and one-shot `CreateTimer`; copy it as a private nested class in `OverlayTests`.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/Messages/Capture.cs` -- add `ClipsWriting` -- the count the icon needs (AD-22 additive).
- [x] `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- publish `ClipsWriting` as writes start and end; update docs.
- [x] `src/ReplayBuffer.App/Overlay/OverlayViewModel.cs` -- `Saving` with the cooldown timer.
- [x] `src/ReplayBuffer.App/Overlay/OverlayWindow.cs` -- draw and place the saving icon beside the dot.
- [x] `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` -- the shape test with `clipsWriting`, and old JSON reads 0.
- [x] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- one test: gated write, press → a published state with `CooldownUntil` and `ClipsWriting == 1`, recorded with `Stopwatch.GetTimestamp()` in the publish callback, less than 250 ms after the press QPC; a press 1 s later adds no published state; open the gate → last state `ClipsWriting == 0`.
- [x] `tests/ReplayBuffer.App.Tests/OverlayTests.cs` -- (1) manual time: writing + cooldown → `Saving`; cooldown passes with the write still going → still `Saving`; write ends → not; then cooldown only → `Saving` until the timer fires at its end; (2) a new view model over a snapshot mid-cooldown → `Saving`, and disconnected → not; (3) extend the default window test: a saving state with no dot shows the window.

**Acceptance Criteria:**
- Given a game session with a slowed writer, when a Save press is accepted, then a `capture.stateChanged` with `cooldownUntil` and `clipsWriting = 1` is published within 250 ms of the press QPC, and `clipsWriting` returns to 0 when the writer completes.
- Given a press inside the cooldown, when it arrives, then no state changes.
- Given the Overlay view model, when that state arrives, then the saving icon shows until the later of `cooldownUntil` and the writer's completion, then hides.
- Given the App reconnecting mid-cooldown, when the snapshot arrives, then the icon shows.
- Given the Overlay window, when saving with no dot, then the window shows; with neither, it hides.

## Implementation Notes

- The icon is a green (`#43A047`) down arrow over a bar (a `Path`, 16 DIP), 6 DIP left of the dot, in a fixed 38 DIP strip; the icon and dot are `Hidden`, never collapsed. `Place` keeps the dot at `Monitors.TopRight` and extends the window left.
- Not in the plan: 4.3's drain test compared the exact list of published `Replay` values, which the `ClipsWriting` publishes now repeat; it collapses consecutive repeats.
- The 250 ms is shown structurally, not by a wall-clock bound (CLAUDE.md "Tests"): both states leave in the press's own orchestrator turn, before the next posted press is handled.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 4 findings — high 0, medium 0, low 4, false 0, maybe-false 0
- findings:
  - `[low]` `[patch]` The Core test asserted a wall-clock 250 ms bound in the default run, against CLAUDE.md's no-real-time-latency rule — fixed: the bound is gone; the test waits for the next press to be ignored (same channel, in order) and asserts exactly two states, the second with `CooldownUntil` and `ClipsWriting == 1`, were published in the press's turn.
  - `[low]` `[patch]` `.Distinct()` in the drain test also hid non-adjacent repeats, weakening the order check — fixed: it collapses only consecutive repeats.
  - `[low]` `[patch]` The new `SetState` sat inside the try whose catch means "the write never started", so a throwing publish would delete a file being written — fixed: it runs after the try/catch on the success path, and the catch returns.
  - `[low]` `[patch]` The architecture's `capture.stateChanged` shape (line 86) lacked `clipsWriting` — fixed: added.

## Design Notes

Two publishes per accepted press (cooldown first, then the writing count once the write has started) keep 4.4's ordering intact; the second carries both fields, which is what the ticket's check reads. A press that is accepted but refused (busy, too short) still shows the icon for the cooldown, since the press was taken. The window is one fixed-width strip so the dot never moves when the icon comes and goes.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build`, likewise `ReplayBuffer.Core.Tests`, `ReplayBuffer.Engine.Tests` and `ReplayBuffer.App.Tests` (App.Tests with a timeout) -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL warnings (Core and Contracts are engine code)

**Manual checks (if no CLI):**
- Builder (Human Check Queue): in a borderless game press Save; the green icon appears left of the dot at once and stays for the cooldown and the write.

## Auto Run Result

- **Summary:** `capture.stateChanged` gains an additive `clipsWriting` count (AD-22) that the SessionOrchestrator publishes as each Clip write starts and ends, after 4.4's `cooldownUntil` publish in the same press turn. The Overlay view model's `Saving` is true while connected and a Clip is writing or the cooldown is ahead, timing the cooldown's end with a `TimeProvider` timer; the Overlay draws a green saving icon left of the dot and shows while there is either. A snapshot at reconnect drives it the same way.
- **Files:**
  - `src/ReplayBuffer.Contracts/Messages/Capture.cs`: `ClipsWriting = 0`.
  - `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs`: publishes the count at write start and end.
  - `src/ReplayBuffer.App/Overlay/OverlayViewModel.cs`: `Saving` and the cooldown timer.
  - `src/ReplayBuffer.App/Overlay/OverlayWindow.cs`: the icon, the fixed strip, placement.
  - `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs`, `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs`, `tests/ReplayBuffer.App.Tests/OverlayTests.cs`: tests.
  - `architecture-replay-buffer.md`: the message shape lists `clipsWriting`.
- **Review:** quick lens, 4 low findings, all patched; nothing deferred or rejected.
- **Follow-up review recommended:** false (no high, no medium patched).
- **Verification:** Release build 0 warnings; Contracts 99/99, Core 75/75, App 69 passed (7 Heavy skipped), Engine 153 passed (18 Heavy skipped); `tools/publish-engine.ps1` exit 0, no IL warnings. Heavy Overlay tests not run (Remote Desktop).
- **Residual risks:** the icon's look and placement over a real game are for the builder (Human Check Queue). Each accepted press now publishes two state changes and each write end one more.
