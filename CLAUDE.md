# Magpie Mail — notes for Claude Code

Windows email client (C# / .NET 8 / WPF, CommunityToolkit.Mvvm, WebView2, MailKit, SQLite + FTS5).
Owner: Krishna Dipayan Bhunia. Public repo `krishnabhunia/Magpie-email-client`, branch `main`.
Current release: **5.0.0** (see CHANGELOG.md). Next: whatever is queued — the queue and every approved design are in `docs/ROADMAP-1.2.0.md`.

## How Krishna works (non-negotiable)

| Rule | What it means for you |
|---|---|
| Designs first | Nothing new is built until Krishna has seen a design and said "approved". For 1.2.0 the designs are already approved (text specs in `docs/ROADMAP-1.2.0.md`). For anything new: show a design first (HTML mock-up or a clear written spec), wait for "approved". |
| Releases (E1) | Krishna only reviews and merges PRs; releasing is automatic (`docs/CI-CD.md`), the same way as in evict-uninstaller. Merging a PR that sets a new version is the deploy. Never push tags, create releases or push to `main` yourself. |
| Queue | Any feature he mentions goes into the queue automatically (add it to `docs/ROADMAP-1.2.0.md` and open a `[Q<n>]` GitHub issue); it leaves only when he explicitly rejects it. |
| Blockers only | He wants to hear blockers, doubts and lacunas — not narration. One question at a time when something is unclear. |
| Honesty | If something is not verified (e.g. you could not run the app), say so. |
| Replies | Tables and lists, short. |
| Pharma-stack | Not relevant here (that is his other work); this project is plain Windows/.NET. |

## Krishna's standing rules for every project (29 Sep 2026)

| Rule | Meaning |
|---|---|
| Release zip | The deployment build is a zip with exactly two folders: `installer/` (the setup EXE that installs the program on Windows) and `portable/` (the program run in portable mode). No other folders. |
| CI/CD | Krishna only reviews, approves and merges PRs on GitHub. Everything after that runs by itself: all tests and checks, the version bump, the release. |
| Updates | The software checks GitHub for new versions and updates itself in the background; an **Auto update** checkbox in the app (checks again once a day). |
| Installer | If the previous version is running, the installer updates it in the background (closes it cleanly, installs, starts it again). |
| Version | Every program shows its version number in its main (home) window. |
| Version numbers (30 Sep 2026, design VB1) | x.y.z — **x** when a feature is added or the UI/UX changes in a big way, **y** when a feature is changed, **z** for a bug or error fix. The number changes with every update: each PR that changes the program sets a new version (so each merge is a release). |

## Build, test, check (run before every commit)

```powershell
# Windows (this laptop) — needs .NET 8 SDK + Python 3; Inno Setup only for the installer
dotnet test tests/Magpie.Core.Tests -c Release --filter "Category!=Integration"   # 288 unit tests
build/publish.ps1        # tests → build → DpDump → xaml_check.py + check-refs (BLOCKING) → publish/Magpie.exe + .sha256
python build/xaml_check.py --dps build/app-types.json   # static XAML check (WPF only validates XAML at run time)
```

- **Cloud (Linux) sessions:** `global.json` needs SDK ≥ 8.0.400 *with* the WindowsDesktop SDK. Ubuntu's `dotnet-sdk-8.0` lacks it and builds.dotnet.microsoft.com is blocked; the Microsoft SDK layer of the `mcr.microsoft.com/dotnet/sdk:8.0-noble` image works (extract `usr/share/dotnet` to e.g. `/opt/msdotnet`, put it first on `PATH`, then `build/publish.sh` steps run). You can build, test and XAML-check there, but not run the app.
- **Missing-assembly check is mandatory too:** `dotnet run --project build/DpDump -c Release -- --check-refs src/Magpie.App/bin/Release/net8.0-windows10.0.19041.0/win-x64` — the 1.2.0 Settings crash (WebView2CompositionControl needs `Microsoft.Windows.SDK.NET`) only showed on Windows. The app targets `net8.0-windows10.0.19041.0` for that reason; don't drop the Windows version from the TFM.
- **XAML check is mandatory.** WPF crashes at run time on XAML mistakes; `build/xaml_check.py` catches Setter/Trigger/StaticResource/typo errors statically (`build/README-checks.md`). 0 problems or don't ship.
- The 6 integration tests (`Category=Integration`) need Dovecot + a test SMTP on Linux (`build/test-servers/start.sh`); they self-skip on Windows. Don't try to make them run on the laptop. In a cloud session `apt-get install -y dovecot-imapd` + `pip install aiosmtpd` works; run start.sh in the background (it doesn't return while the SMTP server runs).
- On this laptop you **can run the app**: `dotnet run --project src/Magpie.App` (or `publish/Magpie.exe`). Do that for UI work and say what you saw. Data lives in `%APPDATA%\Magpie` (settings.json, mail.db, magpie.log).
- Warnings are errors in spirit: keep the build at 0 warnings.

## Releasing (automatic — design E1, `docs/CI-CD.md`)

1. Every PR that changes the program (`src/`, `installer/`, `Directory.Build.props`, `global.json`, `Magpie.sln`) is a release (design VB1). Write the user-facing lines at the top of `CHANGELOG.md` under `## Next version (not released yet)`, each under `### New` (feature added / big UI-UX change → x), `### Changed` (feature changed → y) or `### Fixed` (bug or error fix → z), and put "Closes #n" in the PR body for the `[Q..]` issues it finishes. Never type the number.
2. In the same PR run `python build/release_prep.py apply --date YYYY-MM-DD` (today, India time): it works the number out (last release + the biggest kind) and sets `Directory.Build.props` (version, release date), `installer/Magpie.iss`, the CHANGELOG heading and CLAUDE.md. CI fails a PR that changes the program without a new version (`needs-version`), and `check` fails if anything is missing or the number breaks the rule.
3. That PR's CI publishes a test version `x.y.z-beta.N` (pre-release). Krishna tries it, then merges.
4. On merge `build.yml` publishes `vx.y.z` (EXE, installer, zip, checksums, notes), removes its test versions and closes the issues listed in the section's `<!-- closes: #n … -->` (GitHub's "Closes #n" in the PR body doesn't close them here — always fill that comment). Tags are created by CI — never push tags.
5. Installed copies update themselves (Auto update; Settings → Updates / the title-bar pill; SHA-256 checked, rollback on failure).
6. PRs that only touch docs, CI, tests or build scripts need no version and release nothing.

## Where things are

| Area | Files |
|---|---|
| Sync | `src/Magpie.Core/Mail/AccountSync.cs` — first sync = recent mail, then backfill rounds list every older UID (headers only); bodies on open; `Prioritise`, `Remaining`, `Backlog` |
| Store | `src/Magpie.Core/Storage/MailStore.cs` — schema v4; `ListThreads` dedupes copies across folders (Gmail All Mail); counts; `GetFolderDetails` (hover card) |
| Engine | `src/Magpie.Core/MailEngine.cs` — local-first actions (archive/trash/move/read/pin/snooze/tags/reminders), outbox (undo send, send later, `SendNow`), drafts on this PC |
| Settings | `src/Magpie.Core/Settings/AppSettings.cs` — `Appearance` (toolbar, counts, colourful, `FolderHover`, `RowActions`), `WindowPlacement` (sidebar width/rail), `UpdateSettings`; always `Normalise()` new blocks |
| Updates | `src/Magpie.Core/Updates/Updates.cs`, `src/Magpie.App/Services/UpdateService.cs` |
| Backup (EX1) | `src/Magpie.Core/Settings/SettingsBackup.cs` (lock/unlock, staged restore applied at start), `src/Magpie.App/Services/BackupUi.cs`, `Views/PasswordDialog.cs` |
| Mail folder (DL1) | `AppPaths.MailRoot` (+ `mail-folder.txt`), `src/Magpie.Core/Storage/MailLocation.cs` (move), `src/Magpie.App/Services/MailFolderStartup.cs` (move at start, drive missing) |
| AI connections (AI2) | `AiSettings.Connections` / `ActiveId` / `UseActive()` (Provider/Endpoint/Model mirror the one in use), `SecretVault.AiKeyFor(id)`, `ViewModels/SettingsAi.cs` |
| Settings change tracking (AP1) | `ViewModels/SettingsChanges.cs` — `Snapshot()` is the pure twin of `SaveCore`: add every new setting to BOTH, or Apply stays greyed out for it |
| Reader (RL1, Q38/Q39) | `ThreadViewModel.Show` switches the pane at once; `LoadAsync` renders from the store first; `MessageBody.Images` + `ImagesComplete` (schema v7); `MimeText.NeedsDownload` / `MissingPictures`; `MailEngine.FetchBodyAsync` (text-only on open); page cache + `WarmNeighboursAsync` |
| Calendar (B2, Q7) | `src/Magpie.Core/Calendar/` — `GoogleCalendar.cs` (Calendar API v3 client, errors → plain messages), `CalendarService.cs` (poll 5 min + `Poke`, local-first save/delete/respond, pending ops pushed next round), `CalendarLayout.cs` (ranges, titles, overlap columns); `MailStore.Calendar.cs` (`cal_calendars`, `cal_events`, `TakeDueEventReminders`); `ViewModels/CalendarViewModel.cs`, `Views/CalendarView.xaml`, `Views/EventWindow.xaml`; Google sign-in asks for calendar + contacts scopes (`OAuth.GrantedScopes`) |
| Hover cards (HM1) | `src/Magpie.Core/Mail/HoverMenus.cs` (which lines each card shows, ids E1–E11 / S1–S9 / A1–A9), page script + `hm-a` / `hm-f` targets in `HtmlRenderer`, `Services/ReaderHover.cs` (address + file cards, runs the ids), `Services/SubjectCard.cs` (WPF popup), `Views/ThreadWindow.cs` (S3), `Views/PreviewWindow.cs` (A5); search `with:` / `file:` in `SearchQuery` |
| Trash / Spam, delete past, selection (TB1, DP1, SL1) | `MailEngine.EmptyFolder` (pending op `EmptyFolder`, sync skips that folder until it reached the server), `Restore` (`MailStore.Trash.cs` remembers the folder), `NotSpam`, `AutoEmptyTrash` (`EmptyTrashAfterDays`); `PastFrom` / `TrashEmails` + `AutoDelete.PastEmails`; `Mail/Selection.cs` (Select ▾ filters); `MainViewModel` `SelectBy`, `AllInView` / `ViewTotal` (Select all N), `IsBinView`, `EmptyBin`, `DeletePastLater`; one auto-delete rule per sender (`MergeDuplicateAutoDeleteRules`) |
| Deletion dates (DD1) | `Core/Mail/DeleteDates.cs` (texts), `Appearance.DeleteDates` (`DeleteDateLook`: Hover H1–H3, List L1–L4, Reader R1–R3; trial switch in Settings → Appearance), `ThreadItem` (row: pill / day / edge / ring, H1 tooltip), `Services/HoverCard.cs` (H2 row card, R2 chip card in `MainWindow`), `ThreadViewModel.RefreshDeleteBar` (R1 banner, R2 chip), `RenderMessage.DeleteNote` (R3) |
| Download window (DS1, Q40) | `AccountSync.PrefetchWindowAsync` (every round until complete, 25 per request via `GetStreamsAsync`), `WantBodies`, `MailStore.WindowProgress` / `ShareBodiesWithCopies` |
| Signature editor (B6) | `Views/SignatureEditorWindow.xaml(.cs)` (own window, normal WebView2), `Services/SignatureEditorPage.cs`, `Core/Mail/GmailSignature.cs` (Gmail API sendAs) |
| Downloads (DS1) | `Account.SyncDays` (0 = everything) / `DownloadAttachments`; `AccountSync.PrefetchWindowAsync` + `SaveTextOnlyAsync`; `MimeText.PendingAttachments` / `ResolveIndex` |
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
- Menus: every `MenuItem` in a context menu uses Magpie's templates in `Styles/Controls.xaml` (SubmenuItem and SubmenuHeader roles) — never the Windows default (grey panel with a vertical line).
- Don't touch `build/wpf-dps.json` (generated). `build/app-types.json` is regenerated by publish.ps1.

## Known gaps / doubts to raise with Krishna

- 1.1.1 / 1.1.2 have not been smoke-tested on this laptop yet — do that first (`docs/ROADMAP-1.2.0.md` → "Smoke test").
- Google Calendar API + People API must be enabled in his Google Cloud project (the one used for Gmail sign-in) before the 1.2.0 calendar/contacts items; re-sign-in needed for the new scopes.
- `magpie.log` review (queue #5) — ask him to look at `%APPDATA%\Magpie\magpie.log` after real use.
- Code signing: parked (unsigned by choice).
- CI/CD (E1) needs no setup. Branch protection on `main` (require the **build** check) is recommended so a red PR can't be merged and released (`docs/CI-CD.md`).
