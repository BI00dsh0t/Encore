---
title: 'Track meters in the engine'
type: 'feature'
ticket: '6'
created: '2026-11-03'
status: done
baseline_revision: '63d104d521d9c3af51c62a4308707424294a47a5'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** The App can't show live Track levels (R6): there is no `meters.*` message, and the only audio mixers are a capture session's, so nothing measures a layout while nothing is captured.

**Approach:** `meters.watch { layout }` builds a meter-only mixer for the given layout (saved or an unsaved draft) on the AudioDeviceHub's fan-out: one hub subscription per distinct device, cut by an `AudioTimeline`, fanned to per-Track `TrackMixer`s (mic-mute gates as in a capture) whose output is a peak meter, not an encoder. A meter thread publishes coalesced `meters.levels` (one peak per Track) at most 20 times a second. `meters.unwatch` (or a new watch) closes the subscriptions.

## Boundaries & Constraints

**Always:** Contracts changes are additive (AD-22): payloads and messages only in `Contracts/Messages/Audio.cs`, plus a `StateSnapshot.Meters` field with an `Empty` default and null-guarded `init`. `meters.levels` coalesces and maps to snapshot field `meters`. One watch at a time, engine-wide: a new watch replaces the previous one; unwatch with nothing watched is a no-op. Meters show what is recorded: same `SessionAudio.FromLayout` mapping, same post-limiter level, and an `isMic` device in a `muteAffected` Track reads silent while mic mute is on (the orchestrator's current state, updated live). Peaks are gathered on device threads without allocation or blocking. Two publishes are never less than 50 ms apart. No Sink, encoder, muxer or capture session is created. Device subscriptions exist only while watched.

**Never:** No App UI (story 2.8). No change to `TrackMixer`, `AudioTimeline`, `MicMuteGate`, the hub or `SessionOrchestrator`. No per-connection watch ownership (an App that disconnects without unwatching leaves the watch until the next watch/unwatch or engine exit; story 2.8 unwatches on page close).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Watch a draft | `meters.watch` with a layout not in settings, no capture | `meters.levels` with one entry per Track, in layout order, at ≤ 20 Hz | — |
| Tone on a device | device D plays a tone; Track A holds D, Track B doesn't | A's peak ≈ tone amplitude, B's 0 | — |
| Muted mic | `isMic` M in `muteAffected` Track, mic muted | that Track's peak 0; a non-affected Track with M moves | — |
| Mute toggled while watched | `micMute` press | the gated Tracks follow from then on | — |
| Unwatch | `meters.unwatch` | subscriptions disposed; one final `meters.levels` with no Tracks | — |
| Re-watch | second `meters.watch` | old subscriptions closed before the new ones open | — |
| Device can't open | hub throws `audio-device-failed` for one device | that device is left out (its Tracks mix the rest); Warning logged | watch still succeeds |
| Bad layout | Track names a device key the layout lacks | reply `invalid-arguments`, previous watch kept | — |
| Track with no devices | | peak 0 every tick | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Messages/Audio.cs` -- add `MetersWatch(TrackLayout Layout)`; `TrackLevel(Guid TrackId, float Peak)` (linear sample peak since the last publish, 0..1); `MeterLevels(IReadOnlyList<TrackLevel> Tracks)` with `Empty` and sequence `Equals`/`GetHashCode` (copy `AudioDeviceList`); `AudioMessages.Watch` = `meters.watch` (`MetersWatch` → `Empty`), `Unwatch` = `meters.unwatch` (`Empty` → `Empty`), `Levels` = `meters.levels` event (`MeterLevels`, field `meters`, `coalesces: true`); add them to `Commands`/`Events`; register `MetersWatch`, `MeterLevels` in `AudioJsonContext`.
- `src/ReplayBuffer.Contracts/Messages/Engine.cs` -- `StateSnapshot.Meters` (`MeterLevels`, default `Empty`, null-guarded like `Devices`), set in `Initial`.
- `src/ReplayBuffer.Engine/Audio/TrackMeters.cs` (new) -- `TrackMeters(IAudioDeviceHub hub, Action<MeterLevels> publish, ILogger log) : IDisposable`. `Watch(TrackLayout)` (validates via `SessionAudio.FromLayout` first, so a bad layout keeps the old watch), `Unwatch()`, `SetMicMuted(bool)` (default muted = true like `CaptureState.Initial`; while watched → schedule `Set(muted, Stopwatch.GetTimestamp())`), `Dispose` = unwatch. All under one lock. A private `Watch` object: anchor QPC = now; subscribes each device with a tap like `SessionAudioTrack` (drops blocks until its timeline is published), dropping a device whose `Subscribe` throws `ErrorCodeException` (Warning logged); then per Track a `PeakMeter` sink and `new TrackMixer(<subscribed inputs>, peakMeter)`; per device an `AudioTimeline` over its sinks (wrapped in `MicMuteGate` for `IsMic && MuteAffected`, `AudioFanOut` for several), then publishes the timelines. A dedicated background thread publishes `MeterLevels` (track ids from `layout.Tracks` by index) whenever ≥ 50 ms (Stopwatch) passed since the last publish, waiting on a stop event. Dispose: stop + join the thread, dispose subscriptions, then publish `MeterLevels.Empty`.
- `PeakMeter` (same file, `internal sealed`, `IAudioSampleSink`) -- `Write` computes the max |sample| of the span and raises a stored float peak with a CAS loop on its bits (`BitConverter.SingleToInt32Bits`); `WriteSilence` no-op; `float Take()` = `Interlocked.Exchange(ref bits, 0)`.
- Reuse (read-only): `SessionAudio.FromLayout` (`src/ReplayBuffer.Core/Sessions/SessionSpec.cs`), `TrackMixer`/`AudioFanOut` (`src/ReplayBuffer.Media/TrackMixer.cs`), `AudioTimeline`, `MicMuteSchedule`/`MicMuteGate` (`src/ReplayBuffer.Media/MicMuteGate.cs`); the wiring loop in `CaptureSession.StartOutput` (`src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` ~line 476) is the pattern to follow.
- `src/ReplayBuffer.Engine/EngineHost.cs` -- `EngineHostOptions.MeterAudio` (`IAudioDeviceHub?`, null = the engine's hub). Build `TrackMeters` (publishing `AudioMessages.Levels` to the EventHub) before the orchestrator; the orchestrator's state callback also calls `meters.SetMicMuted(state.MicMuted)`. Register `AudioMessages.Watch` / `Unwatch`. Expose `Meters`; dispose it after `Sessions`, before `Audio`; also in the startup catch. Update the class doc.
- Tests: `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` (a `meters.levels` sample in `Samples`; only events need one); `tests/ReplayBuffer.Engine.Tests/ToneHub.cs` -- add an active-subscription count that `Dispose` of a subscription decrements, and per-endpoint silence (amplitude 0 is fine); `tests/ReplayBuffer.Engine.Tests/MeterServeTests.cs` (new) -- in-process `EngineHost` over a real pipe (copy `DeviceListServeTests`' setup) with `MeterAudio = ToneHub`.

## Tasks & Acceptance

**Execution:**
- [x] `src/ReplayBuffer.Contracts/Messages/Audio.cs`, `Engine.cs` -- payloads, messages, snapshot field -- the wire shape
- [x] `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` -- samples -- registry coverage tests demand them
- [x] `src/ReplayBuffer.Engine/Audio/TrackMeters.cs` -- meter mixer, peak meter, publish thread -- the feature
- [x] `src/ReplayBuffer.Engine/EngineHost.cs` -- wiring, commands, mute feed -- serve it
- [x] `tests/ReplayBuffer.Engine.Tests/ToneHub.cs`, `MeterServeTests.cs` -- the AC below, plus a bad-layout reply -- proof

**Acceptance Criteria:**
- Given a test-instance engine with a `ToneHub` (device T: 0.5-amplitude tone; mic M: 0.5-amplitude tone), mic mute on (no `runtime.json`), and no capture, when a client sends `meters.watch` with a draft layout of Tracks `Game`={T}, `Chat`={M} `muteAffected` with M `isMic`, `MicRaw`={M} not affected, `Empty`={}, then within 2 s it receives `meters.levels` with four entries in layout order: `Game` and `MicRaw` ≈ 0.5, `Chat` and `Empty` 0; and over ~1 s the count of `meters.levels` events is at most 21.
- Given that watch, when a `micMute` press unmutes the mic, then `Chat` moves (≈ 0.5) in a later `meters.levels`.
- Given that watch, when the client sends `meters.unwatch`, then the hub has no active subscriptions, and the client's `State.Meters` ends empty.
- Given a layout whose Track names a missing device key, when `meters.watch` is sent, then the reply is `invalid-arguments`.
- Given the NativeAOT publish, then `publish-engine.ps1` reports 0 IL/AOT warnings.

## Implementation Notes

- The private watch object is `TrackMeters.ActiveWatch` (the name `Watch` is the public method). Replacing a watch disposes the old one, which publishes `MeterLevels.Empty` before the new watch's first levels; coalescing usually hides it.
- `EngineHost.DisposeAsync` disposes `Meters` after the pipe server (so no `meters.watch` can arrive after it), still after `Sessions` and before `Audio`.
- Not covered by a test: a device whose `Subscribe` throws (`ToneHub` has no failing endpoint); the code path catches `ErrorCodeException`, logs a Warning and builds the Track mixers over the devices that opened.

- Superseding the two notes above (the session's verify pass, before review): a re-watch now calls `ActiveWatch.Stop()`, which publishes nothing, so no empty levels come between two watches; `Unwatch`/`Dispose` still publish `MeterLevels.Empty`. `ToneHub.Subscribe` throws `ErrorCodeException(audio-device-failed)` for an endpoint it has no tone for, and `MeterServeTests.ALayoutNamingAMissingDeviceIsRefusedAndKeepsThePreviousWatchUntilTheNextOne` now covers the re-watch and device-can't-open matrix rows.
- Lean tests (builder's standing decision): two host tests over a real pipe cover every matrix row and AC 1–4; no Heavy tests were needed (about 3 s of real-time tones).

## Plan Change Log

## Review Triage Log

### 2026-11-03 — Review pass
- verdicts: 3 findings — high 0, medium 0, low 2, false 0, maybe-false 1
- findings:
  - `[low]` `[patch]` `TrackMeters.Watch` stops the old watch before building the new one, so a constructor throw (a non-`ErrorCodeException` from `Subscribe`, an allocation failure) left the old levels frozen in `State.Meters` — fixed: a failed build publishes `MeterLevels.Empty` and rethrows.
  - `[low]` `[reject]` The `MeterLevels.Empty` that `Unwatch` publishes can follow the last tick by under 50 ms — it is the terminal state, not a levels stream, and coalescing keeps only the latest; guarding it would delay the unwatch reply for no visible gain.
  - `[maybe-false]` `[reject]` Two Implementation Notes contradict the code — the fix is editing this build's plan; a superseding note was appended instead (notes are append-only).

## Design Notes

Why a separate meter mixer and not taps on a running session: AD-20 says meters subscribe to the hub's fan-out with their own mixer, so they work with nothing captured and show a draft the session doesn't use. The hub shares one client per endpoint, so a meter and a session on the same device don't reopen it.

Why ≥ 50 ms by Stopwatch on one thread rather than a 50 ms `Timer`: Windows timer granularity (~15.6 ms) can fire a 50 ms timer early, and the AC is a hard "at most 20 Hz".

Why linear peak, not dBFS: the App picks the scale (story 2.8); a float 0..1 is cheap and exact for silence (0).

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Contracts.Tests -c Release --no-build` -- expected: pass
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build` -- expected: pass
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

## Auto Run Result

**Summary:** `meters.watch { layout }` meters a saved or draft Track Layout with a meter-only mixer on the hub (one subscription per distinct device → `AudioTimeline` → per-Track `TrackMixer`, mic-mute gated as in a capture → `PeakMeter`); a meter thread publishes coalesced `meters.levels` (one linear peak per Track id, layout order) never sooner than 50 ms apart. `meters.unwatch` closes the subscriptions and publishes no Tracks. Mic mute follows the orchestrator live.

**Files:**
- `src/ReplayBuffer.Contracts/Messages/Audio.cs` — `MetersWatch`, `TrackLevel`, `MeterLevels`; `meters.watch`, `meters.unwatch`, `meters.levels` (coalescing, field `meters`)
- `src/ReplayBuffer.Contracts/Messages/Engine.cs` — `StateSnapshot.Meters` (default empty)
- `src/ReplayBuffer.Engine/Audio/TrackMeters.cs` — the meter mixer, `PeakMeter`, publish thread
- `src/ReplayBuffer.Engine/EngineHost.cs` — wiring, `MeterAudio` test override, mute feed, commands
- `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` — `meters.levels` sample
- `tests/ReplayBuffer.Engine.Tests/ToneHub.cs` — active-subscription count; unknown endpoints fail to open
- `tests/ReplayBuffer.Engine.Tests/MeterServeTests.cs` — the ACs and matrix rows over a real pipe

**Review:** 1 patch (low), 0 deferred, 2 rejected (see the triage log). Follow-up review recommended: false (0 high, 0 medium patched).

**Verification:** `dotnet build ReplayBuffer.slnx -c Release` 0 warnings; Contracts 86/86, Engine 140 passed (17 opt-in skipped), App 58 passed (5 opt-in skipped), Core 54/54; `publish-engine.ps1` published 10.1 MB with no IL/AOT warnings. Engine re-run and publish repeated after the review patch.

**Residual risks:** the watch is engine-wide, not owned by a connection: an App that disconnects without `meters.unwatch` leaves the devices open until the next watch/unwatch or engine exit (story 2.8 unwatches on page close). Meter tests run on real time (about 3 s), so a heavily loaded PC could time out their 2 s windows.
