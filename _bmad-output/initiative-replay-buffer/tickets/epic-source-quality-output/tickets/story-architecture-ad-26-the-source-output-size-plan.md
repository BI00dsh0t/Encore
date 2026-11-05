---
title: 'Architecture: AD-26, the source output size'
type: 'docs'
ticket: '6'
created: '2026-11-05'
status: done
baseline_revision: 'c51f4be'
route: 'quick'
risk: 'low'
---

# Story 12.6: AD-26 spine edit

A docs-only architecture update under AD-22, like AD-25's before it. Its decisions are the builder's (2026-11-05: "for 12, I accept the recommended answers").

## What changed

| File | Change |
|---|---|
| `architecture-replay-buffer.md` | **AD-26 "Source output size: the chain follows the source, a file never changes size"**, after AD-25. It has Binds (R20, R40; R12, R17, R21, R33), Prevents, and a Rule covering six things: the `video.matchSource` setting (resolution only, fallback size, storage estimate unchanged); the game capture size (the first frame's content size, fitted by `MonitorCanvasRule.Fit`, nothing under 640×360, no codec switch for games, 1:1 or letterbox); the held size change (3 s, AD-7's build-ahead switch started by the engine and not by a `ChainKey` comparison, never during a Recording, within the budget, GPU reset); a one-screen Monitor Recording through `MonitorCanvasRule.Plan`; and the cost, measured in 12.2. |
| | Pointers: AD-7 now binds R40, and its "Chain changes at a Reset" says a source-size move uses the same switch. AD-25's Canvas says one screen at source size takes its rule. |
| | Frontmatter: `binds` gains R40, `updated` is 2026-11-05, and `scope` says 13 epics. The Capability map gains an R40 row. |
| `epic-source-quality-output.md` | References cite AD-26. Every unattended default is now the builder's decision. The three scope questions are answered. Two Decision lines record the builder's words and link AD-26. |

## Verification

- Docs only; no tests run, as the coordinator asked. The `tickets.py status` tree check still applies to the ticket files.

## Follow-ups

- The spine's binds and capability map don't list R41 (epic 13). It sits under AD-4, AD-5, AD-18 and AD-22 with no new rule, so a one-row map addition is enough when the spine is next touched.
