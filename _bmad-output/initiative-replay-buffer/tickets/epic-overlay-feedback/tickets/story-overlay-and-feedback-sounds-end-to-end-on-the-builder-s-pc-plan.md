---
title: 'Overlay and Feedback Sounds end to end on the builder''s PC'
type: 'chore'
ticket: '14'
created: '2026-11-04'
status: done
baseline_revision: 'ba7dbcd'
route: 'hitl'
risk: 'low'
context: ['{project-root}/CLAUDE.md']
---

# 5.14 Overlay and Feedback Sounds end to end on the builder's PC

The epic's closing check. The unattended part runs the epic-5 Heavy tests and one new Heavy end-to-end test against the real engine and the App's real Overlay and status line, on a throwaway test instance. What only a person can check is the ordered list below, for the builder.

## What was added

- `tests/ReplayBuffer.App.Tests/OverlayEndToEndTests.cs` (Heavy, Explicit). Nothing else ran the Overlay against the real engine. The test hosts the whole engine (`EngineHost.Start`) in-process on a throwaway test instance (`ReplayBuffer-Test-…` under a temp folder), with the Replay Buffer on, the real foreground Game Detection, the real pipe and the real `RegisterHotKey` Hotkeys. The App's own `EngineConnection`, `OverlayViewModel`, `OverlayWindow` and `MainViewModel` (the status line) sit on it, wired as `ShellApp` wires them: the real overlay-plane query (`Monitors.OverlayPlanes`) and the capture-exclusion hook. Bench's borderless GPU load (2560×1440, 60 fps) is the Target Game. Injected presses (`SendInput`) and `dev.simulate` drive it. The engine's Feedback Sounds go to a recording `IFeedbackSoundOutput`, so each is requested and identified but nothing plays aloud. It checks:
  - the hollow dot within 1 s of the Replay Buffer buffering, with the mic-muted icon (a test instance starts muted), and the load keeps the foreground;
  - the adaptive dot: with no overlay planes, `auto` hides the dot 3 s after a change and shows it again on the next (the Mic Mute press);
  - the mic-muted icon going with the Mic Mute press, and the status line's state text (`Buffering on <game> · Mic muted`, then without it);
  - the saving icon within 250 ms of a real Save press, held through the 5 s Save Cooldown and the write; a second press inside the cooldown makes no Clip;
  - queued messages: a failed save and a disk full sent together show one after the other, oldest first;
  - the red X while disk-full is active, the status line listing it newest first, and its Dismiss (`warning.dismiss`) clearing the red X;
  - the solid dot within 1 s of the stub Recording, and the hollow one within 1 s of its stop;
  - every visible App window, the Overlay included, carrying `WDA_EXCLUDEFROMCAPTURE`;
  - the five sounds requested in order (saved, failed, disk full, Recording started, Recording stopped) on the default communications endpoint.
- `src/ReplayBuffer.Engine/ReplayBuffer.Engine.csproj`: `InternalsVisibleTo` for `ReplayBuffer.App.Tests`, so that test can host `EngineHost`. No product code changed.
- Why not through `ReplayBuffer.App.exe --tray`: as 4.14 found, a test instance's engine keeps the Replay Buffer and Game Detection off, and `serve` has no switch to turn them on, so the hollow dot and the saving icon can't be driven through the exe; and the Overlay is excluded from capture, so its contents can't be read from outside the App. `bench overlay --host app` (5.8, 5.12) already covers the real exe with `dev.simulate`; this test covers the rest in one process.

## Unattended results

RESULTS-PENDING

## Builder's hand checks for epic 5 (in order)

One sitting with the Dev instance. Start `ReplayBuffer.Engine serve` (no `--debug-capture`) and the Main App, then work down the list. Each step names the Human Check Queue row it closes; the row has the detail.

**A failed save by hand:** there is no command-line sender for `dev.simulate` (see Findings), so the rows that say "send `dev.simulate saveFailed`" use a real failure instead. Set Save Cooldown to 0 on Settings > Replay Buffer, then press Save twice within a second: the first saves, the second is refused as "Clip not saved: clip too short", with the failed sound. Set it back to 5 afterwards. The disk-full red X and the stub Recording have no hand trigger; the automated run covers them, and 6.8's row covers a real Recording.

1. **Settings > Feedback Sounds (5.11).** Click Play on each of the five rows and listen: Clip saved and Save failed must sound clearly different. Switch Save failed off, pick another Output Device and back, check the page by eye.
2. **Overlay planes first (5.15, 5.12, part 1).** Read `dxdiag /t` "MPO MaxPlanes" as it is now. It decides what you should see below: with no planes, `overlayDot: auto` shows the dot for 3 s after each change; with planes, always.
3. **A borderless game (5.1, 5.3, 5.5, 6.8).** Play a borderless game with the Replay Buffer on.
   - The hollow dot appears top-right of the game's monitor within 1 s, 24 px in, and the game keeps focus (5.1).
   - Press Save: the green saving icon shows left of the dot at once and stays through the cooldown; a second press inside it changes nothing (5.3). You hear the saved sound.
   - Cause a failed save (cooldown 0, double press, above): "Clip saved" in green, then "Clip not saved: clip too short" in red, one after the other, 4 s each; check the text reads well (12 px, shade, ellipsis) and the dot doesn't move (5.5). You hear the saved sound, then the failed one.
   - Press the Mic Mute Hotkey: the mic-muted icon goes and comes with it.
   - Record start and stop with the Record Hotkey (Alt+F9), with Recordings on for the game's profile: the solid dot, the start chime and the stop sound (6.8).
   - Record or screenshot the monitor (Win+Shift+S, or OBS display capture): neither the dot nor the Main App appears (5.1).
4. **The adaptive dot by eye (5.15).** In the same game, set `overlayDot` to `always`, then `onChange`, then back to `auto`, and watch the dot follow each.
5. **Status line (5.10).** Unplug a device used by a Track: its dropout warning appears at the top of the status line with its time; Dismiss it. Kill `ReplayBuffer.Engine.exe` in Task Manager: "Restarting the engine…", then the engine-restarted warning. Check the line on each tab.
6. **Exclusive fullscreen by sound alone (5.2, 5.8 (1), the ticket's Verify).** Play each of your exclusive-fullscreen games (use PresentMon to tell legacy exclusive from fullscreen-optimized, as in the 5.2 row). Press Save, then cause a failed save (cooldown 0, double press). Without looking, tell saved from failed by sound. The game stays fullscreen and focused throughout; the dot shows only where Windows runs the game with fullscreen optimizations.
7. **See the dot over the three loads (5.2, second row).** `ReplayBuffer.Bench overlay`, a minute per mode, watching the top-right corner.
8. **Overlay planes and the presentation path (5.15/5.12 row, 5.8 (2)).** Work through the 5.15, 5.12 row's steps (re-read MaxPlanes with the second monitor off, RTX HDR/VSR/NIS off, the other overlays closed), and run 5.8 (2)'s two `bench overlay --modes borderless` runs. Where planes appear, run 5.12's `bench overlay --host app --dot-only` and `bench fps --overlay` re-runs.
9. **Capture cost (NFR-1 row).** Last, since it takes about 33 min per step: the NFR-1 (capture cost) row's `bench fps` steps from `research/capture-cost-regression.md` §6. The Overlay adds nothing measurable (5.12); this is capture's own cost, which the epic's Done when ("stays within the NFR-1 target") depends on.

The automated run covers the dot, saving icon and message timings, the queue, the icons, the status line, the sound requests and capture exclusion against the real engine over the synthetic load. The list above covers what it can't: real games, hearing the sounds, seeing the Overlay, a recorder, a real dropout and engine kill, exclusive fullscreen and the overlay-plane and capture-cost measurements.

## Findings

FINDINGS-PENDING

## Log

- 2026-11-04: branch `feature/5.14-overlay-sounds-end-to-end` cut from `develop` at `ba7dbcd`. Added `OverlayEndToEndTests`. Waited for another session's `bench soak` to release `.bench-running` before the GPU runs.
