---
title: 'Recordings end to end on the builder''s PC'
type: 'chore'
ticket: '13'
created: '2026-11-04'
status: done
baseline_revision: '23e29ec'
route: 'hitl'
risk: 'low'
context: ['{project-root}/CLAUDE.md']
---

# 6.13 Recordings end to end on the builder's PC

The epic's closing check. The builder has paused GPU tests, and most of epic 6 captures, so the unattended part covers only what needs no capture. It drives the real engine and the App's real pages on a throwaway test instance with temp folders. Everything that needs a real capture is either a Heavy test listed below as "not run (GPU tests paused)" or a step in the builder's ordered list.

## What was added

- `tests/ReplayBuffer.App.Tests/RecordingsEndToEndTests.cs` (Heavy, Explicit, CPU only). It hosts the whole engine (`EngineHost.Start`) in-process on a throwaway test instance, `ReplayBuffer-Test-recordings-<guid>`, and asserts `IsTest`. The Replay Buffer is set off explicitly, so the engine builds no Recordings, Monitor Recording or game detection. The test uses the App's own `EngineConnection` and the real `MainViewModel`'s pages. `ffmpeg.exe` from `native/ffmpeg` makes a 4 s fragmented MP4 on the CPU (libx264, `empty_moov`, 1 s fragments), shaped like a Recording. There are three engine runs, and the test checks, in order:
  - **Recordings switch (R9):** a Game Profile, created over IPC as detection would, has Recordings off by default. The Game Profiles page switches them on, and `recordingsEnabled` is saved.
  - **`monitors.list`:** this PC's monitors come back with exactly one primary, listed first (`\\.\DISPLAY1` LC27G7xT 2560×1440 primary, `\\.\DISPLAY2` LS28AG700N 3840×2160). The Recordings page offers "Primary monitor" and then each one. Picking the second monitor saves `monitorRecordingMonitor`.
  - **Hotkeys rows per mode (R9, 6.9):** in Toggle mode the page shows Save Clip, Mic mute, Record (toggle) and Monitor Recording (toggle). Record start, bound in that mode, reads "Not used in this Record mode", and Monitor Recording (toggle) reads "Working". Switching to Separate saves `recordHotkeyMode`, shows the four start and stop rows, and the engine registers Record start and stop ("Working"). The toggle row then reads "Not used in this Record mode". The keys are Ctrl+Alt+Shift+F20 to F22, which nothing else uses, and with Recordings not built a press would do nothing.
  - **Round trip:** a new engine run reads every setting back. The pages show Recordings on, Separate mode, both Record bindings and the chosen monitor, and the effective settings match.
  - **Recording names and folders (R26, AD-21):** Core's `OutputPathAllocator` on the engine's own document gives `out\Alpha\Alpha_<yyyy-MM-dd_HH-mm-ss>.mp4`, then `_2` for a second one in the same second, and `out\Monitor Recordings\Monitor_<stamp>.mp4`. The profile's folder is `Alpha`. With the fragmented MP4 at both paths, the Library lists both with a length and groups them as "Alpha" and "Monitor Recordings".
  - **An interrupted Recording (R11, FR-12):** `runtime.json`'s `openRecordings` lists two files. One is the MP4 torn 200 bytes into a fragment; the other is cut before its first fragment. At the next start the torn one is trimmed to its last whole fragment (3 kept, about 3 s). It is complete (`Mp4File.IsComplete`), ffprobe reads its length, ffmpeg decodes it with no error, and the Library lists it. The empty one is deleted. Two `recording-interrupted` warnings name the paths and show on the App's status line, and `openRecordings` is cleared from `runtime.json`.
  - The engine log across all three runs has no `Detected`, `Capturing`, `Recording started`, `Recording to` or `Monitor Recording to` line.
- No product code changed.

## Unattended results

Run on 2026-11-04 in this worktree, Release, with no capture and no GPU work.

| What | Tests | Result |
|------|-------|--------|
| Recordings switch, `monitors.list` and the Recordings page, Hotkeys rows per mode and registration, settings round trip, Recording and Monitor Recording names and folders in the Library, interrupted fragmented MP4 trimmed or deleted with `recording-interrupted` | `RecordingsEndToEndTests` (Heavy, new, CPU only) | pass: 19 of 20 runs after one fix to the test. The first run failed on the test, not the product: `runtime.json`'s folder didn't exist yet, and ffmpeg's last tiny audio-only fragment was torn instead of a real one. After the fix, the third run failed and its output wasn't kept; the 17 runs after it passed (see Findings) |
| App default suite | `ReplayBuffer.App.Tests` | 126 passed, 18 skipped (explicit), 0 failed |
| Build | `dotnet build ReplayBuffer.slnx -c Release` | 0 warnings, 0 errors |

Totals: 127 passed, 0 failed.

Scope: the diff adds one App.Tests file and no product code, so only App.Tests ran, and there was no NativeAOT publish. The capture-free parts of Engine, Core and FFmpeg that epic 6 touched were QA'd in 6.1 to 6.12.

Not run (GPU tests paused by the builder). These are the ticket's automated checks that need a real capture, with the Heavy tests that already cover each one:
1. Record presses ignored with Recordings off, and both Hotkey modes starting and stopping a real Recording: `RecordingCaptureTests` (and `SessionOrchestratorTests` for the gate, in the default run).
2. The solid dot and both sounds during a Recording: no capture test; 6.8's Core tests cover the notices in the default run. Builder step 2.
3. Clips saved mid-Recording with no glitch in either: `RecordingCaptureTests.ClipsDuringARecordingLeaveItUnbrokenAndAChainChangeWaitsForItsStop`.
4. The game's exit finishing a complete file in its folder: `RecordingCaptureTests.KillingTheTargetMidRecordingFinishesItInTheGamesFolderAsGameExit`.
5. Engine kills each losing at most 1 s, every Track in sync: `RecordingCrashTests`. The trim-at-start half ran here, on a pre-made file.
6. A 4-hour Recording as one file: `LongRecordingTests.FourHoursOfThreeTracksAreOneFileWithEveryTrackTheVideosLength`. It is FFmpeg and CPU, but long, and it encodes.
7. A forced encoder restart giving two playable files: `RecordingCaptureTests.AWindowResizeKeepsOneFileAndAnEncoderRestartStartsASecond` and `AFailedRestartRebuildFailsTheSessionWithEncodeFailed`.
8. A filled volume stopping the Recording with a playable file, the red X and the sound: `DiskFullRecordingTests.AFullDiskLeavesTheFileAtItsLastWholeFragmentWithEveryTrack`. Builder step 7.
9. A Monitor Recording with and without a game, without the App's windows, and on the Desktop Duplication fallback: the eight `MonitorRecordingCaptureTests` (they take `.bench-running` themselves).
10. `BeginWrite` and `EndWrite` for every Recording: the recording library in `RecordingCaptureTests`. Builder step 5 checks the lock in the real Library.
11. The installed build with injected Hotkeys against the synthetic game window, the ticket's integrated suite: not built. It would capture, so it waits for the GPU pause to lift. Items 1 to 10 cover its parts one test class each.

When GPU tests resume, run them one project at a time:

`dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only --filter-class "*Recording*"`

`dotnet test --project tests/ReplayBuffer.Platform.FFmpeg.Tests -c Release --no-build --explicit only --filter-class "*Recording*"`

## Builder's hand checks for epic 6 (in order)

Do these in one sitting with the Dev instance, once GPU tests are back on. Start `ReplayBuffer.Engine serve` (no `--debug-capture`) and the Main App. Each step names the Human Check Queue row it closes; the row has the detail. Recordings go to `<Output Folder>\<game>` and Monitor Recordings to `<Output Folder>\Monitor Recordings` (by default `Videos\<instance>`).

1. **The pages (6.11, 6.3).** Open Settings → Game Profiles and switch Recordings on for the game you'll play. Open Settings → Hotkeys: in Toggle mode, bind Record (toggle), default Alt+F9, and Monitor Recording (toggle). Switch to Separate and bind Record start and stop, and Monitor Recording start and stop. Switch back to Toggle. Open Settings → Recordings and pick your second monitor. Check each page by eye; `recordings-settings.png` and `hotkeys-settings.png` beside the App test binary show the same pages.
2. **A game Recording with its dot and sounds (6.1, 6.3, 6.8).** Play a detected borderless game with a three-Track layout. First, switch Recordings off for it and press the Record Hotkey: nothing happens. Switch Recordings back on and press it. You should hear the start chime, see "Recording started", and see the Overlay's solid dot. Record about a minute, then press it again: you should hear the stop sound and see "Recording stopped".
3. **Separate mode (6.3, 6.11).** Switch Hotkeys to Separate and record again with Record start and Record stop. A second Record start while it runs does nothing. Switch back to Toggle.
4. **Clips during a long session (6.5, 6.6, the ticket's builder session).** Start a Recording and play for at least 30 minutes. Press Save a few times along the way. Each Clip should save, and the Recording should keep going with no glitch at the Clip's moment.
5. **Locked while written, then the game's exit (6.4, R10).** During that Recording, open the Library tab. The Recording's row is locked, with Rename and Delete greyed out. Exit the game. The Recording should stop with "Recording stopped: the game exited" and the stop sound, and the file should show in its game's folder and unlock in the Library.
6. **Play the files (6.1, 6.6, R7).** Play the long Recording and a Clip from it in VLC, and run `ffprobe` on the Recording. It should be one fragmented MP4 (moov, then moof fragments). Every named Track should play in sync to the end, with no glitch at the Clips. DaVinci Resolve isn't installed (2.9's row). If you install it, open the Recording there too and check the Track names and order.
7. **A full disk (6.7).** Optional, on a small scratch volume: point the Output Folder at a nearly full USB stick or VHD. Record until it fills. The Recording should stop with a playable file, the red X and the disk-full sound. Point the Output Folder back.
8. **A Monitor Recording of the second monitor (6.9, 6.11).** With no game running, press Monitor Recording (toggle), and move windows on the second monitor for about 30 s. Open the Main App and let the Overlay show there. Press it again. The file in `Monitor Recordings` should show the second monitor, with none of the Main App's windows or the Overlay. Repeat with a game Recording running at the same time: both files should play. Then do it in Separate mode with Monitor Recording start and stop.
9. **Monitor Recording on the Desktop Duplication fallback (6.10, 6.9).** No setting or switch forces the fallback in the real app (see Findings). Run the forced-fallback Heavy tests at the physical console, one at a time:

   `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --explicit only --filter-method "*DesktopDuplication*"`

   This covers `WithWgcFailingAMonitorRecordingFallsBackToDesktopDuplication`, `ALostDuplicationIsRecreatedIntoTheSameRecording` and `AWindowExcludedFromCaptureNeverShowsInADesktopDuplicationRecording`. The last one shows that a window excluded from capture stays out of a fallback recording. Whether the real Main App and Overlay also stay out on the fallback path can only be seen once there is a way to force it.
10. **Kill the engine mid-Recording (6.2, R11).** Start a Recording, wait 20 s, and kill `ReplayBuffer.Engine.exe` in Task Manager. The App restarts it. The status line should show "The Recording … was cut short", and the file should play in VLC to within about 1 s of the kill, with every Track in sync.
11. **Optional: a power cut (6.2, epic Note).** Mid-Recording, cut the power (or hold the power button). After restarting, the same `recording-interrupted` warning should show, and the file should play up to about 1 s before the cut.

A forced encoder restart has no production trigger yet (epic Note, 6.6), so it is only the Heavy test in item 7 above, not a hand check.

The automated run covers the capture-free part of each Done-when line against the real engine and pages. The list above covers the rest: real capture and Hotkey presses, sounds, the dot, the players, a full disk, a second monitor, and an engine kill.

## Findings

- None of the capture-free epic-6 checks failed.
- **Finding (not fixed: not a one-line change):** nothing outside the tests can force the Monitor Recording's Desktop Duplication fallback. `CaptureSessionOptions.WgcFault` is a test seam only, so the builder can't confirm that the real Main App and Overlay are hidden from a fallback recording, which is 6.10's Human Check. A dev-only switch (a `dev.simulate` event, or a `serve --debug-capture` flag that sets `WgcFault`) would make it checkable. Queued for a later story or the builder's call.
- **Observation:** `RecordingsEndToEndTests` failed once in 20 runs after its fix, and its output wasn't kept (it ran with summary output only). The 17 runs after it all passed in about 5 s each. It may be a timing wait. If it recurs, run it with `--output Detailed`; it is Heavy and Explicit, so the default run never sees it.
- GPU pause: no capture happened. The test asserts `IsTest` and sets the Replay Buffer off, and its engine log has no `Detected`, `Capturing` or Recording line. The engine did register three global Hotkeys (Ctrl+Alt+Shift+F20 to F22) for a few seconds, which is not capture.

## Log

- 2026-11-04: branch `feature/6.13-recordings-end-to-end` cut from `develop` at `23e29ec` (6.1 to 6.12 and the `e2e-test-instance-names` bugfix merged). Added `RecordingsEndToEndTests`. It passed 19 of 20 runs with no capture; the App default suite passed (126), and the build had 0 warnings. Wrote the ordered epic-6 checklist above, and pointed the epic-6 Human Check Queue rows (6.1, 6.8, 6.11, plus a new 6.9/6.10 row) at it.
