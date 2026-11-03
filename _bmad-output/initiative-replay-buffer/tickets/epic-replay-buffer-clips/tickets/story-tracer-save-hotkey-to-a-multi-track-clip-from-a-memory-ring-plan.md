---
title: 'Tracer: Save Hotkey to a multi-Track Clip from a Memory ring'
type: 'feature'
ticket: '1'
created: '2026-11-03'
status: 'built'
baseline_revision: '60cb338ab39bd1a62e9f48a5b4b3d8fb2a1482e2'
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
      A Clip still writing when the engine shuts down is not published, and its .rbpart can be left behind if the process exits mid-write.
    evidence: |-
      ClipWrite runs on a background thread and posts ClipWritten to the orchestrator, which may already be disposed; nothing joins the writer at shutdown. The exit drain and writer lifecycle belong to stories 4.3 and 4.5.
    location: >-
      src/ReplayBuffer.Engine/Sessions/CaptureSession.cs (ClipWrite)
    severity: low
  - summary: >-
      A save with nothing to cut fails with a plain InvalidOperationException, which is logged but carries no error code.
    evidence: |-
      The plan allowed no new Contracts beyond clip.saved; clip-too-short and clip.failed with reasons are stories 4.2 and 4.5.
    location: >-
      src/ReplayBuffer.Engine/Sessions/CaptureSession.cs (ClipWrite.Run)
    severity: low
---

<intent-contract>

## Intent

**Problem:** A detected game gets no Replay Buffer: game sessions start only under `serve --debug-capture` and write one long fMP4, and the `save` Hotkey does nothing. Nothing turns a press into a Clip.

**Approach:** The thinnest path through every layer (R1, R4, R6 parts). On `target.changed` to a game, the orchestrator starts a replay session from one `EffectiveSettings` snapshot, its audio from the snapshot's Track Layout. The session's encoders write into a Memory-mode ring of encoded packets in reference-counted native slabs with a keyframe index, trimmed to `bufferWindow`. A `save` press posts its QPC to the orchestrator. It reserves the path through the OutputPathAllocator and hands the session a write. A writer thread pins the slabs from the first video keyframe inside the Buffer Window up to the press and remuxes them into a faststart MP4 at `<final>.rbpart`, then renames it. The orchestrator publishes `clip.saved { path }`.

## Boundaries & Constraints

**Always:**
- AD-19: only the orchestrator starts or ends sessions, takes presses and publishes `clip.saved`. The writer thread posts its result back to the orchestrator's channel.
- AD-8: the ring holds encoded packets only, in native slabs counted by reference. A save pins the slabs it reads, so trimming or ending the session never frees them under a writer, and the writer never blocks the ring.
- No managed allocation per packet in the ring: a preallocated circular entry array (it doubles only when full) and native slab memory. Allocating per save is fine.
- AD-9: one AAC-LC 48 kHz stream per Track, `title` and `handler_name` set to the Track name, Track 1 the default audio stream, `movflags +faststart` (the moov atom before mdat).
- AD-21: the path comes only from `OutputPathAllocator`. The empty file it reserves is replaced by the rename. A failed write deletes both the `.rbpart` and the reserved file.
- The writer thread runs at background I/O priority (AD-7).
- Contracts change additively only (AD-22): the `clip.saved` event and an optional `lastClip` snapshot field.
- `serve --debug-capture` keeps its file-per-session behaviour, and `debug-capture` is unchanged.
- Lean tests, per `CLAUDE.md` "Tests". Anything that injects input or needs window focus is `[Fact(Explicit = true)]` + `[Trait("Category","Heavy")]`.

**Never:**
- The exact AD-10 cut, the Reset with a forced keyframe, `clip-too-short` (all story 4.2). The 30 s exit drain and Ignored (4.3). Save Cooldown, save events beyond `clip.saved`, and two concurrent writers (4.4). `clip.failed`, retry and `Library.BeginWrite/EndWrite` (4.5). Generations and resizing (4.6). Disk mode (4.7; Disk falls back to Memory with a log line). The RAM margin (4.8). Any App UI.
- Changing `debug-capture`'s output, the `TracerCapture` driver or story 3.x detection rules.
- Creating Game Profiles (story 3.4, which runs in parallel).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Save in a game | Target detected, buffering > 2 s, `save` pressed | `<outputFolder>\<profile folder>\<Name>_<time>.mp4`: video plus one titled AAC stream per Track, faststart, from the first keyframe in the window up to the press; `clip.saved { path }` | No error expected |
| Ring past the window | Buffering longer than `bufferWindow` | Packets older than the window (plus 2 s of GOP slack) are trimmed, and unpinned slabs are freed | — |
| No profile yet | The Target's `profileId` isn't in the document (story 3.4 not merged) | The allocator falls back to a folder named after the game's executable | — |
| No Replay Buffer | `save` with no game session, or before the first keyframe | No Clip, no file left behind | The press is ignored; a failed write is logged at Warning by the engine |
| Session ends mid-write | The Target exits while a writer is running | The Clip still completes from its pinned slabs | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- `Handle(HotkeyPressed)`, `CommitTarget`/`StartOnTarget`, `Start(StartSession)`, `Events`. A `save` press goes to a new `SaveClip(qpc)`. The constructor gains a last optional `ReplayClips? clips` parameter (3.1's order: micMute, targetChanged, targetCapture, then clips). A private `ClipWritten(path, error)` message carries the result back.
- `src/ReplayBuffer.Core/Sessions/TargetGames.cs` -- `TargetCapture(Func<SessionAudio> Audio, …)`: change to `Func<EffectiveSettings, SessionAudio>`, so the replay session's audio comes from the snapshot `Start` resolves. Update `SessionOrchestratorTests` (lines ~313, ~399) and `EngineHost`.
- `src/ReplayBuffer.Core/Sessions/CaptureSessionPorts.cs` -- `ICaptureSession` gains `IClipWrite? Save(long qpc) => null;` (a default member, so test fakes need no change), and a new port `IClipWrite { void Write(string path, Action<Exception?> done); }`.
- `src/ReplayBuffer.Core/Output/OutputPathAllocator.cs` -- `Allocate`/`Reserve` from 3.5. Add a method for a Target whose profile may not exist yet: it calls `Allocate` when the profile is there, else `Reserve(Combine(outputFolder, Sanitize(gameName)), gameName, localTime)`. Don't change `Allocate`.
- `src/ReplayBuffer.Core/ReplayBuffer.Core.csproj` -- add `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` for the ring's native slabs.
- New `src/ReplayBuffer.Core/Replay/PacketRing.cs` -- the ring (see Design Notes).
- `src/ReplayBuffer.Platform.FFmpeg/FragmentedMp4Muxer.cs` -- `NeedsGlobalHeader`, `AddStream(AVCodecContext*, title, isDefault)`, `Write(AVPacket*, streamIndex, timeBase)` (drops pre-start packets: `pts + duration <= 0`). Pull these into a new public abstract class `PacketSink` with internal abstract members, which the muxer and the ring sink derive from.
- `src/ReplayBuffer.Platform.FFmpeg/VideoEncoder.cs`, `AacEncoder.cs` -- the constructors take `PacketSink` instead of `FragmentedMp4Muxer`. VideoEncoder's `muxer: null` probe path stays. Bench and tests that pass a `FragmentedMp4Muxer` still compile.
- `src/ReplayBuffer.Platform.FFmpeg/NativeMethods.txt` -- CsWin32 list. Add what background I/O mode needs (`SetThreadPriority`, `GetCurrentThread`).
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- `StartOutput` creates the muxer and encoders, `Finish` flushes, `End` returns `SessionOutcome(_path, …)`, and `Open` allocates `_path`. Replay mode uses the ring sink, allocates no path, returns `Path: null`, and implements `Save`.
- `src/ReplayBuffer.Engine/EngineHost.cs` -- composes the orchestrator. A `TargetCapture` is set only for `DebugCapture`.
- `src/ReplayBuffer.Contracts/Messages/Capture.cs`, `Messages/Engine.cs` (`StateSnapshot`, `LastHotkeyPress` is the pattern) -- add `ClipSaved(string Path)`, `CaptureMessages.ClipSaved` ("clip.saved", field `lastClip`), the JSON context entries, and add it to `Events`.
- Tests: `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` (fakes and fixtures), `tests/ReplayBuffer.Engine.Tests/SessionTestKit.cs` (`OpenWindow`, `Probe`, `RequireFFmpeg`), `ToneHub.cs`, and `MicMuteCaptureTests.cs` (the pattern for an in-process session with `ToneHub`, a pwsh window and `Anchored`).

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.Core/Replay/PacketRing.cs` (+ csproj) -- the ring and `RingCut` (pinned entries + slabs, `IDisposable`) -- AD-8's store
- [ ] `src/ReplayBuffer.Core/Sessions/{CaptureSessionPorts,TargetGames,SessionOrchestrator}.cs` -- `Save`/`IClipWrite`, the `TargetCapture` audio from the snapshot, `ReplayClips(OutputPathAllocator Paths, Func<string> DefaultOutputFolder, Action<ClipSaved> Saved, Action<Exception> Failed)`, `SaveClip` (game session plus `ReplayClips`, else ignore; allocate from the current document's Global `outputFolder`, else the default, with `_target.Name` as the fallback name; `Write`, then post back; on success `Saved`, on failure `Failed`) -- R1, R4 and R6 wiring under AD-19
- [ ] `src/ReplayBuffer.Core/Output/OutputPathAllocator.cs` -- the fallback method -- until 3.4 creates profiles
- [ ] `src/ReplayBuffer.Platform.FFmpeg/{PacketSink,FragmentedMp4Muxer,VideoEncoder,AacEncoder}.cs` -- the sink abstraction
- [ ] `src/ReplayBuffer.Platform.FFmpeg/ReplayRingSink.cs` -- `PacketSink` over a `PacketRing`. It copies each stream's `AVCodecParameters` plus title/default/time base at `AddStream`, `NeedsGlobalHeader => true`, and in `Write` copies the packet bytes in and drops pre-start packets. `Cut(startUs, endUs)` returns a `ClipSource` (the `RingCut` plus its own copies of the parameters, so it outlives the sink) or null.
- [ ] `src/ReplayBuffer.Platform.FFmpeg/ClipWriter.cs` (+ NativeMethods.txt) -- writes a `ClipSource` to `<final>.rbpart` as a faststart MP4: timestamps rebased to the keyframe, packets in time order through `av_interleaved_write_frame`, background I/O mode for the thread; then `File.Move(rbpart, final, overwrite: true)`. On failure it deletes both files and rethrows.
- [ ] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- a `CaptureSessionOptions.Replay` flag: the ring sink sized from the snapshot (`bufferWindow`, fps, Track count), no path, outcome `Path: null`. `Save(qpc)` returns a write that runs on its own background thread. That thread waits up to 1 s for every stream to pass the press, cuts `[press − bufferWindow, press]` (anchor-relative), writes and calls `done`.
- [ ] `src/ReplayBuffer.Engine/EngineHost.cs` -- `EngineHostOptions.ReplayBuffer` (bool?; null = on for non-test instances). When it's on and `DebugCapture` is off: the factory in Replay mode, `TargetCapture(s => SessionAudio.FromLayout(s.TrackLayout), log)`, and `ReplayClips` (default folder `<MyVideos>\<instance name>`, `Saved` publishes `clip.saved` and logs Information, `Failed` logs Warning). `DebugCapture` uses `_ => SessionAudio.None` as before.
- [ ] `src/ReplayBuffer.Contracts/Messages/{Capture,Engine}.cs` -- `clip.saved` and `lastClip`
- [ ] `tests/ReplayBuffer.Core.Tests/PacketRingTests.cs` -- (1) append over more than the window: trimmed, and a cut starts at the first keyframe inside the window and ends at the press, audio included; (2) a cut's bytes are still intact after the ring is trimmed past them and disposed
- [ ] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- a `save` press on a session whose fake `Save` completes: `Saved` gets a path under `<folder>\<profile folder>\`, and a press with no session is ignored
- [ ] `tests/ReplayBuffer.Engine.Tests/ReplayClipCaptureTests.cs` -- in process, default run: a `ToneHub` with three devices, a three-Track layout, a profile "Test Game", and `ProposeTarget` on a pwsh window. After about 4 s it posts a `save` press and checks the file through `Saved`.

**Acceptance Criteria:**
- Given a detected Target Game and a three-Track layout, when the orchestrator commits it, then a replay session starts with that profile's snapshot and the state reads `buffering` (R1 part).
- Given about 4 s buffered, when `save` is pressed, then `clip.saved` names `<outputFolder>\Test Game\Test Game_<time>.mp4`. ffprobe reads h264 plus three AAC streams titled in layout order, only Track 1 default. The video lasts 2.5–5 s, ending at the press within 0.5 s. Each Track's duration is within 0.1 s of the video's (R4 part).
- Given that Clip, when its top-level boxes are read, then `moov` comes before `mdat`, and no `.rbpart` is left in the folder (R6 part).
- Given the default engine run, when the 0-warning Release build, the Core, Contracts and Engine default suites and the NativeAOT publish run, then all pass.

## Implementation Notes

- The implementing subagent built every task. Names that differ from the plan:
  - The Contracts descriptor is `CaptureMessages.Saved`, because a property named `ClipSaved` would clash with the `ClipSaved` record. The wire name `clip.saved` and the `lastClip` field are as planned.
  - The allocator method is `AllocateForGame`.
- `ReplayRingSink` has `Lease`/`Release` reference counting. Without it, a session that ends while a save is still waiting to cut would free the ring under the writer.
- The ffprobe title for these MP4 streams reads as `tags.name`, as in `TrackLayoutCaptureTests`. The writer sets both `title` and `handler_name`.
- The coordinator session added `OutputPathAllocatorTests.AGameWithNoProfileYetGetsAFolderNamedAfterIt`, the test for the matrix's "No profile yet" row. It also made `Cut` pin slabs through a reference `HashSet` rather than `List.Contains`.
- Matrix coverage:
  - "Save in a game": `ReplayClipCaptureTests`.
  - "Ring past the window": `PacketRingTests` test 1.
  - "No profile yet": the allocator test above.
  - "No Replay Buffer": `SessionOrchestratorTests`, a press with no session.
  - "Session ends mid-write": `PacketRingTests` test 2, pins surviving trim and dispose.

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 2 findings — high 0, medium 1, low 1, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` `PacketRing.Cut` sorted and projected every picked entry inside the ring lock. That stalls the video encoder and the audio threads' `Append` for each save (about 60k entries at the defaults), against "the writer never blocks the ring". The coordinator applied the fix itself, because the step-03 subagent had ended: the lock now covers only finding K, copying the entries, pinning the slabs and the origins; the `OrderBy` and the `RingPacket` projection run after it. Core 62/62 and Engine 162/162 pass, and the publish is clean.
  - `[low]` `[reject]` The descriptor is `CaptureMessages.Saved`, not the plan's `ClipSaved`. The wire name and the snapshot field match, and the plan's name would clash with the `ClipSaved` record inside `CaptureMessages`. The fix would only edit the plan; the difference is recorded in Implementation Notes.

## Auto Run Result

- **Summary:** a detected Target Game now starts a Replay Buffer session whose encoders write into a Memory-mode `PacketRing` (native slabs counted by reference, with a keyframe index, trimmed to `bufferWindow`). A `save` press reserves a path through the OutputPathAllocator. A background writer pins the slabs from the first keyframe in the window up to the press and writes a faststart MP4 to `<final>.rbpart`, renames it, and the orchestrator publishes `clip.saved { path }`. `serve --debug-capture` and `debug-capture` are unchanged.
- **Files:**
  - Core: `Replay/PacketRing.cs` (the ring and `RingCut`); `Sessions/{CaptureSessionPorts,TargetGames,SessionOrchestrator}.cs` (`Save`/`IClipWrite`, `ReplayClips`, `SaveClip`, the session's audio from the snapshot); `Output/OutputPathAllocator.cs` (`AllocateForGame`); the csproj (unsafe).
  - Platform.FFmpeg: `PacketSink.cs`, `ReplayRingSink.cs`, `ClipWriter.cs`, `FragmentedMp4Muxer.cs`, `VideoEncoder.cs`, `AacEncoder.cs`, `NativeMethods.txt`.
  - Engine: `Sessions/CaptureSession.cs` (Replay mode, `ClipWrite`); `EngineHost.cs` (`ReplayBuffer` option, the default Output Folder `<MyVideos>\<instance>`).
  - Contracts: `Messages/Capture.cs`, `Messages/Engine.cs` (`clip.saved`, `lastClip`).
  - Tests: `PacketRingTests`, `SessionOrchestratorTests`, `OutputPathAllocatorTests`, `MessagesTests`, `ReplayClipCaptureTests`.
- **Review:** 1 patch applied (medium), 0 deferred from review, 1 low rejected (descriptor name, see the triage log). Two deferred items from implementation are in the frontmatter (shutdown mid-write, no error code on an empty save).
- **Follow-up review recommended:** false. One medium was patched, none high.
- **Verification:** Release build 0 warnings; Core 62/62, Contracts 95/95, Platform.FFmpeg 31/31 and Engine 162 passed with 0 failed (explicit Heavy tests skipped); `publish-engine.ps1` published `ReplayBuffer.Engine.exe` (10.4 MB) with no warnings. After the patch: build, Core, Engine and the publish ran again and are green.
- **Residual risks:**
  - The builder hasn't played a real game and pressed a real Save Hotkey yet (Human Check).
  - A 300 s window at a high bitrate holds over 1 GB of RAM until story 4.8's margin.
  - The cut ends with what is in the ring about 1 s after the press, not the exact AD-10 cut (story 4.2).

## Design Notes

**Ring (Core).** Each entry is a struct `{ Slab, Offset, Length, Stream, Pts, Dts, Duration, Key, TimeUs }`. `TimeUs` is the packet's start in µs since the anchor, from its stream's time base, which is registered per stream. Slabs are 4 MB native blocks; a larger packet gets a slab of its own size. A slab's count is the ring's one reference (held while it's current or holds live entries) plus one per cut. It is freed at 0. `Append` and `Cut` take one short lock. The writer reads slab bytes with no lock, since bytes are never rewritten. Trimming runs on each append: drop from the head while `head.TimeUs < newest − window − 2 s`.

**Cut.** Find the first video keyframe K with `TimeUs ≥ start` and `≤ end`; with none, return null. Take video entries with `K ≤ TimeUs ≤ end`, and audio entries that overlap `[K, end]`. Rebase each stream to K in its own time base. An audio packet that starts a little before 0 gets an edit list from the mov muxer.

**Why the writer waits.** Encoding and the AAC FIFO lag the press by tens of ms, and a silent Track's 100 ms timer lags further. So the cut is taken on the writer thread once every stream's newest packet has passed the press, or after 1 s. That way each Track ends with the video. The exact cut is 4.2's.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- expected: all pass (the known flakes, once rerun)
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

**Manual checks (if no CLI):**
- Builder (Human Check Queue): play a detected game for a minute with a three-Track layout, press Save, and open the Clip in the game's folder in VLC. Every named Track should play in sync, and the Clip should end at the press.
