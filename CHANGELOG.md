# Changelog

## 1.1.1 (28 Sep 2026)
- **Every folder now shows all its emails.** Before, a folder only listed mail from the last 90 days and never went back for the rest, so a folder holding only older mail (a 2022 email in a Gmail label, say) looked empty. Now the first sync still shows recent mail quickly, then Magpie keeps listing older emails in every folder — headers first, newest first, the folder you are looking at before the others — and an email's body is downloaded when you click it. A folder that is still loading says "Getting this folder's emails from the server…"; the status bar shows "Getting older emails · account · folder · N left".
- **Gmail's All Mail, Starred and Important are listed too** (archived Gmail mail lives only in All Mail). An email that sits in several folders is shown and counted once in Pinned, tag and search views; Archive / Delete from those views leave the All Mail and Starred copies alone.
- Old mail found this way never triggers "new mail" notifications; new mail keeps arriving while older emails are being listed.

## 1.1.0 (28 Sep 2026)
- **Updates from GitHub, inside Magpie**: checks at start and once a day, downloads in the background, checks the SHA-256, and "Restart now" swaps Magpie.exe (the old one is kept as Magpie.previous.exe; if the new one doesn't start, Magpie goes back). Settings → Updates; a green pill in the title bar when an update is ready.
- **Automatic releases**: every new version pushed to GitHub publishes its own Release (EXE, installer, checksums, notes) — nothing to click.
- **Colourful look**: a coloured icon for every folder and action, always the same colour (Delete is red, Snooze violet…); coloured initials for senders; tags in their own colours.
- **Icon + name on every button** (Archive, Delete, Snooze, Remind, Tag, Pin, Move…). Settings → Toolbar & buttons: show or hide each one, change the order, Icon + name / Icon only / Name only, colourful on or off; the list's right-click menu follows the same order.
- **Folder numbers** in conversations: unread / total (3 / 10) where new mail arrives, a single count for Pinned, Snoozed, Drafts, Scheduled, Trash…, nothing for Sent. Choose Unread / total, Unread only or Off.
- **Status bar**: one row at the bottom — online/offline, syncing progress, "Received 3 new · …", sending with Undo, problems with their fix (Sign in again, Retry), update downloads; click it for per-account activity.
- **Switching emails shows "Loading…" at once** (subject and sender straight away) instead of the previous email staying on screen; the page is built in the background, and a message that can't be downloaded says why with a Try again button.

## 1.0.1 (28 Sep 2026)
- **AI quick setup** (Settings → AI features): one click for Off · A (provider only) · B (Summarise) · C (full assistant); any other mix shows as Custom.
- **Collapsible sidebar**: arrows on Folders, Accounts and Tags, on each account and on folders with subfolders (now shown as a real tree). A closed section still shows the unread count or a sign-in warning. Open/closed state is remembered; ← / → close and open the focused heading.
- **Drafts are safe offline**: a message is saved on this PC a few seconds after each change. Closing asks Keep / Discard; Keep saves to the server's Drafts folder, or — when offline — keeps it on this PC and uploads it automatically once the account is connected. Unsent drafts survive a crash or restart and are listed under Drafts marked "On this PC".
- **Undo for every send**: each message gets its own Undo row with a countdown (up to three; more fold into "+N more — see Scheduled").
- Saving the window size or sidebar state no longer rebuilds the sidebar.
- Drafts on this PC: click or press Enter to open one; Delete removes it (after asking). Pasted pictures stay in a draft when it is reopened. An older copy of a message is dropped once a newer version is sent or saved, so it can never upload over it. Keep / Discard / Send / autosave take turns, and typing during a save is never lost.
- Servers that keep every folder under INBOX (INBOX.Sent, INBOX.Work…) show them as normal top-level folders.

## 1.0.0 — Build 1 (27 Sep 2026)
- First release: Gmail / Microsoft 365 / IMAP accounts with OAuth, unified + smart inbox, threaded reader,
  snooze, remind me, pin, tags, send later, undo send, templates, full-text search, tray + notifications.
- Unified AI toggles (designs S1–S5): master switch + Summarise / Write draft / Rewrite / Suggested replies,
  OpenAI · Anthropic · Ollama · OpenAI-compatible, per-feature consent.

### Fixed before release (Windows smoke test + independent code review)
- Opening an unread conversation blanked the reader (list reload reset the selection); saving Settings or a
  folder-list change did the same.
- Choosing "No" to "Save this message as a draft?" and quitting with a compose window open both crashed
  (WPF Close during Closing).
- A message pulled back with Undo send / Cancel & edit could be discarded on close without asking;
  Cc/Bcc edits and text typed before the editor loaded didn't count as changes.
- Double-clicking Send (or holding Ctrl+Enter) could queue the message twice.
- A failing SMTP goodbye after the server accepted a message marked it failed, so it was sent again on retry.
- "Regenerate" summary skipped the consent dialog; the Write-draft consent now states that replies include the conversation.
- Removing a carried-over attachment (forward) could leave it in the sent message.
- Windows sign-out / restart now asks about unsent messages; a start-up failure no longer leaves a hidden process.
- Thread toolbar buttons no longer stretch when the pane is narrow; snoozed rows show "Snoozed until…";
  failed sends show in red; the remote-images banner no longer lingers after closing a conversation.
