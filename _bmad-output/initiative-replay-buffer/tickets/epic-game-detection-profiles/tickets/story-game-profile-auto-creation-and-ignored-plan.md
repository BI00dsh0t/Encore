---
title: 'Game Profile auto-creation and Ignored'
type: 'feature'
ticket: '4'
created: '2026-11-03'
status: 'in-progress'
baseline_revision: '60cb338ab39bd1a62e9f48a5b4b3d8fb2a1482e2'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** A detected game has no Game Profile: nothing creates one, so it can't be renamed, ignored or given a folder. `ignored` exists in the document (story 1.4) but Game Detection never reads it, so an Ignored game is still detected and stays the Target.

**Approach:** On first detection the engine creates the profile through `SettingsStore.Update` (AD-4's one queue), named from the executable's version resource. GameMonitor reads each profile's `ignored` every poll: an Ignored game is never detected, an Ignored Target is ended at once with reason `ignored`, and clearing the flag lets the normal rules detect it again.

## Boundaries & Constraints

**Always:**
- Profile id = the candidate's `ProfileId` (the one `GamePaths.Canonicalize`, AD-18). A junction or `..` path to the same file reaches the same id, so the same profile.
- Creation is one `settings.update`-equivalent op, `SettingsOp.SetProfile(id, "name", <name>)`, at the store's current revision, made only when `Current.Profiles` lacks the id. `ignored`/`recordingsEnabled` stay false and `overrides` empty (inherits Global); the store assigns `folder`. On `settings-conflict` (an App write landed first) re-read and retry while the profile is still missing (a few tries); any other refusal or write failure is logged as a warning and detection carries on.
- Name = `FileVersionInfo.GetVersionInfo(path).ProductName` trimmed, read from the file on disk (AD-12: no code loaded); when empty, unreadable or not a PE file, the file name without its extension (`Game.EXE` → `Game`).
- The profile exists before `game.detected` is published: creation runs in the monitor's `detected` callback, before the publish. So on first detection the events are `settings.changed`, `game.detected`, `target.changed`.
- Ignored is read live (AD-5) from `store.Current` every poll. A detected game whose profile became Ignored stops being detected (`game.exited` fires, so the snapshot's `DetectedGames` drops it). If it was the committed Target, propose the best other detected game, else none, reason `Ignored`. If it was only a held desired Target, re-propose the committed one, reason `Ignored`.
- `Ignored` passes the Recording block like `Exit` (AD-5: an Ignored game is never captured). "No drain": the reason `ignored` tells epic-replay-buffer-clips not to drain; this story only proposes it.
- Lean tests: one or two per AC.

**Never:**
- Contracts changes (`TargetChangeReason.Ignored`, `GameProfile`, `SettingsOp` exist). App changes (story 3.6 lists profiles). Renaming or Ignored UI.
- Reading game process memory or loading the executable (no `LoadLibrary`).
- Edits outside this story's files: 2.10 (audio/engine sweep) and 4.1 run in parallel.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| First detection | no profile for `C:\g\Game.exe` (ProductName "Foo") | one `settings.changed` with profile `{name: "Foo", ignored: false, recordingsEnabled: false, folder: <assigned>, overrides: {}}` before `game.detected` | — |
| No version resource | empty `Game.EXE` | name `Game` | — |
| Junction | profile exists; game launched via `C:\link\Game.exe` → same file | no new profile, same `profileId` | — |
| App race | App holds revision r, engine creates the profile (r+1), App sends `expectedRevision: r` | App gets `settings-conflict`; the profile is still in the document | engine retries its own create on conflict |
| Target Ignored | A Target, App sets `ignored: true` | `game.exited` A, then `target.changed { A → null, ignored }` (or → best other game) at once | — |
| Ignored cleared | A ignored and in front | next poll detects A again (`game.detected`, `target.changed { null → A, detected }`) | — |
| Ignored never detected | profile ignored before launch | no `game.detected`, no proposal | — |
| Blocked by Recording | flag set, Target A Ignored | `A → …, ignored` commits through the block | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Platform.Windows/GameMonitor.cs` -- constructor gains, after `time`, `Func<string, bool>? isIgnored = null` (profile id → Ignored; null = never). `Poll` (after `ForgetExited`, a new `ForgetIgnored()` modelled on `ForgetExited`: clear `Candidate`, fire `_exited`, then the same committed/desired handover with reason `Ignored`). Detection paths (`Poll`'s fullscreen branch, `ScanListed`) detect only when not ignored (`Detect` callers: check `game.Candidate is null && !Ignored(game)`). Update the class summary.
- `src/ReplayBuffer.Platform.Windows/GamePaths.cs` -- add `public static string ProductName(string path)` (name rule above; catch `FileNotFoundException`, `IOException`, `UnauthorizedAccessException`, `ArgumentException`).
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` (~line 426) -- `propose.Reason is not (Exit or Ignored)` in the block; update `CommitTarget`'s summary.
- `src/ReplayBuffer.Engine/Settings/GameProfiles.cs` (new) -- `internal static class GameProfiles { public static void EnsureProfile(SettingsStore store, string profileId, ILogger log) }` per the creation rule. Op shape: `SettingsOp.SetProfile(id, ProfileAttributes.Name, JsonSerializer.SerializeToElement(name, <a string JsonTypeInfo from SettingsJsonContext or JsonElement from a Utf8JsonWriter>))` — NativeAOT-safe, no reflection serialization. `SettingsStore.ApplyAttribute` already creates a profile from a `name` set.
- `src/ReplayBuffer.Engine/EngineHost.cs` (~lines 234–248) -- `detected: game => { GameProfiles.EnsureProfile(load.Store, game.ProfileId, gameLog); hub.Publish(GameMessages.Detected, game); }`, and `isIgnored: id => load.Store.Current.Profiles.TryGetValue(id, out var p) && p.Ignored`.
- Tests: `tests/ReplayBuffer.Engine.Tests/GameDetectionServeTests.cs` (existing tests: insert `await NextAsync("settings.changed")` before the first `game.detected` of a new profile; `TheTargetExitingHandsOverToABackgroundListedGame`'s second game shares the path, so no second one; `AHandAddedGame…` sends its manualGames update at revision 0, then the auto-create is revision 2). `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` (the 3.3 `Exit commits through the block` test is the model).

## Tasks & Acceptance

**Execution:**
- [ ] `GamePaths.cs` -- `ProductName`.
- [ ] `GameMonitor.cs` -- `isIgnored`, `ForgetIgnored`, ignored-aware detection, summary.
- [ ] `SessionOrchestrator.cs` -- `Ignored` passes the block.
- [ ] `GameProfiles.cs`, `EngineHost.cs` -- creation with conflict retry; wiring.
- [ ] Tests (lean). Engine serve: (1) first detection of a game whose exe is a copy of a file with a version resource (copy `typeof(GameDetectionServeTests).Assembly.Location` to `_folder\Game.EXE`; expected name = that assembly's `AssemblyProductAttribute.Product`) yields `settings.changed` holding one profile with that name, `ignored`/`recordingsEnabled` false, no overrides, a folder; then after it exits, a second process whose image path goes through a directory junction to `_folder` (create with `cmd /c mklink /J`) is detected with the same `ProfileId` and no new `settings.changed`. (2) A client update at the revision read before detection gets `ErrorCodes.SettingsConflict`; the profile is still in `_client.State.Settings.Profiles`. (3) Target A; client sets `ignored: true` → `game.exited`, `target.changed { A → null, Ignored }`; client sets it false → `game.detected`, `target.changed { null → A, Detected }`. While Ignored, a few polls with A in front publish nothing. One existing serve test (empty `Game.EXE`) asserts its profile's name is `Game`. Core: a Recording-blocked `Ignored` proposal commits at once. Update the existing serve tests for the new `settings.changed`.

**Acceptance Criteria:**
- Given no profile for a game, when it is first detected, then the document gains exactly one profile keyed by its canonical path, named from its version resource, with `ignored` and `recordingsEnabled` false and no overrides, before `game.detected`.
- Given that profile, when the same executable is launched through a junction path, then it is detected with the same profile id and no new profile is written.
- Given the Target's profile, when the App sets `ignored: true`, then `target.changed { reason: ignored }` away from it is published at once (even with the Recording flag set), and the game isn't detected while Ignored; when the App clears it, then the game is detected again by the normal rules.
- Given the App read revision r before a game's profile was created, when it sends `settings.update` with `expectedRevision: r`, then it gets `settings-conflict` and the created profile is still in the document.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

- Creation on the monitor thread, synchronously in `detected`, keeps event order deterministic and guarantees the profile exists whenever `game.detected` or `target.changed` names it. It's one small file write per new game.
- Un-detecting an Ignored game (rather than keeping it known but unproposable) means "clearing it restores detection" falls out of the existing fullscreen and listed rules, and the snapshot never shows an Ignored game as detected.

## Verification

**Commands:**
- Before every build or test, check that `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` doesn't exist. Never run the full solution test suite. Commit nothing; the session commits.
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Platform.Windows.Tests -c Release --no-build`, same for `ReplayBuffer.Core.Tests` and `ReplayBuffer.Engine.Tests` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings
