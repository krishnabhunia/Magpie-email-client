# CI / CD — how Magpie is released (design R1, approved 29 Sep 2026)

Krishna only **reviews and merges pull requests**. Everything else is automatic.

## The flow

| # | Step | Who / what |
|---|---|---|
| 1 | A feature PR adds its lines under `## x.y.z (not released yet)` in `CHANGELOG.md` (and `<!-- closes: #n #m -->` for the `[Q..]` issues it finishes) | Claude |
| 2 | CI on the PR: unit tests → release-script tests → build → XAML check → EXE → installer | `build.yml` |
| 3 | Review and merge the feature PR (CI must be green) | **Krishna** |
| 4 | "Release x.y.z" PR is opened or updated: version, release date, installer version, dated CHANGELOG heading | `release-pr.yml` |
| 5 | CI on the release PR publishes a **test version** `x.y.z-beta.N` (pre-release; older test builds of the same version are removed) | `build.yml` |
| 6 | Smoke-test it: Magpie → Settings → Updates → *Include test versions* → Check now | **Krishna** |
| 7 | Merge the release PR — this is the deploy | **Krishna** |
| 8 | Release `vx.y.z` is published (EXE, installer, `Magpie-x.y.z.zip` with only `installer/` + `portable/`, checksums, notes from CHANGELOG); its test builds are removed; the issues in step 1 close | `build.yml` + GitHub |
| 9 | Installed copies update themselves (Settings → Updates / the green pill) | Magpie |

More feature PRs merged while a release PR is open just update it (and publish a new test version).
If no `(not released yet)` section is left, an open release PR is closed.

## One-time setup (GitHub → repository settings)

| # | Where | What |
|---|---|---|
| 1 | Your profile → Settings → Developer settings → Fine-grained tokens → Generate | Repository access: only `Magpie-email-client`. Permissions: **Contents: Read and write**, **Pull requests: Read and write**. Expiry: up to a year (note the date). |
| 2 | Repo → Settings → Secrets and variables → Actions → New repository secret | Name `RELEASE_TOKEN`, value = the token from step 1. Without it the release PR gets no CI run (GitHub doesn't run workflows for PRs opened with the built-in token), so no test version and no green check. |
| 3 | Repo → Settings → Branches → Add branch ruleset for `main` | Require a pull request before merging (required approvals: **0** — you can't approve your own PRs); require status checks: **build**; block force pushes. |
| 4 | Repo → Settings → General → Pull Requests | Tick "Automatically delete head branches" (tidy; optional). |

When the token expires the release PR stops getting CI: create a new one and replace the secret.

## Files

| File | Job |
|---|---|
| `.github/workflows/build.yml` | CI for every PR and push; test version for the release PR; release on `main` when the version has none yet |
| `.github/workflows/release-pr.yml` | Keeps the "Release x.y.z" PR (branch `release/next`) up to date after each merge to `main` |
| `build/release_prep.py` | Reads the pending version / notes / closed issues from CHANGELOG, bumps `Directory.Build.props`, `installer/Magpie.iss`, the CHANGELOG heading and CLAUDE.md |
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
