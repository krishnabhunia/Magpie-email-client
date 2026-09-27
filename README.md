# Magpie Mail

A Windows email client that combines the power features of **eM Client** with the calm, smart inbox of **Spark** —
local-first, with optional AI you control from one Settings page.

**Version 1.0.0 · Build 1** · C# / .NET 8 / WPF · single-file `Magpie.exe` (no install needed; an installer is built by CI)

## What's in Build 1

| # | Area | Features |
|---|------|----------|
| 1 | Accounts | Gmail (Google sign-in or app password), Outlook / Microsoft 365 (Microsoft sign-in), any IMAP/SMTP mailbox; provider presets; passwords and tokens encrypted with Windows DPAPI |
| 2 | Sync | MailKit IMAP with CONDSTORE/IDLE where available, periodic check, per-folder sync, offline cache in SQLite |
| 3 | Unified inbox | All accounts in one list with a colour dot per account; per-account folder tree |
| 4 | Smart inbox (Spark) | People · Notifications · Newsletters tabs with unread counts; "always put this sender in People" |
| 5 | Conversations | Threaded view with collapsed quotes, safe HTML rendering (WebView2, scripts stripped), remote images blocked until you allow them, trusted senders |
| 6 | Triage | Archive (E), Delete (Del), Pin (P), Mark unread (U), Snooze (S), Remind me (incl. "if nobody replies by…"), Tags, Unsubscribe, move to folder, auto-advance to the next conversation |
| 7 | Compose | Rich editor, Cc/Bcc, attachments and pasted images, quick templates, server drafts, **Send later**, **Undo send** |
| 8 | Search | Full-text search (SQLite FTS5) with operators `from:` `to:` `subject:` `has:attachment` `is:unread` `is:pinned` `before:` `after:` |
| 9 | Views | Inbox, Pinned, Snoozed, Follow up, Scheduled, Sent, Drafts, Archive, Spam, Trash, Tags |
| 10 | Desktop | Tray icon, close-to-tray, start with Windows, notifications (optionally people only), single instance, keyboard shortcuts (J/K, R, A, F, E, Ctrl+N, Ctrl+F, F5, /) |
| 11 | **AI features** (optional) | One master switch + four feature switches — see below |

## AI features — one page, five switches (approved designs S1–S5)

| # | Switch | What appears when ON | Sends to the model |
|---|--------|----------------------|--------------------|
| 1 | **Enable AI features** (master) | Nothing on its own; OFF hides every AI control and blocks every call | — |
| 2 | Summarise thread | Indigo "Summarise thread" button in the thread toolbar → streaming summary card | Thread text |
| 3 | Write draft from prompt | Indigo assistant rail on the right of Compose → prompt, tone, draft preview → Insert / Replace / Discard | Your prompt + thread context |
| 4 | Rewrite selection | Rewrite chips (shorter, friendlier, more formal, fix grammar, custom) for selected text in Compose | Selected text |
| 5 | Suggested replies | Reply chips under the last message of a thread | Thread text |

Providers: **OpenAI**, **Anthropic**, **Ollama (local)**, or any **OpenAI-compatible** endpoint. Each feature asks for
consent the first time it sends data to a cloud provider; a local model never asks because nothing leaves the PC.
Nothing is ever inserted or sent automatically — every AI result is a card you accept. Mail keeps working when the
provider is down or switched off.

## First run

1. Run `Magpie.exe` → **Add account**.
2. Gmail / Outlook with one-click sign-in need your own free sign-in app (once, ~5 min): see [`docs/SIGN-IN-SETUP.md`](docs/SIGN-IN-SETUP.md).
   Without it, Gmail works with an **app password**, and any IMAP mailbox with its normal password.
3. Optional: **Settings → AI features** → switch on, pick a provider, paste an API key (or choose Ollama), **Test connection**, then switch on the features you want.

Data lives in `%APPDATA%\Magpie` (mail cache, settings, `magpie.log`).

## Known limits in Build 1

| # | Limit | Planned |
|---|-------|---------|
| 1 | Drafts are saved to the server's Drafts folder, so **Save draft needs a connection**; offline you can keep the window open or send later | Local draft autosave |
| 2 | A second send inside the undo window replaces the Undo toast (the first message can then only be stopped from Scheduled) | Stacked toasts |
| 3 | Light theme only; dialogs use the Windows title-bar colour | Dark theme |
| 4 | Not code-signed, so Windows shows "unknown publisher" for the join script / installer | Signing |

## Build

```
build/publish.sh        # Linux/macOS
build\publish.ps1       # Windows
```
Runs unit tests, builds, runs the **blocking** XAML static check (`build/xaml_check.py`), and publishes `publish/Magpie.exe`.
Integration tests against a real IMAP/SMTP server: `build/test-servers/start.sh`, then
`dotnet test tests/Magpie.Core.Tests --filter Category=Integration`.

| Project | Contents |
|---|---|
| `src/Magpie.Core` | Accounts, OAuth (PKCE loopback), IMAP/SMTP sync (MailKit), SQLite + FTS5 store, threading, smart categories, HTML sanitiser, composer, outbox (send later / undo), reminders, AI providers + unified toggle gating |
| `src/Magpie.App` | WPF UI (MVVM Toolkit), WebView2 reader and editor, tray, dialogs, Settings |
| `tests/Magpie.Core.Tests` | xUnit unit tests + end-to-end tests against Dovecot |
| `build/` | publish scripts, XAML static checker + WPF metadata dump, test servers |
| `installer/` | Inno Setup script (built by GitHub Actions) |
