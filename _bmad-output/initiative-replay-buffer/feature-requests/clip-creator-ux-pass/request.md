# Builder input: clip creator and navigation UX pass

Recorded 2026-11-05 from this chat, after focused clip-creator QA.

## Editor request

> the clip creator needs a UX pass. The UI is too busy. The zoom buttons need to be a plus and minus. The pan buttons should just be arrows.
>
> For the export options, there should be an export button that has a dropdown arrow beside it. Clicking the dropdown allows you to select export as audio or as video. Clicking the button opens a popup that contains the export options relevant to the export type
>
> the video player needs to be larger so you can see more of the clip while editing. The waveform viewer takes up too much height. It should be more streamlined.
>
> Create a mockup first before implementing any plan

## Accepted mockup

The interactive mockup showed a dominant video preview; compact named waveform lanes and time ruler; icon-only pan/zoom; millisecond In/Out fields; a separate Preview selection action; and a split Export video/audio button opening type-specific options. Builder: **"I like this mockup."**

The approved conversation mockup is preserved beside this input as `mockup.html`. It is a design reference, not production code. The illustrative footage, navigation labels, sample track names and tweak controls do not create product requirements.

## Sidebar addition

> I would like to also include the changes to the side menu. It should be expandable and collapsible to icons only.

The mockup was updated with a top-left toggle, labeled expanded navigation, an icon-only collapsed rail, tooltips, the current Library selection, and more editor width when collapsed.

## Settings addition

> In this UX pass. I want to also include the Settings being split out into a button just like in the mockup

Recorded scope: a dedicated Settings button pinned to the bottom of the sidebar, separate from the primary navigation list, labeled when expanded and a gear with a tooltip when collapsed. It opens the existing Settings experience.

## Spec instruction

> use bmad to create a spec for this UX pass

This requests specification work. It does not start implementation, schedule old backlog/HITL work, or expand the current test clearance.

## Control styling correction

> The styling of the controls does not match the mockup

The accepted mockup governs control appearance as well as layout: shapes, density, typography, icon weight, borders, fills, spacing and grouped controls must match. Reusing an existing control template is not sufficient if its appearance differs. Keep semantic theme colors and accessible interaction states while matching the reference treatment.
