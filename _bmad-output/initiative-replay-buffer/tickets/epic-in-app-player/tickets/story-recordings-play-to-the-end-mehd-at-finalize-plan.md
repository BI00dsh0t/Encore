---
title: 'Recordings play to the end: mehd at finalize'
type: 'feature'
ticket: '3'
created: '2026-11-05'
status: 'built'
baseline_revision: '3cca32f9b04352c3872ee3db3f2201f99dbaa1eb'
route: 'full'
route_source: 'auto'
risk: 'high'
review: 'quick'
review_source: 'pinned'
lenses_ran: [quick]
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Media Foundation takes a fragmented MP4's length from its last `mfra/tfra` entry and stops playback there, so a Recording or Monitor Recording loses up to one fragment in Windows' players and the in-app player (R5, AD-27).

**Approach:** When the durable muxer finalizes, after the trailer and before the writer reports done (so before `EndWrite`), it patches the `moov` in place: the first 20 bytes of the `udta` that follows `mvex` become a version-1 `mehd` with the file's fragment duration, the rest of that `udta` becomes a `free` box, and `mvex` grows by 20 bytes to take the `mehd` in as its last child. No byte after the `moov` moves, so the `moof` offsets in `tfra` stay right.

**Decision (coordinator, at approval, 2026-11-05):** the patch must not leave `mvex` and `free` half-updated on a power cut: written with `FlushFileBuffers` before `EndWrite`, kept off sector boundaries where cheap, and the plan says what a torn patch looks like; `Mp4File.IsComplete` and the Library's scan must treat it sensibly (it plays to its last fragment, as today). In response, the patch is two ordered writes, each flushed to the disk: first the `mehd` and the `free` header over the `udta`'s start (28 bytes), then `mvex`'s size field. Every state in between is a valid box tree (measured: Media Foundation opens the in-between file with today's length). This replaces the draft's single rewrite of `[mvex, udta+24)` with `mehd` first, which had no valid in-between state.

## Boundaries & Constraints

**Always:**
- The patch is only applied when the layout is exactly the expected one: `moov` holds `mvhd`, and `mvex` (no `mehd` yet) is directly followed by `udta` whose size is 20 or at least 28. Otherwise the file is left as written and a warning names it.
- The duration is in the `mvhd` timescale, read from the header (FFmpeg sets it to the audio rate, 48000, when there's audio), rounded up, in a version-1 `mehd` (64-bit), so no length overflows.
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
| Finalized Recording | durable muxer, `Finish` | `mvex` = old size + 20, the `trex` boxes then a v1 `mehd`; `free` box of `udta` size − 20; `moov` and file length unchanged; `mfra` intact | No error expected |
| Power cut between the two writes | `mehd` + `free` written, `mvex` size not | A valid tree with a stray `mehd` directly in `moov`; complete to the Library; plays to its last fragment, as today | None needed |
| Failed Recording finished as far as it got | `RecordingWriter`'s catch path calls `Finish` | Same patch | No error expected |
| Unexpected layout | no `udta` after `mvex`, `udta` 21–27 bytes, `mehd` already present | File as written | Warning naming the file |
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
- Measured 2026-11-05 on the fetched FFmpeg (avformat 63) with CPU encoders and our movflags: H.264, HEVC (`hvc1`) and AV1 with four audio Tracks all give `moov` = `mvhd`, `trak`×5, `mvex` 168 B (5 `trex`, no `mehd`), `udta` 97 B (`meta/hdlr/ilst/©too "Lavf63.1.102"`) last. With `+bitexact`, `udta` is still there (61 B). So `udta` is always present and ≥ 28 bytes; no reserved-box fallback is needed.
- Measured 2026-11-05 with WinRT `MediaSource.OpenAsync().Duration` (a parse, no decode) on those files, 2 s long, unpatched 1.045–1.067 s: an `mehd` v0 or v1, first or last in `mvex`, gives 2.000 s, read in the `mvhd` timescale (a video-only file's is 1000; with audio FFmpeg sets it to the audio rate, 44100 or 48000). The in-between state (a stray `mehd` in `moov`, `mvex` not grown) opens at 1.067 s, as unpatched; ffprobe reads all three the same.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Core/Library/Mp4File.cs` -- add a pure `FragmentDurationPatch? PlanFragmentDuration(ReadOnlySpan<byte> header, long durationUs)` (a record of two writes: `(MehdOffset, MehdAndFree bytes)` then `(MvexOffset, new 4-byte size)`): walk top-level to `moov`, its children to `mvhd` (timescale at +20 for v0, +28 for v1), `mvex` and the box after it; on the expected layout return the `mehd` v1 (20 bytes) plus a `free` header of `udta` size − 20 (none when `udta` is exactly 20), and `mvex` size + 20. Otherwise null -- testable without FFmpeg.
- [x] `src/ReplayBuffer.Platform.FFmpeg/DurableOutput.cs` -- keep a copy of the bytes written before the first `Flush` (`Header`), and add `Overwrite(long offset, ReadOnlySpan<byte>)`: seek, one write, `Flush(flushToDisk)` (`FlushFileBuffers`), seek back to the end -- the header is at offset 0, so offsets in it are file offsets.
- [x] `src/ReplayBuffer.Platform.FFmpeg/FragmentedMp4Muxer.cs` -- in `Write`, per stream keep the first DTS, the last DTS and the last packet's duration (in stream time base, after rescale). In `Finish` (durable), after the trailer flush: the duration = max over streams of `lastDts + lastDuration − firstDts`, rescaled to µs rounding up (movenc's `track_duration`); plan the patch on `Header`, then `Overwrite` the `mehd` write, then the `mvex` size write, in that order; log a warning on null or on an `IOException`, and log the `mehd` duration on success.
- [x] `tests/ReplayBuffer.Platform.FFmpeg.Tests/FragmentDurationTests.cs` -- one default test: a synthetic three-Track Recording through `RecordingWriter` (as `RecordingStartTests`). Walk its boxes: `Mp4File.IsComplete`; `moov`'s children tile it exactly; `mvex` ends with an `mehd` (20 bytes, v1); a `free` box ends where the `udta` did; `mehd` / `mvhd` timescale is within one frame of the video length; ffprobe's format duration is within one frame of it and lists 1 + `Tracks` streams with their titles; the `mfra` is last, its `mfro` size matches, and the first `tfra` entry's offset points at a `moof`. Then a copy with `mvex`'s size set back by 20 (the state between the two writes): `Mp4File.IsComplete`, `moov`'s children still tile it, and ffprobe still reads it with every stream.
- [x] `tests/ReplayBuffer.Core.Tests/Mp4FileTests.cs` (new; the flat layout of that project) -- one test: a header whose `mvex` isn't followed by a `udta` plans no patch (null).
- [x] `tests/ReplayBuffer.Engine.Tests/RecordingCaptureTests.cs`, `MonitorRecordingCaptureTests.cs` -- Heavy (`Explicit`, night): a ~5 s Recording each in H.264, HEVC and AV1, and the existing Monitor Recording test, opened in WinRT `MediaPlayer` from a stream: `NaturalDuration` within one frame of ffprobe's; seek to 1.5 s before the end, play, `MediaEnded` fires with the position at the end. A small shared helper in the Engine tests project holds the `MediaPlayer` check.

**Acceptance Criteria:**
- Given a Recording finalized by the engine, when ffprobe reads it, then its duration and Track list match the video length and Tracks, and its `mvex` holds an `mehd` with that duration.
- Given a real H.264, HEVC or AV1 Recording, or a 6400×2160 Monitor Recording, when WinRT `MediaPlayer` opens it, then `NaturalDuration` is within one frame of ffprobe's and playback reaches the last frame (night, Heavy).
- Given a Recording made before this change, or crash-cut, when the engine starts or the file is played, then its bytes are untouched.

## Implementation Notes

- Built in 9fe1fb0 (patcher, header copy, muxer, tests) and 930745a (review fixes). `mvex` and `udta` must also use 32-bit sizes, and so must `mvhd` after review. The success log line says whether the first write straddles a 512-byte sector.
- The Heavy MediaPlayer check is `MediaPlayerCheck` in Engine.Tests. It's a Theory over H.264, HEVC and AV1 in `RecordingCaptureTests`, and is appended to `MonitorRecordingCaptureTests`' 60 s two-screen test. None of it has run (night only). The end-position assertion (within one frame after `MediaEnded`) may need loosening if MF reports a slightly different end.
- Not tested, on purpose (lean tests): a patch write that throws `IOException` only logs. Full-disk and crash-cut paths never reach the patch; the existing `DiskFullRecordingTests` and `RecordingCrashTests` still pass.
- Day verification: the 0-warning Release build; Core 171/171, Platform.FFmpeg 41 passed with 2 skipped, Engine 201 with 51 skipped, App 201 with 20 skipped (the skips are the existing Explicit tests); `tools/publish-engine.ps1` with no IL warnings.
- Crash-cut experiment, parse half (2026-11-05, CPU-encoded spike files, WinRT `MediaSource.Duration`): with `mfra` cut off, MF reports no length (`TimeSpan.MaxValue`); with a hand-patched `mehd` it reports the full 2.000 s. So `InterruptedRecordings` writing an `mehd` would give crash-cut Recordings a known length. Whether they then seek waits for the night playback check.

## Plan Change Log

- 2026-11-05, at approval (coordinator's durability point): the single `[mvex, udta+24)` rewrite with a v0 `mehd` first became two ordered, flushed writes with a v1 `mehd` last in `mvex`. It avoids a half-updated `mvex`/`free` on a power cut (the old rewrite had no valid in-between state) and a 32-bit overflow at 48 kHz after 24.8 h. Measured before the switch: Media Foundation reads v0 and v1, first or last; the in-between file opens with today's length. KEEP: the layout check, the header copy, the duration source, nothing outside `moov` touched.

## Review Triage Log

- Pass 1 (quick lens): high 0, medium 0, low 3, false 0, maybe-false 0.
  - low, patch: the first write's straddle of a 512-byte sector isn't recorded (the coordinator's "off sector boundaries where cheap"). The offsets are FFmpeg's and can't move, so the success log now says whether it straddles.
  - low, patch: an `mvhd` with a 64-bit size header would be read as an 8-byte one, giving a wrong timescale. It's now rejected like `mvex` and `udta`. Latent: FFmpeg never writes one.
  - low, rejected: an `mvex` whose children don't tile it passes the "no `mehd`" check. Latent with FFmpeg's well-formed output, and the fix needs a new tiling check, more code than the problem deserves.

## Design Notes

- **Why `mehd` last in `mvex`:** it lets the patch be two small ordered writes with a valid file between them, where `mehd` first (ISO's listed order) means rewriting all of `mvex` in one ~200-byte write with no valid half-state. Media Foundation and ffprobe read it in either position (measured).
- **What a power cut leaves:** before the first write's flush, today's file. Between the writes, a stray `mehd` and a `free` box directly in `moov`, after `mvex`: valid, plays to its last fragment (measured). After the second, the patched file; the second write changes one byte of the size field unless its low byte carries, so it can't tear. A torn first write needs its 28 bytes to straddle a 512-byte sector (about 5% of layouts; none of the three measured) and the cut to land inside that write: then the boxes after `mvex` inside `moov` may be junk. In every case no top-level box size changes, so `Mp4File.IsComplete` and `PlayableLength` (the Library's scan and the start-up trim) see the file as complete, as today; the `trak` and `mvex` boxes before the patch are untouched.
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
