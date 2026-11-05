---
title: 'Tracer: the matchSource key and a game chain at the source size'
type: 'feature'
ticket: '1'
created: '2026-11-05'
status: built
baseline_revision: 'c51f4be'
route: 'quick'
risk: 'medium'
---

# Story 12.1: the matchSource key and a game chain at the source size

## Changes

- **Contracts (additive, AD-22):** `VideoSettings` gains a trailing optional `MatchSource = false`, serialized as `matchSource` and **left out of the JSON when false** (`JsonIgnore(WhenWritingDefault)`).
  - So every existing value, `Keys.Video`'s default JSON included, reads and writes byte for byte as before.
  - `IsValidVideo` is unchanged, so `width` and `height` stay required as the fallback size.
- **Core:** `Core.Output.SourceOutputSize.For(width, height, codec)` is the pure rule.
  - Under 640×360 (either side) it gives null.
  - Otherwise it gives `MonitorCanvasRule.Fit(w, h, MonitorCanvasRule.MaxSide(codec))`: even sides, aspect kept, H.264 4096 px, HEVC and AV1 8192 px. The codec is never switched.
- **Engine:**
  - `EncoderChain.OutputSize` is settable before `Build` through `SizeTo` (a built chain refuses).
  - `CaptureSession.StartOutput` records the first frame's content size and sizes the first chain from it, before `Build` creates the D3D11VA pool and `FrameScaler`. The anchor line then logs the source size.
  - Every later chain of the session (a restart, a Reset's chain switch, a prepared chain, a GPU recovery) goes through the same `SizedToSource`. So a chain built after the anchor keeps the source size and doesn't drop back to the snapshot's (12.1 covers one size for the session; a size change is a later story's, R3).
  - Only a game session with `matchSource` on changes. With it off, for the Monitor session, and for a game under 640×360, the chain keeps the snapshot's size. Frame rate, codec, bitrate and rate control always come from the snapshot.
- **`debug-capture --match-source`** (dev): sets `matchSource` on the command line's video, for the Heavy check.
- **Tests' `BenchLock`** moved out of `MonitorRecordingCaptureTests` into its own internal file, so the new Heavy test takes the same lock.

## Tests

- **Default:**
  - `Contracts.Tests/KeysTests.AVideoValueWithoutMatchSourceReadsAsOffAndOneWithItRoundTrips`: an existing value reads `false` and writes back unchanged; `true` round-trips.
  - `Core.Tests/SourceOutputSizeTests`: 2560×1440 stays 2560×1440; 5120×1440 is 4096×1152 under H.264 and 5120×1440 under HEVC; odd sides round down; 600×300 is null.
  - `Engine.Tests/MatchSourceTests`:
    - a game chain with `matchSource` on and a 2560×1440 source is 2560×1440 at the snapshot's 60 fps;
    - off, the Monitor session, or a 600×300 source keeps 1920×1080;
    - over a real pipe on a Guid test instance, an old `settings.json` with no `matchSource` loads with it false, and `settings.update` with it true round-trips.
- **Heavy, written and not run** (nights only): `MatchSourceTests.ARealCaptureOfA2560By1440WindowWithMatchSourceRecordsAt2560By1440`.
  - It runs `debug-capture --match-source` on a borderless, animated 2560×1440 window at the primary screen's top-left for 30 s at 60 fps, with a 1280×720 fallback size.
  - It asserts exit 0, that the log's anchor line reads `output 2560x1440`, a 2560×1440 file, and a clean decode. The test takes `.bench-running` itself.

  `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only --filter-method "*ARealCaptureOfA2560By1440WindowWithMatchSourceRecordsAt2560By1440"`

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings.
- Default suites, 0 failed: Contracts 137, Core 170, Media 102, Platform.Capture 8, Engine 201, App 197.
- `pwsh tools/publish-engine.ps1`: clean, 12.1 MB.
- No Heavy, GPU or bench runs (day rules).

## Log

- 2026-11-05 09:20: branch `feature/12.1-match-source-tracer` from develop `c51f4be`. Built and verified by 09:55.
