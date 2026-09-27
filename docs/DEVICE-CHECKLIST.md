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
