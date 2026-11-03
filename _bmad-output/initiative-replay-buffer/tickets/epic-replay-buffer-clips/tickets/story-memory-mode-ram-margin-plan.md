---
title: 'Memory-mode RAM margin'
type: 'feature'
ticket: '8'
created: '2026-11-03'
status: 'built'
baseline_revision: '2b0eb895423eb38e8d4b60ec4945c12394cd0d57'
route: 'full'
route_source: 'auto'
risk: 'high'
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

**Problem:** A Memory-mode ring takes native memory for each new slab, however little RAM Windows has left. A long Buffer Window, or a long Clip pinning its slabs while it writes, can push the machine into paging under the game. R3 needs the ring to stop growing at a safe margin, and to tell the status line when it does.

**Approach:**
- A Core `MemoryMargin` reads available and installed physical memory through a port. The ring asks it before each new native-memory slab.
- When the slab would drop available memory below `max(4 GB, 15% of installed)`, the ring doesn't grow. It trims from its oldest entries until slabs whose last reference it held free at least the slab's size, then allocates.
- Available memory already counts pinned bytes. A pinned slab leaves the ring but stays allocated for its cut, so trimming goes on past it to the oldest slab only the ring holds.
- `MemoryMargin` raises `Entered` once per entry into the margin, and `ShortRing` once per entry when the ring then holds under 1 s. The engine turns each into a `warning.raised { code: memory-margin }`.

## Boundaries & Constraints

**Always:**
- AD-8: nothing managed is allocated per packet. The status is read only when a new memory slab is needed, not on every `Append`. A margin trim never frees bytes a `RingCut` pins, and never drops the current slab's entries.
- The margin applies to every native-memory slab: Memory mode, and a Disk ring's spill slabs (4.7's advisory). A disk ring retries a block after its trim, before it falls back to memory.
- One `MemoryMargin` per engine, shared by every session and ring generation, so a resize or a drained session doesn't re-arm the warning.
- Hysteresis: once inside, the ring leaves the margin only when the slab would still leave `margin + LeaveSlackBytes` (256 MiB) available. Re-entering raises again.
- If the trim can free nothing (only pinned slabs and the current slab are left), allocate anyway. The pins go when their writes finish.
- Lean tests (CLAUDE.md "Tests"), with a fake memory status and no real memory pressure.

**Never:**
- A setting for the margin. It is a constant (an epic decision).
- Changes to the save path, the cut, `RingGenerations`' generation logic, or the estimate (story 10).
- Contracts changes beyond adding `ErrorCodes.MemoryMargin` (additive, AD-22).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Plenty of RAM | Fake: 32 GB installed, 20 GB available | The ring grows as before; no event | — |
| Margin reached | Available drops to 4.5 GB of 32 (the margin is 4.8 GB) | Slab count holds steady: the oldest unpinned slabs are freed for each new one. One `Entered` | — |
| Pinned head | A cut pins the oldest slabs while inside | The cut's bytes stay intact. The ring trims past them to free ring-only slabs | — |
| Short ring | Inside, and the trim leaves under 1 s | One `ShortRing` per entry | — |
| Leave, re-enter | Available recovers past margin + 256 MiB, then drops again | A second `Entered` | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Replay/MemoryMargin.cs` (new):
  - `public readonly record struct MemoryStatus(long AvailableBytes, long TotalBytes)`, and `public interface IMemoryStatus { MemoryStatus Read(); }`.
  - `public enum MemoryMarginEvent { Entered, ShortRing }`.
  - `public sealed class MemoryMargin(IMemoryStatus status)`:
    - The constants `FloorBytes = 4 GiB`, `InstalledShare = 0.15` and `LeaveSlackBytes = 256 MiB`, and `static long MarginBytes(long totalBytes)`.
    - `event Action<MemoryMarginEvent>? Raised`, and `bool Inside`.
    - `bool Allows(long bytes)`: deny when `available - bytes < margin + (inside ? LeaveSlack : 0)`. The state is guarded by its own lock. It raises `Entered` on entering (and resets the short flag), and leaves when the call allows.
    - `void ReportShortRing()`: raises `ShortRing` once per entry, and only while inside.
    - Events are invoked outside its lock.
- `src/ReplayBuffer.Core/Replay/PacketRing.cs`:
  - Both constructors take an optional `MemoryMargin? margin = null` (the Disk constructor too).
  - `NewSlab`: before a native-memory slab, if `margin?.Allows(capacity) == false`, call `Reclaim(capacity)`. On a disk ring, retry `TryRent` after it.
  - `Reclaim(long bytes)`: drop head entries while the bytes freed are below `bytes`, `_count > 0` and the head entry's slab isn't `_current`. Then, if the ring is empty or spans under `RingGenerations.MinClipUs` (`_newestUs - head.TimeUs`), call `margin.ReportShortRing()`.
  - Factor the head drop out of `Trim` (the entry, the keyframe index, `Live`, `ReleaseHeld`) so both use it.
  - `Slab.Release()` returns whether it freed. A freed memory slab counts its `Capacity` toward `Reclaim`; a returned file block counts nothing.
  - Update the class doc.
- `src/ReplayBuffer.Platform.Windows/SystemMemoryStatus.cs` (new, public sealed, `IMemoryStatus`): `GlobalMemoryStatusEx` through CsWin32, with `dwLength` set. Add `GlobalMemoryStatusEx` and `MEMORYSTATUSEX` to `NativeMethods.txt`. On failure, return `(long.MaxValue, 0)`: never at the margin.
- `src/ReplayBuffer.Contracts/ErrorCodes.cs`: `MemoryMargin = "memory-margin"`, with a doc comment in the warning style, added to `All`.
- `src/ReplayBuffer.Engine/Replay/MemoryMarginReporter.cs` (new, internal sealed; or beside `DeviceDropoutReporter` in a fitting folder), modelled on `Audio/DeviceDropoutReporter.cs`:
  - It subscribes to `Raised` and calls `Warnings.Raise(ErrorCodes.MemoryMargin, Warning, "replay", message, overlay: false)`.
  - The message for `Entered`: Clips may be shorter than the Buffer Window. For `ShortRing`: under 1 s held, so a save makes no Clip until memory frees up.
  - It is `IDisposable` and unsubscribes.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs`: `CaptureSessionOptions.Margin` (`MemoryMargin?`), and `NewRing` passes it to `ReplayRings.Create`.
- `src/ReplayBuffer.Engine/Sessions/ReplayRings.cs`: `Create` gains `MemoryMargin? margin` and passes it to every `PacketRing` it makes. Update `tests/ReplayBuffer.Engine.Tests/ReplayRingsTests.cs`'s call.
- `src/ReplayBuffer.Engine/EngineHost.cs`:
  - Near `dropouts`, create `var margin = new MemoryMargin(new SystemMemoryStatus())` and a `MemoryMarginReporter(margin, warnings)`. Dispose the reporter where `dropouts` is disposed, at lines ~498 and ~541. Hold it as a field (`_marginReporter`), as `_feedbackCheck` is held.
  - Pass `Margin = margin` in `CaptureSessionOptions` (line ~304).
- Reuse: `tests/ReplayBuffer.Core.Tests/PacketRingTests.Streams`/`Feed`/`Pattern` (internal). `LiveSlabs` is internal, and Core tests can see it.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Core/Replay/MemoryMargin.cs` -- the margin, the port and the events -- R3's margin
- [x] `src/ReplayBuffer.Core/Replay/PacketRing.cs` -- the margin check in `NewSlab`, `Reclaim`, the shared head drop, `Release` returns freed -- stop growing, oldest unpinned first
- [x] `src/ReplayBuffer.Platform.Windows/{SystemMemoryStatus.cs,NativeMethods.txt}` -- the real status -- Windows' available physical memory
- [x] `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- `memory-margin` -- the warning code
- [x] `src/ReplayBuffer.Engine/...MemoryMarginReporter.cs`, `Sessions/{CaptureSession,ReplayRings}.cs`, `EngineHost.cs` -- the warning, and the margin wired into every session ring -- epic 5's status line
- [x] New `tests/ReplayBuffer.Core.Tests/MemoryMarginTests.cs` -- two tests, with a settable fake status (16 KiB slabs, so the slab counts are small):
  - Feed a 60 s window for 20 s with plenty of RAM, then pin a cut of the last 10 s. Lower the available memory below the margin and feed 20 s more. Then:
    - `LiveSlabs` never exceeds its value at the drop, plus the slabs the cut pins.
    - The cut's packets still match `Pattern`.
    - The ring's oldest entry is now well after its oldest before the drop.
    - A new cut over the full window ends at the newest content, and spans less than the window.
    - `Entered` was raised exactly once.
  - The events: raise the available memory past margin + slack, then lower it again, and a second `Entered` is raised. Lower it so far that the trim leaves under 1 s (a ring holding only small slabs), and one `ShortRing` is raised, not one per slab.
- [x] New `tests/ReplayBuffer.Engine.Tests/MemoryMarginReporterTests.cs` -- one test:
  - Two entries into the margin and one short ring give three `memory-margin` warnings: severity Warning, source `replay`, no overlay, dismissable.

**Acceptance Criteria:**
- Given a Memory-mode ring and a fake memory status, when available memory falls below `max(4 GB, 15% of installed)`, then the ring stops growing and frees its oldest unpinned slabs, and slabs a cut pins keep their bytes.
- Given the margin is entered, when the ring keeps receiving packets, then one `memory-margin` warning is raised, and another only after the margin is left and entered again.
- Given the margin trim left the ring under 1 s, when that happens, then one `memory-margin` warning says so.
- Given a Clip saved while inside the margin, when it is cut over the Buffer Window, then it ends at the newest content and is shorter than the Buffer Window.
- Given the default run, when the 0-warning Release build, the Core, Contracts and Engine suites and the NativeAOT publish run, then all pass.

## Implementation Notes

- `Reclaim` on a Disk ring also stops once it has returned a file block the new slab fits, since `NewSlab` then retries a block and needs no new memory. Counting only freed memory slabs there would trim through the whole file to reach the newer spilled slabs. A block returned for an oversized packet doesn't stop it.
- After review: `Reclaim` first checks `AnyFreeable` (a ring-only, non-current slab before the current one, in memory or a fitting block) and returns at once when there is none. It reports a short ring only after dropping something.
- The reporter is `src/ReplayBuffer.Engine/Sessions/MemoryMarginReporter.cs`. `Raised` fires on the ring's appending thread, under that ring's lock but outside the margin's.
- Test 1 asserts `LiveSlabs <= atDrop`: `atDrop` already counts the cut's pinned slabs, since the ring still holds them at the drop. The short-ring case uses one keyframe of 40 slabs, whose `Reclaim` empties every small slab but the current one.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 4 findings — high 0, medium 3, low 1, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` `Reclaim` reported `ShortRing` on a brand-new ring (a session start, resize or restart while the shared margin is inside), though nothing was trimmed. Confirmed: the first `NewSlab` has `_count == 0`. Fixed: a short ring is reported only when this `Reclaim` dropped an entry, and the freeability check below returns first on a new ring.
  - `[medium]` `[patch]` With a cut pinning the whole window, `Reclaim` emptied the ring back to the current slab and freed nothing (every `DropHead` returns null). Confirmed by tracing a full-window `Cut`. Fixed: the new `AnyFreeable` returns early unless a held, non-current slab is ring-only (`Slab.RingOnly`), a memory slab (or a block when one fits), and wholly before the current slab's entries. New test `InsideTheMarginAFullyPinnedRingTrimsNothing`.
  - `[medium]` `[patch]` On a Disk ring of file blocks only, a packet bigger than a block wiped the disk buffer for no RAM. Same root cause as the row above, fixed by the same `AnyFreeable` check: with `blockFits` false, a file slab doesn't count. The "wholly before the current slab" condition, added on the main side, also covers an oversized packet's own slab that sits after the current slab's first entry.
  - `[low]` `[patch]` Test 1's `span < 60 s` passed without the margin, since only 40 s were fed. Fixed: it now asserts `< 25 s`, which fails without the trim.

## Design Notes

**Why trim past pinned slabs.** A cut pins a contiguous range from its keyframe to the press, and the ring can't drop entries from its middle without leaving a gap in every later Clip. So freeing memory means trimming from the head: through the pinned slabs (the trim frees nothing, since the cut keeps them alive) to the oldest slab the ring alone holds. The cut's bytes are untouched. Its slabs are freed when its write ends, and available memory, which already counted them, rises.

**Why no net growth rather than shrink-to-margin.** If another program takes RAM, shrinking the ring to win back the margin could empty it in one call. Replacing slab for slab keeps the newest content and holds the ring's RAM flat. Available memory includes the ring's own slabs, so the ring is never what pushes it further down.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build` -- expected: all pass (ErrorCodes registry)
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

## Auto Run Result

- **Summary:** R3's RAM margin.
  - Before each native-memory slab, a ring asks the engine's shared `MemoryMargin` (Windows' available physical memory, via `GlobalMemoryStatusEx`).
  - When the slab would leave less than `max(4 GiB, 15% of installed)`, the ring stops growing. It trims its oldest entries until slabs only it held free the new slab's size, going past slabs a cut pins (their bytes stay), then allocates.
  - It trims nothing when nothing could be freed. Disk rings get the same margin for their spill slabs.
  - Each entry into the margin, and a margin trim that leaves under 1 s, raises one `warning.raised { code: memory-margin }`.
  - Leaving takes 256 MiB of hysteresis.
- **Files:**
  - Core: new `Replay/MemoryMargin.cs` (the port, the margin and its events). `Replay/PacketRing.cs` gains the margin check, `Reclaim`/`AnyFreeable`, a shared `DropHead`, and a `Slab.Release` that returns whether it freed.
  - Contracts: `ErrorCodes.MemoryMargin`.
  - Platform.Windows: new `SystemMemoryStatus.cs`, plus `NativeMethods.txt`.
  - Engine:
    - New `Sessions/MemoryMarginReporter.cs`.
    - `CaptureSessionOptions.Margin`.
    - `ReplayRings.Create` takes the margin.
    - `EngineHost` makes one margin and one reporter.
  - Tests:
    - New `MemoryMarginTests` (3, Core) and `MemoryMarginReporterTests` (1, Engine).
    - `ReplayRingsTests` updated for the new signature.
- **Review:** quick lens, 4 findings. All 4 were patched: 3 medium (two from one root cause) and 1 low. Nothing was deferred or rejected.
- **Follow-up review recommended:** true. Three medium findings were patched. The unverified risk: the margin trim (`Reclaim`) running alongside a Buffer Window resize's `CopyTailTo` and a writer's unpin is covered by no test. Coordinator QA serves as the follow-up.
- **Verification (after the patches):**
  - `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings.
  - Core 93/93, Contracts 109/109, Engine 160 passed with 19 Explicit skipped.
  - `tools/publish-engine.ps1`: clean, 11.0 MB.
  - Heavy not run: capture, encode, mux and audio are unchanged, and the margin is off unless memory is low.
- **Residual risks:**
  - `MemoryMargin.Raised` runs `Warnings.Raise` on the encoder thread under the ring's lock, once per entry. It doesn't call back into the ring.
  - `SystemMemoryStatus` is covered only by the build and the publish.
  - No real Disk ring is tested under the margin.
  - The warnings aren't withdrawn when the margin is left. They go only by `warning.dismiss`, like `device-dropout`.
