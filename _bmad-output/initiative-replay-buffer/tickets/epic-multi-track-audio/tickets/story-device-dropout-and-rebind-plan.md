---
title: 'Device dropout and rebind'
type: 'feature'
ticket: '5'
created: '2026-11-02'
status: 'ready-for-dev'
baseline_revision: ''
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

**Problem:** When an open audio device vanishes (unplugged, disabled, or the BEACN app restarting its virtual devices), the hub's WASAPI client either faults and holds the stream silent for the rest of the session (with `DeviceFault` set) or, for an Input Device, simply stops delivering. It never comes back, and nothing tells the user (R5).

**Approach:** AudioDeviceHub reconciles its open devices against the present endpoints on every `EndpointsChanged` (story 2.3's watcher), on a client's "device invalidated" signal, and every 500 ms while a device is out. A device that is gone is dropped: its client is closed and a hub-owned silence hold keeps its `DeviceStream` continuous. When a matching endpoint is present again (same endpoint id and flow, else exactly one endpoint with the same flow, friendly name and parent name), a new client is opened on the same stream, so every subscriber resumes with no capture restart. The hub raises `Dropped` and `Resumed`; the engine turns each `Dropped` into `warning.raised { code: device-dropout, overlay: false }`.

## Boundaries & Constraints

**Always:** Silence during a dropout comes from that device only; other devices and every Track keep running, and each Track keeps its length. One client per endpoint (AD-20); capture threads never block on the hub's gate (the invalidated callback only sets a flag and pokes a timer). The rebinding lives only in the hub's memory: settings are never read or written by it (AD-4). Each dropout raises exactly one `device-dropout` warning (severity warning, source `audio`, `overlay: false`, dismissable) and exactly one log entry at Warning; a resume logs one Information entry. Contracts changes are additive (AD-22): only a new `ErrorCodes.DeviceDropout`. A device-invalidated error is a dropout, not a `DeviceFault`.

**Never:** No change to `IAudioDeviceHub.Subscribe`, `SessionSpec`, the `trackLayout` shape, `TrackMixer` or the capture session's Track wiring. No handling of a layout device that is absent when the capture starts (it still fails as today). No auto-dismiss of the warning on resume (AD-3: warnings go only by `warning.dismiss`). No test changes Windows device state: tests use a fake client factory and a fake endpoint source. No App UI, mute (2.4) or meters (2.6).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Removed, idle | open loopback device, its id leaves the present list, `EndpointsChanged` | dropped within 1 s; stream continues as silence; one `Dropped` | — |
| Invalidated | client signals device-invalidated (its endpoint still listed for a moment) | dropped; `DeviceFault` stays null | — |
| Returns, same id | dropped device's id+flow listed again | new client on the same stream within 2 s of the change; one `Resumed`; subscribers get its audio | — |
| Returns, new id | same flow, friendly name and parent name, different id | rebound to the new id; `Resumed` carries it | — |
| Ambiguous return | two present endpoints match by name, neither by id | stays dropped, silent | logged once at Debug per reconcile |
| Name taken | the only name match is already another open device's current endpoint | stays dropped | — |
| Open fails on return | endpoint listed but the client can't open | stays dropped; retried every 500 ms until it opens | logged at Debug |
| Listing fails | `ListPresent` throws | nothing dropped or rebound this round | logged |
| Last subscriber leaves while dropped | `Dispose` of the last subscription | hold stopped, device closed, no further retries for it | — |
| Two devices | A drops, B keeps playing | B's blocks unchanged; A's stream silent for the dropout | — |
| Repeated dropout | A drops, returns, drops again | two `Dropped`, two warnings | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Audio/AudioDeviceHub.cs` -- add `public sealed record AudioDeviceDropout(string EndpointId, AudioFlow Flow, string FriendlyName, string ParentName)` and port `public interface IAudioDropoutSource { event Action<AudioDeviceDropout>? Dropped; event Action<AudioDeviceDropout>? Resumed; }` (raised on the hub's reconcile thread; handlers must not block long). `Resumed` carries the endpoint it is now bound to.
- `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- `DeviceDropout = "device-dropout"` ("A warning: an audio device in use dropped out; its Tracks record silence from it until it returns."), added to `All`.
- `src/ReplayBuffer.Platform.Audio/DeviceClient.cs` (new) -- `internal interface IDeviceClient : IDisposable { AudioEndpoint Endpoint; Exception? Fault; bool OnCaptureThread; void Start(DeviceStream, AudioStampStats); }` and `internal delegate IDeviceClient DeviceClientFactory(string endpointId, AudioFlow flow, Action invalidated)`. Also `internal sealed class SilenceHold(DeviceStream stream) : IDisposable`: a background thread that every 20 ms calls `stream.FillTo(now − 50 ms)` (same lag constants as `WasapiCapture`), `OnCaptureThread`, `Dispose` joins. A throw from `FillTo` stops the hold (logged once).
- `src/ReplayBuffer.Platform.Audio/WasapiCapture.cs` -- implements `IDeviceClient`; ctor takes `Action? invalidated`. In `Run`, an `HRESULT` of `AUDCLNT_E_DEVICE_INVALIDATED` (0x88890004, from `COMException.HResult` or CsWin32's thrown exception) from start or drain holds silent like a failure but does **not** set `Fault`, logs at Debug, and calls `invalidated` once. Other failures unchanged.
- `src/ReplayBuffer.Platform.Audio/AudioDeviceHub.cs` --
  - Implements `IAudioDropoutSource`.
  - Public ctor `AudioDeviceHub(ILoggerFactory)` = internal ctor `(ILoggerFactory, DeviceClientFactory, IAudioEndpointSource)` with WASAPI and a new internal `WindowsEndpointSource` (move today's `ListPresent` and lazy `EndpointWatcher` code there; keep `internal EndpointWatcher? Watcher` on the hub delegating to it when it is one, so `EndpointWatcherTests` hold). The hub's `IAudioEndpointSource` members delegate to the inner source.
  - On the first `SubscribeDevice`, the hub subscribes its own reconcile to the inner `EndpointsChanged` (that starts the real watcher) and creates a `System.Threading.Timer` (not started). `EndpointsChanged` and `invalidated` only call `_timer.Change(0, Infinite)`.
  - Reconcile (timer callback, serialized by a `_reconcile` lock): list present (catch: log, reschedule, return); then under `_gate`, for each open, not-closed `HubDevice`: drop it if invalidated or its current id+flow isn't listed; for a dropped one find a match (id+flow, else the unique name match not held by another open device) and try `factory(match.Id, …)` then `device.Rebind(client)`; on a throw keep it dropped (Debug log). Collect events, raise `Dropped`/`Resumed` and log after leaving `_gate`. If any device is still dropped, `_timer.Change(500 ms, Infinite)`.
  - Logging: one Warning per dropout ("{Code}: '{Name}' ({Parent}) dropped out; held silent until it returns", code `device-dropout`), one Information per resume (with the new id when it changed).
  - `Dispose` stops the timer (`Timer.Dispose(WaitHandle)`), unsubscribes, then closes devices as today.
- `HubDevice` (same file) -- `Client` becomes a mutable `IDeviceClient?` plus `SilenceHold? Hold`, `Dropped`, `volatile bool Invalidated`, `AudioEndpoint Endpoint` (current). `Drop()`: dispose client (joins), start `SilenceHold`. `Rebind(client)`: dispose hold (joins), `client.Start(stream, stamps)`, clear flags, update `Endpoint` and `Diagnostics.Endpoint`. `OnCaptureThread` checks the client and the hold. `Close` disposes whichever runs. `AudioDeviceSubscription.DeviceFault` reads the current client's fault (null while held).
- `src/ReplayBuffer.Platform.Audio/ReplayBuffer.Platform.Audio.csproj` -- `InternalsVisibleTo ReplayBuffer.Engine.Tests` (the AC test builds the hub with fakes).
- `src/ReplayBuffer.Engine/Audio/DeviceDropoutReporter.cs` (new) -- `(IAudioDropoutSource, Warnings, IDisposable)`: on `Dropped`, `warnings.Raise(ErrorCodes.DeviceDropout, WarningSeverity.Warning, "audio", "'{Name}' dropped out. Its Tracks record silence from it until it returns.", overlay: false)`. No log (the hub's entry is the one log line). Unsubscribes on dispose.
- `src/ReplayBuffer.Engine/EngineHost.cs` -- build the reporter over `audio` after `warnings`; dispose it before `Audio`. Update the class doc.
- `src/ReplayBuffer.Engine/Sessions/CaptureSession.cs` -- read-only; its `DeviceFault` log stays for real faults.
- Reuse in tests: `RecordingSubscriber` in `AudioDeviceHubTests.cs`; `ToneHub`'s tone thread, `Decode`, `Rms`, `AssertDurationsMatchTheVideo` and `CaptureAsync` pattern in `TrackMixCaptureTests.cs`; `SessionTestKit`.

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.Contracts/ErrorCodes.cs` -- `DeviceDropout` -- the warning's code
- [ ] `src/ReplayBuffer.Core/Audio/AudioDeviceHub.cs` -- `AudioDeviceDropout`, `IAudioDropoutSource` -- the port the engine listens on
- [ ] `src/ReplayBuffer.Platform.Audio/DeviceClient.cs`, `WasapiCapture.cs`, `AudioDeviceHub.cs`, csproj -- client seam, silence hold, invalidation, reconcile and rebind
- [ ] `tests/ReplayBuffer.Platform.Audio.Tests/DeviceDropoutTests.cs` (new) -- a hub over a fake factory (tone clients stamped from QPC, like `ToneHub`) and a fake endpoint source; every matrix row; and the stream stays continuous: across a 2 s dropout the subscriber's frame count tracks wall time within 0.1 s and the dropout span is silent
- [ ] `src/ReplayBuffer.Engine/Audio/DeviceDropoutReporter.cs`, `EngineHost.cs` -- the warning
- [ ] `tests/ReplayBuffer.Engine.Tests/DeviceDropoutReporterTests.cs` (new) -- a fake `IAudioDropoutSource` raising `Dropped` twice and `Resumed` once gives two `device-dropout` warnings in `EventHub.State.Warnings`, `overlay: false`, severity warning, source `audio`
- [ ] `tests/ReplayBuffer.Engine.Tests/DeviceDropoutCaptureTests.cs` (new) -- the ticket's automated AC (below)
- [ ] `tests/ReplayBuffer.Contracts.Tests` -- only if a registry test needs the new code listed

**Acceptance Criteria:**
- Given an in-process capture session of the animated test window over a real `AudioDeviceHub` built with fake tone clients A (440 Hz) and B (1 kHz) and Tracks `A`={A}, `B`={B}, `AB`={A, B}, when A is removed from the endpoint source (and its client stops delivering) for 10 s mid-capture and then listed again, then every AAC stream lasts as long as the video to within 0.1 s; Track `A` is silent (below −60 dBFS RMS) over the dropout, within 1 s at its start and within 2 s of the return at its end, and carries A's tone before and after; Track `B` carries B's tone throughout; Track `AB` carries B throughout and A outside the dropout; and the hub raised `Dropped` once and `Resumed` once, `Resumed` within 2 s of A being listed again.
- Given the same session, when A returns, then no session was restarted (one session, one file) and the subscriptions were not re-made (the session's subscriber received A's new client's audio on the old subscription).
- Given the published NativeAOT engine, when it is built, then `publish-engine.ps1` reports 0 IL/AOT warnings.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

Why the hub owns dropout, not the session: the hub owns the one client per endpoint and the `DeviceStream` every subscriber hangs off. Swapping the client underneath the stream keeps every Track sample-aligned and continuous without the session knowing; a session-level rebind would need new subscriptions, a re-anchored timeline and mixer surgery.

Why presence-based detection plus the invalidated signal: an idle loopback endpoint or a vanished Input Device may deliver no error at all (no events, so no `GetBuffer` call), so the watcher's re-list is the reliable signal; the invalidated HRESULT just makes it faster.

Why a separate `SilenceHold` thread rather than keeping the dead client in its failed loop: the stream must have exactly one writer at a time; dispose-then-start on one gate gives a clean hand-off, and the fake clients in tests need no hold logic.

## Verification

**GPU lock:** before every build or test run, check that `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` doesn't exist; while it does, wait.

**Environment:** a fresh worktree needs `native/ffmpeg` (copy it from the main checkout with its `.sha256`, or run `tools/fetch-ffmpeg.ps1`), or the capture tests skip.

**Commands (scoped per automation-state.md):**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --project tests/ReplayBuffer.Platform.Audio.Tests -c Release`, `tests/ReplayBuffer.Media.Tests`, `tests/ReplayBuffer.Engine.Tests`, `tests/ReplayBuffer.Contracts.Tests`, `tests/ReplayBuffer.Core.Tests` -- expected: all pass (a real-time timing test that fails gets one rerun)
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

**Manual checks (Human Check Queue):**
- The builder restarts the BEACN app during a capture: its devices resume within 2 s, with one `device-dropout` warning and one log entry each.
