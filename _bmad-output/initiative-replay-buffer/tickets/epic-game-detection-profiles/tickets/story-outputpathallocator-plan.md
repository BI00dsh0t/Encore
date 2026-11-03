---
title: 'OutputPathAllocator'
type: 'feature'
ticket: '5'
created: '2026-11-03'
status: 'in-progress'
baseline_revision: '898121f15adb57704d152e8788901cfe319b7417'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: []
deferred: []
---

<intent-contract>

## Intent

**Problem:** Nothing in Core produces output paths yet; the debug `OutputNaming` in Engine is a tracer shortcut. Epics 4 and 7 need one producer of `<Output Folder>\<profile folder>\<Game>_<yyyy-MM-dd_HH-mm-ss>.mp4` (R26, AD-21) whose profile folders never move.

**Approach:** Add `OutputPathAllocator` and `ProfileFolders` to Core. Each Game Profile gets a persisted `folder` (an additive, optional `GameProfile.Folder`), assigned once by the settings store in the commit that first sees the profile without one, unique among profiles and never `Monitor Recordings`. The allocator builds the file name from the profile's live name and the local time of a QPC stamp, and reserves the path with `FileMode.CreateNew`, adding `_2`, `_3`… on a collision.

## Boundaries & Constraints

**Always:** Folder comparisons are case-insensitive (Windows). A profile's folder, once set, is never changed by a rename or by other profiles being added. Characters Windows doesn't allow (invalid file-name chars, control chars, trailing dots/spaces, reserved device names) are replaced. The reserved path is created on disk (an empty file) before it's returned. Contracts change additively only (AD-22): `folder` is optional and omitted from JSON when null.

**Never:** Rewire `CaptureSession`/`TracerCapture` to the allocator (epics 4 and 7 call it). Let clients set `folder` through `settings.update` (it isn't a `ProfileAttributes` entry; an op naming it is refused as an unknown key, as today). Cache a resolved path. Resolve the null Output Folder default here (the caller passes the folder).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Same second | two allocations, same profile, same QPC second | `Game_2026-11-03_10-00-00.mp4`, then `..._2.mp4`; both files exist | No error expected |
| Colon in name | profile named `Halo: Reach` | folder `Halo_ Reach`, file `Halo_ Reach_<ts>.mp4` | No error expected |
| Duplicate names | profiles a, b named `Launcher`; later c also `Launcher` | a → `Launcher`, b → `Launcher (2)`; adding c leaves them and gives `Launcher (3)` | No error expected |
| Reserved | profile named `monitor recordings` | folder `monitor recordings (2)` | No error expected |
| Rename | profile renamed after its folder was assigned | new file `<NewName>_<ts>.mp4` in the old folder | No error expected |
| Unknown profile | id not in the document | — | `ErrorCodeException(ErrorCodes.ProfileNotFound)` |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Settings/SettingsDocument.cs` -- `GameProfile` record: add `public string? Folder { get; init; }` with `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` and a doc comment (assigned once by the engine, AD-21).
- `src/ReplayBuffer.Engine/Settings/SettingsStore.cs` -- `Apply` (builds `profiles` builder): after the op loop, assign folders to profiles lacking one via `ProfileFolders`. `ApplyAttribute` uses `profile with { Name = ... }` on rename, so `Folder` already survives renames.
- `src/ReplayBuffer.Engine/OutputNaming.cs` -- debug-capture naming with a `Sanitize` (invalid chars → `_`, trim, 100-char cap, reserved device names prefixed `_`, fallback `capture`). Move the sanitizing logic into Core and have `OutputNaming.Sanitize` delegate to it (same results, tests in `tests/ReplayBuffer.Engine.Tests/CommandLineTests.cs` `OutputNamingTests` must still pass).
- `src/ReplayBuffer.Contracts/ErrorCodes` -- `ErrorCodes.ProfileNotFound`, `ErrorCodeException` (namespace `ReplayBuffer.Contracts`).
- `src/ReplayBuffer.Core/Settings/SettingsResolver.cs` -- neighbour style reference (static, sealed, doc comments).
- `tests/ReplayBuffer.Core.Tests/` -- xUnit, no extra packages; Core has `InternalsVisibleTo` the tests.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/Settings/SettingsDocument.cs` -- add optional `GameProfile.Folder` -- persisted folder (AD-22 additive).
- [x] `src/ReplayBuffer.Core/Output/PathNames.cs` -- `public static string Sanitize(string name, string fallback)` holding the sanitizer moved from `OutputNaming` -- one sanitizer.
- [x] `src/ReplayBuffer.Core/Output/ProfileFolders.cs` -- `MonitorRecordings = "Monitor Recordings"`; `AssignMissing(ImmutableSortedDictionary<string, GameProfile>.Builder)` (or an equivalent shape the store and allocator both use) giving each folder-less profile, in id order, `Sanitize(name)` or `name (2)`, `(3)`… unique case-insensitively among all assigned folders and the reserved one -- assign once.
- [x] `src/ReplayBuffer.Core/Output/OutputPathAllocator.cs` -- sealed class over a `TimeProvider` (static `System` instance). `Allocate(SettingsDocument, string profileId, string outputFolder, long qpc)`: profile's `Folder` (or, if null, the one `ProfileFolders` would assign in that document), file stem `Sanitize(Name)_<local time of qpc, yyyy-MM-dd_HH-mm-ss>`; `Reserve(string folder, string name, DateTime localTime)` public (epic-recordings uses it with `Monitor Recordings`): create the directory, `new FileStream(path, FileMode.CreateNew)` and dispose, on `IOException` with the path now existing try `_2`, `_3`…. QPC → local: `GetLocalNow() - (GetTimestamp() - qpc) / TimestampFrequency`.
- [x] `src/ReplayBuffer.Engine/Settings/SettingsStore.cs` -- call `ProfileFolders` in `Apply` -- folders persisted through the op queue commit.
- [x] `src/ReplayBuffer.Engine/OutputNaming.cs` -- `Sanitize` delegates to `PathNames.Sanitize(title, "capture")`.
- [x] `tests/ReplayBuffer.Core.Tests/OutputPathAllocatorTests.cs` -- one test per verify item (I/O matrix rows 1–5), in a temp folder, with a fixed `TimeProvider` subclass.
- [x] `tests/ReplayBuffer.Engine.Tests/ProfileFolderStoreTests.cs` -- one test: a profile created through `SettingsStore.Update` gets `folder`, it's in the reopened file, and a rename keeps it. New file so the parallel test-trim work on `SettingsStoreTests.cs` doesn't conflict.

**Acceptance Criteria:**
- Given a profile in a document, when `Allocate` runs twice with QPCs in the same second, then two distinct `.mp4` paths are returned under `<outputFolder>\<folder>` and both exist on disk.
- Given a profile created through the settings store, when the store is reopened, then the profile still has its `folder`, and renaming it leaves `folder` unchanged.
- Given the Release build, then it has 0 warnings and the NativeAOT engine publish has 0 IL warnings.

## Implementation Notes

- `ProfileFolders.FolderOf(document, id)` is the allocator's shared shape: the profile's folder, or the one `AssignMissing` would give it in that document.
- The folder name's fallback (a name that sanitizes to nothing) is `Game`, as is the file stem's in `Reserve`; `OutputNaming` keeps `capture`.
- `SettingsStoreTests.SettingANameCreatesTheProfileAndAttributesSetInPlace` compared a whole `GameProfile`; it now carries `Folder = created.Folder`, since the store assigns one.

## Plan Change Log

## Review Triage Log

## Design Notes

Assigning the folder inside the store's commit (under its lock) is what makes it unique and stable: two new `Launcher` profiles in one or two commits never compute the same folder, and existing folders are only ever read. The allocator's fallback for a folder-less profile (a document from before this change, not yet committed) computes the same answer the next commit will persist, because assignment is deterministic in id order.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --filter-class *ProfileFolderStoreTests --filter-class *SettingsStoreTests --filter-class *OutputNamingTests` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL warnings
