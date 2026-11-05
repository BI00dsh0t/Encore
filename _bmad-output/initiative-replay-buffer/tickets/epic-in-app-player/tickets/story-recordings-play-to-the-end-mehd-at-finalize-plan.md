---
title: 'Recordings play to the end: mehd at finalize'
type: 'feature'
ticket: '3'
created: '2026-11-05'
status: 'draft'
baseline_revision: '3cca32f9b04352c3872ee3db3f2201f99dbaa1eb'
route: 'full'
route_source: 'auto'
risk: 'high'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Media Foundation takes a fragmented MP4's length from its last `mfra/tfra` entry and stops playback there, so a Recording or Monitor Recording loses up to one fragment in Windows' players and the in-app player (R5, AD-27).

**Approach:** When the durable muxer finalizes, after the trailer and before the writer reports done (so before `EndWrite`), it rewrites a few hundred bytes inside the `moov` in place: `mvex` grows by 16 bytes to hold a version-0 `mehd` with the file's fragment duration, taken from the first 16 bytes of the `udta` that follows it, and the rest of that `udta` becomes a `free` box. No byte after the `moov` moves, so the `moof` offsets in `tfra` stay right.

## Boundaries & Constraints

**Always:**
- The patch is only applied when the layout is exactly the expected one: `moov` holds `mvhd`, and `mvex` (no `mehd` yet) is directly followed by `udta` whose size is 16 or at least 24. Otherwise the file is left as written and a warning names it.
- The duration is in the `mvhd` timescale, read from the header (FFmpeg's `movie_timescale`, 1000 today), rounded up; it doesn't fit in 32 bits → no patch, with a warning.
- A failed patch write never fails the Recording: the trailer is already on the disk, and the file plays to its last fragment as today.
- Contracts unchanged (AD-22). Lean tests (CLAUDE.md).

**Never:**
- Move or rewrite bytes outside the `moov`, or rewrite a large file.
- Touch Recordings made before this change, crash-cut files, or `InterruptedRecordings`' trim.
- Change Clips (`ClipWriter`, faststart) or the non-durable muxer path (`EncoderChain`'s debug direct-write, test experiments).
- Touch the App's Library or player code (14.2's lane), the builder's settings, Library or installed app.
- Run GPU, Heavy or capture tests by day.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Finalized Recording | durable muxer, `Finish` | `mvex` = old size + 16, `mehd` first in it, then the `trex` boxes; `free` box of `udta` size − 16; `moov` and file length unchanged; `mfra` intact | No error expected |
| Failed Recording finished as far as it got | `RecordingWriter`'s catch path calls `Finish` | Same patch | No error expected |
| Unexpected layout | no `udta` after `mvex`, `udta` 17–23 bytes, `mehd` already present | File as written | Warning naming the file |
| Patch write fails | I/O error after the trailer | File as written, Recording kept | Warning; no throw |
| Full disk / crash cut | no trailer | No `mehd`, as today | Unchanged paths |

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.Platform.FFmpeg/FragmentedMp4Muxer.cs` -- `Write` (durable branch rescales into `_context->streams[i]->time_base`, which movenc set to the track timescale at `avformat_write_header`); `Finish` durable branch: `av_write_trailer` → `_output.Flush()` → `CloseOutput()`. The patch goes between the flush and the close.
- `src/ReplayBuffer.Platform.FFmpeg/DurableOutput.cs` -- custom AVIO over a `FileStream` (`FileAccess.Write`, unbuffered). `Start` flushes once right after the header, so the bytes before the first `Flush` are exactly `ftyp` + `moov`.
- `src/ReplayBuffer.Core/Library/Mp4File.cs` -- "the one MP4 box walk"; `Next` reads a box header from a stream. Add the span-based patcher here, reusing its box conventions (32-bit size, 1 = 64-bit, 0 = to end).
- `src/ReplayBuffer.Platform.FFmpeg/RecordingWriter.cs` -- `Run`: `muxer.Finish()` then `_done(error)`; unchanged. `SessionOrchestrator.cs:2539` calls `EndWrite` after `_done`, so the patch lands before the Library lists the file. Monitor Recordings use the same writer (`CaptureSession.cs:3010`).
- `tests/ReplayBuffer.Platform.FFmpeg.Tests/RecordingStartTests.cs`, `LongRecordingTests.cs` -- synthetic-packet `RecordingWriter` tests (`Streams()`, `Feed`, `Fps`, `Tracks`, ffprobe JSON helpers) to copy for the new test.
- `tests/ReplayBuffer.Engine.Tests/RecordingCaptureTests.cs`, `MonitorRecordingCaptureTests.cs` -- existing Heavy real-capture Recording tests for the night check.
- Measured 2026-11-05 on the fetched FFmpeg (avformat 63) with CPU encoders and our movflags: H.264, HEVC (`hvc1`) and AV1 with four audio Tracks all give `moov` = `mvhd`, `trak`×5, `mvex` 168 B (5 `trex`, no `mehd`), `udta` 97 B (`meta/hdlr/ilst/©too "Lavf63.1.102"`) last. With `+bitexact`, `udta` is still there (61 B). So `udta` is always present and ≥ 24 bytes; no reserved-box fallback is needed.

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.Core/Library/Mp4File.cs` -- add `public static bool TryAddFragmentDuration(Span<byte> header, long durationUs, out int start, out int length)`: walk top-level to `moov`, its children to `mvhd` (timescale at +20 for v0, +28 for v1), `mvex` and the box after it; on the expected layout rewrite `[mvex, udta + 24)` in the span (`mvex` header with size + 16, `mehd` v0, the old `trex` children, `free` header of `udta` size − 16; when `udta` is exactly 16, no `free`), and return the changed range. Otherwise return false and leave the span untouched -- pure and testable without FFmpeg.
- [ ] `src/ReplayBuffer.Platform.FFmpeg/DurableOutput.cs` -- keep a copy of the bytes written before the first `Flush` (`Header`), and add `Overwrite(long offset, ReadOnlySpan<byte>)`: seek, write, flush (to the disk unless `flushToDisk` is false), seek back to the end -- the header is at offset 0, so the patched range writes back where it came from.
- [ ] `src/ReplayBuffer.Platform.FFmpeg/FragmentedMp4Muxer.cs` -- in `Write`, per stream keep the first DTS, the last DTS and the last packet's duration (in stream time base, after rescale). In `Finish` (durable), after the trailer flush: the duration = max over streams of `lastDts + lastDuration − firstDts`, rescaled to µs rounding up (movenc's `track_duration`); call `TryAddFragmentDuration` on a copy of `Header` and `Overwrite` the changed range; log a warning on false or on an `IOException`, and log the `mehd` duration on success.
- [ ] `tests/ReplayBuffer.Platform.FFmpeg.Tests/FragmentDurationTests.cs` -- one default test: a synthetic three-Track Recording through `RecordingWriter` (as `RecordingStartTests`). Walk its boxes: `Mp4File.IsComplete`; `moov`'s children tile it exactly; `mvex` holds `mehd` (16 bytes, v0) first; a `free` box ends where the `udta` did; `mehd` / `mvhd` timescale is within one frame of the video length; ffprobe's format duration is within one frame of it and lists 1 + `Tracks` streams with their titles; the `mfra` is last, its `mfro` size matches, and the first `tfra` entry's offset points at a `moof`.
- [ ] `tests/ReplayBuffer.Core.Tests/Mp4FileTests.cs` (new; the flat layout of that project) -- one test: a header with no `udta` after `mvex` returns false and leaves the bytes unchanged.
- [ ] `tests/ReplayBuffer.Engine.Tests/RecordingCaptureTests.cs`, `MonitorRecordingCaptureTests.cs` -- Heavy (`Explicit`, night): a ~5 s Recording each in H.264, HEVC and AV1, and the existing Monitor Recording test, opened in WinRT `MediaPlayer` from a stream: `NaturalDuration` within one frame of ffprobe's; seek to 1.5 s before the end, play, `MediaEnded` fires with the position at the end. A small shared helper in the Engine tests project holds the `MediaPlayer` check.

**Acceptance Criteria:**
- Given a Recording finalized by the engine, when ffprobe reads it, then its duration and Track list match the video length and Tracks, and its `mvex` holds an `mehd` with that duration.
- Given a real H.264, HEVC or AV1 Recording, or a 6400×2160 Monitor Recording, when WinRT `MediaPlayer` opens it, then `NaturalDuration` is within one frame of ffprobe's and playback reaches the last frame (night, Heavy).
- Given a Recording made before this change, or crash-cut, when the engine starts or the file is played, then its bytes are untouched.

## Design Notes

- **Why `mehd` first in `mvex`:** ISO/IEC 14496-12 lists it before `trex`, and it costs nothing: the whole `mvex` is rewritten from the header copy. The research's hand patch that MF accepted put an `mehd` in `mvex`; the night check proves this order on real files.
- **Atomicity:** one contiguous write of about 200 bytes near the file start, after the trailer is durable. A crash before it leaves today's file; a torn sector is the only half-state, and the patch region sits in the first 4 KB sector on every layout measured.
- **Crash-cut files (open question in the epic):** no `Finish`, so no `mehd`; MF has no length (AD-27: elapsed time only, seeking off, 14.5). `InterruptedRecordings` *could* write one: the trim already walks the fragments, and the length is each track's last `tfdt` + its last `trun` durations. Not built here. The night run hand-patches an `mehd` into a crash-cut copy (scratch script) to see whether MF then reports a length and seeks; the result goes to the coordinator for the epic's Notes (this build never writes ticket files), and a follow-up story if it works.
- **Clips:** faststart MP4 from `ClipWriter`, not fragmented; unaffected.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Platform.FFmpeg.Tests -c Release --no-build` -- expected: pass
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: pass
- Night, on the coordinator's go, one project at a time: `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only` filtered to the new Heavy tests -- expected: pass

**Manual checks (night):**
- R33: a real Recording per codec still plays with every Track in VLC and Chrome (the bench's `PlayerScripts`); DaVinci Resolve needs a person and goes to 14.8's hitl check.
