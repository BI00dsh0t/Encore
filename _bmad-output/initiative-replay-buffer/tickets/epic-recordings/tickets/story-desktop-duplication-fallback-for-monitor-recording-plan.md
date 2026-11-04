---
title: 'Desktop Duplication fallback for Monitor Recording'
type: 'feature'
ticket: '10'
created: '2026-11-04'
status: 'in-progress'
baseline_revision: '319cac7f750dce44745dc1f46f0472c32c5c98c4'
route: 'full'
route_source: 'auto'
risk: 'medium'
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

**Problem:** A Monitor Recording (6.9) captures only through a WGC monitor item; when that item can't be created or fails to start, the session fails and nothing is recorded (AD-12 names DXGI Desktop Duplication as the monitor-only fallback).

**Approach:** Platform.Capture gains a frame-source port (`ICaptureSource`: `Start`, `SetFps`, `Closed`, `Failed`, `Dispose`) that `WindowCapture` implements, and a new `DesktopDuplicationCapture` that implements it for one monitor: frames stamped with DXGI's `LastPresentTime` (already QPC, AD-6), delivered through the same `CapturedFrameHandler`, so the session's scaler, pacer and encoder (1.11) are unchanged and the file is the same. The Monitor session tries WGC first; if creating or starting it throws, it disposes it, opens Desktop Duplication on the same device and monitor, and raises a one-time `warning.raised { code: monitor-capture-fallback, overlay: false }`. `ACCESS_LOST` (desktop switch, mode change) recreates the duplication inside the source, without ending the session or its Recording.

## Boundaries & Constraints

**Always:** The fallback is chosen only in the `SessionKind.Monitor` path of `CaptureSession`; a game (window) session never constructs `DesktopDuplicationCapture` — a WGC failure there fails as today. Contracts change additively only (AD-22): `ErrorCodes.MonitorCaptureFallback = "monitor-capture-fallback"` appended to `All`. Frames are delivered at most `2 × fps` a second (as WGC's `MinUpdateInterval`), throttled by waiting before the next `AcquireNextFrame` so DXGI accumulates updates and no content is lost. The warning is raised once per session (not again on `ACCESS_LOST`). App windows stay excluded: `WDA_EXCLUDEFROMCAPTURE` also hides them from Desktop Duplication, so no extra masking is added. Test fault hook: `CaptureSessionOptions.WgcFault` (`Func<Exception?>?`, called before any WGC source is created; non-null is thrown), like `RestartFault`. Test hook to simulate `ACCESS_LOST`: internal `DesktopDuplicationCapture.SimulateAccessLost()` reached through internal `CaptureSession.SimulateCaptureAccessLost()`.

**Never:** no Desktop Duplication for game capture (AD-12, NFR-3); no change to the WGC path's behaviour; no Core or orchestrator change (the session hides the fallback); no cursor drawing (DD reports the pointer separately and it is not composited) and no rotation handling; no new settings; real-capture tests only as `[Fact(Explicit = true)]` + `[Trait("Category","Heavy")]` under the bench lock.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| WGC works | Monitor session opens | WGC monitor item as in 6.9; no warning | — |
| WGC fails at create/start | `WgcFault` set, or WGC throws | WGC source disposed; DD source started on the session's device; `monitor-capture-fallback` raised once; Recording starts at the first DD frame | DD creation failing too fails the session (capture-failed) as today |
| ACCESS_LOST | mid-Recording | duplication released and recreated (retry every 100 ms while `DuplicateOutput` fails); frames resume; same Recording file; no new warning | monitor's output gone (not found on the adapter) → `Closed` (session ends `window closed`, Recording stopped as for a WGC item close) |
| Static desktop | no desktop change | no new frames; the pacer repeats the last frame (as with WGC) | — |
| Game session with `WgcFault` | window session opens | `Start` throws; no DD source, no fallback warning | as today |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Platform.Capture/WindowCapture.cs` -- add `: ICaptureSource`; nothing else changes. `CapturedFrameHandler` (:23) is the shared per-frame delegate.
- `src/ReplayBuffer.Platform.Capture/ICaptureSource.cs` (new) -- the port: `void Start(); void SetFps(int fps); event Action? Closed; event Action<Exception>? Failed;` + `IDisposable`.
- `src/ReplayBuffer.Platform.Capture/DesktopDuplicationCapture.cs` (new) -- `DesktopDuplicationCapture(D3D11Device device, nint hmonitor, int fps, CapturedFrameHandler onFrame, ILogger logger)`. Find the output: `IDXGIDevice` (QI from `device.Device`) → `GetAdapter` → `EnumOutputs` until `GetDesc().Monitor == hmonitor` → QI `IDXGIOutput1` → `DuplicateOutput(device)`. A dedicated background thread (`Start`) loops: wait until half a slot since the last delivered frame; `AcquireNextFrame(100 ms)`; `WAIT_TIMEOUT` → loop; `ACCESS_LOST` (or the simulate flag) → release, recreate (retry 100 ms until it works, the output is gone → `Closed`, or disposed); success with `LastPresentTime == 0` (pointer-only) → `ReleaseFrame`, loop; else QI `ID3D11Texture2D` from the `IDXGIResource`, size from its desc, `onFrame(LastPresentTime, texture, size)` (exceptions → log + `Failed`, as WindowCapture), release, `ReleaseFrame`. `Dispose` stops and joins the thread, then releases the duplication. Use `D3D11Device.Device` (internal) for the native device. All failures use HRESULTs from CsWin32's raw signatures.
- `src/ReplayBuffer.Platform.Capture/NativeMethods.txt` -- add `IDXGIOutput1`, `IDXGIOutputDuplication`, `IDXGIResource`, `DXGI_OUTDUPL_FRAME_INFO`, `DXGI_ERROR_ACCESS_LOST`, `DXGI_ERROR_WAIT_TIMEOUT`.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- `_capture` (:192) and every local `WindowCapture? old/capture` become `ICaptureSource`; `OnClosed(WindowCapture)` (:1492) takes `ICaptureSource`. `NewCapture` (:897): window sessions call `_options.WgcFault` then `new WindowCapture` as now (fault throws). New `StartMonitorCapture()` used by `Open` (:300–310) for a Monitor session: try { fault; `WindowCapture.ForMonitor`; `Start()` } catch → dispose it, log Warning, `new DesktopDuplicationCapture(...)`, `Start()`, `_options.CaptureFallback?.Invoke(message)`; record `CaptureApi` ("wgc" | "desktop-duplication", internal, for tests). Open's `capture.Start()` stays for window sessions. `SimulateCaptureAccessLost()` internal. Options (:60–95): add `WgcFault` and `CaptureFallback` (`Action<string>?`, null ignores). Update the class doc.
- `src/ReplayBuffer.Engine/EngineHost.cs` (:356) -- `CaptureFallback = message => warnings.Raise(ErrorCodes.MonitorCaptureFallback, WarningSeverity.Warning, "recordings", message, overlay: false)`.
- `src/ReplayBuffer.Contracts/ErrorCodes.cs` (:245–320) -- the new code with a doc comment, appended to `All`.
- Tests: `tests/ReplayBuffer.Engine.Tests/MonitorRecordingCaptureTests.cs` -- reuse `NewRig` (add `wgcFault` and `fallback` params into `CaptureSessionOptions`), `RecordMonitorAsync`, `ExcludedWindowScript`, `KeepingFactory`, `BenchLock`; `SessionTestKit.VideoPts`, `Probe`, `FrameStats`, `AssertDecodesCleanly`.

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- add `MonitorCaptureFallback` -- the one-time warning's code (AD-22 additive).
- [ ] `src/ReplayBuffer.Platform.Capture/ICaptureSource.cs`, `WindowCapture.cs`, `NativeMethods.txt`, `DesktopDuplicationCapture.cs` -- the port and the DD source per the Code Map.
- [ ] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs`, `src/ReplayBuffer.Engine/EngineHost.cs` -- fallback selection in the Monitor path only, options, warning wiring, test hooks.
- [ ] `tests/ReplayBuffer.Engine.Tests/MonitorRecordingCaptureTests.cs` -- Heavy/Explicit, under the bench lock: (1) with `WgcFault` set, a Monitor Recording plays: `CaptureApi == "desktop-duplication"`, one fallback message, every Global Track present with duration within 0.2 s of the video's, video PTS steps all one 30 fps slot, decodes cleanly; (2) the same with `SimulateCaptureAccessLost()` mid-Recording (twice, 1 s apart): one file, decodes cleanly, frames span the whole Recording; (3) `ExcludedWindowScript` magenta window absent from a DD-path Monitor Recording; (4) a game window session started with `WgcFault` set throws from `Start`, and no fallback message is raised. The ErrorCodes registry test (if it enumerates constants) passes with the new code.

**Acceptance Criteria:**
- Given WGC's monitor item forced to fail, when the Monitor Record toggle is pressed twice, then a file in `Monitor Recordings` plays with every Global Track in sync and a constant 30 fps, and `monitor-capture-fallback` was raised once with `overlay: false`.
- Given a Desktop Duplication Monitor Recording, when `ACCESS_LOST` is simulated mid-Recording, then the Recording continues into the same single file, which plays.
- Given a window marked `WDA_EXCLUDEFROMCAPTURE` on the monitor, when it is recorded through Desktop Duplication, then none of its pixels appear.
- Given a game session whose WGC fails, when it starts, then it fails and Desktop Duplication is never selected.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

- **Capture exclusion (5.9):** the ticket states `WDA_EXCLUDEFROMCAPTURE` also applies to Desktop Duplication (Windows 10 2004+ hides such windows from every capture path, DD included), so the App windows need no extra handling; Heavy test (3) proves it on this PC, and the 6.9 Human Check for the real Main App and Overlay covers both paths.
- **Cost against WGC (research `capture-cost-regression.md`):** WGC alone cost 4.3 % of the game's FPS on this Windows build, in DWM's per-item capture work. Desktop Duplication hands over DWM's composed desktop surface instead of an extra per-item copy, so its cost profile may differ; but it only runs for a Monitor Recording when WGC fails, never for game capture, so it can't change NFR-1's game-capture figure. Not measured here (out of the story's scope); a `bench` stage for it would be a separate spike.
- Throttling by waiting before `AcquireNextFrame` (not by dropping acquired frames) keeps the latest desktop image: DXGI accumulates updates into the next acquire.
- The DD source runs on its own thread (no WGC frame pool), and frames come only when the desktop changes; the pacer already repeats the last frame for a static window.
- The cursor isn't drawn by either path; rotation isn't handled (the frame is the output's unrotated surface), noted as a residual risk.

## Verification

**Guard:** never build or test while `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` exists; Heavy tests take the lock themselves.

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build` -- all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- all pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only --filter-class "*MonitorRecordingCaptureTests"` -- Heavy fallback tests pass
- `pwsh tools/publish-engine.ps1` -- 0 IL warnings
