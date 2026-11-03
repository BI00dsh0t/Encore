---
title: 'Game Profiles page in the Main App'
type: 'feature'
ticket: '6'
created: '2026-11-03'
status: 'built'
baseline_revision: 'd484e0992d8edfa09e84279defdf9a0aab10fd49'
route: 'full'
route_source: 'auto'
risk: 'low'
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

**Problem:** Game Profiles exist in the settings document (story 3.4 creates them on detection), but the Main App can't show, rename, Ignore or switch Recordings on for them, and there's no way to add a game by hand: `manualGames` (R1) is only reachable by writing the whole key.

**Approach:** A "Game Profiles" page in the Settings host, modelled on the General and Track Layout pages, lists every profile from the snapshot's settings and sends each edit as a one-op `settings.update` through its `SettingsSender`. A new `game.add` command in `Game.cs` takes a raw executable path; the engine canonicalizes it (AD-18) and appends it to `manualGames` through the settings op queue (AD-4).

## Boundaries & Constraints

**Always:**
- Every profile edit is one `SettingsOp.SetProfile(id, ProfileAttributes.<Name|Ignored|RecordingsEnabled>, value)` sent through `SettingsPage.SendAsync` against the rendered revision; a conflict reloads, a refusal re-renders (the base class already does both).
- The page lists `Document.Profiles`, sorted by name (ordinal ignore-case), each showing its name (editable), its profile id (the canonical path, read-only), Ignored and Recordings checkboxes. Items are updated in place by id on render so an unrelated `settings.changed` doesn't rebuild rows needlessly; rows for removed ids go, new ids are added.
- Rename sends only when the trimmed name is non-empty and differs from the rendered one; a blank name re-renders the old one. The name text box commits on focus loss (WPF default), not per keystroke.
- `game.add { path }` → result `{ path }` (the canonical path written). The engine: `GamePaths.Canonicalize(path)`; a path that can't be opened (`Win32Exception`, `IOException`, `UnauthorizedAccessException`, `ArgumentException`) or isn't a `.exe` → `ErrorCodeException(ErrorCodes.InvalidArguments, …)`. Already in `manualGames` (ordinal ignore-case) → no write, same result. Otherwise one `SettingsOp.SetGlobal("manualGames", list + canonical)` at the store's current revision, retried on `settings-conflict` (a few tries, as `GameProfiles.EnsureProfile` does).
- The page's Add field takes a typed path or one picked with a Browse button (`Microsoft.Win32.OpenFileDialog`, filter `*.exe`, in the view's code-behind setting the page's `AddPath`). Add sends `game.add` with the raw text; on success clear the field and set `Message` to say the game appears in the list once it is detected; on `ErrorCodeException` set `Message` to `Not added ({code}): {message}`.
- Contracts change is additive (AD-22): `GameAdd(string Path)`, `GameAdded(string Path)`, `GameMessages.Add` (`game.add`) in `Commands`, both types in `GameJsonContext`.
- Lean tests: one or two per AC, App tests through the view model with `FakeEngineSession`.

**Never:**
- New Settings keys or `GameProfile` attributes; showing or editing `folder` or `overrides` (4.11 does overrides).
- Removing hand-added games (not in the ticket).
- Edits to other Engine files beyond `EngineHost.cs`'s one `router.Register` line and the new handler file: 4.1 (Replay Buffer, engine) runs in parallel.
- A UX pass: the builder checks the page by hand.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| List | document rev 3 with profiles `c:\b\b.exe` "Beta", `c:\a\a.exe` "Alpha" (ignored) | rows Alpha (Ignored ticked), Beta | — |
| Rename | Alpha renamed "Apex" | one update, expectedRevision 3, op `profile c:\a\a.exe name "Apex"` | conflict → reload message |
| Blank rename | name set to "  " | nothing sent, name shows "Alpha" again | — |
| Ignored / Recordings | tick Recordings on Beta | one op `recordingsEnabled true` | — |
| Add by hand | `C:\Games\..\Games\Game.EXE` exists | engine writes `manualGames: ["c:\games\game.exe"]`, replies that path | — |
| Add twice | path already listed | no `settings.changed`, same reply | — |
| Add missing | path not on disk | `invalid-arguments`; page shows `Not added (invalid-arguments): …` | nothing written |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Messages/Game.cs` -- add the two records, `public static CommandType<GameAdd, GameAdded> Add { get; } = new("game.add", GameJsonContext.Default.GameAdd, GameJsonContext.Default.GameAdded);`, `Commands = [Add]`, `[JsonSerializable]` both. `MessageRegistry` (`Ipc/MessageTypes.cs:81`) picks it up already. `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs:76` checks command names against a convention; `game.add` fits.
- `src/ReplayBuffer.Engine/Settings/ManualGames.cs` (new) -- `internal static class ManualGames { public static GameAdded Add(SettingsStore store, GameAdd add) }`, modelled on `Settings/GameProfiles.cs` (same retry loop). Read the current list with `SettingsResolver.Resolve(store.Current, null).ManualGames` (as `EngineHost.ListedGames` does, ~line 414) or `Keys.ManualGames.TryRead`; write with `SettingsOp.SetGlobal(Keys.ManualGames.Name, Keys.ManualGames.ToJson(list))` (NativeAOT-safe).
- `src/ReplayBuffer.Engine/EngineHost.cs` (~line 274, beside `FeedbackMessages.Dismiss`) -- `router.Register(GameMessages.Add, (add, _) => ValueTask.FromResult(ManualGames.Add(load.Store, add)));`. The listed-games set already re-reads `manualGames` when the document changes, so detection follows with no other change.
- `src/ReplayBuffer.App/Settings/GameProfilesPage.cs` (new) -- `internal sealed class GameProfilesPage(SettingsSender sender, IEngineSession engine) : SettingsPage(sender)`, `Title => "Game Profiles"`, `ObservableCollection<GameProfileItem> Profiles`, `string AddPath`, `ICommand AddCommand` (`RelayCommand` in `Observable.cs`). `GameProfileItem : Observable` with `ProfileId`, `Name`, `Ignored`, `RecordingsEnabled`; setters call back into the page to send (and a silent `Show(GameProfile)` for renders), like `GeneralSettingsPage.StartWithWindows`.
- `src/ReplayBuffer.App/Settings/GameProfilesView.xaml` + `.xaml.cs` (new) -- modelled on `TrackLayoutView.xaml`: a ListBox/ItemsControl of rows (name TextBox, id TextBlock grey, two CheckBoxes "Ignored", "Recordings"), then a DockPanel with a path TextBox, Browse and Add buttons, and the page's `Message` if the other views show it (check `MainWindow.xaml` for where `Message` binds; follow that).
- `src/ReplayBuffer.App/Settings/SettingsPage.cs` (`SettingsPages.Create`) -- add `new GameProfilesPage(sender, engine)` after Track Layout; the comment already names it.
- `src/ReplayBuffer.App/MainViewModel.cs` -- nothing needed: every page is rendered from `state.Settings` in `Refresh`. Optionally expose `GameProfiles` like `TrackLayout` for tests.
- Tests: `tests/ReplayBuffer.App.Tests/GameProfilesPageTests.cs` (new, `[Collection(nameof(WpfCollection))]`, patterns from `TrackLayoutPageTests.cs`: `FakeEngineSession`, `SettingsDocument.Empty with { Revision, Profiles = ... }`). `TrackLayoutPageTests.TheSettingsTabListsThePageAndItsViewRendersWithoutBindingErrors` asserts the page titles: add "Game Profiles". `tests/ReplayBuffer.Engine.Tests/GameDetectionServeTests.cs`: one serve test for `game.add` (model: `AHandAddedGameWithACaptionedWindowIsDetected`, line 86).

## Tasks & Acceptance

**Execution:**
- [x] `Game.cs` -- `GameAdd`, `GameAdded`, `game.add`.
- [x] `ManualGames.cs`, `EngineHost.cs` -- the handler and its registration.
- [x] `GameProfilesPage.cs`, `GameProfilesView.xaml(.cs)`, `SettingsPage.cs` -- the page.
- [x] Tests (lean). App: (1) a document with two profiles lists both sorted with their flags; renaming one, ticking Recordings on the other and ticking Ignored each send one update at the rendered revision with the right op; a blank rename sends nothing. (2) Add sends `game.add` with the raw path and clears the field; a refusal shows the `Not added` message. (3) The existing view test lists "Game Profiles"; render the Game Profiles view off-screen with two rows and assert no binding errors mentioning it (reuse the existing helper pattern). Engine serve: `game.add` with a `..` path to `GamePath` → `settings.changed` with `manualGames == [Canonicalize(GamePath)]` and that reply; a second add of the same path → same reply, no new revision; a missing path → `invalid-arguments`.

**Acceptance Criteria:**
- Given a settings document with Game Profiles, when the Game Profiles page is shown, then every profile is listed by name with its path, Ignored and Recordings state.
- Given a listed profile, when the user renames it, toggles Ignored or switches Recordings, then the App sends one `settings.update` with that profile attribute against the rendered revision, and the page shows the committed document once `settings.changed` arrives.
- Given an executable path typed or browsed, when the user presses Add, then the engine adds its canonical path to `manualGames` once (a repeat writes nothing) and the game is then detected by the listed-games rule; a path that isn't an existing executable is refused with `invalid-arguments`, shown on the page.

## Implementation Notes

- `MainViewModel` exposes `GameProfiles` like `TrackLayout`, for tests. The private `BindingErrors` trace listener moved from `TrackLayoutPageTests` to a shared `tests/ReplayBuffer.App.Tests/BindingErrors.cs` so both view tests use it.
- The view shows the page's `Message` through `MainWindow.xaml`'s existing `SelectedPage.Message` binding, so the view itself doesn't.
- The review patch was applied by the session itself (one line per property), not by the implementing subagent.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 2 findings — high 0, medium 1, low 1, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` `GameProfileItem.Show` raised `PropertyChanged` for every attribute on every render, so another profile's commit (e.g. 3.4 auto-creating a newly detected game) pushed the stored name back into a name box mid-edit (LostFocus binding). — `Show` now uses `Set`, raising only for values that changed. App.Tests 65/65.
  - `[low]` `[reject]` `ManualGames.Add` accepts a directory whose name ends in `.exe`, since `Canonicalize` opens directories too. — Unlikely in everyday use: Browse picks existing files only (`CheckFileExists`), and a listed directory is never a running process, so it detects nothing. A guard adds a branch for a typed folder named `*.exe`.

## Design Notes

- Immediate per-field sends (like General) rather than a draft and Save (like Track Layout): each edit is one attribute of one profile, so a draft buys nothing and Ignored should take effect at once ("sees detection stop").
- `game.add` exists instead of the App writing `manualGames` because the App can't canonicalize as the engine does (AD-18: one `Canonicalize`, in Platform.Windows, which the App doesn't reference).

## Verification

**Commands:**
- Before every build or test, check that `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` doesn't exist. Never run the full solution test suite. Commit nothing; the session commits.
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build`, same for `tests/ReplayBuffer.Contracts.Tests` and `tests/ReplayBuffer.Engine.Tests` (default, non-explicit) -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings (engine code changed)

**Manual checks (if no CLI):**
- Builder (Human Check Queue): rename a detected game, toggle Ignored and see detection stop, switch Recordings on, add a windowed game by hand and see it detected.

## Auto Run Result

- **Change:** A Game Profiles page in Settings, after Track Layout, lists every profile in the snapshot's settings by name, with its path and its Ignored and Recordings checkboxes. Each rename or toggle sends one `settings.update` op against the rendered revision; a blank rename sends nothing. Add game by hand sends the new `game.add` command with the typed or browsed path. The engine canonicalizes it (AD-18), refuses one it can't open or that isn't a `.exe` with `invalid-arguments`, and appends it to `manualGames` once, retrying on a conflict.
- **Files:**
  - `Contracts/Messages/Game.cs`: `GameAdd`, `GameAdded`, `game.add`.
  - `Engine/Settings/ManualGames.cs` (new): the handler. `Engine/EngineHost.cs`: its registration (one line).
  - `App/Settings/GameProfilesPage.cs`, `GameProfilesView.xaml(.cs)` (new): the page. `SettingsPage.cs`: the page in the list. `MainViewModel.cs`: a `GameProfiles` property.
  - Tests: `GameProfilesPageTests.cs` (3), `BindingErrors.cs` (shared helper), `TrackLayoutPageTests.cs` (the page titles), `GameDetectionServeTests.cs` (1 serve test for `game.add`).
- **Review:** quick lens, 2 findings. 1 medium patched, 1 low rejected (reason in the triage log), nothing deferred.
- **Follow-up review recommended:** false (one medium patched, no high).
- **Verification:**
  - Release build: 0 warnings.
  - App 65 passed (5 explicit skipped), Contracts 93/93, Engine 147 passed (18 explicit skipped). App re-run after the patch: 65 passed.
  - `publish-engine.ps1`: clean, no IL/AOT warnings.
  - Media, Capture, Audio, Core and Platform.Windows suites weren't run: the diff doesn't touch them.
- **Residual risks:**
  - The page isn't checked by hand. That's the builder's Human Check Queue item.
  - A hand-added game shows in the list only once it is detected, and `manualGames` can't be edited from the page (not in the ticket).
