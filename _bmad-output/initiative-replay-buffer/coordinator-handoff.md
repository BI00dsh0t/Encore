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

- **Taller waveform tracks merged:** builder requested more height per track. Lane height 24 → 40 pixels, proportional peaks and centered labels; existing lane scrolling and responsive player preserved. Feature `4a1fb9296a29c8c239ba227385b4897fbbf19330`, no-ff develop merge `aa92384adb2403d6ac76b6a8a63900fea1c0fd96`. Focused ClipCreatorTests **15 passed, 0 failed/skipped**; feature/post-merge Release **0 warnings/errors**. Ordinary diff review and Gaming expanded fake-media screenshot inspection clear. Existing real-media/high-DPI/HITL restrictions persist; app/data untouched, no push. Rebuild/restart Dev App to see it.

- **Per-track waveform amplitude scaling implemented:** builder approved `plan-clip-creator-waveform-amplitude-scaling.md` after requesting BMad planning only; implementation used ordinary coding/review. Feature `5873727d6f9d4fae83a0657e9061f51f4247b19d`, no-ff develop merge `aaa9fc82e7f625c4cf730c875c2b7c2fe9f7ca13`. Each track's cached whole-source maximum scales display peaks, stable across zoom/pan/inclusion; silence remains flat. Cache bytes, source, playback/export, renderer style and worker unchanged. Final scoped ClipCreatorTests **15 passed, 0 failed/skipped**; feature and post-merge Release builds **0 warnings/errors**; ordinary diff review clear. Existing real-media/Heavy, resource/publish, high DPI and HITL checks remain deferred. Rebuild/restart Dev App from develop; app/data untouched, no push.

- **Waveform seeking/cache and responsive player implemented:** feature `c83ccecf43219c21adf53f97eda7939716043e2e`, no-ff develop merge `53872864d6fbffef9930cc227b290e98d6b0983a`. Separate draggable ruler playhead and keyboard seeking preserve trim; full-source 1 ms peaks are prepared once per creator into an owned disk cache with bounded viewport reads; routine loading messages removed. Picture grows with available window height; small editors scroll. AD-28 amended before implementation. Review findings on persistent errors and hide cancellation fixed; follow-up clear. Final focused ClipCreatorTests **14 passed, 0 failed/skipped**; feature and post-merge Release builds **0 warnings/errors**. Fake-service both-look screenshots inspected. Initial preparation still takes time; actual worker media fidelity/resource use, publish/NativeAOT, high DPI and HITL remain pending in test-backlog.md. See [plan-clip-creator-waveform-interaction.md](plan-clip-creator-waveform-interaction.md). Supersedes repeated viewport decoding/fixed player height. Rebuild/restart Dev App from develop; running app/data left untouched. No push or broader test clearance.

- **Approved control styling implemented and merged:** feature `68899fba035dc7494cf30aae013959899875ce26`, no-ff develop merge `7d3c1fe205365c2d2167ede2f0158afeea2fa56d`. Finishes UX R10/R11: compact scoped templates/vector playback and timeline icons, joined Export/menu/rounded popup, integrated In/Out/nudges/duration and responsive groups, rounded focus-aware/dimmed handles, non-overlapping ruler labels, softer sidebar/Settings. Crossed handle drags preserve the last valid range. All four quick-review findings fixed and follow-up clear. Final ClipCreatorTests **11 passed, 0 failed/skipped**; feature and merged Release builds **0 warnings/errors**. Fake-service both-look expanded/rail/narrow and popup PNGs inspected; real media, shared-shell/Settings execution, high DPI and full pointer/state HITL remain pending. See [plan-clip-creator-control-styling.md](plan-clip-creator-control-styling.md) and newest backlog section. Builder: rebuild/restart the Dev App from develop to see the changes; running app/data untouched. This supersedes earlier spec-only/unscheduled wording for the implemented UX pass. No push or expanded test clearance.

- **Clip creator/navigation UX spec recorded:** builder liked the interactive mockup, added expandable/icon-only global navigation and a dedicated bottom Settings button, then requested a BMad spec. See [feature-requests/clip-creator-ux-pass/clip-creator-ux-pass.md](feature-requests/clip-creator-ux-pass/clip-creator-ux-pass.md), its `experience.md`, source input and preserved `mockup.html`. Eleven requirements, including mockup-matching control styling (R10) and compact In/Out/duration controls with integrated nudges and waveform handles (R11); larger player, compact waveform, icon zoom/pan, split audio/video export with relevant popup, sidebar toggle and separate Settings. Two assumptions are marked: expanded first-use/sidebar state retained during the session, and popup choices retained on dismissal to preview before export. Unscheduled and unsliced; no implementation or test-clearance expansion. Existing media behavior, recent QA fixes and all standing safety/test restrictions remain binding.

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

## Clip creator UX implementation checkpoint (2026-11-05)

- Integrated into `develop`: feature `8898f2c8e3899b06168b5559a8f98fca75394004`, no-fast-forward merge `4dfbebaa8c55f5fb3415a5317d74962b9bf87e07`. Quick review completed; both findings fixed and confirmed by follow-up inspection. No push.
- Editor: larger 480 px aspect-preserving player (including its transport), compact symbol pan/zoom tools, retained type/options dialog, conditional progress/cancel/status, 24 px waveform lanes bounded to five visible lanes, scrollbar and Up/Down/PageUp/PageDown lane access. Selection and ruler use the same source-time mapping after resize; constrained gutters are safe below 100 px.
- Export: opening options/type selection never chooses a destination or encodes. Name/track/mix edits survive dismissal; Save uses an injectable guarded chooser. MP4/mixed MP3 uses a file picker; separate MP3 uses a parent-folder picker plus the retained new directory name. Existing worker contract uses a directory even for one separate MP3; the dialog describes this explicitly. No worker/codec/media semantics changed.
- Shell: expanded state starts true and lasts through navigation in the session; 58 px icon rail survives resize/full-screen restoration, with bottom Settings and ordered existing Settings sections inside Settings. Existing routes and drill-in lifecycle remain in the model. Explicit glyph TextBlocks avoid the previous Gaming implicit-font issue.
- Verification: `dotnet build ReplayBuffer.slnx -c Release` passes with **0 warnings, 0 errors** after the final code/test edits. Sandbox-only build initially failed on Windows SDK registry access; escalated builds succeeded.
- `dotnet test --project tests/ReplayBuffer.App.Tests -c Release --no-build --filter-class ReplayBuffer.App.Tests.ClipCreatorTests --explicit off`: final **10 passed, 0 failed, 0 skipped**. Covers retained options/focus/type menu, chooser cancellation/duplicate guards, preview parity, both-look compact many-lane layout, narrow-gutter safety, existing precision/entry/lifecycle checks. An initial 8/9 run caught trigger focus blocked by refresh; fixed and rerun green. The integrated reference test then caught 137 px overflow; compacted duplicated toolbar text/rows and moved Back into the creator while hiding the duplicate shell breadcrumb. Final fake-service actual-Library-host test passes with a 960×900 client area, both looks and both rail widths: zero creator scrolling, 76 px two-lane waveform and ≥390 px allocated picture area. A null fake-player Image assertion was corrected to check its allocated picture row.
- `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build --filter-class ReplayBuffer.Engine.Tests.ClipMediaWorkerTests --explicit off`: **1 passed, 0 failed, 2 Explicit skipped** (identity validation executed).
- Attempted `SharedMediaProcessTests --explicit off` **did not execute**: automatic approval review rejected it, stating the class was not clearly clip-creator-specific and could conflict with the scope restriction. No bypass/retry. The implementation coordinator confirmed this unchanged quoting class need not repeat; its four prior passing theory rows remain historical evidence, not a new run.
- Deferred: newly written `MainWindowTests.NavigationWidthRetainsCurrentViewAndSettingsUsesASeparateBottomRoute` and `SurfacesThemeTests.BottomSettingsAndPrimaryIconsResolveInBothNavigationWidthsAndLooks` (compiled only); all other shell/Settings classes, full-screen/state-under-work integration, high scale, native chooser interaction, pointer/keyboard HITL, actual media/Heavy/night checks, publishes, benches and soaks. No installed/running app, real Library/settings or unowned process touched.

### UX pass integration result (2026-11-05)

- Final feature: `8898f2c8e3899b06168b5559a8f98fca75394004`; develop no-ff merge: `4dfbebaa8c55f5fb3415a5317d74962b9bf87e07`. Plan built; quick review's two findings fixed and follow-up inspection clear.
- Coordinator final ClipCreatorTests: **11 passed, 0 failed, 0 skipped**. Includes minimum 480×320 shell timeline-action reachability in both looks. Feature and post-merge Release builds: **0 warnings, 0 errors**. Engine identity remains 1 pass/2 Explicit skips from implementation-worker run.
- Added Settings reselection regression is compiled but unrun, alongside the two other new shell tests. Pending-work navigation/full-screen, high scale, actual chooser, pointer/keyboard HITL and real media/Heavy acceptance remain deferred in test-backlog.md. The quoted historical 10-case worker result above precedes the review regression.
- To see it: rebuild/run the Dev App from develop, open a clip from the Library or player via Create clip; inspect the split Export video/audio options and sidebar toggle/bottom Settings. The builder's running Dev App and real data/settings remain untouched. No push or further epic/backlog work started.
