# CI / CD — how Magpie is released (design E1, approved 29 Sep 2026 — same as evict-uninstaller)

Krishna only **reviews and merges pull requests**. Merging a PR that sets a new version **is** the release.
Nothing to set up: only GitHub's built-in token is used. (E1 replaced R1, the bot "Release x.y.z" PR, which
needed a personal `RELEASE_TOKEN` secret.)

## The flow

| # | Step | Who / what |
|---|---|---|
| 1 | The PR that should ship writes its lines under `## x.y.z (not released yet)` in `CHANGELOG.md`, then runs `python build/release_prep.py apply --date YYYY-MM-DD`: version in `Directory.Build.props` (+ release date) and `installer/Magpie.iss`, dated CHANGELOG heading, CLAUDE.md. PRs that don't change the version release nothing. | Claude |
| 2 | CI on the PR: unit tests → release-script tests → `check` (new version set everywhere, CHANGELOG dated) → build → XAML + missing-assembly checks → EXE → installer → zip | `build.yml` |
| 3 | Same CI run publishes a **test version** `x.y.z-beta.N` (pre-release; only the newest is kept) | `build.yml` |
| 4 | Smoke-test it: Magpie → Settings → Updates → *Include test versions* → Check now (or download it from the release page) | **Krishna** |
| 5 | Merge the PR — this is the deploy | **Krishna** |
| 6 | Release `vx.y.z` is published (EXE, installer, `Magpie-x.y.z.zip`, checksums, notes from CHANGELOG), marked Latest; its test versions are removed; "Closes #n" in the PR closes the issues | `build.yml` + GitHub |
| 7 | Installed copies update themselves (Auto update, or Settings → Updates / the title-bar pill) | Magpie |

Tags are created by CI — never create releases or tags by hand (a hand-made release with a higher number,
e.g. `v2.0.1-…`, confuses "Latest"; Magpie's updater skips releases without `Magpie.exe`).

## Optional (GitHub → repository settings)

| # | Where | What |
|---|---|---|
| 1 | Settings → Branches → Add branch ruleset for `main` | Require a pull request; require status check **build** (0 approvals). Stops a red PR from being merged — and so from being released. |
| 2 | Settings → General → Pull Requests | "Automatically delete head branches" (tidy). |

## Files

| File | Job |
|---|---|
| `.github/workflows/build.yml` | CI for every PR and push; test version for a PR with a new version; release on `main` when the version has none yet |
| `build/release_prep.py` | `apply` (set a version everywhere), `check`, `notes`, `zip` (design Z1), `numeric`, `closes` |
| `build/test_release_prep.py` | Its tests (run in CI) |

## Release zip (design Z1)

Every release and test version also carries `Magpie-<version>.zip` (+ `.sha256`), built by
`python build/release_prep.py zip` — it refuses to build a zip with anything but these two folders:

| Folder | Holds | Use |
|---|---|---|
| `installer/` | `Magpie-Setup-<version>.exe` + `.sha256` | Install Magpie (Start menu, starts with Windows, updates itself) |
| `portable/` | `Magpie.exe` + `.sha256` + `portable.txt` | Run from anywhere; `portable.txt` keeps all data in `MagpieData\` next to the EXE |

The loose `Magpie.exe` / `.sha256` stay on each release too: installed copies update from them.

## Build checks that fail the build

| Check | Catches |
|---|---|
| Unit tests, release-script tests | Logic and release-script mistakes |
| `xaml_check.py` | XAML errors WPF only reports at run time |
| `DpDump --check-refs` | An assembly the app references but the build doesn't carry (crashes only on Windows, e.g. the 1.2.0 Settings crash) |
