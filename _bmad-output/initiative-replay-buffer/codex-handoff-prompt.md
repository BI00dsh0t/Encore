# Prompt to hand ReplayBuffer to another agent (Codex)

Paste everything below the line.

---

You're taking over **ReplayBuffer**, a personal Windows game-capture app in `%USERPROFILE%\Documents\repos\ReplayBuffer`. It's built in .NET 10, with a WPF App and a NativeAOT engine. It has been built by a pool of agents following a BMad ticket tree. The previous coordinator ran out of tokens.

**Read these first, in order:**
1. `CLAUDE.md` in the repo root: the git flow, test, and commit rules.
2. `_bmad-output/initiative-replay-buffer/coordinator-handoff.md`: the current state, rules in force, and what's open.
3. `_bmad-output/initiative-replay-buffer/test-backlog.md`: every deferred test.
4. The last ~30 lines of `_bmad-output/initiative-replay-buffer/automation-state.md`: the recent log.

Ignore the parts about Claude worker sessions, SendMessage and Trello. You work directly in the repo.

## Rules the builder has set (keep them)

- **Branching:** git flow (AVH). Code changes go on `feature/…` or `bugfix/…` branches cut from `develop`, and merge back with `git merge --no-ff <branch> -m "<type>(<scope>): merge <name>"`.
- **Commits:** Conventional Commits: lowercase imperative summary of 72 characters or fewer, and a `Refs: <story>` footer when there is one.
- **Never push.** There's no remote.
- **No tests for now.** The builder said: "don't run any tests until later. make a backlog of tests to run." Build with `dotnet build ReplayBuffer.slnx -c Release`; it must have 0 warnings. Write tests, but don't run them; add them to `test-backlog.md`. GPU, Heavy, bench and soak runs happen only at night, and only when the builder says go.
- **Never touch:**
  - the installed app in `%LOCALAPPDATA%\Programs\ReplayBuffer`, the builder's real settings or Library, or their running Dev App;
  - Windows, driver, NVIDIA, display or audio settings.
- Don't kill processes you didn't start.

## Your first task: bug "player controls show as blank rectangles"

The in-app player is epic 14. Story 14.9 (merged at `a38758d`) restyled its control bar after an approved mockup (https://claude.ai/artifact/4CCgWPDzuYpDAxFJWsZdxz):
- a thin seek bar;
- Play/Pause, the speaker with a hover volume slider, and the time;
- a gear menu (Audio track, Open in default player) and full screen.

The builder ran it, and **the control buttons render as blank rectangles**.

**The likely cause:** the buttons draw icon-font glyphs:
- `PlayerPage.cs` has `PlayPauseGlyph` (`\uE768`/`\uE769`), `MuteGlyph` (`\uE767`/`\uE74F`) and `FullScreenGlyph` (`\uE740`/`\uE73F`);
- the gear in `PlayerView.xaml` uses `&#xE713;`.

Their font is `RbPlayerGlyphFont` = `"Segoe Fluent Icons, Segoe MDL2 Assets"` (`src/ReplayBuffer.App/Theme/LibraryTheme.xaml`, about line 345), used by `RbPlayerIconButtonStyle` and `RbPlayerIconToggleStyle` (about lines 387–430). **This PC doesn't have Segoe Fluent Icons;** `C:\Windows\Fonts` has only `segmdl2.ttf`. Confirm the cause before fixing. Also rule out:
- a template whose ContentPresenter doesn't get the font;
- a Foreground key missing in one theme;
- the subtle-fill border drawing with no glyph.

**What to do:**
1. `git checkout develop`. A partial branch `bugfix/player-blank-controls` may exist from the previous worker; check it, and reuse or discard it. Then create or continue `bugfix/player-blank-controls`.
2. Fix it so the icons always draw. Either:
   - put **Segoe MDL2 Assets first**, or use whatever font the rest of the App already uses for its icons (search the App for `Segoe MDL2` and `FontFamily`); or
   - draw the icons as WPF `Path` geometry, so they don't depend on a font.

   Both looks (Fluent and Gaming dark), theme tokens only.
3. You may render the player once to confirm the icons now show. That's the only exception to the no-tests rule. The App tests' `PlayerViewThemeTests` renders `player-*.png` beside the test binary. Don't run the whole suites.
4. Build Release with 0 warnings, and commit as `fix(app): ...`. Merge into `develop` with `--no-ff` and the message `fix(app): merge player-blank-controls`, then build develop again.
5. Add a line to the end of `automation-state.md`'s Log and update `coordinator-handoff.md`. Commit those on develop as `docs(bmad): ...`.

## Also open, from 14.9's review (small; do them if quick, else note them)

1. Clicking the gear while its menu is open may close the menu and then reopen it (`PlayerView.xaml.cs` about 404–416).
2. `Page.Margin` is hardcoded in full screen (`MainWindow.xaml.cs` about 646); save and restore it instead.
3. Alt+F4 sends the window to the tray while in full screen, and it comes back still in full screen. Leave full screen when the window hides.
4. To match the mockup: centre the gear menu's "Audio track" heading, and show the tick as an accent check on the right (`LibraryTheme.xaml` about 829).

## After that

Report to the builder what you changed and how they can see it: rebuild and run the Dev App, open a clip from the Library. Everything else (the night test backlog, the 8.8 Disk soak, epic 8's last stories, and the HITL end-to-end stories 11.6, 12.5 and 14.8) waits for the builder. Don't start it without their word.
