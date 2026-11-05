# Coordinator handoff (2026-11-05, evening)

Read this first, then `automation-state.md` (the pool table, Queue, Human Check Queue, Decisions Awaiting Review, and Log; its last ~40 Log lines are the recent story) and `test-backlog.md`.

## The job

You coordinate the ReplayBuffer initiative, built unattended by a pool of Claude Code sessions. You don't write product code. You brief workers, send each story to a QA or review worker, merge into develop, mark tickets, keep the state file and the Trello board in step, and commit each step on develop (Conventional Commits, `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`).

- **Tickets:** `python _bmad/method/scripts/tickets.py next|status|find|mark _bmad-output/initiative-replay-buffer [ref] [state]`.
- **Trello:** board ids are in `trello-board.md`. Lists: Backlog `6ae9fa34de1908c24b0712be`, In progress `6ae9fa36873f9ca073071568`, In QA `6ae9fa379a75786dc5c08722`, Done `6ae9fa383e33708cc8d74fb3`. Card and list ids use the form `ari:cloud:trello::card/workspace/6ae9f97af5eb2665e52529f6/<id>` (and `…::list/…/<id>`). Labels: red (high risk) `6ae9fa1c9105c93239e2ce69`, purple (HITL) `6ae9fa1c9105c93239e2ce6a`.
- **Workers:** message them with SendMessage, using `to` = the session id. Reset a worker before a new job with `clear_session`; if that's refused, reuse the session and say "new job" in the brief. Set the model (`claude-opus-5-5` for builds, `claude-sonnet-5-5` for QA and review), effort, and title through the `ccd_session_mgmt` tools.

## Standing rules (the builder's words are in the state file's top section)

1. **Dev resumed, tests deferred** (builder: "resume dev work. don't run any tests until later. make a backlog of tests to run. Merge to dev when changes are ready").
   - Workers build with 0 warnings but run **no tests**: no default, Heavy, bench or soak runs.
   - QA is **code review only**, and a story merges when its review passes.
   - Every unrun test goes into `test-backlog.md`.
   - Only the builder lifts this rule.
2. **GPU work at night only.** Heavy, GPU, capture, bench and soak runs happen at night, after the builder's go. Tonight's 23:03 timer (a session-only cron, lost with this session) is **held** by rule 1.
3. **Measurement runs need a quiet PC.** Before any fps or bench run, pause every session's builds and suites. `.bench-running` doesn't stop other sessions. Keep an overlap log.
4. **Merges:** in the main folder `%USERPROFILE%/Documents/repos/ReplayBuffer` on develop, with exactly `git merge --no-ff <branch> -m "<type>(<scope>): merge <name>"`, serialized. Never push; there's no remote.
5. **Never:**
   - touch the installed app (`%LOCALAPPDATA%\Programs\ReplayBuffer`), the builder's real settings or Library, or the builder's Dev App;
   - change Windows, driver, NVIDIA, display or audio settings;
   - trigger a real TDR;
   - kill processes you didn't start; ask the builder instead.
6. **Releases:** v0.8.0, v0.9.0 and v0.10.0 are held until epic 8 finishes. Then cut them in order (or one v1.0.0 if the builder prefers). Use git flow release, and reword merge messages to Conventional Commits.

## Where things stand

- **Done and merged:**
  - epics 1–7, 9, 10 and 13;
  - 11.1–11.5, and 12.1–12.4 with 12.6;
  - 14.1–14.7, merged as 14.2 `de6f653`, 14.3 `6a8340a`, 14.5 `8ed5775`, 14.4 `de23206`, 14.6 `d2273f9`, 14.7 `2a1f913`.
- **Develop:** at `0841503` or later; it builds Release with 0 warnings. **No test suite has run since these merges.** See `test-backlog.md` §1, then the 10 night runs in §2.
- **In progress: 14.9 "Player controls restyled, with full screen."** Worker story-2-7 (`local_12351eee-812a-4dcb-8f13-6b288e96bce5`) is on `feature/14.9-player-restyle`. The spec is the builder-approved mockup at https://claude.ai/artifact/4CCgWPDzuYpDAxFJWsZdxz: two boards, gaming and Fluent; a thin seek bar; Play, speaker with hover slider, time; a gear menu (Audio track and Open in default player) and full screen. No logo button. Expect "READY FOR REVIEW: 14.9 at <sha>". Then send it to a review worker (Sonnet; code review only, no tests), merge, mark it done, move card `6aed0412e7856cbfde24bfc7` to Done, and add its unrun tests to `test-backlog.md`.
- **Waiting on the builder or the test backlog:**
  - 8.8's 8-hour Disk soak (the Memory soak ran 5 h 9 min; engine growth was 1.87 MB/h against a 2 MB/h limit, so watch it). Then 8.9–8.11.
  - The HITL end-to-end stories 11.6, 12.5 and 14.8.
  - The Human Check Queue.
  - Decisions Awaiting Review: NFR-1's 3 % (clean fps loss 5.46 %), and the others.
- **Candidate follow-ups, not yet tickets:**
  - an `mehd` written at start-up for crash-cut Recordings (14.3's finding; the night seek check decides it);
  - the two Heavy recording-timing questions (§2 rows 9–10).

## The pool (session ids)

All are idle except story-2-7.

| Name | Session id | Usually |
|---|---|---|
| story-2-7 | `local_12351eee-812a-4dcb-8f13-6b288e96bce5` | Opus; App and player builds (14.9 now) |
| qa-2-1 | `local_e7074a28-6407-45d9-9f57-3ac6d03c4a4f` | Opus; engine and capture (11.x, 12.x); owns the 11.5, 12.1 and 12.2 Heavy runs |
| slice-epic-4 | `local_bddcb996-00d5-440b-abab-994cff2c6899` | Opus; 14.3 (`mehd`); owns its Heavy runs and the crash-cut check |
| slice-epic-5 | `local_1d2ad9da-a155-47ef-9312-64143e709751` | Opus; 8.8 soaks (`feature/8.8-soaks`, Disk soak command in its plan) |
| qa-1-14 | `local_6dea16b4-fdf6-4dc5-bb4e-994e666dee97` | Opus; epic 10 stories |
| slice-epic-8 | `local_72b7980a-c079-4b93-949b-0a98dc8cee69` | Sonnet; QA and code review |
| 2-1-tracer-track-layout | `local_9c35886e-1667-4c7b-996a-23e8da89130b` | Sonnet; QA and review (`clear_session` is often refused; reuse it with "new job") |

## When the builder says tests may run again

1. Restate the rule change in the state file.
2. Run `test-backlog.md` §1 (default suites on develop) by day. Any failure goes to a bugfix branch.
3. At night, after the builder's go, run §2 in order, one at a time, with the quiet-PC rule for row 5 (the cost run).
4. Each night pass that clears a story's Heavy check closes its note.
5. Then start 8.9 onward once the Disk soak passes.
