---
title: 'Tracer: Record Hotkey to a fragmented MP4 Recording beside the Replay Buffer'
type: 'feature'
ticket: '1'
created: '2026-11-03'
status: 'ready-for-dev'
baseline_revision: 'd6b57b6ef463e170bf92f9b0f96ea25e148f419d'
route: 'full'
route_source: 'auto'
risk: 'high'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/CLAUDE.md'
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** There is no Recording: a `recordToggle` press does nothing. Epic 6 needs the thinnest path through every layer: in the Target Game with Recordings on, one press starts a single fragmented MP4 written beside the Replay Buffer from the same encoder chain, and a second press finishes it.

**Approach:** The Replay Buffer's encoders write into a new `RecordingTap` packet sink that forwards every packet to the ring unchanged and, while a Recording is attached, hands a reference of each to a `RecordingWriter`. The writer runs on its own background-I/O thread and muxes them, rebased to a keyframe forced on the first frame after the press, into Platform.FFmpeg's `FragmentedMp4Muxer`. The SessionOrchestrator gates the press on the profile's live `recordingsEnabled`, reserves the path, calls `BeginWrite` and `EndWrite`, and publishes the state and `recording.started` and `recording.stopped`.

## Boundaries & Constraints

**Always:**
- Only the orchestrator starts and stops Recordings (AD-19). The writer posts its completion back, and the orchestrator never waits on file I/O.
- At the press, read `document.Profiles[profileId].RecordingsEnabled` live (R1), and resolve one `EffectiveSettings` snapshot for the profile (AD-5): its `OutputFolder` (else `Recordings.DefaultOutputFolder()`) and the press QPC go to `OutputPathAllocator.AllocateForGame`, the same folder as the game's Clips. Compare `ChainKey` (kind, process, `Video`, Track names and devices) with the session's. On a mismatch, share the session's current chain anyway, as the epic Note decides (6.5 owns the wait), and say so through `Info`.
- `BeginWrite(path, profileId)` before the writer creates the file, and `EndWrite(path, outcome)` once, after the trailer and close (or after a failed write's cleanup).
- The Recording starts on the first video keyframe at or after the forced key (`VideoEncoder.RequestKeyframe(pressQpc)` and `TryGetForcedKey`). If the encoder serves no forced key within 1 s, it starts on the first keyframe whose time is at or after the press. Each stream is rebased to that key in its own time base, as `PacketRing.Cut` does: video from the key, and audio packets whose centre is at or after the key. Audio that arrives before the key is known is held, at most about 2 s of it. A stop at QPC E keeps packets before E, as the same centre rule judges them, then finishes once every stream has passed E, 2 s after the stop at the latest, or as soon as the session's output ends.
- The writer thread runs in `THREAD_MODE_BACKGROUND_BEGIN` (AD-7). The tap's `Write` never blocks on file I/O: it does `av_packet_clone` plus an enqueue under an uncontended lock.
- `capture.stateChanged` shows `recording: running` from the accepted start and `off` once the writer has posted back. A toggle pressed while a Recording is stopping is ignored.
- Contracts change additively only (AD-22): payloads, events and a JSON context in `Recording.cs`, plus two nullable `StateSnapshot` fields.
- Keep Replay Buffer changes minimal (4.6 is changing the ring in parallel). `CaptureSession` gains only the tap's wiring and `Record`. `ReplayRingSink` and `PacketRing` don't change.

**Never:** no `recordStart` or `recordStop` handling and no `recordHotkeyMode` logic (6.3); no game-exit, Ignored, Target hold or shutdown reasons, and no replacement of the `RecordingRunning` stub (6.4); no `FlushFileBuffers` and no `recording-interrupted` (6.2); no disk-full truncation (6.7); no FeedbackRouter or sounds (6.8); no Monitor Recording; no reach back into the ring; no new settings keys; no Trello edits.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Start | Game session buffering, profile `recordingsEnabled` on, `recordToggle` | Path reserved in the game's folder, `BeginWrite`, state `recording: running`, `recording.started { path }` | Path allocation or start throws: the reserved file is deleted, `EndWrite(Failed)`, `Failed(e)`, state unchanged |
| Stop | Recording running, `recordToggle` | Writer finishes at the press; `EndWrite(Written)`, `recording.stopped { path, reason: "stopped" }`, state `off` | Writer error: reason `WriteFailures.ReasonOf(e)`, outcome `Written` if the file holds data, else deleted and `Failed` |
| Gate off | `recordingsEnabled` off, no profile, no game session, only a drain, or not anchored | Nothing: no file, no event, no state change; `Info` says why | — |
| Session ends mid-Recording | `EndActive` or `DrainActive` with a Recording on that session | The file is finished with everything encoded; `recording.stopped { reason: "session-ended" }`, state `off` | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- `Handle` routes `HotkeyAction.Save` and `MicMute`; add `RecordToggle`. Mirror `SaveClip` (L660) and `OnClipWritten` (L774) for the path, the Library calls and the post-back. `GameName(active)` names the folder. `EndActive` (L386) and `DrainActive` (L420) mark a running Recording `session-ended`. `_recordingRunning` and `RecordingRunning` stay as they are (6.4).
- `src/ReplayBuffer.Core/Sessions/CaptureSessionPorts.cs` -- `ICaptureSession` gains `IRecordingWrite? Record(long qpc) => null;`. Add the `IRecordingWrite { void Start(string path, Action<Exception?> done); void Stop(long qpc); }` interface, documented like `IClipWrite`.
- `src/ReplayBuffer.Core/Sessions/TargetGames.cs` -- beside `ReplayClips`, add `Recordings(OutputPathAllocator Paths, Func<string> DefaultOutputFolder, Action<RecordingStarted> Started, Action<RecordingStopped> Stopped, Action<Exception> Failed, ILibraryWrites? Library = null, Action<string>? Info = null)` and a `ChainKey` record with `Of(SessionSpec)` and `Of(kind, target, snapshot)`. Tracks compare by name and the `(EndpointId, Flow)` of each device, through `SessionAudio.FromLayout`. A layout that throws counts as a mismatch.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- in Replay mode, `StartOutput` (L572) wraps `_ring` in `_tap = new RecordingTap(_ring)` and passes `_tap` to the encoders. `End` closes the tap after `Finish()` and before `DisposeAll`; `DisposeAll` disposes it. `Record(qpc)` returns a nested `RecordingWrite`, or null when not Replay, not anchored, the output is finished, released, or a Recording is already attached. Its `Start` requests the keyframe, builds the writer from `_tap.CopyStreams()` and attaches it. Its `Stop` sends `MicrosAt(qpc)`.
- `src/ReplayBuffer.Platform.FFmpeg/RecordingTap.cs` (new) -- a `PacketSink` over an inner one. `AddStream` forwards the call and keeps its own `ClipStream` (the parameters, title, default flag, time base and whether it is video), and its index must equal the inner one's. `Write` offers the packet to the attached writer (skipping `BeforeStart`), then forwards the original. It also has `Attach` (one writer at a time), `Detach` from the writer, `Close`, `CopyStreams` and `Dispose` (frees its parameters).
- `src/ReplayBuffer.Platform.FFmpeg/RecordingWriter.cs` (new) -- the thread, queue, start and stop timing, muxer and done callback described under **Always**. It frees every cloned packet on every path. Use `av_packet_free` after `Write`, since the muxer takes the reference and leaves the struct.
- `src/ReplayBuffer.Platform.FFmpeg/FragmentedMp4Muxer.cs` -- add an internal `AddStream(AVCodecParameters*, AVRational timeBase, string? title, bool isDefault)` that shares the tagging and metadata code with the existing overload. Its `Write`, `Finish` and fragment options are reused unchanged.
- `src/ReplayBuffer.Platform.FFmpeg/ClipWriter.cs` -- make `EnterBackgroundMode` internal for reuse. That's its only change.
- `src/ReplayBuffer.Contracts/Messages/Recording.cs` -- `RecordingStarted(string Path)`, `RecordingStopped(string Path, string Reason)`, `RecordingStopReasons.Stopped = "stopped"` and `SessionEnded = "session-ended"`, the `recording.started` and `recording.stopped` `EventType`s, `Events`, and `RecordingJsonContext` (copy `CaptureJsonContext`'s options).
- `src/ReplayBuffer.Contracts/Messages/Engine.cs` -- nullable `LastRecordingStarted` and `LastRecordingStopped` on `StateSnapshot`, documented like `LastClip`.
- `src/ReplayBuffer.Engine/EngineHost.cs` -- when `replay` is on, pass `recordings:` with `OutputPathAllocator.System`, the same default folder as Clips, publishing to the hub plus a `Recordings` logger, and `NoLibraryWrites.Instance`.
- Tests: `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` (`FakeFactory`, `FakeSession`, `FakeLibraryWrites` L947–1100), `tests/ReplayBuffer.Engine.Tests/ReplayClipCaptureTests.cs` (the pattern: a ToneHub, a pwsh window, a profile layout, `ProposeTarget`, and the `SessionTestKit.Probe` and `TopLevelBoxes` helpers), `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` (`Samples` needs one entry per event).

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.Contracts/Messages/Recording.cs`, `Engine.cs` -- the payloads, events, context and snapshot fields -- the R7 events.
- [ ] `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` -- samples for both events -- the registry coverage test requires them.
- [ ] `src/ReplayBuffer.Core/Sessions/CaptureSessionPorts.cs`, `TargetGames.cs`, `SessionOrchestrator.cs` -- the ports, `Recordings`, `ChainKey`, and the toggle, gate, start, stop, post-back and session-end handling -- R1 and R8.
- [ ] `src/ReplayBuffer.Platform.FFmpeg/FragmentedMp4Muxer.cs`, `ClipWriter.cs`, `RecordingTap.cs`, `RecordingWriter.cs` -- the sink and the writer -- R2.
- [ ] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs`, `EngineHost.cs` -- the wiring -- R2.
- [ ] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- one test with a fake session and a fake `IRecordingWrite`. With `recordingsEnabled` off, a press calls nothing and publishes nothing. Switched on, two presses start, then stop, with `BeginWrite` and `EndWrite` once each, `recording.started` then `recording.stopped "stopped"`, and the state running then off.
- [ ] `tests/ReplayBuffer.Engine.Tests/RecordingCaptureTests.cs` (new) -- in process, a three-Track ToneHub profile with `RecordingsEnabled = true`, and two `recordToggle` presses `seconds` apart. A default `[Fact]` records about 4 s, and an explicit Heavy one records 60 s, through a shared body. The checks are in the second AC below.

**Acceptance Criteria:**
- Given a game session buffering and its profile's `recordingsEnabled` off, when `recordToggle` is pressed, then no file, event, Library call or state change follows.
- Given `recordingsEnabled` on, when `recordToggle` is pressed twice N s apart, then exactly one `.mp4` is in `<outputFolder>\Test Game`. Its top-level boxes have `moov` before the first `moof`. ffprobe reads one h264 stream and three AAC streams titled Game, Voice and Mic, with Game the default. The video lasts N ± 0.5 s, every Track is within 0.1 s of it, and the first video frame is a keyframe. It decodes cleanly, and no `.rbpart` is left.
- Given the same run, then `BeginWrite` and `EndWrite(Written)` were called once each, for that path. `capture.stateChanged` went `recording: running`, then `off`. `recording.started` and `recording.stopped "stopped"` carry the path, and the Replay Buffer stayed `buffering` throughout.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

The tap stays thin so the ring's hot path barely changes. A packet costs one `av_packet_clone` (a native ref, no byte copy) and one enqueue, and only while a Recording runs. The queue is a `ConcurrentQueue<(nint Packet, int Stream)>` plus a `SemaphoreSlim` the writer waits on with a 50 ms timeout. Closing takes the queue lock, sets `_closed`, then drains and frees, so a late `Offer` frees its clone instead of leaking. The writer owns copies of the stream parameters, so the session can dispose the tap, ring and encoders while the writer still finishes.

`RecordingWriter` sits in Platform.FFmpeg, not Media as the ticket says, because it handles `AVPacket`s and Media doesn't reference FFmpeg. Its job is the same.

Audio PTS are frames since the anchor in 1/48000. Video PTS are 1/90000 since the anchor. To get µs, rescale with Int128: `pts * 1_000_000 * num / den`.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build`, `... Core.Tests ...`, `... Platform.FFmpeg.Tests ...`, `... Engine.Tests ...` -- all pass (the Explicit 60 s test runs separately with `--explicit only --filter-method *Sixty*`)
- `pwsh tools/publish-engine.ps1` -- 0 IL or AOT warnings
