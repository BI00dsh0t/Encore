---
title: 'Output Settings and encoder probing'
type: 'feature'
ticket: '10'
created: '2026-10-26'
status: 'done'
baseline_revision: '5d52a4a892d2f213233a45be044b4ebef43078f6'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The `video` key (codec, resolution, fps, bitrate, rate control) exists in the catalog, but nothing reads it: every session is h264_nvenc, CBR 20 Mbps, 60 fps, at the window's size. Nothing tells the App which codecs this PC can encode (R2, AD-4, AD-14).

**Approach:** The engine probes the hardware encoders once at startup and publishes what it can encode, with the common resolutions and the 120 fps ceiling, as a new `videoOptions` snapshot field. A session builds its encoder from the snapshot's `video` value: the encoder the probe chose for that codec, at that resolution, fps, bitrate and rate control.

## Boundaries & Constraints

**Always:**
- Probe order per codec: NVENC, AMF, QSV, each opened on D3D11 frames on adapter 0 and fed one frame. The first that works is that codec's encoder. H.264 falls back to libx264 only when no hardware H.264 encoder works. HEVC and AV1 have no software fallback.
- Offers carry `mayNotPlayEverywhere` = true for HEVC and AV1, false for H.264. H.264 stays the catalog default.
- The probe runs off the pipe's start path. `videoOptions` starts as `probed: false` with no codecs, and one `capture.videoOptions` event sets it. A failed probe (FFmpeg missing) publishes `probed: true` with no codecs and logs a warning; `serve` keeps running.
- The offered resolutions are fixed: 1280×720, 1920×1080, 2560×1080, 2560×1440, 3440×1440, 3840×1600, 3840×2160. The key keeps accepting any even size up to 8192, since the list is what's offered, not a limit.
- The `video` key keeps refusing fps above 120 (`setting-invalid-value`), as it does today.
- The encoder's output size is the `video` resolution. Until story 11 scales, a frame is copied at the top left: a larger window is cropped, and a smaller one is padded black.
- GOP = fps (1 s). Packet duration fallback = 1/fps. WGC's `MinUpdateInterval` = 1/fps.
- If a session's codec isn't offered (a hand-edited or copied document), it falls back to H.264 and logs a warning.
- HEVC is written with the `hvc1` tag.
- `debug-capture` keeps its output, report and exit codes. With no new option, its resolution is the window's size, made even, so `CaptureEndTests` passes unchanged. It gains `--codec`, `--size WxH`, `--fps`, `--bitrate` and `--encoder <ffmpeg name>`; the last forces an encoder for tests.
- 0 warnings. AOT-clean, with no new suppressions. No managed allocation per frame (AD-7). P/Invoke stays only in `Platform.*`. Contracts change only additively.

**Never:** GPU scaling, letterboxing, the frame pacer or the colour-path work, which are story 11's. Refusing a codec in `settings.update`. A UI for Output Settings. Per-codec resolution limits. Choosing the probe adapter per game.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Probe on RTX 5090 | `serve` starts | `capture.videoOptions` offers h264, hevc and av1 via `*_nvenc`; HEVC and AV1 marked | No error expected |
| No hardware H.264 | NVENC, AMF and QSV all fail for H.264 | H.264 offered via libx264 | No error expected |
| FFmpeg missing | probe can't load | `probed: true`, no codecs | warning logged |
| 144 fps | `settings.update` sets video fps 144 | refused, revision unchanged | `setting-invalid-value` |
| Unoffered codec in snapshot | session starts with av1 on a PC with no AV1 | file is H.264 | warning logged |
| 3440×1440 @ 120 | session with that `video` | file is 3440×1440 and the codec asked for; frame rate about 120 when the window updates faster | No error expected |

</frozen-after-approval>

## Code Map

- `src/ReplayBuffer.Contracts/Settings/Keys.cs:135`, `SettingValues.cs` -- `Keys.Video` and `IsValidVideo` (fps ≤ `VideoSettings.MaxFps` = 120). Reuse as is.
- `src/ReplayBuffer.Contracts/Messages/Capture.cs`, `Engine.cs` -- `CaptureMessages`, `StateSnapshot` (add field with default, like `Hotkeys`), `EngineJsonContext`. `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` requires an event per non-identity snapshot field and a sample payload for each event.
- `src/ReplayBuffer.Platform.FFmpeg/VideoEncoder.cs` -- hardcodes h264_nvenc, 20 Mbps CBR, GOP 60, framerate 60, duration `TimeBase/60`. It takes `VideoEncoderOptions(Preset, Tune)`.
- `src/ReplayBuffer.Platform.FFmpeg/D3D11VAFrames.cs`, `FragmentedMp4Muxer.AddStream` (codec tag).
- `src/ReplayBuffer.Platform.Capture/D3D11Device.cs` -- `Create(0, logger)` gives adapter 0 for the probe. `CopyFrame` already crops or pads. `WindowCapture.MinUpdateInterval` is a static 1/60.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` `StartOutput` -- sets the output size from content (`VideoSize.EvenFloor`) and builds `VideoEncoder`. Change it to use `_spec.Snapshot.Video`.
- `src/ReplayBuffer.Engine/TracerCapture.cs:86` -- resolves against `SettingsDocument.Empty`. `CommandLine.cs` holds the debug-capture options.
- `src/ReplayBuffer.Engine/EngineHost.cs` `Start` -- the composition root. Start the probe after `server.Start`, publish through `hub`, and await or cancel it in `DisposeAsync`.
- `tests/ReplayBuffer.Engine.Tests/SessionCaptureTests.cs`, `EngineServeTests.cs` -- the patterns for the pwsh test window plus ffprobe, and for serve plus `EngineClient`.
- Builder's PC: RTX 5090, i9-12900KF (no iGPU), two 2560×1440 monitors at 239 Hz (5120×1440 virtual, max track 5140×1460). VLC 3.0.23 is at `C:\Program Files (x86)\VideoLAN\VLC\vlc.exe`. The FFmpeg build has every `*_nvenc`, `*_amf` and `*_qsv`, plus libx264.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/Messages/Capture.cs` (+ `Engine.cs`) -- `VideoOptions(bool Probed, IReadOnlyList<CodecOffer> Codecs, IReadOnlyList<ResolutionOption> Resolutions, int MaxFps)`, `CodecOffer(VideoCodec Codec, string Encoder, bool MayNotPlayEverywhere)`, `ResolutionOption(int Width, int Height)`, a `VideoOptions.Unprobed` default, the `capture.videoOptions` event, and the snapshot field. Update `MessagesTests` samples.
- [x] `src/ReplayBuffer.Platform.FFmpeg/EncoderProbe.cs` -- `Probe(nint d3d11Device, ILogger)` → the offers, per the rules. Candidate names are a static table.
- [x] `src/ReplayBuffer.Platform.FFmpeg/VideoEncoder.cs` -- the settings record gains encoder name, codec, fps, bitrate and rate control. Map rate control per family: NVENC `rc`; AMF `rc` cbr/vbr_peak; QSV bit_rate/maxrate; x264 `nal-hrd cbr`. Preset and tune apply only to NVENC. Make libx264 work: copy into a staging texture and map it, `sws_scale` BGRA→yuv420p into a pooled native frame, with nothing managed allocated per frame.
- [x] `src/ReplayBuffer.Platform.Capture/WindowCapture.cs` -- `MinUpdateInterval` from a constructor `fps` argument.
- [x] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- output size, fps, codec and encoder from the snapshot and the probe result (`IVideoEncoderCatalog`, probed once and cached). Fall back to H.264. `CaptureSessionOptions.Encoder` forces one encoder.
- [x] `src/ReplayBuffer.Engine/EngineHost.cs` -- the async startup probe, publishing `capture.videoOptions`.
- [x] `src/ReplayBuffer.Engine/CommandLine.cs`, `TracerCapture.cs` -- the new debug-capture options, building the session's document `video` (refusals give exit code 2 like the other bad options).
- [x] `tests/ReplayBuffer.Platform.FFmpeg.Tests/EncoderProbeTests.cs` -- the probe set equals the set of encoders an `ffmpeg.exe -f lavfi -i nullsrc=s=640x360 -frames:v 1 -c:v <name> -f null -` run accepts, across nvenc, amf and qsv. libx264 is offered only with no hardware H.264.
- [x] `tests/ReplayBuffer.Engine.Tests/OutputSettingsTests.cs` -- the story verify. `serve`'s `videoOptions` equals the probe oracle. `settings.update` at fps 144 is refused. For each offered codec, an in-process session against a 3440×1440 pwsh window repainting at ≥ 200 Hz writes 3440×1440 at that codec, with 90–125 fps measured from the PTS. A forced libx264 session writes a playable H.264 file. Each file plays in VLC (headless; the case skips if VLC is absent). Also cover `CommandLine` for the new options.

**Acceptance Criteria:**
- Given the builder's PC, when the verify tests run, then `videoOptions` offers exactly h264, hevc and av1 (NVENC), 144 fps is refused, and each offered codec's 3440×1440 at 120 fps capture plays in VLC.
- Given a test window, when `debug-capture` runs with no new options, then every `CaptureEndTests` and `SessionCaptureTests` case passes unchanged.
- Given the solution, when `dotnet build ReplayBuffer.slnx`, `dotnet test --solution ReplayBuffer.slnx` and `pwsh tools/publish-engine.ps1` run, then there are 0 warnings and all tests pass.

## Implementation Notes

- The probe reuses `VideoEncoder` itself (its muxer is now nullable: packets are counted and discarded), so a candidate "works" through exactly the session's open-and-encode path. It opens 1280×720 BGRA D3D11 frames and silences FFmpeg's log while it runs, since AMF and QSV fail loudly on a PC without them; a session's FFmpeg warnings during a probe are lost too.
- `VideoEncoderCatalog` probes per codec, lazily, once (`Lazy<string?>` per codec), on its own D3D11 device on adapter 0. `serve` probes all three after `server.Start`; `debug-capture` and a session that starts before the probe ends probe only the codec they need, so `debug-capture` with no new option opens only NVENC H.264, as before.
- The software path is chosen when the encoder has no D3D11 `hw_frames_ctx` config (libx264): FFmpeg's `av_hwframe_transfer_data` does the staging-texture copy and map, then `sws_scale_frame` converts BGRA→yuv420p (BT.601 limited, tagged so) into a preallocated frame; `av_frame_make_writable` reallocates natively only if the encoder still holds it. libx264 uses `veryfast`, plus `nal-hrd cbr` for CBR.
- VBR peaks at 1.5× the bitrate (NVENC `rc vbr`, AMF `vbr_peak`, QSV maxrate > bit_rate, x264 VBV).
- `debug-capture` with no `--size` reads the window's size from a WGC item (`WindowCapture.SizeOf`), made even, and keeps the tracer's 60 fps and 20 Mbps. A `--codec`/`--size`/`--fps`/`--bitrate` refusal is checked against `Keys.Video` too. An unknown `--encoder` fails at session start with `encode-failed` (exit 1), since it needs FFmpeg loaded.
- `CommandLineTests` had a case expecting `--fps 60` to be refused as an unknown option; it now expects `--fps 144` to be refused.
- Verified on the builder's PC: the probe and `serve` offer exactly h264/hevc/av1 via `*_nvenc` (AMF and QSV fail, matching `ffmpeg.exe`); 3440×1440 at 120 fps measured 108.6 (AV1), 112.9 (H.264) and 111.2 (HEVC) fps; every file played in VLC 3.0.23. VLC needs `--no-video-title-show` to keep OSD blend errors out of the log.
- Review round: `HotkeyServeTests.AnInjectedPressArrivesWithinAMillisecondAndIsLogged` failed once at 1.12 ms against 1 ms. Its presses ran while `serve`'s startup probe was opening encoders. The test now waits for `videoOptions.probed` before pressing, which measures a settled engine. The probe runs once at engine start, never during play. That test can't be re-run green right now: on 2026-10-26 the unmodified `develop` test also stopped receiving any injected press on this PC, so the machine's input state is blocking it, not the code. Re-run it before the release.

## Plan Change Log

## Review Triage Log

**Step 3 matrix audit:** the rows "No hardware H.264" and "FFmpeg missing" had no test that ran on the builder's PC, whose NVENC means neither branch can run there. `EncoderProbe.Choose` (the pure selection) and an internal per-codec probe constructor on `VideoEncoderCatalog` were added, with tests: `EncoderProbeTests.WithNoHardwareH264OnlyH264FallsBackToLibx264` and `VideoEncoderCatalogTests`.

**Pass 1 (quick, 2026-10-26):** high 0, medium 2, low 3, false 0, maybe-false 1. One more was rejected as the plan's own choice.

| # | Finding | Verdict | Route | Evidence / action |
|---|---------|---------|-------|-------------------|
| 1 | Every in-process `EngineHost.Start` runs the real probe, whose `capture.videoOptions` takes a seq at an unpredictable moment; `IpcTests` count every seq | medium | patch | `IpcTests.StartHost` asserts exact seq values (`Seq == 3`, `Hub.State.Seq == 0`). With FFmpeg present it's a race; without it, the probe publishes before connect every time. Fix: `EngineHostOptions.ProbeEncoders`, set off in `IpcTests` and `AppSupervisorTests`. |
| 2 | One codec's probe throwing drops every offer; `Lazy` caches the exception for the engine's life; `ResolveEncoder` lets it escape, so there's no H.264 fallback | medium | patch | `ProbeAll` wrapped the whole loop in one try, and `ExecutionAndPublication` caches factory exceptions. Fix: catch per codec, cache only results, and in `ResolveEncoder` treat a throw for a non-H.264 codec as "no encoder". Test: `ACodecWhoseProbeThrowsIsNotOfferedNotCachedAndProbedAgain`. |
| 3 | The probe's adapter-0 devices go against AD-7's "one D3D11 device" | low | reject | The frozen intent asks for the probe on adapter 0 at startup. The devices are short-lived, one per codec, and disposed after the probe. Probing on the session's device would tie the offer list to a session. |
| 4 | The probe feeds BGRA D3D11 frames while the oracle lets `ffmpeg.exe` convert, so QSV/AMF may disagree with the oracle | maybe-false | defer | Can't be settled here: only NVENC works on this PC, and it accepts both inputs. Running the tests on an Intel or AMD PC would settle it. Logged in `deferred-work.md`. |
| 5 | `debug-capture` now probes h264_nvenc before opening it again; `WindowCapture.SizeOf` failing reaches `Program`'s generic catch | low | reject | The probe costs a few hundred ms and the output is the same. A window vanishing between `ResolveWindow` and `SizeOf` is rare, and the inaccurate note is in this plan, which triage doesn't edit. |
| 6 | `WindowCapture.SizeOf` leaves its `GraphicsCaptureItem` to the finalizer | low | reject | It runs once per `debug-capture`, the item isn't `IClosable`, and the finalizer releases it. |
| 7 | `ResolveEncoder`'s fallback holds only when `EncoderFor` returns null, not when it throws | medium | patch | Same root cause as #2, fixed with it. |

## Design Notes

**Why there's no codec refusal in `settings.update`.** A document outlives the GPU it was written on, and AD-4's store validates by type and range. The offer list is the gate for the App. The session's H.264 fallback covers the rest.

**VLC check.** `vlc.exe -I dummy --vout=dummy --aout=dummy --play-and-exit --stop-time=2 -vv --file-logging --logfile=<f> <file>`. Passing means the log names a video decoder module and has no `decoder error` or `no suitable decoder` lines. Try it on a known-good file first, and adjust the flags if VLC 3.0.23 needs it.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx` -- expected: 0 warnings, 0 errors
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass, with no new skips on the builder's PC
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL2xxx/IL3xxx warnings
</content>
</invoke>
