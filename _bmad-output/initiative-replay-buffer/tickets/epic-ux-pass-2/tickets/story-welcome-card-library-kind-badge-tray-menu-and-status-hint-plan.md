---
title: "Welcome card, Library kind badge, tray menu and status hint"
ticket: 6
status: done
---

# Plan: 10.6 Welcome card, Library kind badge, tray menu and status hint

**Intent:** the remaining small gaps, to EXPERIENCE.md's Welcome and the empty Library, Clips and Recordings explained, Tray menu and Status header sections. **No tray "Save Clip now":** the builder declined it at 10.1, so no engine command was added.

## Done

- [x] **`LibraryItem.kind`** (Contracts, additive under AD-22):
  - an optional `Kind`, written only when set, with the values in `LibraryItemKinds` (`clip`, `recording`, `monitorRecording`);
  - `ILibraryWrites` gains a default-implemented `BeginWrite(path, profileId, kind)`, so other writers and test fakes are unchanged. The orchestrator passes `clip` for a Clip, and `recording` or `monitorRecording` (from `RecordingRun.Monitor`) for a Recording;
  - the `LibraryActor` keeps the kind on the item and in `library.json` (`LibraryFileEntry.Kind`). A scanned file has none, except one in the `Monitor Recordings` folder (`ScannedKind`).
- [x] **The kind badge:** a Library row shows "Recording" or "Monitor Recording" on the thumbnail's top-left, in the length badge's style. A Clip, or a file whose kind is unknown, shows none.
- [x] **The welcome card** (`LibraryWelcome`, in place of "No Clips or Recordings yet." under the same rule: empty, no filter, no search; `LibraryPage.ShowsWelcome`):
  - the lead line with the live Save Hotkey on a key-cap chip and the Clip length;
  - "Audio: {track 1's sounds} · Change", or the silent line in caution with "Set up audio";
  - "Clips are saved in {folder} · Change";
  - the Recordings line in caption.
  - The links open Audio Tracks and Storage and Cleanup. Its texts follow every snapshot.
- [x] **Tray menu** (`ShellApp.CreateTrayMenu`):
  - the live status item, disabled and bound to the status header's text, then a separator;
  - Open, Open Library (selects the Library, then opens), and Open the Clips folder (the engine's Library folder, in Explorer);
  - a separator, then Start with Windows, Restart (crash loop only), the dev gallery and Quit.
- [x] **Status hint** (`StatusLine.Hint`): "· {Save Hotkey} saves the last {Clip length}" in text-secondary while capturing. It hides during a Recording or Monitor Recording, when no Save Hotkey is set, or when it failed to register (an "unbound" or "inactive" status isn't a failure, as on the Hotkeys page).

## Tests (lean)

- Core `LibraryActorTests.KindsAreKeptThroughTheIndexAndARestartAndAScannedFileHasNoneOutsideMonitorRecordings`.
- App:
  - `LibraryPageTests.TheWelcomeCardShowsOnlyOnAnEmptyUnfilteredLibraryAndFollowsTheSettings`, which also checks the badge shows for a Recording only;
  - `TrayTests`: the menu's order, the status item following a snapshot, Open Library, and Open the Clips folder;
  - `StatusLineTests.TheHintSaysWhichKeySavesHowMuchWhileCapturingOnly`;
  - `LibraryThemeTests.TheWelcomeCardABadgedRowAndTheStatusHintLoadInBothThemes`, which saves `library-welcome-{fluent,gaming-dark}.png` and `library-badge-hint-{fluent,gaming-dark}.png` beside the App test binary.
  - `GlossaryTests` passes with the new XAML.
- `LibraryThemeTests`' empty-state check now finds the card (a `FrameworkElement`) by the same name.

## Log

- 2026-11-05: built and verified; READY FOR QA.
