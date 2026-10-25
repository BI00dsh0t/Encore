# Reconciliation: PLAN.md (rev 2) vs PRD

## Gaps found and fixed (user decisions already recorded in PLAN.md)
- **Decision #5, Memory and Disk ring modes:** the PRD referred to "the storage mode" but had no requirement for choosing it. Added **FR-36**.
- **Decision #8, a failed hotkey registration is logged and shown in the UI:** missing, and the PRD also had no requirement that Hotkeys can be configured at all. Added **FR-37**.
- **Decision #2, fragmented MP4:** a technical choice, so it belongs in the addendum. Added an addendum pointer to PLAN.md §0.

## Gaps for triage (need the user)
- **The overlap warning (PLAN.md §2):** PLAN.md warned when a mixer bus *and* the physical device it feeds are both in the master mix, because audio would be counted twice. With the new Track model (any combination of devices, and an "Everything" Track first), double-counting is easy: for example, BEACN "Game" plus BEACN Mic "Headphones". Keep a warning, or drop it?
- **Clip file naming (PLAN.md §4, `{game}_{date}`):** the PRD has per-game folders but doesn't say how files are named.
- **The qualitative "low manual intervention" promise (the OBS complaint):** it's in the Vision but no requirement covers it. Candidate NFR-6, hands-off operation: after first setup, no steps are needed per session.

## Deliberately set aside (not PRD material, or superseded)
- Multiple Save Hotkeys and Post-roll: superseded by user decisions in this PRD run.
- Per-process audio: a Non-Goal (§6).
- Capture-privacy items beyond keeping the Overlay out of captures (PLAN.md §7.1): deferred, tracked in PLAN.md and the addendum.
- HDR, the encoder and stack choices, the color path, and the T-01…T-25 test catalog: architecture and test inputs, indexed in the addendum.
