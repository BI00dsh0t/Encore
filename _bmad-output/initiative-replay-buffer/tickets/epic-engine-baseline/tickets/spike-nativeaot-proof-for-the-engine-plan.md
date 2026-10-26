---
title: 'NativeAOT proof for the engine'
type: 'chore'
ticket: '3'
created: '2026-10-26'
status: 'built'
baseline_revision: 'a8fd03e44421ba32295edbb3e46af0be5d006311'
route: 'oneshot'
route_source: 'auto'
risk: 'high'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Architecture AD-14 and the spine's Deferred section make a NativeAOT engine the target. That target holds only if a `PublishAot` build has no IL2xxx/IL3xxx warnings and still encodes and muxes. Nothing in the repo publishes the engine that way or proves it, and story 1.13's publish layout waits on the answer (epic R10).

**Approach:** Make NativeAOT the engine's publish mode. Add one script that publishes it reproducibly, and point the engine's existing end-to-end tests at the published binary. Those are `serve`/IPC, the window capture that ffprobe reads back, and the exit codes. Record the outcome as a dated Decision in the epic's Notes, with the binary size and the time from start to first frame (PLAN.md §6.3 row).

A probe on 2026-10-26, before planning, already passed:
- `dotnet publish -r win-x64 -p:PublishAot=true` built with 0 IL warnings, giving a 9.3 MB exe.
- `debug-list` enumerated windows and MMDevice endpoints.
- A 5 s `debug-capture` wrote H.264 3840×2088 plus 2 AAC tracks (296 frames, 5.03 s), with 0 frames dropped.

So the expected Decision is "the engine ships NativeAOT". The framework-dependent fallback is recorded only if the gated runs below fail.

## Boundaries & Constraints

**Always:**
- Warnings stay errors. ILC inherits `TreatWarningsAsErrors` (`IlcTreatWarningsAsErrors`, which passes `--warnaserror`), so any IL2xxx/IL3xxx fails the publish.
- The publish is `win-x64`, `Release`, with the FFmpeg DLLs beside the exe, as they already come via `Directory.Build.targets`.
- Ordinary `dotnet build` and `dotnet test` keep running the JIT engine, unchanged.

**Never:**
- Don't touch the App, supervision or `CopyEngineBesideApp` (story 1.8's area). The App + engine install layout is story 1.13's.
- No new interop, and no suppression attributes (`UnconditionalSuppressMessage`, `TrimmerRootDescriptor`) to silence warnings.
- No push.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error handling |
|---|---|---|---|
| Publish | `pwsh tools/publish-engine.ps1` | AOT exe and FFmpeg DLLs in `publish/engine` (gitignored); prints the exe size | Exits non-zero on any build or IL warning |
| vswhere not on PATH | A shell without the VS Installer folder | The script adds `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` before publishing | Missing `native/ffmpeg` → points to `tools/fetch-ffmpeg.ps1` and fails |
| Tests on AOT | `REPLAYBUFFER_ENGINE_DIR=publish/engine` | Engine tests that launch the exe use that folder, ffprobe included | The variable names a folder with no exe → the test fails, saying so |
| Tests by default | The variable is unset | Unchanged: the exe beside the test assembly | — |

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.Engine/ReplayBuffer.Engine.csproj` -- add `<PublishAot>true</PublishAot>`. Leave out `RuntimeIdentifier`, so the build output folder and the App's copy are unchanged.
- New `tools/publish-engine.ps1` -- `-Output` (default `publish/engine`). Puts vswhere on PATH and runs `dotnet publish src/ReplayBuffer.Engine -c Release -r win-x64 -o <Output>`. Same style as `tools/fetch-ffmpeg.ps1`.
- `.gitignore` -- `publish/`.
- `tests/ReplayBuffer.Engine.Tests/` -- `EngineServeTests.EngineExe`, `CaptureEndTests.BinFolder` and `EngineProcessTests.EngineFolder` all use `Path.GetDirectoryName(typeof(CommandLine).Assembly.Location)`. Route them through one new `EngineBinaries.Folder` that honours `REPLAYBUFFER_ENGINE_DIR`.
- `_bmad-output/.../epic-engine-baseline/epic-engine-baseline.md` -- add the Notes Decision line.
- Don't touch: `tests/ReplayBuffer.App.Tests` (story 1.8), or the Platform.* interop.

## Tasks & Acceptance

**Execution:**
- [x] `ReplayBuffer.Engine.csproj` -- `PublishAot` -- makes NativeAOT the engine's declared publish mode (Stack table).
- [x] `tools/publish-engine.ps1`, `.gitignore` -- a reproducible publish -- story 1.13 builds on it.
- [x] `tests/ReplayBuffer.Engine.Tests/EngineBinaries.cs` plus the 3 test files -- the engine folder override -- runs the existing e2e tests against the AOT binary.
- [x] Run the gates (see Verification), plus a `--stats` capture for first-frame time.
- [x] Epic Notes -- a dated Decision: AOT or fallback, the warnings count, the exe size, first frame, and the test results.

**Acceptance Criteria:**
- Given a clean worktree with `native/ffmpeg`, when `pwsh tools/publish-engine.ps1` runs, then it exits 0 with no IL2xxx/IL3xxx in its output.
- Given `REPLAYBUFFER_ENGINE_DIR` pointing at that output, when the Engine tests run, then `EngineServeTests` (IPC snapshot, settings update, shutdown) and `CaptureEndTests` (an ffprobe-readable fMP4 with all 3 streams) pass against the AOT exe.
- Given the epic file, when it is read, then Notes holds a 2026-10-26 Decision stating whether the engine ships NativeAOT, with its evidence.

## Implementation Notes

Oneshot: about 60 lines across 7 files. The investigation and a probe publish already showed the AOT build is clean; the work is making it repeatable and recorded.

- Changed: `ReplayBuffer.Engine.csproj` (`PublishAot`), new `tools/publish-engine.ps1`, `.gitignore` (`publish/`), new `tests/ReplayBuffer.Engine.Tests/EngineBinaries.cs`. `EngineServeTests`, `CaptureEndTests` and `EngineProcessTests` now take their folder from `EngineBinaries.Folder`.
- Gates: build has 0 warnings. The JIT suite passes 298/298. The publish exits 0 with no IL2/IL3 (8.9 MB). The Engine tests pass 128/128 with `REPLAYBUFFER_ENGINE_DIR=publish/engine`. Pointed at a bogus folder instead, 13 fail with the override's message, which shows the 13 exe-launching tests really ran the AOT exe.
- Startup: `startupToAnchorMs` is 289/256/245 ms on AOT against 1061/411/454 ms on JIT Debug, from 5 s captures of a visible window, interleaved.
- Surprise, not AOT: `ffmpeg -f null` decoding of a capture prints "non monotonically increasing dts" on both the AOT and the JIT files. The file's own packet timestamps strictly increase (90 kHz, VFR). The CLI maps VFR frames onto the null encoder's coarse time base. Audio decodes clean. Not recorded as a defect.
- Surprise: Git Bash `sed -i` rewrote `CaptureEndTests.cs`'s CRLF endings. It was reverted, and the edit redone with the editor.
- The Decision is in the epic's Notes, dated 2026-10-26.

## Review Triage Log

- Quick lens, 1 pass: 1 medium patched, 1 low patched, 1 deferred, 1 false.
- medium, patch: under `REPLAYBUFFER_ENGINE_DIR`, a folder with only the exe would let the FFmpeg tests skip, not fail. `EngineBinaries` now also requires `avcodec-63.dll` and `ffprobe.exe` there. The gated run had 0 skipped, so the recorded evidence stands.
- medium, defer: `publish/engine` carries `ffmpeg.exe`, `ffplay.exe` and the PDBs, because the `Directory.Build.targets` glob predates this spike. Story 1.13 owns the layout; it's in `deferred-work.md`, and the Decision names it.
- false: the epic's 2026-10-25 tray Decision line was rewritten by a line-ending change. It was already restored to its original LF before triage; the epic diff is additions only.
- low, patch: the plan's tasks were unticked and its status was stale. Fixed here, at finalize.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx` -- expected: 0 warnings, 0 errors.
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass (JIT, unchanged).
- `pwsh tools/publish-engine.ps1` -- expected: exit 0, no IL2/IL3 warnings.
- `$env:REPLAYBUFFER_ENGINE_DIR='publish/engine'; dotnet test --project tests/ReplayBuffer.Engine.Tests` -- expected: all pass against the AOT exe.

**Manual checks:**
- `publish/engine/ReplayBuffer.Engine.exe debug-capture --window <visible window> --seconds 10 --stats <f>.json`: the .mp4 plays in the NFR-4 players; the stats give start→first frame.
