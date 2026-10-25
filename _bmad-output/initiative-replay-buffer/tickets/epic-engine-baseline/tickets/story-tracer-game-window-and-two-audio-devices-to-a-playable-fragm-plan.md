---
title: 'Tracer: game window and two audio devices to a playable fragmented MP4'
type: 'feature'
ticket: '1'
created: '2026-10-25'
status: done
baseline_revision: '78d4d558556c65257fdf14f48dd52748d7882ea9'
route: 'full'
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
- [x] Root files plus `.gitignore` (`bin/`, `obj/`, `.vs/`, `native/ffmpeg/`); packages incl. `xunit.v3` 4.0.2, `xunit.runner.visualstudio` 4.0.1, `Microsoft.NET.Test.Sdk` 18.11.0. Any `NoWarn` is listed in Implementation Notes with its reason.
- [x] `tools/fetch-ffmpeg.ps1` -- download, verify the hash (on mismatch delete and exit non-zero), extract `bin/*` to `native/ffmpeg/`, check that the DLL names the bindings import exist; idempotent.
- [x] `src/ReplayBuffer.Contracts/ErrorCodes.cs`, `Instance.cs` -- the matrix's codes; instance `ReplayBuffer` only when the process image is under `%LOCALAPPDATA%\Programs\ReplayBuffer\`, else `ReplayBuffer-Dev`; folders `%APPDATA%\<instance>` and `%LOCALAPPDATA%\<instance>`.
- [x] `src/ReplayBuffer.Media/AudioTimeline.cs` -- packets (QPC, frames, flags) → contiguous 48 kHz stereo float from the anchor; 5 ms tolerance; stamp fallback; `DATA_DISCONTINUITY` is a gap; `FillTo(qpc)`.
- [x] `src/ReplayBuffer.Platform.Capture/*` -- adapter owning the window's monitor (else adapter 0); device with `BGRA_SUPPORT | VIDEO_SUPPORT` and `SetMultithreadProtected(TRUE)`; WinRT device via `CreateDirect3D11DeviceFromDXGIDevice`; item via `RoGetActivationFactory` + `IGraphicsCaptureItemInterop.CreateForWindow` (hard-coded IIDs; release raw pointers after `FromAbi`); texture via `IDirect3DDxgiInterfaceAccess`; free-threaded pool, `MinUpdateInterval` 1/60 s, `IsBorderRequired=false` attempted and logged.
- [x] `src/ReplayBuffer.Platform.Audio/*` -- event-driven shared mode, `LOOPBACK` for render, `AUTOCONVERTPCM` to 48 kHz float stereo, 100 ms buffer, MMCSS "Pro Audio"; 20 ms wait timeout calls `FillTo(now − 50 ms)` on loopback only; started after the anchor is set.
- [x] `src/ReplayBuffer.Platform.FFmpeg/*` -- bindings init (DLLs beside the exe; fail with `ffmpeg-missing`); D3D11VA device over the engine's device; BGRA pool with `BIND_RENDER_TARGET`, size ≥ NVENC surfaces + 4; `h264_nvenc` p5 CBR 20 Mbps, `framerate` 60/1, time_base 1/90000, g 60, bf 0; AAC-LC FLTP through an audio FIFO; mov `frag_keyframe+empty_moov+default_base_moof`, `frag_duration` 1 s, `max_interleave_delta` 500 ms; one lock per encoder, only writes under the mux lock.
- [x] `src/ReplayBuffer.Platform.Windows/WindowFinder.cs` -- visible titled top-level windows; pure matching function; `--hwnd` override.
- [x] `src/ReplayBuffer.Engine/Program.cs`, `TracerCapture.cs` -- `debug-list`; `debug-capture --window|--hwnd [--seconds 30] [--output] [--output-device] [--input-device]`; Serilog to `%LOCALAPPDATA%\<instance>\logs\engine-*.log` plus console; default output `%LOCALAPPDATA%\<instance>\debug\<sanitized title>_<yyyy-MM-dd_HH-mm-ss>.mp4` with `_2` suffixes; copies `native/ffmpeg/*` to the output dir.
- [x] `tests/*.Tests` for Contracts, Media, Platform.Capture, Platform.Audio, Platform.Windows, Platform.FFmpeg, Engine -- codes kebab-case and unique; instance rule; every timeline row; window and endpoint matching; even-size rule; argument parsing and file-name sanitizing.
- [x] `tests/ReplayBuffer.Platform.FFmpeg.Tests` -- synthetic 3 s: a white flash frame and a click in both tones at t = 1 s; ffprobe asserts 1 h264 + 2 aac 48 kHz, titles, default disposition, ≥ 3 `moof`, `start_time` and flash/click offsets within one frame. Needs an NVIDIA GPU; skipped when `native/ffmpeg` is absent; DLLs copied to the test bin.

**Acceptance Criteria:**
- Given FFmpeg fetched, when `dotnet build` and `dotnet test` run, then both succeed with no warnings and the synthetic-encode test runs and passes.
- Given a tampered download, when the fetch script runs, then it exits non-zero and leaves no extracted files.
- Given a window title that matches nothing, or a device name that matches two endpoints, when `debug-capture` runs, then it lists the candidates and exits non-zero with the matching code.
- Given a capture running, when Ctrl+C is pressed or the window is closed, then the file finalizes and ffprobe reads all three streams.
- Given a running game, the BEACN mixer and BEACN Mic, when the builder runs `debug-capture` for 30 s, then the .mp4 plays in VLC with game video and both audio streams in sync.

## Implementation Notes

- **No `NoWarn` anywhere.** `dotnet build ReplayBuffer.slnx`: 0 warnings, 0 errors.
- **Tests run on Microsoft.Testing.Platform.** xunit.v3 4.x ships only MTP v2, and the .NET 10 SDK refuses VSTest for MTP projects, so `global.json` sets `test.runner: Microsoft.Testing.Platform`. `xunit.runner.visualstudio` and `Microsoft.NET.Test.Sdk` stay referenced as planned (IDE discovery). Commands: `dotnet test --solution ReplayBuffer.slnx`, or `--project <csproj>`.
- **Logging: Serilog swapped for an in-house provider** (Design Notes rule). A throwaway `PublishAot` console with `Serilog.Extensions.Logging` 10.0.0 + `Serilog.Sinks.File` 7.0.0 + `Serilog.Sinks.Console` 6.1.1 raised IL2104, expanded to IL2072 in `Serilog.Capturing.PropertyValueConverter.CreatePropertyValue` (destructuring; reachable from any log call). `src/ReplayBuffer.Engine/Logging/TextLoggerProvider.cs` replaces it: daily `engine-yyyyMMdd.log` plus console (warnings+ on stderr), written by one background thread so capture threads never touch the file. Its own `PublishAot` check: no IL2xxx/IL3xxx, and the AOT binary logs correctly. Serilog packages are no longer referenced. (Local AOT publishes need `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on PATH for `vswhere`.)
- **AAC priming compensated in the muxer.** The first synthetic run put the click 1024 samples (21.3 ms) late: the empty-moov fMP4 has no edit list, so the priming packet played. `FragmentedMp4Muxer.Write` drops any packet that ends at or before time 0. Measured after the fix: every stream's `start_time` is 0, and the flash and both clicks land within one frame of 1.0 s.
- **Stream titles on read-back.** FFmpeg's mp4 muxer stores `title` in the track's udta `name` box, and its demuxer reads it back as `tags.name`, not `tags.title`. The muxer also sets `handler_name` to the same text (what VLC shows). The test asserts both.
- **Window close needs a watchdog.** `GraphicsCaptureItem.Closed` never arrived in the headless engine, even when the window's process was killed: the creating thread has no message loop. `WindowCapture` keeps the event and also polls `IsWindow` every 250 ms; either one raises `Closed` once.
- **Name matching:** a single exact (case-insensitive) title, endpoint friendly name, or endpoint ID wins before substring matching. Otherwise "Microphone (BEACN Mic)" could never be picked when "Microphone (BEACN Mic) 2" exists. None/many still list candidates and fail with the matrix codes.
- **`--seconds` counts from the anchor QPC**, not from when the encoders finished opening (about 0.6 s later on first run).
- **Engine process sets its console title** to `<instance> engine`, so its own terminal tab can't match a `--window` query.
- **Video packets get a nominal 1/60 s duration** when NVENC leaves it 0. It only sizes the last sample of a fragment, and it silences the mov muxer's "estimating duration" warning.
- **WASAPI failures hold the stream silent.** A failure while capturing logs `audio-device-failed`, and the device's timeline is then filled with silence (`now − 50 ms`), so the file stays aligned. A second failure, in the fill itself, stops that stream.
- **Engine.Tests `CaptureEndTests`** spawn a throwaway WinForms window (pwsh) and the real engine on the default eConsole devices, which briefly opens the default mic. They check that closing the window, and a real `CTRL_C_EVENT`, each finalize a file ffprobe reads as h264 + aac + aac. The test host ignores Ctrl+C and children inherit that, so a pwsh launcher re-enables it for the engine only.
- **Verified on the builder's PC** (RTX 5090, driver 617.42): `debug-list` lists windows and every BEACN endpoint; a 5 s Task Manager capture; a mid-capture resize (pool recreated, output size kept, overlap copied, black below, checked on an extracted frame); window close; and Ctrl+C. All but `debug-list` used the silent Steam virtual devices.
- **Keyframes are forced on time** (review fix). Capture is variable-rate, so NVENC's 60-frame GOP alone gives a 2 s GOP at 30 fps, and longer for a static window. `VideoEncoder` forces an IDR (`forced-idr`) once 1 s of PTS has passed since the last keyframe. Test: 10 fps for 3.5 s gives keyframes at 0, 1, 2 and 3 s.
- **Trim and AOT analyzers are on** (review fix, AD-14). `IsAotCompatible` is set for every src project except the WPF App and Bench, so IL2xxx/IL3xxx fail `dotnet build`. Currently 0 warnings.

## Plan Change Log

## Review Triage Log

Lens `quick`, iteration 0. Findings were verified against the code and patched directly, since this build ran with no implementation subagent.

| # | Finding | Verdict | Route | Evidence |
|---|---------|---------|-------|----------|
| 1 | `VideoEncoder`: GOP is 60 frames, not 1 s, under variable-rate capture | medium | patch | WGC delivers frames only on change, so `gop_size` 60 spans 2 s at 30 fps. Fixed: an IDR is forced at ≥ 90000 PTS since the last keyframe; `KeyframesComeAtLeastEverySecondEvenAtALowFrameRate` passes. |
| 2 | Encoder/muxer failure on an audio thread logged as a device fault, exit 0 | medium | patch | `FFmpegLibrary.Check` throws `ErrorCodeException(encode-failed)` up through `AudioTimeline` into `WasapiCapture.Run`'s catch; `ReportAudioFaults` only warned. Fixed: an `encode-failed` fault now fails the run with that code. |
| 3 | `DisposeAll` frees the encoders and muxer before joining the WASAPI threads | high | patch | On any throw between `StartSession` and the explicit `Stop()` calls, `finally` disposed `_inputAudio`/`_outputAudio`/`_muxer` while their writer threads ran: a native use-after-free. Fixed: captures are disposed (threads joined) first. Also, the first-frame timeout now takes the session gate, so a session still opening its encoders is not misreported as `capture-failed`. |
| 4 | Dropping the AAC priming packet windows in the first 21 ms | low | reject | Real but negligible: one 1024-sample fade-in at file start. This is the compensation the Design Notes call for, because an empty-moov fMP4 has no edit list, and the alternative delays all audio by 21 ms. The fix would need a different container strategy. |
| 5 | Trim/AOT analyzers not enabled (AD-14) | medium | patch | No project set `IsAotCompatible`/`EnableAotAnalyzer`. Fixed in `Directory.Build.props`; `dotnet msbuild -getProperty` confirms both analyzers on Engine and Platform.*, and the build has 0 warnings. |
| 6 | `VideoSurface` allocated per frame (AD-7) | low | reject | Real, but the WGC projection already allocates per frame (`TryGetNextFrame`'s wrapper, `MarshalInterface.FromManaged`), so the managed path cannot be allocation-free here. The hot-path budget belongs to spike 2 and story 11; no user-visible harm. |
| 7 | Verification section still says `dotnet test ReplayBuffer.slnx` | low | reject | Its fix is an edit to this build's plan (rejected by rule). Implementation Notes record the MTP form: `dotnet test --solution ReplayBuffer.slnx`. |

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
