---
title: 'Ring generations and Buffer Window resizing'
type: 'feature'
ticket: '6'
created: '2026-11-03'
status: 'built'
baseline_revision: 'd6b57b6ef463e170bf92f9b0f96ea25e148f419d'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: ['oversized']
deferred:
  - summary: >-
      A fallback Clip just after a restart ends at the boundary, but a concurrent write has already claimed past it, leaving [boundary, key) in neither Clip.
    evidence: |-
      ClipWrite.Run claims toUs before RingGenerations.Cut decides the previous-generation fallback, so ClipClaim's in-flight max is the key, not the boundary. Needs a restart plus two writes in flight within ~1 s (saveCooldown 0); no engine path calls Restart yet. For 4.9: decide the generation (and so the end) before publishing the claim.
    location: >-
      src/ReplayBuffer.Engine/Sessions/CaptureSession.cs ClipWrite.Run / ClipClaim
    severity: medium
  - summary: >-
      VideoMicros and AllStreamsPast read only the current generation, so a save waiting across a Restart misreads its key.
    evidence: |-
      ReplayRingSink.VideoMicros converts with the current ring's video stream (-1 until the new chain adds streams); Restart documents that no save may be waiting. 4.9's chain switch or device-lost recovery must drain in-flight saves first, or convert per generation.
    location: >-
      src/ReplayBuffer.Platform.FFmpeg/ReplayRingSink.cs VideoMicros / AllStreamsPast
    severity: medium
---

<intent-contract>

## Intent

**Problem:** The Replay Buffer is one `PacketRing` for the session's whole life, sized from the snapshot's `bufferWindow` at the anchor. Nothing models a forced encoder restart (device lost, chain rebuild), which can change the streams' codec parameters and must count as a Reset (R5, AD-8). A `bufferWindow` change never reaches a running session.

**Approach:** The ring sink holds numbered **generations**, each a `PacketRing` with its own streams. Core's new `RingGenerations` does the routing, cutting and retiring, so it can be tested with synthetic packets.
- A **restart** opens an empty generation. The encoders of the new chain add their streams to it, and its first video keyframe is its start, the boundary. A Clip holds one generation. A cut whose end lies less than 1 s past a boundary uses the previous generation, ending at the boundary, if that holds at least 1 s.
- A **resize** happens at the next Reset. The orchestrator gives the session the Effective `bufferWindow` when it Resets. If that value changed, the session opens a generation with a new ring of the new size and the same streams, seeded with a copy of the old ring's packets from the Reset keyframe on. Then it retires the old ring. Slabs a writing Clip pins outlive the ring, so the Clip is never cut short.
- The engine has no restart path yet. 4.9's chain switch, or device-lost recovery, will call `Restart`. This story delivers it at the sink and proves it with a simulated restart.

## Boundaries & Constraints

**Always:**
- AD-8: no managed allocation per packet in `Append` (routing is a field read under the lock). Retiring a ring never frees pinned bytes. A Clip holds exactly one generation.
- AD-19: only the orchestrator decides a Reset. The session applies the new window inside `IClipWrite.Reset()` on the orchestrator thread.
- The cut, filter and sort of a long Clip run outside the generations lock, so a 2 h cut never blocks the encoders. Only the resize's tail copy holds the lock, and it copies only a few seconds of packets.
- Resizing keeps Clips gapless: the new generation starts at the Reset keyframe, so the next Clip starts exactly where the last one ended.
- Lean tests (CLAUDE.md "Tests"). Keep changes inside the ring, the sink and the session's Replay code. 6.1 is changing the capture and encode chain.

**Never:**
- An engine encoder-restart or device-lost path, chain switching, or Disk mode (4.7, 4.9).
- A Contracts change: `bufferWindow` already has no upper limit (`v >= 1`) and applies `NextSession`, which means the next Reset.
- A resize on a drained session, or before the session's first Reset.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Restart mid-capture | Generation 1 runs 0–10 s; a restart's first key is at 10 s; a press at 14 s | The Clip runs from 10 s to the press | — |
| Press 0.5 s after the restart | Cut `[0, 10.5 s)` | The previous generation, `[first key, 10 s)`, ending at the boundary; the next Clip starts at 10 s | — |
| Both generations too short | Generation 1 holds 0.6 s; press 0.5 s after the restart | No cut | `clip-too-short` |
| 30 s window | 40 s fed; press at 40 s | The Clip lasts no more than 30 s | — |
| 2 h window | `bufferWindow` 7200 | Accepted by the key and sized by the session (capacity capped at `1 << 24` entries) | — |
| Shrink while a Clip is writing | The Clip's cut pins generation 1; Reset, then a resize to 30 s | The Clip's bytes stay intact; generation 1's slabs are freed once its cut is disposed | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Replay/PacketRing.cs`:
  - Add a public `long WindowUs`.
  - Add an internal `long FirstKeyUs`: the oldest indexed keyframe's `TimeUs`, or `long.MaxValue` when there is none.
  - Add an internal `CopyTailTo(PacketRing target, long fromUs)`. If `target` has no streams, register the source's streams (same order, time bases and video flag) on it. Then append to `target` every entry from the position `Cut` would copy from (the first key at or after `fromUs`, minus `LookBackUs`; with no key, the first entry at or after `fromUs - LookBackUs`) through the tail. Take the source lock, then the target's. Reuse `Append` for the target.
  - Leave `Cut`'s semantics as they are.
- New `src/ReplayBuffer.Core/Replay/RingGenerations.cs` (public sealed, `IDisposable`):
  - Generations oldest first. Each has `Number` (counting from 1), `Ring`, `StartUs` (`long.MinValue` for generation 1; for a later one, its ring's `FirstKeyUs` once known, `long.MaxValue` before that), `Users` and `Retired`. One `Lock`.
  - Members: `Current` (number); `Ring` (the current ring); `AddStream` (to the current ring); `Append` (to the current ring under the lock, then the retire check); `AllStreamsPast` and `ToMicros(stream, ticks)` (the current ring); `int Restart(PacketRing next)`; `int Resize(PacketRing next, long fromUs)` (copy the tail into `next`, make it current, retire the old one); `(RingCut Cut, int Generation)? Cut(long fromUs, long toUs)`; `Dispose` (every ring).
  - Cut: under the lock, pick `cur` (the newest generation whose `StartUs < toUs`) and its live predecessor. If the predecessor exists and `toUs - cur.StartUs < MinClipUs` (1 s): try `prev.Ring.Cut(fromUs, cur.StartUs)` and return it when it holds at least 1 s (`EndUs - StartUs`). Otherwise return null if there is a predecessor, else `cur.Ring.Cut(max(fromUs, cur.StartUs), toUs)`. Mark the chosen generation's `Users++` under the lock and call `Ring.Cut` outside it. Then decrement `Users`, and dispose the ring if it is retired and now unused.
  - Retire: a restart's predecessor goes once the current generation's newest packet is at or past `StartUs + MinClipUs + RetireSlackUs` (5 s, which covers a waiting writer's `SaveWait` twice). Check it in `Append`. A resize retires the old generation at once. Opening a generation retires every one before the predecessor.
- `src/ReplayBuffer.Platform.FFmpeg/ReplayRingSink.cs`:
  - Keep the constructor (`PacketRing`) and the public API, and wrap a `RingGenerations`.
  - Streams per generation: `Dictionary<int, List<ClipStream>>`. `AddStream` goes to the current generation's list. A resize shares the old generation's list; a restart starts an empty one.
  - Add `int Generation`, `Restart(PacketRing)` (documented: the old chain must have stopped writing; the new chain's encoders then call `AddStream`) and `Resize(PacketRing, long fromUs)`.
  - `Cut` copies the chosen generation's parameters. `Release` frees each distinct list once.
- `src/ReplayBuffer.Core/Sessions/CaptureSessionPorts.cs` -- `ICaptureSession.SetBufferWindow(int seconds) { }` (a default member): "the Buffer Window from the next Reset on; orchestrator thread; never blocks".
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs:784-791` -- `OnClipWritten`: for `_active` only (not the drain), before `written.Write.Reset()`, call `active.Session.SetBufferWindow(SettingsResolver.Resolve(_currentDocument(), active.Spec.Target.ProfileId).BufferWindow)`.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs`:
  - Extract `NewRing(int seconds)` from `StartOutput:572-579` (the same capacity formula).
  - Fields `_windowSeconds` (set at the anchor) and `_pendingWindow`.
  - `SetBufferWindow` writes `_pendingWindow` (volatile).
  - `Save` reads `_windowSeconds` into the `ClipWrite`, which uses it in place of `_spec.Snapshot.BufferWindow` at line 1080.
  - `ClipWrite.Reset()`: after `ResetTo(end)`, `session.ResizeAt(end)`. That does nothing when it is drained (`_keepRing` or `_outputFinished`), has no ring, or the window is unchanged. Otherwise it calls `_ring.Resize(NewRing(pending), end)`, updates `_windowSeconds`, and logs the old and new windows and the generation at Information.
  - `Run`: `_endUs` is `source.Cut.EndUs`, the boundary when the previous generation was cut. A null cut's message also covers "less than 1 s since a ring generation began".
  - Update the `Save` and class docs.
- `src/ReplayBuffer.Engine/Sessions/ClipClaim.cs` -- `Succeeded(long claimedUs, long endUs)`: drop the claim and record `endUs`. This is the only caller. Then a Clip that ended at a boundary leaves the next start there.
- Tests:
  - `tests/ReplayBuffer.Core.Tests/PacketRingTests.cs` has the `Streams`, `Feed` and `Pattern` helpers. Copy what the new tests need into the new file, or make them `internal static`.
  - `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` has `FakeSession` (line 1034; add a recorded `SetBufferWindow`), `FakeWrite`, `NextAsync` and `_document`.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Core/Replay/PacketRing.cs` -- `WindowUs`, `FirstKeyUs`, `CopyTailTo` -- the resize seed
- [x] `src/ReplayBuffer.Core/Replay/RingGenerations.cs` -- as in the Code Map -- R5 generations and AD-8 resizing
- [x] `src/ReplayBuffer.Platform.FFmpeg/ReplayRingSink.cs` -- wrap `RingGenerations`, per-generation streams, `Restart`, `Resize`, `Generation`
- [x] `src/ReplayBuffer.Core/Sessions/{CaptureSessionPorts,SessionOrchestrator}.cs` -- `SetBufferWindow`, called at each Reset of the active session
- [x] `src/ReplayBuffer.Engine/Sessions/{CaptureSession,ClipClaim}.cs` -- the window in effect per press, the resize at the Reset, the end at the cut's end
- [x] New `tests/ReplayBuffer.Core.Tests/RingGenerationsTests.cs` -- three tests, synthetic 30 fps video with a 1 s GOP and AAC:
  - A restart at 10 s (ring 2's first key at 10 s): `Cut(0, 14 s)` starts at 10 s; `Cut(0, 10.5 s)` comes from generation 1, ends at 10 s and holds at least 1 s. A restart after only 0.6 s of generation 1, with a press 0.5 s after it, gives null.
  - A 30 s window fed 40 s: the cut from `press - 30 s` lasts no more than 30 s. `Keys.BufferWindow` validates 7200. A `RingGenerations` resized to a 7200 s ring takes packets and cuts.
  - Shrink while writing: a 60 s ring fed 20 s; cut A is held. `Resize(30 s ring, A.EndUs)`; feed 5 s more. A's bytes still match `Pattern`. The old ring's `LiveSlabs` is above 0 until A is disposed, then 0. A cut from `A.EndUs` in the new generation starts exactly at `A.EndUs`.
- [x] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- one test: with `saveCooldown` 0, set `bufferWindow` to 30 in `_document` after press 1; when press 1's write succeeds, the fake session records `SetBufferWindow(30)` before its write's `Reset`.

**Acceptance Criteria:**
- Given a simulated encoder restart mid-capture, when `save` is pressed more than 1 s after it, then the Clip starts at the restart's first keyframe.
- Given a press 0.5 s after a restart, when the previous generation holds at least 1 s, then the Clip is the previous generation up to the restart, and the next Clip starts at the restart.
- Given a 30 s Buffer Window, when a Clip is saved, then it holds no more than 30 s; and a 2 h Buffer Window is accepted.
- Given a Clip still writing, when the Buffer Window shrinks and the Reset resizes the ring, then the Clip's bytes stay whole and the old ring is freed after the Clip finishes.
- Given the default run, when the 0-warning Release build, the Core, Platform.FFmpeg and Engine suites and the NativeAOT publish run, then all pass.

## Implementation Notes

- `PacketRing.Cut`'s keyframe search and audio look-back moved into private `KeyAtOrAfter` and `LookBack`, shared with `CopyTailTo`; `Cut`'s behaviour is unchanged.
- A resize's generation takes its ring's `FirstKeyUs` (the Reset keyframe) as `StartUs` at once; a restart's starts at `long.MaxValue` and is set on its first keyframe in `Append`.
- `ReplayRingSink.Cut` now runs `RingGenerations.Cut` outside the sink's lock and takes the lock only to copy the chosen generation's codec parameters, so a long cut never blocks `Resize` on the orchestrator thread. Parameters of retired generations are freed at `Release`, not at retirement.
- `CaptureSession` gained `RingCapacity(seconds)` beside `NewRing(seconds)`, because the ring's capacity is internal to Core and the anchor's log line prints it.
- `ClipClaimTests` now calls `Succeeded(30, 30)`.

## Plan Change Log

- Review: the resize's tail runs from the Reset keyframe to the newest packet, which spans the whole write of a long Clip, not "a few seconds". `PacketRing.CopyTailTo` now copies it in batches of 256 entries (tracked by sequence number) under the ring locks only, and `RingGenerations.Resize` takes its lock just to copy the remainder (`CopyFrom`) and swap.
- Review: `ReplayRingSink.Cut` disposes the cut and throws `ObjectDisposedException` when the sink was released between the generations cut and its lock. `Restart` documents that no save may be waiting for its keyframe or streams.
- Review: a press's Buffer Window is measured back from the Clip's end (the key), not the press, so a Clip never exceeds it.

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 7 findings — high 0, medium 3, low 4, false 0, maybe-false 0
- findings:
  - `[medium]` `[defer]` A press 0.5 s after a restart falls back to the previous generation and ends at the boundary. A second write already in flight with it has claimed the press's key, though, so `[boundary, key)` lands in neither Clip. This takes a restart plus two writes in flight within about 1 s, which only `saveCooldown` 0 allows, and no engine path calls `Restart` yet. The fix (deciding the fallback before the claim is published) belongs with 4.9's restart path. Recorded in `deferred`.
  - `[medium]` `[patch]` The resize's tail copy ran from the Reset keyframe to the newest packet, which spans the whole write, while holding the generations lock and blocking every encoder `Append`. Fixed: `CopyTailTo` copies in batches of 256 under the ring locks only, and `Resize` takes the generations lock just for the remainder (`CopyFrom`) and the swap.
  - `[low]` `[patch]` `ReplayRingSink.Cut` checked disposal outside its lock and read `_streams` outside the cut's dispose path, so a `Release` racing an unleased cut leaked the pinned cut. Fixed: inside the lock, a released sink or missing streams dispose the cut and throw `ObjectDisposedException`.
  - `[medium]` `[patch]` + `[defer]` `VideoMicros` and `AllStreamsPast` read the current generation, so a save waiting across a `Restart` would convert the old encoder's PTS with the new generation (whose video stream is -1 until it adds streams). `Restart` has no caller yet. Patched the docs with the precondition that no save may be waiting when `Restart` is called; the real per-generation handling is deferred to 4.9.
  - `[low]` `[patch]` The window was measured back from the press, while the Clip ends at the forced key after it, so a keyframe at exactly `press - window` gave a Clip up to a frame longer than the window. Fixed: `ownFromUs = max(toUs - window, Reset)`.
  - `[low]` `[reject]` The 2 h sizing isn't tested through `CaptureSession.RingCapacity`. That is plain arithmetic with a cap. The test covers the key accepting 7200 and a 7200 s ring taking packets and cutting; anything more would be a duplicate check (lean tests).
  - `[low]` `[reject]` Before a restart generation's first video key, every AAC packet (all flagged key) re-reads `FirstKeyUs`, and the retire check re-enters the ring lock through `ToMicros`. Both are uncontended re-entries with no allocation, only while a restart is fresh (and no caller exists yet). The result is correct, and the fix adds per-generation stream tracking.

## Design Notes

**Why a resize copies instead of switching at a boundary.** A resize must stay gapless, and a failed save must still retry over the same content. So the switch happens only on a successful Reset. By then the encoders have appended a second or two past the Reset keyframe. Copying that tail into the new ring keeps one continuous timeline, and the old generation is never cut again, so it retires at once.

**Why a restart is a real boundary.** A new encoder can have new extradata and new streams. A Clip that spans two generations would need two sets of sample entries, so a Clip holds one. A boundary counts as a Reset, which gives the 1 s rule and the previous-generation fallback.

**Why the generation, not the ring, is pinned.** `Users` keeps a ring alive while a cut is reading its entries outside the lock. Its slabs then outlive it through the cut's own pins, exactly as today.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Platform.FFmpeg.Tests -c Release --no-build` -- expected: all pass (the sink changed)
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- expected: all pass (the session changed)
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

## Auto Run Result

- **Summary:** R5's ring generations and AD-8's Buffer Window resize.
  - The Replay Buffer is now `RingGenerations` (Core), wrapped by `ReplayRingSink`.
  - **Restart.** A `Restart` opens an empty generation whose first keyframe is a boundary that counts as a Reset. A Clip holds one generation, and a cut ending less than 1 s past a boundary takes the previous generation up to the boundary when that holds at least 1 s.
  - **Resize.** The orchestrator passes the Effective `bufferWindow` at each Reset of the running session (`ICaptureSession.SetBufferWindow`). When it changed, the session opens a generation on a new ring of that size, seeded from the Reset keyframe in bounded batches, and retires the old ring. A writing Clip's pinned slabs outlive it.
  - Each press uses the window in effect then, measured back from the Clip's end.
- **Files:**
  - Core: `Replay/PacketRing.cs` (`WindowUs`, `FirstKeyUs`, the batched `CopyTailTo` and `CopyFrom`); new `Replay/RingGenerations.cs`; `Sessions/CaptureSessionPorts.cs` (`SetBufferWindow`); `Sessions/SessionOrchestrator.cs` (the window at the Reset).
  - Platform.FFmpeg: `ReplayRingSink.cs` (generations, per-generation streams, `Restart`, `Resize`, `Generation`).
  - Engine: `Sessions/CaptureSession.cs` (`NewRing`, `ResizeAt`, the window per press, the end at the cut's end); `Sessions/ClipClaim.cs` (`Succeeded(claimed, end)`).
  - Tests: new `RingGenerationsTests` (3); an orchestrator test for `SetBufferWindow` before `Reset`; `ClipClaimTests` call updated.
- **Review:** quick lens, 7 findings.
  - Patched 4: 2 medium (the batched tail copy; the `Restart` precondition docs) and 2 low (the `Cut` dispose race; the window measured back from the Clip's end).
  - Deferred 2: the fallback claim gap and per-generation `VideoMicros`, both for 4.9's restart path.
  - Rejected 2 low (reasons in the triage log).
- **Follow-up review recommended:** false. Two mediums were patched, which would make it true by count, but one of them is docs only and the other is a local internal change covered by the re-run ring and sink tests. No unverified risk can be named.
- **Verification (after the patches):**
  - `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings, 0 errors.
  - Core 76/76. Platform.FFmpeg 32 passed, 1 skipped.
  - Engine 150 passed, 18 Explicit skipped, and 1 failed on the first run. Three reruns passed 151 each. The failing test wasn't captured; the same one-off failure appeared once in the implementer's runs, and other sessions were testing in parallel.
  - `tools/publish-engine.ps1`: clean (10.6 MB).
  - Heavy not run: the change touches ring bookkeeping, not capture, encode, mux or audio throughput.
- **Residual risks:**
  - No engine path calls `Restart` yet. It is proven with a simulated restart at the ring level, and 4.9 or device-lost recovery must honour its precondition.
  - The session's resize wiring (`ResizeAt` at the Reset) is covered by the orchestrator test and the ring tests, not by an in-process capture.
  - The intermittent Engine failure above.
