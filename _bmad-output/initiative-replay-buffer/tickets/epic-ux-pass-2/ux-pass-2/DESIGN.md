---
name: ReplayBuffer UX pass 2 (addendum)
description: New components for epic 10, built only from epic 9's tokens. It adds no colours, type, corners or spacing.
status: final
updated: 2026-11-04
extends: ../../epic-modern-ui/ux-modern-ui/DESIGN.md
sources:
  - ../epic-ux-pass-2.md
  - EXPERIENCE.md
components:
  library-row-selected:
    background: '{colors.accent-subtle}'
    bar: '{colors.accent}'
    border: '{colors.stroke-card}'
    radius: '{rounded.control}'
  library-row-selected-hover:
    background: '{colors.accent-subtle}'
    overlay: '{colors.fill-subtle-hover}'
  library-command-bar:
    background: '{colors.surface-card}'
    border: '{colors.stroke-card}'
    count: '{typography.body-strong}'
    summary: '{typography.body}'
    caption: '{colors.text-secondary}'
    radius: '{rounded.control}'
    padding: '{spacing.sm} {spacing.md}'
  search-box:
    base: text-box
    glyph: '{colors.text-secondary}'
    min-width: 200px
  track-card:
    background: '{colors.surface-card}'
    border: '{colors.stroke-card}'
    radius: '{rounded.control}'
    padding: '{spacing.md}'
    gap: '{spacing.sm}'
    number: '{typography.subtitle}'
    name: '{typography.body-strong}'
    summary: '{typography.caption}'
  source-row:
    background: '{colors.fill-subtle}'
    hover: '{colors.fill-subtle-hover}'
    name: '{typography.body}'
    kind: '{typography.caption}'
    kind-foreground: '{colors.text-secondary}'
    note: '{colors.danger}'
    radius: '{rounded.small}'
  sound-picker:
    base: menu
    section-header: '{typography.section-header}'
    default-caption: '{colors.text-accent}'
    max-height: 420px
  save-bar:
    base: info-bar
    background: '{colors.surface-card-secondary}'
    border: '{colors.accent}'
  nav-dirty-dot:
    fill: '{colors.accent}'
    size: 6px
  welcome-card:
    base: card
    lead: '{typography.subtitle}'
    body: '{typography.body}'
    link: '{colors.text-accent}'
  folder-picker:
    path-background: '{colors.fill-control-disabled}'
    path-foreground: '{colors.text-primary}'
    path-border: '{colors.stroke-control}'
    caption: '{colors.text-secondary}'
    error: '{colors.danger}'
    radius: '{rounded.control}'
    gap: '{spacing.xs}'
  breadcrumb-header:
    back-button: button-subtle
    parent-link: '{colors.text-accent}'
    separator: '{colors.text-secondary}'
    current: '{typography.title}'
  game-row:
    base: library-row
    icon-size: 32px
    name: '{typography.body-strong}'
    caption: '{typography.caption}'
    chevron: '{colors.text-secondary}'
  pill-capturing:
    background: '{colors.accent-subtle}'
    foreground: '{colors.text-accent}'
    radius: '{rounded.small}'
    type: '{typography.caption}'
  pill-not-captured:
    background: '{colors.fill-control}'
    foreground: '{colors.text-secondary}'
    radius: '{rounded.small}'
    type: '{typography.caption}'
  inheritance-chip-main:
    background: '{colors.fill-control}'
    border: '{colors.stroke-control}'
    foreground: '{colors.text-secondary}'
    radius: '{rounded.small}'
    type: '{typography.caption}'
  inheritance-chip-changed:
    background: '{colors.accent-subtle}'
    border: '{colors.accent}'
    foreground: '{colors.text-accent}'
    radius: '{rounded.small}'
    type: '{typography.caption}'
  inherited-summary:
    foreground: '{colors.text-primary}'
    type: '{typography.body}'
  library-kind-badge:
    background: '{colors.fill-on-image}'
    foreground: '{colors.text-primary}'
    radius: '{rounded.small}'
    type: '{typography.caption}'
---

# ReplayBuffer UX pass 2 — Design addendum

## Brand & Style

This addendum doesn't change epic 9's two looks. Every component here takes its colours, type, corners and spacing from [epic 9's tokens](../../epic-modern-ui/ux-modern-ui/DESIGN.md), by `DynamicResource`, so it follows Fluent and Gaming dark and a live switch (AD-23). It defines no new token.

## Components

### Library row, selected

- **Selected:** the row card's background becomes `{colors.accent-subtle}`, with a 3 px `{colors.accent}` bar on its left edge, the same bar as the nav item's selected state.
- **Hover:** hover on a selected row keeps the selected fill and adds the hover fill over it.
- **Focus:** the focused row shows epic 9's focus visual around the card, whether or not it's selected, so focus and selection always look different.
- Under Gaming dark, the bar is square (`{rounded.nav-item}`), as in the nav.
- **A grey Windows accent** (story 10.1's renders): Fluent's accent-subtle fill is then barely different from a row. When the accent's contrast against the row is below 1.5:1, the selected fill is `SubtleFillColorSecondaryBrush` instead, and the accent bar stays. Story 10.2 checks it on the builder's PC.

### Library command bar

- **Layout:** a one-line card (`{colors.surface-card}`, `{colors.stroke-card}`, `{rounded.control}`) between the filter row and the list. It's 40 px tall in both states, so the list never moves.
- **Nothing selected:**
  - the summary in `{typography.body}`;
  - the how-to caption in `{typography.caption}`, `{colors.text-secondary}`.
- **Something selected:**
  - the count in `{typography.body-strong}`, and "(n hidden by filters)" in caption;
  - the actions as standard buttons, spaced `{spacing.xs}`, with Delete last before the ✕;
  - the ✕ is a subtle (transparent) button.
- **When the actions don't fit:** they move into a "More ⋯" menu, Copy to folder… first, then Show in Explorer. Favourite, Copy and Delete stay. Story 10.1's renders show this happens under Gaming dark at the default 960 px width, because its button labels are uppercase.

### Search box

Epic 9's text box with a leading search glyph (Segoe Fluent Icons Search, U+E721, in `{colors.text-secondary}`) and a trailing ✕ that shows only while there's text. It's 200 px minimum, and grows in the filter row's WrapPanel.

### Track card

```
┌──────────────────────────────────────────────────────────┐
│ 1   Everything                   [ level meter  ]   [⋯]  │  number: subtitle; name: body-strong
│     2 sounds                                             │  caption, text-secondary (caution if silent)
│   ┌ 🔊 Speakers (Realtek)   playback  [meter]   [⋯] ┐    │  source rows
│   └ 🎙 Microphone (Yeti)    microphone [meter]  [⋯] ┘    │
│   [+ Add sound]                                          │  subtle button, text-accent
│   [■] Mic mute key silences the microphone on this track │  toggle switch (check box style)
│   ⚠ You may hear some sounds twice: …                    │  inline caution text
└──────────────────────────────────────────────────────────┘
```

- **The card:** epic 9's card, `{spacing.md}` padded, cards `{spacing.sm}` apart.
- **The track number** in `{typography.subtitle}`, `{colors.text-secondary}`.
- **The header meter:** epic 9's level meter, 160 px wide.
- **Source meters:** the same level meter, 80 px wide and 4 px tall.
- **Glyphs:** Segoe Fluent Icons Volume (U+E767) and Microphone (U+E720), in `{colors.text-secondary}`.
- **Not connected:** the source's name stays `{colors.text-primary}`, and the note is inline status text in `{colors.danger}`.

### Sound picker

Epic 9's flyout and menu surface (`{colors.fill-flyout}`, `{colors.stroke-flyout}`).

- **Top:** a search box.
- **Sections:** two headers in `{typography.section-header}`.
- **Rows:** a name, a caption with the parent and "Windows default", in `{colors.text-accent}`, and an 80 px meter on the right.
- **Already-added rows:** shown disabled with a ✓.
- **Size:** at most 420 px tall, scrolling inside.

### Save bar

An info bar in the neutral variant: `{colors.surface-card-secondary}` with a `{colors.accent}` left border. It holds the text "Unsaved changes" with the applies hint in caption, and the Save changes (accent) and Discard buttons on the right. It's sticky at the top of the page content.

### Nav dirty dot

A 6 px `{colors.accent}` circle after the nav item's text. Under Gaming dark it's square, like every other corner there. It has no animation.

### Welcome card

Epic 9's card, centred in the list area, at most 520 px wide:

- the lead line in `{typography.subtitle}`, with the Hotkey in `{typography.body-strong}` on a `{colors.fill-control}` key-cap chip (`{rounded.small}`);
- the audio and folder lines in `{typography.body}`;
- links as hyperlinks in `{colors.text-accent}`;
- the silent-audio line in `{colors.caution}`.

### Folder picker

```
[ C:\Users\sam\AppData\Local\ReplayBuffer\Cache                         ]
[Browse…] [Open in Explorer] [Use default]
  help text (caption, text-secondary)
  212 GB free on C: (caption, text-secondary)
  The cache folder can't be inside the Clips folder. (caption, danger)
```

- **The path:** a read-only text box (`{colors.fill-control-disabled}` fill, `{colors.text-primary}` text, so it stays readable at AA), selectable, with a middle ellipsis and the full path as its tooltip. It isn't drawn disabled.
- **Buttons:** standard buttons on their own row under the path, `{spacing.xs}` apart. Use default is a subtle button. [DECISION, story 10.1] The renders showed one row clips the buttons beside a long path under Gaming dark.
- **Disabled** (the cache folder under Memory): the whole row takes the disabled look, with "Used only in Disk mode" as its caption.
- **The file variant** shows the same row with Browse… only.

### Breadcrumb header (drill-in pages)

The page header of an item page:

- a subtle **Back** button with the ← glyph (Segoe Fluent Icons Back, U+E72B);
- the parent's title as a hyperlink in `{colors.text-accent}`;
- a › separator in `{colors.text-secondary}`;
- the item's name in `{typography.title}`, Gaming dark's uppercase included.

The Back button and the link get the focus visual. On the Games list the header is the plain title, as on every page.

### Game row

- **Layout:** the selected Library row's card and its selected, hover and focus states, with a 32 px program icon in place of the thumbnail (`{rounded.small}` corners), and a › chevron at the right end.
- **Text:** the name in `{typography.body-strong}`, and the caption line in `{typography.caption}`, `{colors.text-secondary}`.
- **Pills,** in the caption line: **Capturing now** (accent-subtle, text-accent) and **Not captured** (fill-control, text-secondary). The unsaved dot is the nav dirty dot.
- **The "Ignored games" group header** is epic 9's group header.

### Inheritance section

```
CLIPS                                       [ Uses the main setting ]      (section header + chip)
  Save the last 30 seconds                  [Change for this game]  Edit the main setting
```

- **The chip** sits right-aligned on the section header row. The main-setting chip is neutral; the changed chip is accent-subtle with an accent border.
- **The summary** of an inherited value is `{typography.body}` text in `{colors.text-primary}`, never disabled controls, so it meets AA.
- **Buttons:**
  - Change for this game is a standard button.
  - Reset to the main setting is a standard button.
  - Edit the main setting is a hyperlink.

### Library kind badge

Like the length badge on the thumbnail (`{colors.fill-on-image}`, `{rounded.small}`, caption type), in the thumbnail's top-left corner, reading "Recording" or "Monitor Recording".

### Tray status item

A disabled menu item in `{colors.text-secondary}`, at the top of the tray menu, followed by a separator.

## Do's and Don'ts

| Do | Don't |
|---|---|
| Show selection with the accent-subtle fill and the bar together | Show selection with colour alone, or use the focus outline for selection |
| Keep the command bar's height fixed | Let the list jump when the selection changes |
| Reuse the level meter at two sizes | Draw a new meter style |
| Take every colour from epic 9's tokens | Add a token for this epic |
| Show an inherited value as readable text | Show it as a row of disabled controls |
| Show a path read-only with Browse | Offer an editable path box |
