---
title: 'Disk full during a Recording'
type: 'feature'
ticket: '7'
created: '2026-11-04'
status: done
baseline_revision: 'de2abb2d08355dac8b1f66f760a1a1a0ffdc7533'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/CLAUDE.md'
warnings: ['oversized']
deferred:
  - summary: >-
      A Disk-mode Replay Buffer stopped by a full disk usually restarts at the first 30 s recheck, refilling the drive.
    evidence: |-
      The ring file is DeleteOnClose and preallocated to ReplayStorageEstimate.Bytes, the same size ReplayRings.Fits asks for, so when the ring and the Recording share a drive (the default), ending the session frees exactly the room the check needs. The restart preallocates it again. This follows the ticket's literal rule ("restarts once the ring's drive has room for the ring again"); requiring a margin beyond the ring changes that rule, so it is the builder's call.
    location: >-
      src/ReplayBuffer.Engine/Sessions/ReplayRings.cs Fits; src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs RecheckRing
    severity: medium
---

<intent-contract>

## Intent

**Problem:** When a Recording's disk fills (FR-14, R5), the RecordingWriter today tries `muxer.Finish()` (which fails again on a full disk) and leaves a half-written fragment at the file's end; nothing raises the `disk-full` red X, and a Disk-mode Replay Buffer keeps running on a full drive with no notice and no way back.

**Approach:** The writer remembers the file length after each flushed fragment; on a disk-full failure it skips the trailer, closes the file and truncates it to that length (deleting it when no fragment is complete), then posts back the disk-full error as today. The orchestrator, seeing the `disk-full` stop reason, calls a new `Recordings.DiskFull` (wired to the existing `FeedbackRouter.DiskFull`, withdrawn at the next Recording start as 5.4/5.7 built). If the session's ring is Disk mode it also ends the Replay Buffer, raises `replay-stopped-disk-full`, and every 30 s checks whether the ring's drive has room for the ring again, restarting the session on the Target when it does.

## Boundaries & Constraints

**Always:** AD-19: the orchestrator alone stops and restarts the session; timers only post. AD-22: Contracts change additively — `ErrorCodes.ReplayStoppedDiskFull = "replay-stopped-disk-full"` (in `All`) and `RecordingStopReasons.DiskFull = "disk-full"` (same string `WriteFailures.ReasonOf` already returns). Never fill a real disk and no VHD: disk full is simulated with a writer test seam that throws the real `ERROR_DISK_FULL` `IOException` (HResult `0x80070070`). Tests write only to temp folders they create. Lean tests. The recheck interval is injectable so no test waits 30 s.

**Never:** No change to Clip saving's disk-full path (4.x `clip.failed`). No change to `RingFile`/`PacketRing` (the ring file is preallocated, so its own writes don't hit the full disk). No Monitor Recording work (story 9 reuses this). No sound for `replay-stopped-disk-full` (the disk-full sound already played). No new real-encode Heavy test: the writer and orchestrator are proven with synthetic streams and fakes.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Disk fills mid-Recording | writer has flushed N ≥ 1 fragments; a write throws disk-full | file closed, truncated to the end of fragment N; plays with every Track; `done` gets the disk-full error | no `Finish` attempted |
| Memory-mode ring | Recording stops with `disk-full` | `Stopped(path, "disk-full")`, `DiskFull()` once, session keeps running, a later save calls `Session.Save` | none |
| Disk-mode ring | same, snapshot `ReplayStorage.Disk` | as above, then the session is ended (`Replay` Off), `ReplayDiskFull(true)`; every recheck tick: when the Target is still the same process and the current snapshot is Memory mode or `RingFits` is true → `StartOnTarget`, `ReplayDiskFull(false)` | `RingFits` null counts as fits |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- add `ReplayStoppedDiskFull` (doc: a Disk-mode Replay Buffer stopped by a full disk; overlay warning; stays until it restarts or is dismissed) beside `DiskFull` (line ~150) and in `All`. `src/ReplayBuffer.Contracts/Messages/Recording.cs:22` `RecordingStopReasons` -- add `DiskFull = "disk-full"`. `ContractsTests` checks `All`.
- `src/ReplayBuffer.Platform.FFmpeg/DurableOutput.cs` -- test seam ctor param `long? capacity = null`: in `WritePacket`, when `_file.Position + size > capacity`, write the bytes up to the capacity, then throw `new IOException("There is not enough space on the disk.", unchecked((int)0x80070070))` (stored as `_failure` like any write failure). `Length` stays.
- `src/ReplayBuffer.Platform.FFmpeg/FragmentedMp4Muxer.cs` -- thread `long? capacity` through the internal durable ctor to `DurableOutput`. Add `internal long DurableBytes` = `_output.Length` captured after each successful `_output.Flush()` in `Start` and `CutFragment` (0 before), and `internal int Fragments` counting successful cuts.
- `src/ReplayBuffer.Platform.FFmpeg/RecordingWriter.cs` -- `internal long? CapacityBytes { get; init; }` passed to the muxer in `Open`. In `Run`'s catch: when `WriteFailures.ReasonOf(e) == ErrorCodes.DiskFull`, do not `Finish`; remember `muxer.DurableBytes` and `muxer.Fragments`; after `muxer.Dispose()` in `finally`, truncate the file to `DurableBytes` (`new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.Read)` + `SetLength`), or delete it when `Fragments == 0`; log at Warning with the bytes kept. Other failures keep today's path. Update the class summary (one sentence). `WriteFailures` is `ReplayBuffer.Core.Output` (already referenced).
- `src/ReplayBuffer.Core/Sessions/TargetGames.cs:116` `Recordings` record -- add trailing optional params: `Action? DiskFull = null` (a Recording stopped on a full disk), `Func<EffectiveSettings, bool>? RingFits = null` (whether a Disk-mode ring of the snapshot fits its drive now; null = fits), `Action<bool>? ReplayDiskFull = null` (true: a Disk-mode Replay Buffer stopped for a full disk; false: that wait ended, restarted or not), `TimeSpan? RingRecheck = null` (default `DefaultRingRecheck = 30 s`, a static on `Recordings`). Doc each `<param>`.
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs`:
  - Fields: `TargetCandidate? _ringWait`, `Timer? _ringTimer`. New private message `RingRecheck` posted by a periodic `Timer` (like `_drainTimer`, line ~558).
  - `OnRecordingWritten` (line 1263): after `recordings.Stopped(...)`, when `reason == ErrorCodes.DiskFull`: `recordings.DiskFull?.Invoke()`; then if not disposing, `_clips is not null`, `_active` is the Recording's session, Kind Game, `Spec.Snapshot.ReplayStorage == ReplayStorage.Disk`, and `_target` is set: `EndActive("disk full", null, RecordingStopReasons.DiskFull)`, set `_ringWait = _target`, start the timer, call `ReplayDiskFull(true)`, log via `Info`. The rest of the method runs as today (`_active` is null, so no `ApplySettings`).
  - `Handle`: `case RingRecheck when _ringWait is { } wait:` if `_active` is set or `_target?.ProcessId != wait.ProcessId` → `ClearRingWait()`; else resolve the current snapshot for `_target.ProfileId` (`SettingsResolver.Resolve(_currentDocument(), ...)`); if it isn't Disk mode or `RingFits` (null = true) says yes → `ClearRingWait()` then `StartOnTarget(_target)`.
  - `ClearRingWait()`: no-op when none; disposes the timer, nulls `_ringWait`, calls `ReplayDiskFull(false)`.
  - `Start(...)` (line ~425): call `ClearRingWait()` before starting (any new session ends the wait). `CommitTarget` changing the Target reaches `Start` or not; also call `ClearRingWait()` when the Target changes to another process or none.
  - `FollowTargetWindow` (line 718): with no session and `_ringWait` set, don't start.
  - `Run` (line 257) end: dispose `_ringTimer` (no callback).
  - Class summary: one sentence on disk full.
- `src/ReplayBuffer.Core/Feedback/FeedbackRouter.cs` -- `ReplayStoppedDiskFull()`: raise `FeedbackNotice(ErrorCodes.ReplayStoppedDiskFull, WarningSeverity.Warning, "replay", "The Replay Buffer stopped: its disk is full. It restarts once there is room.", null)` kept in a list; `ReplayResumed()` withdraws them. Update the class summary.
- `src/ReplayBuffer.Engine/Sessions/ReplayRings.cs` -- `public static bool Fits(EffectiveSettings snapshot, string defaultFolder, Func<string, long?> freeDisk)`: `ReplayStorageEstimate.Bytes(snapshot.Video, snapshot.TrackLayout.Tracks.Count, snapshot.BufferWindow) <= free` for `snapshot.RingPath ?? defaultFolder`; unknown free (null) counts as fits.
- `src/ReplayBuffer.Engine/EngineHost.cs:409` `new Recordings(...)` -- named args: `DiskFull: () => { recordingLog.LogWarning(...); notices.DiskFull(); }`, `RingFits: s => ReplayRings.Fits(s, Path.Combine(instance.LocalDataFolder, "ring"), EstimatePublisher.FreeDiskBytes)`, `ReplayDiskFull: stopped => { log; if (stopped) notices.ReplayStoppedDiskFull(); else notices.ReplayResumed(); }`. Use the same ring-folder expression the session options use.
- Tests to reuse: `tests/ReplayBuffer.Platform.FFmpeg.Tests/LongRecordingTests.cs` (its `Streams()`, `Feed`/`Offer`, `Probe` — copy what's needed or make them `internal static` and share). `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` (`NewRecordingOrchestrator` line 1747, `FakeRecording`, `FakeFactory`, `SetRecordingsEnabled`, `Press`, `WaitUntil`); the fake recording's done callback takes the exception. `tests/ReplayBuffer.Core.Tests/FeedbackRouterTests.cs`. `tests/ReplayBuffer.Engine.Tests/ReplayRingsTests.cs`.

## Tasks & Acceptance

**Execution:**
- [ ] Contracts -- the two additive constants -- AD-22.
- [ ] `src/ReplayBuffer.Platform.FFmpeg/{DurableOutput,FragmentedMp4Muxer,RecordingWriter}.cs` -- capacity seam, fragment bookkeeping, truncate-on-disk-full -- R5.
- [ ] `src/ReplayBuffer.Core/Sessions/{TargetGames,SessionOrchestrator}.cs` -- the four `Recordings` params and the disk-full stop, wait and recheck per the matrix -- R5.
- [ ] `src/ReplayBuffer.Core/Feedback/FeedbackRouter.cs`, `src/ReplayBuffer.Engine/Sessions/ReplayRings.cs`, `src/ReplayBuffer.Engine/EngineHost.cs` -- notice, ring fit check, wiring.
- [ ] New `tests/ReplayBuffer.Platform.FFmpeg.Tests/DiskFullRecordingTests.cs` -- one test: a writer (`FlushToDisk = false`, `CapacityBytes` ≈ a few seconds of the synthetic streams, e.g. 200 KB with 2 KB packets) fed 30 s of synthetic video + three AAC Tracks; `done` gets an error whose `WriteFailures.ReasonOf` is `disk-full`; the file exists, its length ≤ the capacity and equals a fragment boundary (ffprobe exits 0 with `-v error` and no stderr); ffprobe reports 4 streams, video duration > 1 s, each AAC duration within 0.5 s of the video's.
- [ ] `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs` -- (a) Memory mode: the fake Recording fails with the disk-full `IOException` → `Stopped(path, "disk-full")`, `DiskFull` called once, state Recording Off, Replay still Buffering, a save press still reaches the session; `ReplayDiskFull` never called. (b) Disk mode (snapshot `replayStorage: disk`), `RingRecheck` 50 ms, `RingFits` returning false then true: the session is ended, `ReplayDiskFull(true)`, then a new session starts on the Target and `ReplayDiskFull(false)`.
- [ ] `tests/ReplayBuffer.Core.Tests/FeedbackRouterTests.cs` -- one test: `ReplayStoppedDiskFull` raises `replay-stopped-disk-full` (Warning, no sound); `ReplayResumed` withdraws it; plus `StateChanged(Running)` withdraws an earlier `DiskFull` if not already covered.
- [ ] `tests/ReplayBuffer.Engine.Tests/ReplayRingsTests.cs` -- one test: `Fits` true with free bytes above the estimate, false below, true for null.

**Acceptance Criteria:**
- Given a Recording whose disk fills after some fragments, when the write fails with disk full, then the file is kept, ends at its last complete fragment, and ffprobe reads every Track with no error.
- Given a Recording on a Memory-mode Replay Buffer, when its writer reports disk full, then `recording.stopped { reason: disk-full }` is published, one `disk-full` notice (error, overlay, `diskFull` sound) is raised and is withdrawn when the next Recording starts, and a Clip save afterwards still reaches the session.
- Given a Recording on a Disk-mode Replay Buffer, when its writer reports disk full, then the Replay Buffer stops with a `replay-stopped-disk-full` notice, and once a recheck finds room for the ring it restarts on the Target and the notice is withdrawn.
- Given the default run, when the 0-warning Release build and the touched test projects run, then all pass, and the NativeAOT publish has no IL warnings.

## Implementation Notes

- `FragmentedMp4Muxer.Fragments` counts only cuts that grew the file, so a header-only file is still deleted.
- The writer test reads stream durations from ffprobe's packets (a file with no trailer has no stream durations). ffprobe's `mpeg4`/`aac` decoders print errors about the synthetic zero payloads while probing, so the test ignores those decoder lines and fails on any other stderr, which covers demuxer errors such as a torn fragment.
- `LongRecordingTests`' stream constants, `Streams()` and `Offer` are now `internal` so the disk-full test can reuse them.
- The disk-full stop also requires a `TargetCapture`, since without one `StartOnTarget` can't restart the session.

## Plan Change Log

## Review Triage Log

### 2026-11-04 — Review pass
- verdicts: 6 findings — high 0, medium 2, low 3, false 1, maybe-false 0
- findings:
  - `[medium]` `[patch]` `OnDiskFull` ended the session with stop reason `disk-full`, so on the 6.6 restart path the still-running replacement Recording was stamped `disk-full`, published as such on a clean finish, and raised a second disk-full notice and sound — fixed: the session ends with `session-ended` for any Recording still on it.
  - `[medium]` `[defer]` The Disk-mode stop usually restarts at the first recheck, since the stopped ring's DeleteOnClose file frees exactly the room `Fits` asks for — real, but it is the ticket's literal rule; a margin is the builder's decision. Deferred and queued for review.
  - `[low]` `[patch]` `ErrorCodes.ReplayStoppedDiskFull`'s doc named only a restart or a dismissal as ending the notice, while any end of the wait withdraws it — fixed: the doc lists every way the wait ends.
  - `[low]` `[patch]` EngineHost's disk-full log said the Recording "stopped at its last complete fragment" even when the writer deleted a fragmentless file — fixed: the line says only that it stopped; the writer logs which.
  - `[low]` `[patch]` The writer test allowed a length equal to the capacity, so a no-op truncation (a torn last fragment) could pass — fixed: the seam fills the file to the capacity, so the test now requires a shorter file.
  - `[false]` `[reject]` The last AC (build, tests, publish) wasn't checked by the reviewer — it reviewed by inspection only; the session runs that verification (see Auto Run Result).

## Design Notes

The Disk-mode stop is unconditional (the ticket's literal rule: "A Disk-mode one stops"), and the 30 s recheck decides when it may run again; a ring on another, healthy drive so restarts at the first recheck. The ring file is preallocated (4.7), so its own writes never hit the full disk; stopping it frees its file for the Recording's drive, and restarting needs the whole ring to fit again.

Truncating at the last flushed fragment matches the crash-safe layout (6.2): an empty moov plus whole `moof`+`mdat` pairs plays without the trailer's `mfra`, the same as a file a killed engine leaves. Shrinking a file needs no free space.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build` -- expected: pass
- `dotnet test --project tests/ReplayBuffer.Core.Tests -c Release --no-build` -- expected: pass
- `dotnet test --project tests/ReplayBuffer.Platform.FFmpeg.Tests -c Release --no-build` -- expected: pass (no GPU: synthetic streams; wait while `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` exists, as it runs FFmpeg)
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- expected: pass (the default run; EngineHost wiring changed)
- `pwsh tools/publish-engine.ps1` -- expected: no IL or AOT warnings

## Auto Run Result

- **Summary:** On a full disk (R5, FR-14) the RecordingWriter writes no trailer and cuts the file back to the end of its last flushed fragment, or deletes it if no fragment is complete, then posts the disk-full error. The orchestrator publishes `recording.stopped { reason: disk-full }` and calls `Recordings.DiskFull`, which raises the existing `disk-full` red-X notice (error, overlay, `diskFull` sound). That notice is withdrawn when the next Recording starts. A Memory-mode Replay Buffer keeps running. A Disk-mode one ends, raising `replay-stopped-disk-full`, and every 30 s (`Recordings.RingRecheck`) it checks `ReplayRings.Fits` and restarts on the Target once its ring fits.
- **Files:**
  - Contracts: `ErrorCodes.ReplayStoppedDiskFull` and `RecordingStopReasons.DiskFull`, both additive (AD-22).
  - Platform.FFmpeg: a `capacity` test seam in `DurableOutput`/`FragmentedMp4Muxer`/`RecordingWriter.CapacityBytes`, which throws the real `ERROR_DISK_FULL` IOException. The muxer gains `DurableBytes` and `Fragments`. The writer cuts the file back on disk full.
  - Core: four new optional `Recordings` params. The orchestrator gains `OnDiskFull`, `RecheckRing`, `ClearRingWait` and the `RingRecheck` timer. `FeedbackRouter` gains `ReplayStoppedDiskFull` and `ReplayResumed`.
  - Engine: `ReplayRings.Fits`, plus the `EngineHost` wiring.
  - Tests:
    - The new `DiskFullRecordingTests`.
    - 2 orchestrator tests (Memory mode, and Disk mode stop then restart).
    - 1 router test and 1 `Fits` test.
- **Review:** quick lens, 6 findings.
  - 4 patched: 1 medium (the restart replacement was stamped `disk-full`) and 3 low (Contracts doc, log line, test length pin).
  - 1 medium deferred: the Disk-mode stop usually restarts at the first recheck, because the freed ring is exactly the room `Fits` asks for. This is the ticket's literal rule, so a margin is the builder's call.
  - 1 rejected as false: the reviewer didn't run verification.
- **Follow-up review recommended:** false. 1 medium and 0 high were patched.
- **Verification (after the patches):**
  - Release build: 0 warnings.
  - Contracts: 118 passed.
  - Core: 119 passed.
  - Platform.FFmpeg: 37 passed, 2 explicit skipped. `DiskFullRecordingTests` was also run on its own and passed.
  - Engine: 169 passed, 28 explicit skipped.
  - `tools/publish-engine.ps1`: no IL or AOT warnings.
  - No real disk was filled and no VHD was used.
- **Residual risks:**
  - The restart loop described in the deferral above.
  - If `StartOnTarget` fails after a fit, there is no retry.
  - Nothing was run against a real full disk.
  - The synthetic payloads prove the container's layout, not decoding.
