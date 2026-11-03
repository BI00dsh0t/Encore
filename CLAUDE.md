# ReplayBuffer

## Git workflow: git flow (AVH)

- `master` holds released versions only. Never commit to it directly.
- `develop` is the integration branch. BMad planning artifacts (`_bmad-output/`, `PLAN.md`) may be committed to it directly.
- **Code changes always go on a feature branch** cut from `develop` with `git flow feature start <name>`, and merge back with `git flow feature finish` (`--no-ff`).
  - BMad stories are named `<epic id>.<entry id>-<short-slug>`, e.g. `git flow feature start 1.1-tracer`, which creates `feature/1.1-tracer`.
  - A story's plan file (`*-plan.md` beside its ticket) is committed on its feature branch.
- **Bug fixes against `develop`:** `git flow bugfix start <name>`.
- **Hotfixes to a release:** `git flow hotfix start <version>`.
- **Releases:** `git flow release start <version>`, then `git flow release finish <version>`, which tags `v<version>`. Cut a release when a usable milestone lands, e.g. the end of an epic.
- Don't push unless asked; there is no remote yet.
- The git-flow settings live in `.git/config`. On a fresh clone, run `git flow init -d` and `git config gitflow.prefix.versiontag v`.

## Tests

- **Lean tests.** This is a personal project with no end users. A suite only proves the feature works: one or two happy-path tests per feature or AC, plus a test for any bug that really bit. No exhaustive edge-case tables, no duplicate checks across layers, no long real-time waits, and no benchmark-style bounds in the default run.
- **Heavy tests are opt-in.** Slow, hardware-bound or flaky tests (GPU capture and encode matrices, real-time latency, drift and allocation bounds, long captures, supervision timeouts, external players and the GPU load) are marked `[Fact(Explicit = true)]` (or `[Theory(Explicit = true)]`) with `[Trait("Category", "Heavy")]`. xUnit skips explicit tests unless asked.
- **Default suite** (build first; it must have 0 warnings):

  `dotnet build ReplayBuffer.slnx -c Release`

  `dotnet test --solution ReplayBuffer.slnx -c Release --no-build`, or one project: `dotnet test --project tests/ReplayBuffer.Engine.Tests -c Release --no-build`

- **Heavy category**, when a change touches the capture, encode, mux or audio path, or an AC needs the measurement: add `--explicit only` to either command (`--explicit on` runs both). Run one project at a time, and not while another session's tests are running.

## Commit messages: Conventional Commits 1.0.0

- **Format:** `<type>(<scope>): <summary>`. The summary is imperative, lowercase, has no trailing period, and is 72 characters or fewer. A body (optional) explains why, wrapped at 72 characters.
- **Types:**
  - `feat` — a new feature
  - `fix` — a bug fix
  - `perf` — a performance improvement
  - `refactor` — restructuring with no behaviour change
  - `test` — tests only
  - `docs` — documentation and BMad planning artifacts
  - `build` — the solution, csproj files, packages or `global.json`
  - `ci`
  - `chore` — tooling and housekeeping
  - `style` — formatting only
  - `revert`
- **Scopes** (optional, but use one when it applies): `engine`, `app`, `contracts`, `core`, `media`, `capture`, `audio`, `ffmpeg`, `windows`, `bench`, `tools`, `bmad`, `prd`, `arch`, `tickets`.
- **Breaking changes:** add `!` after the type or scope, and a `BREAKING CHANGE:` footer. Under architecture AD-22, a breaking change to Contracts also needs a spine update first.
- **BMad stories:** reference the story in a footer, e.g. `Refs: 1.1`.
- **Feature-branch merges:** keep git flow's merge commit, written in the same format, e.g. `feat(engine): merge 1.1-tracer`.
- **Example:** `feat(capture): capture game window via WGC`

  `Refs: 1.1`

  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`
