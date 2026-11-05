---
title: 'Screen selection: the monitorRecordingScreens key and richer monitor list'
type: 'feature'
ticket: '2'
created: '2026-11-04'
status: built
baseline_revision: 'ea10553'
route: 'quick'
risk: 'low'
---

# Story 11.2: screen selection, the key and the richer monitor list

## Scope

Contracts, settings, Core and the engine's monitor list (AD-25, AD-22, AD-4). No capture change: 11.3 wires the resolver and the canvas rule into the Monitor session. No App UI change: 11.4's picker comes after 10.9, which is in progress on the Hotkeys page.

## Changes

- **Contracts.Settings:**
  - `MonitorRecordingScreens(Mode, Monitors?)` and the `MonitorScreensMode { All, Selected }` enum. The JSON forms are `{ "mode": "all" }` and `{ "mode": "selected", "monitors": [ids] }`.
  - The Global-only key `monitorRecordingScreens`, applied at the next session, default null, appended to `Keys.All` (additive, AD-22).
    - Valid values: null; All with no list; Selected with at least one non-blank id, none repeated ignoring case. Anything else is refused with `setting-invalid-value`.
  - `EffectiveSettings.MonitorRecordingScreens`, optional and not `required`, so an existing initializer and an older document still work. The resolver fills it.
  - `monitorRecordingMonitor` is unchanged. It's documented as read only while the new key is null.
- **Contracts.Messages:** `DisplayMonitor` gains the optional trailing parameters `MonitorId`, `X`, `Y`, `RefreshHz` and `Hdr`. An older App ignores the new JSON fields, and a newer App reads null from an older engine.
- **Contracts.ErrorCodes:** `monitor-missing`, `monitor-recording-no-screens` and `monitor-added-next-recording`, added to `All`.
- **Core.Monitors:**
  - `MonitorScreens.Resolve(screens, legacyDeviceName, attached)` returns the screens and the missing ids.
    - All: every attached monitor.
    - Selected: each id matched by `MonitorId`, else by `DeviceName` (a value carried over from the old key); the unmatched ids are reported missing.
    - Null: the old key exactly as `MonitorFinder.Find` did it, that monitor by name, else the primary.
  - `MonitorCanvasRule.Plan(screens, outputCodec, canEncode)` follows AD-25 as edited:
    - the bounding box at physical positions;
    - when H.264 is the Output codec and the canvas is over 4096 px, HEVC, else AV1, at full size;
    - a fit only to the codec's largest side (4096 or 8192), with even sides and no pixel budget.
    - `Fit` and the side constants live here, and `Platform.Capture.MonitorCanvas.Fit` and `debug-capture --monitors` now use them.
- **Platform.Capture** (where `monitors.list` really comes from; the ticket says Platform.Desktop):
  - `MonitorFinder.Describe` fills the monitor id from DisplayConfig's `monitorDevicePath`, and HDR from `DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO.advancedColorEnabled`.
  - X, Y, size and refresh rate come from `EnumDisplaySettings` (`dmPosition`, `dmPels*`, `dmDisplayFrequency`), never `rcMonitor`.
  - `DisplayMonitors.List` carries all of it, so `monitors.list` and `monitors.changed` have the new fields.

## Decisions (unattended)

- **No HEVC or AV1 encoder at all:** a canvas over 4096 px stays H.264, fitted to 4096. AD-25 says the canvas never falls back to H.264, but it doesn't cover a PC that can encode neither, and a scaled Recording beats none. Logged for review.
- **Screens without a position** (an engine that doesn't report X and Y): `Plan` places each to the right of the previous one, top-aligned, so the picker still shows a size.
- **The legacy fallback is unchanged:** an old `monitorRecordingMonitor` naming a monitor that isn't attached still records the primary, as 6.9 does, and isn't reported missing. Only the new key's Selected mode reports missing screens.

## Tests (lean)

- `Contracts.Tests/KeysTests`: the catalog lists `monitorRecordingScreens`. Four bad values are refused (an empty selection, a selection with no list, a repeated id, All with a list), and three good ones are accepted.
- `Core.Tests/MonitorScreensTests`:
  - the old key resolves as before (by name, null to the primary);
  - a selection follows its monitors by id when their device names swap;
  - a missing id is reported, and an all-missing selection resolves to none;
  - the canvas rule gives 6400x2160 HEVC for 2560x1440 beside 3840x2160 on H.264 output, AV1 when HEVC is absent, and 4096x1382 H.264 with neither; one 3840x2160 screen stays H.264; two 1440p give 5120x1440; three fit 4096x768.
- `Engine.Tests/MonitorScreensServeTests` (real pipe, Guid test instance, fake audio and game probe, real displays read only):
  - the key round-trips through `settings.update`, an empty selection is refused with `setting-invalid-value`, and the value is read back after a restart on the same instance, with the old key still null;
  - `monitors.list` on this PC gives each screen a distinct id, X and Y (the primary at 0,0), size and refresh rate.
- **A bug that bit:** `NothingToReselectServeTests` polled `library.json` with `File.ReadAllText` while the engine was writing it, and failed on the sharing violation in about half the full Engine runs. It now treats a locked file as not written yet.

## Verification

- `dotnet build ReplayBuffer.slnx -c Release`: 0 warnings.
- `Contracts.Tests`: 135 passed. `Core.Tests`: 165 passed. `Platform.Capture.Tests`: 6 passed, 1 explicit skipped.
- `Engine.Tests`: 192 passed, 44 explicit skipped, 0 failed, in 2 of 3 runs on a quiet PC.
  - The third run failed only `RecordingCrashTests.ARecordingKilledThreeSecondsInPlaysToItsLastFragment...`. It doesn't touch settings or monitors, and it passed alone and on the next full run.
  - Runs overlapping great-nash's full-solution test run (23:26) also timed out in unrelated tests (`TrackLayoutCaptureTests`, `LibraryServeTests` and `ReplayClipCaptureTests`).
- `pwsh tools/publish-engine.ps1`: clean, 12.0 MB.
- No GPU or Heavy runs; the App projects weren't touched.

## Log

- 2026-11-04 23:05: branch `feature/11.2-screen-selection` from develop `ea10553`.
- 23:15 to 23:30: built, tested, published. Fixed the `NothingToReselectServeTests` read race.
