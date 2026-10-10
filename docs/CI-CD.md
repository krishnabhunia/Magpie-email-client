# CI / CD — how Magpie is released (design E1, approved 29 Sep 2026 — same as evict-uninstaller)

Krishna only **reviews and merges pull requests**. Merging a PR that sets a new version **is** the release.
Nothing to set up: only GitHub's built-in token is used. (E1 replaced R1, the bot "Release x.y.z" PR, which
needed a personal `RELEASE_TOKEN` secret.)

## The flow

| # | Step | Who / what |
|---|---|---|
| 1 | Every PR that changes the program writes its lines under `## Next version (not released yet)` in `CHANGELOG.md` — each under `### New`, `### Changed` or `### Fixed` (see *Version rule*) — then runs `python common/scripts/release_prep.py apply --date YYYY-MM-DD`: works the number out, sets it in `Directory.Build.props` (+ release date) and `windows/installer/Magpie.iss`, dates the CHANGELOG heading, updates CLAUDE.md. PRs that only touch docs, CI, tests or build scripts need no version. | Claude |
| 2 | CI on the PR, one workflow with jobs **version** (release-script tests, `needs-version`, `check`) → **windows** (unit tests, build, XAML + missing-assembly checks, UI smoke, EXE, installer) · **macos** (on `macos-latest`, Apple Silicon: .NET 8 + 10, headless Mac tests, `macOS/build/build.sh` → `Magpie_<v>.dmg`) · **android** (`android/build.sh` → apk; skipped while `android/` has no app) → **package** (the one artifact `Magpie_<v>.zip`, release or test version) | `build.yml` |
| 3 | Same CI run publishes a **test version** `x.y.z-beta.N` (pre-release; only the newest is kept). If the PR is merged before this finishes and `vx.y.z` is out, no test version is published (or it is removed right away) | `build.yml` |
| 4 | Smoke-test it: Magpie → Settings → Updates → *Include test versions* → Check now (or download it from the release page) | **Krishna** |
| 5 | Merge the PR — this is the deploy | **Krishna** |
| 6 | Release `vx.y.z` is published (`Magpie.exe`, installer, `Magpie_x.y.z.zip`, the Mac `Magpie_x.y.z.dmg`, checksums, notes from CHANGELOG), marked Latest; its test versions are removed; the issues in its CHANGELOG section's `<!-- closes: #n … -->` are closed with a link to the release | `build.yml` |
| 7 | Installed copies check at every start and update themselves (Auto update, or Settings → Updates / the title-bar "Update to vx.y.z" button) | Magpie |

## Version rule (design VB1, Krishna 30 Sep 2026)

| Part | Raised when | Example after 2.2.0 |
|---|---|---|
| **x** | `### New` — a feature added, or a big change to how Magpie looks or works | 3.0.0 |
| **y** | `### Changed` — a feature changed | 2.3.0 |
| **z** | `### Fixed` — a bug or error fixed | 2.2.1 |

The biggest kind in the section wins (New + Fixed → x). The count starts from the newest dated version in
`CHANGELOG.md`; nobody types the number. The number changes with every update: CI fails a PR that changes the app code
(`common/src/`, `windows/Magpie.App/`, `windows/installer/`, `macOS/Magpie.Mac/`, `android/app/`), `Directory.Build.props`, `global.json` or `Magpie.sln` while its version is already released. Releases
before 30 Sep 2026 keep their numbers. The version shows in the main window's title bar (design V1).

Tags are created by CI — never create releases or tags by hand (a hand-made release with a higher number,
e.g. `v2.0.1-…`, confuses "Latest"; the Windows updater skips releases without `Magpie.exe` + `.sha256`, the Mac one releases without `Magpie_<v>.dmg` + `.sha256`).

## Optional (GitHub → repository settings)

| # | Where | What |
|---|---|---|
| 1 | Settings → Branches → Add branch ruleset for `main` | Require a pull request; require status check **build** (0 approvals). Stops a red PR from being merged — and so from being released. |
| 2 | Settings → General → Pull Requests | "Automatically delete head branches" (tidy). |

## Files

| File | Job |
|---|---|
| `.github/workflows/build.yml` | CI for every PR and push; test version for a PR with a new version; release on `main` when the version has none yet |
| `common/scripts/release_prep.py` | `apply` (work the version out and set it everywhere), `pending`, `needs-version`, `check`, `notes`, `zip` / `check-folders` / `artifact-name` (zip rule), `numeric`, `closes` |
| `common/scripts/test_release_prep.py` | Its tests (run in CI) |
| `.github/workflows/remove-release.yml` | Run by hand: removes one release and its tag (e.g. one made by mistake); refuses the newest real release |

## Release zip and CI download (Krishna's GitHub Skills rules, 10 Oct 2026; replaced design Z1)

Every run's **Artifacts** box holds exactly one file, `Magpie_<version>.zip` (`Magpie_7.2.0-beta.95.zip` for a PR).
Every release and test version carries the same zip (+ `.sha256`), built by `python common/scripts/release_prep.py zip`,
which refuses anything else:

| Folder | Holds | Use |
|---|---|---|
| `portable/` | `Magpie_<version>.exe` | Run from anywhere; a `Magpie_*.exe` the installer didn't put there keeps all data in `MagpieData\` next to it |
| `windows-x64/` | `Magpie_<version>.exe` (the installer) | Install Magpie (Start menu, starts with Windows, updates itself) |
| `macOS/` | `Magpie_<version>.dmg` | Magpie for Mac (Apple Silicon, macOS 12+; unsigned — first open: right-click → Open, see `macOS/README.md`) |
| `Android/` | `Magpie_<version>.apk` | Only when `android/` has an app (none planned yet) |

The loose `Magpie.exe` / `.sha256` and `Magpie-Setup-<version>.exe` stay on each release too: installed copies
update from `Magpie.exe`, and copies installed for all users point at the setup file.

How a platform plugs in: the **macos** job installs .NET 8 and 10 (`setup-dotnet` with both versions; `macOS/global.json` picks SDK 10), runs `dotnet test macOS/Magpie.Mac.Tests` (headless) and `macOS/build/build.sh <version>`, and expects `macOS/out/Magpie_<version>.dmg` (also on the release with its `.sha256`, which installed Mac copies update from);
the **android** job runs `android/build.sh <version>` and expects `android/out/Magpie_<version>.apk`. The jobs hand
their files to **package** as working artifacts (`build-*`), which package deletes after building the zip.

## Build checks that fail the build

| Check | Catches |
|---|---|
| Unit tests, release-script tests | Logic and release-script mistakes |
| Mac headless tests (`macOS/Magpie.Mac.Tests`), `build.sh` (`codesign --verify`, `hdiutil verify`) | Mac windows that don't build or bind; a Mac bundle or disk image that isn't right |
| `release_prep.py needs-version` / `check` | A program change without a new version; a number that breaks the version rule |
| `xaml_check.py` | XAML errors WPF only reports at run time |
| `DpDump --check-refs` | An assembly the app references but the build doesn't carry (crashes only on Windows, e.g. the 1.2.0 Settings crash) |
