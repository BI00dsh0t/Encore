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
