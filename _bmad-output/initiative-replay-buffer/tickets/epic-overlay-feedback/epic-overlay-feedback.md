---
id: 5
type: epic
title: "Overlay and Feedback Sounds"
parent: initiative-replay-buffer
covers: [R6, R8, R36]
sources: []
companions: []
after: []
refined: true
risk: medium
---

# Overlay and Feedback Sounds

## Description

This epic tells the user what happened without breaking their focus. It provides:
- An always-on-top Overlay, outside the game process: a hollow or solid dot, a green saving icon through the Save Cooldown, a message queue, the mic-muted icon, and the disk-full red X.
- Feedback Sounds, each of which can be switched off.
- The Main App status line.

It starts by measuring Q-4: which of the builder's games run in exclusive fullscreen, and whether the Overlay costs FPS in borderless.

## Outcome

In any game, the builder knows a Clip saved or failed, by sight where the Overlay can show and by sound everywhere, with no FPS cost beyond NFR-1 (R6, R8).

## Requirements

- R1 (parent R6, FR-6; architecture AD-3, AD-15): The Overlay is a topmost, click-through, never-activated WPF window in the App that renders only `capture.stateChanged` and `warning.raised`. It shows a hollow red dot while the Replay Buffer is buffering or draining, a solid red dot while a Recording or Monitor Recording is running, and nothing when all are off; the dot appears or disappears within 1 s of the state change. It never minimizes, takes focus from or interrupts a game, and over exclusive fullscreen it behaves as the Q-4 measurement decides. It dies with the App; the engine's sounds cover that gap.
- R2 (parent R6, FR-6; architecture AD-10): A green saving icon shows within 250 ms of an accepted Save Hotkey press and stays for the whole Save Cooldown and while a Clip is being written. Presses inside the cooldown change nothing.
- R3 (parent R6, FR-6, FR-7, FR-14, FR-20): Messages that arrive close together queue, never overwrite: Clip saved, save failed with a short reason, disk full, and the Recording notices. The Overlay also shows a mic-muted icon while mute is on, and a red X for disk full until it is dismissed or the next Recording starts. Warnings raised with `overlay: false` (device dropouts) never show.
- R4 (parent R8, FR-39; architecture AD-1, AD-15): The engine plays the Feedback Sounds (Clip saved, save failed, Recording started, Recording stopped, disk full) on `feedbackSoundDevice`, by default the default communications endpoint. Each can be switched off (`feedbackSounds`). A saved Clip and a failed save can be told apart by sound alone. A Track that loopback-captures the sound endpoint raises `feedback-device-captured`.
- R5 (parent R8, FR-39; architecture AD-3): The Main App status line shows the current capture state and the recent warnings, including epic-multi-track-audio's device dropouts and the engine restarts reported by epic-engine-baseline and epic-hardening. Dismissing a warning is the `warning.dismiss` command.
- R6 (PLAN.md §7.1; architecture AD-15): Every window the App creates (the Overlay, toasts, the Main App and its dialogs, the tray menu) sets `WDA_EXCLUDEFROMCAPTURE`, so none appears in a capture.
- R7 (parent R36, NFR-1; PRD Q-4): Q-4 is measured: whether the Overlay can show over the builder's exclusive-fullscreen games, and whether it costs FPS or the fastest presentation path (independent flip) in borderless. epic-engine-baseline's benchmark, rerun with capture and the Overlay on, stays within the NFR-1 target.

## Constraints

- (architecture AD-1, AD-15) The engine owns the Feedback Sounds; the App owns the Overlay and never plays sounds. Every state the Overlay and status line render is a field of `state.snapshot` (AD-3).
- (architecture AD-2) The App's Win32 calls (window styles, capture exclusion, the fullscreen check) live in `ReplayBuffer.Platform.Desktop`; the sound render client lives in `ReplayBuffer.Platform.Audio` behind a Core port.
- (architecture AD-22) Contracts change additively only: new fields on `capture.stateChanged` and new codes in `Contracts.ErrorCodes`, in the `Capture.cs` and `Feedback.cs` areas. The `feedbackSounds` and `feedbackSoundDevice` keys already exist in story 1.4's catalog.
- (NFR-3) The Overlay is the app's own window: nothing is drawn in, injected into or hooked onto the game process.

## Done when

- The Q-4 measurement is recorded, and the Overlay's behaviour in exclusive fullscreen is decided from it.
- The dot appears or disappears within 1 s of a state change, the saving icon within 250 ms of the press, and messages queue. The Overlay never appears in captured video and never takes focus from a game (R6).
- In an exclusive-fullscreen game, a saved Clip and a failed save can be told apart by sound alone. Each sound can be switched off. The status line shows state and warnings, including epic-multi-track-audio's device dropouts and epic-hardening's engine restarts (R8).
- Every window the app creates (the Overlay, toasts, the Main App) is excluded from capture (PLAN.md §7.1).
- epic-engine-baseline's benchmark, rerun with the Overlay on, stays within the NFR-1 target. This is the Overlay part of R36. Verified integrated on the builder's PC.

## Boundaries

- Out: the events themselves (epic-replay-buffer-clips and epic-recordings emit them; epic-multi-track-audio supplies the mute state).
- Handoff: the Overlay and sound channel used by epic-recordings for the solid dot, the red X, and the Recording started/stopped notices. This epic verifies those with a stub Recording state; epic-recordings verifies them for real.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R6, R8, R36; Notes (Q-4)
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, AD-1, AD-2, AD-3 (capture.stateChanged, warning.raised), AD-15, AD-16 (instances), AD-22
- prd — _bmad-output/initiative-replay-buffer/prd-replay-buffer/prd-replay-buffer.md, FR-6, FR-7, FR-39, NFR-1, §9 Q-4
- architecture input — PLAN.md, §7.1
- addendum — _bmad-output/initiative-replay-buffer/prd-replay-buffer/addendum.md, Review-driven mechanism notes (Overlay and independent flip)

## Notes

- Decision: 2026-11-02, refined unattended from the initiative's lines, the PRD FRs and the architecture ADs the epic cites. Q-4 is the one thing only measurement can answer, so it is spike 2, run on the tracer's real Overlay with story 1.14's bench.
- Decision: 2026-11-02, the tracer bullet is story 1: the orchestrator's `capture.stateChanged` → IPC → a topmost, click-through, capture-excluded Overlay window that draws the dot. The builder plays a borderless game and sees the dot come and go with the Replay Buffer.
- Decision: 2026-11-02, the least-certain item is spike 2 (Q-4), so it comes right after the tracer; story 8 builds what it decides.
- Decision: 2026-11-02, the sequencing:
  - The Overlay lane runs 1 → 3 → 5 → 7 → 8 → 9, because all of them change the Overlay window or its view model.
  - The engine lane runs 4 → 6: the FeedbackRouter, then the sounds it names. Story 3 also waits on 4, since both change the SessionOrchestrator.
- Decision: 2026-11-02, the set check's findings applied: story 10 waits on 1.8 and 4.9 for the warnings it names, `feedback.preview` moved to story 6 so story 11 is UI only, story 8 is hitl (PresentMon and exclusive fullscreen need the GPU), story 12's Bench launches the App as a separate process (AD-2), and the spike records `SHQueryUserNotificationState` per game besides PresentMode.
  - The Main App lane runs 10 → 11, after epic 4's Settings pages (story 4.12).
  - Story 12 reruns the benchmark once the Overlay is final, a Refactor sweep (13) closes the build, and a closing end-to-end check on the builder's PC (14) follows it.
- Decision: 2026-11-02, 14 entries, past the usual 8 to 12, kept in one epic: the Overlay, the sounds and the status line report the same events and deliver together. Unattended default, awaiting review.
- Decision: 2026-11-02, the Overlay sits in the top-right corner of the monitor holding the Target Game's window (else the primary monitor), 24 px in; a constant, not a setting. Unattended default, awaiting review.
- Decision: 2026-11-02, the saving icon is driven by snapshot state, not by the `clip.saving` event: `capture.stateChanged` gains an additive `clipsWriting` count beside `cooldownUntil`, and the icon shows until the cooldown has passed and no Clip is writing. Unattended default, awaiting review.
- Decision: 2026-11-02, Overlay messages are `warning.raised` notices (AD-15 lets the Overlay render nothing else): an engine FeedbackRouter raises `clip-saved` (info, dismissed by the engine after 10 s), `clip-failed` (dismissed by the user or the next saved Clip), and the Recording and disk-full notices for epic-recordings. Each message shows for 4 s, oldest first. Unattended default, awaiting review.
- Decision: 2026-11-02, a dev-instance-only `dev.simulate` command stands in for epic-recordings' Recording state, disk full and save failures, so this epic verifies the solid dot, the red X and the Recording sounds with a stub, as Boundaries says. Unattended default, awaiting review.
- Decision: 2026-11-02, the Feedback Sounds are five built-in synthesized sounds (under 600 ms, peaking at -12 dBFS), with no custom sound files and no volume setting in v1; saved is a rising chime and failed a low descending buzz. Unattended default, awaiting review.
- Decision: 2026-11-02, over exclusive fullscreen the Overlay detects `QUNS_RUNNING_D3D_FULL_SCREEN` and stays hidden, and sounds carry the feedback; if the spike finds an always-shown dot pushes a borderless game out of independent flip, the Overlay shows only while it has something to show (the dot for 3 s after each state change), a deviation from FR-6 put to the builder. Both are defaults spike 2 confirms or replaces. Unattended default, awaiting review.
- Decision: 2026-11-03, spike 2 (Q-4) measured at the physical console with `bench overlay` (the synthetic GPU load at 80 fps, 2560x1440, RTX 5090; story 1's Overlay window hosted on its own; PresentMon 60 s per phase). It replaces the two defaults above.
  - (a) The Overlay doesn't hide on `QUNS_RUNNING_D3D_FULL_SCREEN`; it shows the same way over every game. The shell reports that state both for DXGI fullscreen promoted by fullscreen optimizations, where the dot showed and the game stayed in Hardware Composed: Independent Flip at −0.24 % FPS cost, and for legacy exclusive fullscreen. In legacy exclusive the dot can't be seen (Hardware: Legacy Flip), but showing it there cost 0.01 % FPS and never took the game out of fullscreen. Feedback Sounds cover those games.
  - (b) An always-shown dot keeps a borderless game in independent flip: Hardware Composed: Independent Flip with the dot hidden or shown, at 0.08 % FPS cost, within noise. FR-6's always-shown dot stands, with no 3 s mitigation and no deviation.
  - Story 8 drops the QUNS hide and checks with the real App that a legacy exclusive game keeps fullscreen. Story 12 checks the borderless PresentMode stays independent flip.
  - The details are in the spike's plan file and `measurements/bench-5.2/overlay.md`. The builder's list of exclusive-fullscreen games, and a check by eye, are queued as Human Checks.
- Decision: 2026-11-02, the Feedback Sounds page has a Play button per sound through a new `feedback.preview` command. Unattended default, awaiting review.
- Decision: 2026-11-02, there's no `bmad-ux` pass for the Overlay, the status line or the Feedback Sounds page; the builder checks them by hand, as in epics 2 to 4. Unattended default, awaiting review.
- Decision: 2026-11-02, a closing end-to-end story on the builder's PC (hitl) follows the sweep, because the Done when ends "Verified integrated on the builder's PC".
- Waits on epic-replay-buffer-clips because: it displays that epic's Clip save, saving and failure events and its capture state.
