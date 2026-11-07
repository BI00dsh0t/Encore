---
title: Clip editor refresh from timeline reference
status: implemented
updated: 2026-11-07
---

The proposed visual contract is [DESIGN.md](DESIGN.md); the proposed behavior contract is [EXPERIENCE.md](EXPERIENCE.md).

Open [.working/mockup.html](.working/mockup.html) for the offline interactive design. Choose Gaming dark, Fluent dark or Fluent light in the review strip. Drag the playhead and either trim grip, change exact times, nudge by 1ms, zoom/pan, double-click a waveform, collapse navigation, preview the illustrative selection, and inspect video/audio Export options. No files are exported.

The builder approved the filmstrip, ±10s transport, red playhead and styling on 2026-11-07 and requested implementation. It is integrated into develop at `5e7c56afcab2e93355cb274f81ab828f712bc1bf`, preserving 40px per-track lanes and recent waveform/precision/preview/export/shell contracts. Historical mockups remain preserved. See [implementation-notes.md](implementation-notes.md) for verification and deferred checks.
