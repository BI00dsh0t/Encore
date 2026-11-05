---
title: "Settings reorganised: the folder picker, the Clips, Hotkeys and Storage pages, Recordings removed"
ticket: 9
status: review
---

# Plan: 10.9 Settings reorganised

**Intent:** the settings pages rearranged to EXPERIENCE.md's Information Architecture, Settings pages and Folder picker sections. Only labels, layout and where controls sit change. No settings key, message or Contracts type changes (AD-22, AD-24), and no Engine or Core code changes.

## Done

- [x] **Folder picker** (`Settings/FolderPicker.cs`, `FolderPickerView.xaml`): one reusable control in the shared settings layout.
  - The path is read-only and selectable, with the full path as its tooltip; it reads "Default ({path})" while the key is unset.
  - Browse… opens `OpenFolderDialog` at the current folder and sends the key's Global `set` on a pick; a cancel sends nothing.
  - Open in Explorer goes through the shell, and is disabled with a tooltip while the folder is missing.
  - Use default shows only when a path is set, and sends `remove`.
  - The drive's free space shows as "212 GB free on C:".
  - A danger line shows the refusal in plain words: "The cache folder can't be inside the Clips folder." or "ReplayBuffer can't write to that folder." The old path is kept.
  - It has a disabled state with its caption, and a file variant (`IsFile`, a file dialog with a filter) for 10.10's Add a game.
  - Seams for tests: `Dialog`, `OpenFolder`, `Exists`, `FreeBytes`, `Validate` and `Confirm`.
- [x] **Clips** (was Replay Buffer, `ReplayBufferSettingsPage`):
  - the intro with the Save Hotkey;
  - Clip length: Save the last N seconds, and Time between saves with its help;
  - Where recent gameplay is kept: Memory and Disk, each with its estimate line, and the cache folder as a folder picker, enabled only under Disk ("Used only in Disk mode") with its help;
  - Video quality — for Clips and Recordings: Format with the glossary's codec names, Resolution, Frame rate "fps (up to N)", Bitrate with its help, and "Variable bitrate (smaller files in quiet scenes)".
  - The keys and validation are as before. The refusal texts use the glossary.
  - A game's own Clip length and video quality keep their check boxes until 10.10, relabelled "Use the main …".
- [x] **Hotkeys**, grouped into Clips, Recordings (with Record mode), Monitor Recording and Microphone, each with its sentence from "Clips and Recordings, explained", filled with the Clip length and the bound key.
  - The screen picker and Refresh moved here. The Recordings page's view model became `MonitorPicker`, a component Hotkeys hosts, so 11.4's choice of several screens replaces it in one place.
  - Row labels: Record, Monitor Recording, and their start and stop keys.
- [x] **Recordings page removed:** its view and nav item are gone, and `monitorRecordingMonitor` is unchanged. The tray never opened that page, so nothing else had to be redirected.
- [x] **Storage and Cleanup** (was Library and Cleanup): the Clips folder is a folder picker with its help. It keeps the "Existing files aren't moved" confirmation, now on Use default too. The size line is kept, and Cleanup is unchanged.
- [x] **Sounds** (was Feedback Sounds): only the title and section header changed.
- [x] **Nav order:** Library, General, Clips, Hotkeys, Audio Tracks, Game Profiles (renamed Games by 10.10), Sounds, and Storage and Cleanup.
- [x] The default cache folder (`<LocalData>\ring`) is passed from `ShellApp` through `MainViewModel` to the Clips page, so "Default (…)" names it.

## Deviations

- **The path box has no middle ellipsis.** It's a read-only TextBox, so the whole path stays selectable and copyable, and the tooltip shows it in full.
- **No `ring-path-invalid` error code exists in the engine.** The App checks the cache folder before sending: it may not be inside the Clips folder, and the folder must be writable, probed with a delete-on-close file in the nearest existing folder. A refused update from the engine still shows on the page's error bar.
- **Use default for the Clips folder now sends `remove`** where it sent a `set` to null, as the ticket asks. The engine treats both as the default.

## Tests (lean)

- `FolderPickerTests`: a pick sends `set`, Use default sends `remove`, and a cancel sends nothing; a refused pick shows its text and keeps the old path; Open in Explorer is off for a missing folder; the free-space line.
- `ReplayBufferSettingsPageTests.TheCacheFolderIsEnabledOnlyUnderDiskAndSendsRingPathAndTimeBetweenSaves`, plus the existing bufferWindow, replayStorage and video test.
- `MonitorPickerTests` (was `RecordingsSettingsPageTests`): the screen picker on Hotkeys sends `monitorRecordingMonitor`.
- `MainViewModelTests.TheNavListsTheEightPagesInOrderWithNoRecordingsPage`.
- `SettingsLayoutTests`: Clips, Hotkeys, Sounds, Storage and Cleanup and Game Profiles load in both themes with their sections as cards. Every theme key the changed views use resolves under each theme. They're saved as `settings-{clips,hotkeys,sounds,storage-and-cleanup}-{fluent,gaming-dark}.png` beside the App test binary.
- Existing tests were updated only for moved controls and renamed strings.

## Log

- 2026-11-05: built and verified; READY FOR QA.
- 2026-11-05: QA round 1, 1 finding fixed: the Clips folder is refused when it contains the cache folder (its ringPath, else the engine default), "The Clips folder can't contain the cache folder." The containment check also handled a drive root wrongly (D: never matched), now fixed for both directions.
