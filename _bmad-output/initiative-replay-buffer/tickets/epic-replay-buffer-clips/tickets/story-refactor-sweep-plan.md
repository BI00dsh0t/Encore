---
title: 'Refactor sweep'
type: 'refactor'
ticket: '13'
created: '2026-11-04'
status: 'built'
baseline_revision: '199bd41f985b39037ac2da8b2bea362010cc978b'
route: 'full'
route_source: 'auto'
risk: 'low'
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

**Problem:** Epic 4 (4.1–4.12) left items for this sweep in its plans and the epic's Notes:
- 4.6's encoder-restart path (`RingGenerations.Restart`, `ReplayRingSink.Restart`, the previous-generation fallback in `Cut`, the restart predecessor and its retire check) has no caller. 4.9 switched chains with a sink per chain instead, so it is dead code with documented hazards.
- `RingFile.RemoveStale` deletes every `ring-*.bin`, so a foreign `ring-notes.bin` in a user-set `ringPath` would go (4.7 QA).
- `DiskRingClipTests` runs a real NVENC encode in the default suite; `CLAUDE.md` makes GPU encode tests Heavy/Explicit, and the Core Memory-vs-Disk test already covers identity (4.7 QA).
- Deferred items whose fix changes behaviour or lies outside epic 4 are only in plan frontmatter and Notes, not in `_bmad-output/deferred-work.md`.

**Approach:** Delete the restart path and collapse what only it needed. Narrow the stale match to the names `RingFile` itself creates. Mark the GPU test Heavy. Record the rest in `deferred-work.md` with owners. No other behaviour changes.

## Boundaries & Constraints

**Always:**
- Behaviour-preserving for every engine path in use: the Resize path, cuts, generation numbers, disposal timing of retired rings, and log text stay as they are.
- `RingGenerations.Cut` keeps its `(RingCut Cut, int Generation)?` shape; `ReplayRingSink.Generation`, `Resize`, `Cut`, `Lease`/`Release` keep their signatures.
- `RingGenerations.MinClipUs` stays (PacketRing's trim uses it).
- Keep each file's line endings, doc-comment style and file-scoped namespaces. Doc comments that describe the restart or fallback are rewritten to match.

**Never:**
- Touch `SessionOrchestrator.cs`, `EncoderChain.cs`, `LibraryWrites.cs`, `Contracts/*`, the App's Overlay or Settings pages: stories 5.9, 5.11, 6.6, 7.6 and the Overlay dot are building there. In `CaptureSession.cs`, only the one doc sentence named below.
- Fix the dispose-time `EndWrite` gap, the hotkey-capture collision, the `memory-margin` withdrawal or the switch audio seam: they change behaviour or belong to other epics, so they are only recorded.
- Add tests beyond adjusting the stale-file test.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Stale sweep | folder holds `ring-7.bin`, `ring-notes.bin`, `other.bin` | only `ring-7.bin` is deleted; returns 1 | in-use or undeletable files skipped, as now |
| Resize while a Clip writes | cut pinned on gen 1, Resize to gen 2 | old ring disposed when the cut is disposed; next cut from gen 2 starts at the old end | none |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Replay/RingGenerations.cs` -- `Restart` (~126), `RetireSlackUs`, `_predecessor`, the `StartUs == long.MaxValue` keyframe set and predecessor retire in `Append`, the fallback branch in `Cut`, `_predecessor = null` in `Open`/`Retire`. Without `Restart`, `Open` + `Resize` always leave one live generation (Resize retires the previous at once), so `_generations` only ever holds `_current`; the `previous`/`fallback` branch in `Cut` can never fire. A resize generation's `StartUs` is `next.FirstKeyUs`; generation 1's is `long.MinValue`; keep `Cut` returning null when `StartUs >= toUs`.
- `src/ReplayBuffer.Platform.FFmpeg/ReplayRingSink.cs` -- `Restart` (~135); `_streams` is a `Dictionary<int, List<ClipStream>>` only because a restart's generation had its own streams; Resize shares the list. `Release` frees `_streams.Values.Distinct()`. Class doc mentions `Restart`.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` (~450) -- the `Save` doc's sentence "less than 1 s past a generation's boundary, the previous one up to the boundary, where the Clip then ends and the next one starts (`RingGenerations.Cut`)".
- `src/ReplayBuffer.Core/Replay/RingFile.cs` -- `Prefix`, `Extension`, `RemoveStale` (~84) enumerates `Prefix + "*" + Extension`. Check how `Create` names files (`ring-<generation>.bin`).
- `tests/ReplayBuffer.Core.Tests/RingGenerationsTests.cs` -- first test exercises `Restart` only; the other two cover Resize and window bounds.
- `tests/ReplayBuffer.Core.Tests/DiskRingTests.cs` -- `RemoveStaleDeletesLeftRingFilesOnly` (~80).
- `tests/ReplayBuffer.Platform.FFmpeg.Tests/DiskRingClipTests.cs` -- one `[Fact]` (~38); see other Heavy tests (e.g. `tests/ReplayBuffer.App.Tests/OverlayTests.cs:250`) for the attribute pair.
- `_bmad-output/deferred-work.md` -- entries of `source_plan` / `summary` / `evidence` / `outcome`, grouped by epic, blank line between groups.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Core/Replay/RingGenerations.cs` -- remove `Restart`, `RetireSlackUs`, `_predecessor`, the fallback, and the restart-only `StartUs` handling; hold the one live generation without the list if that reads simpler, keeping `Users`/`Retired` so a ring a cut is reading is disposed after it; update the class and member docs -- dead code (4.6, 4.9)
- [x] `src/ReplayBuffer.Platform.FFmpeg/ReplayRingSink.cs` -- remove `Restart`; one `List<ClipStream>` instead of the per-generation dictionary; `Cut` throws `ObjectDisposedException` when released, as now; update docs -- dead code
- [x] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- drop the fallback sentence from `Save`'s doc (keep "the cut comes from one ring generation") -- stale doc
- [x] `src/ReplayBuffer.Core/Replay/RingFile.cs` + `tests/ReplayBuffer.Core.Tests/DiskRingTests.cs` -- `RemoveStale` deletes only `ring-<digits>.bin`; the test adds a `ring-notes.bin` that survives -- 4.7 QA
- [x] `tests/ReplayBuffer.Core.Tests/RingGenerationsTests.cs` -- delete the restart test
- [x] `tests/ReplayBuffer.Platform.FFmpeg.Tests/DiskRingClipTests.cs` -- `[Fact(Explicit = true)]` + `[Trait("Category", "Heavy")]` -- 4.7 QA, CLAUDE.md
- [x] `_bmad-output/deferred-work.md` -- add an epic 4 group: (a) a Clip writing at engine shutdown is not published and gets `BeginWrite` without `EndWrite` (4.1, 4.5), open, epic 7's LibraryActor; (b) Disk-mode slabs spill to native memory while long Clips pin ring blocks, silent (4.7), open, epic 8 hardening; (c) `memory-margin` warnings aren't withdrawn on leaving the margin (4.8), open, epic 5's status line; (d) capturing a combination an active Hotkey holds fires that action (4.12), open, needs an engine pause during capture, epic 8; (e) the chain switch's audio seam (up to ~10.7 ms) is unmeasured, and a Record press while a switch is armed starts nothing (4.9), open, epic 6 (Recordings) for the second, epic 8 for the first; (f) epic 4's sweep closed the restart path, the stale-file match and the Heavy marking.

**Acceptance Criteria:**
- Given the sweep, when `dotnet build ReplayBuffer.slnx -c Release` runs, then 0 warnings and 0 errors.
- Given the sweep, when the default runs of `ReplayBuffer.Core.Tests`, `ReplayBuffer.Platform.FFmpeg.Tests` and `ReplayBuffer.Engine.Tests` run, then all pass, covering the ring, Resize, Disk mode, saves in both storage modes and the session.
- Given the sweep changes engine code, when `pwsh tools/publish-engine.ps1` runs, then it succeeds with 0 IL2xxx/IL3xxx warnings.
- Given `grep -rn "Restart(" src/ReplayBuffer.Core/Replay src/ReplayBuffer.Platform.FFmpeg`, then nothing matches.

## Implementation Notes

- `RingGenerations` holds only `_current` (no list). `Resize` sets the new generation and retires the old one, whose ring is disposed once the last cut reading it drops `Users`, as before. `Generation.StartUs` is read-only now. `Cut` keeps its shape and returns null when the current generation starts at or after `toUs`.
- `ReplayRingSink` keeps one `List<ClipStream>`. `Cut` still throws `ObjectDisposedException` when the sink is released between the ring cut and the parameter copy.
- `RingFile.RemoveStale` still enumerates `ring-*.bin`, then skips any name whose middle isn't all ASCII digits (`IsRingName`).
- Left as is, since the plan allows only the one doc sentence in `CaptureSession.cs`: the comment above `ClipWrite`'s `endUs` still mentions "a ring generation's boundary when the cut came from the one before it", which no longer happens.
- After implementation, the orchestrating session also corrected that `endUs` comment (to "the press's keyframe") under the Always rule that restart/fallback docs are rewritten, a comment-only hunk; and, from review, rewrapped the `Save` doc and dropped the boundary clause from `ClipClaim.Succeeded`'s doc.

## Plan Change Log

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 3 findings — high 0, medium 0, low 3, false 0, maybe-false 0
- findings:
  - `[low]` `[patch]` `CaptureSession.cs` ~1437: the `endUs` comment edit goes past the Never rule's "one doc sentence", and the Implementation Notes said it was left — real conflict between Always (rewrite fallback docs) and Never; the edit is comment-only and removes a false statement, so it was kept and the notes now record it.
  - `[low]` `[patch]` `CaptureSession.cs` ~452: the rewrapped `Save` doc left a ~150-char line — reflowed to the block's ~100-char wrap.
  - `[low]` `[patch]` `ClipClaim.cs:42`: `Succeeded`'s doc still said the end could be a generation boundary — clause removed.

## Design Notes

Why removing the fallback is behaviour-preserving: a generation with a predecessor exists only after `Restart`. `Resize` calls `Open` (which retires all but the current) and then retires the previous, so outside `Restart` the list holds one generation and `previous` in `Cut` is always null.

Ticket verify ("the Save Hotkey still writes a multi-Track Clip in both storage modes"): the Engine suite's default run saves a multi-Track Clip through the session (`ReplayClipCaptureTests`, Memory mode); Disk mode is covered by the Core identity test by default and by the now-Heavy `DiskRingClipTests`, which this sweep runs once with `--explicit only` since it touches the ring.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: pass (likewise `Platform.FFmpeg.Tests`, `Engine.Tests`)
- `dotnet test --project tests/ReplayBuffer.Platform.FFmpeg.Tests -c Release --no-build --explicit only --filter-class "*DiskRingClipTests"` -- expected: pass (Disk-mode Clip byte-identical to Memory-mode)
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL warnings

## Auto Run Result

Status: built

**Summary:** Behaviour-preserving sweep of epic 4. Removed the unused encoder-restart path (4.6) that 4.9's sink-per-chain made dead: `RingGenerations.Restart`, the predecessor, `RetireSlackUs` and the previous-generation fallback, so `RingGenerations` holds one live generation and `ReplayRingSink` one stream list. `RingFile.RemoveStale` now deletes only `ring-<digits>.bin`. `DiskRingClipTests` (NVENC) is Heavy/Explicit. Deferred items that change behaviour or belong to other epics are recorded in `_bmad-output/deferred-work.md`.

**Files:**
- `src/ReplayBuffer.Core/Replay/RingGenerations.cs` -- restart path and fallback removed; one current generation
- `src/ReplayBuffer.Platform.FFmpeg/ReplayRingSink.cs` -- `Restart` removed; one `List<ClipStream>`
- `src/ReplayBuffer.Core/Replay/RingFile.cs` -- `RemoveStale` matches `ring-<digits>.bin` only (`IsRingName`)
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs`, `ClipClaim.cs` -- stale fallback wording removed from docs/comments
- `tests/ReplayBuffer.Core.Tests/RingGenerationsTests.cs` -- restart test deleted
- `tests/ReplayBuffer.Core.Tests/DiskRingTests.cs` -- `ring-notes.bin` survives the sweep
- `tests/ReplayBuffer.Platform.FFmpeg.Tests/DiskRingClipTests.cs` -- Heavy/Explicit
- `_bmad-output/deferred-work.md` -- epic 4 group (a)–(f)

**Review:** quick lens, 3 low findings, all patched (comment/doc only). Nothing deferred or rejected.

**Follow-up review recommended:** false (0 high, 0 medium patched).

**Verification:** Release build 0 warnings; default runs Core 110/110, Platform.FFmpeg 35 passed 2 skipped, Engine 167 passed 24 skipped; `DiskRingClipTests --explicit only` 1/1; `publish-engine.ps1` 0 IL warnings (11.3 MB). App and other test projects not run: the diff touches no App code.

**Residual risks:** none known; outside epic 4 nothing was changed (items for epics 5–8 are in `deferred-work.md`).
