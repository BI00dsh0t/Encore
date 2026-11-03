---
title: 'Crash-safe Recordings and recording-interrupted'
type: 'feature'
ticket: '2'
created: '2026-11-03'
status: 'ready-for-dev'
baseline_revision: '368522baa927322d54ddae7cbe83638bd8684055'
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

**Problem:** 6.1's Recording relies on FFmpeg's own 1 s fragments and file buffering: nothing forces a fragment to disk, a fragment can hold audio ahead of the video, and an engine that dies mid-Recording leaves no trace, so nobody hears it was cut short (FR-12, R3, AD-16).

**Approach:** The `RecordingWriter` cuts the fragments itself: one at each video keyframe and at least every 0.5 s, each holding every Track up to the same instant, written whole, then pushed through FFmpeg's buffer and `FlushFileBuffers` before the next. Each open Recording's path is kept in `runtime.json` beside the mic mute from `BeginWrite` to `EndWrite`. At start the engine trims every path the last instance left open back to its last whole fragment and raises `recording-interrupted` for it.

## Boundaries & Constraints

**Always:**
- The durable muxer writes through a custom AVIO over a `FileStream` it owns (`frag_custom+empty_moov+default_base_moof`, `av_write_frame`, `av_write_frame(ctx, null)` per cut). The moov is flushed to disk right after the header. All muxer and file calls run on the writer thread (background I/O mode, AD-7); the orchestrator never waits on them.
- A cut at video time V writes every pending packet before V (audio by its centre, as 6.1 judges it) once every audio stream has reached V, or once the video is 250 ms past V. No Track's fragment runs ahead of the video.
- A write callback failure is kept and rethrown as the original exception (6.7 needs the disk-full `IOException`), never swallowed.
- `runtime.json` holds `openRecordings` only while it isn't empty, so `{"micMuted":false}` stays byte-identical. The path goes in after `BeginWrite` and before the writer starts. It comes out after `EndWrite`, on every path (written, failed, never started).
- On start, before the Library loads: the store returns the paths and clears them from the file. Each file that still exists is trimmed to its last whole fragment (a trailing `moof` without a whole `mdat` goes too). A file left with no fragment is deleted. Then one `recording-interrupted` warning per path is raised (Warning, source `recordings`, overlay, dismissable, the path in its message). No Recording starts.
- The box walk ("is this MP4 complete / where does its last whole fragment end") lives only in `Core/Library/Mp4File.cs`, with the `IsComplete` that 7.2 plans, so 7.2 uses it rather than writing its own.
- Contracts: only the `ErrorCodes.RecordingInterrupted` doc comment changes (AD-22).

**Never:** no resume of a Recording; no disk-full truncation or `disk-full` handling (6.7); no shutdown `EndWrite` (6.4); no sound for the warning (6.8); no changes to the Replay Buffer's ring, `RecordingTap`, `CaptureSession` or the non-durable muxer path (debug capture); no Library reconciliation (7.2); no Trello edits.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Running | Recording on, 3 Tracks | a `moof` with one `traf` per stream at each keyframe and at least every 0.5 s, each flushed to disk | write fails: the writer fails with the callback's exception, keeps what it holds (6.1 path) |
| Killed | engine dies T s in | the file plays with every Track; its media ends under 1 s before T, each Track within one AAC frame of the video | — |
| Restart | `runtime.json` lists an open path | the file is trimmed to its last whole fragment, one `recording-interrupted` with the path, nothing recording, the list cleared | file missing: still warned; trim fails: logged, still warned |
| Nothing written | open path holding only ftyp/moov or less | file deleted, warned | delete fails: logged |
| Normal end | stop press | path leaves `runtime.json` after `EndWrite`; no warning next start | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Platform.FFmpeg/FragmentedMp4Muxer.cs` -- add an `internal` durable mode: a ctor flag that opens a `DurableOutput` in `Start` (set `_context->pb`, `AVFMT_FLAG_CUSTOM_IO`), the movflags above with no `frag_duration`, `Write` through `av_write_frame`, `internal void CutFragment()`, and `Finish` with `av_write_trailer` then a flush and close. The non-durable path stays as it is. `Dispose` mustn't `avio_closep` a custom pb.
- `src/ReplayBuffer.Platform.FFmpeg/DurableOutput.cs` (new) -- `avio_alloc_context` (64 KB `av_malloc` buffer, write flag) over an unbuffered `FileStream(path, Create, Write, FileShare.Read)`. It uses a `GCHandle` opaque and `[UnmanagedCallersOnly]` cdecl write and seek callbacks (seek handles `AVSEEK_SIZE` and masks `AVSEEK_FORCE`; a failure returns `-5`, EIO, and keeps the first exception). `Flush()` does `avio_flush` then `Flush(flushToDisk: true)`. `Dispose` frees the buffer, the context and the handle, and closes the file.
- `src/ReplayBuffer.Platform.FFmpeg/RecordingWriter.cs` -- `Open` uses the durable muxer. From K on, `Emit` filters as now but appends to `_pending` instead of writing. It tracks `_reached[stream]`, `_fragmentUs` (K at `StartAt`) and `_cutUs`, the first pending video packet after `_fragmentUs` that is a keyframe or ≥ `FragmentUs` (500 ms) past it. `Cut` loops while it's ready (above): write the pending packets before the cut in order, `CutFragment()`, move on. The current rebase + `muxer.Write` + free becomes `WritePending`, which counts `_written`. `Run` drains `_pending` after `Loop`, before the `_written == 0` check. `Release` frees `_pending` too.
- `src/ReplayBuffer.Core/Library/Mp4File.cs` (new, public static) -- `IsComplete(path)`: the first box is `ftyp`, a `moov` is present, and the boxes end exactly at the length (32-bit size; `1` = 64-bit largesize; `0` = to end). It opens with `FileShare.ReadWrite | FileShare.Delete`, and an `IOException` propagates (7.2's contract). Also `PlayableLength(Stream)`: the end of the last whole box, not counting a `moof` whose `mdat` isn't whole; 0 without `ftyp` and `moov`. And `TrimToLastFragment(path)`: it truncates to that length and returns the count of whole fragments kept.
- `src/ReplayBuffer.Core/Sessions/CaptureSessionPorts.cs` -- an `IOpenRecordings { void Opened(string path); void Closed(string path); }` beside `IMicMuteStore`.
- `src/ReplayBuffer.Core/Sessions/TargetGames.cs` -- `Recordings` gains a trailing `IOpenRecordings? Open = null`.
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` `StartRecording` (L870-941), `OnRecordingWritten` (L960) -- `Opened` after `BeginWrite`; `Closed` after each `EndWrite`.
- `src/ReplayBuffer.Engine/Settings/RuntimeStateStore.cs` -- it keeps `_muted` and `_open` in memory and writes both on every change. `RuntimeState(bool? MicMuted, IReadOnlyList<string>? OpenRecordings)` drops a null when writing. It implements `IOpenRecordings`. `TakeInterrupted()` reads `openRecordings`, rewrites the file without them (only when there were some), and returns them. `Load` keeps its mic rules.
- `src/ReplayBuffer.Engine/Sessions/InterruptedRecordings.cs` (new) -- `Recover(paths, ILogger)` trims or deletes each one, as above. Errors are logged, never thrown.
- `src/ReplayBuffer.Engine/EngineHost.cs` -- create one `RuntimeStateStore` before `LibraryActor.Load` (L229), then `TakeInterrupted` + `Recover`. After `warnings` exists, raise each warning before `server.Start` (beside `SettingsUnreadable`, L449). Pass the same store as `micMute` (L306) and as `Open:` on `Recordings` (L343).
- `src/ReplayBuffer.Contracts/ErrorCodes.cs:93-98` -- the doc comment now says the engine raises it at start.
- Tests: `tests/ReplayBuffer.Engine.Tests/RecordingCaptureTests.cs` (the setup to copy: a ToneHub, three Tracks, a pwsh window, `ProposeTarget`); `ReplayClipCaptureTests.TopLevelBoxes`; `SessionTestKit.Probe`, `RequireFFmpeg`; `MicMuteServeTests` (an in-process `EngineHost.Start` on a test `Instance`, the first snapshot via `EngineClient`); `RuntimeStateStoreTests`; `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` (the Recording test, `FakeLibraryWrites`).

## Tasks & Acceptance

**Execution:**
- [ ] `DurableOutput.cs`, `FragmentedMp4Muxer.cs` -- the durable mode -- R3 flush per fragment
- [ ] `RecordingWriter.cs` -- pending, cut and drain -- R3 interleave and cadence
- [ ] `Mp4File.cs` -- the box walk, `IsComplete`, trim -- shared with 7.2
- [ ] `CaptureSessionPorts.cs`, `TargetGames.cs`, `SessionOrchestrator.cs` -- `IOpenRecordings` -- R1 restart tracking
- [ ] `RuntimeStateStore.cs`, `InterruptedRecordings.cs`, `EngineHost.cs`, `ErrorCodes.cs` -- the store, recovery and warning -- R1 no resume
- [ ] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- the existing Recording test also asserts `Opened` then `Closed` for the path, in order with `BeginWrite`/`EndWrite`
- [ ] `tests/ReplayBuffer.Engine.Tests/RuntimeStateStoreTests.cs` -- one test: `Opened`/`Closed` persist beside `micMuted`, `TakeInterrupted` returns them once and leaves `{"micMuted":…}`
- [ ] `tests/ReplayBuffer.Engine.Tests/RecordingCrashTests.cs` (new) -- 6.1's setup with a `RuntimeStateStore` on a test instance as `micMute` and `Open`. A "kill" at T copies the Recording's bytes and `runtime.json` (`FileShare.ReadWrite`), which is exactly what a killed process leaves. Default `[Fact]`: one kill about 3 s in; Heavy explicit: ten kills at random 20–90 s points in one Recording. Then the Recording stops normally, and the finished file has a `moof` holding 4 `traf`s at least every 0.5 s of its length. Copies truncated at each fragment boundary, and one cut mid-fragment then trimmed, all play (ffprobe, 4 streams). The kill copy is put back with its `runtime.json`, and `EngineHost.Start` on that instance is checked (third AC)

**Acceptance Criteria:**
- Given a Recording of three Tracks, when the engine is killed T s in, then the file left plays with h264 + 3 AAC streams, its video ends under 1 s before T (measured from the press, an upper bound), and each Track's end is within one AAC frame plus one video frame of the video's.
- Given a finished Recording, when it is cut after any fragment, or mid-fragment and trimmed, then every cut plays with all four streams, and `Mp4File.IsComplete` holds for the trimmed one.
- Given the killed Recording's file and `runtime.json`, when the engine starts on that instance, then its first snapshot holds exactly one dismissable overlay `recording-interrupted` warning naming the path, Recording is off, the file is trimmed (`IsComplete`), and `runtime.json` lists no open Recording.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

Why 0.5 s and not 1 s: the loss at a kill is the open fragment plus the pipeline's latency (capture to encoded packet, the cut's wait for audio), so 1 s fragments could lose a little over 1 s. With GOP = fps (`VideoEncoder.cs:241`), each second has one keyframe cut and one cut mid-GOP. A fragment that starts mid-GOP is valid fMP4 (trun sample flags), and the decoder carries on from the last keyframe.

Why a custom AVIO: `FlushFileBuffers` needs the handle that FFmpeg's file protocol hides. Owning the `FileStream` gives the handle, the low-priority writes on the writer thread, and the exception, all in one place. 6.7 also needs the fragment end offsets.

The kill copy and a real kill leave the same bytes: both hold what reached the OS and nothing still in FFmpeg's buffer. The OS cache survives a process kill, and power loss is covered by the flush, which only the builder can check (epic Note).

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build`, then the same for `ReplayBuffer.Engine.Tests`, `ReplayBuffer.Platform.FFmpeg.Tests` and `ReplayBuffer.Contracts.Tests` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only --filter-class *RecordingCrashTests` -- expected: the ten-kill run passes
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings
