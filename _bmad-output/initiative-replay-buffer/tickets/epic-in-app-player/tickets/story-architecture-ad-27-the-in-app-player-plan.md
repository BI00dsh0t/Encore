---
title: 'Architecture: AD-27, the in-app player'
type: 'docs'
ticket: '1'
created: '2026-11-05'
status: done
baseline_revision: 'c0353d5'
route: 'quick'
risk: 'low'
---

# Story 14.1: AD-27 spine edit

A docs-only architecture update under AD-22, like AD-26's before it. Its decisions are the builder's: "use the windows media player" and, for the epic's defaults, "Yes, start epic 14" (2026-11-05).

## What changed

| File | Change |
|---|---|
| `architecture-replay-buffer.md` | **AD-27 "In-app player: WinRT MediaPlayer into a D3DImage, App-side and read-only"**, after AD-26. Binds R42 (and R11, R12, R15, R21, R28, R33), with its Prevents. The Rule covers ten things: <ul><li>the owner: App-only, with no message, type or key;</li><li>opening: finished items only, read-only, shared for read, write and delete, through a stream, and closed when the item leaves;</li><li>the player behind a seam;</li><li>frame-server rendering into a shared D3D11 texture, a D3D9Ex surface and a `D3DImage`, with no airspace;</li><li>the interop in `Platform.Desktop` through CsWin32;</li><li>Tracks: index 0 at open because Media Foundation picks the last, one at a time, names from `handler_name`;</li><li>codecs: the Store extensions as an external touch point, the missing-codec page and its dev switch;</li><li>the Recording length: `mehd` at finalize, in place, before `EndWrite`; old files untouched; a crash-cut file shows elapsed time only;</li><li>what's out;</li><li>the research's measurements.</li></ul> |
| | Pointers: <ul><li>AD-1 gains an exception: the App may read a finished file to play it.</li><li>AD-2: `Platform.Desktop` holds the player's D3D interop.</li><li>AD-9 binds R42 and adds `mehd` at finalize to Recordings.</li><li>AD-17: a playing file never blocks a rename, delete or Cleanup.</li></ul> |
| | Frontmatter: `binds` gains R42, and `scope` says 14 epics. The Stack gains a row for Windows' playback and the codec extensions. The Structural Seed adds the Player to the Main App and the player's interop to `Platform.Desktop`. The Capability map gains an R42 row. |
| `epic-in-app-player.md` | References link AD-27. Every unattended default is now the builder's decision ("Yes, start epic 14"). A Decision line links AD-27 and its pointers. |

## Verification

- Docs only; no tests run, as the coordinator asked. The AD-27 anchor in the epic's links matches the heading's GitHub anchor, and `tickets.py status` is still clean.

## Follow-ups

- None for the spine. 14.2 (the player tracer) and 14.3 (`mehd` at finalize) can start in parallel once this merges.
