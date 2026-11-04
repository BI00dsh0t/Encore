---
name: ReplayBuffer Modern UI
description: Two themes on one token set for the ReplayBuffer Main App, its tray menu, dialogs and Overlay messages. Fluent is .NET 10 WPF's built-in ThemeMode; Gaming dark is a fixed near-black palette with a teal accent. Colour values below are Gaming dark's; each token's Fluent source is in the Colors table.
status: final
updated: 2026-11-04
sources:
  - ../epic-modern-ui.md
  - ../../../architecture-replay-buffer/architecture-replay-buffer.md
  - ../../../research/ui-look-mockups.html
  - ../../../research/ui-look-render/gaming-theme.xml
colors:
  # Gaming dark values. WPF hex is #AARRGGBB when 8 digits.
  bg-base: '#0A0B0E'
  bg-layer: '#0A0B0E'
  surface-card: '#16191F'
  surface-card-secondary: '#121419'
  fill-control: '#1E222A'
  fill-control-hover: '#252A33'
  fill-control-pressed: '#16191F'
  fill-control-disabled: '#16191F'
  fill-input-active: '#0A0B0E'
  fill-subtle: '#00000000'
  fill-subtle-hover: '#1A8B949E'
  fill-subtle-pressed: '#0F8B949E'
  fill-flyout: '#1E222A'
  fill-track: '#262B34'
  fill-on-image: '#CC0A0B0E'
  stroke-control: '#262B34'
  stroke-control-secondary: '#323844'
  stroke-card: '#262B34'
  stroke-flyout: '#262B34'
  stroke-divider: '#262B34'
  stroke-strong: '#8B949E'
  stroke-strong-disabled: '#323844'
  text-primary: '#E6E8EB'
  text-secondary: '#8B949E'
  text-disabled: '#4D545E'
  text-on-accent: '#04201C'
  text-on-accent-disabled: '#4D545E'
  text-accent: '#2DD4BF'
  accent: '#2DD4BF'
  accent-hover: '#5EEAD4'
  accent-pressed: '#14B8A6'
  accent-disabled: '#262B34'
  accent-subtle: '#1A2DD4BF'
  danger: '#FF4655'
  danger-background: '#2A1215'
  caution: '#F5B83D'
  caution-background: '#2A2212'
  success: '#4ADE80'
  record: '#FF4655'
  focus-outer: '#2DD4BF'
  focus-inner: '#0A0B0E'
  overlay-card: '#E61E222A'
typography:
  # Gaming dark values; Fluent values in the Typography table.
  caption: { fontFamily: 'Segoe UI', fontSize: 12px, fontWeight: '400' }
  body: { fontFamily: 'Segoe UI', fontSize: 14px, fontWeight: '400' }
  body-strong: { fontFamily: 'Segoe UI', fontSize: 14px, fontWeight: '700' }
  section-header: { fontFamily: 'Segoe UI', fontSize: 12px, fontWeight: '700', letterSpacing: 0.6px, note: 'uppercase' }
  nav-item: { fontFamily: 'Segoe UI', fontSize: 13px, fontWeight: '600', letterSpacing: 0.4px, note: 'uppercase' }
  button: { fontFamily: 'Segoe UI', fontSize: 13px, fontWeight: '700', letterSpacing: 0.4px, note: 'uppercase' }
  subtitle: { fontFamily: 'Segoe UI', fontSize: 20px, fontWeight: '700' }
  title: { fontFamily: 'Segoe UI', fontSize: 26px, fontWeight: '900', note: 'uppercase' }
  brand: { fontFamily: 'Segoe UI', fontSize: 15px, fontWeight: '900', note: 'uppercase' }
rounded:
  # Gaming dark values; Fluent values in the Shapes table.
  control: 4px
  overlay: 4px
  small: 2px
  nav-item: 0px
spacing:
  # The same in both themes (Windows 4 px grid; Fluent has no spacing keys).
  xs: 4px
  sm: 8px
  md: 12px
  lg: 16px
  xl: 24px
  xxl: 32px
  nav-width: 200px
components:
  button:
    background: '{colors.fill-control}'
    border: '{colors.stroke-control}'
    foreground: '{colors.text-primary}'
    radius: '{rounded.control}'
    type: '{typography.button}'
    padding: '{spacing.md} {spacing.xs}'
  button-accent:
    background: '{colors.accent}'
    border: '{colors.accent}'
    foreground: '{colors.text-on-accent}'
    radius: '{rounded.control}'
    type: '{typography.button}'
  toggle-button-checked:
    background: '{colors.accent-subtle}'
    border: '{colors.accent}'
    foreground: '{colors.text-accent}'
    radius: '{rounded.control}'
  text-box:
    background: '{colors.fill-control}'
    border: '{colors.stroke-control}'
    focused-background: '{colors.fill-input-active}'
    focused-underline: '{colors.accent}'
    foreground: '{colors.text-primary}'
    radius: '{rounded.control}'
  combo-box:
    background: '{colors.fill-control}'
    border: '{colors.stroke-control}'
    glyph: '{colors.text-secondary}'
    dropdown: '{colors.fill-flyout}'
    radius: '{rounded.control}'
  check-box:
    box-border: '{colors.stroke-strong}'
    checked-fill: '{colors.accent}'
    tick: '{colors.text-on-accent}'
    radius: '{rounded.small}'
  radio-button:
    ring: '{colors.stroke-strong}'
    checked-ring: '{colors.accent}'
    checked-dot: '{colors.accent}'
  slider:
    track: '{colors.fill-track}'
    value-fill: '{colors.accent}'
    thumb: '{colors.text-primary}'
  list-item:
    hover: '{colors.fill-subtle-hover}'
    selected: '{colors.accent-subtle}'
    selected-pill: '{colors.accent}'
    radius: '{rounded.control}'
  nav-item:
    foreground: '{colors.text-secondary}'
    hover: '{colors.fill-subtle-hover}'
    selected-background: '{colors.accent-subtle}'
    selected-bar: '{colors.accent}'
    selected-foreground: '{colors.text-accent}'
    type: '{typography.nav-item}'
    radius: '{rounded.nav-item}'
  menu:
    background: '{colors.fill-flyout}'
    border: '{colors.stroke-flyout}'
    item-hover: '{colors.fill-subtle-hover}'
    check: '{colors.text-accent}'
    radius: '{rounded.overlay}'
  tooltip:
    background: '{colors.fill-flyout}'
    border: '{colors.stroke-flyout}'
    foreground: '{colors.text-primary}'
    radius: '{rounded.control}'
  scroll-bar:
    thumb: '{colors.stroke-strong}'
    track-hover: '{colors.fill-subtle-hover}'
  progress-bar:
    track: '{colors.fill-track}'
    fill: '{colors.accent}'
  level-meter:
    track: '{colors.fill-track}'
    fill: '{colors.success}'
    peak: '{colors.caution}'
  card:
    background: '{colors.surface-card}'
    border: '{colors.stroke-card}'
    radius: '{rounded.overlay}'
    padding: '{spacing.lg}'
  status-header:
    background: '{colors.surface-card}'
    border: '{colors.stroke-card}'
    radius: '{rounded.overlay}'
  info-bar-danger:
    background: '{colors.danger-background}'
    accent: '{colors.danger}'
    foreground: '{colors.text-primary}'
  info-bar-caution:
    background: '{colors.caution-background}'
    accent: '{colors.caution}'
    foreground: '{colors.text-primary}'
  dialog:
    background: '{colors.bg-base}'
    button-bar: '{colors.surface-card-secondary}'
    divider: '{colors.stroke-divider}'
  overlay-card:
    background: '{colors.overlay-card}'
    border: '{colors.stroke-flyout}'
    radius: '{rounded.control}'
    type: '{typography.caption}'
  library-row:
    background: '{colors.surface-card}'
    hover: '{colors.fill-control-hover}'
    border: '{colors.stroke-card}'
    radius: '{rounded.overlay}'
  length-badge:
    background: '{colors.fill-on-image}'
    foreground: '{colors.text-primary}'
    radius: '{rounded.small}'
  star-toggle:
    off: '{colors.text-secondary}'
    on: '{colors.text-accent}'
  focus-visual:
    outer: '{colors.focus-outer}'
    inner: '{colors.focus-inner}'
---

## Brand & Style

ReplayBuffer is a tool you glance at between games. It sits in the tray, and its Main App is a settings-and-library window, not a dashboard. Both themes keep it quiet: one accent that means "selected" or "act here", status colours only for status, and nothing that moves on its own.

The two themes are one layout and one token set with two skins.

- **Fluent** is the Windows 11 look. It's .NET 10 WPF's built-in `ThemeMode = System`. It follows the Windows light or dark mode and accent colour live, and shows Mica on the Main App window. The App adds no colour of its own: every Fluent token is a key Fluent already defines, or a brush built from one of Fluent's colour keys.
- **Gaming dark** is the near-black, teal-accent look from the mockups. It's `ThemeMode = Dark` with the App's Gaming dictionary merged after Fluent's. The dictionary overrides the Fluent brush keys listed below and replaces the control templates. The palette is fixed whatever the Windows mode. Corners are 4 px, with square navigation items and 2 px small radii. Navigation items, button labels, section headers, the page title and the brand are uppercase.

The mockups (`research/ui-look-mockups.html`), the two renders and the `ui-look-render` prototype are the starting point. The renders in [`renders/`](renders/) show this design. These spines win where they differ from any of them.

## Colors

**How tokens are keyed.** A token's XAML key is the Fluent brush key itself, so App XAML writes `{DynamicResource TextFillColorPrimaryBrush}`. Under Fluent the key resolves to Fluent's own brush and follows Windows live. Under Gaming dark the Gaming dictionary redefines the same key. The four tokens Fluent has no key for (`accent-subtle`, `record`, `overlay-card` and the small and nav-item radii) get App keys prefixed `Rb`. Both theme dictionaries define them: the Fluent one as a brush whose `Color` is a `DynamicResource` on a Fluent colour key, so it follows Windows too.

[DECISION] Fluent's brush keys serve as the token keys, rather than a parallel `Rb*` set aliasing them. WPF can't alias one resource key to another dynamically, and Fluent swaps its whole dictionary when Windows changes mode. A parallel set would need re-merging on every Windows change. Using the keys directly is what AD-23 describes ("overriding Fluent's brush keys") and what the prototype does.

| Token | XAML key | Fluent source | Gaming dark | Used for |
|---|---|---|---|---|
| `bg-base` | `SolidBackgroundFillColorBaseBrush` | same key | `#0A0B0E` | Window background where Mica isn't shown; dialog background; nav pane |
| `bg-layer` | `LayerFillColorDefaultBrush` | same key | `#0A0B0E` | The content area's layer over Mica (Fluent's NavigationView pattern) |
| `surface-card` | `CardBackgroundFillColorDefaultBrush` | same key | `#16191F` | Cards: status header, settings sections, Library rows |
| `surface-card-secondary` | `CardBackgroundFillColorSecondaryBrush` | same key | `#121419` | Dialog button bar |
| `fill-control` | `ControlFillColorDefaultBrush` | same key | `#1E222A` | Button, combo box and text box rest fill; thumbnail placeholder |
| `fill-control-hover` | `ControlFillColorSecondaryBrush` | same key | `#252A33` | Control hover; Library row hover |
| `fill-control-pressed` | `ControlFillColorTertiaryBrush` | same key | `#16191F` | Control pressed |
| `fill-control-disabled` | `ControlFillColorDisabledBrush` | same key | `#16191F` | Disabled control fill |
| `fill-input-active` | `ControlFillColorInputActiveBrush` | same key | `#0A0B0E` | Focused text box fill |
| `fill-subtle` | `SubtleFillColorTransparentBrush` | same key | `#00000000` | Rest fill of nav items, menu items, list items |
| `fill-subtle-hover` | `SubtleFillColorSecondaryBrush` | same key | `#1A8B949E` (muted at 10 %) | Hover on nav, menu, list items; scroll bar track hover |
| `fill-subtle-pressed` | `SubtleFillColorTertiaryBrush` | same key | `#0F8B949E` (muted at 6 %) | Pressed nav, menu, list items |
| `fill-flyout` | `SolidBackgroundFillColorQuarternaryBrush` | same key | `#1E222A` | Context and tray menus, combo drop-downs, tooltips |
| `fill-track` | `ControlStrongFillColorDefaultBrush` | same key | `#262B34` | Slider, progress and level-meter tracks |
| `fill-on-image` | `ControlOnImageFillColorDefaultBrush` | same key | `#CC0A0B0E` | Length badge over a thumbnail |
| `stroke-control` | `ControlStrokeColorDefaultBrush` | same key | `#262B34` | Control borders |
| `stroke-control-secondary` | `ControlStrokeColorSecondaryBrush` | same key | `#323844` | Control border on hover |
| `stroke-card` | `CardStrokeColorDefaultBrush` | same key | `#262B34` | Card borders |
| `stroke-flyout` | `SurfaceStrokeColorFlyoutBrush` | same key | `#262B34` | Menu, tooltip, drop-down and Overlay card borders |
| `stroke-divider` | `DividerStrokeColorDefaultBrush` | same key | `#262B34` | Separators, menu separators, the dialog's bar divider |
| `stroke-strong` | `ControlStrongStrokeColorDefaultBrush` | same key | `#8B949E` | Check box and radio rings, scroll bar thumb |
| `stroke-strong-disabled` | `ControlStrongStrokeColorDisabledBrush` | same key | `#323844` | Disabled rings |
| `text-primary` | `TextFillColorPrimaryBrush` | same key | `#E6E8EB` | All body text |
| `text-secondary` | `TextFillColorSecondaryBrush` | same key | `#8B949E` | Muted text: help text, metadata, unselected nav items, the warning time |
| `text-disabled` | `TextFillColorDisabledBrush` | same key | `#4D545E` | Disabled labels |
| `text-on-accent` | `TextOnAccentFillColorPrimaryBrush` | same key | `#04201C` | Labels on accent buttons and the check tick |
| `text-on-accent-disabled` | `TextOnAccentFillColorDisabledBrush` | same key | `#4D545E` | Disabled accent button label |
| `text-accent` | `AccentTextFillColorPrimaryBrush` | same key | `#2DD4BF` | Selected nav item, checked toggle label, Favourite star on, menu check |
| `accent` | `AccentFillColorDefaultBrush` | same key | `#2DD4BF` | Accent button, checked box, selection pill, slider fill |
| `accent-hover` | `AccentFillColorSecondaryBrush` | same key | `#5EEAD4` | Accent hover |
| `accent-pressed` | `AccentFillColorTertiaryBrush` | same key | `#14B8A6` | Accent pressed |
| `accent-disabled` | `AccentFillColorDisabledBrush` | same key | `#262B34` | Disabled accent fill |
| `accent-subtle` | `RbAccentSubtleBrush` | colour `SubtleFillColorSecondary` | `#1A2DD4BF` | Selected nav item and checked toggle background |
| `danger` | `SystemFillColorCriticalBrush` | same key | `#FF4655` | Error text, Error severity, danger info-bar edge |
| `danger-background` | `SystemFillColorCriticalBackgroundBrush` | same key | `#2A1215` | Page error message and library-corrupt banner background |
| `caution` | `SystemFillColorCautionBrush` | same key | `#F5B83D` | Warnings (double-counting, Warning severity), the "Saving…" lock, meter peak |
| `caution-background` | `SystemFillColorCautionBackgroundBrush` | same key | `#2A2212` | Caution info-bar background |
| `success` | `SystemFillColorSuccessBrush` | same key | `#4ADE80` | Buffering dot in the status header, level-meter fill, Overlay success text |
| `record` | `RbRecordBrush` | colour `SystemFillColorCritical` | `#FF4655` | The status header's dot while Recording |
| `focus-outer` | `FocusStrokeColorOuterBrush` | same key | `#2DD4BF` | Focus visual, outer 2 px |
| `focus-inner` | `FocusStrokeColorInnerBrush` | same key | `#0A0B0E` | Focus visual, inner 1 px |
| `overlay-card` | `RbOverlayCardBrush` | colour `SolidBackgroundFillColorQuarternary`, opacity 0.9 | `#E61E222A` | The Overlay's message card |

[DECISION] Gaming dark's state colours (hover, pressed, disabled, the input, flyout and info-bar backgrounds, `success`, `caution`, `text-disabled`) aren't in the epic's palette. They're derived from it, staying in the same blue-grey family as the surfaces. `danger` reuses the record colour `#FF4655`. The subtle fills are translucent muted grey, as Fluent's are translucent white, so a hover lightens whatever it sits on: the nav pane, a card or a menu. A solid hover was darker than the menu in the first render.

[DECISION] Status colours are the only hues besides the accent. `success` is green in both themes, so the status header's dot keeps the Overlay's meaning: green Buffering, red Recording. The Gaming render's red dot for Buffering is not followed.

[DECISION] The Favourite star loses its hard-coded gold (`#D4A017`). It uses `text-accent` when on and `text-secondary` when off, as in the Gaming mockup, so it follows the theme.

**Contrast (WCAG AA, 4.5:1 for text).** Gaming dark, computed:

| Pair | Ratio |
|---|---|
| `text-primary` on `bg-base` | about 16:1 |
| `text-secondary` on `bg-base` | 6.4:1 |
| `text-secondary` on `surface-card` | 5.8:1 |
| `text-secondary` on `fill-control` | 5.2:1 |
| `text-accent` on `bg-base` | 10.6:1 |
| `text-on-accent` on `accent` | 9.1:1 |
| `danger` on `surface-card` | 5.3:1 |
| `danger` on `danger-background` | 5.3:1 |
| `caution` on `caution-background` | above 8:1 |

`text-disabled` is exempt from AA (WCAG 1.4.3 exempts inactive components) but still reads as text, at about 2.6:1. Fluent's pairs are Microsoft's, which meet AA in both modes. The accent shades come from Windows, which picks the light or dark accent variant for contrast.

## Typography

One family per theme, with the ramp below. Under Fluent, the XAML keys are Fluent's own `*TextBlockStyle` styles and `*FontSize` keys. Under Gaming dark, the Gaming dictionary redefines those styles and adds the App ones (`Rb*`).

| Token | Style key | Fluent | Gaming dark |
|---|---|---|---|
| `caption` | `CaptionTextBlockStyle` | 12, Regular (`CaptionTextBlockFontSize`) | 12, Regular |
| `body` | `BodyTextBlockStyle` | 14, Regular (`BodyTextBlockFontSize`, `ControlContentThemeFontSize`) | 14, Regular |
| `body-strong` | `BodyStrongTextBlockStyle` | 14, SemiBold | 14, Bold |
| `section-header` | `RbSectionHeaderStyle` | 14, SemiBold (as `BodyStrongTextBlockStyle`) | 12, Bold, uppercase, 0.6 spacing, `text-secondary` |
| `nav-item` | `RbNavItemStyle` (in the nav item template) | 14, Regular | 13, SemiBold, uppercase |
| `button` | in the Button templates | 14, Regular, sentence case | 13, Bold, uppercase |
| `subtitle` | `SubtitleTextBlockStyle` | 20, SemiBold (`SubtitleTextBlockFontSize`) | 20, Bold |
| `title` | `RbPageTitleStyle` | 28, SemiBold (`TitleTextBlockFontSize`) | 26, Black, uppercase |
| `brand` | `RbBrandStyle` | 15, SemiBold | 15, Black, uppercase |

- **Family.** Fluent uses the family its Window style sets, and the App sets none. Gaming dark uses `Segoe UI`.
- **Uppercase.** WPF has no text-transform. Gaming's Button, ToggleButton and nav-item templates, and its title and brand styles, uppercase string content through a converter inside the template. View models and XAML keep their sentence-case wording, so no text, test or `AutomationProperties.Name` changes. [DECISION]
- **Where uppercase applies.** Menus, combo items, check box and radio labels, body text and the Overlay stay sentence case in both themes. The epic names navigation and buttons, and those are kept short; uppercasing longer labels hurts reading. [DECISION]

## Layout & Spacing

A 4 px grid, the same in both themes. Fluent has no spacing keys, so these are App constants (`Thickness` resources prefixed `Rb`, e.g. `RbPagePadding`) defined once, in story 2's shared dictionary. They live outside the two theme dictionaries.

| Token | Value | Use |
|---|---|---|
| `spacing.xs` | 4 | Label to control, badge padding |
| `spacing.sm` | 8 | Between controls in a row, between rows in a card, nav item gap |
| `spacing.md` | 12 | Card padding for status header rows, group header top gap |
| `spacing.lg` | 16 | Card padding, gap between status header and page header |
| `spacing.xl` | 24 | Page padding, gap between settings sections |
| `spacing.xxl` | 32 | Dialog content padding (top) |
| `spacing.nav-width` | 200 | The nav pane's width |

The shell's grid is the nav pane (`spacing.nav-width`) plus the content column, which fills the rest. Its full layout is in EXPERIENCE.md.

## Elevation & Depth

Depth is tonal, not shadowed:

- `bg-base` or Mica first, then `bg-layer` for the content area, `surface-card` for cards, and `fill-control` for controls.
- Menus, drop-downs and tooltips are `fill-flyout` with a `stroke-flyout` border.
  - Fluent adds its own popup shadow.
  - Gaming dark adds none; the border carries the edge.
- Dialogs are separate owned windows, so they get Windows' own window shadow. There's no scrim.
- Under Fluent the content layer has a 1 px `stroke-card` top-left edge and an 8 px top-left radius against the nav pane, as WinUI's NavigationView does. Gaming dark has no layer edge: nav and content share `bg-base`.

## Shapes

| Token | Key | Fluent | Gaming dark | Use |
|---|---|---|---|---|
| `rounded.control` | `ControlCornerRadius` | 4 (same key) | 4 | Buttons, text boxes, combo boxes, toggle buttons, tooltips, list items, the Overlay card |
| `rounded.overlay` | `OverlayCornerRadius` | 8 (same key) | 4 | Cards, Library rows, menus, drop-downs, the content layer's corner |
| `rounded.small` | `RbSmallCornerRadius` | 4 | 2 | Check box, length badge, thumbnail clip, meter bars |
| `rounded.nav-item` | `RbNavItemCornerRadius` | 4 | 0 | Nav item background; Gaming's is square with a 3 px left bar |

## Components

Every control the App uses, in every state, in both themes. In the tables, "Fluent built-in" means Fluent's own template and state brushes, which the App keeps and doesn't restyle. Gaming dark replaces those templates in its dictionary (story 2), starting from `research/ui-look-render/gaming-theme.xml`. Every state is listed for both themes, because the prototype templates have no hover, pressed, disabled or focus states.

**Who builds what:**

- **Story 2:** the tokens and every generic control below (Button through Tab control, Card, Info bar, the focus visual).
- **Story 3:** the Nav item, Status header and Title bar.
- **Story 4:** the Library row, Group header and Star toggle in use.
- **Stories 5 and 6:** the settings layout's use of Card, Info bar and Inline status text.
- **Story 7:** the Dialog, the tray's Context menu use and the Overlay message card.

EXPERIENCE.md's Build Map has the full list.

**Focused**, in every table, means the focus visual: a 2 px `focus-outer` ring outside 1 px of `focus-inner`, following the control's corner radius plus 2. Fluent draws its own (`DefaultControlFocusVisualStyle`), and Gaming dark defines one with the same shape in its own tokens. It's shown on keyboard focus only, the WPF default.

### Button

| State | Fluent | Gaming dark |
|---|---|---|
| Normal | Fluent built-in (`fill-control`, `stroke-control`, `text-primary`) | `fill-control`, 1 px `stroke-control`, `text-primary`, `typography.button` uppercase, `rounded.control`, padding 12×4 min height 28 |
| Hover | built-in | `fill-control-hover`, `stroke-control-secondary` |
| Pressed | built-in | `fill-control-pressed`, label `text-secondary` |
| Focused | focus visual | focus visual |
| Disabled | built-in | `fill-control-disabled`, `stroke-control`, label `text-disabled` |

### Accent button (`AccentButtonStyle`)

Used once per surface at most: the dialog's default button. [DECISION] Track Layout's Save stays a plain button. Its enabled state already says "unsaved", and an accent next to Revert would read as the only choice.

| State | Fluent | Gaming dark |
|---|---|---|
| Normal | built-in (`accent`, `text-on-accent`) | `accent` fill and border, `text-on-accent`, Bold uppercase |
| Hover | built-in | `accent-hover` |
| Pressed | built-in | `accent-pressed` |
| Focused | focus visual | focus visual |
| Disabled | built-in | `accent-disabled`, `text-on-accent-disabled` |

### Toggle button (Library's Favourites filter, the Favourite star)

| State | Fluent | Gaming dark |
|---|---|---|
| Normal | built-in (as Button) | as Button, label `text-secondary` |
| Hover | built-in | as Button hover |
| Pressed | built-in | as Button pressed |
| Checked | built-in (accent fill, `text-on-accent`) | `accent-subtle` fill, `accent` border, label `text-accent` |
| Checked + hover | built-in | `accent-subtle`, `accent-hover` border |
| Focused | focus visual | focus visual |
| Disabled | built-in | as Button disabled; checked keeps an `accent-disabled` border |

**Star toggle** (`RbStarToggleStyle`, the Library row's Favourite) is a borderless variant in both themes: a ★ glyph at 16 px, `text-secondary` off and `text-accent` on, with `fill-subtle-hover` behind on hover, `rounded.control`. [DECISION] It replaces the transparent Button and keeps the command binding. "Checked" is bound one-way to the item's Favourite state, so a click still only sends `library.setFavourite`.

### Text box

| State | Fluent | Gaming dark |
|---|---|---|
| Normal | built-in (`fill-control`, bottom stroke) | `fill-control`, 1 px `stroke-control`, `text-primary`, `rounded.control`, padding 10×5 |
| Hover | built-in | `fill-control-hover` |
| Focused | built-in (`fill-input-active`, accent underline) | `fill-input-active`, 2 px `accent` bottom border; no separate focus ring, since the underline is the focus cue. The caret is `text-primary` and the selection is `accent` at 40 % |
| Disabled | built-in | `fill-control-disabled`, text `text-disabled` |
| Read-only | built-in | as Normal, no hover change |
| Invalid | — (the App shows errors as page messages, not on the box) | — |

### Combo box and combo item

| State | Fluent | Gaming dark |
|---|---|---|
| Normal | built-in | `fill-control`, `stroke-control`, `rounded.control`, chevron `text-secondary` on the right |
| Hover | built-in | `fill-control-hover`, `stroke-control-secondary` |
| Pressed / open | built-in | `fill-control-pressed`; drop-down `fill-flyout`, `stroke-flyout`, `rounded.overlay`, 4 px inset |
| Item hover | built-in | `fill-subtle-hover` |
| Item selected | built-in (accent pill) | `accent-subtle` with a 3 px `accent` left pill, text `text-primary` |
| Focused | focus visual | focus visual |
| Disabled | built-in | `fill-control-disabled`, text and chevron `text-disabled` |

### Check box

| State | Fluent | Gaming dark |
|---|---|---|
| Unchecked | built-in | 18×18 box, 1.5 px `stroke-strong`, transparent, `rounded.small`; label `text-primary` 14 |
| Unchecked + hover | built-in | box fill `fill-subtle-hover` |
| Checked | built-in | `accent` fill and border, tick `text-on-accent` |
| Checked + hover | built-in | `accent-hover` |
| Pressed | built-in | `accent-pressed` (checked) or `fill-subtle-pressed` |
| Focused | focus visual (around box and label) | focus visual |
| Disabled | built-in | box `stroke-strong-disabled`; checked `accent-disabled`; label `text-disabled` |

The App has no three-state check boxes; Gaming draws none.

### Radio button

| State | Fluent | Gaming dark |
|---|---|---|
| Unchecked | built-in | 18 px ring, 1.5 px `stroke-strong` |
| Hover | built-in | ring fill `fill-subtle-hover` |
| Checked | built-in | `accent` ring, 8 px `accent` dot |
| Pressed | built-in | dot 6 px |
| Focused | focus visual | focus visual |
| Disabled | built-in | ring `stroke-strong-disabled`, dot `accent-disabled`, label `text-disabled` |

### Slider

The App uses none today, but story 2 styles it so the gallery is complete and future pages inherit it.

| State | Fluent | Gaming dark |
|---|---|---|
| Normal | built-in | 4 px track `fill-track`, value side `accent`, 12×18 thumb `text-primary`, `rounded.small` |
| Hover | built-in | thumb `accent-hover` |
| Pressed / dragging | built-in | thumb `accent-pressed` |
| Focused | focus visual (around the thumb) | focus visual |
| Disabled | built-in | track `fill-control-disabled`, value `accent-disabled`, thumb `text-disabled` |

### List box and list item (Track Layout's Devices and Tracks lists), list view

| State | Fluent | Gaming dark |
|---|---|---|
| List | built-in | transparent, 1 px `stroke-control`, `rounded.control` |
| Item normal | built-in | `fill-subtle`, padding 10×6 |
| Item hover | built-in | `fill-subtle-hover` |
| Item pressed | built-in | `fill-subtle-pressed` |
| Item selected | built-in (subtle fill, accent pill) | `accent-subtle`, 3 px `accent` left pill, text `text-primary` |
| Item selected + hover | built-in | `accent-subtle` with `fill-subtle-hover` over it |
| Focused | focus visual on the item | focus visual on the item |
| Disabled (Use Global on) | built-in | items `text-disabled`, no hover |

`ListView` takes the same item style; the App uses none.

### Nav item (the shell's navigation list)

A `ListBox` with its own item style (`RbNavItemStyle`).

| State | Fluent | Gaming dark |
|---|---|---|
| Normal | `fill-subtle`, `text-primary`, `typography.nav-item`, `rounded.nav-item`, height 36, padding 12 | `fill-subtle`, `text-secondary`, uppercase, height 40, padding 16 |
| Hover | `fill-subtle-hover` | `fill-subtle-hover`, text `text-primary` |
| Pressed | `fill-subtle-pressed`, text `text-secondary` | `fill-subtle-pressed` |
| Selected | `fill-subtle-hover` with a 3×16 `accent` pill at the left, centred vertically, `rounded.small` | `accent-subtle` full-width, 3 px full-height `accent` bar at the left, text `text-accent` |
| Focused | focus visual | focus visual |
| Disabled | — (no nav item is ever disabled) | — |

The **section label** above the settings pages is not selectable. It uses `typography.caption` in `text-secondary` under Fluent, and `typography.section-header` under Gaming dark.

### Context menu, menu item, separator (Library row menu, tray menu)

| State | Fluent | Gaming dark |
|---|---|---|
| Menu | built-in (`ContextMenuBackground`, flyout stroke) | `fill-flyout`, 1 px `stroke-flyout`, `rounded.overlay`, 4 px padding |
| Item normal | built-in | `text-primary` 14, sentence case, height 32, padding 12×0, `rounded.control` |
| Item hover / keyboard highlight | built-in | `fill-subtle-hover` |
| Item pressed | built-in | `fill-subtle-pressed` |
| Item checked (Start with Windows) | built-in check glyph | ✓ glyph in `text-accent` in the left gutter |
| Item disabled (a command that can't run) | built-in | `text-disabled` |
| Separator | built-in | 1 px `stroke-divider`, 4 px vertical margin |

Keyboard highlight is the hover look, so Gaming dark needs no separate focus ring inside menus. Fluent does the same.

### Tooltip

| State | Fluent | Gaming dark |
|---|---|---|
| Shown | built-in (`ToolTipBackground`) | `fill-flyout`, 1 px `stroke-flyout`, `rounded.control`, `text-primary` 12, padding 8×5, max width 320, wraps |

### Scroll bar

| State | Fluent | Gaming dark |
|---|---|---|
| Rest | built-in (thin, expands on hover) | 4 px thumb, `stroke-strong` at 60 %, no track, no arrows |
| Hover | built-in | 8 px thumb `stroke-strong`, track `fill-subtle-hover` |
| Dragging | built-in | thumb `text-primary` |
| Disabled | hidden | hidden |

### Progress bar and level meter

The level meter is the `ProgressBar` in Track Layout's Tracks list, with `RbLevelMeterStyle`.

| State | Fluent | Gaming dark |
|---|---|---|
| Progress normal | built-in (`ProgressBarForeground` accent) | 4 px track `fill-track`, fill `accent`, `rounded.small` |
| Progress disabled | built-in | fill `accent-disabled` |
| Level meter | 8 px track `fill-track`, fill `success`, `rounded.small`; the top 10 % (level ≥ 0.9) turns `caution` | the same in Gaming tokens |
| Level meter, Track muted | fill `text-disabled` | fill `text-disabled` |

[DECISION] The meter uses `success` and not the accent, so it reads as signal, not selection. The peak colour is drawn by a trigger on Value. "Track muted" applies only if the Track's view model already exposes it; story 6 adds no new state.

### Tab control

The shell's TabControl goes (EXPERIENCE.md), and the App has no other. Story 2 still styles TabItem for the gallery:

| State | Fluent | Gaming dark |
|---|---|---|
| Normal | built-in | `text-secondary`, uppercase `typography.nav-item` |
| Hover | built-in | `text-primary` |
| Selected | built-in | `text-accent`, 2 px `accent` underline |
| Focused | focus visual | focus visual |
| Disabled | built-in | `text-disabled` |

### Card (`RbCardStyle`, a Border style)

`surface-card`, 1 px `stroke-card`, `rounded.overlay`, padding `spacing.lg`. It has no states. Settings sections and the status header are cards; Library rows are a card variant with hover.

### Status header

A card at the top of the content column.

- **Left:** a 10 px dot (`success` Buffering, `record` Recording or Monitor Recording, `text-disabled` otherwise), then the state line in `typography.body-strong` (uppercase under Gaming dark).
- **Below:** the warnings list, one row per warning:
  - the time in `caption`, `text-secondary`;
  - the severity in `body-strong`, coloured `danger` for Error and `caution` for Warning;
  - the message in `body`, `text-primary`;
  - a Dismiss button (Button style) where it applies.

### Info bar (`RbInfoBarStyle`: danger and caution variants)

Used for the library-corrupt banner (caution), each settings page's error message (danger) and the Replay Buffer estimate warning (caution).

- `*-background` fill, a 3 px left edge in `danger` or `caution`, `rounded.control`, padding 12×8.
- Text is `text-primary`.
- An optional action button on the right (the banner's Acknowledge).

### Inline status text

Short coloured messages that stay inline rather than in a bar:

- the Hotkeys row status: `text-secondary`, or `danger` when the row failed;
- the Track Layout device "Note": `danger`;
- the Track warning glyph ⚠ and its text: `caution`;
- the Library action error: `danger`.

Every hard-coded `DarkRed`, `DarkOrange`, `Gray`, `#C42B1C` and `#D4A017` in today's XAML maps to `danger`, `caution`, `text-secondary` or `text-accent`.

### Library row (`RbLibraryRowStyle`)

- **Box:** a card variant, `surface-card`, `stroke-card`, `rounded.overlay`, padding 8, 6 px gap between rows.
- **Hover:** `fill-control-hover`. **Context menu open:** stays at hover. The rows aren't selectable.
- **Thumbnail:** 128×72, clipped to `rounded.small`. Its placeholder is `fill-control` with the ▶ glyph in `text-secondary`.
- **Length badge:** at the thumbnail's bottom right, `fill-on-image`, `text-primary` 12, `rounded.small`, padding 6×1.
- **Text:** name in `body-strong`, with game · date · size beneath in `caption`, `text-secondary`.
- **Lock:** "Saving…" as a caution pill (`caution-background`, `caution` text, `caption`).
- **Star toggle:** on the right.
- **Rename box:** the Text box style, in place of the name.

### Group header (Library)

`typography.section-header` (uppercase under Gaming dark), `text-secondary`, with a `spacing.md` top gap and a 1 px `stroke-divider` rule after the text, filling the row.

### Dialog (`RbDialogWindow`)

An owned window, 440 wide, height to content, centred on its owner, no resize, and no Mica in either theme.

- **Content:** on `bg-base`, padding 24, top 32:
  - the caption (the prompt's caption) in `subtitle`;
  - then the message in `body`, wrapping;
  - an optional glyph at the left of the caption: ⚠ in `caution` for warnings, ? in `text-accent` for questions.
- **Button bar:** `surface-card-secondary` with a 1 px `stroke-divider` top, padding 24×16, buttons right-aligned and 8 apart, each at least 96 wide.
  - The default button uses the Accent button style; the other uses the Button style.
- **Title bar:** the system's, set to the theme like the Main App's (EXPERIENCE.md).

### Overlay message card

Restyled from today's black `#B0000000` box:

- `overlay-card` fill, 1 px `stroke-flyout`, `rounded.control`, padding 8×3, max width 260 as today;
- text in `caption`, coloured `danger` for an Error message and `success` otherwise, as today's red and green.

The dot, saving icon, mic-muted icon, disk-full and red X glyphs keep their fixed brushes (`#E53935`, `#43A047`, `#EEEEEE`) in both themes. The card is the Overlay's only themed element. [DECISION] It's 90 % opaque so a game behind it reads as context without lowering text contrast.

### Title bar

- **Fluent:** the system title bar with Mica behind the window (`WindowBackdropType = Mica`). It's dark or light with Windows.
- **Gaming dark:** the system title bar coloured through DWM, with `DWMWA_CAPTION_COLOR` = `bg-base` and `DWMWA_TEXT_COLOR` = `text-primary`. The border colour is left at the default.

[DECISION] There's no custom window chrome. The system caption buttons, snap layouts and accessibility come free, and a custom title bar would be a new behaviour surface.

## Do's and Don'ts

- **Do** take every colour, corner and type style from the token keys above, through `DynamicResource`. The only literal colours left in the App are the Overlay's glyph brushes.
- **Do** keep keyboard focus visible on every focusable control in both themes.
- **Do** keep one accent action per surface at most.
- **Don't** use the accent for status. Status is `success`, `caution`, `danger` and `record`.
- **Don't** uppercase text in XAML or view models. Gaming's templates uppercase it.
- **Don't** add shadows, gradients or animation beyond Fluent's built-in transitions. Gaming dark has none.
- **Don't** put Mica on any window but the Main App, and never on the Overlay, which stays transparent.
- **Don't** theme the Overlay's dot or glyphs.
