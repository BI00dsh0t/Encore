---
title: 'Settings store, complete key catalog and Resolve'
type: 'feature'
ticket: '4'
created: '2026-10-25'
status: done
baseline_revision: 'a15cb5716f8a18ebc2d034c29ad383e592f02ba0'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Epics 2 and 3 are built in parallel on one settings document, but there is no key catalog, no settings file, no update protocol and no `Resolve`, so each would invent its own shape (AD-4, AD-22).

**Approach:** Contracts gains the closed AD-4 catalog (every key's type, default, overridability and `appliesAt`), the document, `settings.update` and `EffectiveSettings` DTOs, all source-generated `System.Text.Json`. Core gains the one `Resolve`. The engine gains `SettingsStore`: the instance's `settings.json`, revision-checked all-or-nothing op lists, atomic write, defaults for missing keys.

## Boundaries & Constraints

**Always:**
- Document `{ schemaVersion: 1, revision, global, profiles: { <id>: { name, ignored, recordingsEnabled, overrides } } }` at `<Instance.RoamingDataFolder>\settings.json`; camelCase JSON, enums as camelCase strings.
- Only `video`, `trackLayout`, `bufferWindow` are overridable; a value replaces its key whole (no deep merge).
- Commit order: validate every op → write temp → replace → swap the immutable in-memory document → raise `Changed`. Revision +1 per commit; a refused update changes nothing.
- One `Resolve(SettingsDocument, string? profileId)`, pure, in Core: valid override → valid global → catalog default.
- No reflection serialization; 0 build warnings with the trim/AOT analyzers; new codes kebab-case in `ErrorCodes.All`.

**Never:**
- No IPC wiring, `settings.changed` transport, or snapshot (story 5); no behaviour on any key (stories 9, 10, 13, later epics).
- No `SettingsMigrations` entries; no settings in the App; mic mute stays out of settings (AD-5).

**Decisions (user, 2026-10-25):**
- Default `bufferWindow` is 300 s.
- Default `video` is 1920×1080, 60 fps, H.264, CBR, 40 000 kbps.
- Default `hotkeys` are ShadowPlay-like: `save` Alt+F10, `recordToggle` Alt+F9, `micMute` Alt+F8, `monitorRecordToggle` Alt+Shift+F9, `recordStart`/`recordStop` unbound. The catalog default is the same for every instance; AD-16's "dev instance starts with no Hotkeys bound" is applied by story 9's registration (a dev instance registers only an explicitly stored `hotkeys`), so Resolve stays pure.
- The plan is kept whole at about 2 500 tokens.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| First run | no `settings.json` | revision 0, empty `global`/`profiles`; Resolve gives catalog defaults; no file until the first commit | — |
| Override wins | profile `video` set, global `bufferWindow` set | Resolve(profile): profile's `video`, global's `bufferWindow`, defaults for the rest | — |
| Stale revision | `expectedRevision` ≠ current | nothing written | `settings-conflict` |
| Non-overridable override | profile op on `saveCooldown` | refused, nothing written | `key-not-overridable` |
| Unknown key / bad value | key not in catalog; value fails the key's type or range | refused | `setting-unknown-key` / `setting-invalid-value` |
| Override on missing profile | profile op on an id with no profile | refused | `profile-not-found` |
| Profile attributes | profile op on `name`/`ignored`/`recordingsEnabled` | sets the attribute; `set name` on a new id creates the profile (`ignored`/`recordingsEnabled` false) | — |
| Mixed op list | one valid op, one invalid | whole update refused | first failing op's code |
| Killed write | temp written, process dies before replace | reopen reads the last good file; stale temp deleted | — |
| Write I/O failure | temp write or replace throws | in-memory document and file unchanged | `settings-write-failed` |
| Unreadable file | bad JSON or `schemaVersion` ≠ 1 | file moved aside to `settings.json.unreadable-<yyyyMMdd-HHmmss>`, store starts empty, load result carries the code | `settings-unreadable` |
| Bad stored value | a key's stored JSON doesn't fit its type | Resolve falls back to the next layer | — |

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- add codes to the constants and `All`; `ContractsTests.EveryDeclaredCodeIsInTheRegistry` checks both.
- `src/ReplayBuffer.Contracts/Instance.cs` -- reuse `RoamingDataFolder`; don't change.
- `src/ReplayBuffer.Contracts/ErrorCodeException.cs` -- the store's failures throw it.
- `src/ReplayBuffer.Core/` -- empty project referencing Contracts; Resolve is its first code.
- `src/ReplayBuffer.Engine/` -- the store goes in `Settings/`; `Program.cs` is untouched (no composition root yet).
- `Directory.Build.props` -- `IsAotCompatible` already on Contracts, Core, Engine; tests auto-reference xunit.v3.
- `ReplayBuffer.slnx` -- add `tests/ReplayBuffer.Core.Tests`.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- add `settings-conflict`, `key-not-overridable`, `setting-unknown-key`, `setting-invalid-value`, `profile-not-found`, `settings-write-failed`, `settings-unreadable`.
- [x] `src/ReplayBuffer.Contracts/Settings/SettingValues.cs` -- value DTOs and enums from the catalog table.
- [x] `src/ReplayBuffer.Contracts/Settings/SettingsDocument.cs` -- immutable document and `GameProfile` (key → `JsonElement` maps), `SettingsUpdate`/`SettingsOp` (`scope: global|profile`, `op: set|remove`), `EffectiveSettings` (one typed property per key, plus `profileId`, `settingsRevision`).
- [x] `src/ReplayBuffer.Contracts/Settings/Keys.cs` -- `SettingKey` (name, value type, default, overridable, `AppliesAt`), `SettingKey<T>` with its `JsonTypeInfo<T>` and validator, `Keys.All`; profile attribute names kept apart from key names.
- [x] `src/ReplayBuffer.Contracts/Settings/SettingsJsonContext.cs` -- the source-generated context.
- [x] `src/ReplayBuffer.Core/Settings/SettingsResolver.cs` -- `Resolve`.
- [x] `src/ReplayBuffer.Engine/Settings/SettingsStore.cs` -- `Open(folder)` returning store + load code, `Current`, `Update` under one lock, `Changed`; an internal fault seam between temp write and replace.
- [x] `tests/ReplayBuffer.Contracts.Tests/KeysTests.cs` -- names unique and camelCase, every default present and valid and round-tripping, overridable set is exactly the three keys, attribute names disjoint.
- [x] `tests/ReplayBuffer.Core.Tests/*` (new project, in slnx) -- Resolve rows of the matrix.
- [x] `tests/ReplayBuffer.Engine.Tests/SettingsStoreTests.cs` -- every store row of the matrix, in a temp folder; reload after commit returns the same document.

**Acceptance Criteria:**
- Given a profile overriding `video` only, when Resolve runs for it, then `video` is the profile's and every other key is global or default.
- Given an engine killed after its temp write, when the store reopens, then it reads the last committed revision.
- Given the catalog, when the tests enumerate it, then every key has a valid default and only the three AD-4 keys are overridable.
- Given the solution, when `dotnet build ReplayBuffer.slnx` and `dotnet test --solution ReplayBuffer.slnx` run, then 0 warnings and all tests pass.

## Implementation Notes

- Values are normalized on `set` (parsed through the key's `JsonTypeInfo<T>`, re-serialized), so the file holds canonical camelCase enums; reads of enum names are case-insensitive, integers are refused.
- Value records use `JsonUnmappedMemberHandling.Disallow`; the context sets `RespectNullableAnnotations` and `RespectRequiredConstructorParameters`, so a missing or extra field fails the key's type.
- Ranges beyond the table: `video` width/height even, 2..8192, bitrate ≥ 1; `trackLayout` ids/keys unique and non-empty GUIDs, every `deviceKeys` entry names a layout device; hotkey `vk` 1..254, `modifiers` ⊆ `MOD_ALT|CONTROL|SHIFT|WIN|NOREPEAT`; `ringPath`/`outputFolder`/`manualGames` fully qualified; `cleanup` limits ≥ 1. Cross-key rules (`ring-path-invalid`, `hotkey-duplicate`) stay with their stories.
- Op edge cases: a global op with a `profileId`, or an undefined scope/op, is `invalid-arguments`; a profile op without a `profileId` is `profile-not-found`; `remove` on a profile attribute is `setting-invalid-value`.
- Maps are `ImmutableSortedDictionary` re-keyed ordinally on load, so the file is written in a stable key order. The replace is `File.Move(overwrite: true)`; `Changed` is raised under the store lock so events follow revision order.
- Resolve echoes the requested `profileId` even when no such profile exists (it then resolves Global).

## Plan Change Log

## Review Triage Log

Lens `quick`, iteration 0. Verdicts: high 0, medium 1, low 2, false 0, maybe-false 0.

| # | Finding | Verdict | Route | Evidence |
|---|---------|---------|-------|----------|
| 1 | A null profile value in `settings.json` crashes `Open` with a NullReferenceException instead of `settings-unreadable` | medium | patch | `RespectNullableAnnotations` doesn't check generic collection elements, so `"profiles":{"id":null}` deserializes; `WithOrdinalKeys` dereferences `p.Value`, and `TryParse` catches only `JsonException`. The engine could not start. Fixed: `TryParse` treats a null profile as unreadable; added to the unreadable-file theory. |
| 2 | Maps are deserialized with the culture comparer before being re-keyed ordinally, so canonically equivalent ids collide | low | reject | Real: STJ builds `ImmutableSortedDictionary` with `Comparer<string>.Default`. But profile ids come from `Canonicalize` (one normalized form), and Global keys are catalog names, so two ids that compare equal under the culture are unlikely in everyday use, and the fix (a custom converter or an intermediate `Dictionary` DTO) adds more than a direct correction. |
| 3 | An exception from a `Changed` handler leaves `Update` throwing after the commit, while the docs say an exception means "refused" | low | patch | Real, and nothing subscribes yet (story 5 will). Fixed by correcting the XML docs: a handler's exception propagates after the commit. No behaviour change. |

## Design Notes

**Catalog** (Global-only unless marked; `appliesAt`: `immediate`, `nextSession` = next Reset or Recording):

| Key | Type | Default | appliesAt |
|---|---|---|---|
| `video` (overridable) | `{ codec: h264\|hevc\|av1, width, height, fps ≤ 120, bitrateKbps, rateControl: cbr\|vbr }` | decision above | nextSession |
| `trackLayout` (overridable) | AD-4 shape | one "Everything" Track (fixed GUID), no devices, `muteAffected` | nextSession |
| `bufferWindow` (overridable) | seconds ≥ 1 | 300 | nextSession |
| `replayStorage` | `memory\|disk` | `memory` | nextSession |
| `ringPath` | path or null (= `%LOCALAPPDATA%\<instance>\ring`) | null | nextSession |
| `saveCooldown` | seconds ≥ 0 | 5 | immediate |
| `outputFolder` | path or null (= instance-named folder under Videos) | null | immediate |
| `hotkeys` | `{ save, micMute, recordToggle, recordStart, recordStop, monitorRecordToggle }`, each `{ vk, modifiers }` (Win32 VK and `MOD_*` flags) or null | decision above | immediate |
| `recordHotkeyMode` | `toggle\|separate` | `toggle` | immediate |
| `monitorRecordingMonitor` | monitor device name or null (= primary) | null | nextSession |
| `feedbackSounds` | `{ clipSaved, saveFailed, recordingStarted, recordingStopped, diskFull }` bools | all true | immediate |
| `feedbackSoundDevice` | endpoint id or null (= default communications) | null | immediate |
| `cleanup` | `{ maxBytes, maxAgeSeconds }`, each null = off | both null | immediate |
| `manualGames` | canonical exe paths | `[]` | immediate |
| `startWithWindows` | bool | false | immediate |

- Values are stored as raw `JsonElement` per key so the document stays closed-catalog yet heterogeneous; each `SettingKey<T>` deserializes through its own generated `JsonTypeInfo<T>`, which is what keeps it AOT-safe.
- Unknown keys already in a file are kept on round-trip and ignored by Resolve; updates refuse them.
- No Monitor Record mode key: AD-11's closed `HotkeyAction` has only `monitorRecordToggle`.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx` -- expected: 0 warnings, 0 errors.
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass.
</content>
