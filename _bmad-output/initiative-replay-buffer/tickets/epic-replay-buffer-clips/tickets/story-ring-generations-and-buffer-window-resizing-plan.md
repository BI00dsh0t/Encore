---
title: 'Ring generations and Buffer Window resizing'
type: 'feature'
ticket: '6'
created: '2026-11-03'
status: 'ready-for-dev'
baseline_revision: 'd6b57b6ef463e170bf92f9b0f96ea25e148f419d'
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
- [ ] `src/ReplayBuffer.Core/Replay/PacketRing.cs` -- `WindowUs`, `FirstKeyUs`, `CopyTailTo` -- the resize seed
- [ ] `src/ReplayBuffer.Core/Replay/RingGenerations.cs` -- as in the Code Map -- R5 generations and AD-8 resizing
- [ ] `src/ReplayBuffer.Platform.FFmpeg/ReplayRingSink.cs` -- wrap `RingGenerations`, per-generation streams, `Restart`, `Resize`, `Generation`
- [ ] `src/ReplayBuffer.Core/Sessions/{CaptureSessionPorts,SessionOrchestrator}.cs` -- `SetBufferWindow`, called at each Reset of the active session
- [ ] `src/ReplayBuffer.Engine/Sessions/{CaptureSession,ClipClaim}.cs` -- the window in effect per press, the resize at the Reset, the end at the cut's end
- [ ] New `tests/ReplayBuffer.Core.Tests/RingGenerationsTests.cs` -- three tests, synthetic 30 fps video with a 1 s GOP and AAC:
  - A restart at 10 s (ring 2's first key at 10 s): `Cut(0, 14 s)` starts at 10 s; `Cut(0, 10.5 s)` comes from generation 1, ends at 10 s and holds at least 1 s. A restart after only 0.6 s of generation 1, with a press 0.5 s after it, gives null.
  - A 30 s window fed 40 s: the cut from `press - 30 s` lasts no more than 30 s. `Keys.BufferWindow` validates 7200. A `RingGenerations` resized to a 7200 s ring takes packets and cuts.
  - Shrink while writing: a 60 s ring fed 20 s; cut A is held. `Resize(30 s ring, A.EndUs)`; feed 5 s more. A's bytes still match `Pattern`. The old ring's `LiveSlabs` is above 0 until A is disposed, then 0. A cut from `A.EndUs` in the new generation starts exactly at `A.EndUs`.
- [ ] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- one test: with `saveCooldown` 0, set `bufferWindow` to 30 in `_document` after press 1; when press 1's write succeeds, the fake session records `SetBufferWindow(30)` before its write's `Reset`.

**Acceptance Criteria:**
- Given a simulated encoder restart mid-capture, when `save` is pressed more than 1 s after it, then the Clip starts at the restart's first keyframe.
- Given a press 0.5 s after a restart, when the previous generation holds at least 1 s, then the Clip is the previous generation up to the restart, and the next Clip starts at the restart.
- Given a 30 s Buffer Window, when a Clip is saved, then it holds no more than 30 s; and a 2 h Buffer Window is accepted.
- Given a Clip still writing, when the Buffer Window shrinks and the Reset resizes the ring, then the Clip's bytes stay whole and the old ring is freed after the Clip finishes.
- Given the default run, when the 0-warning Release build, the Core, Platform.FFmpeg and Engine suites and the NativeAOT publish run, then all pass.

## Implementation Notes

## Plan Change Log

## Review Triage Log

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
