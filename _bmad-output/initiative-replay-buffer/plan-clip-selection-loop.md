# Loop playback inside current clip boundaries

2026-11-07 builder request: “the playback should loop through the current boundary for the clip sliders.” Ordinary implementation following the existing scoped test/data/app rules. This explicitly supersedes the refresh design's original non-looping playback behavior.

Implemented on `feature/clip-selection-loop`:

- Play resumes inside the current In/Out interval; if paused outside it, Play first seeks to In. While playing, reaching Out or moving In past the current position seeks back to the live In boundary and continues playback. Slider/numeric/nudge changes feed the same current requested bounds without changing export alignment or requiring a rendered preview.
- Rendered selection playback loops from its effective local end to zero and maps back to effective source time. Trimming a playing rendered preview invalidates it and continues the loop on source footage with the new range.
- Paused seeking remains independent. Active scrubbing temporarily suppresses automatic wrapping. Pause, hide and disposal prevent automatic restart. No global loop toggle/settings or ordinary Library playback change.
- The shared player accepts an optional creator-supplied live-range callback. The default is absent; ordinary EOF still pauses. Normal source/preview clocks handle wrap, and the existing UI-marshaled EOF handler handles loops ending at physical media EOF. Callback ownership is cleared on disposal.

Verification: Release build **0 warnings, 0 errors**; authorized non-Explicit `ClipCreatorTests` **22 passed, 0 failed/skipped**, including two new fake-player regressions for live range edits/paused seeking and source/rendered EOF/trim invalidation/hide/ordinary-player isolation. Scoped diff review found no unresolved directly introduced issue. Prototype script syntax checked after bringing its loop behavior into agreement with EXPERIENCE.md.

Actual media loop timing, seek latency and audio continuity (especially ranges shorter than the position refresh interval), native input/HITL/high DPI and broader player suites remain deferred under existing clearance. This uses seek-based looping, not a new sample-accurate or gapless media engine. Dev App, real data/settings and system settings untouched; no push.
