---
id: 8
type: epic
title: "Hardening and hands-off operation"
parent: initiative-replay-buffer
covers: [R34, R35, R33, R36]
sources: []
companions: []
after: []
refined: true
risk: medium
---

# Hardening and hands-off operation

## Description

This epic proves the whole app holds up. It runs an 8-hour soak with no memory growth, restarts the engine automatically after a crash (with the Replay Buffer resuming and the status line reporting it), and keeps Hotkeys working when the Main App closes or crashes. The engine recovers from a GPU driver reset, sleep and resume, the lock screen and monitor changes. Settings and device choices survive reboots, app restarts and mixer restarts. It closes out PLAN.md's failure cases T-01 to T-25 against the finished app; each earlier epic carries its own tests.

## Outcome

The app runs for a month of daily use with nothing to do per session, which is the bar for the initiative's Done when (R34, R35, toward SM-1 and SM-2).

## Requirements

- R1 (parent R34, NFR-5; PLAN.md T-15, T-16): Over an 8-hour session, in Memory mode and in Disk mode, with the Library, Cleanup, the Overlay and a Recording running, every Save Hotkey press outside the Save Cooldown produces a Clip that plays with every Track, the engine's and the App's memory, handle, GDI and USER object counts don't grow, the Disk-mode ring file stays at a fixed size, and two devices on independent clocks drift under one frame at the end.
- R2 (parent R34, NFR-5; architecture AD-16; PLAN.md T-17, T-18): When the engine crashes or hangs, the App restarts it, the Replay Buffer resumes on the running Target Game with its Hotkeys registered, and the status line reports the restart. A Recording that was running is not resumed: its file plays up to its last fragment and `recording-interrupted` is raised. When the App is killed, the Hotkeys keep saving Clips and the engine relaunches the App, which reports it. A process that keeps crashing is not relaunched forever.
- R3 (parent R34, R35; PLAN.md T-01): After a GPU driver reset (`DXGI_ERROR_DEVICE_REMOVED` or `DXGI_ERROR_DEVICE_RESET`), the engine rebuilds its device and encoder chains without restarting, starts a new ring generation, and the content buffered before the reset stays saveable.
- R4 (parent R35; PLAN.md T-02, T-04): After sleep and resume, capture and audio recover with no packet carrying a negative or huge timestamp. During the lock screen, a UAC prompt or Ctrl+Alt+Del, capture holds or blacks frames and recovers afterwards, with no generation break unless the format changes.
- R5 (parent R35; PLAN.md T-03): When a monitor is connected or removed or the primary monitor changes, game capture continues, the Overlay moves to its new place, and a Monitor Recording of a removed monitor ends cleanly with a clear warning; nothing crashes.
- R6 (parent R35, NFR-6; PLAN.md T-05, T-06, T-07): After a reboot, an App or engine restart and a BEACN mixer restart, nothing needs re-selecting: settings, Game Profiles, Track Layout device choices, Hotkeys, the mic mute state and Favourites are as they were, and Start with Windows brings the app up so the Replay Buffer starts by itself when a game is detected.
- R7 (parent R33, R36, NFR-1, NFR-4; PLAN.md T-24): The final conformance run, with epic-engine-baseline's benchmark and everything on (Replay Buffer, a Recording, the Overlay, every Track, the Library and Cleanup): A/V and Track-to-Track drift under one frame over a 60-minute Recording; FPS loss within the NFR-1 target; and every H.264 Clip and Recording opening in Discord, Chrome, VLC, Windows Media Player and DaVinci Resolve with every Track present, named and in sync, HEVC and AV1 too in the players that support them.
- R8 (PLAN.md §8): Each of T-01 to T-25 passes against the finished app, or has a recorded decision, in one conformance record.

## Constraints

- (architecture AD-16) Supervision is the App restarting the engine (crashed, or hung after 3 missed 2 s heartbeats or a 10 s unanswered command) and the engine relaunching the App; only the Replay Buffer resumes after a restart.
- (architecture AD-5) Mic mute is engine runtime state in `runtime.json`, restored before any session starts; a missing or corrupt file means muted.
- (architecture AD-6, AD-7) One clock (QPC) for every stamp; one D3D11 device on the Target Game's adapter; at most two encoder chains; a forced encoder restart opens a new ring generation (story 4.6).
- (NFR-2, NFR-3) Recovery never touches the game process: no injection, no hooks. Fault injection used by tests exists only in the dev instance.
- (architecture AD-22) Contracts change additively only: new warning codes go in `Contracts.ErrorCodes`.
- (automation, 2026-11-01) Unattended sessions never change Windows settings, sleep, lock or reboot the PC, or change its displays: those checks are simulated through fault-injection seams and the real ones go to the Human Check Queue.

## Done when

- An 8-hour soak keeps producing correct Clips with no memory or handle growth, and Hotkeys survive the Main App being killed (R34).
- Killing the engine brings it back automatically: the Replay Buffer resumes, and epic-overlay-feedback's status line reports the restart (R34).
- The final conformance run, using epic-engine-baseline's benchmark: drift under one frame over a 60-minute Recording with every Track, and FPS loss within the NFR-1 target with everything on (R33, R36, final part).
- After a reboot, an app restart and a BEACN restart, nothing needs re-selecting (R35).
- The T-01 to T-25 failure cases (PLAN.md §8) pass or have a recorded decision. Verified integrated on the builder's PC; ShadowPlay is uninstalled, and the month of use begins.

## Boundaries

- Out: new features. A failure found here that needs new behaviour in a feature another epic owns becomes a bugfix against that epic's story (`git flow bugfix`), recorded in the conformance record.
- Out: the month of daily use itself (the initiative's Done when, SM-1 to SM-4); this epic only starts it.
- In: recovery from T-01 to T-04 (driver reset, sleep, monitor changes, the secure desktop), which no earlier epic owns.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R33, R34, R35, R36; Done when
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, AD-3, AD-5, AD-6, AD-7, AD-12, AD-15, AD-16, AD-17, AD-22
- prd — _bmad-output/initiative-replay-buffer/prd-replay-buffer/prd-replay-buffer.md, NFR-1, NFR-2, NFR-4, NFR-5, NFR-6
- architecture input — PLAN.md, §8 Test catalog

## Notes

- Waits on epic-recordings because: it soaks and crash-tests Recordings.
- Waits on epic-library-cleanup because: the soak includes the Library and Cleanup.
- Decision: 2026-11-02, refined unattended from the initiative's lines, the PRD NFRs, the architecture ADs and PLAN.md §8. Story 1.8 already has mutual supervision and single instance; story 1.14's ReplayBuffer.Bench has the FPS and drift runs, extended with the Overlay by story 5.12; story 2.5 rebinds devices by id, then name; story 4.6 opens a new ring generation on a forced encoder restart and story 6.6 restarts a Recording on one; story 6.2 makes Recordings crash-safe and raises `recording-interrupted`; story 5.10's status line shows `engine-restarted` and `app-restarted`.
- Decision: 2026-11-02, the tracer bullet is story 1: a `bench soak` command drives the installed engine and App against a synthetic game window, presses the Save Hotkey on a schedule, samples each process's memory, handles, GDI and USER objects, and checks every Clip, and a 20-minute run writes a passing report.
- Decision: 2026-11-02, the least-certain item is engine crash recovery end to end (the restart warning, the Replay Buffer resuming, the crash loop), so story 2 follows the tracer.
- Decision: 2026-11-02, the sequencing: stories 2 to 6 each change the engine's start and recovery paths and run as one lane (crash, driver reset, sleep and the secure desktop, monitors, persistence); the conformance run (7) and then the 8-hour soaks (8) run against the hardened app, one after the other because both extend ReplayBuffer.Bench, and the T-01 to T-25 record (9) follows them. A Refactor sweep (10) closes the build, and a closing end-to-end check on the builder's PC (11) follows it.
- Decision: 2026-11-02, recovery from T-01 to T-04 is built here, not sent to another epic: no earlier epic owns it, and it is robustness, not a new feature. Unattended default, awaiting review.
- Decision: 2026-11-02, the restart warnings: the App relaunches a crashed or hung engine with `--restarted crashed|hung`, and the new engine raises `warning.raised { code: engine-restarted, dismissable }` before its first snapshot; the engine raises `app-restarted` itself when it relaunches the App. After 3 engine restarts within 10 minutes the App stops relaunching it, shows the tray in an error state, and offers Restart from the tray (`engine-crash-loop`); the engine stops relaunching the App the same way (`app-crash-loop`). A hang is simulated by a dev-only `dev.simulate engine-hang`. Unattended default, awaiting review.
- Decision: 2026-11-02, the time to recover: after the engine is killed, the Replay Buffer is buffering again on the running Target Game within 10 s; after a hang, within 20 s. Unattended default, awaiting review.
- Decision: 2026-11-02, a GPU driver reset is detected from any D3D11, DXGI or encoder call returning `DXGI_ERROR_DEVICE_REMOVED` or `DXGI_ERROR_DEVICE_RESET`, and handled in the SessionOrchestrator: the device, WGC items and encoder chains are rebuilt on the Target Game's adapter (retried every 2 s for up to 60 s, then `gpu-reset-failed` and the Replay Buffer stops until the next Target), a new ring generation starts, and `warning.raised { code: gpu-reset, overlay: false }` is logged in the status line. Tests use a dev-only fault seam; a real reset (`dxcap -forcetdr`, needing elevation) is a Human Check. Unattended default, awaiting review.
- Decision: 2026-11-02, sleep (`PBT_APMSUSPEND`) closes the current ring generation, which stays saveable, and stops the audio clients and capture; a running Recording ends with `recording.stopped { reason: suspend }` and `recording-suspended`, and is not restarted; resume (`PBT_APMRESUMEAUTOMATIC`) rebuilds them and opens a new generation, so no packet's timestamp spans the sleep. The secure desktop (lock, UAC, Ctrl+Alt+Del) is handled as a static screen: the pacer repeats the last frame and audio continues. Tests inject the power events and a frame-starved source; a real sleep, lock and UAC prompt are Human Checks. Unattended default, awaiting review.
- Decision: 2026-11-02, a display change (`WM_DISPLAYCHANGE`) re-places the Overlay by story 5.1's rule; a Monitor Recording whose monitor is removed ends at its last frame with `warning.raised { code: monitor-removed }` and its file kept; one whose monitor stays continues. Tests use a fake monitor list; plugging a monitor is a Human Check. Unattended default, awaiting review.
- Decision: 2026-11-02, the soak's pass bounds: after a 30-minute warm-up, each process's private bytes end within the larger of 5% and 20 MB of their warm-up value with a fitted slope under 2 MB per hour, and handle, GDI and USER counts end within 50 of their warm-up value; every Clip passes an ffprobe check (every Track present and named, length within one frame of the Buffer Window). Unattended default, awaiting review.
- Decision: 2026-11-02, the 8-hour soak is two unattended runs on the builder's PC, Memory mode then Disk mode, on Bench's synthetic game window without the synthetic GPU load, with the builder's real Audio Devices (silent loopbacks included, T-14), the Save Hotkey pressed every 5 minutes with jitter, a 10-minute Recording each hour, a Cleanup size limit that is crossed, and the App killed once in the last hour (T-17); a soak in a real game is a Human Check. Unattended default, awaiting review.
- Decision: 2026-11-02, the conformance record is `conformance-t01-t25.md` beside this epic file: each case with its owning story, the test that covers it, and its result against the finished build. T-09's HDR half and T-12 (Post-roll) are recorded as out of v1 (PRD §6, addendum Precedence); T-11's "queue or reject with a toast" is met by 4.4's `save-busy`. A failure that needs new behaviour becomes a bugfix against the owning story. Unattended default, awaiting review.
- Decision: 2026-11-02, epic 8 now also declares needs on epics 1 (Bench, supervision, Start with Windows), 2 (rebinding, named Tracks), 3 (Game Profiles), 4 (Clips, ring generations) and 5 (status line, Overlay, `bench fps --overlay`), pinned on the stories that use them.
- Decision: 2026-11-02, a closing end-to-end story on the builder's PC (hitl) follows the sweep, because the Done when ends with checks only the builder can run (a reboot, a BEACN restart, a real game, uninstalling ShadowPlay).
