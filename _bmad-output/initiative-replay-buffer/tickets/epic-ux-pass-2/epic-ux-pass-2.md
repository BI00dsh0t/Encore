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
status: done
---

# UX pass 2

## Description

This epic makes the Main App work the way a gamer who isn't a developer expects, after the builder's second look at it. The Library becomes a real Windows list: several items selected with the usual mouse and keys, acted on together, sorted and searched. The Track Layout page becomes **Audio Tracks**: one card per Track listing the sounds it records, added by name from a picker with a live level beside each, with presets and a first-run layout that just works. Game Profiles becomes **Games**: a list of games, each opening its own page that shows plainly which settings it takes from the main ones and which it changes. The settings pages are reorganised around what they control. The Replay Buffer page becomes Clips, and the Recordings page is folded into Hotkeys and Games. Every folder is chosen with a folder picker, and a glossary keeps internal terms out of everything the user reads. A short list of smaller gaps closes it: first-run guidance, a Recording badge in the Library, the tray menu and a save hint in the status line. A `bmad-ux` gap review ([ux-pass-2/EXPERIENCE.md](ux-pass-2/EXPERIENCE.md), with a [DESIGN.md](ux-pass-2/DESIGN.md) addendum on epic 9's tokens) sets it out. The builder approves it first.

## Outcome

A gamer installs the App, gets Clips with game sound and voice without opening a setting, clears out or shares a week of Clips in a few clicks, splits voice from game onto its own track, and gives one game longer Clips, all without meeting a word of the engine's model (R38).

## Requirements

- R1 (parent R38; architecture AD-24): In the Library, items can be selected one or several at a time with Windows Explorer's mouse and keys: click, Ctrl+click, Shift+click, Ctrl+A, the arrow keys with Shift and Ctrl, Ctrl+Space and Esc. The selection survives a filter, a search, a new item arriving and the App's own rename, and an item leaving the Library leaves it. Actions act only on the selected items that are shown, and the count says how many are hidden. With nothing selected, the list shows a count and size summary and how to select.
- R2 (parent R38, R28; architecture AD-24, AD-17, AD-1): The selected items can be acted on together: Favourite or remove from Favourites, copy (one clipboard file drop with every file), copy to a folder, show in Explorer, and delete with one confirmation that names the count and size. Delete never deletes a Favourite or a file being written: it leaves them out and says so. A partial failure is reported once, with the count that succeeded. Open and Rename act on one item.
- R3 (parent R38, R28): A right-click on a row acts on the selection when the row is in it, else on that row alone. The keyboard does the same as the menus: Enter opens, Delete deletes, F2 renames, Ctrl+C copies, Ctrl+A selects all, Ctrl+F finds and Space toggles the Favourite.
- R4 (parent R38, R27): The Library sorts by date (newest or oldest first), name, size, length or game, groups by game, day or none, and filters by a name search typed into it. Sort and grouping are kept across restarts; the search isn't.
- R5 (parent R38, R15, R18, R30; architecture AD-4, AD-24): The audio tracks are edited as one card per Track, in file order. Each card shows the sounds the Track records, named as Windows names them, each with its own live level, and a sound is added to a Track from a picker that lists every playback and recording device with its live level and the Windows default marked. Microphones and the mute rule read in plain words, and two presets, Simple and For editing, fill the tracks from the Windows default devices. Unsaved changes show at the top of the page and in the nav, and a game can use the main tracks or its own. The engine's Track Layout model is unchanged: named Tracks of devices, Mic Devices, the mute rule, Everything first, and the double-counting warning.
- R6 (parent R38, R35; architecture AD-24, AD-22): A fresh install records the Windows default playback device and the default microphone on one "Everything" Track, with no setup and without the Main App opened. An existing Track Layout is never changed.
- R7 (parent R38): The smaller gaps are closed:
  - an empty Library shows a welcome card with the Save Hotkey, the Clip length, the audio being recorded, the Clips folder and how to make a Recording, each linked to its page;
  - a Recording or Monitor Recording shows its kind in the Library;
  - the tray menu shows the status and offers Open Library and Open the Clips folder;
  - while capturing, the status header says which key saves and how much.
- R8 (parent R38): Everything else behaves as before: every engine rule, setting key, message and existing App test keeps working, except the tests of behaviour this epic changes on purpose (single delete of a Favourite, the page titles, layout and wording, and the Recordings page's removal).
- R9 (parent R38, R23, R25, R26; architecture AD-4, AD-21, AD-24): Games lists every game with its icon, name, whether it was detected or added by hand, whether it's being captured, how many of its settings it changes, and whether it's Ignored, with Ignored games last and no internal id. Selecting one, with a click or Enter, opens that game's page, and Back, Esc, Alt+Left or the mouse's Back button return to the list with focus on its row. A game's page renames the game and switches Capture (Ignored), Recordings allowed, Clip length, Video quality and Audio tracks. For each of the last three, it shows plainly whether the game uses the main setting or its own, with a way to change it for the game and a confirmed reset back to the main setting. It shows the game's folder and file names read-only, and stops detecting a hand-added game. A game is added by hand by picking its program. Detection, the Target rule, automatic profiles, Ignored and per-game folders behave as R23 to R26 say.
- R10 (parent R38): Each settings page is named for what it controls and holds the settings a gamer looks for there: Clips (was Replay Buffer), Hotkeys grouped by Clips, Recordings, Monitor Recording and Microphone, Audio Tracks, Games, Sounds, and Storage and Cleanup. The Recordings page is gone: its screen picker sits beside the Monitor Recording key, and Recordings are allowed per game. Clip, Recording and Monitor Recording are each explained in one plain sentence where the user meets them. Every folder setting is chosen with a folder picker (the Windows folder dialog, Open in Explorer, a reset to the default where there is one, the free space, and a refusal shown in plain words); no path is a bare text box.
- R11 (parent R38; architecture AD-22, AD-24): No text the user can see uses an internal term from EXPERIENCE.md's glossary. That covers labels, help, status, the engine's warnings, refusals, dialogs, the tray, tooltips and the Overlay. Settings keys, error codes, message names, Contracts and logs keep the internal names, and a default-run test guards the App's views.

## Constraints

- (architecture AD-22) Contracts change additively only: `AudioDevice.isDefault` (default false) and `LibraryItem.kind` (default absent) are optional. No key, message or code is renamed or removed, labels change only in text, and `schemaVersion` and the IPC `v` stay 1.
- (architecture AD-21, AD-4) A game's folder is assigned once by the engine and never moved; Games shows it read-only. A game's own settings are AD-4's overridable keys only: `bufferWindow`, `video` and `trackLayout`.
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
- Games lists every game and opens a page per game that shows and resets each inherited or changed setting, with R23 to R26 unchanged (R9).
- The settings pages are named for what they control, the Recordings page is gone, and every folder is chosen with the folder picker (R10).
- No user-visible text uses a glossary term, as the glossary test and a walk of every page show (R11).
- The welcome card, the kind badge, the tray items and the status hint are in place, and every existing App test still passes (R7, R8). Verified integrated on the builder's PC.

## Boundaries

- Out: a "Save Clip now" tray item or any new engine command, unless the builder approves it at 10.1 (PRD Q-2; the initiative's one Save Hotkey).
- Out: drag-and-drop reorder or drag-to-add; checkboxes on every Library row; a card grid; an advanced audio view; a "follow the Windows default device" source (R35, AD-4).
- Out: changes to the engine's Track Layout model, the Library's engine rules, Cleanup, or the Overlay's behaviour (its texts follow the glossary).
- Out: changing a game's folder, deleting a game's profile, and new per-game settings beyond AD-4's overridable keys and the existing profile attributes.
- Out: a page per Library item; the drill-in pattern allows one later.
- Out: renaming settings keys, error codes, messages or code to match the labels (AD-22).
- Out: new looks, tokens or themes (epic-modern-ui owns them).
- Touch point: the Windows shell's file operation (Copy to folder) and Explorer's multi-select (Show in Explorer), consumed only.
- Touch point: Discord's paste of several files, consumed only.
- Touch point: the Windows folder and file dialogs and the program icons read through the shell, consumed only.

## References

- parent — _bmad-output/initiative-replay-buffer/initiative-replay-buffer.md, Requirements R38 (and R15, R18, R27, R28, R30, R35)
- design — _bmad-output/initiative-replay-buffer/tickets/epic-ux-pass-2/ux-pass-2/EXPERIENCE.md (the gap review, Library list, Audio Tracks, Build Map)
- design — _bmad-output/initiative-replay-buffer/tickets/epic-ux-pass-2/ux-pass-2/DESIGN.md (the components addendum)
- design — _bmad-output/initiative-replay-buffer/tickets/epic-modern-ui/ux-modern-ui/DESIGN.md and EXPERIENCE.md (the tokens, shell and keyboard rules this extends)
- architecture — _bmad-output/initiative-replay-buffer/architecture-replay-buffer/architecture-replay-buffer.md, AD-1, AD-3, AD-4, AD-17, AD-20, AD-22, AD-23, AD-24

## Notes

- Decision: 2026-11-05, **the builder approved 10.1's design and every recommendation in `ux-pass-2/DECISIONS-FOR-BUILDER.md` ("I approve the recommended decisions in the design docs")**: (1) the page names and order: Library, then General, Clips, Hotkeys, Audio Tracks, Games, Sounds, and Storage and Cleanup; (2) the Recordings page is removed; (3) the glossary labels everywhere; (4) Favourites can't be deleted, even one at a time; (5) no tray "Save Clip now"; (6) the presets "Simple" and "For editing", with Simple seeded on a fresh install; (7) the renders as they stand, with the three DESIGN.md adjustments.

- Decision: 2026-11-05, 10.4's meter cost: **per-device meters use probe Tracks on `meters.watch`; the fallback level list isn't added.** A probe adds, per device, one `TrackMixer` input and a `PeakMeter` to the meter-only mixer: a copy, the limiter and a peak scan over 96,000 samples a second, well under 1 ms of CPU a second per device, plus about 0.8 MB of mixer ring each while watched. The real cost of a level per device is opening every device, which the fallback list would need just the same, and the hub already opens each device once (AD-20). So the fallback would save almost nothing and adds a Contracts field. Measured by QA with the Heavy `MeterProbeCostTests` on the builder's PC: 30 devices probed (19 playback, 11 recording) cost 36 ms of CPU a second over idle (0.036 of a core, against a limit of 0.1), and `meters.levels` held 16 a second (floor 15; nominally 20). The engine also stops a watch when its last client disconnects, so a crashed App doesn't leave every probed device open.
- Decision: 2026-11-05, 10.4 checked R17: a saved Track Layout prepares the new encoder chain at once and switches at the next save's Reset, or applies to the next game session (while a Recording runs, it waits for the Recording to stop). The hint "from your next game, or right after your next saved Clip" stands.
- Decision: 2026-11-05, 10.4 put the presets (`TrackLayoutPresets`) and the probe id rule (`MeterProbes`) in Contracts, so the engine's seed and the App's Simple preset share one builder (AD-24). Probe ids come from the endpoint id and flow, not a layout key, so a picker device that no Track holds has one too. The seed is off on test instances unless a test asks for it, since the other engine tests list the PC's real devices.
- Decision: 2026-11-05, the builder's words: "We need to do another pass on UX. The library is missing basic list functionality, like multi select." and "The track layout is convoluted and makes no sense to an end user."
- Decision: 2026-11-05, the builder's later words, via the coordinator: "game profiles needs to be revamped as well" and "game profiles probably needs to have a new page opened once you select a game"; "the recordings tab in the settings seems silly. What does recordings even mean. What is the point of that setting."; "Why is it called replay buffer. The name needs to make more sense for what settings it controls." "What is a Ring Folder? The name should reflect what it does for the end user." "It should also not be a normal text box. It should be a folder picker." These add R9 to R11 and stories 9 to 11.
- Decision: 2026-11-05, this overrides epic 7's decision (automation-state, 2026-11-02) that "Actions act on one item at a time (no multi-select in v1)".
- Decision: 2026-11-05, the timing: after epic 9 (its last story, 9.9), and before 8.11, the hands-off run on the builder's PC, so that run uses the final UI; 8.11's `after` names 10.8. 8.7 to 8.9 measure the engine (conformance, soaks, the T-01 to T-25 record) and don't wait on this epic.
- Decision: 2026-11-04, the gap review ran headless (`bmad-ux`) with no questions to the builder; its choices are in ux-pass-2/.memlog.md. Story 1 puts them to the builder with renders. Unattended default, awaiting review.
- Decision: 2026-11-04, Favourites are protected from Delete, single and many: Delete leaves them out and says so, and unstarring comes first. This changes 7.7, where one Favourite could be deleted after the confirmation. Unattended default, awaiting review at 10.1.
- Decision: 2026-11-04, bulk actions send the existing per-item commands one at a time, so no Contracts message is added and the LibraryActor's per-delete checks still apply (AD-24). Unattended default, awaiting review.
- Decision: 2026-11-04, the only Contracts change is the optional `AudioDevice.isDefault`, for the presets and the first-run seed. Per-device meters reuse `meters.watch` with probe Tracks; if 10.4 measures that as too costly, an optional per-device level list in `meters.levels` replaces it, additively. Unattended default, awaiting review.
- Decision: 2026-11-04, the first-run seed is written by the engine when settings.json has no Global `trackLayout`: the Simple preset from the Windows default devices. The catalog default stays an empty "Everything", and the builder's existing layout is untouched. Unattended default, awaiting review.
- Decision: 2026-11-05, the settings pages become Library, General, Clips (was Replay Buffer), Hotkeys, Audio Tracks, Games (was Game Profiles), Sounds (was Feedback Sounds), and Storage and Cleanup (was Library and Cleanup). The Recordings page is removed: its screen picker goes beside the Monitor Recording key on Hotkeys, and the per-game Recordings switch is on each game's page. "Clips" is the gamer's word for what the old Replay Buffer page controls; "Instant replay" is NVIDIA's, and "Replay Buffer" the engine's. Video quality and Memory or Disk with the cache folder stay on Clips, because the estimate depends on the Clip length beside them. Unattended default, awaiting review at 10.1.
- Decision: 2026-11-05, the glossary in EXPERIENCE.md maps every internal term to the label the user sees: Ring folder becomes Cache folder, Buffer Window becomes Clip length, Save cooldown becomes Time between saves, Target Game becomes "the game being captured", Global becomes "the main setting", and an override becomes "Changed for this game". Only text changes; keys, codes, messages and Contracts keep their names (AD-22). Unattended default, awaiting review at 10.1.
- Decision: 2026-11-05, one folder picker control serves every folder setting (the cache folder and the Clips folder), with a file variant for adding a game; a path is never a bare text box.
- Decision: 2026-11-05, Games uses a reusable drill-in pattern (a list, then a page per item, with a breadcrumb and Back), recorded in EXPERIENCE.md so a later Library item page can reuse it. Each of a game's three overridable settings shows "Uses the main setting" or "Changed for this game", replacing the Use Global check boxes with the same settings operations. A game's folder stays read-only (AD-21).
- Decision: 2026-11-05, Library rows show a Recording's kind from an optional `LibraryItem.kind` (AD-22) that the engine sets when a writer begins. Files found by the folder scan have none, except in the Monitor Recordings folder. Unattended default, awaiting review.
- Decision: 2026-11-04, the tracer is the design pass (story 1), as in epic 9, because every later story builds to its spines. The least certain parts come next and can run side by side: Library selection on a virtualized, grouped list (story 2) and the audio model work, with the default device, the seed and the probe meters (story 4).
- Decision: 2026-11-05, the sequencing, re-sliced for the builder's later points: two lanes after story 1. The Library lane is selection and bulk actions (2), then sort and search (3), both in LibraryPage and LibraryView. The Audio and settings lane is the defaults, presets and meters (4), then the editor (5). Next comes the settings reorganisation with the folder picker (9), which shares the settings layout and the nav with 5. Then Games (10), which embeds 5's editor and 9's Clips rows and file picker. Last is the plain-words sweep (11), after every new page exists. The smaller gaps (6) touch LibraryView and the status line, so they wait on 3 and 11. A Refactor sweep (7) and a closing end-to-end check on the builder's PC (8, hitl) close the epic, as in epics 6, 7 and 9. Ids 9 to 11 are new; build order is the table order, not the id.
- Decision: 2026-11-04, lean tests: the selection, bulk-action, sort, search, preset, seed and meter logic is tested in the view models and the engine with fakes; how it looks and feels is the builder's check at 10.1, 10.2, 10.5 and 10.8. No screenshot comparison tests.
- Open question: should the tray menu get "Save Clip now"? It needs a new engine command and touches PRD Q-2. The builder decides at 10.1; it joins 10.6 only if approved.
- Open question: are the page names (Clips, Audio Tracks, Games, Sounds, Storage and Cleanup), the Recordings page's removal, and the glossary's labels (Cache folder, Clip length) right? The builder decides at 10.1.
- Assumption: a saved audio-tracks change applies from the next Reset (R17), so the page's hint reads "from your next game, or right after your next saved Clip"; 10.4 checks this against the engine before 10.5 uses the wording.
