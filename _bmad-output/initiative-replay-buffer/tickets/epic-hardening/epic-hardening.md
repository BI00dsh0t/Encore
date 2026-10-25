---
id: 8
type: epic
title: "Hardening and hands-off operation"
parent: initiative-replay-buffer
covers: [R34, R35]
sources: []
companions: []
after: []
refined: false
risk: medium
---

# Hardening and hands-off operation

## Description

This epic proves the whole app holds up. It runs an 8-hour soak with no memory growth, restarts the engine automatically after a crash (with the Replay Buffer resuming and the status line reporting it), and keeps Hotkeys working when the Main App closes or crashes. Settings and device choices survive reboots, app restarts and mixer restarts. It closes out PLAN.md's failure cases T-01 to T-25 against the finished app; each earlier epic carries its own tests.

## Outcome

The app runs for a month of daily use with nothing to do per session, which is the bar for the initiative's Done when (R34, R35, toward SM-1 and SM-2).

## Done when

- An 8-hour soak keeps producing correct Clips with no memory or handle growth, and Hotkeys survive the Main App being killed (R34).
- Killing the engine brings it back automatically: the Replay Buffer resumes and the status line reports the restart (R34).
- After a reboot, an app restart and a BEACN restart, nothing needs re-selecting (R35).
- The T-01 to T-25 failure cases (PLAN.md §8) pass or have a recorded decision. ShadowPlay is uninstalled, and the month of use begins.

## Boundaries

- Out: new features. A failure found here that needs new behaviour becomes a story in the epic that owns it.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R34, R35; Done when
- architecture input — PLAN.md, §8 Test catalog

## Notes

- Waits on epic-recordings because: it soaks and crash-tests Recordings.
- Waits on epic-library-cleanup because: the soak includes the Library and Cleanup.
