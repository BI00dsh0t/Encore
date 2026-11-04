---
name: ReplayBuffer Modern UI
status: final
updated: 2026-11-04
sources:
  - ../epic-modern-ui.md
  - ../../../architecture-replay-buffer/architecture-replay-buffer.md
  - ../../../research/ui-look-mockups.html
---

# ReplayBuffer Modern UI — Experience Spine

## Foundation

- **Form factor:** a single-surface Windows desktop app, in .NET 10 WPF.
- **Surfaces:**
  - the Main App window;
  - the tray icon's context menu (H.NotifyIcon's WPF `ContextMenu`);
  - the App's dialogs;
  - the Overlay, a topmost, click-through, never-activated window over the game.
- **UI system:** WPF's built-in Fluent theme (`ThemeMode`). Fluent's control behaviour is the baseline for both themes, and this spine specifies only what the App adds or changes.
- **Visual identity:** [DESIGN.md](DESIGN.md). Token names below (`{colors.accent}`) are DESIGN.md's.
- **The restyle changes no behaviour (R6).** Every page keeps its settings, bindings, commands, wording, validation and messages. Where this spine moves something, the move is layout only. The one new control is the theme picker.

The renders in [`renders/`](renders/) show the shell, the Library and a settings page in each theme, plus the dialog, tray menu and Overlay card. Spines win on conflict with them, the mockups or the prototype.

## Information Architecture

| Surface | Reached from | Holds |
|---|---|---|
| Main App window | Tray left-click, tray **Open**, start (unless started with Windows) | Shell: nav pane, status header, the selected page |
| Library | Nav item **Library** (the default page) | The Library list, its filters and actions |
| General | Nav, under the Settings label | Start with Windows, the Overlay's dot, **Theme** (new) |
| Replay Buffer | Nav | Buffer Window, storage, Ring folder, Save cooldown, estimate, Output |
| Hotkeys | Nav | Record mode and every Hotkey row |
| Track Layout | Nav | Devices, Tracks, meters, Save and Revert |
| Game Profiles | Nav | Games, Add a game by hand, Overrides for a game |
| Feedback Sounds | Nav | Sound device and each sound's switch and Play |
| Recordings | Nav | The Monitor Recording monitor and Refresh |
| Library and Cleanup | Nav | Output Folder, Cleanup limits |
| Tray menu | Right-click the tray icon | Restart (only in a crash loop), Open, Start with Windows, Quit |
| Dialog | A Library delete, a Cleanup limit confirmation, any other `Prompts` call | One question with its buttons |
| Overlay | Shown by the engine's state over the game | Capture dot, saving icon, mic-muted icon, red X, and the message card |
| Control gallery | Tray item **Control gallery**, dev instances only (story 2) | Every control in every state, for the builder's check |

### Navigation

[DECISION] One flat nav pane replaces today's two levels: the Library and Settings tabs, then the Settings list. The nav pane lists, in order:

1. **Library**
2. a non-selectable **Settings** section label
3. the eight settings pages in `SettingsPages.Create`'s order, under their existing titles: General, Replay Buffer, Hotkeys, Track Layout, Game Profiles, Feedback Sounds, Recordings, Library and Cleanup.

Every existing page is one click away, and none is renamed.

- **The mockups' labels aren't used.** "Game profiles", "Audio tracks", "Settings" as a page, and a separate "Recordings" Library view would rename pages or invent new ones. The epic's Boundaries rule both out.
- **No icons.** Neither the App nor the Gaming render has any, and Segoe Fluent Icons glyphs would be new art for no behaviour. [DECISION]
- **Brand:** the window's name sits above the nav list in `{typography.brand}`. It's "ReplayBuffer", with the instance suffix on dev instances as the title already shows. It's static text, not a control.

**The view model.** Story 3 has the shell bind one list, Library plus `Pages`, so every existing test of `SelectedPage` keeps working.

- Choosing a settings page sets `SelectedPage` and `IsSettingsTabSelected = true`.
- Choosing Library sets `IsSettingsTabSelected = false`.
- Any code path that sets `IsSettingsTabSelected` or `SelectedPage` today, the tray's included, selects the matching nav item.
- The Track Layout and Game Profiles pages still meter only while they're the page on screen.

### Shell layout

```
┌ title bar (system; Mica under Fluent, bg-base caption under Gaming) ───────────┐
│ BRAND              │ ┌ status header (card) ──────────────────────────────────┐ │
│                    │ │ ● Buffering on Valorant · Recording                     │ │
│ Library            │ │   12:04  Warning  Mic not found…            [Dismiss]   │ │
│ SETTINGS           │ └─────────────────────────────────────────────────────────┘ │
│  General           │  Page title                              [page actions]   │
│  Replay Buffer     │  ┌ page content (scrolls) ─────────────────────────────┐  │
│  Hotkeys           │  │                                                      │  │
│  …                 │  │                                                      │  │
│  Library and Cl…   │  └──────────────────────────────────────────────────────┘  │
└────────────────────┴────────────────────────────────────────────────────────────┘
```

- **Columns:** the nav pane (`{spacing.nav-width}`, 200) and the content column. Below a 640 px window width, the nav pane narrows to 160 and long titles trim with an ellipsis and a tooltip.
- **Content column:** padded `{spacing.xl}`, with three parts in order:
  1. the **status header**;
  2. the **page header**: the page title in `{typography.title}`, with the Library's filters on a row beneath it on the Library page;
  3. the **page content**, which fills the rest and scrolls on its own.
- **Window size:** [DECISION, builder 2026-11-04] the default grows to 960×600, so the navigation and the Library rows fit without crowding; the minimum stays 480×320. Story 3 makes the change.

### Status line's place

[DECISION] The status line becomes the status header: the first thing in the content column, on every page, as in both renders. It isn't in the title bar or a bottom bar. It holds today's `StatusLine` unchanged: the state text, and the warnings list with Dismiss, scrolling past 120 px.

- **The dot:** coloured by the capture state the snapshot already has. `{colors.record}` while a Recording or Monitor Recording runs, `{colors.success}` while Buffering, and `{colors.text-disabled}` otherwise or when not connected.
- **No Save Clip button.** The mockups show one, but the App has no Save command today, and the epic adds no features.
- **No target-game or buffer details line.** Only the existing state text is shown. The epic's "the Target Game and the buffer" is already in that text ("Buffering on Valorant").

## Voice and Tone

The App's wording doesn't change. Every label, message, prompt and tooltip keeps its text; Gaming dark's uppercase is visual only. The new strings are:

| Where | Text |
|---|---|
| General page, section header | "Appearance" |
| General page, label | "Theme" |
| Theme choices | "Fluent (follows Windows)", "Gaming dark" |
| Theme help text | "Applies at once to every window." |
| General page, existing controls' section | "Startup and Overlay" |
| Nav section label | "Settings" |
| Dev tray item | "Control gallery" |

| Do | Don't |
|---|---|
| Keep the existing sentence-case wording | Write uppercase strings into XAML for Gaming dark |
| Name pages by their titles ("Track Layout") | Rename to the mockups' "Audio tracks" |
| "Applies at once to every window." | "Restart to apply" (it never needs one) |

## Component Patterns

Behavioural rules. The visuals are in DESIGN.md's Components section.

| Component | Use | Behavioural rules |
|---|---|---|
| Nav pane | Shell | A `ListBox` with single selection. Selection follows the keyboard: arrows move and open the page, as today's settings list does. The section label is a disabled, non-focusable item that arrow keys skip. Selection is never empty. |
| Status header | Shell, every page | Read-only except each warning's Dismiss. It shows the same states and warnings as today's `StatusLine`. |
| Page header | Every page | The page title is static text, set from `SettingsPage.Title` or "Library". A view's own first heading that repeats the title (Replay Buffer, Hotkeys, Recordings, Feedback Sounds) becomes a section header, so the views keep it when they're embedded elsewhere, such as Game Profiles' Overrides. |
| Settings section | Settings pages | A section header plus a card holding that section's rows. Sections are the views' existing SemiBold headings (Output Folder and Cleanup; Replay Buffer and Output; Devices and Tracks). A page with no heading (General) gets the new "Appearance" and "Startup and Overlay" sections. |
| Settings row | Settings pages | Label column (shared size, as today's `SharedSizeGroup="Label"`) plus a control. Rows are `{spacing.sm}` apart. Help text sits below its row in `caption`, `text-secondary`. The content is at most 720 wide and left-aligned. |
| Page error message | Settings pages | Today's `SelectedPage.Message` becomes a danger info bar at the bottom of the page content, shown only when the message isn't empty. The text is unchanged. |
| Theme picker | General → Appearance | A combo box of the two choices. Choosing one sends `settings.update` for `appTheme` at once, through the page's `SettingsSender`, like Start with Windows, with no Save button. The App restyles when `settings.changed` returns. A refused update shows in the page error message, as for any setting. |
| Library filters | Library page header, on its own row under the title | Group by, Game and Date combo boxes with their existing labels as captions, and Favourites only as a toggle button. It's bound to the same `FavouritesOnly`, and its label stays "Favourites only". [DECISION] They sit under the title, not beside it: the render showed the title squeezed to "L…" beside them at 900 px. The row is a WrapPanel, as today, so it wraps when narrow. |
| Library list | Library | One virtualized list of group headers and rows, as today (`VirtualizingStackPanel`, recycling, pixel scrolling). [DECISION] It stays a **list, not a card grid**: a grid with group headers needs a custom virtualizing wrap panel, and story 4 must keep the list virtualized. The bigger thumbnail and the card styling bring the mockups' look to the rows. |
| Library row | Library | Double-click opens, and right-click opens the context menu (Open, Show in Explorer, Copy, Rename, Delete). Inline rename keeps its rules: Enter or leaving commits, Escape cancels. The star toggle sends `library.setFavourite` and shows the snapshot's state. The being-written lock shows "Saving…" as a pill. The path tooltip stays on the name. |
| Library-corrupt banner | Library, above the list | A caution info bar with the existing text and its Acknowledge button. It's shown while `IsCleanupBlocked` is true. |
| Context menu | Library rows, tray | WPF menu behaviour: arrows, Enter, Esc, and access keys where the menu has them. The tray menu is built by `CreateTrayMenu` and takes `Application.Resources`, so it restyles live. |
| Dialog | `Prompts` callers | One owned, modal window per prompt (story 7), replacing `MessageBox` behind `Prompts`' unchanged API. Wording and caption are unchanged. Yes is the default button, as MessageBox's YesNo default is. Enter activates the focused button, which starts on the default. Esc and the close box answer No (`false`), the safe answer, since YesNo has no Cancel. With no owner shown, it centres on screen and shows in the taskbar. It sets `WDA_EXCLUDEFROMCAPTURE`, being a `ShellWindow`. |
| Overlay message card | Overlay | Only its brushes, corner and type come from the theme, and it restyles live. Timing, placement, size, click-through and never-activated behaviour are unchanged. |
| Control gallery | Dev tray item | A non-modal `ShellWindow` showing every DESIGN.md component in every state. Disabled and selected states are set statically; hover and focus are shown live. It isn't built or shown on the installed instance. |

## State Patterns

| State | Surface | Treatment |
|---|---|---|
| Connecting, restarting, not running, crash loop | Status header | Today's text, with the dot in `text-disabled`. The pages show their last state, as today. |
| Buffering, Recording, Monitor Recording, Mic muted | Status header | Today's text with its "·" parts; the dot is `success` or `record`. |
| Warnings present | Status header | The list under the state line, newest first, scrolling past 120 px; each row's Dismiss where it applies. |
| Empty Library | Library | [DECISION] A centred `body` line in `text-secondary`: "No Clips or Recordings yet." It shows only when there are no entries and no filter is on, and the filters bar stays. Today's App shows an empty list; this is the one new message. Kept by the builder (2026-11-04). |
| Filter matches nothing | Library | The empty list, as today. |
| Missing thumbnail | Library row | The placeholder with the ▶ glyph. |
| Being written | Library row | The "Saving…" caution pill. Rename and Delete behave as today. |
| Library index rebuilt | Library | The caution banner, until it's acknowledged. |
| Action failed | Library | `ActionError` in `danger` under the filters, as today. |
| Page validation or refused update | Settings page | The danger info bar at the bottom. |
| Hotkey failed or duplicate | Hotkeys row | The row status in `danger`, as today. |
| Device unavailable | Track Layout | The device "Note" in `danger`. |
| Double counting | Track Layout | ⚠ and the warning text in `caution`. |
| Estimate over the limit | Replay Buffer | The estimate warning as a caution info bar. |
| Use Global on | Profile overrides | The controls disabled, in their themes' disabled look. |
| Theme switching | Every window | The restyle happens in place on the next layout pass. There's no animation, no recreated window, and no lost focus, scroll, selection or draft. |
| Windows mode or accent changes under Fluent | Every window | It follows live, the same way. |

## Interaction Primitives

**Keyboard.** Everything reachable by mouse is reachable by keyboard, as today.

- **Tab order** in the Main App window: nav pane → status header's Dismiss buttons → page header controls (the Library's filters) → page content in reading order. Shift+Tab reverses it.
- **Nav pane:** Up, Down, Home and End move the selection and open the page. Typing a letter jumps to the next item starting with it (ListBox text search).
- **Ctrl+Tab and Ctrl+Shift+Tab** step to the next or previous nav item from anywhere in the window. Today's TabControl gives this between Library and Settings, so the new nav keeps it, over all nine pages. While the Hotkeys page is capturing a key, the page takes every key, Ctrl+Tab included, as it does today.
- **Library rows:** the star toggle and the rename box are the focusable parts, as today. The Menu key or Shift+F10 on a focused star opens its row's context menu.
- **Dialog:** focus starts on the default button. Tab cycles the buttons, Enter activates, and Esc answers No.
- **Tray menu:** standard menu keys.
- **Overlay:** never takes focus or input.

**Focus.**

- The focus visual (DESIGN.md) shows on every keyboard-focused control in both themes. It's never removed or set transparent.
- A theme switch keeps the focused element.
- After the nav selection changes by keyboard, focus stays in the nav pane, so arrowing through pages works. Tab moves into the page.

**Mouse.** Hover states on every control. There's no drag except the slider and the scroll bars.

## Accessibility Floor

- **Contrast:** text meets WCAG AA in both themes (contrast table in DESIGN.md). Under Fluent that's Microsoft's tokens; under Gaming dark, the values computed there.
- **Status is never colour alone.** The status header's dot always sits beside the state text, and warnings carry their severity word.
- **Screen readers:** nav items expose their page titles, sentence case, since uppercase is a template transform. The section label is not a list item for UI Automation. Dialogs expose their caption as the window name and the message as its text.
- **No new timing,** no auto-dismiss, no motion.

## Theme switching

- The setting `appTheme` is `fluent` or `gamingDark`, and defaults to `fluent` (AD-23).
- A `ThemeService` applies it:
  - at start, before the first window shows;
  - on every `settings.changed` whose `appTheme` differs.
- Applying swaps the merged dictionaries on the UI thread. Every window, the tray menu, open dialogs and the Overlay take their resources from `Application.Resources`, so they all follow at once.
- Fluent's Mica applies to the Main App window only. Gaming sets the DWM caption colour on each window, when it's created and on each switch.

## Key Flows

[ASSUMPTION] The protagonist is the builder, the App's one user.

### Flow 1 — Picking a look (R1)

1. The builder opens the Main App from the tray. It's Fluent, dark like Windows, with Mica behind the nav pane. Library is selected.
2. They press Ctrl+Tab to reach General. Appearance is the first section.
3. They open Theme and choose Gaming dark.
4. **Climax:** the window, the open tray menu and a Library delete dialog left open behind it all turn near-black and teal at once, nav items uppercase. There's no restart, and General stays selected with focus on the combo box.
5. They quit and start the App: it opens in Gaming dark.
6. **Failure path:** if the engine refuses the update, the danger info bar shows the error, and the theme stays as it was.

### Flow 2 — Fluent following Windows (R2)

1. With Fluent chosen, the builder switches Windows to light mode and picks a new accent colour.
2. **Climax:** the open Main App turns light with the new accent on the selected nav pill and the focused button, without being touched.
3. Under Gaming dark the same change does nothing to the App.

### Flow 3 — Clip check after a match (R5)

1. Mid-game, the builder saves a Clip. The Overlay's message card reads the save in the theme's card. The dot and saving icon keep their colours.
2. After the match they open the Library. The status header reads "Buffering on Valorant". The new Clip is the top row of today's group, with "Saving…" until it's written.
3. They star it, right-click, choose Rename, type, and press Enter.
4. They delete an older Clip.
5. **Climax:** the themed dialog asks to move it to the Recycle Bin, with Yes as the accent default. They press Esc, and nothing is deleted.

### Flow 4 — Every page reachable (R5, R6)

1. The builder arrows down the nav pane from Library to Library and Cleanup. Each page opens as its item is selected, with its title in the page header and its content in sections.
2. **Climax:** nothing is missing compared with today's tabs.
3. They walk it again in the other theme.

## Build Map

What stories 2 to 7 build from these spines. Each area story adds its own dictionary that references tokens only.

| Story | Builds |
|---|---|
| 2 — Theme infrastructure, live switching and a control gallery | The `appTheme` key. The `ThemeService` (start, live switch, Mica on the Main App, DWM caption colour for Gaming). The Gaming token dictionary (every Colors, Typography and Shapes token above) and the Fluent-side App keys (`RbAccentSubtleBrush`, `RbRecordBrush`, `RbOverlayCardBrush`, `RbSmallCornerRadius`, `RbNavItemCornerRadius`, `Rb*` spacing and type styles). Gaming templates and every state for Button, Accent button, Toggle button, Star toggle, Text box, Combo box, Check box, Radio button, Slider, List box and items, List view, Context menu and Menu item, Tooltip, Scroll bar, Tab control, Progress bar and Level meter, Card and Info bar, and the focus visual. The uppercase converter. The theme picker on the General page (story 5 later puts it in the Appearance section). The control gallery and its dev tray item. |
| 3 — Shell, navigation and status line | The nav pane and nav item style, the brand, the section label, the shell grid and its narrow width, the page header, Ctrl+Tab, the status header and its dot, and the title bar per theme (Mica or DWM caption colour). |
| 4 — Library restyled | The page header filters, the Favourites toggle, group headers, the Library row (thumbnail, length badge, lock pill, star toggle, rename box), the row context menu's look, the corrupt banner, the action error, and the empty state. |
| 5 — Settings pages: General, Replay Buffer, Recordings, Hotkeys and Feedback Sounds | The General page's Appearance and Startup and Overlay sections. The shared settings layout (section header, settings card, settings row, help text, the page error info bar) and those five pages on it, including the estimate warning bar and the Hotkeys row status. |
| 6 — Settings pages: Track Layout, Game Profiles, and Library and Cleanup | Those three pages on story 5's layout. The device list and its Note, the Tracks list with Level meters, the double-counting warning, and Save and Revert. The profile list, overrides and their disabled states. The Output Folder row and the Cleanup limits. |
| 7 — Tray menu, dialogs and Overlay messages | The tray menu's look and live restyle (the gallery item included). The dialog window behind `Prompts`. The Overlay message card. |

## Inspiration & Anti-patterns

- **Lifted from WinUI's NavigationView:** the left pane, the selection pill, and the content layer over Mica (Fluent).
- **Lifted from the Gaming mockup:** the full-width selected nav row with its left bar, the uppercase labels, the near-black surfaces, the teal accent.
- **Rejected — the mockups' search box:** the App has no search.
- **Rejected — a Save Clip button in the status header:** the App has no save command.
- **Rejected — a Library card grid:** it can't keep the virtualized grouped list.
- **Rejected — custom window chrome:** the system title bar is kept, themed.
- **Rejected — a Gaming light variant, per-game themes and custom accents:** the epic's Boundaries.
