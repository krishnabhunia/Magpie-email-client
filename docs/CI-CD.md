# CI / CD — how Magpie is released (design E1, approved 29 Sep 2026 — same as evict-uninstaller)

Krishna only **reviews and merges pull requests**. Merging a PR that sets a new version **is** the release.
Nothing to set up: only GitHub's built-in token is used. (E1 replaced R1, the bot "Release x.y.z" PR, which
needed a personal `RELEASE_TOKEN` secret.)

## The flow

| # | Step | Who / what |
|---|---|---|
| 1 | Every PR that changes the program writes its lines under `## Next version (not released yet)` in `CHANGELOG.md` — each under `### New`, `### Changed` or `### Fixed` (see *Version rule*) — then runs `python build/release_prep.py apply --date YYYY-MM-DD`: works the number out, sets it in `Directory.Build.props` (+ release date) and `installer/Magpie.iss`, dates the CHANGELOG heading, updates CLAUDE.md. PRs that only touch docs, CI, tests or build scripts need no version. | Claude |
| 2 | CI on the PR: release-script tests → **a change to Magpie gets a new version** (`needs-version`) → `check` (new version set everywhere, CHANGELOG dated, number follows the rule) → unit tests → build → XAML + missing-assembly checks → EXE → installer → zip | `build.yml` |
| 3 | Same CI run publishes a **test version** `x.y.z-beta.N` (pre-release; only the newest is kept). If the PR is merged before this finishes and `vx.y.z` is out, no test version is published (or it is removed right away) | `build.yml` |
| 4 | Smoke-test it: Magpie → Settings → Updates → *Include test versions* → Check now (or download it from the release page) | **Krishna** |
| 5 | Merge the PR — this is the deploy | **Krishna** |
| 6 | Release `vx.y.z` is published (EXE, installer, `Magpie-x.y.z.zip`, checksums, notes from CHANGELOG), marked Latest; its test versions are removed; the issues in its CHANGELOG section's `<!-- closes: #n … -->` are closed with a link to the release | `build.yml` |
| 7 | Installed copies update themselves (Auto update, or Settings → Updates / the title-bar pill) | Magpie |

## Version rule (design VB1, Krishna 30 Sep 2026)

| Part | Raised when | Example after 2.2.0 |
|---|---|---|
| **x** | `### New` — a feature added, or a big change to how Magpie looks or works | 3.0.0 |
| **y** | `### Changed` — a feature changed | 2.3.0 |
| **z** | `### Fixed` — a bug or error fixed | 2.2.1 |

The biggest kind in the section wins (New + Fixed → x). The count starts from the newest dated version in
`CHANGELOG.md`; nobody types the number. The number changes with every update: CI fails a PR that changes `src/`,
`installer/`, `Directory.Build.props`, `global.json` or `Magpie.sln` while its version is already released. Releases
before 30 Sep 2026 keep their numbers. The version shows in the main window's title bar (design V1).

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
| `build/release_prep.py` | `apply` (work the version out and set it everywhere), `pending`, `needs-version`, `check`, `notes`, `zip` (design Z1), `numeric`, `closes` |
| `build/test_release_prep.py` | Its tests (run in CI) |
| `.github/workflows/remove-release.yml` | Run by hand: removes one release and its tag (e.g. one made by mistake); refuses the newest real release |

## Release zip (design Z1)

Every release and test version also carries `Magpie-<version>.zip` (+ `.sha256`), built by
`python build/release_prep.py zip` — it refuses to build a zip with anything but these two folders:

| Folder | Holds | Use |
|---|---|---|
| `installer/` | `Magpie-Setup-<version>.exe` + `.sha256` | Install Magpie (Start menu, starts with Windows, updates itself) |
| `portable/` | `Magpie.exe` + `.sha256` + `portable.txt` | Run from anywhere; `portable.txt` keeps all data in `MagpieData\` next to the EXE |

The loose `Magpie.exe` / `.sha256` stay on each release too: installed copies update from them.

The CI download of every run (Actions → the run → Artifacts) is the same zip unpacked: only `installer/` and
`portable/` (`release_prep.py check-folders` fails the build otherwise).

## Build checks that fail the build

| Check | Catches |
|---|---|
| Unit tests, release-script tests | Logic and release-script mistakes |
| `release_prep.py needs-version` / `check` | A program change without a new version; a number that breaks the version rule |
| `xaml_check.py` | XAML errors WPF only reports at run time |
| `DpDump --check-refs` | An assembly the app references but the build doesn't carry (crashes only on Windows, e.g. the 1.2.0 Settings crash) |
