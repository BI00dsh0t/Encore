---
title: "Audio Tracks editor"
ticket: 5
status: review
---

# Plan: 10.5 Audio Tracks editor

**Intent:** the Track Layout page rebuilt as Audio Tracks, to EXPERIENCE.md's Audio Tracks section and the DESIGN.md addendum, on 10.4's draft model, presets and probe meters. The engine's Track Layout model (R14 to R19) is unchanged; only App files change.

## Done

- [x] **View model** (`Settings/TrackLayoutPage.cs`, class name kept so Game Profiles and the tests keep their references). It sits over 10.4's `AudioTracksDraft`.
  - One `AudioTrackCard` per Track, in file order, kept by id across edits: its number, an inline name, its level, the summary ("{n} sounds" or the silent line), its `AudioSourceRow`s, the mute switch (`HasMicrophone`), the reworded double-count line, and Rename, Move up, Move down and Remove track. Remove asks first when the Track has sounds.
  - Each `AudioSourceRow` has a glyph by flow, the Windows name with the parent when shared, its kind (playback, microphone or input), its own level, the not-connected note, Remove from this track, and Treat as a microphone, which changes the one record on every Track.
  - The sound picker (`OpenPicker`, `Pick`, `PickerRows`, `PickerSearch`): the playback and microphone sections, Windows default captions, live levels, added rows disabled, Ctrl+click keeps it open, and the highlighted row survives a device update.
  - Use a preset: Simple and For editing, after a Yes/No confirmation (`Confirm`, which tests replace).
  - Save changes and Discard replace Save and Revert. The blank-name refusal shows on the save bar and outlines the name. `HasUnsavedChanges` drives the nav dot (`SettingsPage` virtual, forwarded by `NavItem`).
  - Meters: the watched layout is `MeterProbes.WithProbes(draft, picker open ? every listed device : none)`. Each Track's level comes from its own entry, and each source's and picker row's from its probe. The disconnect and reconnect rules from 10.4 are kept.
  - The read-only summary (`ShowsSummary`, `Summary`): a game that uses the main tracks shows them as text. Until 10.10, Game Profiles keeps embedding the editor under the same check box, relabelled "Use the main audio tracks".
- [x] **View** (`Settings/TrackLayoutView.xaml`): the sticky save bar above the scroller, the intro line with Use a preset ▾, the cards, + Add a track, the "No tracks" line, and the picker as a `Popup` flyout grouped by section.
  - Keys: Ctrl+S saves. On a card, F2 renames and Alt+↑/↓ move it. Delete removes a focused source row. In the picker, ↑ and ↓ move, Enter adds, Ctrl+Enter or Ctrl+click adds and keeps it open, Esc closes, and typing goes to the search box.
  - A page embedded in Game Profiles drops its ScrollViewer (`Unscroll`).
- [x] **Styles** in their own dictionary, `Theme/AudioTracksTheme.xaml`, placed by `ThemeService`: the save bar, card, number, name outline, summary, glyph, caption, source row, picker surface, picker caption and picker item. Every colour is a token by `DynamicResource`. The meters reuse `RbLevelMeterStyle` at 160 and 80 × 4.
- [x] **Nav dot:** a 6 px accent dot after the item's text in both nav templates, with its radius from the corner tokens (round-cornered in Fluent, square in Gaming dark).
- [x] **Strings** follow the glossary: the page and Game Profiles section are "Audio Tracks", and every string in EXPERIENCE.md's Audio Tracks wording table is used.

## Deviations

- The card name is a plain TextBox (no borderless edit-on-click template); F2 and Rename focus and select it.
- The old Devices pool and its tests are gone. Tests that only exercised the pool (checking a device on a Track, the picker's choices list) were rewritten against the cards and the picker, rather than kept with renamed strings.

## Tests (lean)

- `TrackLayoutPageTests`:
  - a picker sound goes on that Track only, and an input is a microphone;
  - removing a sound from its last Track drops it from the saved layout;
  - Move up and Move down keep the ids, and an empty Track is removed without asking;
  - Remove track asks when the Track has sounds;
  - Treat as a microphone and the mute switch;
  - a preset applies only after its confirmation;
  - the save bar and nav dot follow dirty, and Discard restores the saved tracks;
  - the read-only summary;
  - per-source and picker levels come from probes, and the saved layout has none.
  - The existing save, conflict, refusal, reload, double-count and meter tests are kept, updated for the new strings and the card API.
- `SettingsLayoutTests`: the page in both themes with fabricated Tracks, devices and levels, an unsaved draft, and the picker open. It checks every Audio Tracks resource key resolves under each theme, and saves `settings-audio-tracks-{fluent,gaming-dark}.png` and `settings-audio-tracks-picker-{fluent,gaming-dark}.png` beside the App test binary.
- `GameProfilesPageTests`: updated for the source rows.

## HITL (builder)

- Rebuild the BEACN layout from a preset with the meters moving, save it, and check the next Clip's tracks in DaVinci Resolve.

## Log

- 2026-11-05: built and verified; READY FOR QA.
- 2026-11-05: QA round 1, 2 findings fixed: a reload closes the sound picker (its Track belonged to the old draft), and the nav dot takes its radius from RbSmallCornerRadius and RbNavItemCornerRadius.
