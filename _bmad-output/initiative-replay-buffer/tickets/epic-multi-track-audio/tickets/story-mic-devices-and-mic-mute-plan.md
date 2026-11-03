---
title: 'Mic Devices and mic mute'
type: 'feature'
ticket: '4'
created: '2026-11-03'
status: 'in-progress'
baseline_revision: '03d62ebf472c91bd0c3455a56c843f7e3cd087d8'
route: 'full'
route_source: 'auto'
risk: 'high'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/CLAUDE.md']
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** The layout already marks devices `isMic` and Tracks `muteAffected` (story 1.4), and `micMute` is a Hotkey action (1.9), but nothing acts on them: a press is only published as `hotkey.pressed`, `capture.stateChanged.micMuted` is stuck at its initial `true`, and every Mic Device is always recorded (R4).

**Approach:** The SessionOrchestrator owns mic mute (AD-19): it loads it from `runtime.json` before any session can start (missing or corrupt means muted), toggles it on each `micMute` press, saves it atomically, publishes `micMuted`, and hands the press QPC to the running session. The session gates each `isMic` device's input to each `muteAffected` Track's mixer, before the mix, silencing it from the first frame at or after the press QPC (AD-5, AD-13).

## Boundaries & Constraints

**Always:** Mute is runtime state, never a setting; `runtime.json` lives in `Instance.LocalDataFolder` and is written temp-then-`File.Move(overwrite)`, flushed to disk. Only `isMic` devices are silenced, only in `muteAffected` Tracks; the same device stays audible in other Tracks and non-mic devices are untouched. A session starts with the orchestrator's current mute in place before its first audio frame (no unmuted burst at start). The gate allocates nothing on the audio thread and never blocks on the orchestrator. Every press still publishes `hotkey.pressed`. A save failure keeps the new state in memory and logs a Warning; it never stops the orchestrator. Mute isn't part of `ChainKey` or the snapshot.

**Never:** No Contracts change (`CaptureState.MicMuted` and the layout fields exist). No App or editor UI (2.11), no Overlay icon (epic 5), no meters (2.6), no per-profile overrides (epic 4). No change to `TrackMixer`'s alignment, `AudioTimeline`, the hub or the `trackLayout` shape. No retroactive muting of frames a mixer has already passed.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Cold start, no file | `runtime.json` missing | `micMuted: true` in the first snapshot; sessions start muted | — |
| Corrupt file | invalid JSON, wrong shape, or unreadable | muted | logged once at Warning |
| Saved unmuted | file says `micMuted: false` | first published state `micMuted: false`, before any session starts | — |
| Toggle, idle | `micMute` press, no session | state flips, saved, `capture.stateChanged` once, `hotkey.pressed` once | — |
| Toggle mid-capture | press at QPC P | gated inputs silent from frame ceil((P−anchor)·48000/f); untouched before | — |
| Unmute mid-capture | second press at P2 | mic audible again from P2's frame | — |
| Press before anchor | toggle while the session waits for its first frame | applied from frame 0 | — |
| Device in two Tracks | mic in `Everything` (affected) and `Mic` (not) | silent in `Everything` only | — |
| Non-mic in affected Track | game device in `Everything` | unchanged | — |
| Save fails | the folder can't be written | state still toggles and publishes | Warning logged, temp file removed |
| Other Hotkey | `save` press | mute unchanged | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Sessions/SessionSpec.cs` -- `SessionAudioDevice` gains `bool IsMic = false`; `SessionTrack` gains `bool MuteAffected = false` (optional, so existing callers compile). `FromLayout` sets `IsMic` when any layout key naming that endpoint+flow is `IsMic`, and copies `Track.MuteAffected`. `SessionSpec` gains `bool MicMuted = true`: the mute at Start.
- `src/ReplayBuffer.Core/Sessions/CaptureSessionPorts.cs` -- `ICaptureSession.SetMicMuted(bool muted, long qpc)`: called on the orchestrator thread; applies from `qpc`. New port `IMicMuteStore { bool Load(); void Save(bool muted); }` (neither throws: `Load` returns true for a missing or corrupt file; `Save` logs a Warning on failure and returns).
- `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- new optional ctor param `IMicMuteStore? micMute = null` (null: in memory, starts muted). Ctor calls `Load()` before the thread starts and sets `_state = CaptureState.Initial with { MicMuted = loaded }`; when that differs from `Initial` it publishes it as the first thing the thread does (post a private message before any public one can be handled). `HotkeyPressed` with `HotkeyAction.MicMute`: flip, `Save` (wrapped in a catch-all anyway, so a faulty store can't kill the thread), `SetState`, then `_active?.Session.SetMicMuted(muted, qpc)` (a throw from it is caught: mute is still committed). Then the existing press callback. `Start` builds the spec with `MicMuted = _state.MicMuted`. Update class and message docs ("no action is bound" is no longer true for `micMute`).
- `src/ReplayBuffer.Media/MicMuteGate.cs` (new) -- `MicMuteSchedule`: thread-safe toggle log, `MicMuteSchedule(bool muted)`, `Set(bool muted, long qpc)` (orchestrator thread; swaps an immutable `(long Qpc, bool Muted)[]` via `Volatile`; a QPC lower than the last is clamped to it; a same-state set is ignored), `bool Muted`. `MicMuteGate(IAudioSampleSink inner, MicMuteSchedule schedule, long qpcFrequency, long anchorQpc) : IAudioSampleSink`: counts its frame position from 0 (the timeline's output is contiguous from the anchor), maps each change to its first frame at or after its QPC (≤ anchor → 0), and splits each `Write` into runs: unmuted runs pass as `inner.Write(slice)`, muted runs as `inner.WriteSilence(n)`; `WriteSilence` passes through. No allocation, no lock beyond a `Volatile.Read`.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- owns one `MicMuteSchedule(spec.MicMuted)` from the ctor; `SetMicMuted` → `_micMute.Set`. In `StartOutput`, for device `d` in Track `t`, the sink is `new MicMuteGate(_mixers[t].Input(input), _micMute, _qpcFrequency, anchorQpc)` when `Devices[d].IsMic && Tracks[t].MuteAffected`, else unchanged. Log one Information line per toggle ("Mic muted/unmuted from frame N") and the start state. Update the class doc.
- `src/ReplayBuffer.Engine/Settings/RuntimeStateStore.cs` (new) -- `IMicMuteStore` over `<folder>/runtime.json` `{ "micMuted": bool }`, source-generated JSON (NativeAOT, AD-14), atomic write like `SettingsStore.Write` (temp `runtime.json.tmp`, `Flush(flushToDisk: true)`, `File.Move(overwrite: true)`, temp deleted on failure). `Load` logs a Warning for a corrupt/unreadable file, Information for a missing one; `Save` catches `IOException`/`UnauthorizedAccessException`, logs a Warning and returns.
- `src/ReplayBuffer.Engine/EngineHost.cs` -- build `RuntimeStateStore(instance.LocalDataFolder, logger "Audio")` and pass it to the orchestrator; update the class doc.
- `src/ReplayBuffer.Engine/TracerCapture.cs` -- pass the same store for `instance` (read only in practice: debug-capture has no Hotkeys), and log the mic mute state it starts with, since a muted start silences Mic Devices in `muteAffected` Tracks.
- Read-only / reuse: `HotkeyService` (already posts `HotkeyPressed`), `CaptureState.Initial`, `SettingsStore.Write`'s pattern, `TrackMixer`, `AudioTimeline`. Test reuse: `ToneHub`, `Decode`, `Rms`, `CaptureAsync` in `tests/ReplayBuffer.Engine.Tests/TrackMixCaptureTests.cs`; `DeviceDropoutCaptureTests` for a mid-capture action; `SessionTestKit`; `FakeFactory`/`FakeSession` in `tests/ReplayBuffer.Core.Tests/SessionOrchestratorTests.cs`; `EngineHost.Start` with `Instance.ForTest` as in `DeviceListServeTests`/`IpcTests`.

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.Core/Sessions/SessionSpec.cs`, `CaptureSessionPorts.cs` -- mic fields, `MicMuted`, `SetMicMuted`, `IMicMuteStore` -- the session's mute inputs
- [ ] `src/ReplayBuffer.Core/Sessions/SessionOrchestrator.cs` -- load, toggle, save, publish, forward -- AD-19 ownership
- [ ] `src/ReplayBuffer.Media/MicMuteGate.cs` -- schedule and gate -- mute before the mix from the press frame
- [ ] `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- gate wiring -- only `isMic` × `muteAffected`
- [ ] `src/ReplayBuffer.Engine/Settings/RuntimeStateStore.cs`, `EngineHost.cs`, `TracerCapture.cs` -- `runtime.json` -- AD-5 persistence
- [ ] `tests/ReplayBuffer.Media.Tests/MicMuteGateTests.cs` (new) -- sample-exact: a gate over a capturing sink, frames before the press frame bit-identical, from it zero; unmute; change before anchor; change inside one write; silent writes pass through; a single-input and a two-input `TrackMixer` behind gates; no allocation per write after warm-up
- [ ] `tests/ReplayBuffer.Core.Tests` -- `SessionAudioTests`: `FromLayout` mic/muteAffected mapping; `SessionOrchestratorTests`: every orchestrator matrix row (fake store: load false publishes `micMuted: false` first; toggle saves, publishes, forwards QPC to the fake session; a throwing fake `Save` still toggles and the orchestrator keeps running; `save` press doesn't; spec carries `MicMuted`)
- [ ] `tests/ReplayBuffer.Engine.Tests/RuntimeStateStoreTests.cs` (new) -- missing, corrupt, wrong shape → muted; round-trip; no temp file left; a failed write (folder made read-only or a directory where the temp file goes) returns, logs, and leaves the old file
- [ ] `tests/ReplayBuffer.Engine.Tests/MicMuteCaptureTests.cs` (new) -- the capture AC below
- [ ] `tests/ReplayBuffer.Engine.Tests` (host test, beside `DeviceListServeTests`) -- the restart AC below

**Acceptance Criteria:**
- Given an in-process capture over a `ToneHub` with a game device G (440 Hz, not mic) and a mic device M (1 kHz, `IsMic`), Tracks `Everything`={G, M} `muteAffected`, `Game`={G}, `Mic`={M} `muteAffected`, `MicRaw`={M} not affected, started unmuted (a fake store), when a `micMute` press is posted mid-capture at QPC P and a second one ~3 s later, then: `Mic` is silent (below −60 dBFS RMS) from 50 ms after P's frame to the second press and carries the tone before P and after it; `MicRaw` carries M's tone throughout; `Everything` carries G throughout and M's 1 kHz only outside the muted span (measured by a 1 kHz band check, e.g. Goertzel, against the 440 Hz); `Game` is unchanged; every stream lasts as long as the video to within 0.1 s; and the orchestrator published `micMuted` true then false.
- Given a test-instance engine host, when a `micMute` press unmutes it, then `capture.stateChanged` reports `micMuted: false` and `runtime.json` holds it; after a restart it starts unmuted; after deleting `runtime.json` and restarting, its first snapshot reports `micMuted: true`.
- Given the published NativeAOT engine, when it is built, then `publish-engine.ps1` reports 0 IL/AOT warnings.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

Why a gate per (device, Track) input and not on the device stream: one device feeds several Tracks through `AudioFanOut`, and only `muteAffected` ones may lose it (AD-13 "before the mix, only in Tracks marked"). Turning muted runs into `WriteSilence` reuses the mixer's own silence path, so nothing is copied or allocated.

Why the QPC is applied by frame position: the timeline's output frame k is k/48000 s after the anchor (AD-6), so the press frame is exact and every gate of the session cuts at the same sample. A press the orchestrator handles late can leak at most the frames that passed the gate between the press QPC and its handling (audio blocks always arrive after their stamps), i.e. the post latency; the AAC test allows 50 ms for that and the codec's window, the Media test checks exactness.

Why `MicMuted` rides on `SessionSpec`: the session's taps start before `Start` returns, so a mute set afterwards could let a few unmuted frames through; the spec puts it in place before `Open`.

`isMic` when two layout keys name one endpoint: muted if any key is a mic (the safe side for a privacy control).

## Verification

**GPU lock:** before every build or test run, check that `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` doesn't exist.

**Environment:** copy `native/ffmpeg` (with its `.sha256`) from the main checkout, or the capture tests skip.

**Commands (scoped per automation-state.md):**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Media.Tests -c Release`, then `tests/ReplayBuffer.Core.Tests`, `tests/ReplayBuffer.Engine.Tests` -- expected: all pass (a real-time timing test that fails gets one rerun)
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

**Manual checks (Human Check Queue):**
- With a game focused and the engine serving, press the Mic Mute Hotkey mid-capture, speak, and check the file: the mic is gone from the `muteAffected` Tracks only, and `micMuted` flips in the App.
