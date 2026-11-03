---
title: 'Replay Buffer and Output Settings page with the storage estimate'
type: 'feature'
ticket: '10'
created: '2026-11-03'
status: 'built'
baseline_revision: 'e83a7f8fe440e8f4274d1c7a960dcacd269e6bbe'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: true
context: ['{project-root}/CLAUDE.md']
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** The Replay Buffer and Global Output Settings keys exist (1.4, 1.10) but no page sets them, and nothing shows what a Buffer Window will cost in RAM or disk (R2).

**Approach:**
- A Settings page "Replay Buffer" in the App edits the Global `bufferWindow`, `replayStorage`, `ringPath`, `saveCooldown` and `video` keys through `SettingsPage.SendAsync`, offering codecs, resolutions and the fps ceiling from the snapshot's `VideoOptions`.
- Core turns the Global effective settings and the free bytes into a `StorageEstimate` (existing `ReplayStorageEstimate.Bytes`). The engine publishes it as a new coalescing event `estimate.changed`, a field of `state.snapshot` (AD-3; additive, AD-22): when the settings change, and when the free bytes move.
- The page shows the estimate, "worst case" for VBR, and a warning that blocks nothing when it exceeds the free RAM (Memory) or the free space on the ring folder's drive (Disk).

## Boundaries & Constraints

**Always:**
- The estimate is of the Global settings: Global `video`, `bufferWindow`, `replayStorage`, `ringPath`, and the Global `trackLayout`'s Track count.
- Contracts change additively only: a new record, event, snapshot field (null in a snapshot without it) and JSON context entry. Say so in the report.
- Free RAM is Windows' available physical memory (`IMemoryStatus`, `SystemMemoryStatus`); free disk is the available free space of the ring folder's drive (`ringPath`, else `<LocalDataFolder>\ring`). Unknown free bytes (a failed read) give no warning.
- The initial estimate is in the hub's initial state (no event, as the device list), so tests that count events see no new one at startup.
- A re-check every 30 s publishes only when the storage, the bytes, the over-free flag change, or the free bytes move by 10 % or more.
- Each edit is sent as one Global op at once; an invalid entry (a Buffer Window under 1 s, a negative Cooldown, an odd or zero size, fps over the ceiling, a non-positive bitrate, a ring folder that isn't a full path) is not sent and the page shows the rendered value again with a `Message`.
- Lean tests (CLAUDE.md "Tests").

**Never:**
- Per-Game-Profile overrides on this page (4.11/4.9), Hotkeys (4.12), or engine session behaviour changes.
- A setting or command for the estimate; the App computing it itself (it doesn't reference Core).
- Blocking a save or a value because of the warning.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| CBR Memory | 40 000 kbps CBR, 1 Track, 300 s, 32 GB free | `Bytes = ReplayStorageEstimate.Bytes(...)`, `WorstCase` false, no warning | — |
| VBR | same, VBR | `WorstCase` true, bytes at 1.5× video; page label says worst case | — |
| Over free | Disk mode, ring drive has less free space than the estimate | `ExceedsFree` true; page shows the warning; the value stays saved | — |
| Unknown free | memory read fails | `FreeBytes` null, `ExceedsFree` false | — |
| Bad entry | Buffer Window `0` typed | nothing sent; rendered value shown again with a message | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Messages/Capture.cs` -- add `public sealed record StorageEstimate(ReplayStorage Storage, long Bytes, bool WorstCase, long? FreeBytes, bool ExceedsFree)` (doc: R2; `Bytes` from the Global settings; `FreeBytes` RAM for Memory, ring drive for Disk, null when unknown). Add `CaptureMessages.EstimateChanged = new("estimate.changed", CaptureJsonContext.Default.StorageEstimate, "estimate", (s, e) => s with { Estimate = e }, coalesces: true)` (check `EventType<T>` ctor parameter order in `Ipc/MessageTypes.cs`; `AudioMessages.Levels` is the coalescing example), add it to `Events`, and `[JsonSerializable(typeof(StorageEstimate))]`.
- `src/ReplayBuffer.Contracts/Messages/Engine.cs` -- `StateSnapshot.Estimate` (`StorageEstimate?`, init; remark: a snapshot without it, from before story 4.10, reads as none). Ensure `Initial` leaves it null.
- `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` -- add an `estimate.changed` sample to `Samples` (the coverage tests require it).
- `src/ReplayBuffer.Core/Replay/ReplayStorageEstimate.cs` -- add `public static StorageEstimate For(EffectiveSettings global, long? freeBytes)`: `Bytes(global.Video, global.TrackLayout.Tracks.Count, global.BufferWindow)`, `WorstCase = Video.RateControl == Vbr`, `ExceedsFree = freeBytes is { } f && bytes > f`. Core references Contracts already.
- `src/ReplayBuffer.Engine/Sessions/EstimatePublisher.cs` (new, internal sealed, IDisposable), modelled on `MemoryMarginReporter`/`DeviceListPublisher`:
  - ctor `(Func<SettingsDocument> settings, IMemoryStatus memory, Func<string, long?> freeDisk, string defaultRingFolder, Action<StorageEstimate> publish, TimeProvider time)`.
  - `StorageEstimate Current()`: `SettingsResolver.Resolve(settings(), null)`; free = Memory → `memory.Read()` (`TotalBytes == 0` → null) else `freeDisk(RingPath ?? defaultRingFolder)`; `ReplayStorageEstimate.For`.
  - `Seed()` returns `Current()` and records it as last; `OnSettingsChanged(SettingsDocument)` and a `time.CreateTimer` every 30 s call `Refresh()`, which publishes when it differs per the Always rule. Guard `last` with a lock.
  - `static long? FreeDiskBytes(string folder)`: `new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace`, null on any IO/argument/unauthorized exception.
- `src/ReplayBuffer.Engine/Ipc/EventHub.cs` -- ctor gains optional `StorageEstimate? estimate = null`, set in the initial state.
- `src/ReplayBuffer.Engine/EngineHost.cs` (4.9 edits this file in parallel; keep the change small and local):
  - Before `devices.Seed(...)` (line ~248), build the publisher with `() => load.Store.Current`, `new SystemMemoryStatus()`, `EstimatePublisher.FreeDiskBytes`, `Path.Combine(instance.LocalDataFolder, "ring")`, `e => Volatile.Read(ref publishTo)?.Publish(CaptureMessages.EstimateChanged, e)`, `hostOptions.Time`; pass `estimate: estimates.Seed()` to `new EventHub(...)`.
  - `load.Store.Changed += estimates.OnSettingsChanged;`. Dispose it where `marginReporter` is disposed (hold as a field the same way).
- `src/ReplayBuffer.App/Settings/ReplayBufferSettingsPage.cs` (new) + `ReplayBufferSettingsView.xaml(.cs)` (new), modelled on `GeneralSettingsPage`/`GameProfilesPage`:
  - Title "Replay Buffer". Properties: `BufferWindowText`, `IsDisk` (Memory/Disk radio), `RingPath` (empty = default), `SaveCooldownText`, `Codecs` (list of items with `Label` = codec name plus " (may not play everywhere)" when flagged; when unprobed, the current codec alone), `SelectedCodec`, `Resolutions` (offered list plus the current size if absent; label `1920 × 1080`), `SelectedResolution`, `FpsText`, `MaxFps`, `BitrateText` (kbps), `IsVbr`.
  - Setters validate then `SendAsync([SettingsValues.SetGlobal(key, value)])`; `video` sends the whole `VideoSettings` with one field changed. Text values commit on LostFocus (default `TextBox` binding).
  - `ShowState(StateSnapshot state)` (called from `MainViewModel.Refresh` before `page.Render`) takes `VideoOptions` and `Estimate`. Estimate text: `"Estimated RAM use: 1.6 GB"` / `"Estimated disk use: …"`, appending `" (worst case for VBR)"`; `EstimateWarning`: `"More than the free RAM (x GB). The value is kept; Clips may be shorter."` or `"More than the free space on the ring folder's drive (x GB). The value is kept."`; null when not exceeded; "Estimate not available yet" when null. Format bytes with one decimal in GB (1e9) or MB.
- `src/ReplayBuffer.App/Settings/SettingsPage.cs` -- add the page to `SettingsPages.Create` after General.
- `src/ReplayBuffer.App/MainViewModel.cs` -- expose `ReplayBuffer` (`Pages.OfType<ReplayBufferSettingsPage>().Single()`), call `ReplayBuffer.ShowState(state)` in `Refresh`.
- Reuse: `tests/ReplayBuffer.App.Tests/FakeEngineSession`, `GameProfilesPageTests` (pattern for send capture, `Create()`, and the binding-error render test); `tests/ReplayBuffer.Core.Tests/PacketRingTests.Streams/Feed`.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/Messages/{Capture,Engine}.cs`, `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` -- the estimate record, event, snapshot field, sample -- AD-3 transport
- [x] `src/ReplayBuffer.Core/Replay/ReplayStorageEstimate.cs` -- `For` -- the Core estimate
- [x] `src/ReplayBuffer.Engine/Sessions/EstimatePublisher.cs`, `Ipc/EventHub.cs`, `EngineHost.cs` -- compute, seed and publish -- R2's live estimate
- [x] `src/ReplayBuffer.App/Settings/ReplayBufferSettings{Page.cs,View.xaml,View.xaml.cs}`, `SettingsPage.cs`, `MainViewModel.cs` -- the page -- the builder's settings
- [x] New `tests/ReplayBuffer.Core.Tests/ReplayStorageEstimateTests.cs` -- (1) a 300 s Memory ring (default slabs) fed 320 s of 4 Mbps CBR at 30 fps plus the helper's audio: `LiveSlabs * DefaultSlabBytes` is within 20 % of `Bytes(video 4000 CBR, 1 Track, 300)`; (2) `For` sets `WorstCase` for VBR and `ExceedsFree` when free < bytes, not when free is null -- the ticket's estimate check
- [x] New `tests/ReplayBuffer.Engine.Tests/EstimatePublisherTests.cs` -- one test with a fake memory status and `FakeTimeProvider` (if Engine.Tests has it; else call `Refresh` directly): a `bufferWindow` change publishes a new estimate; a later refresh with unchanged state publishes nothing; Disk mode reads the ring folder's free bytes
- [x] New `tests/ReplayBuffer.App.Tests/ReplayBufferSettingsPageTests.cs` -- (1) edits send one Global op each at the rendered revision (`bufferWindow`, `replayStorage`, `video` bitrate, rate control) and a `0` Buffer Window sends nothing; (2) the estimate text and warning follow the snapshot's `Estimate` (VBR says worst case, over free shows the warning) and the codec list labels HEVC as may-not-play-everywhere; (3) the view renders without binding errors and saves a PNG of it to the test output folder for the HITL record

**Acceptance Criteria:**
- Given a 5-minute synthetic CBR run into a Memory ring, when its size is measured, then it is within 20 % of the estimate.
- Given the Replay Buffer page, when the Buffer Window, the bitrate, the rate control or the storage mode is changed, then one `settings.update` op is sent for that key and the estimate shown follows the engine's next `estimate.changed`.
- Given an estimate over the free RAM or the ring drive's free space, when the page renders it, then it shows the warning and the value stays set.
- Given the default run, when the 0-warning Release build and the Contracts, Core, Engine and App suites run, then all pass; the NativeAOT publish is clean.

## Implementation Notes

- The page also exposes `IsMemory` (the Memory radio's binding; only a check acts) beside `IsDisk`.
- `TrackLayoutPageTests`' page-order assertion now lists "Replay Buffer" after General.
- Free bytes at 0 count as moved when they become non-zero (the 10 % rule has no base at 0).
- The binding-error test writes `replay-buffer-settings.png` to the App test output folder.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 5 findings — high 0, medium 3, low 2, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` `EstimatePublisher.Refresh`: a timer refresh that read rev N before a slow free-bytes read could publish E(N) after E(N+1) — confirmed, `Current` ran outside the lock on a document read earlier. Fixed: the last estimate's revision is recorded and an older one is dropped inside the lock.
  - `[medium]` `[patch]` `OnSettingsChanged` ran the drive read and publish under `SettingsStore`'s lock, and an exception would fail a committed `settings.update` — confirmed by `SettingsStore.Changed`'s doc. Fixed: the refresh is queued to the thread pool; the pool and timer paths swallow exceptions.
  - `[medium]` `[patch]` Free bytes were read after the running ring took its RAM or ring file, so a fitting buffer warned once live — confirmed, the Disk ring file is sized from the same estimate. Fixed: Disk adds back the `ring-<digits>.bin` sizes in the ring folder; Memory adds the process working set (an approximation).
  - `[low]` `[patch]` The seed reads free disk before EngineHost removes stale ring files — real; covered by the fix above, which counts those files back as free.
  - `[low]` `[reject]` `EstimatePublisherTests` creates a live 30 s system timer that could race the direct `Refresh` calls — the test runs well under 30 s; a fake timer provider is more complexity than this risk.

## Design Notes

**Why an engine-published estimate.** The App doesn't reference Core, the epic puts the estimate in Contracts' `Capture.cs` area, and AD-3 lists `estimate.changed` as a coalescing event with every rendered state in the snapshot. The engine also owns the free-RAM reading (`SystemMemoryStatus`) and the default ring folder. Since each edit commits at once, the estimate follows within one round trip.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build` -- all pass
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- all pass
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build` -- all pass
- `pwsh tools/publish-engine.ps1` -- 0 IL/AOT warnings

**Manual checks (if no CLI):**
- Human Check row: the builder changes the Buffer Window, bitrate and storage mode and sees the estimate update; sets a value past free space, sees the warning, and a save still works.

## Auto Run Result

- **Summary:** R2's settings page and live estimate.
  - A "Replay Buffer" Settings page edits the Global `bufferWindow`, `replayStorage`, `ringPath`, `saveCooldown` and `video` (codec with the may-not-play-everywhere label, resolution, fps up to the ceiling, bitrate, CBR or VBR), one Global op per edit. Invalid entries send nothing.
  - Core's `ReplayStorageEstimate.For` makes a `StorageEstimate`. The engine's `EstimatePublisher` seeds it into the initial snapshot and publishes `estimate.changed` (coalescing) after settings changes and every 30 s, when it moves.
  - The page shows the estimate ("worst case for VBR") and a warning that blocks nothing when it is over the free RAM (Memory) or the ring drive's free space (Disk). The ring's own use counts back as free.
- **Contracts (additive, AD-22):** `StorageEstimate`, `CaptureMessages.EstimateChanged`, `StateSnapshot.Estimate` (null when absent).
- **Files:**
  - Contracts: `Messages/Capture.cs`, `Messages/Engine.cs`.
  - Core: `Replay/ReplayStorageEstimate.cs` (`For`).
  - Engine: new `Sessions/EstimatePublisher.cs`; `Ipc/EventHub.cs` (initial estimate); `EngineHost.cs` (wiring, disposal).
  - App: new `Settings/ReplayBufferSettingsPage.cs`, `ReplayBufferSettingsView.xaml(.cs)`; `SettingsPage.cs`, `MainViewModel.cs`.
  - Tests: new `ReplayStorageEstimateTests` (Core, 2), `EstimatePublisherTests` (Engine, 1), `ReplayBufferSettingsPageTests` (App, 3); the `estimate.changed` sample in `MessagesTests`; the page order in `TrackLayoutPageTests`.
- **Review:** quick lens, 5 findings. 4 patched (3 medium, 1 low covered by the same fix), 1 low rejected (a live 30 s timer in a sub-second test).
- **Follow-up review recommended:** true (3 medium patched). Unverified risk: the thread-pool settings-change refresh and the revision ordering are covered only through direct `Refresh` calls; the working-set add-back for Memory mode is an approximation. Coordinator QA serves as the follow-up.
- **Verification (after the patches):** Release build 0 warnings; Contracts 115/115, Core 101/101, Engine 163 passed (19 explicit skipped), App 79 passed (7 explicit skipped); `tools/publish-engine.ps1` clean, 11.2 MB. Heavy not run: capture, encode, mux and audio are unchanged.
- **Residual risks:**
  - The page edits Global values only; per-profile overrides are 4.11's.
  - The real encoder's CBR accuracy is not measured here (the ring check uses synthetic packets); the builder's Human Check covers the live page.
  - `EngineHost.cs` overlaps with 4.9's parallel edits.
