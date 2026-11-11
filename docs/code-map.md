# Code map

A one-page tour for a new developer. The detail behind each class is in [code-notes.md](code-notes.md).

## The projects and who may reference whom

Encore is two processes. The **App** (WPF) shows the windows and tray icon. The **engine** (NativeAOT, no UI) watches for games,
captures, encodes and saves. They share only [Encore.Contracts](../src/Encore.Contracts).

| Project | Job | References |
|---|---|---|
| [Encore.Contracts](../src/Encore.Contracts) | The wire format: messages, settings catalog, error codes, supervision timings, `Instance` | nothing |
| [Encore.Core](../src/Encore.Core) | Rules and state with no platform code: the session orchestrator, Library, Rewind's ring, settings resolver | Contracts |
| [Encore.Media](../src/Encore.Media) | Timing and audio maths: the frame pacer, audio timeline and mixer, QPC clock | Core |
| [Encore.Platform.Windows](../src/Encore.Platform.Windows) | Game detection, Hotkeys, Recycle Bin, sleep and resume | Core, Media |
| [Encore.Platform.Audio](../src/Encore.Platform.Audio) | WASAPI capture, the audio device hub, Feedback Sound output | Core, Media |
| [Encore.Platform.Capture](../src/Encore.Platform.Capture) | Windows Graphics Capture, Desktop Duplication, the D3D11 device, the frame scaler | Core, Media |
| [Encore.Platform.FFmpeg](../src/Encore.Platform.FFmpeg) | Video and AAC encoders, the MP4 writers for Clips and Recordings | Core, Media |
| [Encore.Engine](../src/Encore.Engine) | The engine program: start-up, capture sessions, the pipe server, settings store, supervision | everything above except Desktop |
| [Encore.Platform.Desktop](../src/Encore.Platform.Desktop) | Windows pieces only the App needs: shell calls, Mica, the Overlay's window styles, the player's surface | nothing |
| [Encore.App](../src/Encore.App) | The WPF App: views, view models, tray, Overlay, player, engine connection | Contracts, Desktop (engine: build order only) |

Core has no platform code, and the App never references Core or the engine. Two files are shared as source instead:
[ClipPeakFile.cs](../src/Shared/ClipPeakFile.cs) and `MonitorCanvasRule.cs` (Core). [tests](../tests) mirror the projects one to one;
[bench](../bench) holds the measurement tool, its GPU load and an Overlay host, which never compile against the App.

[EngineStartup](../src/Encore.Engine/Hosting/EngineStartup.cs) is the **composition root**: the one place that builds platform types
directly and hands them to Core through interfaces (`IAudioDeviceHub`, `ICaptureSessionFactory`, `ILibraryWrites`, …). Elsewhere,
depend on the interface, and fake it in tests.

## From the game to a saved Clip

1. [GameMonitor](../src/Encore.Platform.Windows/GameMonitor.cs) finds a game; [SessionOrchestrator](../src/Encore.Core/Sessions/SessionOrchestrator.cs)
   (one thread, the only code that starts and ends sessions) starts a session for the Target Game.
2. [CaptureSession](../src/Encore.Engine/Sessions/CaptureSession.cs) owns one capture. Its source
   ([WindowCapture](../src/Encore.Platform.Capture/WindowCapture.cs)) hands each frame to `OnFrame`. The first frame is the session's **anchor**.
3. The current [EncoderChain](../src/Encore.Engine/Sessions/EncoderChain.cs) draws the frame into a pooled texture with
   [FrameScaler](../src/Encore.Platform.Capture/FrameScaler.cs), then [PacedVideoEncoder](../src/Encore.Platform.FFmpeg/PacedVideoEncoder.cs)
   uses [FramePacer](../src/Encore.Media/FramePacer.cs) to emit exactly one frame per output slot to [VideoEncoder](../src/Encore.Platform.FFmpeg/VideoEncoder.cs).
4. Audio takes its own path: [AudioDeviceHub](../src/Encore.Platform.Audio/AudioDeviceHub.cs) timestamps device packets, and one
   [TrackMixer](../src/Encore.Media/TrackMixer.cs) and AAC encoder per Track (`ChainAudio`) make the Tracks.
5. Encoded packets go to a [PacketSink](../src/Encore.Platform.FFmpeg/PacketSink.cs). With Rewind that is
   [ReplayRingSink](../src/Encore.Platform.FFmpeg/ReplayRingSink.cs), which copies them into the [PacketRing](../src/Encore.Core/Replay/PacketRing.cs),
   trimmed to the Buffer Window. A Recording also takes a copy through [RecordingTap](../src/Encore.Platform.FFmpeg/RecordingTap.cs).
6. A Save press reaches the orchestrator as a Hotkey ([HotkeyThread](../src/Encore.Platform.Windows/HotkeyThread.cs), then
   [HotkeyService](../src/Encore.Engine/Hotkeys/HotkeyService.cs)). `CaptureSession.ClipWrite` cuts the ring and
   [ClipWriter](../src/Encore.Platform.FFmpeg/ClipWriter.cs) writes a complete MP4 to a part file, then renames it.
7. The orchestrator reports the write to the [LibraryActor](../src/Encore.Core/Library/LibraryActor.cs) (`ILibraryWrites`: `BeginWrite`, then
   `EndWrite`). The actor adds the finished file as an item and the engine publishes `library.itemAdded`, so the App's list shows the Clip.

Times: `qpc` is the Windows performance-counter clock (`QueryPerformanceCounter` ticks), the engine's only timebase
([QpcClock](../src/Encore.Media/QpcClock.cs)). `…Us` is microseconds since the session's anchor, and `pts` is a stream's own presentation time.

## How the App and the engine talk

- The engine serves a named pipe, `Encore.Engine.v1.<instance>.<user SID>` ([PipeName](../src/Encore.Contracts/Ipc/PipeName.cs),
  [IpcServer](../src/Encore.Engine/Ipc/IpcServer.cs)). Frames are one JSON object per line ([Frames.cs](../src/Encore.Contracts/Ipc/Frames.cs)).
- **Commands** (App to engine) get exactly one reply, a result or an error with a code from [ErrorCodes](../src/Encore.Contracts/ErrorCodes.cs).
  **Events** (engine to App) carry a `seq`; `state.snapshot` sends everything first, then each event changes one field. Every message type is in
  [Messages](../src/Encore.Contracts/Messages), listed in [MessageTypes](../src/Encore.Contracts/Ipc/MessageTypes.cs). Fields are only ever added.
- The App side is [EngineConnection](../src/Encore.App/Engine/EngineConnection.cs); the engine side is [CommandRouter](../src/Encore.Engine/Ipc/CommandRouter.cs)
  and [EventHub](../src/Encore.Engine/Ipc/EventHub.cs).
- **Supervision is two-way** (timings in [Supervision.cs](../src/Encore.Contracts/Supervision.cs)). The App kills and relaunches a hung or crashed engine
  ([EngineSupervisor](../src/Encore.App/Engine/EngineSupervisor.cs)); the engine relaunches an attached App that exits unasked
  ([AppSupervisor](../src/Encore.Engine/Supervision/AppSupervisor.cs)). Both stop after a crash loop.
- An **instance** (`Encore`, `Encore-Dev`, or `Encore-Test-*` for tests) names the pipe, mutexes and data folders ([Instance](../src/Encore.Contracts/Instance.cs)).

## Where settings live and flow

- The catalog of keys, defaults and which a game may override is [Keys.cs](../src/Encore.Contracts/Settings/Keys.cs). A value is Global, with an optional per-game
  override; [SettingsResolver](../src/Encore.Core/Settings/SettingsResolver.cs) picks the one in force (override, else Global, else default).
- The engine owns `settings.json` (in the instance's roaming data folder) through [SettingsStore](../src/Encore.Engine/Settings/SettingsStore.cs): revision-checked,
  all-or-nothing, atomic writes. Runtime state such as mic mute is in `runtime.json` ([RuntimeStateStore](../src/Encore.Engine/Settings/RuntimeStateStore.cs)), never a setting.
- A settings page never writes. It sends `settings.update` through [SettingsSender](../src/Encore.App/Settings/SettingsSender.cs) against the revision it rendered;
  the engine commits and publishes `settings.changed`, and every page renders from that. Sessions resolve their settings from the document when they start.

## Naming rules

- `…ViewModel`: a page's state and commands, on the UI thread, with no WPF types where avoidable. `…View`: the XAML control; its code-behind only wires events to the view model.
- One public type per file. A class split by job is partial, as `CaptureSession.Clips.cs`. Interfaces are `I…` seams that tests fake.
- Names are whole words. The exceptions are `qpc`, `pts`, `fps` and Windows API names.
- Product terms are capitalised and spelled as in the UI: Clip, Recording, Rewind, Buffer Window, Target Game, Game Profile, Track, Track Layout, Output Folder, Hotkey.
  `GlossaryTests` in [Encore.App.Tests](../tests/Encore.App.Tests) checks the words the App shows.
- Comments are one line and say why; longer rules go in [code-notes.md](code-notes.md), under the class.

## Where to start reading

| Feature | Start at |
|---|---|
| Rewind | [SessionOrchestrator](../src/Encore.Core/Sessions/SessionOrchestrator.cs), then [CaptureSession](../src/Encore.Engine/Sessions/CaptureSession.cs) and [PacketRing](../src/Encore.Core/Replay/PacketRing.cs) |
| Recording | [Recordings](../src/Encore.Core/Sessions/Recordings.cs), `CaptureSession.Recording.cs`, [RecordingWriter](../src/Encore.Platform.FFmpeg/RecordingWriter.cs) |
| Library | [LibraryActor](../src/Encore.Core/Library/LibraryActor.cs) (engine side), [LibraryViewModel](../src/Encore.App/Library/LibraryViewModel.cs) (App side) |
| Player | [PlayerViewModel](../src/Encore.App/Library/PlayerViewModel.cs), [WindowsMediaPlayer](../src/Encore.App/Library/WindowsMediaPlayer.cs), [VideoSurface](../src/Encore.Platform.Desktop/VideoSurface.cs) |
| Clip creator | [ClipCreatorViewModel](../src/Encore.App/Library/ClipCreatorViewModel.cs), then the engine's [ClipMediaWorker](../src/Encore.Engine/ClipMediaWorker.cs) |
| Overlay | [OverlayViewModel](../src/Encore.App/Overlay/OverlayViewModel.cs), [OverlayWindow](../src/Encore.App/Overlay/OverlayWindow.cs) |
| Settings pages | [SettingsViewModelFactory](../src/Encore.App/Settings/SettingsViewModelFactory.cs) lists them; [SettingsViewModel](../src/Encore.App/Settings/SettingsViewModel.cs) is their base |
| Start-up | the App: [ShellApp](../src/Encore.App/ShellApp.cs); the engine: [Program](../src/Encore.Engine/Program.cs), then `EngineStartup` |
