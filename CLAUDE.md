# Magpie Mail — notes for Claude Code

Windows email client (C# / .NET 8 / WPF, CommunityToolkit.Mvvm, WebView2, MailKit, SQLite + FTS5).
Owner: Krishna Dipayan Bhunia. Public repo `krishnabhunia/Magpie-email-client`, branch `main`.
Current release: **1.1.2** (see CHANGELOG.md). Next: **1.2.0** — the queue and every approved design are in `docs/ROADMAP-1.2.0.md`.

## How Krishna works (non-negotiable)

| Rule | What it means for you |
|---|---|
| Designs first | Nothing new is built until Krishna has seen a design and said "approved". For 1.2.0 the designs are already approved (text specs in `docs/ROADMAP-1.2.0.md`). For anything new: show a design first (HTML mock-up or a clear written spec), wait for "approved". |
| Deploy word | Never `git push` a release until Krishna writes **deploy**. Commit locally, tell him what is ready, wait. |
| Queue | Any feature he mentions goes into the queue automatically (add it to `docs/ROADMAP-1.2.0.md` and open a `[Q<n>]` GitHub issue); it leaves only when he explicitly rejects it. |
| Blockers only | He wants to hear blockers, doubts and lacunas — not narration. One question at a time when something is unclear. |
| Honesty | If something is not verified (e.g. you could not run the app), say so. |
| Replies | Tables and lists, short. |
| Pharma-stack | Not relevant here (that is his other work); this project is plain Windows/.NET. |

## Build, test, check (run before every commit)

```powershell
# Windows (this laptop) — needs .NET 8 SDK + Python 3; Inno Setup only for the installer
dotnet test tests/Magpie.Core.Tests -c Release --filter "Category!=Integration"   # 189 unit tests (1.2.0 branch)
build/publish.ps1        # tests → build → DpDump → xaml_check.py (BLOCKING) → publish/Magpie.exe + .sha256
python build/xaml_check.py --dps build/app-types.json   # static XAML check (WPF only validates XAML at run time)
```

- **Cloud (Linux) sessions:** `global.json` needs SDK ≥ 8.0.400 *with* the WindowsDesktop SDK. Ubuntu's `dotnet-sdk-8.0` lacks it and builds.dotnet.microsoft.com is blocked; the Microsoft SDK layer of the `mcr.microsoft.com/dotnet/sdk:8.0-noble` image works (extract `usr/share/dotnet` to e.g. `/opt/msdotnet`, put it first on `PATH`, then `build/publish.sh` steps run). You can build, test and XAML-check there, but not run the app.
- **XAML check is mandatory.** WPF crashes at run time on XAML mistakes; `build/xaml_check.py` catches Setter/Trigger/StaticResource/typo errors statically (`build/README-checks.md`). 0 problems or don't ship.
- The 5 integration tests (`Category=Integration`) need Dovecot + a test SMTP on Linux (`build/test-servers/start.sh`); they self-skip on Windows. Don't try to make them run here.
- On this laptop you **can run the app**: `dotnet run --project src/Magpie.App` (or `publish/Magpie.exe`). Do that for UI work and say what you saw. Data lives in `%APPDATA%\Magpie` (settings.json, mail.db, magpie.log).
- Warnings are errors in spirit: keep the build at 0 warnings.

## Releasing (only after "deploy")

1. Bump `<Version>`, `<AssemblyVersion>`, `<FileVersion>` in `Directory.Build.props`; set `<ReleaseDate>` (shown in Settings → About); bump `#define MyAppVersion` in `installer/Magpie.iss`.
2. Add the section to `CHANGELOG.md` (the release notes come from it).
3. Commit with a descriptive message; `git push origin main`.
4. GitHub Actions (`.github/workflows/build.yml`) runs tests → build → XAML check → EXE → installer, and **publishes Release `v<version>` automatically** when no complete release for that version exists (`x.y.z-suffix` = pre-release). Tags are created by CI — never push tags.
5. Installed copies get the update through Settings → Updates / the title-bar pill (`UpdateService`, SHA-256 checked, rollback on failure).

## Where things are

| Area | Files |
|---|---|
| Sync | `src/Magpie.Core/Mail/AccountSync.cs` — first sync = recent mail, then backfill rounds list every older UID (headers only); bodies on open; `Prioritise`, `Remaining`, `Backlog` |
| Store | `src/Magpie.Core/Storage/MailStore.cs` — schema v4; `ListThreads` dedupes copies across folders (Gmail All Mail); counts; `GetFolderDetails` (hover card) |
| Engine | `src/Magpie.Core/MailEngine.cs` — local-first actions (archive/trash/move/read/pin/snooze/tags/reminders), outbox (undo send, send later, `SendNow`), drafts on this PC |
| Settings | `src/Magpie.Core/Settings/AppSettings.cs` — `Appearance` (toolbar, counts, colourful, `FolderHover`, `RowActions`), `WindowPlacement` (sidebar width/rail), `UpdateSettings`; always `Normalise()` new blocks |
| Updates | `src/Magpie.Core/Updates/Updates.cs`, `src/Magpie.App/Services/UpdateService.cs` |
| Main window | `src/Magpie.App/MainWindow.xaml(.cs)`, `ViewModels/MainViewModel.cs` (nav, list, counts, toasts, sidebar, hover card, row actions, bulk bar), `ViewModels/ThreadViewModel.cs` (reader) |
| Settings UI | `src/Magpie.App/Views/SettingsWindow.xaml(.cs)`, `ViewModels/SettingsViewModel.cs` (incl. search index `Index[]`, About Me) |
| Compose | `src/Magpie.App/Views/ComposeWindow.xaml(.cs)`, `ViewModels/ComposeViewModel.cs`, `src/Magpie.Core/Mail/Composer.cs` |
| Look | `src/Magpie.App/Themes/Light.xaml` (every brush key — a Dark.xaml must define the same set; `theme-parity` check), `Styles/Magpie.xaml`, `Styles/Controls.xaml`, `Services/Icons.cs` (icon set with light + dark colour pairs, `Icons.Dark`) |
| Tests | `tests/Magpie.Core.Tests` — one `Release<ver>Tests.cs` per release; `TestUtil.cs` has `Rows.Make` / `Rows.NewStore` |

## Conventions

- One `Release<ver>Tests.cs` per release; test Core logic, not WPF.
- New settings: add to `AppSettings`, handle in `Normalise()` (old settings.json must load), round-trip in `SettingsViewModel` (load → Save), and add the row to the Settings search `Index` with plain-word keywords + an `x:Name="Row…"` anchor.
- Icons: add to `Icons.All` with a light and a dark colour pair; use `IconChip`.
- Every user-facing string in plain English, no jargon ("Getting older emails", not "backfilling").
- Keep `CHANGELOG.md` in the same voice: what the user gets, not how.
- Commit messages: what changed and why; end with `Co-Authored-By: Claude <noreply@anthropic.com>` if Claude wrote it.
- Don't touch `build/wpf-dps.json` (generated). `build/app-types.json` is regenerated by publish.ps1.

## Known gaps / doubts to raise with Krishna

- 1.1.1 / 1.1.2 have not been smoke-tested on this laptop yet — do that first (`docs/ROADMAP-1.2.0.md` → "Smoke test").
- Google Calendar API + People API must be enabled in his Google Cloud project (the one used for Gmail sign-in) before the 1.2.0 calendar/contacts items; re-sign-in needed for the new scopes.
- `magpie.log` review (queue #5) — ask him to look at `%APPDATA%\Magpie\magpie.log` after real use.
- Code signing: parked (unsigned by choice).
