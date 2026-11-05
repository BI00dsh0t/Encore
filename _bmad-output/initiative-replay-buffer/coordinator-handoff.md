# Coordinator handoff (2026-11-05, Codex takeover)

Read root `CLAUDE.md`, this file, `test-backlog.md`, then the last ~30 Log lines of `automation-state.md`. Its Human Check Queue and Decisions Awaiting Review remain pending.

## The job

Codex works directly in `%USERPROFILE%/Documents/repos/ReplayBuffer`. The former Claude worker-session, SendMessage and Trello instructions are retired for this takeover. Follow the builder's current scope; do not resume the remaining initiative work without their word.

- **Tickets:** `python _bmad/method/scripts/tickets.py next|status|find|mark _bmad-output/initiative-replay-buffer [ref] [state]`.
- **Git:** git flow (AVH); code on `feature/...` or `bugfix/...` from develop. Conventional Commits, lowercase imperative summary of at most 72 characters, and a `Refs: <story>` footer when applicable. Planning artifacts may be committed directly on develop.

## Standing rules (the builder's words are in the state file's top section)

1. **Dev resumed, tests deferred** (builder: "resume dev work. don't run any tests until later. make a backlog of tests to run. Merge to dev when changes are ready").
   - Build with `dotnet build ReplayBuffer.slnx -c Release`, with 0 warnings, but run **no tests**: no default, Heavy, bench or soak runs.
   - QA is **code review only**, and a story merges when its review passes.
   - Every unrun test goes into `test-backlog.md`.
   - Only the builder lifts this rule.
   - The sole exception for the blank-player-controls bug has been used: one focused icon-render test passed. It does not authorize further test runs.
2. **GPU work at night only.** Heavy, GPU, capture, bench and soak runs happen at night, after the builder's go. Tonight's 23:03 timer was **deleted** at the builder's request; nothing is scheduled.
3. **Measurement runs need a quiet PC.** Before any fps or bench run, pause every session's builds and suites. `.bench-running` doesn't stop other sessions. Keep an overlap log.
4. **Merges:** in the main folder `%USERPROFILE%/Documents/repos/ReplayBuffer` on develop, with exactly `git merge --no-ff <branch> -m "<type>(<scope>): merge <name>"`, serialized. Never push; there's no remote.
5. **Never:**
   - touch the installed app (`%LOCALAPPDATA%\Programs\ReplayBuffer`), the builder's real settings or Library, or the builder's Dev App;
   - change Windows, driver, NVIDIA, display or audio settings;
   - trigger a real TDR;
   - kill processes you didn't start; ask the builder instead.
6. **Releases:** v0.8.0, v0.9.0 and v0.10.0 are held until epic 8 finishes. Then cut them in order (or one v1.0.0 if the builder prefers). Use git flow release, and reword merge messages to Conventional Commits.

## Where things stand

- **Clip creator implementation requested:** builder said "start implementation" on 2026-11-05. The [request](feature-requests/clip-creator/clip-creator.md) covers Library/player entry points, Twitch-style range selection, waveform zoom/millisecond controls, MP4/MP3 and separate/mixed tracks. Branch `feature/clip-creator` is cut from develop; [plan-clip-creator.md](plan-clip-creator.md) is draft, awaiting the mandatory BMad plan checkpoint. Investigation is complete; product code has not changed. Plan records an isolated media worker, seekable delete-shared FFmpeg input, matching mix preview, packaging changes and deferred verification. Reconcile the editing boundary/architecture before product code. Other pending stories and the test hold remain unchanged.
- **Done and merged:**
  - epics 1–7, 9, 10 and 13;
  - 11.1–11.5, and 12.1–12.4 with 12.6;
  - 14.1–14.7, merged as 14.2 `de6f653`, 14.3 `6a8340a`, 14.5 `8ed5775`, 14.4 `de23206`, 14.6 `d2273f9`, 14.7 `2a1f913`.
- **Develop:** player fix merged at `2b38a99` (`eecdc8c` on `bugfix/player-blank-controls`); the post-merge Release build passed with **0 warnings, 0 errors**. The subsequent docs commit updates this handoff and the state log. **No full test suite has run since the earlier merges.** See `test-backlog.md` §1, then the 10 night runs in §2.
- **14.9 "Player controls restyled, with full screen" is merged and done** (`a38758d`; its review passed at `4d3066f`). Its blank-controls bug is now fixed:
  - Source diagnosis: Gaming dark's implicit `TextBlock` style assigns Segoe UI, overriding the glyph font inherited by the custom template's `ContentPresenter`. The template now has an explicit `TextBlock` with template-bound font, size, foreground and content; Segoe MDL2 Assets comes first in the font list. Foreground tokens exist in both looks, and the fill triggers do not replace the icon.
  - Both looks visibly render Play, speaker, gear and full screen. The one authorized run, `PlayerViewThemeTests.ThePlayerIconsRenderInBothLooks`, passed (1 test), including all alternate glyphs and a deliberately conflicting implicit text style. It uses a fake player and no engine, real media or GPU surface. The two inspected PNGs are `tests/ReplayBuffer.App.Tests/bin/Release/net10.0-windows10.0.26100.0/player-icons-{fluent,gaming}.png`.
  - Review follow-ups fixed: save/restore `Page.Margin`; leave full screen when the window hides (including close/Alt+F4 to tray); centre "Audio track" and put an accent Path check on the right. The full-screen/video background now shares a player theme token instead of `Brushes.Black`.
  - **Still open:** the gear's possible outside-click close/reopen race. It needs a real pointer/input check; left unchanged and recorded in `test-backlog.md` for the builder/14.8. The broader updated theme/menu/full-screen regression method was written but not run.
- **Builder can see the fix:** rebuild and run their Dev App from develop, then open a clip from the Library. The existing running Dev App, installed app and real data were left alone.
- **Waiting on the builder or the test backlog:**
  - 8.8's 8-hour Disk soak (the Memory soak ran 5 h 9 min; engine growth was 1.87 MB/h against a 2 MB/h limit, so watch it). Then 8.9–8.11.
  - The HITL end-to-end stories 11.6, 12.5 and 14.8.
  - The Human Check Queue.
  - Decisions Awaiting Review: NFR-1's 3 % (clean fps loss 5.46 %), and the others.
- **Candidate follow-ups, not yet tickets:**
  - an `mehd` written at start-up for crash-cut Recordings (14.3's finding; the night seek check decides it);
  - the two Heavy recording-timing questions (§2 rows 9–10).

## When the builder says tests may run again

1. Restate the rule change in the state file.
2. Run `test-backlog.md` §1 (default suites on develop) by day. Any failure goes to a bugfix branch.
3. At night, after the builder's go, run §2 in order, one at a time, with the quiet-PC rule for row 5 (the cost run).
4. Each night pass that clears a story's Heavy check closes its note.
5. Then start 8.9 onward once the Disk soak passes.
