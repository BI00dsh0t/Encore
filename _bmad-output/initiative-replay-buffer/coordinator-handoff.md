# Coordinator handoff (2026-11-05, Codex takeover)

Read root `CLAUDE.md`, this file, `test-backlog.md`, then the last ~30 Log lines of `automation-state.md`. Its Human Check Queue and Decisions Awaiting Review remain pending.

## The job

Codex works directly in `%USERPROFILE%/Documents/repos/ReplayBuffer`. The former Claude worker-session, SendMessage and Trello instructions are retired for this takeover. Follow the builder's current scope; do not resume the remaining initiative work without their word.

- **Tickets:** `python _bmad/method/scripts/tickets.py next|status|find|mark _bmad-output/initiative-replay-buffer [ref] [state]`.
- **Git:** git flow (AVH); code on `feature/...` or `bugfix/...` from develop. Conventional Commits, lowercase imperative summary of at most 72 characters, and a `Refs: <story>` footer when applicable. Planning artifacts may be committed directly on develop.

## Standing rules (the builder's words are in the state file's top section)

**Current scoped exception (2026-11-05):** builder said "you are cleared to run tests again", then "only run tests for the clip creator". Focused QA may build Release and run only non-Explicit `ClipCreatorTests`, `ClipMediaWorkerTests` (identity validation), and `SharedMediaProcessTests` (quoting), one project at a time. This includes fake-service both-look layout/focus checks. The blanket hold below still applies outside clip creator. Explicit/Heavy real-media/shared-handle checks still require the separate night/go clearance; no old backlog, HITL, publish, install or real Dev App work is authorized.

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

- **Clip creator focused QA passed, opening crash fixed:** fix `dd58b7b06aa931d18057835e9b676d1465a64d1c`, merged with `--no-ff` into develop at `ec5064d1157376e655da4e01dc1f37f8101ef944`. The builder's Create clip exception was reproduced in both view tests: progress defaulted to TwoWay against private-set Progress; binding is now OneWay. Also fixed a 1 ms nudge displaying the preceding millisecond by rounding display time only. Final App 7, Engine identity 1 and Windows quoting 4 passed (12 total, 0 failed); 3 Explicit/Heavy cases skipped. Independent quick review: no findings. Post-merge Release build: 0 warnings/errors. [qa-clip-creator.md](qa-clip-creator.md) records exact commands/counts, initial failures and remaining gaps. Accept the UI bugfix/default checks; real-media/export acceptance stays provisional pending separate clearance. Builder can restart their Dev App from develop; QA left it untouched. This result supersedes the implementation-time deferred-default statements below; Heavy/media/publish/HITL remain pending.

- **Clip creator implemented and merged:** builder approved [plan-clip-creator.md](plan-clip-creator.md), now built, for [the request](feature-requests/clip-creator/clip-creator.md). Feature commit `6cbffa9dd262880e012b244d21e2294e0917acea`; no-fast-forward merge `60104c13eddc72abfb5c15fbf369ad8bf1d37d29` (`feat(app): merge clip-creator`). Includes Library/player entry points, precise waveform selection/zoom, named separate/mixed MP4/MP3 export, matching selection preview, isolated worker and packaging changes. MP3 retains sample boundaries independently of MP4 frame alignment. AD-28 records ownership and the editing exception. Feature and post-merge develop Release builds passed with 0 warnings/errors; independent quick review and follow-up passed after four UI/lifecycle patches. All tests/media/UI/publish checks remain deferred in `test-backlog.md`; other pending stories stay on hold. Builder can rebuild/restart their Dev App, select a finished Library item or open it in the player, and choose Create clip. The running Dev App was left alone.
- **Done and merged:**
  - epics 1–7, 9, 10 and 13;
  - 11.1–11.5, and 12.1–12.4 with 12.6;
  - 14.1–14.7, merged as 14.2 `de6f653`, 14.3 `6a8340a`, 14.5 `8ed5775`, 14.4 `de23206`, 14.6 `d2273f9`, 14.7 `2a1f913`.
- **Develop:** clip creator merged at `60104c13eddc72abfb5c15fbf369ad8bf1d37d29`; the post-merge Release build passed with **0 warnings, 0 errors**. Subsequent docs commits update this handoff and the state log. **No full test suite has run since the earlier merges.** See `test-backlog.md` §1, the 10 night runs in §2, and the deferred clip-creator checks.
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

The current clearance is clip creator only; do not execute the general resumption sequence below under that exception.

1. Restate the rule change in the state file.
2. Run `test-backlog.md` §1 (default suites on develop) by day. Any failure goes to a bugfix branch.
3. At night, after the builder's go, run §2 in order, one at a time, with the quiet-PC rule for row 5 (the cost run).
4. Each night pass that clears a story's Heavy check closes its note.
5. Then start 8.9 onward once the Disk soak passes.
