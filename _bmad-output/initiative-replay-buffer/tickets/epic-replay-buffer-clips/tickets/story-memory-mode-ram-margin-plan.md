---
title: 'Memory-mode RAM margin'
type: 'feature'
ticket: '8'
created: '2026-11-03'
status: 'in-progress'
baseline_revision: '2b0eb895423eb38e8d4b60ec4945c12394cd0d57'
route: 'full'
route_source: 'auto'
risk: 'high'
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
- [ ] `src/ReplayBuffer.Core/Replay/MemoryMargin.cs` -- the margin, the port and the events -- R3's margin
- [ ] `src/ReplayBuffer.Core/Replay/PacketRing.cs` -- the margin check in `NewSlab`, `Reclaim`, the shared head drop, `Release` returns freed -- stop growing, oldest unpinned first
- [ ] `src/ReplayBuffer.Platform.Windows/{SystemMemoryStatus.cs,NativeMethods.txt}` -- the real status -- Windows' available physical memory
- [ ] `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- `memory-margin` -- the warning code
- [ ] `src/ReplayBuffer.Engine/...MemoryMarginReporter.cs`, `Sessions/{CaptureSession,ReplayRings}.cs`, `EngineHost.cs` -- the warning, and the margin wired into every session ring -- epic 5's status line
- [ ] New `tests/ReplayBuffer.Core.Tests/MemoryMarginTests.cs` -- two tests, with a settable fake status (16 KiB slabs, so the slab counts are small):
  - Feed a 60 s window for 20 s with plenty of RAM, then pin a cut of the last 10 s. Lower the available memory below the margin and feed 20 s more. Then:
    - `LiveSlabs` never exceeds its value at the drop, plus the slabs the cut pins.
    - The cut's packets still match `Pattern`.
    - The ring's oldest entry is now well after its oldest before the drop.
    - A new cut over the full window ends at the newest content, and spans less than the window.
    - `Entered` was raised exactly once.
  - The events: raise the available memory past margin + slack, then lower it again, and a second `Entered` is raised. Lower it so far that the trim leaves under 1 s (a ring holding only small slabs), and one `ShortRing` is raised, not one per slab.
- [ ] New `tests/ReplayBuffer.Engine.Tests/MemoryMarginReporterTests.cs` -- one test:
  - Two entries into the margin and one short ring give three `memory-margin` warnings: severity Warning, source `replay`, no overlay, dismissable.

**Acceptance Criteria:**
- Given a Memory-mode ring and a fake memory status, when available memory falls below `max(4 GB, 15% of installed)`, then the ring stops growing and frees its oldest unpinned slabs, and slabs a cut pins keep their bytes.
- Given the margin is entered, when the ring keeps receiving packets, then one `memory-margin` warning is raised, and another only after the margin is left and entered again.
- Given the margin trim left the ring under 1 s, when that happens, then one `memory-margin` warning says so.
- Given a Clip saved while inside the margin, when it is cut over the Buffer Window, then it ends at the newest content and is shorter than the Buffer Window.
- Given the default run, when the 0-warning Release build, the Core, Contracts and Engine suites and the NativeAOT publish run, then all pass.

## Implementation Notes

## Plan Change Log

## Review Triage Log

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
