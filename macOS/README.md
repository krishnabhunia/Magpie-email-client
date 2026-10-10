# Magpie for Mac

The first version of Magpie for Mac (8.0.0, queue #74, plan PX1 part B): an Avalonia app in `macOS/Magpie.Mac/`
that uses the same engine as Windows (`common/src/Magpie.Core`), for **Apple Silicon** (M1 and newer, macOS 14
Sonoma or later — .NET 10 needs it), delivered as `Magpie_<version>.dmg`.

## Install and first open

| Step | What to do |
|---|---|
| 1 | Download `Magpie_<version>.dmg` from the release (or take it from `macOS/` in `Magpie_<version>.zip`) |
| 2 | Open it and drag **Magpie** onto **Applications** |
| 3 | First open only: Magpie isn't signed with an Apple Developer ID (by choice), so macOS refuses a double-click. **Right-click Magpie in Applications → Open → Open**. On macOS 15 and later, if there is no Open button: try to open it once, then **System Settings → Privacy & Security → "Magpie was blocked…" → Open Anyway** |
| 4 | After that it opens normally. Updates install themselves and don't ask again |

Run it from **Applications**, not from the disk image or Downloads: macOS starts apps from those places in a
read-only copy, where Magpie can't update itself (it then opens the new disk image for you instead).

## What it does (first version)

| Area | Mac |
|---|---|
| Main window | Native title bar with the traffic lights; top bar with "Magpie", the version, and "Update to vx.y.z" at the top right (only when GitHub has a newer version) · New message · Get mail · Settings |
| Sidebar | All inboxes, then each account with its folders (Inbox, Sent, Drafts, Trash, Spam, the rest) and unread counts |
| List | Sender, subject, preview, date, unread dot, pin, attachment, count; search (same words as Windows: `from:`, `to:`, `subject:`, `has:attachment` …; a search in All inboxes covers every folder, and Archive / Delete act on the copies it found); 400 conversations at a time with Load more; in Drafts, the drafts kept on this Mac come first ("On this Mac") |
| Reader | The conversation drawn by Core's HtmlRenderer in the Mac's own web view (WKWebView, through NativeWebView); web links (http/https only) open in the browser, mailto: in a new message, other links are ignored; internet pictures follow Settings (Ask / Always / Never) with Show pictures; attachments open in their app |
| Actions | Reply, Reply all, Forward, Archive, Delete (Trash; in Trash and Spam "Delete forever", which asks first with Cancel as the default), Mark read / unread, Pin; a draft shows Edit draft instead (or press Return / double-click it in the list) |
| Compose | From, To / Cc / Bcc with suggestions from your contacts, subject, HTML editor (Core's editor page, the same as Windows; ⌘K adds a web or mail link, empty removes it), attachments, Send (Undo for the seconds set), Send later, Save draft (server Drafts, or this Mac when offline) |
| Accounts | IMAP / SMTP with server settings found by themselves, Sign in with Google / Microsoft in the browser (client IDs in Settings, Import Google client JSON…) |
| Settings | Accounts (add, sign in again, remove, sign-in apps) · Updates (Auto update, Include test versions, Check now, last check) · About (version, data folder, log) |
| Menus | Magpie (About, Settings… ⌘,, Check for Updates…, Hide, Quit ⌘Q) · File (New Message ⌘N, Add Account…, Close Window ⌘W) · Edit (Undo, Redo, Cut, Copy, Paste, Select All, Find ⌘F) · View (All Inboxes ⌘1, Get New Mail ⇧⌘N, Show Pictures) · Message (Reply ⌘R, Reply All ⇧⌘R, Forward ⇧⌘F, Archive ⌃⌘A, Delete ⌘⌫ — ⌫ alone in the list —, Read/Unread ⇧⌘U, Pin ⇧⌘L) · Window (Minimize ⌘M, Zoom, Magpie ⌘0) |
| Updates | Every start checks GitHub; Auto update also checks daily, downloads `Magpie_<v>.dmg`, checks it against `Magpie_<v>.dmg.sha256` (and again just before installing) and installs it when you quit; the top-right button downloads (if needed), installs and restarts at once — and then quitting installs nothing more. The old app is kept in `~/Library/Caches/Magpie/previous/Magpie.app` (outside Applications, so Launchpad shows one Magpie); if a new version can't start, Magpie offers to go back to it |
| Still Windows only | Calendar, rules, auto-delete, Gatekeeper, snooze / set aside / reminders, tags, AI, hover cards, settings backup, the other Settings pages (next: PR C, Mac parity) |

## Where things are kept

| What | Where |
|---|---|
| Mail, settings, sign-ins, log | `~/Library/Application Support/Magpie` (`mail.db`, `settings.json`, `secrets.json`, `magpie.log`) |
| Caches, update downloads, the previous version | `~/Library/Caches/Magpie` (`updates/`, `previous/Magpie.app`) |
| Key for the passwords in `secrets.json` | Your login Keychain, item "Magpie secrets" (account "Magpie"), AES-256-GCM |

## Build

```bash
bash macOS/build/build.sh 8.0.0          # → macOS/out/Magpie_8.0.0.dmg (on a Mac)
cd macOS && dotnet test Magpie.Mac.Tests # headless window checks (any OS with the .NET 10 SDK)
```

| Needs | Why |
|---|---|
| .NET 10 SDK | Avalonia 12 and the WKWebView control (NativeWebView 12) are .NET 10; `macOS/global.json` picks SDK 10 — run `dotnet` from `macOS/` (the repository root's `global.json` picks SDK 8 for Windows). Magpie.Core stays .NET 8 |
| Python 3 | `release_prep.py numeric` (bundle version) |
| macOS | `sips` + `iconutil` (icon), `codesign` (ad-hoc signature, needed on Apple Silicon), `hdiutil` (disk image). On Linux the script builds and assembles `Magpie.app`, then stops with a note |

`build.sh` steps: `dotnet publish -r osx-arm64 --self-contained` → `Magpie.app` (`Contents/MacOS` = the publish,
`Contents/Resources/Magpie.icns`, `Contents/Info.plist` from `macOS/build/Info.plist`: `com.krishnabhunia.magpie`,
version, macOS 14+) → `codesign --force --deep --sign -` → `hdiutil create … -format UDZO` with an Applications link.

## How CI picks it up

The **macos** job in `.github/workflows/build.yml` (on `macos-latest`, Apple Silicon) installs .NET 8 and 10, runs
the headless tests (`continue-on-error` until they have run on a Mac runner) and `bash macOS/build/build.sh <version>`, which writes `macOS/out/Magpie_<version>.dmg`. The
package job puts that file into `macOS/` inside `Magpie_<version>.zip` and on the release with a `.sha256` — the
file the Mac app updates from. The job runs only when both exist:

| File | Job |
|---|---|
| `macOS/Magpie.Mac/*.csproj` | the app |
| `macOS/build/build.sh <version>` | builds it and writes `macOS/out/Magpie_<version>.dmg` |

## Code map

| File | Job |
|---|---|
| `Magpie.Mac/App.axaml(.cs)`, `Program.cs` | Start-up (engine with the Keychain protector, updater), light/dark from the Mac, quit (compose windows first, install on quit) |
| `Magpie.Mac/MainWindow.axaml(.cs)`, `ViewModels/MainViewModel.cs`, `ViewModels/Items.cs` | Top bar, sidebar, list, search, toasts |
| `ViewModels/ReaderViewModel.cs`, `Services/ReaderPresenter.cs`, `Services/WebSurface.cs` | Reading pane: Core pages in WKWebView (NativeWebView), page messages, links |
| `Views/ComposeWindow.axaml(.cs)`, `ViewModels/ComposeViewModel.cs` | Compose (Core's `EditorPage.ForMac`) |
| `Views/AddAccountWindow.axaml(.cs)`, `ViewModels/AddAccountViewModel.cs` | Add account / sign in again |
| `Views/SettingsWindow.axaml(.cs)`, `ViewModels/SettingsViewModel.cs` | Settings |
| `Services/MacUpdateService.cs`, `Services/MacInstaller.cs` | Updates (rules in Core's `UpdatePolicy`, bundle swap in Core's `MacBundle`; `hdiutil`, `ditto`, relaunch, rollback dialog here) |
| `Services/MacMenus.cs`, `Services/EditCommands.cs` | Menu bar; Edit commands for text fields and web pages |
| `Magpie.Mac.Tests/` | Headless Avalonia tests |
| `build/build.sh`, `build/Info.plist` | The .app and the .dmg |
