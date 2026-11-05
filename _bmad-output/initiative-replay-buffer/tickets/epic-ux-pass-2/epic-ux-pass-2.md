---
id: 10
type: epic
title: "UX pass 2"
parent: initiative-replay-buffer
covers: [R38]
sources: []
companions: [_bmad-output/initiative-replay-buffer/tickets/epic-ux-pass-2/ux-pass-2/EXPERIENCE.md, _bmad-output/initiative-replay-buffer/tickets/epic-ux-pass-2/ux-pass-2/DESIGN.md]
after: []
refined: true
risk: high
---

# UX pass 2

## Description

This epic makes the Main App work the way a gamer who isn't a developer expects, after the builder's second look at it. The Library becomes a real Windows list: several items selected with the usual mouse and keys, acted on together, sorted and searched. The Track Layout page becomes **Audio Tracks**: one card per Track listing the sounds it records, added by name from a picker with a live level beside each, with presets and a first-run layout that just works. A short list of smaller gaps closes it: first-run guidance, plain words, the tray menu and a save hint in the status line. A `bmad-ux` gap review ([ux-pass-2/EXPERIENCE.md](ux-pass-2/EXPERIENCE.md), with a [DESIGN.md](ux-pass-2/DESIGN.md) addendum on epic 9's tokens) sets it out. The builder approves it first.

## Outcome

A gamer installs the App, gets Clips with game sound and voice without opening a setting, clears out or shares a week of Clips in a few clicks, and splits voice from game onto its own track without learning the engine's model (R38).

## Requirements

- R1 (parent R38; architecture AD-24): In the Library, items can be selected one or several at a time with Windows Explorer's mouse and keys: click, Ctrl+click, Shift+click, Ctrl+A, the arrow keys with Shift and Ctrl, Ctrl+Space and Esc. The selection survives a filter, a search, a new item arriving and the App's own rename, and an item leaving the Library leaves it. Actions act only on the selected items that are shown, and the count says how many are hidden. With nothing selected, the list shows a count and size summary and how to select.
- R2 (parent R38, R28; architecture AD-24, AD-17, AD-1): The selected items can be acted on together: Favourite or remove from Favourites, copy (one clipboard file drop with every file), copy to a folder, show in Explorer, and delete with one confirmation that names the count and size. Delete never deletes a Favourite or a file being written: it leaves them out and says so. A partial failure is reported once, with the count that succeeded. Open and Rename act on one item.
- R3 (parent R38, R28): A right-click on a row acts on the selection when the row is in it, else on that row alone. The keyboard does the same as the menus: Enter opens, Delete deletes, F2 renames, Ctrl+C copies, Ctrl+A selects all, Ctrl+F finds and Space toggles the Favourite.
- R4 (parent R38, R27): The Library sorts by date (newest or oldest first), name, size, length or game, groups by game, day or none, and filters by a name search typed into it. Sort and grouping are kept across restarts; the search isn't.
- R5 (parent R38, R15, R18, R30; architecture AD-4, AD-24): The audio tracks are edited as one card per Track, in file order. Each card shows the sounds the Track records, named as Windows names them, each with its own live level, and a sound is added to a Track from a picker that lists every playback and recording device with its live level and the Windows default marked. Microphones and the mute rule read in plain words, and two presets, Simple and For editing, fill the tracks from the Windows default devices. Unsaved changes show at the top of the page and in the nav, and a game can use the main tracks or its own. The engine's Track Layout model is unchanged: named Tracks of devices, Mic Devices, the mute rule, Everything first, and the double-counting warning.
- R6 (parent R38, R35; architecture AD-24, AD-22): A fresh install records the Windows default playback device and the default microphone on one "Everything" Track, with no setup and without the Main App opened. An existing Track Layout is never changed.
- R7 (parent R38): The smaller gaps are closed:
  - an empty Library shows a welcome card with the Save Hotkey, the Buffer Window, the audio being recorded and the Output Folder, each linked to its page;
  - Track Layout, Game Profiles, and Library and Cleanup are renamed Audio Tracks, Games, and Storage and Cleanup, and the wording in EXPERIENCE.md's Voice and Tone replaces engine words;
  - the Games list shows no internal id;
  - the tray menu shows the status and offers Open Library and Open the Clips folder;
  - while buffering, the status header says which key saves and how much.
- R8 (parent R38): Everything else behaves as before: every engine rule, setting, message and existing App test keeps working, except the tests of behaviour this epic changes on purpose (single delete of a Favourite, the page titles and wording).

## Constraints

- (architecture AD-22) Contracts change additively only: `AudioDevice.isDefault` is optional with a default of false. No message is renamed or removed, and `schemaVersion` and the IPC `v` stay 1.
- (architecture AD-24, AD-17, AD-3) Bulk actions are per-item commands with each item's identity as shown; there is no bulk delete message, and the LibraryActor's checks still run before each delete.
- (architecture AD-4) The engine alone writes settings; the first-run seed goes through its settings queue, and the App's presets go through `settings.update` like any edit.
- (architecture AD-1) The App never opens the media files: Open, Show in Explorer, Copy and Copy to folder go through the Windows shell.
- (architecture AD-23) New components take every colour, corner and type from epic 9's tokens through `DynamicResource`, and work in both themes.
- (architecture AD-20) Meters open no device twice; probe Tracks share the AudioDeviceHub's one client per device.
- (CLAUDE.md) Lean tests: one or two happy-path tests per requirement, plus one for any bug that bit; nothing slow in the default run.

## Done when

- Library multi-select, the command bar and the context menu work with the mouse and every listed key, and the selection survives filtering, search and refresh (R1, R3).
- A bulk Favourite, copy, copy to folder, show in Explorer and delete act on the shown selection, and Favourites and files being written are never deleted (R2).
- Sort, group by None and search work and sort is kept across restarts (R4).
- Audio Tracks shows cards, a picker with live levels, the presets and the save bar, and a fresh dev instance records default playback and microphone with no setup while an existing layout is untouched (R5, R6).
- The welcome card, the renamed pages, the tray items and the status hint are in place, and every existing App test still passes (R7, R8). Verified integrated on the builder's PC.

## Boundaries

- Out: a "Save Clip now" tray item or any new engine command, unless the builder approves it at 10.1 (PRD Q-2; the initiative's one Save Hotkey).
- Out: drag-and-drop reorder or drag-to-add; checkboxes on every Library row; a card grid; an advanced audio view; a "follow the Windows default device" source (R35, AD-4).
- Out: changes to the engine's Track Layout model, the Library's engine rules, Cleanup, or the Overlay.
- Out: new looks, tokens or themes (epic-modern-ui owns them).
- Touch point: the Windows shell's file operation (Copy to folder) and Explorer's multi-select (Show in Explorer), consumed only.
- Touch point: Discord's paste of several files, consumed only.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R38 (and R15, R18, R27, R28, R30, R35)
- design — _bmad-output/initiative-replay-buffer/tickets/epic-ux-pass-2/ux-pass-2/EXPERIENCE.md (the gap review, Library list, Audio Tracks, Build Map)
- design — _bmad-output/initiative-replay-buffer/tickets/epic-ux-pass-2/ux-pass-2/DESIGN.md (the components addendum)
- design — _bmad-output/initiative-replay-buffer/tickets/epic-modern-ui/ux-modern-ui/DESIGN.md and EXPERIENCE.md (the tokens, shell and keyboard rules this extends)
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, AD-1, AD-3, AD-4, AD-17, AD-20, AD-22, AD-23, AD-24

## Notes

- Decision: 2026-11-05, the builder's words: "We need to do another pass on UX. The library is missing basic list functionality, like multi select." and "The track layout is convoluted and makes no sense to an end user."
- Decision: 2026-11-05, this overrides epic 7's decision (automation-state, 2026-11-02) that "Actions act on one item at a time (no multi-select in v1)".
- Decision: 2026-11-05, the timing: after epic 9 (its last story, 9.9), and before 8.11, the hands-off run on the builder's PC, so that run uses the final UI; 8.11's `after` names 10.8. 8.7 to 8.9 measure the engine (conformance, soaks, the T-01 to T-25 record) and don't wait on this epic.
- Decision: 2026-11-04, the gap review ran headless (`bmad-ux`) with no questions to the builder; its choices are in ux-pass-2/.memlog.md. Story 1 puts them to the builder with renders. Unattended default, awaiting review.
- Decision: 2026-11-04, Favourites are protected from Delete, single and many: Delete leaves them out and says so, and unstarring comes first. This changes 7.7, where one Favourite could be deleted after the confirmation. Unattended default, awaiting review at 10.1.
- Decision: 2026-11-04, bulk actions send the existing per-item commands one at a time, so no Contracts message is added and the LibraryActor's per-delete checks still apply (AD-24). Unattended default, awaiting review.
- Decision: 2026-11-04, the only Contracts change is the optional `AudioDevice.isDefault`, for the presets and the first-run seed. Per-device meters reuse `meters.watch` with probe Tracks; if 10.4 measures that as too costly, an optional per-device level list in `meters.levels` replaces it, additively. Unattended default, awaiting review.
- Decision: 2026-11-04, the first-run seed is written by the engine when settings.json has no Global `trackLayout`: the Simple preset from the Windows default devices. The catalog default stays an empty "Everything", and the builder's existing layout is untouched. Unattended default, awaiting review.
- Decision: 2026-11-04, the tracer is the design pass (story 1), as in epic 9, because every later story builds to its spines. The least certain parts come next and can run side by side: Library selection on a virtualized, grouped list (story 2) and the audio model work, with the default device, the seed and the probe meters (story 4).
- Decision: 2026-11-04, the sequencing: two lanes after story 1. The Library lane is selection and bulk actions (2), then sort and search (3), both in LibraryPage and LibraryView. The Audio lane is the defaults, presets and meters (4), then the editor (5). The other gaps (6) touch LibraryView's empty state and the Games page, so they wait on 3 and 5. A Refactor sweep (7) and a closing end-to-end check on the builder's PC (8, hitl) close the epic, as in epics 6, 7 and 9.
- Decision: 2026-11-04, lean tests: the selection, bulk-action, sort, search, preset, seed and meter logic is tested in the view models and the engine with fakes; how it looks and feels is the builder's check at 10.1, 10.2, 10.5 and 10.8. No screenshot comparison tests.
- Open question: should the tray menu get "Save Clip now"? It needs a new engine command and touches PRD Q-2. The builder decides at 10.1; it joins 10.6 only if approved.
- Open question: are the page renames (Audio Tracks, Games, Storage and Cleanup) right? The builder decides at 10.1.
- Assumption: a saved audio-tracks change applies from the next Reset (R17), so the page's hint reads "from your next game, or right after your next saved Clip"; 10.4 checks this against the engine before 10.5 uses the wording.
