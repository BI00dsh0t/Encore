---
title: 'Failed saves, retry and the file-being-written signal'
type: 'feature'
ticket: '5'
created: '2026-11-03'
status: 'in-progress'
baseline_revision: '2bb1f18a870fd555d146b76308c4c8ec278d35ea'
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

**Problem:** When a Clip write fails after `clip.saving` (disk full, access denied, a mux error), the failure is only logged, so the Overlay gets no outcome for that press (4.4 QA note 1). Nothing tells the future Library that a file is being written. And with `saveCooldown` 0, a second press that resolves to the same keyframe as a write still in flight cuts an empty span and writes an empty Clip (4.4 QA note 2).

**Approach:** Every save that makes no Clip now emits `clip.failed { reason }`. The reason is a short code from `Contracts.ErrorCodes`: the new `disk-full`, `access-denied` and `save-failed`, plus the existing `clip-too-short` and `save-busy`. A failed write still Resets nothing and drops its claim, so the next press retries with the same content plus anything captured since. A new Core port, `ILibraryWrites`, carries the "file being written" signal. The orchestrator calls `BeginWrite(path, profileId)` after the path is reserved and before the writer creates the `.rbpart`, and `EndWrite(path, outcome)` once the writer's result is back. Until epic-library-cleanup's LibraryActor exists, a no-op implementation stands in. In the session, a cut shorter than 1 s (because an earlier write still in flight has claimed the span) is refused as `clip-too-short` instead of being written.

## Boundaries & Constraints

**Always:**
- AD-19: only the orchestrator emits `clip.*` and calls `ILibraryWrites`, on its own thread. Writers post back to it.
- AD-3/AD-22: Contracts change additively, with three new `ErrorCodes` (also added to `All`) and no new message types.
- A failure leaves no `.rbpart` and no `.mp4` (the reserved one included), releases the ring lease and the cut's pins, and calls no `Reset`. The cooldown still counts from the accepted press.
- `BeginWrite` and `EndWrite` are called exactly once each for every attempt that reserved a path, with `Written` or `Failed`. A press refused before the path is reserved (cooldown, busy, too short, or nothing anchored) calls neither.
- Lean tests (CLAUDE.md "Tests").

**Never:**
- The LibraryActor, the index or the watcher (epic 7), Recordings (epic 6), or Overlay and sounds (epic 5).
- A new event type for write failures, or `clip.failed` for a press ignored inside the cooldown.
- A retry the engine starts by itself: a retry is the user's next press.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Disk full while writing | FFmpeg returns `AVERROR(ENOSPC)`, or an `IOException` has HResult `0x80070070` or `0x80070027` | `clip.saving`, then `clip.failed { disk-full }`. Files removed. No Reset. `EndWrite(Failed)` | Warning logged with the message |
| Access denied | `UnauthorizedAccessException`, or `AVERROR(EACCES)` | `clip.failed { access-denied }` | as above |
| Other writer error | Any other exception (mux error, `encode-failed` from `Check`) | `clip.failed { save-failed }` | as above |
| Reserve fails | The allocator throws `IOException` (disk full) | `clip.failed { disk-full }`. No `BeginWrite` | — |
| Retry | Press A saved; press B fails; press C | C's Clip runs from A's end to C's press | — |
| Same keyframe | `saveCooldown` 0, two presses with one QPC | One Clip is saved, and the other press gets `clip.failed { clip-too-short }`. No empty file | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/ErrorCodes.cs:137-188` -- Add `DiskFull = "disk-full"`, `AccessDenied = "access-denied"` and `SaveFailed = "save-failed"` after `SaveBusy`, with doc comments that say which writers raise each (Clips now, Recordings later). Add all three to `All`.
- `src/ReplayBuffer.Platform.FFmpeg/FFmpegLibrary.cs:72-80` -- `Check` throws `EncodeFailed`. Map `-28` (ENOSPC) to `DiskFull` and `-13` (EACCES) to `AccessDenied`; every other error stays `EncodeFailed`. `FFmpegLibraryTests` already reaches internals.
- New `src/ReplayBuffer.Core/Output/WriteFailures.cs` -- `public static string ReasonOf(Exception e)`. An `ErrorCodeException` keeps its code when that code is `ClipTooShort`, `SaveBusy`, `DiskFull` or `AccessDenied`; any other `ErrorCodeException` maps to `SaveFailed`. An `IOException` with a disk-full HResult maps to `DiskFull`, an `UnauthorizedAccessException` to `AccessDenied`, and anything else to `SaveFailed`. It is public so that epic 6 can reuse it.
- New `src/ReplayBuffer.Core/Library/LibraryWrites.cs` -- Holds `public enum WriteOutcome { Written, Failed }`, `public interface ILibraryWrites { void BeginWrite(string path, string? profileId); void EndWrite(string path, WriteOutcome outcome); }` and `public sealed class NoLibraryWrites : ILibraryWrites` with a static `Instance`. The XML docs are the contract for epics 6 and 7:
  - `path` is the absolute final path.
  - `profileId` is the Game Profile id, or null.
  - Begin comes before the writer creates its temp or final data.
  - End comes after the rename or the final flush, or after the failure's cleanup.
  - Neither call blocks or throws, and any thread may call them (the LibraryActor will post to its queue).
  - Epic 7 replaces the no-op and epic 6 reuses the port.
- `src/ReplayBuffer.Core/Sessions/TargetGames.cs` -- `ReplayClips`: add a last optional `ILibraryWrites? Library = null` (null is `NoLibraryWrites.Instance`). Update the `Failed` and `NotSaved` docs: `NotSaved` now receives every save that made no Clip, and `Failed` receives the exception for logging.
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs:656-790`:
  - `SaveClip`: after `AllocateForGame`, call `Library.BeginWrite(path, active.Spec.Target.ProfileId)` before `write.Write`. If `write.Write` throws, the existing `TryDelete(path)` runs, then `EndWrite(path, Failed)`.
  - `OnClipWritten`: `EndWrite(written.Path, error is null ? Written : Failed)` first.
  - `ReportSaveFailure(e)`: a non-refusal (not `ClipTooShort` or `SaveBusy`) calls `_clips.Failed(e)`. Every failure then calls `_clips.NotSaved(new ClipFailed(WriteFailures.ReasonOf(e)))`.
  - The paths that already call `NotSaved` directly stay as they are.
- `src/ReplayBuffer.Engine/EngineHost.cs:248-268` -- Pass `Library: NoLibraryWrites.Instance` explicitly (the LibraryActor goes here later). Keep the Warning log in `Failed`.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs`:
  - `CaptureSessionOptions` (line 23): add test-only `Func<string, Exception?>? ClipWriteFault`, documented like `Anchored`. It runs on the writer thread with the final path just before `ClipWriter.Write`, and a non-null return is thrown as that write's failure.
  - `ClipWrite.Run` (line ~1053): after `fromUs` is computed, `if (toUs - fromUs < 1_000_000)` throw `ErrorCodeException(ClipTooShort, ...)`. The existing catch drops the claim and deletes both files.
  - The fault hook goes just before `ClipWriter.Write`.
- Read-only: `ClipClaim` (multiset in-flight ends: a duplicate `toUs` is handled), `ClipWriter` (it already deletes the part and the final file on failure), and `OutputPathAllocator`.
- Tests:
  - `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs`: `FakeFactory`, `FakeWrite` (has a gate and can complete with an error), `NextAsync` and `_document`. Add a recording `ILibraryWrites` fake.
  - `tests/ReplayBuffer.Engine.Tests/ReplayClipCaptureTests.cs:88-194`: the first test. `refused` is a single `TaskCompletionSource`, so make it a channel.
  - `tests/ReplayBuffer.Platform.FFmpeg.Tests/FFmpegLibraryTests.cs`.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- three codes, added to `All` -- the reasons that epic 5 shows (AD-22, additive)
- [x] `src/ReplayBuffer.Platform.FFmpeg/FFmpegLibrary.cs` -- `Check` maps ENOSPC and EACCES -- a real disk-full error from the muxer reaches `disk-full`
- [x] `src/ReplayBuffer.Core/Output/WriteFailures.cs`, `src/ReplayBuffer.Core/Library/LibraryWrites.cs` -- as in the Code Map -- the reason mapping and the "file being written" port (R6)
- [x] `src/ReplayBuffer.Core/Sessions/{TargetGames,SessionOrchestrator}.cs` -- `ReplayClips.Library`, Begin and End, and `clip.failed` for every failure -- 4.4 QA note 1 and R6
- [x] `src/ReplayBuffer.Engine/EngineHost.cs` -- wire `NoLibraryWrites.Instance`
- [x] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- the 1 s span refusal and `ClipWriteFault` -- 4.4 QA note 2, and fault injection for the AC
- [x] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- one test, with `saveCooldown` 0:
  - Press 1: its `FakeWrite` completes with `new IOException("disk full", unchecked((int)0x80070070))`. Expect `clip.saving`, then `clip.failed { disk-full }`; no Reset; Library calls `[Begin(path1, profile), End(path1, Failed)]`.
  - Press 2 succeeds. Expect `clip.saved`, one Reset, and `[Begin(path2), End(path2, Written)]` appended.
- [x] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- a second test, where the reserve fails: `DefaultOutputFolder` returns a path under an existing *file*, so `Directory.CreateDirectory` throws `IOException`. Expect `clip.saving`, then `clip.failed { save-failed }`, and no Library call.
- [x] New `tests/ReplayBuffer.Core.Tests/WriteFailuresTests.cs` -- one test over `ReasonOf`:
  - disk-full HResults `0x80070070` and `0x80070027` give `disk-full`;
  - `UnauthorizedAccessException` gives `access-denied`;
  - `ErrorCodeException(DiskFull)` and `ErrorCodeException(ClipTooShort)` keep their codes;
  - `ErrorCodeException(EncodeFailed)` and `InvalidOperationException` give `save-failed`.
- [x] `tests/ReplayBuffer.Engine.Tests/ReplayClipCaptureTests.cs` -- extend the first test:
  - Between `clip` and `next`, wait about 1.5 s and arm a one-shot `ClipWriteFault`. The hook writes a few bytes to `path + ".rbpart"`, then returns `new ErrorCodeException(ErrorCodes.DiskFull, "test volume full")`. Expect `clip.failed { disk-full }`.
  - `next` follows about 1.5 s later. Its existing "covers the time between `press` and `nextPress`" assert now proves the retry.
  - Then wait 1.5 s, post two presses with the same QPC, and expect one `clip.saved` and one `clip.failed { clip-too-short }`, in either order.
  - At the end, the profile folder holds exactly three `.mp4` files (all non-empty) and no `.rbpart`.
- [x] `tests/ReplayBuffer.Platform.FFmpeg.Tests/FFmpegLibraryTests.cs` -- one test: `Check(-28, "x")` throws `DiskFull`, and `Check(-1, "x")` throws `EncodeFailed`. `ErrorText` needs FFmpeg loaded; if the test can't run without it, use the existing `SkipUnless` pattern.

**Acceptance Criteria:**
- Given a running Replay Buffer, when a Clip's write fails with the disk full, then `clip.failed { reason: "disk-full" }` follows `clip.saving`, and neither a `.rbpart` nor the Clip's `.mp4` remains.
- Given a failed write between two saved Clips, when `save` is pressed again, then the new Clip starts where the failed one would have (the earlier Clip's end) and runs to the new press.
- Given any save attempt that reserved a path, when it finishes, then `BeginWrite` was called once before the write and `EndWrite` once after it, with `Written` or `Failed` to match.
- Given `saveCooldown` 0 and two presses that resolve to the same keyframe, when both finish, then one Clip is saved, the other press gets `clip.failed { reason: "clip-too-short" }`, and no empty file is written.
- Given the default run, when the 0-warning Release build, the Contracts, Core, Platform.FFmpeg and Engine suites and the NativeAOT publish run, then all pass.

## Implementation Notes

- `FFmpegLibraryTests` could not reach `FFmpegLibrary.Check` (the Code Map assumed it could): `ReplayBuffer.Platform.FFmpeg.csproj` now has `InternalsVisibleTo ReplayBuffer.Platform.FFmpeg.Tests`, like the other platform projects.
- The existing `APressUnderASecondAfterAResetIsRefusedAndAFailedWriteResetsNothing` asserted that a failed write reached no `NotSaved`; it now expects `clip.failed { save-failed }`, per this story.
- `ReplayClipCaptureTests`' `Failed` callback no longer completes the `saved` channel (a disk-full write now calls it on purpose); outcomes are asserted through `NotSaved`.
- Residual (Design Notes): the allocator's empty reserved `.mp4` exists before `BeginWrite`.

## Plan Change Log

## Review Triage Log

## Design Notes

**Where Begin and End are called.** On the orchestrator thread, around the write it already owns. The writer thread then stays free of Core ports, and one place guarantees once-each. The ticket says "before creating the `.rbpart`". The allocator's empty reserved `.mp4` (AD-21) does exist before `BeginWrite`, because the path is only known once it is reserved. Epic 7 must treat a zero-length `.mp4` without an `EndWrite` as being written, or ignore it. This is recorded as a residual.

**Why 1 s in the session.** R4 says that with less than 1 s buffered, no Clip is made. The orchestrator's 1 s check runs from the last *succeeded* Reset, so it can't see a write still in flight. The session knows the claim, so it refuses there. The same-keyframe case (a span of 0) is the extreme.

**Reasons.** `ReasonOf` keeps the list short for epic 5: `clip-too-short`, `save-busy`, `disk-full`, `access-denied`, and `save-failed` for everything else. `encode-failed` from a muxer `Check` is a write failure here, so it maps to `save-failed`.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Platform.FFmpeg.Tests -c Release --no-build` -- expected: all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- expected: all pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings
