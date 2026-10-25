---
title: 'Tracer: game window and two audio devices to a playable fragmented MP4'
type: 'feature'
ticket: '1'
created: '2026-10-25'
status: 'ready-for-dev'
baseline_revision: ''
route: 'full'
route_source: 'auto'
risk: 'high'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Nothing exists yet: no solution, no toolchain pins, and no proof that a game window plus two independent-clock audio devices can reach a playable file through the chosen stack (PLAN.md §6 Path A).

**Approach:** Scaffold the solution per the spine's Structural Seed, then wire the thinnest direct path in the engine: WGC window capture → BGRA D3D11 frames into `h264_nvenc` → fragmented MP4, with one WASAPI loopback Output Device and one WASAPI capture Input Device as two AAC streams, all stamped in QPC and aligned in the muxer, started by an engine debug command that names a window.

## Boundaries & Constraints

**Always:**
- Reference graph exactly as AD-2; `unsafe`, P/Invoke, COM and FFmpeg calls only in `ReplayBuffer.Platform.*`.
- Win32/COM via CsWin32 (`CsWin32RunAsBuildTask`, `DisableRuntimeMarshalling`, `allowMarshaling: false`); WGC via the TFM's SDK projection with ABI hand-offs through `FromAbi`; FFmpeg via `FFmpeg.AutoGen.Abstractions` + `FFmpeg.AutoGen.Bindings.DynamicallyLinked` 9.0.1.1 only.
- QPC ticks are the only timebase (AD-6): video from `SystemRelativeTime`, audio from `pu64QPCPosition` (100 ns units, so `qpc = v × QPF / 10^7`); conversion to stream time only at the muxer.
- The session anchor is the first video frame's QPC: every stream's PTS 0 is that instant.
- Game window opened only through WGC with a window item; no process handles beyond `PROCESS_QUERY_LIMITED_INFORMATION`, no hooks, no injection (AD-12).
- fMP4 with a fragment at least every 1 s; GOP 1 s; AAC-LC 48 kHz, one stream per device, stream `title` = the endpoint's friendly name, the Output Device's stream default (AD-9).
- Every error code kebab-case and listed in `Contracts.ErrorCodes`.
- Dev runs use instance `ReplayBuffer-Dev`.

**Never:**
- No SessionOrchestrator, AudioDeviceHub, IPC, settings store, encoder/muxer ports, resampler, scaler or pacer (stories 4–12).
- No NativeAOT publish gate (spike 3); no performance tuning or measurement (spike 2).
- No x264/AMF/QSV path, no CPU readback of video frames.
- No FFmpeg binaries committed; the agent does not download FFmpeg itself.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Happy path | `debug-capture --window <substr> --seconds 30`, one matching window, default (eConsole) devices | 30 s fMP4: 1 H.264 + 2 AAC streams, path printed, exit 0 | — |
| Ctrl+C early | capture running | stops, flushes encoders, writes trailer; file plays | — |
| No / ambiguous window | substring matches 0 or >1 windows | lists candidates, exit non-zero | `window-not-found` / `window-ambiguous` |
| FFmpeg DLLs absent | `native/ffmpeg` not fetched | clear message naming `tools/fetch-ffmpeg.ps1`, exit non-zero | `ffmpeg-missing` |
| Device by name | `--output-device`/`--input-device` substring | that endpoint used; none / many → endpoints listed, exit non-zero | `audio-device-not-found` / `audio-device-ambiguous` |
| Idle loopback | nothing playing on the Output Device | silence fills to `now − 50 ms`, loopback only; stream stays continuous | — |
| Late packet after idle fill | real packet stamped inside already-filled silence | overlapping samples trimmed, no gap | — |
| Audio gap / overlap | packet QPC ahead of / behind expected position by > 5 ms | gap filled with silence / overlap trimmed | — |
| Bad audio stamp | `TIMESTAMP_ERROR`, zero, or backwards stamp | previous stamp + frames used | — |
| Pre-anchor data | audio stamped before the first video frame | trimmed | — |
| Frame rate | game renders above 60 fps | `MinUpdateInterval` 1/60 s; any frame whose PTS isn't above the last is dropped | — |
| Window resized | content size changes | pool recreated; output size stays the first frame's size (rounded down to even); target cleared, then the overlap copied | — |
| Window closed | item `Closed` | capture ends and the file finalizes, exit 0 | — |

</frozen-after-approval>

## Code Map

Greenfield: the repo holds only planning docs. New tree per the Structural Seed.

- `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `ReplayBuffer.slnx` -- SDK 10.0.402 `rollForward: latestPatch`; TFM `net10.0-windows10.0.26100.0`, `SupportedOSPlatformVersion` 10.0.26100.0, C# 14, nullable, warnings as errors; central versions.
- `tools/fetch-ffmpeg.ps1` -- BtbN `autobuild-2026-09-30-13-08`, `ffmpeg-n9.0.2-17-g2a571b6068-win64-gpl-shared-9.0.zip`, SHA-256 `3da6c7b60bb9ccd73ec5b5e815ba804a0879eb362ba0e3beebce50174c022696` (matches the release's `checksums.sha256` and GitHub's digest).
- `src/ReplayBuffer.Contracts` -- `ErrorCodes`, `Instance`.
- `src/ReplayBuffer.Media` -- `AudioTimeline` (pure QPC alignment).
- `src/ReplayBuffer.Platform.Capture` -- D3D11 device, WGC window capture.
- `src/ReplayBuffer.Platform.Audio` -- WASAPI clients, endpoint listing and matching.
- `src/ReplayBuffer.Platform.FFmpeg` -- D3D11VA device, NVENC and AAC encoders, fMP4 muxer.
- `src/ReplayBuffer.Platform.Windows` -- window enumeration and matching.
- `src/ReplayBuffer.Engine` -- `Program`, `TracerCapture`.
- `src/ReplayBuffer.Core`, `src/ReplayBuffer.App` (WPF, no UI), `bench/ReplayBuffer.Bench` -- empty placeholders.

## Tasks & Acceptance

**Execution:**
- [ ] Root files plus `.gitignore` (`bin/`, `obj/`, `.vs/`, `native/ffmpeg/`); packages incl. `xunit.v3` 4.0.2, `xunit.runner.visualstudio` 4.0.1, `Microsoft.NET.Test.Sdk` 18.11.0. Any `NoWarn` is listed in Implementation Notes with its reason.
- [ ] `tools/fetch-ffmpeg.ps1` -- download, verify the hash (on mismatch delete and exit non-zero), extract `bin/*` to `native/ffmpeg/`, check that the DLL names the bindings import exist; idempotent.
- [ ] `src/ReplayBuffer.Contracts/ErrorCodes.cs`, `Instance.cs` -- the matrix's codes; instance `ReplayBuffer` only when the process image is under `%LOCALAPPDATA%\Programs\ReplayBuffer\`, else `ReplayBuffer-Dev`; folders `%APPDATA%\<instance>` and `%LOCALAPPDATA%\<instance>`.
- [ ] `src/ReplayBuffer.Media/AudioTimeline.cs` -- packets (QPC, frames, flags) → contiguous 48 kHz stereo float from the anchor; 5 ms tolerance; stamp fallback; `DATA_DISCONTINUITY` is a gap; `FillTo(qpc)`.
- [ ] `src/ReplayBuffer.Platform.Capture/*` -- adapter owning the window's monitor (else adapter 0); device with `BGRA_SUPPORT | VIDEO_SUPPORT` and `SetMultithreadProtected(TRUE)`; WinRT device via `CreateDirect3D11DeviceFromDXGIDevice`; item via `RoGetActivationFactory` + `IGraphicsCaptureItemInterop.CreateForWindow` (hard-coded IIDs; release raw pointers after `FromAbi`); texture via `IDirect3DDxgiInterfaceAccess`; free-threaded pool, `MinUpdateInterval` 1/60 s, `IsBorderRequired=false` attempted and logged.
- [ ] `src/ReplayBuffer.Platform.Audio/*` -- event-driven shared mode, `LOOPBACK` for render, `AUTOCONVERTPCM` to 48 kHz float stereo, 100 ms buffer, MMCSS "Pro Audio"; 20 ms wait timeout calls `FillTo(now − 50 ms)` on loopback only; started after the anchor is set.
- [ ] `src/ReplayBuffer.Platform.FFmpeg/*` -- bindings init (DLLs beside the exe; fail with `ffmpeg-missing`); D3D11VA device over the engine's device; BGRA pool with `BIND_RENDER_TARGET`, size ≥ NVENC surfaces + 4; `h264_nvenc` p5 CBR 20 Mbps, `framerate` 60/1, time_base 1/90000, g 60, bf 0; AAC-LC FLTP through an audio FIFO; mov `frag_keyframe+empty_moov+default_base_moof`, `frag_duration` 1 s, `max_interleave_delta` 500 ms; one lock per encoder, only writes under the mux lock.
- [ ] `src/ReplayBuffer.Platform.Windows/WindowFinder.cs` -- visible titled top-level windows; pure matching function; `--hwnd` override.
- [ ] `src/ReplayBuffer.Engine/Program.cs`, `TracerCapture.cs` -- `debug-list`; `debug-capture --window|--hwnd [--seconds 30] [--output] [--output-device] [--input-device]`; Serilog to `%LOCALAPPDATA%\<instance>\logs\engine-*.log` plus console; default output `%LOCALAPPDATA%\<instance>\debug\<sanitized title>_<yyyy-MM-dd_HH-mm-ss>.mp4` with `_2` suffixes; copies `native/ffmpeg/*` to the output dir.
- [ ] `tests/*.Tests` for Contracts, Media, Platform.Capture, Platform.Audio, Platform.Windows, Platform.FFmpeg, Engine -- codes kebab-case and unique; instance rule; every timeline row; window and endpoint matching; even-size rule; argument parsing and file-name sanitizing.
- [ ] `tests/ReplayBuffer.Platform.FFmpeg.Tests` -- synthetic 3 s: a white flash frame and a click in both tones at t = 1 s; ffprobe asserts 1 h264 + 2 aac 48 kHz, titles, default disposition, ≥ 3 `moof`, `start_time` and flash/click offsets within one frame. Needs an NVIDIA GPU; skipped when `native/ffmpeg` is absent; DLLs copied to the test bin.

**Acceptance Criteria:**
- Given FFmpeg fetched, when `dotnet build` and `dotnet test` run, then both succeed with no warnings and the synthetic-encode test runs and passes.
- Given a tampered download, when the fetch script runs, then it exits non-zero and leaves no extracted files.
- Given a window title that matches nothing, or a device name that matches two endpoints, when `debug-capture` runs, then it lists the candidates and exits non-zero with the matching code.
- Given a capture running, when Ctrl+C is pressed or the window is closed, then the file finalizes and ffprobe reads all three streams.
- Given a running game, the BEACN mixer and BEACN Mic, when the builder runs `debug-capture` for 30 s, then the .mp4 plays in VLC with game video and both audio streams in sync.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

- Color path for the tracer is X1 (BGRA texture straight into NVENC): no shader, no readback. Spike 2 measures it; story 11 applies the final choice.
- Logging: Serilog (`Serilog.Extensions.Logging` 10.0.0, `Serilog.Sinks.File` 7.0.0), configured in code. A throwaway `PublishAot` console checks for IL2xxx/IL3xxx and the result goes in Implementation Notes; on warnings, swap in a minimal in-house MEL file provider.
- Instance folders: `%LOCALAPPDATA%\ReplayBuffer-Dev`, read as the spine's `ReplayBuffer[-<instance>]` with the instance name used whole.
- AAC priming (1024 samples) and fMP4's lack of edit lists are checked by the start-time assertion; compensate in the muxer if it fails.
- Test projects: Core, App and Bench get theirs with their first code.
- Tracer shortcuts, replaced later (stories 6, 11, 12, and the AD-9/AD-21 owners in epic-replay-buffer-clips): encoding runs on the capture threads; no resampler (30 s of drift stays within a frame); a resize clips rather than scales; FFmpeg's file protocol (no `FILE_SHARE_DELETE`, no `BeginWrite`/`EndWrite`, AD-9); file names don't go through `OutputPathAllocator` (AD-21).

## Verification

**Commands:**
- `pwsh tools/fetch-ffmpeg.ps1` -- expected: hash OK, DLLs and `ffprobe.exe` in `native/ffmpeg/`.
- `dotnet build ReplayBuffer.slnx` -- expected: 0 warnings, 0 errors.
- `dotnet test ReplayBuffer.slnx` -- expected: all pass, FFmpeg test not skipped.
- `dotnet run --project src/ReplayBuffer.Engine -- debug-list` -- expected: windows and endpoints listed.

**Manual checks (if no CLI):**
- Builder: `debug-capture --window <game> --seconds 30` with BEACN devices; play in VLC, check video and both audio tracks in sync.
