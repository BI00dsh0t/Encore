---
title: "Plain words everywhere: the glossary on every label, status and warning"
ticket: 11
status: done
---

# Plan: 10.11 Plain words everywhere

**Intent:** apply EXPERIENCE.md's plain-language rule to everything the earlier stories didn't rewrite. **Text only:** settings keys, error codes, message names, Contracts types, logs and automation ids keep the internal names (AD-22, AD-24). Log lines were left alone, apart from warning texts that are also logged.

## Done

- [x] **General:** the Overlay's dot gets its help line, "The small on-screen indicator that shows ReplayBuffer is capturing." **Sounds** needed nothing beyond its 10.9 title.
- [x] **Status header** (`StatusLine`):
  - "Ready to save Clips in {game}" (and "Ready to save Clips" with no game), "Finishing the save…" and "Waiting for a game";
  - the engine states as "ReplayBuffer's background service": connecting, connected, restarting, not running, keeps crashing, and disconnected.
- [x] **The reasons beside those states:** `EngineConnection`'s and `EngineWatchdog`'s problem texts, and `EngineClient`'s "didn't answer" text in Contracts (an exception message only, no type change).
- [x] **Tray:** "Ready to save Clips" for the hollow dot, and the crash-loop tooltip. The Overlay window's title is now "ReplayBuffer Overlay".
- [x] **The engine's `warning.raised` texts and the Overlay's notices,** with their codes unchanged:
  - memory margin and device dropout;
  - Sounds on a recorded device;
  - the Disk-mode stop when the drive is full;
  - Recording stop reasons, and the restart notice "The Recording continues in a new file after its video restarted";
  - the gaps after a video restart (reset-gap), and the encoder budget refusal;
  - graphics driver reset, and the give-ups after a reset or wake;
  - the Recording's chain deferral and the Record press for an unknown game;
  - interrupted Recordings, the rebuilt Library index, Cleanup's unreachable limit (now in GB), and the App and background-service restarts;
  - the "nothing to save yet" and "still saving the last one" reasons.
- [x] **App texts:** the Library's corrupt-index banner says "Clips folder", and the control gallery's sample strings follow the glossary.
- [x] **The glossary test** (`GlossaryTests.NoAppViewShowsAnInternalTerm`): every literal `Text`, `Content`, `Header` and `ToolTip` in the App's XAML is checked against the glossary's left column, as whole words ignoring case. Bindings are skipped. It reads the worktree's own XAML, the nearest `ReplayBuffer.slnx` above the test binary.

## Overlap

- I didn't touch 11.4's files (the Hotkeys page and `MonitorPicker`) or 11.3's monitor warnings: 11.3's branch had no commits when this started. Whichever merges second makes the glossary test pass on the combined XAML.

## Tests (lean)

- `GlossaryTests`:
  - it passes on the App's XAML, and fails on a fabricated view holding "Ring folder";
  - the status texts for Buffering, Draining and Replay Buffer off, two engine states, and the tray's state.
- Engine: `MemoryMarginReporterTests` and `DeviceDropoutReporterTests` assert the new texts with the codes unchanged. Core: `FeedbackRouterTests` asserts the encoder-restart notice's new text.
- Existing tests were updated only for the reworded strings: status line, main view model, tray, Recording stop reasons, the wake give-up, and the deferral filter.
- The General and Sounds pages in both themes are saved by `SettingsLayoutTests` as `settings-{general,sounds}-{fluent,gaming-dark}.png` beside the App test binary.

## Log

- 2026-11-05: built and verified; READY FOR QA.
- 2026-11-05: QA passed at 2967259. Its gap (C#-set strings are covered by spot checks only) is noted on 10.7 for the refactor sweep.
