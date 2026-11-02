---
title: 'Live device list over IPC'
type: 'feature'
ticket: '3'
created: '2026-11-02'
status: 'ready-for-dev'
baseline_revision: '8efe555891633d78a801b620abe975b9acc49157'
route: 'full'
route_source: 'auto'
risk: 'medium'
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

**Problem:** The App can't see the PC's audio devices: there is no `devices.*` message, the snapshot has no device field, and the engine never hears when a device is plugged in, removed, enabled or disabled (R1).

**Approach:** AudioDeviceHub registers an `IMMNotificationClient` (a ComWrappers CCW over CsWin32's generated vtable) and raises a coalesced `EndpointsChanged` from its own MTA watcher thread, behind a new Core port. An engine `DeviceListPublisher` merges the present endpoints with the devices of every Track Layout in the settings document into an `AudioDeviceList`, kept in `state.snapshot` as `devices`, changed by `devices.changed`, and returned by `devices.list`.

## Boundaries & Constraints

**Always:** Contracts changes are additive only (AD-22): a new optional snapshot field with a default, new messages and payloads only in `Contracts/Messages/Audio.cs`. Notification callbacks only signal; they never enumerate, lock for long or throw. Present devices are matched to layout devices by endpoint id (ordinal, ignoring case) and flow. A `devices.changed` is published only when the list differs from the last one. A COM failure while listing is logged and never stops the engine; startup with no audio devices still serves. Settings are never written.

**Never:** No rebinding or dropout handling (2.5), no meters (2.6), no App UI (2.7). No change to `IAudioDeviceHub.Subscribe`, the `trackLayout` shape or capture sessions. No test changes Windows device state (enabling or disabling a real endpoint is forbidden on this PC): tests use a test double of the endpoint source and drive the real CCW through its vtable.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Snapshot | engine starts with present endpoints, no layout devices | `state.snapshot.devices.devices` lists every present Output and Input endpoint with endpointId, friendlyName, parentName, flow, `available: true`; no event published for the initial list (seq unchanged) | — |
| Same names | two Output endpoints named "Speakers", parents "Realtek" and "USB DAC" | two entries, told apart by `parentName` | — |
| Device added / removed / state change | endpoint source raises Changed with a new list | `devices.changed` within 2 s carrying the new list | — |
| Burst | ten notifications within 100 ms | one re-list, at most one event | — |
| Nothing changed | a notification but an identical list (e.g. a property change of another key) | no event | — |
| Configured but absent | Global (or any profile's) trackLayout holds a device whose id+flow isn't present | an entry from the layout's friendlyName/parentName with `available: false` | — |
| Absent and unconfigured | an endpoint vanishes and no layout holds it | it is not listed | — |
| Same id in two layouts | Global and a profile both hold the absent device | listed once | — |
| Wrong flow | layout device's id is present only with the other flow | listed unavailable for its own flow | — |
| Layout edited | `settings.update` adds/removes an absent layout device | `devices.changed` with it shown/hidden; nothing published when the list is unchanged | — |
| `devices.list` | command with `{}` | reply equals the snapshot's current list | — |
| Listing fails | endpoint source throws | the previous present list stays, error logged | no event, engine keeps serving |
| Old engine snapshot | snapshot JSON without `devices` | reads as the empty list | — |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Contracts/Messages/Audio.cs` -- add `AudioDevice(string EndpointId, string FriendlyName, string ParentName, AudioFlow Flow, bool Available)` (Contracts.Settings `AudioFlow`), `AudioDeviceList(IReadOnlyList<AudioDevice> Devices)` with `Empty` and sequence `Equals`/`GetHashCode` (copy `VideoOptions`' pattern in `Capture.cs`), `AudioMessages.List` = `devices.list` (`Empty` → `AudioDeviceList`), `AudioMessages.Changed` = `devices.changed` (snapshot field `devices`), and an `AudioJsonContext` like `CaptureJsonContext` (serializable `AudioDeviceList`, `Empty`).
- `src/ReplayBuffer.Contracts/Messages/Engine.cs` -- `StateSnapshot.Devices` (`AudioDeviceList`, default `Empty`, null-guarded `init` like `VideoOptions`), set in `Initial`.
- `src/ReplayBuffer.Core/Audio/AudioDeviceHub.cs` -- new port `IAudioEndpointSource { IReadOnlyList<AudioDevice> ListPresent(); event Action? EndpointsChanged; }` (both flows, raised on any thread, coalesced; handlers must not block).
- `src/ReplayBuffer.Core/Audio/AudioDeviceListBuilder.cs` (new) -- pure `Build(IReadOnlyList<AudioDevice> present, SettingsDocument document)`: layouts are `SettingsResolver.Resolve(document, null)` plus `Resolve(document, id)` for each profile; absent layout devices added unavailable, deduped by (id, flow); sorted by flow (Output first), friendlyName, parentName (OrdinalIgnoreCase), then endpointId (Ordinal).
- `src/ReplayBuffer.Platform.Audio/EndpointWatcher.cs` (new) -- owns an MTA thread: `AudioEndpoints.EnsureCom`, creates an `IMMDeviceEnumerator`, registers the CCW, waits on an `AutoResetEvent`; after a signal waits 100 ms, resets, raises its callback; on dispose unregisters, releases the CCW refs and enumerator, joins. The CCW: a `ComWrappers` subclass whose `ComputeVtables` returns one `ComInterfaceEntry` for `IMMNotificationClient.IID_Guid` with a vtable from `GetIUnknownImpl` + `IMMNotificationClient.PopulateVTable` (memory from `RuntimeHelpers.AllocateTypeAssociatedMemory`); `CreateObject`/`ReleaseObjects` throw `NotSupportedException`. The managed object implements `IMMNotificationClient.Interface`: added, removed and state-changed signal; property changes signal only for `PKEY_Device_FriendlyName` and `PKEY_DeviceInterface_FriendlyName` (compare fmtid and pid); default-device changes are ignored; every method returns `S_OK`. `IMMNotificationClient` is already appended to `NativeMethods.txt`.
- `src/ReplayBuffer.Platform.Audio/AudioDeviceHub.cs` -- implements `IAudioEndpointSource`. `ListPresent` = `AudioEndpoints.List` of both flows mapped to `AudioDevice` (`Available: true`). `EndpointsChanged` add/remove accessors under `_gate`; the first handler starts the watcher; `Dispose` stops it. Keep the hub's Subscribe path untouched. Expose `internal` access for tests to the CCW pointer (`InternalsVisibleTo` already exists via `AssemblyInfo.cs`; check).
- `src/ReplayBuffer.Engine/Audio/DeviceListPublisher.cs` (new) -- `(IAudioEndpointSource, Func<SettingsDocument>, Action<AudioDeviceList> publish, ILogger)`. Ctor lists once (on failure: empty present, logged) and builds `Current` without publishing. `OnEndpointsChanged`: list outside the lock, then under the lock store present, rebuild from the current document, publish if changed. `OnSettingsChanged(document)`: rebuild from cached present, publish if changed. Subscribes/unsubscribes itself; `IDisposable`.
- `src/ReplayBuffer.Engine/Ipc/EventHub.cs` -- ctor takes an optional initial `AudioDeviceList` placed in the initial state (no seq change).
- `src/ReplayBuffer.Engine/EngineHost.cs` -- `EngineHostOptions.AudioEndpoints` (`IAudioEndpointSource?`, null = the hub). Build the publisher before the `EventHub` (its `Current` seeds the hub; its publish delegate is wired through a field set right after), hook `load.Store.Changed` to it, register `AudioMessages.List`, dispose it before `Audio`. Update the class doc comment.
- Tests: `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` (a `devices.changed` sample; old-snapshot default), `tests/ReplayBuffer.Core.Tests/AudioDeviceListBuilderTests.cs` (new), `tests/ReplayBuffer.Platform.Audio.Tests/EndpointWatcherTests.cs` (new: real hub starts and stops watching cleanly; `ListPresent` matches `AudioEndpoints.List`; calling the CCW's `OnDeviceAdded`/`OnDeviceStateChanged` through its `IMMNotificationClient*` vtable raises Changed within 2 s, a burst raises once, an unrelated property key and a default-device change raise nothing), `tests/ReplayBuffer.Engine.Tests/DeviceListServeTests.cs` (new, in-process `EngineHost` over a real pipe like `IpcTests`, with a fake `IAudioEndpointSource`), and pass a fixed fake source in `IpcTests.StartHost` so its seq counts stay deterministic.

## Tasks & Acceptance

**Execution:**
- [ ] `src/ReplayBuffer.Contracts/Messages/Audio.cs`, `Engine.cs` -- payloads, messages, snapshot field -- the wire shape
- [ ] `tests/ReplayBuffer.Contracts.Tests/MessagesTests.cs` -- sample + default -- registry coverage tests demand it
- [ ] `src/ReplayBuffer.Core/Audio/AudioDeviceHub.cs`, `AudioDeviceListBuilder.cs` + `tests/ReplayBuffer.Core.Tests/AudioDeviceListBuilderTests.cs` -- port and merge -- matrix rows Same names, Configured but absent, Absent and unconfigured, Same id in two layouts, Wrong flow, ordering
- [ ] `src/ReplayBuffer.Platform.Audio/EndpointWatcher.cs`, `AudioDeviceHub.cs` + `tests/ReplayBuffer.Platform.Audio.Tests/EndpointWatcherTests.cs` -- notifications -- Burst and the real COM path
- [ ] `src/ReplayBuffer.Engine/Audio/DeviceListPublisher.cs`, `Ipc/EventHub.cs`, `EngineHost.cs` -- publish and serve -- Listing fails, Nothing changed, Layout edited
- [ ] `tests/ReplayBuffer.Engine.Tests/DeviceListServeTests.cs`, `IpcTests.cs` -- IPC end to end

**Acceptance Criteria:**
- Given a connected client and an engine whose endpoint source is a test double, when the double adds, removes or changes the state of an endpoint, then the client receives `devices.changed` with the new list within 2 s, and its `State.Devices` matches `devices.list`.
- Given two present Output Devices both named "Speakers" on different parent devices, when a client connects, then the snapshot lists both, with different `parentName`.
- Given a Global trackLayout holding a device that isn't present, when a client connects, then the snapshot lists it with `available: false`; and given a device that is neither present nor in a layout, then it isn't listed.
- Given the real Windows endpoints, when the published NativeAOT engine serves, then `devices.list` returns every present Output and Input endpoint, available, and the notification client registers and unregisters with no error or IL warning.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

Why a coarse `EndpointsChanged` and a full re-list rather than per-id events: the list is a few dozen endpoints and listing takes milliseconds; re-listing can't drift from Windows' state, and story 2.5 can diff the list for its rebinds. Callbacks run on MMDevice's threads, where waiting or calling back into the enumerator is unsafe, so they only set an event.

The initial list goes into the snapshot without an event, so a fresh engine's `seq` is still 0 and existing seq-counting tests hold.

CCW sketch:
```csharp
GetIUnknownImpl(out var qi, out var addRef, out var release);
var vtbl = (IMMNotificationClient.Vtbl*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(Wrappers), sizeof(IMMNotificationClient.Vtbl));
vtbl->QueryInterface_1 = (delegate* unmanaged[Stdcall]<IMMNotificationClient*, Guid*, void**, HRESULT>)qi; // AddRef_2, Release_3 likewise
IMMNotificationClient.PopulateVTable(vtbl);
```
`GetOrCreateComInterfaceForObject` returns an AddRef'd IUnknown; QueryInterface for `IID_Guid` gives the pointer to register. Release both after unregistering.

## Verification

**GPU lock:** before every build or test run, check that `%USERPROFILE%\Documents\repos\ReplayBuffer\.bench-running` doesn't exist; while it does, wait.

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` -- expected: 0 warnings, 0 errors
- `dotnet test --solution ReplayBuffer.slnx` -- expected: all pass (a real-time timing test that fails gets one rerun)
- `pwsh tools/publish-engine.ps1` -- expected: 0 IL/AOT warnings

**Manual checks (if no CLI):**
- Run the published engine `serve --instance ReplayBuffer-Test-2-3`, connect, and send `devices.list`: every endpoint from `debug-list` appears, with parent names. Plugging a device in is the builder's check.
