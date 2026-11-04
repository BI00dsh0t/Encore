---
title: 'Clips end to end on the builder''s PC'
type: 'chore'
ticket: '14'
created: '2026-11-04'
status: review
baseline_revision: '157826b'
route: 'hitl'
risk: 'low'
context: ['{project-root}/CLAUDE.md']
---

# 4.14 Clips end to end on the builder's PC

The epic's closing check. The unattended part runs the epic-4 Heavy tests and one new Heavy end-to-end test against the real built App and engine on a throwaway test instance. What only a person can check is the ordered list below, for the builder.

## What was added

- `tests/ReplayBuffer.Engine.Tests/ClipsEndToEndTests.cs` (Heavy, Explicit; Memory and Disk). Nothing else covered the whole chain from a Hotkey press to a Clip. The test hosts the whole engine (`EngineHost.Start`) in-process on a throwaway test instance (`ReplayBuffer-Test-…` under a temp folder), with the Replay Buffer on, the real foreground Game Detection, the real pipe (`EngineClient`) and the real `RegisterHotKey` Hotkeys. Bench's borderless GPU load (2560×1440, 60 fps) is the Target Game. Injected presses (`SendInput`, Ctrl+Alt+Shift+F22) write Clips to a temp Output Folder. It checks:
  - every Clip is faststart (moov before mdat) and has named audio Tracks, each within 50 ms of the video's length, with no `.rbpart` left;
  - the first Clip holds the 8 s Buffer Window, less up to one GOP (6.5–8.1 s);
  - a press inside the 5 s Save Cooldown makes no Clip and no `clip.saving`;
  - a Global Buffer Window change (8 → 3 s) and the game's own `video` override (1280×720 → 640×360) leave the next Clip as it was and take effect in the Clip after it;
  - the Target is the load's Game Profile, and Disk mode creates `ring-*.bin` under the test `ringPath`.
- `HotkeyServeTests.Input` became `internal` so the new test reuses its `SendInput` helper.
- Why not through `ReplayBuffer.App.exe --tray`: the first attempt did, and timed out waiting for buffering. On a test instance the engine keeps the Replay Buffer and Game Detection off (`EngineHost`: `ReplayBuffer ?? !instance.IsTest`), and `serve` has no switch to turn them on, so a test instance driven through the App can never make a Clip. The App doesn't sit on the press-to-Clip path (the engine owns the Hotkeys), so the test hosts the engine instead of adding a product switch. The App's part stays in the hand checks.

## Unattended results

Run on 2026-11-04 in this worktree, Release, holding `.bench-running` for the GPU runs (released after). RTX 5090, NVENC.

| What | Tests | Result |
|------|-------|--------|
| Hotkey → Clip end to end, Memory | `ClipsEndToEndTests(Memory)` (Heavy, new) | pass: Clips of 7.23 s and 6.67 s at 1280×720, then 2.83 s at 640×360; Everything Track within 7 ms of the video each time; the cooldown press was ignored |
| Hotkey → Clip end to end, Disk | `ClipsEndToEndTests(Disk)` (Heavy, new) | pass: 7.33 s, 6.73 s at 1280×720, 2.77 s at 640×360; `ring-*.bin` present |
| Per-profile layout and video at the next Reset, no gap | `ChainSwitchCaptureTests` (Heavy) | pass |
| Injected Save press reaches the engine | `HotkeyServeTests.AnInjectedPressArrivesPromptlyAndIsLogged` (Heavy) | pass: 20 presses, 0.57–1.37 ms |
| Disk ring Clip byte-identical to Memory ring Clip | `DiskRingClipTests` (Heavy) | pass |
| Exact cut, two Clips meet with no frame missing or repeated | `ReplayCutTests` | pass |
| Save, too-short, failed save and retry, kill drain | `ReplayClipCaptureTests` | pass (2) |
| Rings, writer queue, clip claim, estimate, memory margin, Hotkey registration and rebind | `ReplayRingsTests`, `WriterQueueTests`, `ClipClaimTests`, `EstimatePublisherTests`, `MemoryMarginReporterTests`, `HotkeyServeTests` (default) | pass (16) |
| Core (ring generations, cut, estimate, settings resolver, Memory vs Disk identity) | `ReplayBuffer.Core.Tests` | pass (110) |

Totals: 134 passed, 0 failed. The App, Bench and other projects weren't run: the diff touches only `Engine.Tests`, and their epic-4 parts (the Settings pages) are view-model tests, already QA'd in 4.10–4.12.

Not run, left to the builder (with the reason):
- A real game, real key presses, fullscreen focus, and saving with the Main App closed: these need a person at the console.
- The ticket's ±20% CBR estimate on the live engine: 4.10 measured it on the 5-minute ring, and nothing here changed it.
- A real mic for the per-profile Mic Device and mute (4.11): there's no mic on the test instance.
- VLC and DaVinci Resolve playback: VLC playback of a Clip is 4.1's queued check, and Resolve isn't installed.
- The Settings pages by eye (4.10, 4.11, 4.12).

## Builder's hand checks for epic 4 (in order)

One sitting with the Dev instance. Start `ReplayBuffer.Engine serve` (no `--debug-capture`) and the Main App, then work down the list. Each step names the Human Check Queue row it closes.

1. **Settings > Hotkeys (4.12).** Rebind Save to a new combination. Try the same combination on another row: it's refused. Check the page by eye.
2. **Settings > Replay Buffer (4.10).** Change the Buffer Window, the bitrate, CBR/VBR, and Memory/Disk. The estimate updates at once and says "worst case" for VBR. A Buffer Window bigger than your free RAM shows the red warning and still saves. Set it back to something normal (e.g. 120 s) and keep Memory mode.
3. **Settings > Track Layout.** Use three Tracks (BEACN Game, Chat, Mic) as the Global layout.
4. **Settings > Game Profiles (4.11).** Pick game A under "Overrides for". Untick Use Global for Output and choose 1920 × 1080. Untick Use Global for the Track Layout, add a different Mic Device, and Save.
5. **Play game A for 30 minutes in fullscreen, saving as you go (4.1, 4.2, 4.12, the ticket's Verify).**
   - Press Save (the new binding) a few times through the session. Every press outside the 5 s cooldown should make a Clip in `<Output Folder>\<game>`, and a quick double press should make only one.
   - Once, press Save twice about 10 s apart.
   - Once, press the Mic Mute Hotkey and speak, then unmute. Only game A's Mic Device should go silent in that stretch.
   - Close the Main App (the engine keeps running) and press Save once more.
6. **Quit test (4.3).** Close game A (or kill it in Task Manager) and press Save within 30 s. Then wait more than 30 s and press again: no Clip.
7. **Game B.** Play another game for a minute and save one Clip. It should use the Global layout and resolution.
8. **Check the Clips.**
   - Open them in VLC: every named Track plays, in sync, and each Clip ends at its press.
   - The two Clips 10 s apart play back to back with no gap or repeat.
   - The 4.3 Clip holds game A's last moments.
   - `ffprobe` game A's Clips: 1920×1080 and its own Tracks. Game B's: the Global values.
   - Open one game A Clip in DaVinci Resolve (installed by then?): the Track names are listed.
9. **Use Global (4.11).** Back on Game Profiles, tick Use Global for game A and Save. Play it again: the Clip after the next Reset (after one save) has the Global resolution and layout.

The automated run already covers the cut, Tracks, length, cooldown, Memory and Disk modes, the Buffer Window change and a per-profile override through the real App with synthetic presses. The list above covers what it can't: real keys, real games, fullscreen focus, the App closed, a real mic, a real game exit, the pages by eye, and the players.

## Findings

- None failed.
- Observation, not a defect: on a test instance the engine has no way to turn the Replay Buffer on from the command line, so neither `ReplayBuffer.App.exe` nor the published `serve` can be driven to a Clip without touching the builder's Dev instance. That's by design (other tests must never see a stray session), but it means the "against the installed build" half of the ticket's Verify is only the builder's hand run. A `serve --replay-buffer` switch for test instances would let a future closing story drive the real exe. Not added here (it's a product change).
- Observation: a Clip from a full 8 s buffer is 7.2–7.3 s long. The Clip starts at the first keyframe inside the Buffer Window (a GOP is at most 1 s), so it can be up to one GOP short of the window. That matches R2 ("never holds more than it") and AD-8.

## Log

- 2026-11-04: branch `feature/4.14-clips-end-to-end` cut from `develop` at `157826b`. A first end-to-end test through `ReplayBuffer.App.exe --tray` timed out (no Replay Buffer on test instances); it moved to an in-process `EngineHost`, which passed. Ran the epic-4 Heavy and default tests. Pointed the six epic-4 Human Check Queue rows at the checklist.
