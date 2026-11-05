---
title: 'Engine: a Monitor Recording of several screens, composited'
type: 'feature'
ticket: '3'
created: '2026-11-04'
status: in-progress
baseline_revision: '2aef49f'
route: 'full'
risk: 'high'
---

# Story 11.3: a Monitor Recording of several screens, composited

## Shape (AD-25 as edited, design-multi-monitor.md's engine shape)

- **Media `MonitorCompositor`** (new): platform-neutral, behind an `ICanvasSurface`.
  - Each source's frame is drawn into its rectangle as it arrives, so the canvas always holds every source's latest frame.
  - `Tick`, the slot clock, hands the canvas on **at most once per output slot**, stamped with the tick's QPC, and only when something changed. Stamps always increase, and the hand-off can't fill, whatever the sources' rates.
  - `Remove` blacks a source's rectangle. The canvas never changes size.
- **Platform.Capture `CompositeCapture`** (the spike's, reworked):
  - one capture per screen on the Monitor session's device: WGC, else that screen's Desktop Duplication (6.10);
  - a `CanvasSurface` (the BGRA canvas): `CopyFrameTo` for a frame of the screen's size, and, for a mode change, a per-place `FrameScaler` into a staging texture, then the copy, so the frame scales into the same place;
  - the slot clock: a thread on a high-resolution waitable timer at the output fps, calling `Tick` at each slot;
  - `Drop(device)`, and `Closed` once no screen is left;
  - per-source arrival statistics, logged at dispose.
  - `MonitorCanvas.Place(screens)` maps the resolved `DisplayMonitor`s to placements from their display modes now, and skips a screen that's gone or whose mode reads 0x0 (11.2's QA note).
- **Core:**
  - `SessionSpec.Screens` (optional `init`): the orchestrator resolves `monitorRecordingScreens` with 11.2's resolver over the display port's list, a 0x0 mode counting as not attached.
    - A missing chosen screen raises `monitor-missing` and is skipped.
    - None left refuses the start with `monitor-recording-no-screens` through the usual failure notices (`Recordings.Failed`).
    - With the key null, `Screens` is null and the old `monitorRecordingMonitor` path runs unchanged.
  - `ICaptureSession.MonitorDevices` and `DropMonitor`, as defaults. On a display change the orchestrator:
    - drops each removed screen of several (`monitor-removed`, the Recording goes on);
    - ends the Recording as `monitor-removed` only when every recorded screen is gone;
    - under All screens, raises `monitor-added-next-recording` once per arriving screen. The canvas is untouched.
- **Engine `CaptureSession`:**
  - With two or more screens, `PlanCanvas` runs before the encoder resolves. It applies Core's `MonitorCanvasRule.Plan` with the probe's codecs (HEVC, else AV1, over 4096 px, at full size) and replaces the snapshot's video codec and size, so the one chain encodes the whole canvas.
  - A canvas that has to stay H.264 and be scaled raises the new `monitor-recording-scaled` (11.2's QA note; additive in Contracts).
  - One screen, or one left of several: today's single-monitor path, unchanged.
  - `StartMonitorCapture` builds the composite, so a GPU reset (8.3) rebuilds every source and the compositor on the new device, and the Recording continues in a new file.
  - A screen that falls back to Desktop Duplication raises `monitor-capture-fallback`, as 6.10 does.
- **Engine host:** the session's `Warn` goes to the warnings hub. `monitor-missing` and `monitor-added-next-recording` aren't Overlay messages (the codes' docs).
- **One encoder chain** for the Monitor Recording, whatever the screen count.

## Tests

- **Default:**
  - `Media.Tests/MonitorCompositorTests`:
    - two sources (144 and 60 fps) draw into their own rectangles, and the pacer gets 59 to 60 canvases in a second, with increasing stamps; a slot with nothing new hands nothing on;
    - a removed source's rectangle is cleared and its later frames are ignored.
  - `Core.Tests/SessionOrchestratorScreensTests`:
    - All screens starts one session with every screen, and an arrival raises one notice and leaves the session alone;
    - a removed screen is dropped with `monitor-removed` and the Recording ends only with the last;
    - a missing screen is skipped with `monitor-missing`, a 0x0 screen counts as missing, and none connected refuses the start with `monitor-recording-no-screens` and starts no session.
  - The canvas rule (HEVC over 4096 px from H.264 Output) is 11.2's `MonitorScreensTests`.
- **Heavy** (`MonitorRecordingCaptureTests`, on the builder's two screens, under the bench lock):
  - `ATwoScreenMonitorRecordingOf60SecondsPlaysToTheEndWithEveryTrackAndBothScreensPlaced`:
    - All screens, 60 s; one encoder chain;
    - HEVC at the plan's 6400x2160; both Tracks; in step at 30 fps for at least 59 s; decodes cleanly;
    - the canvas gap under the 1440p screen is black in every frame (placed, not stretched).
  - `AScreenOnDesktopDuplicationStillComposites`: one screen forced to Desktop Duplication; one fallback notice naming it; same size; in step; decodes; gap black.
  - `AGpuResetRebuildsTheCompositeAndTheMonitorRecordingContinuesInASecondFile`: a simulated device loss gives a `RecordingRestart`; one chain after it; both files at the canvas size decode cleanly.

## Verification

See the Log.

## Log

- 2026-11-04 23:35: branch `feature/11.3-composited-monitor-recording` from develop `2aef49f`.
- 23:50: Heavy, one at a time under the bench lock: the 3 new tests passed, then the whole `MonitorRecordingCaptureTests` class (13/13).
- 2026-11-05 00:00: first cost run (6 conditions × 2 passes) was contaminated: other processes opened up to 8 NVENC sessions in pass 1, and capture alone read 4.7 %. Its composite runs showed the clock-only hand-off failing under encode load: 46 to 50 canvases a second, 18 to 24 % duplicates.
- 00:10: fix: the canvas is handed on outside the drawing lock, by the first frame of a new slot (from its source's thread) or by the clock for a late change; still at most one per slot with increasing stamps. A 30 s check of capture-monhevc: 1.0 % duplicates, 0.9 % missed slots, 9 hand-off drops, 0 non-increasing stamps (spike: 1 to 2 %, 1 to 1.5 %, 160 to 946, about 2,000).
- 00:15: the clean re-measure aborted after 3 runs: the GPU load exited at 00:17 (exit 0), and other NVENC users appeared again at 00:16 to 00:17. Then the builder stopped all tests.
- **Left (when tests resume):** the stage-split cost run (baseline, capture, capture-monone, capture-monhevc, -monhevccopy, -monhevcwgc, 2 passes × 45 s) on a quiet PC; the Heavy class again after the hand-off change; the default suites; the NativeAOT publish; then the epic Notes and READY FOR QA. Sync develop (11.4, 10.11) and run 10.11's GlossaryTests on the combined branch.
