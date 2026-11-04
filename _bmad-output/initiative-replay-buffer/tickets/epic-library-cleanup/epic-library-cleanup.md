---
id: 7
type: epic
title: "Library and Cleanup"
parent: initiative-replay-buffer
covers: [R27, R28, R29]
sources: []
companions: []
after: []
refined: true
risk: low
---

# Library and Cleanup

## Description

This epic is the Main App's Library. It lists every Clip and Recording, grouped and filtered by game, date and Favourite, with a thumbnail, name, game, date, length and size, and reflects changes made outside the app. Its actions are: open in the Media Player, Favourite, rename, delete (to the Recycle Bin), show in Explorer, and copy to the clipboard for Discord. Cleanup deletes the oldest non-Favourite files permanently once a size or age limit is passed.

## Outcome

The builder finds, opens and shares any Clip from one window, and the Output Folder stays within its limit without losing a Favourite (R27, R28, R29).

## Requirements

- R1 (parent R27, FR-30; architecture AD-3, AD-9, AD-17): The engine's LibraryActor keeps the Library index: one item per `*.mp4` under the Output Folder, keyed by its path relative to the Output Folder, with the `profileId` recorded at `Library.BeginWrite`. It replaces epic-replay-buffer-clips' no-op `BeginWrite`/`EndWrite` port. The items are a field of `state.snapshot`, and `library.*` events keep the App current, so a newly saved Clip appears within 2 s without a refresh.
- R2 (parent R27, FR-30; architecture AD-17): Files added, renamed or removed in the Output Folder outside the app are reflected. A first folder scan is reconciled with `library.json` at start, and a watcher keeps it current after that; an overflowed watcher rescans. A rename outside the app moves the item's metadata (its Favourite included) to the new path. The engine's own renames and deletes are registered in advance, so their echoes are ignored. `*.rbpart` files are never items and are deleted when the engine starts. `library.json` is written atomically with a `.bak`; if neither copy parses, `library-corrupt` is raised and Cleanup stays off until it is acknowledged. Changing `outputFolder` rebuilds the index from the new folder only.
- R3 (parent R27, FR-30; architecture AD-1): Each item shows a thumbnail, its name, game, date, length and size. The engine reads the length; the App gets the thumbnail from the Windows shell by path and never asks for one of an item being written.
- R4 (parent R27, FR-30): The Library can be grouped by game or by date, and filtered by game, by date and to Favourites only.
- R5 (parent R28, FR-31; architecture AD-1): Open starts the Windows default app for the file (the Media Player). Show in Explorer opens the item's folder with the file selected. Copy puts the file on the clipboard as a file drop, so it pastes into Discord as an attachment. These are App-side shell actions on a path; the App never opens the file's contents.
- R6 (parent R28, FR-31; architecture AD-3, AD-17): Favourite toggles a flag kept in `library.json`. Rename changes the file's name in its folder. Delete asks for confirmation, then sends the file to the Recycle Bin. These are engine commands through the LibraryActor that carry the item's path, size and modified time. A file being written (a Clip saving, or a Recording or Monitor Recording in progress) can't be renamed or deleted.
- R7 (parent R29, FR-32; architecture AD-17): Cleanup enforces `cleanup`'s size and/or age limit on the Output Folder by permanently deleting the oldest non-Favourite items until the folder is back within it. It is off while both limits are null, and turning a limit on asks for confirmation that deletion is permanent. It never deletes a Favourite (even if the folder stays over the limit) or a file being written. It never runs in the first 60 s after start, before the first scan is reconciled, or while `library-corrupt` is unacknowledged, and it considers only indexed `*.mp4` items.
- R8 (architecture AD-1, AD-4; gap): The Output Folder is chosen in Settings, on the page that holds the Cleanup limits.

## Constraints

- (architecture AD-17) The LibraryActor is single-threaded and handles watcher events, IPC commands, `BeginWrite`/`EndWrite` and every Cleanup step; it re-checks Favourite and being-written status immediately before each delete.
- (architecture AD-1) The engine owns the library index and Cleanup; the Main App only passes paths to the shell (open, show in Explorer, `CF_HDROP`, `IShellItemImageFactory` with `SIIGBF_THUMBNAILONLY`).
- (architecture AD-3) Every state the App renders is in `state.snapshot`; mutating commands carry the target's path, size and mtime and are never auto-retried.
- (architecture AD-9) Writers open files with `FILE_SHARE_READ | FILE_SHARE_DELETE`; the being-written set holds absolute paths.
- (architecture AD-22) Contracts change additively only, in `Library.cs` and `ErrorCodes`.
- Settings and `library.json` live in `%APPDATA%\ReplayBuffer[-<instance>]\`.

## Done when

- A new Clip appears within 2 s, and files changed outside the app are reflected. Grouping and filtering by game, date and Favourite works (R27).
- Every action works. A copied Clip pastes into Discord as an attachment, deleting goes through a confirmation to the Recycle Bin, and files being written are locked (R28).
- Cleanup is off until a limit is confirmed, then removes the oldest non-Favourite files only, never a Favourite or a file being written (R29). Verified integrated on the builder's PC.

## Boundaries

- Out: writing files and choosing their names (epic-replay-buffer-clips, epic-game-detection-profiles); several items acted on at once (one item per action in v1).
- Handoff: Recordings and Monitor Recordings arrive through the same "file being written" signal, raised by epic-recordings. This epic verifies the locking with Clips and a sample Recording file, and epic-hardening's soak covers live Recordings.
- Touch point: the Windows default app for .mp4 (the Media Player), and Discord's paste, both consumed only.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R27, R28, R29
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, AD-1 (shell actions), AD-3, AD-4, AD-9 (write protocol), AD-17, AD-21, AD-22
- prd — _bmad-output/initiative-replay-buffer/prd-replay-buffer/prd-replay-buffer.md, FR-30, FR-31, FR-32
- architecture input — PLAN.md, §8 (T-19)

## Notes

- Waits on epic-replay-buffer-clips because: it needs Clips written into per-game folders, and the "file being written" signal.
- Decision: 2026-11-02, refined unattended from the initiative's lines, the PRD FRs and the architecture ADs the epic cites. The Contracts already hold the `outputFolder` and `cleanup { maxBytes, maxAgeSeconds }` keys (both apply immediately) and an empty `Library.cs`; story 4.5 adds the `Library.BeginWrite`/`EndWrite` port with a no-op implementation.
- Decision: 2026-11-02, the tracer bullet is story 1: the LibraryActor replaces 4.5's no-op port, indexes the Output Folder into `library.json`, puts the items in `state.snapshot` with `library.*` events, and a Library page lists them; the builder saves a Clip and sees it appear within 2 s.
- Decision: 2026-11-02, the least-certain item is keeping the index right under changes made outside the app (watcher echoes, overflow, renames moving metadata, a corrupt index), so story 2 follows the tracer.
- Decision: 2026-11-02, the sequencing: stories 1 to 8 each change the LibraryActor, the Library page or both, so they run as one lane, and the settings page (9) follows Cleanup, whose preview command it calls. A Refactor sweep (10) closes the build, and a closing end-to-end check on the builder's PC (11) follows it.
- Decision: 2026-11-02, no story in epics 1 to 6 owns an Output Folder picker, so story 9 adds it on a new Library and Cleanup settings page, where a change rebuilds the index (AD-17). Unattended default, awaiting review.
- Decision: 2026-11-02, the engine reads each item's length with FFmpeg's demuxer (no decode) after `EndWrite` and for new or changed files found by the scan, at low I/O priority, cached in `library.json` by size and mtime. An item being written shows as locked, with no thumbnail or length, until `EndWrite`. Unattended default, awaiting review.
- Decision: 2026-11-02, thumbnails come only from the Windows shell (`IShellItemImageFactory`, `SIIGBF_THUMBNAILONLY`) with an in-memory cache in the App and a generic icon when the shell has none (an HEVC or AV1 file without the codec extension); there is no thumbnail cache on disk. Unattended default, awaiting review.
- Decision: 2026-11-02, an item's game is its `profileId` from `BeginWrite`; a file found by the scan takes the profile whose folder it sits in (story 3.5's persisted folder name), Monitor Recordings get their own group, and anything else is grouped by its top-level folder name, or "Other" at the Output Folder's root. An item's date is its `BeginWrite` time, else the file's creation time. Unattended default, awaiting review.
- Decision: 2026-11-02, grouping is by game or by day (newest first); the date filter offers Today, Last 7 days, Last 30 days and All. Unattended default, awaiting review.
- Decision: 2026-11-02, rename changes only the base name (the `.mp4` and folder stay), refused with `file-being-written`, `invalid-file-name` or `file-name-taken`; delete goes through the engine with the shell's recycle operation and is refused with `recycle-bin-unavailable` rather than deleting permanently on a volume with no Recycle Bin. Unattended default, awaiting review.
- Decision: 2026-11-02, Cleanup runs after each successful `EndWrite`, when `cleanup` changes, after the first scan is reconciled, and every 10 minutes for the age limit. Every indexed item, Favourites and files being written included, counts toward the size. When only Favourites and files being written remain over the limit it stops and raises `cleanup-limit-unreachable` once per crossing (dismissable). A file it can't delete (open elsewhere) is skipped until the next pass. Unattended default, awaiting review.
- Decision: 2026-11-02, the confirmation is the App's: turning a limit on, or lowering one, shows a dialog with what a new `cleanup.preview` command says would be deleted now (count and size) and that deletion is permanent, and sends the `settings.update` only on confirm. Unattended default, awaiting review.
- Decision: 2026-11-02, `library-corrupt` is acknowledged by dismissing its warning (`warning.dismiss`); the Library page shows it as a banner with an Acknowledge button, so this epic doesn't wait on epic-overlay-feedback's status line. Unattended default, awaiting review.
- Decision: 2026-11-02, actions act on one item at a time; selecting several items is out of v1. There is no `bmad-ux` pass for the Library or the settings page; the builder checks them by hand, as in epics 2 to 6. Unattended default, awaiting review.
- Decision: 2026-11-02, epic 7 doesn't wait on epic-recordings: Recordings use the same `BeginWrite`/`EndWrite` port, so the locking is verified with Clips and a fake writer growing a sample fragmented MP4, and epic-hardening's soak covers live Recordings. Epic 7 also declares a need on epic 3 (story 3.5's persisted profile folder names).
- Decision: 2026-11-02, a closing end-to-end story on the builder's PC (hitl) follows the sweep, because the Done when ends "Verified integrated on the builder's PC".
- Note: 2026-11-03, 7.3 QA notes, left as they are: the App's thumbnail cache is keyed by path and size and never pruned (about 150 KB each; an outside rewrite of the same size keeps a stale thumbnail), and a shell "no thumbnail" is not retried until the item's path or size changes. 7.6 should check that its rename/delete isn't blocked by a length read: FFmpeg opens files without `FILE_SHARE_DELETE`.
- Note: 2026-11-04, 7.8 QA notes, left as they are: (1) rename and delete's new final-path guard has no test of its own (the scan now keeps junction items out of the index, so only a folder swapped for a junction after the scan reaches it); (2) the scan now skips every reparse point, so OneDrive cloud placeholders are not listed, and an Output Folder inside OneDrive may show fewer items (check in 7.11); (3) the planner passes over a not-inside item without counting it as skipped, so `cleanup-limit-unreachable`'s message could blame Favourites in that stale case. Cleanup deletes permanently, and `cleanup-limit-unreachable` covers the size limit only (an old Favourite under an age limit is kept silently): both are unattended defaults.
