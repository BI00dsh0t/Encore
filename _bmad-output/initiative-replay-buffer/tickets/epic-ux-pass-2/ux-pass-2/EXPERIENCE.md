---
name: ReplayBuffer UX pass 2
status: final
updated: 2026-11-04
sources:
  - ../epic-ux-pass-2.md
  - ../../epic-modern-ui/ux-modern-ui/EXPERIENCE.md
  - ../../epic-modern-ui/ux-modern-ui/DESIGN.md
  - ../../../architecture-replay-buffer/architecture-replay-buffer.md
---

# ReplayBuffer UX pass 2 — Experience Spine

## Foundation

- **What this is.** A gap review of the whole App, from a gamer's view rather than a developer's, and the design for epic 10's fixes. It **extends** epic 9's spine ([ux-modern-ui/EXPERIENCE.md](../../epic-modern-ui/ux-modern-ui/EXPERIENCE.md)). Everything that spine sets stays unless a section here replaces it: the shell, the nav pane, the status header, the themes, the dialog, the Overlay card, and the keyboard rules.
- **Form factor:** the same single-surface Windows desktop app, in .NET 10 WPF with Fluent's control behaviour as the baseline.
- **Visual identity:** epic 9's [DESIGN.md](../../epic-modern-ui/ux-modern-ui/DESIGN.md), plus this folder's [DESIGN.md](DESIGN.md) addendum for the new components. Token names (`{colors.accent-subtle}`) are DESIGN.md's. There are no new tokens.
- **The builder's words (2026-11-05):**
  - "We need to do another pass on UX. The library is missing basic list functionality, like multi select."
  - "The track layout is convoluted and makes no sense to an end user."
  - "Game profiles needs to be revamped as well." and "Game profiles probably needs to have a new page opened once you select a game."
  - "The recordings tab in the settings seems silly. What does recordings even mean. What is the point of that setting."
  - "Why is it called replay buffer. The name needs to make more sense for what settings it controls." "The Ring Folder setting makes no sense to the end user. What is a Ring Folder?" "It should also not be a normal text box. It should be a folder picker."
- **This changes behaviour, unlike epic 9.** It overrides epic 7's decision that "actions act on one item at a time (no multi-select in v1)".
- **What stays:** the engine's rules. A file being written can't be renamed or deleted. Delete confirms and uses the Recycle Bin. Cleanup never deletes Favourites. A Track Layout is still named Tracks of devices, with Mic Devices and mute (R14 to R19). Games are still detected, chosen, profiled, Ignored and foldered as R23 to R26 say. Settings keys, error codes and Contracts keep their names (AD-22); only what the user reads changes.

The protagonist of the flows is **Sam**. Sam plays Valorant and Apex every evening, used ShadowPlay, and has never edited a settings file. The builder is still the one real user, but Sam keeps every screen honest about jargon.

## The gap review

What a gamer who isn't a developer runs into today, ranked by impact. Epic 10 fixes everything above the line.

| # | Where | Gap today | Fix (section) |
|---|---|---|---|
| 1 | Library | No selection at all. The list is an `ItemsControl`: rows can't be selected or focused, every action is one row's right-click, and there are no shortcuts. Deleting 20 old Clips takes 20 menus and 20 dialogs. | [Library list](#library-list) |
| 2 | Track Layout | A fresh install saves Clips with **no sound**. The default layout is one "Everything" Track with no devices, and nothing says so. | [First run just works](#first-run-just-works) |
| 3 | Track Layout | Two-step and indirect. A device is added to a "Devices" pool first, and only then ticked onto a Track in a detail pane under a 110 px list. What each Track holds is invisible until it's selected. | [Audio Tracks](#audio-tracks) |
| 4 | Game Profiles | One long page. Each game is an editable name box with its program path under it, plus two unexplained check boxes. A separate "Overrides for" combo box sits far below the list, and the embedded editors use "Use Global …" check boxes. Nothing shows which settings a game inherits and which it changes, and "Add a game by hand" is a bare path box. | [Games](#games) |
| 5 | Everywhere | Internal terms in labels, status and warnings: Track Layout, Ring, Target Game, Draining, Reset, Buffer Window, Game Profile, Global, override, engine, "(Output)" and "(Input)", and profile ids. Engine warnings say things like "the Replay Buffer stopped growing. Clips may be shorter than the Buffer Window." | [The plain-language rule and glossary](#the-plain-language-rule-and-glossary) |
| 6 | Settings | The Replay Buffer page's name says nothing about what it controls. "Ring folder (Disk)" is a bare text box, and "Buffer Window (s)" and "Save cooldown (s)" are engine words. | [Clips page](#clips-was-replay-buffer), [Folder picker](#folder-picker) |
| 7 | Settings | The Recordings page holds only a monitor picker, a Refresh button, and a note pointing to two other pages. "Recording" is never explained against "Clip". | [Clips and Recordings, explained](#clips-and-recordings-explained), [Hotkeys](#hotkeys) |
| 8 | Library | No sort (always newest first) and no search. Finding "that ace from Tuesday" means scrolling. | [Sort and search](#sort-and-search) |
| 9 | Track Layout | Internal words: "Track Layout", "Mic", "Mic Mute affects this Track", "Use Global Track Layout", "May count audio twice: mixes outputs from…". | [Glossary](#the-plain-language-rule-and-glossary) |
| 10 | Track Layout | Meters only per Track, so with a dozen BEACN endpoints Sam can't tell which one is making sound before adding it. | [Live meters](#live-meters) |
| 11 | Track Layout | Save and Revert sit at the bottom, below the fold. An unsaved draft is easy to leave behind. | [Saving](#saving) |
| 12 | First run | Nothing says which key saves a Clip (Alt+F10) or what to do first. The empty Library says only "No Clips or Recordings yet." | [Welcome](#welcome-and-the-empty-library) |
| 13 | Library | A Recording and a Clip look the same in the list. | [Clips and Recordings, explained](#clips-and-recordings-explained) |
| 14 | Tray | Only Open, Start with Windows and Quit. There's no state, and no way to the Clips folder. | [Tray menu](#tray-menu) |
| 15 | Status header | It says "Buffering on Valorant" but not how to save. | [Status header](#status-header) |
| — | | *Below the line: noted, not in epic 10.* | |
| 16 | Tray | No "Save Clip now". It needs a new engine command, and touches PRD Q-2 and the one-Save-Hotkey boundary. | [Open questions](#open-questions) |
| 17 | Library | No page per Clip. [Drill-in pages](#drill-in-pages) allow one later. | Later |
| 18 | Overlay | A "Clip saved" card can't be clicked to reach the Clip. It's click-through by design (AD-15). | Not a gap: by design |
| 19 | General, Sounds | Usable as they are; the glossary covers their wording. | — |

## Information Architecture

The nav keeps epic 9's flat pane, but the Settings pages are reorganised around what a gamer wants to change. One page goes, five are renamed, and Hotkeys gains what the removed page held. Settings keys and the engine don't change; only titles, layout and where a control sits do.

| Nav item | Was | Holds |
|---|---|---|
| **Library** | Library | The list, its selection, the command bar, sort, search and filters |
| *Settings* (label) | | |
| General | General | Appearance, Startup, and the Overlay's dot |
| **Clips** | Replay Buffer | Clip length, time between saves, where recent gameplay is kept (Memory or Disk, and the cache folder), the memory or disk estimate, and Video quality for Clips and Recordings |
| **Hotkeys** | Hotkeys | Grouped by what each key does: Clips, Recordings (with Record mode), Monitor Recording (with the screen picker moved here), and Microphone |
| **Audio Tracks** | Track Layout | The Track cards, the presets, the sound picker |
| **Games** | Game Profiles | A list of games; each opens [its own page](#games) |
| **Sounds** | Feedback Sounds | as today |
| **Storage and Cleanup** | Library and Cleanup | The Clips folder (folder picker) and its size, and Cleanup |
| — | Recordings | **Removed.** Its monitor picker moves to Hotkeys → Monitor Recording, beside that key. The per-game Recordings switch is on each game's page, and its note is replaced by [the explanations](#clips-and-recordings-explained). |

- **Order:** Library, General, Clips, Hotkeys, Audio Tracks, Games, Sounds, Storage and Cleanup. Every page is still one click away.
- **Keyboard:** Ctrl+Tab still steps through the nav.
- **The tray's open-Settings path** still lands on the page it did. A path that would have opened Recordings opens Hotkeys.
- [DECISION] **Video quality stays on the Clips page,** under a header saying it applies to Clips and Recordings. It's the page about what a save produces. A separate page would be thin, and Storage is about folders.
- [DECISION] **Where recent gameplay is kept (Memory or Disk, and the cache folder) stays on the Clips page,** not Storage. The memory or disk estimate depends on the Clip length beside it.
- [DECISION] **The page name is "Clips".** It's the word Sam already uses for what the page controls. "Instant replay" is NVIDIA's name, and "Replay Buffer" is the engine's.

## Voice and Tone

Plain words a gamer already uses. The domain words they know stay: **Clip, Recording, Monitor Recording, Favourite, Hotkey, track, Library, Cleanup, Overlay**. Engine words go; see [the glossary](#the-plain-language-rule-and-glossary).

### The plain-language rule and glossary

**The rule:** no internal term in the left column may appear in anything the user sees, unless the right column keeps it. That covers labels, help text, status text, warnings (the engine's `warning.raised` texts included), dialogs, the tray menu, tooltips and the Overlay.

Code, settings keys, error codes, Contracts, logs and automation ids keep the internal names, so this is a change to text only (AD-22, AD-24).

| Internal term (code, keys, Contracts) | What the user sees |
|---|---|
| Replay Buffer (the feature), `bufferWindow` | **Clips** (the page); "Clip length": "Save the last {n} seconds". In sentences: "ReplayBuffer keeps the last {n} of your game ready." The app's name, ReplayBuffer, stays. |
| `replayStorage` Memory / Disk | "Keep recent gameplay in" **Memory** / **Disk** |
| Ring, ring file, ring generation, `ringPath` | **Cache folder**: "In Disk mode, the last {Clip length} of gameplay is kept here in one file of about {estimate}. It doesn't grow." |
| `saveCooldown` | "Time between saves": "A press this soon after a save is ignored." |
| Target Game | "the game being captured"; a **Capturing now** pill |
| Game Detection | "Detected automatically" |
| Game Profile | a **game**; "this game's settings" |
| Global Settings | the **main setting** ("Uses the main setting") |
| override (profile scope) | **Changed for this game** |
| `ignored` | **Capture this game** (off); a "Not captured" pill in the list |
| `recordingsEnabled` | **Allow Recordings in this game** |
| `manualGames` | **Added by hand** |
| Track Layout | **Audio Tracks** |
| Mic Device (`isMic`) | microphone |
| `muteAffected` | "Mic mute key silences the microphone on this track" |
| Output / Input Device, flow | playback / microphone (or "input" for a recording device that isn't a mic) |
| parent device | the device's name, in brackets, only when two share a name |
| endpoint id, profile id | never shown; a game's page shows its program's location |
| Output Settings (`video`) | **Video quality**: Format, Resolution, Frame rate, Bitrate (kbps), "Variable bitrate (smaller files in quiet scenes)" |
| codec names | "H.264 (plays everywhere)", "HEVC (smaller files)", "AV1 (smallest; newer players only)" |
| Output Folder (`outputFolder`) | **Clips folder**: "Where Clips and Recordings are saved" |
| profile `folder` | "this game's folder" |
| Reset (of the ring) | never shown: "from your next game, or right after your next saved Clip" |
| Buffering / Draining | "Ready to save Clips" / "Finishing the save…" |
| Replay Buffer off | "Waiting for a game" |
| generation, segment, fragment, anchor, encoder chain, packet | never shown |
| encoder | "your graphics card's video encoder", only where a warning needs it |
| engine | "ReplayBuffer's background service" |
| Feedback Sounds | **Sounds** |
| Monitor Recording | stays: "a Recording of a whole screen" |
| Clip, Recording | stay, and are [explained](#clips-and-recordings-explained) where Sam meets them |
| Overlay | stays: "the small on-screen indicator" in help text |
| Cleanup, Favourite, Library, Hotkey | stay |

**Who applies the rule:**

- Story 10.5 applies it on Audio Tracks.
- Story 10.9 applies it on the pages it rearranges: Clips, Hotkeys, and Storage and Cleanup.
- Story 10.10 applies it on Games.
- Story 10.11 applies it to everything else: General, Sounds, the status texts, the engine's warning texts, refusals, dialogs, the tray tooltip and the Overlay. It adds a default-run test that fails on a glossary term in the App's XAML.
- Story 10.6 writes its new Library, tray and status texts to it.

**Wording changes on Audio Tracks:**

| Was | Now |
|---|---|
| Devices / Devices on this Track | Sounds on this track |
| Speakers (Output) | Speakers · *playback* (caption) |
| Microphone (Input) | Microphone · *microphone* (caption) |
| unavailable | Not connected — records silence until it's back |
| Mic (checkbox) | Treat as a microphone (source menu) |
| May count audio twice: mixes outputs from A, B. | You may hear some sounds twice: A and B can both carry the same audio. |
| Add Track | Add a track |
| Up / Down / Delete | Move up / Move down / Remove track (card menu) |
| Save / Revert | Save changes / Discard |
| Not saved: every Track needs a name. | Give every track a name to save. |
| The Track Layout changed elsewhere, so this page reloaded it. | Audio tracks were changed somewhere else, so this page shows the new ones. |

**New strings:**

| Where | Text |
|---|---|
| Library summary, nothing selected | "{n} items · {size}" + caption "Click to select. Ctrl or Shift selects more." |
| Library command bar | "{n} selected", plus "({m} hidden by filters)" when some are |
| Library bulk delete confirm | "Move {n} items ({size}) to the Recycle Bin?" |
| … with skips | + "{f} Favourites and {w} files being saved are kept." (only the parts that apply) |
| Library delete result, partial | "Deleted {k} of {n}. {reason for the rest}." |
| Library search placeholder | "Search by name" |
| Library sort caption / choices | "Sort" — Newest first, Oldest first, Name, Largest first, Longest first, Game |
| Group by choice | "None" (added) |
| Library row badge | "Recording", "Monitor Recording" (none on a Clip) |
| Audio Tracks intro | "Every Clip and Recording gets these tracks, in this order. Discord and browsers play track 1 only; editors like DaVinci Resolve show each one." |
| Track card summary | "{n} sounds" / "No sounds — this track will be silent" |
| Presets | "Simple — one track with your PC's sound and your microphone"; "For editing — everything, game and chat, and your microphone on their own tracks" |
| Preset confirm | "Replace your audio tracks with {preset}?" |
| Save bar | "Unsaved changes" — Save changes, Discard |
| Applies hint | "Changes apply from your next game, or right after your next saved Clip." [ASSUMPTION, checked in story 10.4] |
| Welcome card | "Play a game, then press **{Save Hotkey}** to save the last {Clip length}." with "Audio: {sources of track 1} · Change" |
| Status header hint, while capturing | "· {Save Hotkey} saves the last {Clip length}" |
| Tray status item | Today's status text, disabled, at the top |
| Tray items | "Open Library", "Open the Clips folder" |
| Games intro | "Games are found when you play them. Each one uses the main settings unless you change them on its page." |
| Games empty | "No games yet. Play a game and it appears here, or add one by hand." |
| Game row captions | "Detected automatically" / "Added by hand"; "Capturing now"; "{n} settings changed"; "Not captured" |
| Inheritance chip | "Uses the main setting" / "Changed for this game" |
| Inheritance buttons | "Change for this game", "Reset to the main setting", "Edit the main setting" |
| Reset confirm | "Reset {game}'s {section} to the main setting?" |
| Folder picker | "Browse…", "Open in Explorer", "Use default"; caption "{free} free on {drive}" |
| Folder picker errors | "The cache folder can't be inside the Clips folder." / "ReplayBuffer can't write to that folder." / today's other refusal texts, reworded by the glossary |

| Do | Don't |
|---|---|
| Name a device the way Windows' Sound settings does | Show endpoint ids, profile ids, "parent", "flow" |
| Say what happens ("records silence until it's back") | Say what state it's in ("unavailable") |
| Say what a setting does for Sam ("Save the last 30 seconds") | Name the mechanism ("Buffer Window", "Ring folder") |
| Keep sentence case; Gaming dark's uppercase stays a template transform | Write uppercase strings |

## Library list

### Selection model

The list becomes a selectable list with Windows Explorer's conventions. It stays one virtualized list of group headers and rows, as epic 9 decided. Group headers can't be selected or focused.

| Input | Does |
|---|---|
| Click a row | Selects only it and makes it the anchor and focus |
| Ctrl+click | Toggles that row; it becomes the anchor |
| Shift+click | Selects the range from the anchor to it, in the shown order, across group headers |
| Ctrl+Shift+click | Adds that range to the selection |
| Ctrl+A | Selects every shown row (the filter and search apply) |
| ↑ ↓, Page Up/Down, Home, End | Move focus and select only that row |
| Shift + any of those | Extends the range from the anchor |
| Ctrl + any of those | Moves focus without changing the selection |
| Ctrl+Space | Toggles the focused row |
| Esc | Cancels a rename first; else clears the search if the search box is focused; else clears the selection |
| Click on empty space below the rows | Clears the selection |
| Click the star | Toggles that row's Favourite only and doesn't change the selection |
| Double-click | Opens that row |

- **Selection is a set of item paths**, held by the page's view model and not by the list control, so virtualization and snapshots can't lose it.
- **Filtering and searching keep it.** A selected item that a filter or search hides stays selected. Actions act only on selected items that are **shown**, and the command bar counts the hidden ones ("3 selected (2 hidden by filters)"). Clearing the filter shows them still selected.
- **Snapshots keep it.**
  - A new Clip arriving doesn't change the selection, the focus or the scroll position.
  - An item that leaves the Library (deleted, or moved outside the app) leaves the selection.
  - The App's own rename carries the selection and focus to the new path.
  - If the focused row goes, focus moves to the next shown row, else the previous one.
- **A being-written item** can be selected. Actions skip it where today's rules refuse it: rename, delete and copy.

### Empty-selection state and the command bar

One bar sits between the filter row and the list, always the same height so the list never jumps.

- **Nothing selected:** the summary "{n} items · {size}" of the shown list, with the caption "Click to select. Ctrl or Shift selects more." No action buttons show, rather than showing disabled ones.
- **One or more selected:** "{n} selected" (plus "({m} hidden by filters)") and the actions, then a **Clear selection** (✕) button at the right end.

| Action | Shown when | Acts on | Notes |
|---|---|---|---|
| Open | exactly 1 selected | it | Today's open in the default player |
| Favourite / Remove from Favourites | ≥ 1 | the shown selected items | One button. It reads "Favourite" if any of them isn't one, which stars them all; it reads "Remove from Favourites" if all are, which unstars them all. Sends one `library.setFavourite` per item that changes. |
| Copy | ≥ 1 | the shown, selected, not-being-written items | One file drop with every file, so it pastes as several attachments (Discord, Explorer). |
| Copy to folder… | ≥ 1 | the same | A folder picker, then the Windows shell copies the files with its own progress and name-conflict dialogs. The App never opens the media itself (AD-1). |
| Show in Explorer | ≥ 1 | the shown selected items | One Explorer window per folder, with that folder's selected files selected. Past 3 folders it opens the Clips folder instead. |
| Rename | exactly 1, not being written | it | Today's inline rename |
| Delete | ≥ 1 deletable | the shown selected items that aren't Favourites or being written | See [Delete](#delete). Disabled, with a tooltip saying why, when every selected item is a Favourite or being written. |

### Delete

- **One confirmation for any number of items:** "Move {n} items ({size}) to the Recycle Bin?". For one item it's today's "Move “{name}” to the Recycle Bin?".
- **Favourites are protected.** Delete never deletes a Favourite, whether one or many are selected. They are left out and named in the confirmation: "{f} Favourites … are kept." To delete a Favourite, unstar it first.
  - [DECISION] This changes story 7.7, where one Favourite could be deleted after the confirmation. It's for the builder to confirm at 10.1.
- **Being-written items** are left out the same way.
- **Esc or No** deletes nothing, as today.
- **After Yes:**
  - The App sends one `library.delete` per item, in order, with its identity as shown (AD-3).
  - Past 10 items the bar reads "Deleting {k} of {n}…", and the actions are disabled until the deletes finish.
  - Rows leave as the snapshot reports each removal.
- **Failures:** one line in the existing action-error place, such as "Deleted 4 of 5. 1 file was still being saved." It uses today's refusal texts, and the most common reason wins when there are several.

### Context menu

- **Right-click a row in the selection:** the menu acts on the selection.
- **Right-click a row outside it:** that row alone is selected first, then the menu opens (Explorer's rule).
- **The Menu key or Shift+F10** opens it for the focused row, by the same rule.

| Item | Gesture shown | Shown or enabled |
|---|---|---|
| Open | Enter | Shown for 1 selected item |
| Favourite / Remove from Favourites | | as the bar |
| Copy | Ctrl+C | as the bar |
| Copy to folder… | | as the bar |
| Show in Explorer | | as the bar |
| *separator* | | |
| Rename | F2 | Shown for 1 selected item; disabled while it's being written |
| Delete | Del | as the bar |

### Keyboard shortcuts

These work while focus is in the list. Ctrl+F also works from anywhere on the Library page.

| Key | Does |
|---|---|
| Enter | Open the focused row |
| Delete | Delete the selection (with the confirmation) |
| F2 | Rename the focused row, when it's the only one selected |
| Ctrl+C | Copy the selection |
| Ctrl+A | Select every shown row |
| Ctrl+F | Focus the search box |
| Esc | as the selection table |
| Space | Toggle the focused row's Favourite |

- ListBox letter-key text search is turned off, because search has its own box.
- While a rename box has focus, it takes every key, as today.
- **Tab order:** filter row → search → command bar actions → the list (focus lands on the focused row, else the first) → out.

## Sort and search

The filter row under the page title (epic 9's WrapPanel) gains a **Search** box at its start and a **Sort** combo box after Group by. It stays one row that wraps.

```
[🔍 Search by name     ]  Group by [Game ▾]  Sort [Newest first ▾]  Game [All games ▾]  Date [All dates ▾]  [★ Favourites only]
```

**Sort** orders rows within each group.

| Choice | Order | Ties |
|---|---|---|
| Newest first (default) | date, descending | path |
| Oldest first | date, ascending | path |
| Name | file name A–Z, case-insensitive, natural number order ("Clip 2" before "Clip 10") | date ↓ |
| Largest first | size ↓ | date ↓ |
| Longest first | length ↓; an unknown length sorts last | date ↓ |
| Game | game name A–Z | date ↓ |

**Grouping** adds **None**, a flat list with no headers, so "largest first across everything" is one choice away.

- Group by Game orders its groups as today: games by name, then Monitor Recordings, then Other.
- Group by Day orders its days newest first, or oldest first under Oldest first.

**Search** filters by file name as you type, case-insensitive, matching anywhere in the name.

- It applies on top of the other filters.
- ✕ in the box or Esc clears it.
- With search text and no match, the list area says "No items match “{text}”." This is the one exception to epic 9's empty list for a filter that matches nothing, because search is typed.

**What's remembered:** sort and grouping persist in `LibraryViewState` with the other view choices. Search text doesn't: the Library opens unsearched.

## Audio Tracks

### What it is today, and why it confuses

The page shows two lists, each 110 px tall: **Devices**, a pool, each line "Name (Output)" with a Mic tick; and **Tracks**, a meter and a name each. Under the Tracks list, a detail pane for the selected Track has its name, "Mic Mute affects this Track", and a tick box per pooled device. Save and Revert sit below everything.

A gamer has to understand four engine ideas before hearing anything:

- the device pool, separate from the Tracks;
- that adding a device puts it in "Everything" only;
- that "Mic" is set on a device but mute is set on a Track;
- that nothing is saved until the bottom button.

A fresh install starts with an empty pool, so Clips are silent until Sam finds all of this.

### The new model

**What Sam sees: a list of tracks, each listing the sounds it records.** There's no pool. The page's devices are simply every source on any track.

```
Audio Tracks                                         [Use a preset ▾]
Every Clip and Recording gets these tracks, in this order. Discord and browsers
play track 1 only; editors like DaVinci Resolve show each one.

┌ 1  Everything                       ▮▮▮▮▮▮▯▯▯▯   [⋯] ┐
│    🔊 Speakers (Realtek)   playback   ▮▮▮▮▯▯   [⋯]  │
│    🎙 Microphone (Yeti)    microphone ▮▮▯▯▯▯   [⋯]  │
│    [+ Add sound]                                     │
│    [✓] Mic mute key silences the microphone on this track │
└──────────────────────────────────────────────────────┘
┌ 2  Game and chat                    ▮▮▮▮▯▯▯▯▯▯   [⋯] ┐
│    🔊 Speakers (Realtek)   playback   ▮▮▮▮▯▯   [⋯]  │
│    [+ Add sound]                                     │
└──────────────────────────────────────────────────────┘
[+ Add a track]
```

**Track card.** One per Track, in file order.

- **Header:** the track number, the name (click or F2 to edit inline; Enter or leaving commits, Esc cancels), the Track's live level meter, and a ⋯ menu with Rename, Move up, Move down and Remove track. Remove asks first if the Track has sounds.
- **Summary under the name:** "{n} sounds", or "No sounds — this track will be silent" in `{colors.caution}`.
- **Source rows:** one per device on the Track, showing:
  - a glyph for playback (🔊) or microphone (🎙);
  - the name as Windows shows it, with the parent device in brackets only when two share a name (today's rule);
  - the kind in caption: "playback" or "microphone";
  - its own live meter;
  - "Not connected — records silence until it's back" in `{colors.danger}` when absent;
  - a ⋯ menu with **Remove from this track** and, for a recording device, a **Treat as a microphone** check.
- **+ Add sound** opens the [sound picker](#the-sound-picker) for this Track.
- **The mute switch** "Mic mute key silences the microphone on this track" shows only when the Track has a microphone source. It's the Track's `muteAffected`.
- **The double-count warning,** reworded: "You may hear some sounds twice: A and B can both carry the same audio." It shows in `{colors.caution}` on the card, and never blocks Save (R15).

**+ Add a track** adds "Track {n}" at the end, empty, with its name in edit mode.

**Track 1** is the first card, normally "Everything". New layouts start with it first (R15). It can be renamed, moved and removed like any track. Removing the last track leaves "No tracks — Clips will have video only", with + Add a track.

**How the UI maps to the engine's model,** which doesn't change: `trackLayout = { devices, tracks }` (AD-4).

- **Adding a source to a Track** reuses the layout's existing `LayoutDevice` for that endpoint and flow, or adds one with a new key. It then appends the key to the Track's `deviceKeys`.
- **Removing a source from its last Track** drops the `LayoutDevice` on save, since a device on no Track records nothing. [DECISION]
- **A device is one record across Tracks,** so "Treat as a microphone" changes it on every Track it's on. The menu says so in its tooltip: "Applies on every track with this sound."
- **New sources' `isMic`:** a recording device added through the picker gets `isMic = true`; a playback device gets `false` (R18's Mic Devices). [DECISION] Most recording devices on a gamer's PC are microphones, and the menu check covers line-ins.
- **Order:** the card order is the `tracks` order; the `devices` order follows first use.
- **Track ids** are kept across renames and moves (today's rule).
- **Per-game tracks:** the same editor, in a game's Audio Tracks section on its page in Games, under the [inherited-or-changed](#inherited-or-changed) pattern. "Change for this game" copies the main tracks first, keeping their ids (AD-4). This replaces the "Use Global Track Layout" check box, with the same behaviour. The editor offers a read-only summary mode for the "Uses the main setting" state.

### The sound picker

A flyout anchored to the card's + Add sound button. It holds a search box at the top, then two sections:

- **Sounds from your PC (playback):** every listed Output Device.
- **Microphones and other inputs:** every listed Input Device.

Each row shows the name (with the parent when it's shared), a **Windows default** caption on the default device of its section, and its **live meter**. Devices already on this Track are listed ticked and disabled. A not-connected device that the layout already holds shows its note. Clicking a row adds it and closes the flyout. Ctrl+click adds it and keeps the flyout open, to add several.

- **Keyboard:** ↑ and ↓ move through the rows, Enter adds, Esc closes, and typing goes to the search box.
- **The list updates live** as devices come and go (R14), without losing the highlighted row.

### Presets

The page header's **Use a preset ▾** menu holds two presets. Each one replaces the draft after its confirmation, and like any edit it isn't saved until Save changes.

| Preset | Tracks it builds |
|---|---|
| **Simple** | 1 "Everything": the Windows default playback device and the default microphone. Mute on. |
| **For editing** | 1 "Everything": default playback and default microphone, mute on. 2 "Game and chat": default playback. 3 "Microphone": default microphone, mute on. |

- **The default devices** come from an additive `isDefault` flag on each `AudioDevice` in `devices.changed` (AD-22).
- **No default device in a section:** that source is left out, and the card shows its silent summary.
- **Keeping Track ids:** a preset keeps the existing Track 1 id when Track 1 is replaced, so a per-game layout copied from it stays recognisable. Other tracks get new ids.

### First run just works

- **The seed:** when `settings.json` has no Global `trackLayout` (a first run), the engine writes the **Simple** preset from the Windows default playback and recording devices, once, through its own settings queue (AD-4). The key's catalog default is unchanged, and an existing install is untouched.
- **Result:** Sam's first Clip has their game and their voice on one track, and the Audio Tracks page shows that before Sam ever opens it.
- **With no default device:** a PC with no microphone gets playback only, and one with no devices at all gets today's empty "Everything", with the silent summary on the card and on the welcome card.

### Live meters

- **Each Track header** shows its level from `meters.levels`, as today (R30).
- **Each source row and picker row** shows that device's own level.
- **How:** the App adds one hidden probe Track per device it wants a level for to the layout it sends with `meters.watch`: the card's sources, plus every listed device while the picker is open. It reads each device's level from its probe Track's entry in `meters.levels`.
  - Probe Track ids are derived from the device key and flow, so they're stable across watches.
  - Probes never reach the saved layout.
  - The engine already holds one client per device (AD-20), so a probe adds a mixer input, not a device.
- **Fallback:** if story 10.4 measures the probe cost as too high with the picker's every-device watch, it adds an optional per-device level list to `meters.levels` instead, additive under AD-22, and records why.
- **Meters run only while the page is on screen,** as today, and the picker's probes only while it's open.
- **Scale:** the existing −60 to 0 dBFS bar. A device with no sound shows an empty bar, never a "not working" message.

### Saving

- **The draft model stays.** Edits change a draft, and Save changes sends one `settings.update`. A layout applies from the next Reset (R17), so live-applying every tick would restart nothing anyway and only add revision churn.
- **The save bar:** while the draft differs from the saved tracks, a **sticky bar at the top of the page content** reads "Unsaved changes", with **Save changes** (the accent button) and **Discard**, plus the applies hint. The nav item shows a dot. With nothing unsaved, the bar is gone.
- **Leaving the page** keeps the draft, as today. The page object lives for the run, and the nav dot reminds Sam. There's no leave prompt.
- **Ctrl+S saves** while the page has focus.
- **Validation:**
  - "Give every track a name to save." shows on the bar, and the empty name box is outlined in `{colors.danger}`.
  - A refused update shows in the page's error bar, as today.

## Welcome and the empty Library

- **The Library with no items** (and no filter or search) shows a **welcome card** instead of the one line. It reads:
  - "Play a game, then press **{Save Hotkey}** to save the last {Clip length} as a Clip", from the live settings;
  - "Audio: {track 1's sources, by name} · **Change**", which goes to Audio Tracks;
  - "Clips are saved in {Clips folder} · **Change**", which goes to Storage and Cleanup;
  - "Want the whole match? Allow Recordings for the game on its page in Games, then press {Record Hotkey}.", in caption.
- **When track 1 has no sources,** the audio line reads "Audio: none yet — your Clips will be silent · **Set up audio**", in `{colors.caution}`.
- **It goes away** as soon as the Library has an item. There's no "don't show again", since it only shows on an empty Library.
- **The Main App opens on the Library** at first start, as today, so this is the first thing Sam sees.

## Clips and Recordings, explained

Sam meets three kinds of file. Each one gets one plain sentence, the same one, wherever Sam meets it.

| Kind | The sentence |
|---|---|
| **Clip** | "The last {Clip length} of your game, saved when you press {Save Hotkey}. ReplayBuffer keeps that much ready all the time, so you never miss a moment." |
| **Recording** | "One long video of your game that you start and stop yourself with {Record Hotkey}, alongside Clips. Allow it per game, on the game's page." |
| **Monitor Recording** | "A Recording of a whole screen instead of a game, for anything that isn't detected as a game. Start and stop it with {Monitor Recording Hotkey}." |

**Where they appear:**

- the Hotkeys page's section help;
- the Clips page's intro (the Clip sentence);
- each game's Recordings section (the Recording sentence);
- the welcome card, which adds "Want the whole match? Allow Recordings for the game on its page in Games, then press {Record Hotkey}.";
- the Library row badge.

**The Library row badge:** a Recording or Monitor Recording row shows a "Recording" or "Monitor Recording" badge beside its length; a Clip shows none.

- The engine knows each file's kind when it starts writing it, so `LibraryItem` gains an optional `kind` (`clip` | `recording` | `monitorRecording`), additive under AD-22 and kept in `library.json`.
- A file found by the folder scan has no kind and shows no badge, except a file in the Monitor Recordings folder, which shows its badge. [DECISION] The scan can't tell a Clip from a Recording, and a wrong badge is worse than none.

## Settings pages

Every settings page keeps epic 9's shared layout: section header, card, rows, help text, and the error bar. The changes per page:

### Clips (was Replay Buffer)

```
Clips
ReplayBuffer keeps the last part of your game ready, so {Save Hotkey} can save it as a Clip.

CLIP LENGTH
  Save the last          [ 30 ] seconds
  Time between saves     [  2 ] seconds     A press this soon after a save is ignored.

WHERE RECENT GAMEPLAY IS KEPT
  (•) Memory   Fastest. Uses about 1.1 GB of RAM.
  ( ) Disk     Uses about 1.1 GB in the cache folder instead of RAM.
  Cache folder  [ C:\Users\sam\AppData\Local\ReplayBuffer\Cache ] [Browse…] [Open in Explorer] [Use default]
                In Disk mode, the last 30 s of gameplay is kept here in one file. It doesn't grow.
  ⚠ (the estimate warning, as today, reworded)

VIDEO QUALITY  — for Clips and Recordings
  Format        [ H.264 (plays everywhere) ▾ ]
  Resolution    [ 1920 × 1080 ▾ ]
  Frame rate    [ 60 ] fps (up to 240)
  Bitrate       [ 20000 ] kbps     Higher looks better and makes bigger files.
  [✓] Variable bitrate (smaller files in quiet scenes)
```

- **Same keys, same validation,** sent the same way: `bufferWindow`, `saveCooldown`, `replayStorage`, `ringPath` and `video`.
- **The Memory and Disk lines** carry today's estimate figures, split per option.
- **The cache folder** is a [folder picker](#folder-picker). It's enabled only while Disk is chosen, and under Memory it shows "Used only in Disk mode" in caption.
- **The estimate's note** about a game's own settings reads "A game's own Clip length or Video quality changes this; see Games."

### Hotkeys

The rows are grouped by what they do, each section with its sentence from [the explanations](#clips-and-recordings-explained):

| Section | Rows |
|---|---|
| **Clips** | Save Clip |
| **Recordings** | Record mode ("One key starts and stops" / "Separate start and stop keys"), then Record, or Record start and Record stop |
| **Monitor Recording** | Monitor Recording (the key), then **Screen to record** [monitor ▾] [Refresh], moved here from the removed Recordings page with its behaviour unchanged |
| **Microphone** | Mic mute, with "Silences your microphone on the tracks you choose in Audio Tracks." |

- Key capture, the duplicate refusal, and the failed-registration and status texts keep their behaviour.
- The status texts follow the glossary.

### Storage and Cleanup (was Library and Cleanup)

- **The Clips folder** is a [folder picker](#folder-picker) with "Use default". Its help: "Where Clips and Recordings are saved. Each game gets its own folder, and Monitor Recordings have theirs." The folder's size line stays.
- **Cleanup** stays as it is, with its permanent-deletion confirmation and its two notes.

### General and Sounds

- **General:** unchanged apart from the glossary. The Overlay's dot row gets the help "The small on-screen indicator that shows ReplayBuffer is capturing."
- **Sounds (was Feedback Sounds):** unchanged apart from its title.

### Recordings (removed)

- The page and its nav item go.
- The `RecordingsSettingsPage` view model's monitor list and Refresh move into the Hotkeys page.
- The `monitorRecordingMonitor` key is unchanged.

## Folder picker

One reusable control for every folder setting, in the shared settings layout, so no path is ever a bare text box (#6).

```
Cache folder   [ C:\Users\sam\AppData\Local\ReplayBuffer\Cache          ]  [Browse…]  [Open in Explorer]  [Use default]
               help text
               212 GB free on C:
               (a danger line when the last choice was refused)
```

- **The path:** shown read-only, as selectable text that can be copied, trimmed with a middle ellipsis, with the full path as its tooltip. It reads "Default ({path})" when the key is unset.
- **Browse…** opens the Windows folder dialog (`OpenFolderDialog`) at the current folder. Choosing a folder sends the key's `settings.update` at once, as today's Output Folder does.
  - Cancelling sends nothing.
  - A refusal shows on the control's danger line, in the glossary's words: `ring-path-invalid` reads "The cache folder can't be inside the Clips folder."; an unwritable folder reads "ReplayBuffer can't write to that folder."
- **Open in Explorer** opens the folder through the shell (AD-1). It's disabled, with a tooltip, while the folder doesn't exist.
- **Use default** shows only where the key has a default (both `ringPath` and `outputFolder` are nullable), and only when a path is set. It sends `remove` for the key, as today's Output Folder does.
- **The free-space caption** comes from the drive of the shown folder, refreshed when the page opens.
- **Keyboard:** the path text, Browse, Open and Use default are tab stops in that order.
- **Used by:** the cache folder (Clips page) and the Clips folder (Storage and Cleanup).
- **A file variant** (Browse opens a file dialog) is used by Games → Add a game…, filtered to programs (`*.exe`).

## Drill-in pages

A navigation pattern any page can use to open one item's page from a list. Games uses it first, and a future Library item page can reuse it unchanged.

```
← Games › Valorant                                   (page header: Back, the parent link, the item's name)
```

- **Where it opens:** the item's page replaces the page content in the content column. The nav selection stays on the parent's item (Games), and the page header becomes a breadcrumb: a **Back** button (← glyph, named "Back to {parent}", tooltip "Back (Esc)"), the parent's title as a link, and the item's name.
- **Opening:** click a row, or Enter on a focused row.
- **Closing:** the Back button, the parent link, Esc, Alt+←, the mouse's Back button, or selecting the parent's nav item again. Esc first cancels what's in progress, such as a text edit or an open flyout or menu, as each control does today.
- **Focus:**
  - Opening puts focus on the item page's first control.
  - Going back puts focus on the row it came from, with the list's scroll position restored.
  - Ctrl+Tab from an item page goes to the next nav item. Coming back to the parent shows its list.
- **State:** the parent keeps each item page's view model for the run, keyed by the item's id.
  - A draft on an item page survives going back, as Audio Tracks' draft survives leaving the page.
  - The list row shows the same unsaved dot as the nav item.
  - There's no leave prompt.
- **An item that goes away** while its page is open (removed from the settings document) returns to the list, with "{name} is no longer in the list." in the page's error bar.
- **UI Automation:** the breadcrumb is a navigation landmark, and the item page's name is the window region's name.

## Games

The Game Profiles page, rebuilt as two levels with the [drill-in pattern](#drill-in-pages). R23 to R26 hold unchanged: detection, the Target rule, automatic profiles inheriting the main settings, Ignored, and per-game folders.

### The games list

```
Games                                                              [Add a game…]
Games are found when you play them. Each one uses the main settings unless you change them on its page.

[icon] Apex Legends        Detected automatically · 2 settings changed            ›
[icon] Indie Game          Added by hand                                          ›
[icon] Valorant            Detected automatically · Capturing now   •             ›
IGNORED GAMES
[icon] Steam               Not captured                                           ›
```

- **The list:** one single-selection, keyboard-navigable list sorted by name, with letter-key jump. It has two groups: the games, then **Ignored games** at the bottom.
- **Each row shows:**
  - the program's icon, read from the program file through the shell, else a generic game glyph;
  - the name;
  - a caption with "Detected automatically" or "Added by hand" (whether the program is in `manualGames`);
  - a **Capturing now** pill while it's the game being captured (from the snapshot);
  - "{n} settings changed" when it has its own Clip length, Video quality or Audio Tracks;
  - "Not captured" when Ignored;
  - the unsaved dot;
  - a › chevron.
- **No profile id or path** appears in the list.
- **Add a game…** at the top right opens the file variant of the [folder picker](#folder-picker)'s dialog, filtered to programs. On a pick it sends `game.add`, and the page says "Added. {name} appears here the next time it runs." A refusal shows today's text, reworded by the glossary, in the error bar.
- **Empty list:** "No games yet. Play a game and it appears here, or add one by hand."

### A game's page

```
← Games › Valorant
[icon] Valorant                                          (click or F2 to rename)
       Detected automatically · Program: C:\Riot Games\VALORANT\live\VALORANT.exe  [Show in Explorer]

CAPTURE
  [■ on ] Capture this game
          When off, ReplayBuffer won't capture it or save Clips while it runs.

CLIPS                                         [ Uses the main setting ]
  Save the last 30 seconds                    [Change for this game]   Edit the main setting

RECORDINGS
  [□ off] Allow Recordings in this game
          One long video of your game that you start and stop yourself with Alt+F9, alongside Clips.

VIDEO QUALITY                                 [ Changed for this game ]
  Format [ HEVC ▾ ]  Resolution [ 2560 × 1440 ▾ ]  Frame rate [ 120 ]  Bitrate [ 40000 ]  …
                                              [Reset to the main setting]

AUDIO TRACKS                                  [ Uses the main setting ]
  1 Everything: Speakers, Microphone · 2 Microphone: Microphone
                                              [Change for this game]   Edit the main setting

FILES
  Saved in   C:\Users\sam\Videos\ReplayBuffer\Valorant          [Open in Explorer]
  File names look like  Valorant_2026-11-04_21-03-11.mp4
  Renaming the game changes new file names only; the folder stays.

ADDED BY HAND   (only for a hand-added game)
  [Stop detecting it by hand]   It's still found if it runs fullscreen or Windows marks it as a game.
```

- **The header:**
  - The name is renamed inline: click it or press F2; Enter or leaving commits, Esc cancels, and a blank name shows the old one again (today's rule).
  - The program's location is shown here, and only here, with Show in Explorer.
- **Capture** is the `ignored` attribute, inverted. Turning it off asks nothing, and the switch says what it does (R25).
- **Recordings** is the `recordingsEnabled` attribute. It's a game attribute with no main setting, so it has no inheritance chip.
- **Files** is read-only. The folder is the profile's `folder`, assigned once by the engine (AD-21), and the file-name example follows R26's pattern from the game's current name.
- **Added by hand** removes the program from `manualGames` with one `settings.update`. The game's settings stay.
- **Applies hint:** each section that changes capture reads "Changes apply from this game's next session, or right after its next saved Clip." under its header.

### Inherited or changed

Clip length, Video quality and Audio Tracks are the three settings a game can change (AD-4's overridable keys). Each section shows its state the same way, replacing today's "Use Global …" check boxes with the same settings operations.

| State | Chip | Body | Buttons |
|---|---|---|---|
| Uses the main setting | "Uses the main setting", neutral | The main value as **readable text**, never as disabled controls: "Save the last 30 seconds"; "H.264 · 1920 × 1080 · 60 fps · 20,000 kbps"; "1 Everything: Speakers, Microphone · …" | **Change for this game**, which copies the main value into this game's own and opens the editor (AD-4 keeps the Track ids); **Edit the main setting**, a link to that settings page |
| Changed for this game | "Changed for this game", accent | The full editor: the Clips page's Clip length row, its Video quality rows, or the [Audio Tracks editor](#audio-tracks) with its own save bar | **Reset to the main setting**, which asks "Reset {game}'s {section} to the main setting?", then removes the game's own value |

- **When the main setting changes,** a game that uses it shows the new value at once.
- **A game with its own value** keeps it, and the chip stays "Changed for this game" even when the values happen to match.

## Tray menu

From the top:

1. **The status**, a disabled item with the status header's text, such as "Ready to save Clips in Valorant".
2. a separator
3. **Open**, as today.
4. **Open Library**, which opens the window on the Library page.
5. **Open the Clips folder**, which opens it in Explorer.
6. a separator
7. **Start with Windows**, as today.
8. **Restart**, shown only in a crash loop, as today.
9. **Quit**, as today.

The dev-only Control gallery item stays where story 9.7 put it. The status item updates live while the menu is open.

## Status header

- **The state texts follow the glossary** (story 10.11): "Ready to save Clips in Valorant", "Finishing the save…", "Waiting for a game", and "ReplayBuffer's background service" for the engine states. The Recording, Monitor Recording and Mic muted parts stay.
- **While capturing,** the state line adds the hint "· {Save Hotkey} saves the last {Clip length}", for example "Ready to save Clips in Valorant · Alt+F10 saves the last 30 s", in `{colors.text-secondary}` (story 10.6).
- **The hint hides** when a Recording runs (the line is long then) and when the Save Hotkey failed to register. The Hotkeys page already shows that failure, and the header's warning list carries it.

## Component Patterns

These are behavioural rules. The visuals are in this folder's [DESIGN.md](DESIGN.md).

| Component | Rules |
|---|---|
| Library list | Extended selection with Explorer's rules ([Selection model](#selection-model)). It stays virtualized and grouped with recycling, and the selection lives in the view model, not in containers. |
| Library command bar | A fixed height. Summary when nothing is selected, actions when something is. Actions are hidden (not disabled) when they don't apply to the count; Delete is disabled, with a reason, when nothing selected is deletable. |
| Search box | Filters as you type, applied at most every 150 ms. ✕ clears. Esc clears while it's focused. |
| Sort combo box | Six choices; it persists. |
| Track card | A card per Track: inline-renamable header, meter, ⋯ menu, source rows, + Add sound, mute switch when a mic is present, warning. Keyboard: Tab reaches the name, the menu, each source's menu, + Add sound and the switch, in that order. |
| Source row | Name, kind, meter, a note when not connected, ⋯ menu. |
| Sound picker | A flyout: search, two sections, rows with meters and default captions. Click adds; Ctrl+click adds and stays open. |
| Save bar | Sticky at the top of the page's content while the draft is dirty. Save changes is the default (Ctrl+S); Discard asks nothing, because it is the same as today's Revert. |
| Welcome card | Shown only on an empty, unfiltered Library. Its links go to pages and select them in the nav. |
| Tray status item | Disabled, live, the first item. |
| Folder picker | Read-only path, Browse…, Open in Explorer, Use default where the key has one, free space, and the refusal line ([Folder picker](#folder-picker)). The only control for a path. |
| Drill-in page | Breadcrumb header with Back. Opens on click or Enter; closes on Back, Esc, Alt+← or the mouse's Back button; focus returns to the row ([Drill-in pages](#drill-in-pages)). |
| Games list | Single selection, two groups (games, Ignored games), letter jump, the icon, captions and pills, and the unsaved dot. |
| Inheritance section | A chip (main or changed), a readable summary or the editor, and Change for this game, Reset to the main setting (confirmed) or Edit the main setting ([Inherited or changed](#inherited-or-changed)). |
| Hotkeys section | A section header, its sentence from [the explanations](#clips-and-recordings-explained), and its rows; Monitor Recording holds the screen picker. |
| Library kind badge | "Recording" or "Monitor Recording" beside the length; none on a Clip or an unknown file. |

## State Patterns

| State | Surface | Treatment |
|---|---|---|
| Nothing selected | Library | Summary and how-to caption in the command bar |
| Some selected, some hidden by filters | Library | "{n} selected ({m} hidden by filters)". Actions act on the shown ones. |
| Bulk action running | Library | "Deleting {k} of {n}…" past 10 items; actions disabled until done |
| Bulk action partly failed | Library | One summary line in the action-error place |
| All selected are Favourites or being written | Library | Delete disabled; its tooltip says why |
| Search matches nothing | Library | "No items match “{text}”." |
| Empty Library | Library | The welcome card |
| A Track with no sounds | Audio Tracks | "No sounds — this track will be silent" in caution |
| No tracks | Audio Tracks | "No tracks — Clips will have video only" with + Add a track |
| A source not connected | Audio Tracks | "Not connected — records silence until it's back" in danger; it stays on the Track (R14) |
| Possible double counting | Audio Tracks | The reworded caution line on the card |
| Draft differs from saved | Audio Tracks | The save bar and the nav dot |
| Changed elsewhere while dirty | Audio Tracks | Reloads, with the reworded message in the error bar (today's rule) |
| A game using a main setting | Games → a game | "Uses the main setting" chip and a readable summary of the main value |
| A game with its own setting | Games → a game | "Changed for this game" chip, the editor, and Reset to the main setting |
| A game's page with unsaved audio tracks | Games list | The unsaved dot on its row and on the nav item |
| A game removed while its page is open | Games | Back to the list, with "{name} is no longer in the list." |
| No games | Games | "No games yet. Play a game and it appears here, or add one by hand." |
| Cache folder under Memory | Clips | The folder picker disabled, with "Used only in Disk mode" |
| A folder choice refused | Folder picker | The glossary's refusal text on the control's danger line; the shown path is unchanged |
| A folder that doesn't exist | Folder picker | Open in Explorer disabled, with a tooltip |
| No default device for a preset | Audio Tracks | That source is left out; the card shows its silent summary |

## Interaction Primitives

These add to epic 9's keyboard rules.

- **Library:** the [selection table](#selection-model) and the [shortcut table](#keyboard-shortcuts). Focus is visible on the focused row, separately from its selection.
- **Audio Tracks:**
  - Tab moves through each card in reading order.
  - F2 on a focused card renames it.
  - Alt+↑ and Alt+↓ on a focused card move it.
  - Delete on a focused source row removes it from the Track.
  - Ctrl+S saves.
  - The picker is [above](#the-sound-picker).
- **Games:** ↑ and ↓ move through the list, letters jump, and Enter opens a game's page. On a game's page, F2 renames, and Esc, Alt+← or the mouse's Back button go back ([Drill-in pages](#drill-in-pages)).
- **Folder picker:** the path, Browse, Open and Use default are tab stops; Enter or Space on Browse opens the Windows dialog.
- **Mouse:** no drag-and-drop reorder or drag-to-add. The menus and Alt+arrows cover them. [DECISION] Drag in a virtualized grouped list and between cards costs more than it gives a personal app.

## Accessibility Floor

- **Selected and focused** are both visible and look different: the selection is the `{colors.accent-subtle}` fill with the accent bar; focus is epic 9's focus visual. Neither is colour alone, since the bar and the outline are shapes.
- **UI Automation:**
  - Each Library row exposes its name and its selection state (`SelectionItemPattern`).
  - The command bar's count is a live region, read when it changes.
  - Each meter exposes its level as a range value.
- **The double-count warning, not-connected notes and silent-track summaries** carry words, never only a colour or glyph.
- **No new timing or motion.**

## Key Flows

### Flow 1 — Clearing out a week of Clips (Library, R38)

1. Sam opens the Library after a week of ranked games: 140 Clips, grouped by game.
2. They choose Sort → Largest first, then Group by → None. The biggest files are at the top.
3. They click the first row, then Shift+click the twentieth. The bar reads "20 selected", with the actions.
4. Two of them are Favourites, and one is still being saved after the last match.
5. They press Delete. The dialog says "Move 17 items (3.8 GB) to the Recycle Bin? 2 Favourites and 1 file being saved are kept."
6. **Climax:** they press Yes, and seventeen rows leave the list with one confirmation. The two Favourites and the saving Clip stay, still selected, with the bar reading "3 selected".
7. **Failure path:** one file was locked by a player. The action line reads "Deleted 16 of 17. 1 file couldn't be deleted." It stays selected.

### Flow 2 — Sending three clutch moments to the squad (Library)

1. Sam types "clutch" in the search box. Five Clips match.
2. They Ctrl+click three of them and press Ctrl+C.
3. **Climax:** in Discord they paste once, and three attachments appear.
4. They clear the search. The three are still selected among the other 140, so they press Space on each to star it.

### Flow 3 — First install, first Clip (first run, Audio Tracks)

1. Sam installs and starts ReplayBuffer. The Library opens on the welcome card: "Play a game, then press Alt+F10 to save the last 30 s." It also reads "Audio: Speakers (Realtek), Microphone (Yeti) · Change".
2. They play a match and press Alt+F10. The Overlay card says the Clip is saved.
3. **Climax:** the Clip plays with the game and Sam's voice, and Sam never opened a setting.

### Flow 4 — Splitting voice from game for editing (Audio Tracks)

1. Sam wants their voice on its own track for a montage, and opens Audio Tracks.
2. One card shows "1 Everything" with Speakers and Microphone, both meters moving as they talk.
3. They choose Use a preset → For editing and confirm. Three cards appear: Everything, Game and chat, and Microphone. The save bar shows.
4. On "Game and chat", they press + Add sound. The picker lists Discord's BEACN "Chat" output with its meter bouncing while a friend talks, and Sam clicks it.
5. **Climax:** they press Save changes. The bar goes, and the next Clip opens in DaVinci Resolve with three named tracks.
6. **Failure path:** Sam clears a track's name. The bar says "Give every track a name to save.", and the box is outlined.

### Flow 5 — The builder's BEACN setup survives (Audio Tracks, R35)

1. The builder opens Audio Tracks on their existing install.
2. Their own layout is shown as cards, nothing re-seeded, with each BEACN endpoint as a named source with its parent in brackets.
3. **Climax:** a BEACN restart greys one source to "Not connected" and back within 2 s, and the Track keeps it (R19).

### Flow 6 — Longer Clips for one game (Games)

1. Sam wants 60-second Clips in Apex only. They open Games; Apex Legends reads "Detected automatically".
2. They press Enter on it. Its page opens with "← Games › Apex Legends", and focus is on the name.
3. Clips reads "Uses the main setting · Save the last 30 seconds". They press Change for this game, and the row becomes editable at 30. They type 60.
4. **Climax:** the chip turns to "Changed for this game". Back in the list, Apex's row reads "1 setting changed", and the next Apex Clip is a minute long.
5. Later they press Reset to the main setting and confirm. The chip goes back, and so do 30-second Clips.

### Flow 7 — Moving the cache to the big drive (Clips page)

1. Sam's C: drive is small. On Clips they choose Disk; the cache folder picker becomes enabled and shows "Default (C:\…\Cache) · 12 GB free on C:".
2. They press Browse…, pick D:\ReplayBuffer Cache in the Windows dialog, and the path and "1.8 TB free on D:" appear.
3. **Climax:** the line under it says what the folder is for in one sentence. Sam never sees the word "ring".
4. **Failure path:** they pick a folder inside their Clips folder. The control reads "The cache folder can't be inside the Clips folder." and keeps the old path.

## Build Map

| Story | Builds |
|---|---|
| 10.1 — Design pass | These spines, the renders, and the builder's approval (Decision lines in the epic's Notes) |
| 10.2 — Library selection, bulk actions and shortcuts | The selectable list, the selection set and its survival rules, the command bar and summary, bulk Favourite, Copy, Copy to folder, Show in Explorer and Delete, the context menu on the selection, and the keyboard shortcuts |
| 10.3 — Library sort and search | The Sort combo box, Group by None, the search box and its empty message, and `LibraryViewState`'s new fields |
| 10.4 — Audio Tracks: defaults, presets and per-device meters | `AudioDevice.isDefault`, the engine's first-run seed, the preset builder, and the probe-Track meters (or the fallback), as view-model logic the editor binds to |
| 10.5 — Audio Tracks editor | The Track cards, source rows, sound picker, save bar and nav dot, the Audio Tracks wording, and the editor's read-only summary mode for a game that uses the main tracks |
| 10.9 — Settings reorganised: the folder picker, the Clips, Hotkeys and Storage pages, Recordings removed | The folder picker control and its file variant; the Clips page; Hotkeys grouped, with the screen picker; the Recordings page removed; Storage and Cleanup; Sounds; the nav order; the glossary on those pages |
| 10.10 — Games list and a page per game | The drill-in pattern; the games list with icons, captions and groups; a game's page with its sections; the inheritance sections with Change, Reset and Edit the main setting; Add a game… with the file picker; Stop detecting it by hand |
| 10.11 — Plain words everywhere | The glossary on General, Sounds, the status texts, the engine's warning texts, refusals, dialogs, the tray tooltip and the Overlay, and the XAML glossary test |
| 10.6 — Welcome card, Library kind badge, tray menu and status hint | The welcome card with the Clips and Recordings lines, `LibraryItem.kind` and its badge, the tray items, and the status hint |

## Inspiration & Anti-patterns

- **Lifted from Windows Explorer:** the selection keys, right-click-selects, Delete and F2, and Show in Explorer selecting the files.
- **Lifted from OBS's Audio Mixer and Windows' Sound settings:** device names as Windows shows them, with a live meter next to each.
- **Lifted from ShadowPlay's "separate both tracks":** a preset, not a page of concepts.
- **Rejected — a device pool separate from the Tracks:** it's the engine's shape, not the gamer's.
- **Rejected — an advanced view:** everything the engine needs fits in the card and source menus.
- **Rejected — auto-saving every audio edit:** it applies at the next Reset anyway, and a half-built layout would be saved.
- **Rejected — a "follow the Windows default device" source:** it conflicts with R35's "nothing to re-select" and AD-4's "rebinding is never written back". Presets and the seed pick the default at that moment instead.
- **Lifted from Windows Settings:** a list that drills into an item's page with a breadcrumb and Back.
- **Rejected — keeping the Recordings page:** one picker and a note don't make a page; the picker belongs beside its key.
- **Rejected — an editable path box:** a typo there is a silent failure. The Windows folder dialog can only pick a real folder.
- **Rejected — changing a game's folder:** the engine assigns it once (AD-21), so its page shows it read-only.
- **Rejected — checkboxes on every Library row:** they're redundant with Ctrl and Shift, and crowd the row. The ✕ and Esc clear the selection.

## Open questions

- **For the builder at 10.1:** should the tray menu get "Save Clip now"? It needs a new engine command (`capture.save`, additive), and touches PRD Q-2 and the "one Save Hotkey" boundary. It's out of epic 10 unless approved; if approved, it joins story 10.6.
- **For the builder at 10.1:** confirm Favourites are protected from Delete, single and many (a change to 7.7).
- **For the builder at 10.1:** confirm the page names, Clips (was Replay Buffer), Audio Tracks, Games, Sounds, and Storage and Cleanup, and the Recordings page's removal.
- **For the builder at 10.1:** confirm the glossary's labels, "Cache folder" and "Clip length" above all.
