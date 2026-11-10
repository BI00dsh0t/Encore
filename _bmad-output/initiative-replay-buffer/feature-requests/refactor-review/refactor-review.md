---
type: epic
title: "Final refactor review"
parent: initiative-replay-buffer
covers: []
sources:
  - _bmad-output/initiative-replay-buffer/feature-requests/refactor-review/request.md
after: []
risk: high
---

# Final refactor review

Status: plan written 2026-11-10. The builder answered the decisions the same day (see Decisions). **Phases 0 and 1 are done** (merges `948448a` and `188f3d4`); phase 2 is next.

**Phase 0 as built:**
- The 24 leftover `ReplayBuffer.*` folders are deleted. They held only `bin` and `obj`.
- **Baseline** (default suite at `60d293e`, one project at a time): 1,258 tests, 1,152 passed, 104 skipped, 2 failed. Ten of the eleven projects pass. The two failures are in `Encore.App.Tests` and are stale tests, not product bugs: `MainWindowTests.CurrentSettingsSectionReselectionClosesDrillInWhileEscapeOnlyDismissesTheMenu` and `ShellTests.TheShellLoadsInBothThemesAndTheNavOpensEveryPage` still expect the nav layout from before Settings became its own button. **Phase 2 fixes both first**, so the App suite is a clean safety net.
- The build now fails on unused usings, unused private members and unused parameters (`.editorconfig`, `Directory.Build.props`). It found little: 37 usings, 2 constants, 1 test parameter and 6 broken doc references. These checks don't see unused `internal` or `public` members, so each later phase still looks for those by hand.
- `.editorconfig` carries the 140-character guide, and `CLAUDE.md` has a Code style section with the "done" rules.

**Phase 1 as built** (29 commits, merge `188f3d4`):
- Every comment in the `.cs` and `.xaml` files of `src`, `tests` and `bench` is one line. Comment lines in `src` fell from 7,830 to 3,452, and the ticket ids are gone.
- The rules that need more room moved to `docs/code-notes.md`, with one heading per class.
- No code changed: the session and the coordinator each compared every file with its comments stripped, before and after, and all 430 changed files match. Release build 0 warnings; no tests (low risk).
- Left over: six ticket ids in `.csproj` comments, which were outside the phase's file types. Phase 8 clears them.

## Goal

Make the code easy for a junior developer to read and change, with no change in behaviour.

The builder's priorities, in order:

1. Remove redundant code.
2. Reduce repeated code.
3. Make the code more readable.
4. Classes follow SOLID.
5. Business logic lives outside controls.
6. Comments are one line and simple.
7. Fix anything that would confuse a junior developer.
8. Split messy logic into named helper functions.

## What the code looks like today

Measured on `develop` at `2b6f095`. `src` holds about 52,900 lines of C# and 6,000 of XAML.

| Signal | Number | Where it is worst |
|---|---|---|
| Files over 1,000 lines | 5 | `CaptureSession.cs` 3,349; `SessionOrchestrator.cs` 2,775; `LibraryActor.cs` 2,117; `LibraryPage.cs` 1,773; `TrackLayoutPage.cs` 1,074 |
| Methods of 100 lines or more | about 12 | `EngineHost.Start` 558; `CommandLine.ParseCapture` 258; `CaptureSession.ClipWrite` 227, `Recover` 185, `Restart` 166, `TearDownLost` 134; `SessionOrchestrator.Handle` 183; `AudioDeviceHub.Reconcile` 137 |
| Methods of 60 lines or more | about 50 | 10 each in `CaptureSession` and `SessionOrchestrator` |
| Types in one file | up to 27 | `LibraryActor.cs` 27; `SessionOrchestrator.cs` 26; `SettingValues.cs` 21 |
| Private fields in one class | up to 65 | `LibraryPage` 65; `CaptureSession` 59; `LibraryActor` 52 |
| Comment lines | 7,830 (15 %) | 812 comment blocks run past 3 lines |
| Ticket ids in comments (`Story 10.5`, `AD-23`, `T-04`) | 680 | everywhere |
| Test-only hooks in shipping classes | 18 | `CaptureSession` options, 13 |
| Copied XAML | 41 shared blocks | `ShellFluent.xaml` and `ShellGamingDark.xaml` |
| Code-behind with real logic | 5 files | `PlayerView.xaml.cs` 496 lines; `LibraryView.xaml.cs` 382; `MainWindow.xaml.cs` 267 |
| Lines over 160 characters | 340 | over 100 XAML lines hold several elements each (`LibraryTheme.xaml`, `ClipCreatorView.xaml`) |

Exact copy-paste between C# files is rare. The repeated code is mostly *similar* code: the settings pages, the capture sources, and the two session classes each repeat a pattern in slightly different words.

Three things would confuse a junior at first sight:

- The classes named `…Page` in the App are view models, not pages.
- 137 resource keys start with `Rb`, which stood for ReplayBuffer.
- The folders `src\ReplayBuffer.*`, `tests\ReplayBuffer.*` and `bench\ReplayBuffer.*` still exist. They hold only old build output and nothing git tracks.

## Rules for every phase

- **No behaviour change.** A refactor that needs one becomes a note in the findings, not a code change.
- **Contracts stay wire-compatible.** No message, setting or error code changes shape or name on the wire (AD-22).
- **The layers stay where they are.** Core has no platform code, the App talks to the engine only through Contracts, and theming uses tokens only (AD-23).
- **Review first, then change.** Each phase writes its findings to `findings-<phase>.md` in this folder before it edits code. Each finding names the file, the problem and the fix.
- **One class or file per commit**, typed `refactor(<scope>)`, so any one change is easy to revert.
- **One feature branch per phase:** `git flow feature start refactor-<n>-<slug>`.
- **Build with 0 warnings** after every commit.

### What "done" means for a file

- No method is over about 50 lines, unless splitting it would make it harder to read.
- No nesting deeper than 3 levels; guard clauses and helpers replace the rest.
- One public type per file, except small records that belong to one message group.
- A class has one reason to change. If its summary needs "and", it is split.
- No comment is longer than one line. A comment says *why*, in plain words; the code says *what*.
- No ticket ids in comments. The commit history keeps them.
- Names are whole words. No abbreviation a new reader has to look up.
- No dead code: no unused members, parameters, `using`s or switches.
- No logic in a control or code-behind beyond wiring events to a view model.

## Phases

The order runs from low risk to high risk, so the riskiest files are changed last and with the most care.

### Phase 0: baseline and housekeeping (small, low risk)

- Delete the leftover `ReplayBuffer.*` folders under `src`, `tests` and `bench`, after listing what is in them.
- Turn on the compiler's own checks for unused code (IDE0051, IDE0052, IDE0060, CS8019) as warnings, and fix what they find. This covers most of priority 1 mechanically.
- Add an `.editorconfig` line-length guide of 140 and record the "done" rules above in `CLAUDE.md`.
- Run the default test suite once to get a baseline, so a later failure can be told apart from one that was already there.

### Phase 1: comments, whole solution (medium size, low risk)

- Cut every comment and `<summary>` to one plain line.
- Remove the 680 ticket ids.
- Delete comments that only restate the code.
- Where a long comment explains a hard rule (thread ownership, ordering, a Windows quirk), keep one line and move the rest into a well-named helper or a short section of `docs/`.
- No code moves in this phase, so the diff is comments only.

### Phase 2: App views and controls (medium size, medium risk)

Scope: `Encore.App` XAML, code-behind and custom controls.

- Move logic out of `PlayerView.xaml.cs`, `LibraryView.xaml.cs`, `MainWindow.xaml.cs`, `TrackLayoutView.xaml.cs` and `ClipCreatorView.xaml.cs` into their view models or small services.
- Do the same for the controls that decide things: `WaveformView`, `MonitorPicker`, `FolderPicker`.
- Merge the copied nav styles of `ShellFluent.xaml` and `ShellGamingDark.xaml` into one shared template; each theme keeps only the values that differ.
- Reformat the XAML that puts several elements on one line.

### Phase 3: App view models (large, medium risk)

Scope: `LibraryPage`, `TrackLayoutPage`, `ClipCreatorPage`, `PlayerPage`, the settings pages, `MainViewModel`, `EngineConnection`, `ShellApp`.

- Split `LibraryPage` (65 fields) by job: the list and its filters, selection, thumbnails, and the commands that act on Clips.
- Find what the settings pages repeat (drafts, dirty checks, saving) and pull it up into `SettingsPage` or a small helper, so each page holds only its own fields.
- Take `System.Windows` types out of the view models where a plain type does the job.
- Split `EngineConnection` into connecting, sending and dispatching replies.

### Phase 4: engine start-up and command line (medium size, medium risk)

Scope: `EngineHost`, `CommandLine`, `Program`, `AppSupervisor`, `ClipMediaWorker`.

- Break `EngineHost.Start` (558 lines, 12 nullable locals) into one builder method per service, with a single place that disposes what was built if a later step fails.
- Break `CommandLine.ParseCapture` (258 lines) into one small parser per option group.

### Phase 5: Core sessions and Library (large, high risk)

Scope: `SessionOrchestrator`, `LibraryActor`, `TargetGames`, `PacketRing`, `FeedbackRouter`.

- Move the 26 and 27 nested types out to their own files.
- Replace the long `Handle` switches with one handler method per message.
- Split each actor's state from its message handling, so the state rules can be read on their own.

### Phase 6: platform projects (medium size, medium risk)

Scope: `Platform.Audio`, `Platform.Capture`, `Platform.FFmpeg`, `Platform.Windows`, `Media`.

- Give `WindowCapture`, `DesktopDuplicationCapture` and `CompositeCapture` one shared base or helper for the blocks they repeat; the findings name them.
- Do the same for `TonePlayer` and `WasapiCapture`, and for `DurableOutput` and `SharedInput`.
- Wrap each raw native call sequence in a named helper, so the calling code reads as steps.

### Phase 7: `CaptureSession` (large, high risk)

Scope: `CaptureSession.cs` (3,349 lines), `EncoderChain`, `ReplayRings`.

- Split by job: opening, the frame loop, restart, GPU-reset recovery, Clip writing and ending each become their own class or partial file.
- Move the 13 test-only hooks behind one `ICaptureFaults` seam, so the shipping options no longer list them.
- This is the hot path. It changes only after phases 0 to 6 have landed, and it needs a Heavy test run at night before it merges.

### Phase 8: the junior read-through (small, low risk)

- Read every project top to bottom as a new starter, using a fresh session with no project memory.
- Fix or list anything still unclear.
- Write a one-page `docs/code-map.md`: what each project is for, how a frame gets from the game to a Clip, and the naming rules.

## How it runs

- One session per phase, working through the usual pool, one phase at a time so no two sessions edit the same files.
- Each phase ends with a code review of its diff against the "done" rules, as QA does today.
- **Tests run after every phase of medium risk or above** (phases 2 to 7): the default suite of each project the phase touched, one project at a time, by day. Phases 6 and 7 touch the capture, encode and audio path, so they also get a Heavy run at night before they merge. A failure that the baseline didn't have blocks the merge.
- Low-risk phases (0, 1 and 8) build with 0 warnings and run no tests.
- Rough size: phases 0, 1 and 8 are a session each; phases 2, 4 and 6 are one or two; phases 3, 5 and 7 are two or three.

## Decisions (builder, 2026-11-10)

1. **Tests:** "run tests after anything medium risk or above". Each phase heading carries its risk, and "How it runs" says which tests run when. The no-tests rule still holds for low-risk phases.
2. **Ticket ids in comments:** not answered, so the recommended default stands: remove all 680 in phase 1.
3. **The `Rb` prefix:** rename the 137 resource keys to `Encore…` in phase 2, and update `DESIGN.md` to match.
4. **The `…Page` view models:** rename the 12 classes to `…ViewModel` in phase 3.
5. **SOLID:** "follow solid". Classes are split by responsibility, are open to extension through the existing seams, and depend on abstractions where they cross a layer. An interface is added where a class depends on a concrete type from another layer.
