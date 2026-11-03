---
title: 'Exact save cut and gapless Reset'
type: 'feature'
ticket: '2'
created: '2026-11-03'
status: done
baseline_revision: 'cc6c2d180eb8ff3bac681ef97cf4626d70b9acf3'
route: 'full'
route_source: 'auto'
risk: 'high'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: ['oversized']
deferred:
  - summary: >-
      A save press while an earlier save is still writing gets past the 1 s refusal and its Clip runs back over the earlier one.
    evidence: |-
      The orchestrator sets ResetQpc, and the session sets _resetUs, only when a write succeeds (OnClipWritten / ClipWrite.Reset), so a press during the write cuts from the older Reset or the window start. Concurrent writers, the Save Cooldown and save-busy are story 4.4's; its cooldown (5 s by default) makes this rare, but a press after the cooldown while a slow write is in flight still needs a rule there (e.g. start at the pending write's key).
    location: >-
      src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs (SaveClip, OnClipWritten)
    severity: medium
---

<intent-contract>

## Intent

**Problem:** 4.1's Clip ends with whatever the ring holds about 1 s after the press, every Clip runs back the whole Buffer Window (so consecutive Clips overlap by seconds), nothing forces a keyframe at a save, a press with too little buffered fails with a bare exception, and each Track opens with ~50 ms of fill because the session's device taps drop the hub's post-anchor blocks until Publish.

**Approach:** AD-10 and the R5 Reset. A save asks the encoder for a keyframe on the next frame it encodes. That forced keyframe is the Clip's end and, once the save succeeds and the orchestrator Resets, the next Clip's start, so Clips meet with no gap. Every Track is cut at the AAC frame boundary nearest that same instant, so consecutive Clips share it. The orchestrator owns the Reset (AD-19): it remembers the last Reset's press QPC and refuses a press less than 1 s after it with `clip.failed { reason: clip-too-short }`, with no keyframe and no Reset. The ring gets a keyframe index so the cut's lock no longer scans every entry twice (4.1's QA note). The session's taps hold pre-Publish blocks and replay them at Publish.

## Boundaries & Constraints

**Always:**
- AD-19: only the orchestrator decides Resets and publishes `clip.saved`/`clip.failed`. Writer threads post back to its channel.
- AD-8: the cut pins its slabs and never blocks the ring for long. The lock in `Cut` covers only finding K through the keyframe index, copying one contiguous entry range (`Array.Copy`) and pinning the slabs that range touches. Filtering, sorting and projecting run after it.
- No managed allocation per packet, per frame or per audio block (the keyframe index and the tap's hold buffer are preallocated). Allocating per save is fine.
- The GOP stays at most 1 s (the existing once-a-second IDR), and a forced keyframe restarts that count.
- Capture never stops at a Reset. Contracts change additively only (AD-22): `clip.failed`, `ErrorCodes.ClipTooShort`, a `lastClipFailure` snapshot field.
- `serve --debug-capture`, `debug-capture` and the meter taps (`TrackMeters`) behave as before.
- Lean tests per `CLAUDE.md` "Tests". Anything that injects input or needs focus is `[Fact(Explicit = true)]` + `[Trait("Category","Heavy")]`.

**Never:**
- Save Cooldown, `clip.saving`, two concurrent writers or `save-busy` (4.4). Writer failures as `clip.failed`, retry semantics beyond "no Reset", `Library.BeginWrite/EndWrite` (4.5). Ring generations and resizing (4.6). Disk mode (4.7). The exit drain (4.3). App UI.
- Changing the pacer (`FramePacer`, `PacedVideoEncoder`) or the 4.1 file layout of a Clip (faststart, titles, Track 1 default).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| First save | Session buffering > 1 s, no Reset yet | Clip from the first keyframe at or after `press − bufferWindow` up to the forced keyframe after the press (excluded); `clip.saved`; the orchestrator Resets | No error expected |
| Second save 10 s later | A Reset at the first press | Clip from that Reset's keyframe to the new one: ~10 s, no frame missing or repeated between the two Clips | No error expected |
| Press soon after a Reset | Press < 1 s after the last Reset's press | No Clip, no keyframe, no Reset; `clip.failed { reason: clip-too-short }` | Logged at Information |
| Press soon after start | Press < 1 s after the session's anchor | Same as above | Same |
| Failed write | The writer throws | No Reset: the next Clip still starts at the previous Reset | Logged at Warning, as 4.1 (4.5 adds `clip.failed`) |
| Pre-anchor audio | Hub blocks arrive between the anchor and Publish | They are replayed at Publish: each Track starts with real audio, not fill | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Replay/PacketRing.cs` -- `Append`, `Cut(startUs, endUs)`, `Trim`, `Grow`, `Slab`, `RingCut`. Entries are a circular struct array in append order (video and audio interleaved, audio lagging by up to ~100 ms). `ToMicros(ticks, StreamInfo)` floors. 4.1's `Cut` scans all entries twice under the lock: replace with a keyframe index (see Design Notes).
- `src/ReplayBuffer.Platform.FFmpeg/ReplayRingSink.cs` -- `Cut(startUs, endUs)` wraps `PacketRing.Cut` and copies stream parameters; `AllStreamsPast(us)`; `Lease`/`Release`. Keep the leasing. Add a µs conversion for the video stream's PTS that uses the ring's own formula, so the session's `toUs` equals the keyframe entry's `TimeUs` exactly.
- `src/ReplayBuffer.Platform.FFmpeg/VideoEncoder.cs` -- `Encode` (lines ~386–417) already forces an IDR when `pts − _lastKeyPts ≥ TimeBase`; `forced-idr`/`forced_idr` options are set per family. Add the keyframe request here; the paced path (`SubmitSlot`) and `Submit` both go through `Encode`.
- `src/ReplayBuffer.Platform.FFmpeg/ClipWriter.cs` -- unchanged; rebases each stream to `cut.Origin(stream)`. An audio packet starting just before K gets an edit list.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- `Save(qpc)`, `ClipWrite.Run` (waits with `SaveWait`, `AllStreamsPast`, `_outputFinished`), `MicrosAt`, `StartOutput` (creates `_video` and `_ring` under `_gate`, then `Volatile.Write(ref _anchored)`), `Subscribe`, `SessionAudioTrack`.
- `src/ReplayBuffer.Engine/Audio/AudioTimelineTap.cs` -- drops blocks while `_timeline` is null. Shared with `TrackMeters.cs:131` (no hold there). `AudioBlock` (`src/ReplayBuffer.Core/Audio/AudioDeviceHub.cs:18`) is a ref struct: `StartQpc`, `Frames`, `Interleaved`, `IsSilent`; stereo float at 48 kHz.
- `src/ReplayBuffer.Core/Sessions/CaptureSessionPorts.cs` -- `ICaptureSession.Save` (default null), `IClipWrite.Write`. Test fakes implement `IClipWrite` (`FakeWrite`), so new members must be default interface members.
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- `SaveClip(qpc)` (lines ~522–560), `OnClipWritten`, private `ClipWritten(Path, Error)`, `Active` (per-session state lives here). Hotkey QPC is `Stopwatch` ticks.
- `src/ReplayBuffer.Core/Sessions/TargetGames.cs:45` -- `ReplayClips(Paths, DefaultOutputFolder, Saved, Failed)`: add a last optional `Action<ClipFailed>? NotSaved = null`, so existing callers compile.
- `src/ReplayBuffer.Contracts/ErrorCodes.cs`, `Messages/Capture.cs` (`ClipSaved`, `CaptureMessages.Saved` is the pattern), `Messages/Engine.cs` (`LastClip` is the pattern for `LastClipFailure`).
- `src/ReplayBuffer.Engine/EngineHost.cs:248` -- composes `ReplayClips`: `NotSaved` publishes `CaptureMessages.Failed` and logs Information.
- Tests: `tests/ReplayBuffer.Core.Tests/PacketRingTests.cs` (`Streams`, `Feed` helpers), `SessionOrchestratorTests.cs` (`FakeFactory { Save = … }`, `FakeWrite`, `NextAsync`), `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` (`Samples` needs every event), `tests/ReplayBuffer.Platform.FFmpeg.Tests/SyntheticEncodeTests.cs` (the pattern for a synthetic capture with known QPCs: `D3D11Device.Create(0, …)`, `TryAcquire(qpc)`/`Submit`, `AudioTimeline.Push`, ffprobe helpers), `tests/ReplayBuffer.Engine.Tests/ReplayClipCaptureTests.cs` (in-process session, `SessionTestKit.Probe`).

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Core/Replay/PacketRing.cs` -- keyframe index; `Cut(fromUs, toUs)` with the half-open semantics and the audio-centre rule; the tightened lock; a public µs conversion for a stream's ticks -- AD-8, AD-10, 4.1 QA note
- [x] `src/ReplayBuffer.Platform.FFmpeg/{VideoEncoder,ReplayRingSink}.cs` -- `long RequestKeyframe()` (a ticket; safe from any thread, never blocks on an encode) and `bool TryGetForcedKey(long ticket, out long pts)`; the next frame `Encode` takes after a request is an IDR. The sink's `Cut` and docs follow the ring -- AD-10 keyframe
- [x] `src/ReplayBuffer.Contracts/{ErrorCodes,Messages/Capture,Messages/Engine}.cs` -- `ClipTooShort = "clip-too-short"`; `ClipFailed(string Reason)`; `CaptureMessages.Failed` ("clip.failed", field `lastClipFailure`) in `Events` and the JSON context; `StateSnapshot.LastClipFailure` -- additive
- [x] `src/ReplayBuffer.Core/Sessions/{CaptureSessionPorts,TargetGames,SessionOrchestrator}.cs` -- `IClipWrite.Reset() { }` (default member: "make this Clip's end the Replay Buffer's Reset"); `ReplayClips.NotSaved`; `Active.ResetQpc`; `SaveClip` refuses `qpc − ResetQpc < Stopwatch.Frequency` before calling the session; any `ErrorCodeException { Code: ClipTooShort }` from `Save` or the write goes to `NotSaved(new ClipFailed(code))`, others to `Failed` as before; `ClipWritten` carries the session id, the press QPC and the write, and on success for the still-active session calls `write.Reset()` and sets `ResetQpc` -- R4, R5 under AD-19
- [x] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- `Save(qpc)`: throws `ErrorCodeException(ClipTooShort)` when the press is < 1 s after the anchor, else requests the keyframe and returns the write. `ClipWrite.Run`: wait (within `SaveWait`, or until the output finished) for the forced key, else `toUs = pressUs`; then for every stream to pass `toUs` plus one AAC frame; cut `[max(pressUs − bufferWindow, session reset), toUs)`; a null cut throws `ClipTooShort`; log the Clip's span. `Reset()` moves the session's reset point (a volatile long, forward only) to this write's `toUs` -- AD-10, R5
- [x] `src/ReplayBuffer.Engine/Audio/AudioTimelineTap.cs`, `Sessions/SessionAudioTrack.cs` -- an optional preallocated hold (`AudioTimelineTap(int holdFrames = 0)`): before Publish, keep the newest blocks up to that many frames (oldest dropped); `Publish` replays them in order, then live blocks follow, with no block lost or reordered between the two (one per-tap lock, uncontended except during Publish). `SessionAudioTrack` holds 1 s; meters hold none -- deferred item from epic 1
- [x] `src/ReplayBuffer.Engine/EngineHost.cs` -- `NotSaved` publishes `clip.failed` and logs Information
- [x] `tests/ReplayBuffer.Core.Tests/PacketRingTests.cs` -- update test 1 to the new semantics; add the cut-boundary test: synthetic 30 fps video (1 s GOP plus a forced key just after each press) and AAC packets; two cuts 10 s apart sharing the first press's keyframe give exactly every video packet once and every audio packet once, the second runs ~10 s, and each cut's audio span is within one AAC frame of its video span
- [x] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- a save, then a press 0.5 s (QPC) later: one `clip.failed { clip-too-short }`, the fake session's `Save` called once, `Reset` once; then a press 1.5 s after the first saves again; a write that fails calls no `Reset`
- [x] `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` -- the `clip.failed` sample
- [x] `tests/ReplayBuffer.Platform.FFmpeg.Tests/ReplayCutTests.cs` -- the synthetic capture: 640×360 at 30 fps for 21 s of virtual QPC into a `ReplayRingSink` (video plus one AAC Track), `RequestKeyframe` just before the first frame after presses at 10.02 s and 20.02 s, two cuts written with `ClipWriter`; ffprobe shows Clip 1 holds exactly the frames captured at or before press 1, Clip 2 the frames after it up to press 2 (~10 s), and each audio stream lasts within one AAC frame of its video
- [x] `tests/ReplayBuffer.Engine.Tests/AudioTimelineTapTests.cs` -- blocks from before and after the anchor arrive before Publish; after Publish the timeline holds the post-anchor frames with no gap fill, and a later block follows contiguously
- [x] `tests/ReplayBuffer.Engine.Tests/ReplayClipCaptureTests.cs` -- a press right after the anchor reports `clip-too-short` through `NotSaved`; after the first Clip, wait ~2 s and press again: the second Clip's video lasts the time between the presses within 0.1 s

**Acceptance Criteria:**
- Given the synthetic capture with known frame QPCs, when saves are pressed 10 s apart, then the second Clip lasts 10 s within one frame, the two Clips overlap by at most one frame with none missing, each ends with the last frame captured at or before its press, and every Track's length matches its video within one AAC frame (R4, R5).
- Given a Reset, when `save` is pressed 0.5 s later, then no Clip is made, `clip.failed { reason: "clip-too-short" }` is published, and the next valid press's Clip still starts at that Reset (R4).
- Given an in-process Replay session, when `save` is pressed twice ~2 s apart, then both Clips are written and the second covers the time between the presses (R5, Capture never stops).
- Given the default run, when the 0-warning Release build, the Core, Contracts, Platform.FFmpeg and Engine default suites and the NativeAOT publish run, then all pass.

## Implementation Notes

- The keyframe index is a `long[]` as long as the entry array, so it can never fill on its own and grows with `Grow`; `PacketRing.ToMicros(stream, ticks)` and `VideoStream` back the sink's `VideoMicros(pts)`.
- `TryGetForcedKey` keeps only the latest served `(ticket, pts)`, as the design note says. `ReplayCutTests` therefore reads each press's key right after the frame it lands on, as a writer polling every 5 ms would; reading both after the whole capture returns the second key for both tickets.
- Where a Clip ends (amends the Design Note): a request made when the press reaches the encoder can still be served by a frame captured before the press, which sits in `PacedVideoEncoder`'s hand-off or is held for the next slot, so Clip 1 would end a frame or more early. `RequestKeyframe(long afterQpc)` takes the press QPC. `Encode` serves outstanding requests only on a frame whose capture QPC is after it; a repeat, which passes 0, counts at its slot's QPC (`CaptureQpcOf(pts)`). Requests outstanding together take the latest QPC. The forced key is therefore the first frame captured after the press, and the Clip ends with the last frame captured at or before it, as AD-10 asks. `ClipWrite.Write` makes the request once the lease is taken, just before the writer thread starts, so a save whose path allocation fails asks for no keyframe. `ReplayCutTests` requests each key one frame early with the press QPC and checks it still lands on frames 301 and 601.
- `ClipWrite.Run` waits up to `SaveWait` for the key, then up to `SaveWait` again for every stream to pass `toUs` plus one AAC frame (21 334 µs).
- `AudioTimelineTap` holds blocks in a preallocated sample buffer twice the hold (compacted in place when its end is reached) and a 1024-entry block ring; a hold of 0 (meters) keeps the old lock-free path.
- Verified: 0-warning Release build; Core 65/65, Contracts 97/97, Platform.FFmpeg 31 passed + 1 explicit skipped, Engine 149 passed + 18 explicit skipped; `tools/publish-engine.ps1` clean. Heavy: Platform.FFmpeg 1/1; Engine 17/18, the failure `HotkeyServeTests.AnInjectedPressArrivesPromptlyAndIsLogged` (an injected SendInput press never arrives in this unattended session; hotkey code untouched).

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 5 findings — high 0, medium 2, low 3, false 0, maybe-false 0
- findings:
  - `[medium]` `[defer]` A second press while the first save is still writing passes the 1 s refusal, because `ResetQpc` and the session's `_resetUs` move only once the write succeeds, so its Clip runs back over the first. This is the concurrent-writer case, which the epic gives to story 4.4 (cooldown, `save-busy`, two writers). Before 4.2 every Clip ran back the whole window, so 4.2 doesn't make it worse. Recorded in `deferred` for 4.4.
  - `[medium]` `[patch]` In the paced path, a frame captured before the press but still in `PacedVideoEncoder`'s hand-off, or held for the next slot, could take the forced key, so Clip 1 would end a frame or more early (more under GPU load). Fixed: `RequestKeyframe(afterQpc)` takes the press QPC, and `Encode` serves a request only on a frame captured after it (a repeat counts at its slot's QPC). `ReplayCutTests` now requests each key one frame early and checks it still lands on frames 301 and 601.
  - `[low]` `[patch]` `PacketRing.Cut`'s look-back comment had the audio lag backwards. Fixed the comment and the `LookBackUs` doc.
  - `[low]` `[reject]` Some AC sentences are tested only indirectly: "the next Clip still starts at that Reset" via the fake's Reset count, and the session's cut start via `ReplayClipCaptureTests`' second-Clip length. Rejected under the lean-tests rule. The core path is covered: the orchestrator test checks that a refused press or a failed write calls no `Reset`, and the in-process test's second Clip is only as long as the gap between presses if it started at the first Clip's key.
  - `[low]` `[patch]` `Save` requested the keyframe before the orchestrator allocated the path, so a failed allocation still forced an IDR. Fixed: `ClipWrite.Write` makes the request once the lease is taken.

## Design Notes

**Where a Clip ends.** AD-10 asks for the last frame with capture QPC ≤ the press and a keyframe on the next frame. The pacer stamps each frame with its slot, not its capture QPC, and a frame captured just before the press can take a slot just after it. So the Clip ends at the forced keyframe: the first frame encoded after the press reached the encoder. Everything before it is in the Clip, and it starts the next one. This is the last frame captured at or before the press, since a later frame still has to be captured, drawn and posted, which takes longer than the press's trip to the encoder. Video is `[K, toUs)`, where `toUs` is the forced key's `TimeUs`. If the key comes late the Clips still meet: the next one starts at the same key.

**Audio boundary.** An audio packet belongs to a cut when its centre (`TimeUs + duration/2`) lies in `[K, toUs)`. Each end is then the AAC boundary nearest the video's end, within half an AAC frame. Two cuts that share a boundary split the packets exactly, with none missing and none in both.

**Reset ownership.** The orchestrator decides. It keeps `ResetQpc` per session and Resets only on a successful write, so a failed save leaves the next Clip starting at the old Reset. The session keeps the ring-time side: `Reset()` on the write sets the session's `resetUs` to that write's `toUs`. The next cut starts at `max(pressUs − window, resetUs)`, and K is the first keyframe at or after that: the previous Clip's end key.

**Keyframe index.** A preallocated circular `long[]` of the absolute sequence numbers of video keyframe entries. It is trimmed with the head and doubles with `Grow`. Each entry's sequence is `_headSeq + i`. `Cut`, under the lock: K is the first indexed key with `TimeUs ≥ fromUs` (none, or `≥ toUs`: null). It steps back from K's position while entries are newer than `K − 1 s` (audio packets that overlap K were appended earlier). It `Array.Copy`s that position through the tail (two segments at most). It pins each slab whose entry-sequence range (`FirstSeq`/`LastSeq`, kept on append) meets the copied range, by walking `_held` (O(slabs)). Then it reads the origins. After the lock: filter by the rules above, stable sort by `TimeUs`, project.

**Encoder stall.** If the forced key isn't encoded within `SaveWait`, the cut ends at the press (`toUs = pressUs`). The next Clip still starts at the first keyframe at or after it, the forced key once encoded.

**Forced key ticket.** `RequestKeyframe` increments a request counter. `Encode`, under its lock, serves every outstanding request with this frame and records `(servedTicket, pts)`. `TryGetForcedKey(t)` answers once `servedTicket ≥ t`. Requests that land before one frame share its key. Saves are seconds apart, so the recorded pts can't be overwritten before the writer reads it (4.4's cooldown makes that a rule).

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Platform.FFmpeg.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- expected: all pass (known flakes rerun once)
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

**Manual checks (if no CLI):**
- Builder (Human Check Queue): in a game, press Save twice about 10 s apart and play the two Clips back to back in VLC. The second should start exactly where the first ended, with each Track in sync.

## Auto Run Result

- **Summary:** AD-10's exact cut and R5's gapless Reset. A save asks the encoder for an IDR on the first frame captured after the press. That keyframe is the Clip's end, and, once the write succeeds and the orchestrator Resets, the next Clip's start. Every Track is cut at the AAC boundary nearest the same instant, so consecutive Clips share it. The orchestrator owns the Reset. A press less than 1 s after the last Reset, or after the session's start, makes no Clip and publishes `clip.failed { reason: clip-too-short }`, with no keyframe and no Reset. A failed write Resets nothing. The ring's cut finds its keyframe through a keyframe index, and its lock now covers only that lookup, one contiguous copy and the slab pins (4.1's QA note). The session's device taps hold the hub's blocks until Publish and replay them, so Tracks no longer open with ~50 ms of fill (epic 1's deferred item).
- **Files:**
  - Contracts: `ErrorCodes.cs` (`clip-too-short`), `Messages/Capture.cs` (`ClipFailed`, `clip.failed`), `Messages/Engine.cs` (`lastClipFailure`).
  - Core: `Replay/PacketRing.cs` (the keyframe index, `Cut(fromUs, toUs)`, the tighter lock); `Sessions/CaptureSessionPorts.cs` (`IClipWrite.Reset`); `Sessions/SessionOrchestrator.cs` (`ResetQpc`, the refusal, Reset on success); `Sessions/TargetGames.cs` (`ReplayClips.NotSaved`).
  - Platform.FFmpeg: `VideoEncoder.cs` (`RequestKeyframe(afterQpc)`, `TryGetForcedKey`); `ReplayRingSink.cs` (`VideoMicros`, the new cut).
  - Engine: `Sessions/CaptureSession.cs` (the too-short check, the wait for the key, the cut from the Reset, `Reset`); `Audio/AudioTimelineTap.cs` and `Sessions/SessionAudioTrack.cs` (the pre-Publish hold); `EngineHost.cs` (publishes `clip.failed`).
  - Tests: `PacketRingTests` (the cut boundary), `SessionOrchestratorTests` (refusal and Reset), `MessagesTests`, new `ReplayCutTests` (a synthetic capture through NVENC) and `AudioTimelineTapTests`, and `ReplayClipCaptureTests` (too-short, plus a second save).
- **Review:** quick lens, 5 findings.
  - Patched: 1 medium (the forced key could land on a frame captured before the press) and 2 low (a backwards comment; a keyframe forced when path allocation fails).
  - Deferred: 1 medium (a press during an in-flight write overlaps it), for 4.4.
  - Rejected: 1 low (the lean-tests rule).
- **Follow-up review recommended:** false. One medium was patched, and no high.
- **Verification (after the patches):**
  - `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings, 0 errors.
  - Default suites: Core 65/65, Contracts 97/97, Platform.FFmpeg 31 passed with 1 Explicit skipped, Engine 149 passed with 18 Explicit skipped.
  - `tools/publish-engine.ps1`: published `ReplayBuffer.Engine.exe` (10.5 MB) with no warnings.
  - The matrix-covering tests ran and passed: `ReplayCutTests`, `PacketRingTests`, `SessionOrchestratorTests`, `AudioTimelineTapTests` and `ReplayClipCaptureTests`.
- **Residual risks:**
  - Concurrent saves can overlap (deferred to 4.4).
  - `TryGetForcedKey` keeps only the latest served key, which is safe only while saves are seconds apart.
  - If the forced key isn't encoded within 1 s, the Clip ends at the press. Frames between the press and the late key then belong to neither Clip.
  - The builder hasn't yet played two real Clips back to back (Human Check).

