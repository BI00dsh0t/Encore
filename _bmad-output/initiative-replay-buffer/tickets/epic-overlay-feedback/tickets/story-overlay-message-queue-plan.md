---
title: 'Overlay message queue'
type: 'feature'
ticket: '5'
created: '2026-11-03'
status: 'in-progress'
baseline_revision: '36632897ef73704fcb2960ea4951928c35739f85'
route: 'full'
route_source: 'auto'
risk: 'low'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** Story 5.4's engine raises `warning.raised` notices (`clip-saved`, `clip-failed`, `recording-started`, `recording-stopped`, `disk-full`) with `overlay: true`, but the Overlay shows none of them, and notices that arrive close together must queue rather than overwrite (R3). QA on 5.4 also found that repeated `clip-failed` and `disk-full` notices stack.

**Approach:** The Overlay view model keeps a FIFO of the snapshot's `overlay: true` warnings it hasn't shown yet and exposes one current `Message` at a time, each for 4 s on a `TimeProvider` timer; the window draws it as a line of text below the dot, green for non-error and red for error notices. A repeat of a notice that is still waiting takes the waiting one's place instead of queueing again.

## Boundaries & Constraints

**Always:**
- The view model reads only `IEngineSession.State.Warnings` and `Status` (AD-3, AD-15); `Overlay == false` warnings are never queued.
- Queueing: on each `Refresh` while connected, every active `Overlay` warning whose `Id` hasn't been seen is appended, oldest `At` first, and its id remembered as seen. Ids are GUIDs, so the seen set survives reconnects; ids no longer active are pruned from it while connected.
- Repeats collapse: a new warning with the same `Code` and `Message` as one still waiting replaces that waiting entry in place (keeps its position). A repeat of the message on screen queues normally.
- Showing: when nothing is on screen, the first waiting entry still active in `State.Warnings` becomes `Message`; entries no longer active are dropped (dismissed before their turn). A one-shot 4 s timer posts the advance to the next. If the shown warning is dismissed while showing, it ends at once and the next one starts.
- Disconnected (`Status != Connected`): `Message` is null and the timer is disposed, but the waiting entries and seen set stay; on reconnect the snapshot only adds unseen ids, so a message already shown never replays, and waiting entries still active show.
- Colour: `WarningSeverity.Error` red (`#E53935`, the dot's red), anything else the saving icon's green (`#43A047`).
- The window shows while `Dot != None || Saving || Message != null`. The dot stays exactly at `Monitors.TopRight(...)`; the message sits below the icon and dot strip, right-aligned to the dot, and the window grows left and down only while a message shows (the strip alone otherwise, as 5.3 left it).
- Lean tests through the view model with manual time (CLAUDE.md "Tests"); no real-time waits.

**Never:** No engine, Core or Contracts change (5.4's notices and their stacking in the snapshot stay; the status line is 5.10). No mic icon or red X (5.7). No sound in the App. No settings key. No `Platform.Desktop` change. Don't move the dot or change 5.3's saving-icon logic.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Three close notices | A, B, C raised 100 ms apart | `Message` A for 4 s, then B for 4 s, then C for 4 s, then null | none |
| Dismissed while waiting | A showing, B queued, B dismissed | after A, C (or null); B never shows | none |
| Overlay false | a `feedback-device-captured`-style warning with `Overlay = false` | never `Message` | none |
| Reconnect | A shown, B waiting; disconnect; reconnect snapshot still holds A and B | `Message` null while disconnected; on reconnect B shows, A never again | none |
| Repeat while waiting | A showing, `clip-failed` X waiting, repeat `clip-failed` X' (same message) | one entry, X' (in X's place); shows once | X dismissed later doesn't drop X' |
| Colour | `clip-saved` info / `clip-failed` error | green / red | none |

</intent-contract>

## Code Map

- `src/ReplayBuffer.App/Overlay/OverlayViewModel.cs` -- add `Message` (`Warning?`), the queue (`List<Warning>` or `LinkedList`), `HashSet<string> _seen`, `ITimer? _messageEnd`, and the advance logic inside `Refresh` (or a helper it calls). Reuse `_post` and `_time` exactly as 5.3's `_cooldownEnd` does. Keep the hide-before-move / move-before-show ordering: include `Message` in the "nothing to show" branch.
- `src/ReplayBuffer.App/Overlay/OverlayWindow.cs` -- add a `TextBlock` (in a `Border` with a translucent dark background, e.g. `#B0000000`, padding 6x2, `TextTrimming.CharacterEllipsis`) below the strip, right-aligned; constants `MessageWidthDip = 260`, `MessageHeightDip = 22`, `MessageGapDip = 6`. The `Viewbox` child is a `Grid` whose `Width`/`Height` switch between the strip (`WidthDip` x `DotSizeDip`) and the strip plus message (`MessageWidthDip` x `DotSizeDip + MessageGapDip + MessageHeightDip`); `Place` scales both by `size / DotSizeDip`, keeps the dot at `(x, y)` and extends the window left and down. `Showing` includes `_model.Message != null`. Foreground brush from `Message.Severity`.
- `src/ReplayBuffer.Contracts/Messages/Feedback.cs` -- `Warning`, `WarningSeverity`. Read only.
- `src/ReplayBuffer.Contracts/Messages/Engine.cs:34` -- `StateSnapshot.Warnings`. Read only.
- `src/ReplayBuffer.App/ShellApp.cs:64` -- constructs the view model; no change.
- `tests/ReplayBuffer.App.Tests/OverlayTests.cs` -- `FakeEngineSession`, `Shift`, `ManualTime` (one-shot timers; `Dispose` removes) already exist. Add a small helper to set `engine.State.Warnings` and raise `Changed`. The Heavy screen-copy test reads only the dot's square, which must stay where it is.

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.App/Overlay/OverlayViewModel.cs` -- the queue, repeat collapse, `Message` and its 4 s timer; update the class summary -- R3's queue.
- [ ] `src/ReplayBuffer.App/Overlay/OverlayWindow.cs` -- draw the message below the dot in green or red, size and place the window for it; update the class summary.
- [ ] `tests/ReplayBuffer.App.Tests/OverlayTests.cs` -- (1) three notices 100 ms apart each shown 4 s in order, colours by severity (assert `Message.Severity`/`Code`); (2) a queued notice dismissed never shows, and an `Overlay = false` warning never shows; (3) reconnect: shown notice not replayed, waiting one shows; (4) a repeat of a waiting `clip-failed` shows once; (5) extend the window test: a message with no dot shows the window, and none hides it.

**Acceptance Criteria:**
- Given three `overlay: true` notices raised 100 ms apart, when the Overlay view model reads them, then each is `Message` for 4 s in the order raised and then `Message` is null.
- Given a notice dismissed while waiting, when its turn comes, then it is skipped; given a warning with `overlay: false`, then it never becomes `Message`.
- Given a notice already shown and another waiting, when the App disconnects and reconnects with both still in the snapshot, then only the waiting one shows.
- Given two `clip-failed` notices with the same message while the first is still waiting, when they are shown, then it shows once.
- Given the Overlay window with a message and no dot or saving icon, then the window is visible; with none of the three, it is hidden.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

The queue is client-side because the engine's notices are plain warnings that stay active (10 s for `clip-saved`, until dismissed for failures), so "shown" is the Overlay's own fact; GUID ids make it survive reconnects with no Contracts change. Collapsing only waiting repeats keeps a burst of failures to one message, while a fresh failure after the last one has left still announces itself. The window grows only while a message shows, so the always-on dot keeps the small window that spike 5.2 measured in independent flip.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` (with a timeout) -- expected: all pass

**Manual checks (if no CLI):**
- Builder (Human Check Queue): with the Dev engine and App, `dev.simulate saveFailed` and a Save over a borderless window show "Clip not saved: save failed" in red and "Clip saved" in green below the dot, one after the other.
