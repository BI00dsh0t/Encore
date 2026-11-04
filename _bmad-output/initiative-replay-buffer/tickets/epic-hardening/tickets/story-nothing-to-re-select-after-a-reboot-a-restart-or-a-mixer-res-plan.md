---
title: 'Nothing to re-select after a reboot, a restart or a mixer restart'
type: 'feature'
ticket: '6'
created: '2026-11-04'
status: done
baseline_revision: '6879b67'
route: 'full'
route_source: 'auto'
risk: 'medium'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/CLAUDE.md'
warnings: []
deferred: []
---

<intent-contract>

## Intent

**Problem:** R6 (NFR-6; PLAN.md T-05 to T-07) has never been proven end to end. Each store already persists on its own: `settings.json` (settings, Game Profiles, the Track Layout, the Hotkeys), `runtime.json` (mic mute, 1.12/AD-5) and `library.json` (Favourites, 7.4). Story 2.5 rebinds an *open* device whose endpoint returns under the same id or a new id with the same names. One gap remained. If a mixer restart gives its endpoints new ids while nothing has them open (no game running), the next session subscribes to the Track Layout's saved ids, which are gone. The session's audio then fails to open, and the user has to re-select the devices.

**Approach:**
- **The fix (persistence-neutral, AD-4):** a session's device now carries its saved parent name (`SessionAudioDevice.ParentName`, filled by `SessionAudio.FromLayout`). `IAudioDeviceHub` gains `Subscribe(SessionAudioDevice, subscriber)`, a default interface method that falls back to the id overload. `AudioDeviceHub` implements it with `Resolve`:
  - the saved id when it is open or present;
  - else the one present endpoint of its flow with the same friendly and parent name;
  - else the saved id, which fails as before.
  `EncoderChain` and `TrackMeters` subscribe through it. Settings are never written: the Track Layout on disk keeps the ids the user chose.
- **The seam:** `EngineHostOptions.AudioHub` builds the engine's one hub. Tests pass the real `AudioDeviceHub` over 2.5's fake clients (`FakeDeviceBank`) and fake endpoint list (`FakeEndpointList`). A mixer restart then drives the real reconcile and rebind code.
- **A reboot** is a cold engine start of a Guid test instance on temp folders. **An App or engine restart** is our own processes being killed or quit and relaunched.

## Boundaries & Constraints

**Always:** AD-4: the hub never reads or writes settings. AD-20: one client per endpoint, so a name-resolved subscriber shares a rebound device's client. Lean tests. Heavy tests are `[Fact(Explicit = true)]` + `[Trait("Category","Heavy")]`, written but run by the GPU runner.

**Never:** no reboot, sign-out, service restart, default-device change or Windows sound setting change. Nothing touches the installed app, the builder's Library or the builder's settings. No Contracts change.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior |
|----------|--------------|---------------------------|
| Cold start | settings, a Game Profile with an override and an attribute, a Track Layout, Hotkeys, mic unmuted, a Favourite | each is the same in the first snapshot, the three files are byte-identical (not rewritten), and the game becomes the Target again on its existing profile |
| Mixer restart, same ids, while open | both Track devices removed then re-listed | dropped (silent Tracks) then resumed; settings.json unchanged |
| Mixer restart, new ids, while open | removed, then re-listed under new ids with the same names | rebound (2.5); settings.json unchanged |
| Default-device change | the same list, `EndpointsChanged` raised | nothing drops or reopens |
| New session after new ids | saved ids absent, one name match each | opens the renumbered endpoints, logged at Information |
| Ambiguous names | two present endpoints share the names | the saved id is used, and fails as before |
| Listing fails | `ListPresent` throws | the saved id is used (Debug log) |

</intent-contract>

## Code Map

- `src/ReplayBuffer.Core/Sessions/SessionSpec.cs`: `SessionAudioDevice.ParentName` (optional, last); `FromLayout` fills it.
- `src/ReplayBuffer.Core/Audio/AudioDeviceHub.cs`: `IAudioDeviceHub.Subscribe(SessionAudioDevice, IAudioPcmSubscriber)`, a default method.
- `src/ReplayBuffer.Platform.Audio/AudioDeviceHub.cs`: the overload and `internal Resolve`.
- `src/ReplayBuffer.Engine/Sessions/EncoderChain.cs`, `src/ReplayBuffer.Engine/Audio/TrackMeters.cs`: subscribe through the device overload.
- `src/ReplayBuffer.Engine/EngineHost.cs`: `EngineHostOptions.AudioHub`.
- `tests/ReplayBuffer.Engine.Tests/GameDetectionServeTests.cs`: `FakeProbe`, `FakeProcess` and `StayingLauncher` made internal for reuse.

## Tasks & Acceptance

**Execution:**
- [x] Core, Platform.Audio and Engine: the name resolution and the hub seam.
- [x] Tests, default run:
  - Platform.Audio: `DeviceDropoutTests.ASessionDeviceWhoseSavedIdIsGoneOpensTheEndpointWithItsNamesUnlessTwoShareThem`.
  - Engine, `NothingToReselectServeTests`:
    - `EveryPersistedValueIsUnchangedAfterAColdStartAndTheGameIsTargetedAgainWithItsProfile`: the reboot simulation, with a fake game probe.
    - `AMixerRestartRebindsEveryTrackWithSilenceOnlyInTheGapAndLeavesTheSettingsAlone`: the Track meters hold the devices open as a session would. Both Tracks go silent in each gap and loud after it. The same ids, then new ids, then a default-device change, then a fresh watch of the saved layout, which opens the new ids by name. `settings.json` is byte-identical throughout.
- [x] Heavy (written, not run here):
  - Engine: `ClipsEndToEndTests.TheReplayBufferStartsOnTheGameByItselfAfterEachColdStart`. Two cold starts with the GPU load running. Each buffers on it by itself and saves a Clip with named Tracks.
  - App: `NothingToReselectPairTests.EveryPersistedValueSurvivesAnAppKillAnEngineKillAndAQuitThenARunKeyLaunch`. The real pair runs through an App kill, an engine kill, and a Quit followed by a launch from `StartupRegistration.Command` plus `--instance`. Settings, mic mute, the Favourite, the Hotkeys and the three files are unchanged after each.

**Acceptance Criteria:**
- Given each restart, then every persisted value matches and nothing is rewritten.
- Given a game after each restart, then it is targeted on its existing profile (default run), and the Replay Buffer buffers on it and saves a Clip with every Track (Heavy).
- Given recreated endpoints (same id or new id), then each Track keeps its devices, is silent only in the gap, and the Track Layout on disk is unchanged.
- Given a default-device change, then no Track's device changes.
- Given new ids before any session, then the next session opens them by name with no re-select.

## Design Notes

- **Why resolve at subscribe, not rewrite settings:** AD-4 and 2.5 keep rebinding in memory. If the mixer's names come back under new ids for good, the layout still works on every start. The Track Layout page still shows the saved device as not connected, though, because `AudioDeviceListBuilder` matches by id. That is display only; capture is unaffected. It is a follow-up for the App's Track Layout page (2.2) if the builder sees it.
- **Ids across a real reboot:** Windows keeps MMDevice endpoint ids across reboots, so the cold-start path normally never needs the name match. It covers a mixer that re-creates its endpoints.
- **Human Checks for the builder:**
  1. Reboot. Without opening the Main App, start a game, and save a Clip. Every Track should be present, with the mic mute state and the Hotkeys as before the reboot.
  2. During a game, restart the BEACN app. Each Track should be silent only while it restarts. Save a Clip afterwards: every Track should have sound, and the Track Layout page should show the same devices.
  3. Restart the BEACN app with no game running, then start a game and save a Clip: every Track should have sound.

## Verification

**Commands:**
- `dotnet build ReplayBuffer.slnx -c Release` (expected: 0 warnings).
- `dotnet test --project tests/ReplayBuffer.<P>.Tests -c Release --no-build` for Core, Platform.Audio, Engine and App.
- `pwsh tools/publish-engine.ps1` (expected: no IL or AOT warnings).
- Heavy, one project at a time, for the GPU runner:
  - `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only --filter-method "ReplayBuffer.Engine.Tests.ClipsEndToEndTests.TheReplayBufferStartsOnTheGameByItselfAfterEachColdStart"`
  - `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build --explicit only --filter-method "ReplayBuffer.App.Tests.NothingToReselectPairTests.EveryPersistedValueSurvivesAnAppKillAnEngineKillAndAQuitThenARunKeyLaunch"`

## Auto Run Result

- **Summary:** the persistence suite and the mixer-restart suite pass in the default run. One fix: a session opened after a mixer gave its endpoints new ids now opens them by their saved names, with no re-select and no settings write.
- **Verification (2026-11-04):** see the session report: build, test counts and the publish.
