# CI / CD — how Magpie is released (design E1, approved 29 Sep 2026 — same as evict-uninstaller)

Krishna only **reviews and merges pull requests**. Merging a PR that sets a new version **is** the release.
Nothing to set up: only GitHub's built-in token is used. (E1 replaced R1, the bot "Release x.y.z" PR, which
needed a personal `RELEASE_TOKEN` secret.)

The 11.0.0 version-manager feature is Windows-only. Krishna deferred the macOS feature on **1 Nov 2026
(IST)**. The existing Mac updater, build and DMG packaging continue; this PR makes no Mac version-manager
readiness claim.

## The flow

| # | Step | Who / what |
|---|---|---|
| 1 | Every PR that changes the program writes its lines under `## Next version (not released yet)` in `CHANGELOG.md` — each under `### New`, `### Changed` or `### Fixed` (see *Version rule*) — then runs `python common/scripts/release_prep.py apply --date YYYY-MM-DD`: works the number out, sets it in `Directory.Build.props` (+ release date) and `windows/installer/Magpie.iss`, dates the CHANGELOG heading, updates CLAUDE.md. PRs that only touch docs, CI, tests or build scripts need no stable version bump. | Claude |
| 2 | Independent path-filtered workflows run only their platform: **Windows Build** (unit tests, XAML/ref/UI checks, EXE, installer), **macOS Build** (headless tests, signed app, DMG), **Android Build** (`android/build.sh` only if Android code exists). The **Build Magpie** release workflow validates version rules and waits for successful platform artifacts from the same revision before packaging Windows ZIPs or standalone macOS/Android releases. | `build-windows.yml`, `build-macos.yml`, `build-android.yml`, `build.yml` |
| 3 | Each platform run uses the same reproducible version for a PR commit, `x.y.z-beta.<commit-time>.g<sha8>` (pre-release). A PR publishes a beta release containing only affected platform assets: a Windows change includes the Windows EXE, installer and ZIP; Mac-only or Android-only changes include their DMG or APK respectively. A beta built on an already released number sorts below that stable number. Stable and beta releases retain their files. | `ci_version.py`, `build.yml` |
| 4 | Smoke-test it: Magpie → Settings → Updates → *Include test versions* → Check now (or download it from the release page) | **Krishna** |
| 5 | Merge the PR — this is the deploy | **Krishna** |
| 6 | Release `vx.y.z` is published for the changed platforms only (Windows EXE, installer, ZIP and compatibility manifest; Mac DMG; Android APK; with checksums and CHANGELOG notes), marked Latest; the issues in its CHANGELOG section's `<!-- closes: #n … -->` are closed with a link to the release | `build.yml` |
| 7 | Windows copies check when automatic checks are enabled. Settings → Updates / the title-bar "Update to vx.y.z" button opens exact-version selection. Only the version explicitly selected and confirmed can be downloaded and installed. The existing Mac updater retains its previous behavior. | Magpie |

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

Tags are created by CI — tag pushes do not trigger extra platform builds. Never create releases or tags by hand. The Windows version manager shows missing files or
compatibility information as unavailable; it never treats a missing asset as a safe update. Installation
is enabled only after checking the version's application file, SHA-256 and compatibility manifest.

## Optional (GitHub → repository settings)

| # | Where | What |
|---|---|---|
| 1 | Settings → Branches → Add branch ruleset for `main` | Require a pull request, the release **version** check, and applicable platform checks (**Windows Build / windows**, **macOS Build / macos**, **Android Build / android**). Account for path-filtered checks in rulesets: a skipped entire workflow does not report a status. Also require **Build Magpie / package** only where it runs. |
| 2 | Settings → General → Pull Requests | "Automatically delete head branches" (tidy). |

## Files

| File | Job |
|---|---|
| `.github/workflows/build-windows.yml` | Windows-only builds, native checks and a versioned Windows artifact; triggered by Windows, shared and global changes |
| `.github/workflows/build-macos.yml` | Mac-only tests, app bundle, DMG and a versioned Mac artifact; triggered by Mac, shared and global changes (never a Windows-only change) |
| `.github/workflows/build-android.yml` | Android-only APK when an Android app exists; Android, shared and global changes never run other platforms in this workflow |
| `.github/workflows/build.yml` | Release metadata checks on every PR/push to main, waits for matching successful platform runs and publishes platform-scoped beta/stable assets |
| `common/scripts/ci_version.py`, `ci_platforms.py`, `ci_wait_artifacts.py` | Deterministic cross-workflow versions, changed-file routing and exact-revision artifact collection; unit tested in the release job |
| `common/scripts/release_prep.py` | `apply` (work the version out and set it everywhere), `pending`, `needs-version`, `check`, `notes`, `zip` / `check-folders` / `artifact-name` (zip rule), `numeric`, `closes` |
| `common/scripts/test_release_prep.py` | Its tests (run in CI) |
| `common/scripts/update_manifest.py` | Generate and validate Windows exact-version compatibility against the built files and current source schemas |
| `common/scripts/test_update_manifest.py` | Reject corrupt hashes, missing builds, mixed versions and unsupported compatibility claims |
| `.github/workflows/remove-release.yml` | Run by hand: removes one release and its tag (e.g. one made by mistake); refuses the newest real release |

## Release zip and CI download (Krishna's GitHub Skills rules, 10 Oct 2026; replaced design Z1)

Each platform run has its own temporary, versioned working artifact (`build-windows-*`, `build-macos-*`, or
`build-android-*`). When a Windows build is part of a change, the separate release run collects successful
artifacts for that exact revision and publishes one `Magpie_<version>.zip` download
(`Magpie_11.0.0-beta.<commit-time>.g<sha8>.zip` for a PR). The combined ZIP (+ `.sha256`) is built by
`python common/scripts/release_prep.py zip`, which refuses unexpected entries:

| Folder | Holds | Use |
|---|---|---|
| `portable/` | `Magpie_<version>.exe` | Run from anywhere; a `Magpie_*.exe` the installer didn't put there keeps all data in `MagpieData\` next to it |
| `windows-x64/` | `Magpie_<version>.exe` (the installer) | Install Magpie (Start menu, starts with Windows, updates itself) |
| `macOS/` | `Magpie_<version>.dmg` | Magpie for Mac (Apple Silicon, macOS 14+; unsigned — first open: right-click → Open, see `macOS/README.md`) |
| `Android/` | `Magpie_<version>.apk` | Only when `android/` has an app (none planned yet) |

The loose `Magpie.exe` / `.sha256` and `Magpie-Setup-<version>.exe` stay on each release too: installed copies
update from `Magpie.exe`. An all-users install is not eligible for the application's self-replacement flow;
its existing installer remains available on the release page.

How a platform plugs in: the independent **macOS Build** workflow installs .NET 8 and 10 (`macOS/global.json` picks SDK 10), runs `dotnet test macOS/Magpie.Mac.Tests` (headless; existing `continue-on-error` remains) and `macOS/build/build.sh <version>`, which writes `macOS/out/Magpie_<version>.dmg` (also on a combined release with its `.sha256` when macOS was built);
the independent **Android Build** workflow calls `android/build.sh <version>` when it exists and expects `android/out/Magpie_<version>.apk`. Platform artifacts expire after seven days. Packaging does not run a platform itself or mix different commit revisions. A platform-specific PR runs only its platform beta build; merging it to `main` runs only the matching stable build. PRs that touch several platforms run each affected platform independently; shared and global changes run every supported dependent platform. The assembler publishes exactly the artifacts built for that change and never starts a platform job.

## Release compatibility and retention

Each new stable and beta release carries `Magpie.update.json` and its SHA-256 checksum, outside the combined
zip so that the one-file-per-platform download layout stays intact. The manifest identifies the exact
version, accepted database/settings schema ranges, supported Windows installation kinds and verified file hashes.
The package job checks all of these against the actual release files before publication.

The current compatibility policy accepts the same database and settings schemas as this build. A future
migration requires an explicit, tested compatibility policy; a backup must never be used to claim that
an older program can read a newer database. The Windows chooser displays older releases without compatibility metadata
as unverified for a transition, even when their downloadable application has a valid checksum. Previously
deleted releases cannot be recovered by the retention change.

Supported version-manager installation kinds are **Windows portable** and **Windows per-user** only.
The manifest does not advertise all-users Windows or Mac application-bundle transition support. A packaged
DMG is still checked against its checksum when present; its hash records file integrity, not Mac version-manager support.
The SHA-256 checks prove file integrity against the GitHub release metadata; they are separate from
optional executable Authenticode signing and Git commit signature verification.

Publishing a new beta or stable release never deletes older releases or their tags. The existing manual
remove-release workflow remains available for correcting an erroneous release; deleting one makes that
version unavailable for future installation. Working CI artifacts still expire and are not the rollback
catalogue.

## Build checks that fail the build

| Check | Catches |
|---|---|
| Unit tests, release-script tests | Logic and release-script mistakes |
| `update_manifest.py generate` / `check` | Compatibility metadata that does not match the exact version, source schemas or checked release files |
| Mac `build.sh` (`codesign --verify`, `hdiutil verify`) | A Mac build, bundle or disk image that isn't right; existing packaging checks remain blocking |
| `release_prep.py needs-version` / `check` | A program change without a new version; a number that breaks the version rule |
| `xaml_check.py` | XAML errors WPF only reports at run time |
| `DpDump --check-refs` | An assembly the app references but the build doesn't carry (crashes only on Windows, e.g. the 1.2.0 Settings crash) |

Mac headless tests remain advisory under the existing workflow. Their result does not establish readiness
of the deferred Mac version-manager feature.
