# Changelog

## 1.2.0 (not released yet)
- **Dark theme**: Settings → Appearance → Theme: **Match Windows** (the default — Magpie turns dark or light with your Windows setting, straight away), **Light** or **Dark**. Everything follows: the window, menus, icons, the reading pane and the message editor. Emails that bring their own colours (newsletters, receipts) keep them on a white card so they stay readable. The "Toolbar & buttons" page in Settings is now called **Appearance**.
- **Rules**: Settings → Rules sorts new mail as it arrives. A rule says *when* (From, To or Cc, Subject, Body, Has attachment, Category, Account — contains / is / starts with / ends with / doesn't contain; all or any of them) and *then* (Move to folder, Tag, Mark as read, Pin, Set aside, Snooze, Skip notification, Delete to Trash). Rules run top to bottom on this PC before you're notified; switch each one on or off, change the order with the arrows, **Preview matches** to see what it would catch in the Inbox, and tick "Also apply to the N matching messages already in Inbox" to tidy up what's there.
- **Set aside** (key **L**): takes a conversation out of the Inbox without a date — unlike Snooze it doesn't come back by itself. It waits in **Set aside** in the sidebar (with its count) until you open it, archive it, or press L again to put it back on top of the Inbox; **Clear all** puts the whole pile back. Also a toolbar button, a row button and a rule action.
- **Gatekeeper** (Settings → General, off unless you switch it on): mail from someone you've never written to or heard from waits at the door instead of landing in the Inbox. A banner says "3 new senders want to reach you · Review"; **Allow** lets them in for good (their waiting mail moves to the Inbox), **Block** sends their mail to Spam, now and later. Nothing is ever deleted, and switching the Gatekeeper off lets everyone waiting in. Blocked senders can be unblocked in Settings.

## 1.1.2 (28 Sep 2026)
- **About Me** in Settings → About: Krishna's details, the feedback address (click it, say what the email is about, and a new message opens in Magpie with the version, Windows details and — for bugs — the last log lines already filled in), LinkedIn and Facebook, version and release date.
- **Send now** next to every **Undo**: in the rows under the list and in the status bar. Ctrl+Shift+Enter sends the newest waiting message now; Ctrl+Z takes it back.
- **Search in Settings**: a search box at the top of Settings (Ctrl+F). Every setting is found by its name or plain words; the page list shows how many matches each page has; click a result and the setting flashes on its page. Esc clears.
- **Sidebar width**: drag the sidebar's edge (200–420 px). Narrower than that and it snaps to a slim **icon rail** with unread badges; point at an icon for its name and numbers. Double-click the edge to reset; the width is remembered. Ctrl+Shift+← / → and Ctrl+Shift+B (show / hide).
- **Folder details on hover**: point at any folder, account folder or tag and a card shows unread and total conversations, today's mail, the oldest unread, the last received, messages, size and attachments — choose the lines and the delay in Settings → Toolbar & buttons, or turn it off.
- **Buttons on email rows**: up to five action buttons on each conversation in the list (archive, delete, snooze, read/unread, pin, remind, tag, move, spam — your pick and order), shown on hover, always, or never. Click a sender's initials to tick several conversations: a bar appears with Archive · Delete · Mark read · Move · Tag · Snooze. Archive, delete and move wait a few seconds with **Undo**; deleting more than 10 asks first.

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
