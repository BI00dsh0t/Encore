---
title: "Audio Tracks: default devices, the first-run seed, presets and per-device meters"
ticket: 4
status: review
---

# Plan: 10.4 Audio Tracks model work

**Intent:** the model the 10.5 editor binds to (AD-24, R5's model part, R6), with no Contracts break (AD-22) and no change to the engine's Track Layout model.

## Done

- [x] **`AudioDevice.isDefault`** (Contracts, additive): an optional last parameter, default false, written only when true, so today's `devices.changed` JSON is byte-identical for every other device.
  - `WindowsEndpointSource` marks each flow's console-role default (`AudioEndpoints.DefaultId`, null when the flow has none).
  - `EndpointWatcher` now signals on a console-role default change of either flow (it signalled only on the communications render default before), so `devices.changed` follows a default change. `AudioDeviceListBuilder` passes the flag through; absent layout devices are never default.
- [x] **Presets as data** (`Contracts/Settings/TrackLayoutPresets.cs`): `Simple` and `ForEditing`, built from a device list's available defaults over the current layout. Track 1 keeps its id (the catalog's `EverythingTrackId` for the seed), a default the layout already holds keeps its record and key (the input marked a Mic Device), a missing default is left out with its Tracks kept empty, and only used devices are kept. It lives in Contracts so the engine's seed and the App's Simple can't differ (AD-24's "Prevents").
- [x] **The first-run seed** (`Engine/Settings/TrackLayoutSeed.cs`): at start, after the device list's first listing and before the event hub's snapshot, when settings.json has no Global `trackLayout`, the engine writes `TrackLayoutPresets.Seed` through `SettingsStore.Update` (AD-4). An existing value is never touched. The catalog default is unchanged.
  - `EngineHostOptions.SeedTrackLayout` (null: on for every instance but a test one, like `ReplayBuffer`), so the existing engine tests, which list the PC's real devices, aren't seeded. The seed tests turn it on.
- [x] **The editor's draft model** (`App/Settings/AudioTracksDraft.cs`): Tracks with their sources; adding a source reuses the record of that endpoint and flow, or adds one (an input as a Mic Device); "Treat as a microphone" sets the one record; `ToLayout` drops a device on no Track and keeps first-use order; `ApplyPreset` uses the shared presets. 10.5 binds its cards to it and replaces today's `TrackLayoutPage` pool model.
- [x] **Per-device meters by probe Tracks** (`Contracts/Messages/MeterProbes.cs`): `WithProbes` appends one probe Track per layout device and per available picker device to the watched copy, each holding its one device with mute off. Probe Track ids, and the keys of picker devices the layout lacks, are name-based GUIDs (SHA-256, version 8) of the endpoint id (ignoring case) and flow, so they're stable across watches. `LevelOf` reads a device's level. The engine is unchanged: it already opens each device once (AD-20), so a probe adds a mixer input, not a device.
- [x] **Meter cost:** probes kept, no fallback. See the epic's Notes. The Heavy `MeterProbeCostTests` measures it on the builder's devices; queued for the coordinator, since Heavy runs are paused for this story.
- [x] **R17 check:** a saved layout prepares the new chain at once and switches at the next save's Reset, or applies to the next game session (`SessionOrchestrator.ApplySettings`). During a Recording it waits for the Recording to stop, then the next save. EXPERIENCE.md's hint "from your next game, or right after your next saved Clip" stands, unchanged.

## Deviations

- The ticket names the probe ids as "derived from the device key and flow". They're derived from the endpoint id and flow instead, because a picker device has no layout key, and the endpoint gives the same id whichever draft holds it.
- The seed isn't written on a test instance unless the host options ask for it (see above).

## Tests (lean)

- Contracts `TrackLayoutPresetsTests`: Simple keeps Track 1's id and a held device's record; For editing's three Tracks, and a missing default left out; `isDefault` is written only when true and reads false when missing.
- Engine `TrackLayoutSeedServeTests`: an empty settings folder is seeded (and the file holds it); no default input seeds playback only; an existing layout is unchanged. `DeviceListServeTests.TheWindowsDefaultsAreMarkedAndFollowADefaultChange`. `MeterServeTests.ProbeTracksGiveALevelPerDeviceAndOpenEachDeviceOnce`.
- App `AudioTracksDraftTests`: a record shared across Tracks, a device on no Track dropped at save, an input marked a Mic Device; a preset keeps Track 1's id; probes only in the watched copy.
- Heavy, not run (paused): `MeterProbeCostTests.EveryListedDeviceProbedCostsLittleCpuAndKeepsTheMeterRate`.

## Log

- 2026-11-05: built and verified; READY FOR QA.
