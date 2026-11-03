---
title: 'Detection sources and exclusions'
type: 'feature'
ticket: '2'
created: '2026-11-03'
status: 'built'
baseline_revision: '80f43f68543ea69995a1a88968b991e9cebace9e'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** The 3.1 tracer detects only a caption-less foreground window that covers its monitor. A windowed game, one Windows lists as a game, or one the user added by hand is never detected; nothing excludes the app's install folder; and no event says a detected game exited.

**Approach:** GameMonitor gains two list sources matched by canonical path even when windowed: Windows' game list read from **GameConfigStore** (spike 3.8's Decision: `HKCU\System\GameConfigStore\Children\*\MatchedExeFullPath` with `Type = 1`, read-only; GameList is dropped) and `manualGames` from Global Settings. It scans visible top-level windows each poll, classifies each new process once (limited open, image path, `Canonicalize`), and keeps every detected game known until its process exits. Processes under the install folder are never detected. New informational events `game.detected` and `game.exited` keep a `detectedGames` snapshot list.

## Boundaries & Constraints

**Always:**
- AD-12: processes opened only with `PROCESS_QUERY_LIMITED_INFORMATION`; no memory reads, module lists, hooks or WinEvents. The registry is opened read-only (`writable: false`) and never written.
- AD-18: every path compared is a `GamePaths.Canonicalize` result (lowercase) or, when a list entry can't be canonicalized (not on disk), its lowercase raw form, also compared with the process's lowercase raw image path.
- AD-19: GameMonitor only proposes; `target.changed` still comes only from the orchestrator. `game.detected`/`game.exited` are informational and published straight to the hub.
- AD-22: Contracts additive only, in `Messages/Game.cs` plus one nullable `StateSnapshot` field (`detectedGames`, null from an older engine, `[]` in `Initial`).
- Stickiness: a detected game stays detected until its process exits; minimizing, losing focus, going windowed or closing its window don't end it.
- Exclusions apply to every source: the engine's pid, the attached App's pid, the shell's processes (existing), and any process whose canonical image path is under the engine's install folder (`Canonicalize(AppContext.BaseDirectory)` with a trailing `\`).
- GameConfigStore is best effort: missing, unreadable or odd-shaped → the last good set (initially empty) is used and the engine logs it once; the fullscreen rule and `manualGames` still work. Re-read at most every 5 s; each raw path is canonicalized once and cached.
- Lean tests (one or two per AC, plus the exclusion cases); test instances keep detection off unless a probe is injected or `--debug-capture` is passed (existing rule).

**Never:**
- The 10 s focus rule, Recording block, cold start, exit handover (3.3); Game Profiles, product names, Ignored (3.4); the add-game command (3.6); App UI changes.
- `Windows.Gaming.Preview.GamesEnumeration.GameList` or any WinRT projection; writing to the registry; `RegNotifyChangeKeyValue` (a timed re-read is enough).
- Edits outside this story's files: story 2.8 (App editor meters) runs in parallel.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Hand-added windowed game | captioned window of a process whose canonical path is in `manualGames`, no Target | `game.detected`, then proposed (reason detected) with that window | — |
| GameConfigStore game | same, path from GameConfigStore | same as above | key missing → logged once, source empty |
| Listed game in background | listed game's window not in front, another game is the Target | `game.detected` only; the Target is not taken over (3.3's rule) | — |
| Listed game in front | newly detected listed game owns the foreground window | `game.detected` and proposed (tracer rule, like fullscreen) | — |
| Sticky | detected game minimized / windowed / no window for many polls | stays known; nothing proposed or exited | — |
| Exit | any detected game's process exits | `game.exited`; if it was the proposed Target, propose null, reason exit | — |
| Install folder | fullscreen or listed process under the install folder | never detected, no events | — |
| Excluded pid | engine / App pid | never opened, no events (existing) | — |
| Non-game window | captioned window, path not listed | classified once (cached until exit), nothing proposed | canonicalize failure → cached as non-game |
| Unopenable | `Open` returns null | skipped, retried next poll (existing) | logged at Debug |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Platform.Windows/GameMonitor.cs` -- `GameWindow`, `IGameProcess`, `IGameWindowProbe`, `ForegroundGameWindowProbe`, `GameMonitor` (`Poll`, `ForgetExited`, `Propose`, `Known`). Extend the probe with `GameWindow? ForegroundWindow()` (the foreground window, any shape, null for the shell's) and `IReadOnlyList<GameWindow> Windows()` (visible, unowned, uncloaked top-level windows that aren't `WS_EX_TOOLWINDOW`, minimized included, Z order); keep `ForegroundGameWindow()` (the fullscreen rule). Pattern for `EnumWindows`: `WindowFinder.List()`.
- `src/ReplayBuffer.Platform.Windows/GamePaths.cs` -- `Canonicalize` works on directories too (`FILE_FLAG_BACKUP_SEMANTICS`). Don't change it.
- `src/ReplayBuffer.Platform.Windows/NativeMethods.txt` -- add any CsWin32 names used (`GetWindow`, `GET_WINDOW_CMD`, ...).
- New `src/ReplayBuffer.Platform.Windows/GameConfigStore.cs` -- reads the list with `Microsoft.Win32.Registry` (in-box, AOT-safe; TFM is `net10.0-windows`).
- `src/ReplayBuffer.Core/Sessions/TargetGames.cs` -- `TargetCandidate`; read-only reference.
- `src/ReplayBuffer.Contracts/Messages/Game.cs` -- add payload, events and JSON context entries; model on `TargetChanged`. `src/ReplayBuffer.Contracts/Messages/Engine.cs` -- `StateSnapshot` (`TargetGame` at line ~85; `Initial`).
- `src/ReplayBuffer.Engine/EngineHost.cs` -- lines ~230–240 build the `GameMonitor`; `load.Store.Current.ManualGames`; `hub.Publish(...)`.
- Tests: `tests/ReplayBuffer.Platform.Windows.Tests/GameMonitorTests.cs` (`FakeProbe`), `tests/ReplayBuffer.Engine.Tests/GameDetectionServeTests.cs` (its `NextAsync(type)` asserts the next event's type, so existing tests must now take `game.detected` first / `game.exited` before `target.changed` exit), `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` (`Samples` needs every event).

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/Messages/Game.cs`, `Messages/Engine.cs` -- `DetectedGame(string ProfileId, string Name, int ProcessId)`; events `game.detected` (adds, replacing any entry with the same pid) and `game.exited` (removes by pid) on snapshot field `detectedGames`; `StateSnapshot.DetectedGames` (`IReadOnlyList<DetectedGame>?`, `[]` in `Initial`) -- informational detection state (AD-18).
- [x] `src/ReplayBuffer.Platform.Windows/GameConfigStore.cs` -- `public sealed class GameConfigStore(ILogger log)` with `IReadOnlySet<string> Paths()`: re-reads `HKCU\System\GameConfigStore\Children` at most every 5 s (read-only), keeps `Type == 1` children's string `MatchedExeFullPath`, maps each raw path through a cache to `Canonicalize` or its lowercase raw form; on a missing key or any exception keeps the last set and logs once -- spike 3.8's source.
- [x] `src/ReplayBuffer.Platform.Windows/GameMonitor.cs` -- probe members above (real implementation included); `GameMonitor` constructor gains, after the existing parameters, `Func<IReadOnlySet<string>>? listed = null`, `string? installFolder = null`, `Action<DetectedGame>? detected = null`, `Action<DetectedGame>? exited = null`; `Poll` per the matrix: forget exited (all known games fire `exited`; the proposed one also proposes null/exit), then the fullscreen rule, then the listed source over `Windows()` (plus the foreground window), classifying each new non-excluded pid once into a seen-cache that holds its handle and is pruned when the process exits; a known Target whose foreground window changes to a fullscreen window or one of its `Windows()` entries is re-proposed with it (existing follow rule); with no Target, a known game in front is proposed (existing) -- R1's sources, stickiness and exclusions.
- [x] `src/ReplayBuffer.Engine/EngineHost.cs` -- build `GameConfigStore`; `listed` = union of its paths and `ManualGames` lowercased, recomputed only when either source returns a different instance; `installFolder` from `AppContext.BaseDirectory`; `detected`/`exited` publish `game.detected`/`game.exited` to the hub.
- [x] Tests (lean): Platform.Windows -- a windowed `listed` game is detected and proposed; a detected game stays known through polls with no foreground or window, then its exit fires `exited` and proposes null; a fullscreen and a listed process under the install folder are never detected; a background listed game doesn't take over the Target but one in front does; a captioned, unlisted window's process is opened once across polls and never proposed; `GameConfigStore.Paths()` reads the real HKCU without throwing and returns lowercase paths (read-only). Engine -- `settings.update` `manualGames` with the fake game's path and a captioned (non-fullscreen) window in `Windows()` yields `game.detected` and `target.changed`; fix the two existing tests for the new event order. Contracts -- samples for the two events.

**Acceptance Criteria:**
- Given the engine serving with `manualGames` holding a game's canonical path, when that game runs with only a captioned window, then within 5 s the client receives `game.detected` and `target.changed { reason: "detected" }` naming it, and `detectedGames` lists it.
- Given a detected game, when it is minimized, goes windowed or loses focus for several polls, then nothing is published, and when its process exits `game.exited` is published (and `target.changed { to: null, reason: "exit" }` if it was the Target).
- Given a process whose executable is under the engine's install folder, when it is fullscreen in front or its path is listed, then it is never detected.
- Given GameConfigStore lists a running game's path, when it runs windowed, then it is detected like a hand-added one; the builder confirms one real Windows-listed game while windowed (Human Check).

## Implementation Notes

- GameMonitor keeps one cache of classified processes (`Seen`: limited handle, canonical path or null, lowercase raw path, eligible), pruned on exit; a detected one carries its `TargetCandidate`. The fullscreen rule and the listed source both classify through it, so a fullscreen process is opened once too, and a canonicalize failure is cached as never detected (the tracer retried it each poll).
- `listed` in EngineHost is rebuilt when `GameConfigStore.Paths()` or `load.Store.Current` (the SettingsDocument) is a different instance; `ManualGames` comes from `SettingsResolver.Resolve(document, null)`, whose list is new on every call.
- `tests/ReplayBuffer.Engine.Tests/IpcTests.cs` (outside the Code Map): its snapshot comparison now also copies `DetectedGames`, a list compared by reference like `Warnings`.
- The new engine test waits for `settings.changed` before the game appears, so `game.detected` can't race it.
- Coordinator fix before review: `Classify` checks the exclusion before the cache, so an App whose window was classified before it attached is still never detected.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 3 findings — high 0, medium 1, low 1, false 1, maybe-false 0
- findings:
  - `[low]` `[patch]` `manualGames` entries were only lowercased, so an on-disk entry spelled differently (8.3 name, junction, `..`) never matched — fixed: `EngineHost.CanonicalOrLowercase` canonicalizes each entry (lowercase when not on disk), recomputed only when the settings document changes.
  - `[false]` `[reject]` AC 4 (a real Windows-listed game while windowed) unconfirmed — it is a HITL check by design, queued for the builder; the listed source is source-agnostic and covered by `AWindowedListedGameIsDetectedAndProposed`, and `GameConfigStore` by its real read-only HKCU test.
  - `[medium]` `[patch]` A newly detected listed game could take an owned dialog, tool window or splash in front as its capture window, while following used only `Windows()` entries — fixed: the foreground window counts in the listed scan only when it is one of `Windows()`; the test's in-front window is now listed too.

## Design Notes

- Newly detected listed games are proposed only when there is no Target or they own the foreground window, so a game starting in the background never steals the Target before 3.3's focus rule; the fullscreen rule's behaviour is unchanged.
- Window for a listed game: the foreground window if it's the game's, else its first window in Z order.
- Classifying a windowed process once (handle held until exit) keeps a poll to `EnumWindows` plus one `GetExitCodeProcess` per seen process; the list sets are matched each poll, so a game added to `manualGames` while running is detected next poll.

## Verification

**Commands:**
- Before every build or test, check that `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` doesn't exist. Never run the full solution test suite. Commit nothing; the coordinator commits.
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Platform.Windows.Tests -c Release --no-build`, same for `ReplayBuffer.Contracts.Tests` and `ReplayBuffer.Engine.Tests` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

**Manual checks (if no CLI):**
- Human Check: a real Windows-listed game detected while windowed; `ReplayBuffer.Engine serve --debug-capture` with a GameConfigStore game launched windowed shows it as the Target.

## Auto Run Result

- **Change:** Game Detection gains Windows' game list (GameConfigStore `MatchedExeFullPath`, read-only, re-read at most every 5 s, per spike 3.8) and `manualGames` as sources matched by canonical path even when windowed; every detected game stays known until its process exits; nothing under the engine's install folder is detected; new informational `game.detected`/`game.exited` events keep a `detectedGames` snapshot list.
- **Files:** Contracts `Messages/Game.cs` (`DetectedGame`, two events), `Messages/Engine.cs` (`DetectedGames`); Platform.Windows `GameConfigStore.cs` (new), `GameMonitor.cs` (probe `ForegroundWindow`/`Windows`, classification cache, listed scan, install-folder exclusion, events), `NativeMethods.txt`; Engine `EngineHost.cs` (listed union, install folder, event publishing); tests in Platform.Windows (`GameMonitorTests`), Engine (`GameDetectionServeTests`, `IpcTests`), Contracts (`MessagesTests`).
- **Review:** 2 patches applied (1 medium, 1 low), 1 false finding rejected (AC 4 is the Human Check), nothing deferred.
- **Follow-up review recommended:** false (one medium patched, no high).
- **Verification:** Release build 0 warnings; Platform.Windows 20/20, Contracts 93/93, Engine 143 passed (17 explicit skipped); `publish-engine.ps1` clean. App.Tests passed in the implementer's run (60, 5 skipped); App code is untouched.
- **Residual risks:** a real Windows-listed game detected while windowed is a Human Check; a non-game GameConfigStore lists (launchers such as `LeagueClientUx.exe`) is detected while it runs until Ignored (3.4); a newly detected listed game in the background doesn't become the Target while another is (3.3's focus rule).

