---
title: 'Refactor sweep'
type: 'refactor'
ticket: '7'
created: '2026-11-03'
status: 'built'
baseline_revision: '2e1b332096ecee204d9944453cc79b3768480e97'
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

**Problem:** Epic 3 (3.1–3.6, 3.8) left some of its code written more than once. GameMonitor's exit and Ignored paths end in the same hand-over. Five places repeat the `Canonicalize` failure filter, and two of them also repeat AD-18's "canonical, else lowercase raw" match. The two settings writers repeat the conflict-retry loop. The allocator works out a QPC's local time twice and spells the `"Game"` fallback twice. Of epic 3's plans, only 3.5 deferred an item: `SettingsMaps.WithOrdinalKeys` throws a NullReferenceException on a profile with no `overrides`. Epic 1's open `OutputNaming` item names 3.5 as its owner, but 3.5 left that rewiring to epics 4 and 7.

**Approach:** Move each duplicate into one shared helper, keeping the code as it is. Record both deferred items in `deferred-work.md` with new owners, and leave their code alone.

## Boundaries & Constraints

**Always:**
- No behaviour change: the same proposals, reasons and order, log text and levels, exception types and messages, and return values.
- Keep to epic 3's code. New helpers are `internal` unless another assembly needs them. `GamePaths` members are `public`, since the Engine calls them.
- Keep each file's line endings (LF) and style (doc comments, file-scoped namespaces).

**Never:**
- Touch the files that story 4.2 is likely editing beyond the hunks named below: in `EngineHost.cs`, only `CanonicalOrLowercase` and `InstallFolder`. Leave `SessionOrchestrator.cs`, `CaptureSession.cs`, `TracerCapture.cs`, `OutputNaming.cs`, `SettingsStore.cs`, `Contracts/*` and `Platform.FFmpeg/*` alone.
- Fix the `WithOrdinalKeys` NRE or rewire `OutputNaming`. Both change behaviour, so they are only recorded.
- Share the test fakes across test projects, or add tests. The existing suites are the proof.

</intent-contract>

## Code Map

- `src/ReplayBuffer.Platform.Windows/GameMonitor.cs`:
  - `ForgetExited` (~583) and `ForgetIgnored` (~630) end in the same block: if the committed Target is gone, `Propose(Best(), reason)`; else if only the desired one is gone and `desired != committed`, `Propose(committed is null ? Best() : Detected(committed), reason)`. The reason is `Exit` in one and `Ignored` in the other.
  - `Classify` (~561) has the `Canonicalize` catch filter.
- `src/ReplayBuffer.Platform.Windows/GamePaths.cs`: `Canonicalize` throws `Win32Exception`. Callers filter on `Win32Exception or IOException or UnauthorizedAccessException or ArgumentException`.
- `src/ReplayBuffer.Platform.Windows/GameConfigStore.cs`: `Map` (~82) caches canonical-or-`raw.ToLowerInvariant()`.
- `src/ReplayBuffer.Engine/EngineHost.cs`:
  - `CanonicalOrLowercase` (~450) is the same match as `Map`.
  - `InstallFolder` (~464) uses the filter with its own fallback (`Path.GetFullPath(...).ToLowerInvariant()`).
- `src/ReplayBuffer.Engine/Settings/ManualGames.cs` and `GameProfiles.cs`: each has `Tries = 3` and a loop that reads `store.Current`, may return early, then calls `store.Update` and retries on `SettingsConflict` while `attempt < Tries`. On its last conflict, `ManualGames` lets it propagate. `GameProfiles` has a later `catch (Exception)` that logs a warning and returns.
- `src/ReplayBuffer.Core/Output/OutputPathAllocator.cs`: `(time.GetLocalNow() - time.GetElapsedTime(qpc)).DateTime` appears twice (~36, ~56), and `Fallback = "Game"` is defined here and in `ProfileFolders.cs`.
- `src/ReplayBuffer.App/Settings/GameProfilesPage.cs`: `GameProfileItem.Ignored` and `.RecordingsEnabled` repeat `Set`, then `page.Send(..., SerializeToElement(value, SettingsJsonContext.Default.Boolean))`.
- Tests: `tests/ReplayBuffer.Platform.Windows.Tests` (`GameMonitorTests`); `tests/ReplayBuffer.Engine.Tests` (`GameDetectionServeTests`, `ProfileFolderStoreTests`, `SettingsStoreTests`, `IpcTests`); `tests/ReplayBuffer.Core.Tests` (`OutputPathAllocatorTests`); `tests/ReplayBuffer.App.Tests` (`GameProfilesPageTests`).

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Platform.Windows/GamePaths.cs`: add `public static bool CanonicalizeFailed(Exception e)` (the four-type filter) and `public static string CanonicalOrLowercase(string path)` (AD-18's listed-path match). Use the filter in `GameMonitor.Classify`, `ManualGames.Add`, `EngineHost.InstallFolder` and inside `CanonicalOrLowercase`. `GameConfigStore.Map` keeps its cache and calls `CanonicalOrLowercase`. `EngineHost` deletes its private copy and passes `GamePaths.CanonicalOrLowercase` to its `Select`.
- [x] `src/ReplayBuffer.Platform.Windows/GameMonitor.cs`: add a private `HandOver(int? committed, int? desired, bool committedGone, bool desiredGone, TargetChangeReason reason)` that holds the shared tail. `ForgetExited` and `ForgetIgnored` call it.
- [x] `src/ReplayBuffer.Engine/Settings/SettingsRetry.cs`: new `internal static class SettingsRetry` with `Tries = 3` and `bool Update(SettingsStore store, Func<SettingsDocument, SettingsUpdate?> build)`. Null from `build` means nothing to write (returns false). It returns true once a write commits, retries a `SettingsConflict` while `attempt < Tries`, and lets the last one propagate. `ManualGames.Add` and `GameProfiles.EnsureProfile` use it. `EnsureProfile` keeps its `name ??=` read-once, its "Created" log on true, and its outer `catch (Exception)` warning.
- [x] `src/ReplayBuffer.Core/Output/OutputPathAllocator.cs` and `PathNames.cs`/`ProfileFolders.cs`: a private `LocalTimeOf(long qpc)`, and one `internal const string GameFallback = "Game"` in `PathNames` that both files use.
- [x] `src/ReplayBuffer.App/Settings/GameProfilesPage.cs`: a private `SendFlag(ref bool field, bool value, string attribute, [CallerMemberName] string? property = null)` (or an equivalent shape) shared by the two flag setters.
- [x] `_bmad-output/deferred-work.md`: add three entries in the existing shape.
  - The `WithOrdinalKeys` NRE from 3.5: recorded, open, owned by epic 8's hardening.
  - Epic 1's `OutputNaming` item: owner changed from 3.5 to epics 4 and 7, since 3.5 left the rewiring to them.
  - Epic 3's sweep: deferred nothing else; story 3.7 closed the duplicates above.

**Acceptance Criteria:**
- Given the sweep, when `dotnet build ReplayBuffer.slnx -c Release` runs, then it reports 0 warnings and 0 errors.
- Given the sweep, when the default suites of `ReplayBuffer.Platform.Windows.Tests`, `ReplayBuffer.Core.Tests`, `ReplayBuffer.Engine.Tests` and `ReplayBuffer.App.Tests` run, then all pass. That covers detection, the Target rules, profile creation and folders, and the Game Profiles page.
- Given the sweep changes engine code, when `pwsh tools/publish-engine.ps1` runs, then it succeeds with 0 IL2xxx/IL3xxx warnings.

## Implementation Notes

- The `System.ComponentModel` usings that the filter left unused were dropped from `GameMonitor.cs`, `GameConfigStore.cs`, `ManualGames.cs` and `EngineHost.cs` (line 1 only, so it won't clash with 4.2's hunks).
- `deferred-work.md`: the original epic 1 `OutputNaming` entry also points at its new entry, so it no longer reads as owned by 3.5 alone.
- `EnsureProfile` (after review): it returns early when the profile exists, then computes `name` and its JSON before the `try`, so only the write and its log sit under the warning catch, as in the baseline.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 2 findings — high 0, medium 0, low 2, false 0, maybe-false 0
- findings:
  - `[low]` `[patch]` `GameProfiles.EnsureProfile`'s outer `catch (Exception)` also swallowed exceptions from `ProductName` and `SerializeToElement`, which propagated in the baseline (a behaviour change). Confirmed: they moved into the `build` lambda. Fixed: both are computed before the `try` after an early-return existence check, and the lambda only re-checks and builds the update.
  - `[low]` `[patch]` The `GameConfigStore.Map` comment said "raw form", but `CanonicalOrLowercase` lowercases it. Fixed: the comment now says lowercase.

## Design Notes

- **Out of scope:**
  - The `FakeProbe` and `FakeProcess` that both `GameMonitorTests` and `GameDetectionServeTests` define. Sharing them would need a shared test project.
  - `OutputNaming`'s stem format, which `OutputPathAllocator.Reserve` repeats. `OutputNaming` is the tracer shortcut that epics 4 and 7 retire.
- **`HandOver` keeps both reads:** `committed` and `desired` are read before the forget loop, as they are today, and are passed in, not re-read.

## Verification

Before each build or test, check that `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` doesn't exist.

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release`: expected 0 warnings.
- `dotnet test --project tests/<project> -c Release --no-build`, for each of `ReplayBuffer.Platform.Windows.Tests`, `ReplayBuffer.Core.Tests`, `ReplayBuffer.Engine.Tests` and `ReplayBuffer.App.Tests` (App with a timeout): expected all pass.
- `pwsh tools/publish-engine.ps1`: expected 0 IL2xxx/IL3xxx warnings.

## Auto Run Result

**2026-11-03, bmad-build-auto (full route, quick review).**

- **Summary:** Epic 3's duplicated code now lives in one place, with no behaviour change:
  - the `Canonicalize` failure filter (`GamePaths.CanonicalizeFailed`, four callers) and AD-18's listed-path match (`GamePaths.CanonicalOrLowercase`, used by `GameConfigStore` and `EngineHost`);
  - GameMonitor's exit and Ignored hand-over (`HandOver`);
  - the settings conflict-retry loop (`SettingsRetry.Update`, used by `ManualGames` and `GameProfiles`);
  - the allocator's QPC-to-local time (`LocalTimeOf`) and the `"Game"` fallback (`PathNames.GameFallback`);
  - the Game Profiles page's two flag setters (`SendFlag`).

  Both deferred items were recorded in `deferred-work.md` with new owners, and their code was left alone: 3.5's `WithOrdinalKeys` NRE goes to epic 8, and epic 1's `OutputNaming` item goes to epics 4 and 7.
- **Files:**
  - `src/ReplayBuffer.Platform.Windows/GamePaths.cs`: the two helpers.
  - `GameMonitor.cs`, `GameConfigStore.cs`: these use the helpers, and `GameMonitor.cs` gains `HandOver`.
  - `src/ReplayBuffer.Engine/Settings/SettingsRetry.cs` (new): the retry loop. `ManualGames.cs` and `GameProfiles.cs` call it.
  - `src/ReplayBuffer.Engine/EngineHost.cs`: drops its private `CanonicalOrLowercase` and uses the filter in `InstallFolder`.
  - `src/ReplayBuffer.Core/Output/OutputPathAllocator.cs`, `PathNames.cs`, `ProfileFolders.cs`: `LocalTimeOf` and `GameFallback`.
  - `src/ReplayBuffer.App/Settings/GameProfilesPage.cs`: `SendFlag`.
  - `_bmad-output/deferred-work.md`: the three entries, plus a pointer on epic 1's entry.
- **Review:** 2 findings, both low and both patched. 0 deferred, 0 rejected. Follow-up review recommended: false (no high or medium patched).
- **Verification**, re-run after the patches:
  - Release build: 0 warnings.
  - Platform.Windows.Tests: 25/25.
  - Core.Tests: 63/63.
  - Engine.Tests: 148 passed, 18 Heavy skipped.
  - App.Tests: 65 passed, 5 explicit skipped.
  - `pwsh tools/publish-engine.ps1`: 0 IL2xxx/IL3xxx warnings (10.5 MB).
  - Heavy tests weren't run: the sweep doesn't touch the capture, encode, mux or audio path.
- **Residual risks:** none known. Story 4.2 may conflict with the two small `EngineHost.cs` hunks.
