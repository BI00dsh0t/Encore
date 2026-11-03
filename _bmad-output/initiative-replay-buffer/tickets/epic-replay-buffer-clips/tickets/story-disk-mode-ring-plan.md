---
title: 'Disk-mode ring'
type: 'feature'
ticket: '7'
created: '2026-11-03'
status: 'in-progress'
baseline_revision: 'ab97142fa244e23889a37a93ef5518bf32f3e5f5'
route: 'full'
route_source: 'auto'
risk: 'medium'
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

**Problem:** `replayStorage = disk` only logs "not built yet" and buffers in memory (`CaptureSession.Open`). R3 needs Disk mode: the ring's slabs in a preallocated `ring-<generation>.bin` under `ringPath`, sized from the storage estimate, at low I/O priority, with Clips identical to Memory mode's.

**Approach:** A Core `RingFile` (a preallocated file of fixed-size blocks) gives a `PacketRing` disk-backed slabs. The keyframe index, pins, trimming and generations stay the same: only where a slab's bytes live changes. A slab is either native memory or one block of the file. The file is deleted when its ring is disposed and its last pinned slab is freed. Stale `ring-*.bin` files are swept at engine start. The session picks the mode from its snapshot, so a mode change applies at the next session (the key is already `NextSession`).

## Boundaries & Constraints

**Always:**
- AD-8: no managed allocation per packet in `Append` (`RandomAccess.Write` on a span; the free-block queue is preallocated). Freeing a slab never touches bytes a cut still pins.
- AD-7: the ring file's handle gets the low I/O priority hint (`SetFileInformationByHandle`, `FileIoPriorityHintInfo`, `IoPriorityHintLow`), set by a callback that Platform.Windows provides, so Core stays OS-neutral.
- Disk use stays within the estimate. The file is never grown past its preallocated size. A block request with none free, or a packet bigger than a block, gets a native-memory slab and is counted (`MemorySlabs`).
- File names: `ring-<n>.bin`, where `<n>` is a process-wide counter. Two sessions (one drained, one new) or two generations never share a file.
- If the file can't be created (disk full, bad path), log a warning and use a Memory ring. The session never fails over it.
- Lean tests (CLAUDE.md "Tests"), all in temp folders. Keep changes to the ring, the sink, the clip writer's read and the session's ring creation. 6.1 is changing the capture session and encode chain.

**Never:**
- A Contracts change. `replayStorage` and `ringPath` already exist and apply `NextSession`.
- The Settings page, the estimate's UI or its free-space warning (story 10). Disk mode's own RAM margin (story 8).
- Memory-mapping the file. Its writeback can't take the low I/O priority hint.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Same packets, both modes | One packet sequence fed to a Memory and a Disk ring; the same cuts | Identical packets and bytes; written Clips are byte-identical | — |
| 10-minute CBR run | Disk ring, 30 s window, packets at the estimate's rate for 600 s | `MemorySlabs` is 0; the file length is at most the estimate | — |
| Killed engine | `ring-7.bin` left in the ring folder | Removed by `RingFile.RemoveStale` at the next engine start | A file in use is skipped |
| Ring disposed while a cut pins it | Disk ring disposed, cut still held | Cut bytes intact; the file is deleted once the cut is disposed | — |
| No room on disk | `RingFile.Create` throws `IOException` | Memory ring, with a warning log line | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Replay/RingFile.cs` (new, public sealed):
  - `const string Prefix = "ring-"`, `Extension = ".bin"`; `Path`, `Bytes` (blocks × block size), `BlockBytes`.
  - `static RingFile Create(string folder, long bytes, int blockBytes = PacketRing.DefaultSlabBytes, Action<SafeFileHandle>? configure = null)`:
    - Create the folder. The name takes the next value of the process-wide counter.
    - The block count is `max(2, ceil(bytes / blockBytes))`.
    - `File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, FileOptions.DeleteOnClose, preallocationSize: Bytes)`, then `configure?.Invoke(handle)`.
  - Free blocks: a FIFO `Queue<long>` of offsets, preallocated, under its own lock (slabs are freed on writer threads).
  - Internal members: `TryRent(out long offset)`, `Return(long offset)`, `Write(long offset, ReadOnlySpan<byte>)`, `Read(long offset, Span<byte>)` (via `RandomAccess`), and an idempotent `Close()` that disposes the handle (`DeleteOnClose` deletes the file) and then tries `File.Delete`.
  - `static int RemoveStale(string folder)`: delete each `ring-*.bin`, skipping `IOException` and `UnauthorizedAccessException`. A missing folder returns 0.
- `src/ReplayBuffer.Core/Replay/ReplayStorageEstimate.cs` (new, public static), for story 10 to reuse:
  - `long Bytes(VideoSettings video, int tracks, int windowSeconds)` = `rate / 8 × (window + 3 s) + 2 × DefaultSlabBytes`.
  - The 3 s covers `SlackUs` and one GOP.
  - `rate` = `BitrateKbps × 1000`, times 1.5 for VBR (the epic's worst case), plus `tracks × AacBitsPerSecond` (192 000, the same as `AacEncoder.BitRate`).
- `src/ReplayBuffer.Core/Replay/PacketRing.cs`:
  - New constructor `PacketRing(long windowUs, int capacity, RingFile file)`: the slab size is `file.BlockBytes`, and the ring owns the file.
  - `Slab`:
    - Either memory (`Data`) or a file block (`File`, `FileOffset`).
    - Add `Write(int offset, ReadOnlySpan<byte>)` and `Read(int offset, Span<byte>)`.
    - At 0 references, a memory slab frees its native memory and a file slab returns its block. Then `OnFreed`.
  - `NewSlab` on a disk ring: rent a block when `capacity <= BlockBytes` and one is free. Otherwise use a memory slab and increment `MemorySlabs` (internal).
  - `Append` writes with `slab.Write`. `CopyBatchTo` reads disk entries into a scratch `byte[]` that it grows as needed (once per batch, not per packet).
  - When the ring is disposed and `_liveSlabs` reaches 0 (in `Dispose` or `OnFreed`), call `file.Close()`.
  - `RingPacket`: replace `Bytes` with `CopyTo(Span<byte> destination)`, and add `internal byte[] ToArray()` for tests. Update the class docs (the Memory-mode-only wording).
- `src/ReplayBuffer.Platform.FFmpeg/ClipWriter.cs:89` -- `av_new_packet(packet, ring.Length)`, then `ring.CopyTo(new Span<byte>(packet->data, ring.Length))`.
- `src/ReplayBuffer.Platform.Windows/` -- new `FileIoPriority.SetLow(SafeFileHandle)`, through CsWin32: add `SetFileInformationByHandle` and `FILE_IO_PRIORITY_HINT_INFO` to `NativeMethods.txt`. On failure, log nothing and don't throw (best effort).
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs`:
  - `CaptureSessionOptions.RingFolder` (string?, the default ring folder).
  - `Open:222-227`: drop the "not built yet" line, and set `_disk` from `Snapshot.ReplayStorage`.
  - `NewRing(seconds)`: on disk, `new PacketRing(us, capacity, RingFile.Create(Snapshot.RingPath ?? _options.RingFolder, ReplayStorageEstimate.Bytes(video, tracks, seconds), configure: FileIoPriority.SetLow))`. Catch `IOException` and `UnauthorizedAccessException` (and a null folder) with a warning, and use a Memory ring.
  - The `StartOutput` and `ResizeAt` log lines name the mode, and the file and its size when on disk.
- `src/ReplayBuffer.Engine/EngineHost.cs:235` -- `RingFolder = Path.Combine(instance.LocalDataFolder, "ring")`. Before the orchestrator is created, `RingFile.RemoveStale` on that folder and on the current document's `RingPath` when set. Log the count at Information when it is above 0.
- Tests: `tests/ReplayBuffer.Core.Tests/PacketRingTests.cs` and `RingGenerationsTests.cs` change `.Bytes` to `.ToArray()`. `PacketRingTests` has the `Streams`, `Feed` and `Pattern` helpers. `tests/ReplayBuffer.Platform.FFmpeg.Tests/ReplayCutTests.cs` shows the GPU encode, `ClipWriter` and skip pattern.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Core/Replay/{RingFile,ReplayStorageEstimate}.cs` -- new, as in the Code Map -- R3 Disk storage and its size
- [x] `src/ReplayBuffer.Core/Replay/PacketRing.cs` -- disk slabs, `MemorySlabs`, closing the file at the last free, `RingPacket.CopyTo` -- same index, pins and trimming in both modes
- [x] `src/ReplayBuffer.Platform.FFmpeg/ClipWriter.cs` -- read via `CopyTo` -- Clips from either mode
- [x] `src/ReplayBuffer.Platform.Windows/{FileIoPriority.cs,NativeMethods.txt}` -- the low I/O priority hint -- AD-7
- [x] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs`, `src/ReplayBuffer.Engine/EngineHost.cs` -- Disk rings per snapshot, fall back to memory, the default folder, the stale sweep at start
- [x] New `tests/ReplayBuffer.Core.Tests/DiskRingTests.cs` -- three tests:
  - The same synthetic sequence (30 fps, 1 s GOP, AAC) into a Memory and a Disk ring. Two cuts at the same points give equal packet metadata and bytes. Then dispose the Disk ring while a cut is held: the file exists until the cut is disposed, then it is gone.
  - 600 s at the estimate's CBR rate (2 Mbps video, one Track, 30 s window) into a Disk ring: `MemorySlabs` is 0, and the file length is at most `ReplayStorageEstimate.Bytes`.
  - `RemoveStale` deletes a planted `ring-7.bin` and leaves `other.bin`.
- [x] `src/ReplayBuffer.Engine/Sessions/ReplayRings.cs` (new, internal static) -- `PacketRing Create(long windowUs, int capacity, string? diskFolder, long diskBytes, ILogger log)`: `diskFolder` null means Memory mode. It holds `NewRing`'s create-or-fall-back logic, so the fallback can be tested; `CaptureSession.NewRing` calls it. Add `internal bool OnDisk` (or `File`) to `PacketRing`. One test in `tests/ReplayBuffer.Engine.Tests/ReplayRingsTests.cs`: a `diskFolder` that is an existing file's path gives a Memory ring, without throwing.
- [x] New `tests/ReplayBuffer.Platform.FFmpeg.Tests/DiskRingClipTests.cs` -- one test, skipped like `ReplayCutTests`:
  - About 4 s of GPU encode and AAC go through a test tee sink, which clones each packet with `av_packet_ref`, into a Memory and a Disk `ReplayRingSink`.
  - Cut both at the same key, write both with `ClipWriter`, and compare the files byte for byte.

**Acceptance Criteria:**
- Given the same encoded packets fed to a Memory and a Disk ring, when each is saved at the same QPCs, then the two Clips are byte-identical.
- Given a Disk ring over a 10-minute synthetic CBR run, when it ends, then its file never exceeded the estimate and no slab fell back to memory.
- Given a ring file left by a killed engine, when the engine starts, then the file is removed.
- Given `replayStorage` disk, when a session starts, then its ring file is `ring-<n>.bin` under `ringPath` (default `%LOCALAPPDATA%\<instance>\ring`), and the file is deleted when the ring is disposed and its last pin drops.
- Given the default run, when the 0-warning Release build, the Core, Platform.FFmpeg and Engine suites and the NativeAOT publish run, then all pass.

## Implementation Notes

- `PacketRing.File` (`RingFile?`) is public, not internal: Engine has no `InternalsVisibleTo` from Core, and the session's log lines and `ReplayRingsTests` read it. It doubles as `OnDisk`.
- The session keeps the Disk-mode folder (`_ringFolder`, null in Memory mode) in place of a `_disk` flag; Disk mode with no folder at all logs a warning and buffers in memory.
- `ResizeAt` disposes the new ring if `Resize` throws, so its file is not left open.
- `tests/ReplayBuffer.Platform.FFmpeg.Tests` now sets `AllowUnsafeBlocks` for the test tee sink, and `PacketRingTests.Streams`/`Feed`/`Pattern` are internal so `DiskRingTests` reuses them.

## Plan Change Log

- `RingFile.Create`'s block count is `max(2, floor(bytes / blockBytes))`, not `ceil`. With `ceil`, the FIFO free list cycles through every block, so the file grows to a whole block past the estimate (5 blocks = 21 MB against a 17.4 MB estimate in the 10-minute test), breaking "disk use stays within the estimate". The estimate's two slabs of headroom cover the rounding down; the 10-minute CBR test shows `MemorySlabs` 0.

## Review Triage Log

## Design Notes

**Why `ring-<n>` is process-wide.** A drained session keeps its ring for 30 s while the next Target's session starts, and each resize opens a new ring. A per-session generation number would collide. Each file is still one ring generation; the number just counts across the engine.

**Why direct positional writes.** `RandomAccess.Write` into the system cache is a memcpy for the encoder thread. Writeback then happens at the handle's low priority hint. Reads go through the same handle at explicit offsets, so concurrent cuts need no extra locking. `DeleteOnClose` also removes the file when the process dies with the handle open; the start-up sweep covers the rest (power loss).

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Platform.FFmpeg.Tests -c Release --no-build` -- expected: all pass (ClipWriter changed)
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- expected: all pass (the session changed)
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings
