# Build 1 — checks on a real Windows PC

Tick these after running `Magpie.exe` on the laptop. If anything fails, send `%APPDATA%\Magpie\magpie.log` and a screenshot.

| # | Check | Expected |
|---|-------|----------|
| W1 | Launch `Magpie.exe` | Window opens, dark title bar, IBM Plex font, "Add an account" empty state; tray icon appears |
| W2 | Open every window once: Settings (every page), Add account, Compose, Snooze / Remind me pickers, Templates | Each opens without an error dialog |
| W3 | Add an IMAP or Gmail app-password account | Folders appear; inbox fills; People / Notifications / Newsletters tabs show counts |
| W4 | Open a conversation | Reader shows HTML safely; remote images blocked with "Load images" |
| W5 | Archive (E), Pin (P), Snooze (S) → "Later today", Remind me | Row disappears/moves; appears under Snoozed / Follow up; returns when due |
| W6 | Compose → send to yourself | "Sending…" toast with **Undo**; mail arrives; Undo reopens the draft |
| W7 | Compose → Send later → pick a time | Appears under Scheduled; sends on time while Magpie runs (tray) |
| W8 | Search `from:<someone> has:attachment` | Matching conversations only |
| S1 | Settings → AI features, master OFF | Provider + features greyed; no AI button anywhere, no compose rail |
| S2 | Master ON, provider set, **Test connection**, only Summarise ON | Thread toolbar shows indigo **Summarise thread**; compose has **no** rail |
| S3 | Turn on Write draft, Rewrite, Suggested replies | Compose shows the indigo rail; reply chips under a thread; first use of each asks consent (cloud only) |
| S4 | Turn Summarise OFF again | Button disappears from the open thread without restarting |
| G1 | (after SIGN-IN-SETUP) Add Gmail with Google sign-in | Browser sign-in → back in Magpie → account syncs |
| M1 | (after SIGN-IN-SETUP) Add Outlook with Microsoft sign-in | Same |

## 14.0.0 — Start-up screen (SP1 B)

| # | Check | Expected |
|---|-------|----------|
| SP1 | Quit Magpie (tray → Exit), open it from the Start menu | The card appears at once where the Magpie window opens; the 4 steps tick with times; accounts as chips; it fades when the window has drawn |
| SP2 | Settings → General → Start-up screen → Dark theme on, reopen | Card in dark colours; Light / Match Windows follow too |
| SP3 | Start-up screen → **Only the first time after an update**, reopen | No card; after the next update (in Magpie, or with the installer / a new portable EXE) the card says "Getting the new version ready · just updated", once |
| SP4 | Start-up screen → **Never**, reopen | No card |
| SP5 | Sign out and in with **Start with Windows** on | Magpie starts in the tray, no card |
| SP6 | Untick **Show my accounts on it**, reopen | No chips |
| SP7 | `%APPDATA%\Magpie\magpie.log` | One line "start-up 1.2 s (update … · mail folder … · look … · accounts …)" per start |
