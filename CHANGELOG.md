# Changelog

<!-- Version rule (design VB1): put the next version's lines at the top, under the heading
    "## Next version (not released yet)" and, inside it, under
    "### New"      a feature added, or a big change to how Magpie looks or works  -> x.0.0
    "### Changed"  a feature changed                                             -> x.y.0
    "### Fixed"    a bug or error fixed                                          -> x.y.z
    then run: python build/release_prep.py apply --date YYYY-MM-DD   (it works the number out) -->

## 4.0.1 (7 Oct 2026)
### Fixed
- Existing files in a chosen mail folder stay intact unless replacement is explicitly chosen. Interrupted backup writes no longer block startup.
- Applying one mail rule keeps any earlier body-dependent rules and notifications waiting for that message.
- Deletions on older mail servers stay deleted, and sending during shutdown keeps local cleanup and follow-up reminders.
- AI rewrites accept selections with leading or trailing spaces, while retaining protection against edits made during generation.
- Reliability fixes work alongside the new Trash, Spam and selection features, including mail databases upgraded from version 4.0.0.
- Sent emails stay sent when draft cleanup or a notification fails, so they are not sent again.
- Offline mail actions check that the server still uses the same message identifiers. Deleting one message no longer clears other messages waiting in Trash on servers without selective deletion.
- Calendar sync preserves edits and deletions made while an upload is running, retries new events without creating duplicates, and removes calendar data when an account is removed.
- Calendar reminders set for the start of an event now appear once, including shortly after the PC wakes.
- Moving the mail checks a temporary copy before replacing existing mail. Failed settings restores recover the previous settings and sign-ins and keep the backup for retry.
- Rules that check the body wait for the complete message and keep their order and notification choices.
- Scrolling the message list can reach conversations after the first 400. AI rewrites replace the original selection and ask for a new rewrite if the message changed.
- AI calls check permission in the service for each connection and endpoint, accept HTTPS or a local HTTP model, and handle empty suggested replies safely.
- Quoted recipient names containing commas are accepted, oversized update versions are ignored, and deletion dates display consistently across Windows settings.
- The README now describes the current calendar, dark theme, search and AI permission behaviour.

## 4.0.0 (1 Oct 2026)
### New
- **Tick boxes on every email row**, always there: click (or Space) ticks one, Shift+click ticks a range. The box above the list ticks everything on screen or nothing; **Ctrl+A** ticks all, **Esc** clears.
- **Select ▾** above the list ticks by filter: All, None, Invert, Unread, Read, Pinned, With attachments, From the same sender as the email open, Older than (1 week to 1 year), People / Notifications / Newsletters, or a tag.
- **Select all in the folder**: once everything on screen is ticked, one click reaches every conversation of the folder or search, not only those loaded. The bar above the list now also has **Mark unread**, **Pin** and **Spam**.
- **Trash and Spam**: a bar shows how many conversations are there, with **Empty Trash** / **Empty Spam** (it says how many go for good and asks first, one account at a time). Ticked conversations (or right-click) can be **restored** to where they were, **moved**, marked read or unread, or **deleted forever**; in Spam, **Not spam** moves them to the Inbox and lets the sender through the Gatekeeper.
- **Empty Trash by itself** after 7 or 30 days (Settings → General; off unless you choose it).
- **Delete the emails already here** from a sender: the Delete ▾ menu can delete every email from that person, or from anyone at their domain, or only those older than 1 week to 2 years — in all folders and accounts, pinned ones kept. It says how many and from when, and gives a few seconds to undo.
### Changed
- **Auto-delete rules include the emails already in the Inbox** by default; those already past the time you choose go to Trash at once (untick "Include the … emails already in the Inbox" for new emails only).
### Fixed
- **Menus no longer show a grey panel with a vertical line** in submenus (Move to, Auto-delete, "From … after…"): every menu and submenu now looks the same, icons line up, and ticked items show a tick.
- **Making the same auto-delete rule twice no longer adds a second rule**: the second changes the first, and rules already doubled are merged when Magpie starts.
<!-- closes: #51 #52 #53 -->

## 3.0.0 (30 Sep 2026)
### New
- **Calendar.** A new **Calendar** entry in the sidebar (and a calendar button in the title bar; Ctrl+2 opens it, Ctrl+1 goes back to mail) shows your Google Calendar as **Day**, **Week**, **Month** or **Agenda**, with **Today ‹ ›**, a small month to jump to any day, and a tick box and colour for each calendar (one per Google account, plus Holidays in India).
- **Add, change and delete events** — double-click a time or press **New event**: title, date and time or all day, calendar, people to invite, where, a Google Meet link, a reminder and repeat (every day, week or month). Changes are kept on this PC at once and sent to Google in the background, so the calendar works without internet too.
- **Invitations in the calendar** show with a dashed border until you answer; open one to **Accept**, **Maybe** or **Decline**.
- **Reminders**: a notification pops up before an event (10 minutes by default); click it to see that day.
- Magpie checks Google for calendar changes every 5 minutes, or at once with **Check Google now**. **Sign in again to each Google account once** (Settings → Accounts) so Magpie may read your calendar — the calendar says so if it can't.
- **Hover cards in the reading pane.** Rest the mouse on an email address, the subject or an attachment (or right-click it, or press the Menu key) and a small card offers what you can do with it:
  - **Address**: copy the address, or name and address · write a new email · emails from this person · all emails with them · new event with them · always show their pictures · make a rule · auto-delete their emails · block (send to Spam).
  - **Subject**: copy it · emails with this subject · open the conversation in its own window · remind me if no reply · snooze · tag · make an event from the email · make a rule · summarise with AI.
  - **Attachment**: open · preview pictures and PDFs inside Magpie · save as · save all · copy the file · forward just this file · show in folder · files from this person · emails with this file name.
  The card opens after the same delay as the folder card (Settings → Appearance) and says what happened ("Copied anita@vendorco.in").
- **Search** understands `with:anita` (from, to or cc) and `file:contract` (attachment name).
<!-- closes: #3 #50 -->

## 2.3.0 (30 Sep 2026)
### Changed
- **Buttons on email rows** now sit where the date is, on the first line, when you point at a row: the same spot on every row, one size, evenly spaced, **⋯** last. Nothing covers the subject, preview or tags any more; the date comes back when the mouse leaves (and shows in full as a tooltip).
- **Settings**: the first button is now **Close** (it closes Settings without applying your changes), next to **Apply** and **Apply and Close**.
- **Downloading, per account**: Settings → Accounts now says how many emails of the chosen days are on this PC ("All 2,400 emails from the last 90 days are on this PC"), and the status bar says "Downloading emails to this PC… 120 left" while they come in.

### Fixed
- **Picking another email** no longer shows the previous one: the reading pane switches to the new email's subject and sender at once, and the next few emails in the list are prepared while you read, so they appear straight away.
- **Emails no longer come from the server again when opened.** Some emails (with an invite, a signature file or a very large picture inside) were read again every time; now everything an email shows is kept on this PC. An email that is in two folders (Gmail's Inbox and All Mail) is downloaded once.
- **Much faster downloading of the chosen days** (e.g. 90): emails come 25 per request and keep coming until all of them are on this PC — before, only 150 came every 5 minutes, and emails with big attachments were skipped. Emails with big attachments now get their text ahead; the attachments come when you open them.
- **Opening an email that isn't on this PC yet** downloads just its text and pictures when attachments come on opening, so a 20 MB attachment no longer delays reading a few lines.
<!-- closes: #43 #44 #45 #46 #47 -->

## 2.2.0 (30 Sep 2026)
- **Emails open from this PC.** Opening an email no longer reads it from the server again: the pictures inside it (a logo, a newsletter) are now kept with its text when it is downloaded, so it is drawn once, straight away. "Loading…" shows only when an email really has to be downloaded, and going back to a conversation you just read is instant. Accounts set to download only the text get those pictures too (not the attachments).
- **Settings buttons**: **Cancel** · **Apply** · **Apply and Close**. Apply (the default button, Enter) and Apply and Close come alive only when something differs from what is saved.
- **Collapsible cards** in Settings: Sign-in apps, each account, and each template fold to one line.
- **Sign in again** is greyed out until Magpie says the account's sign-in has expired; the account card says so too.
- **Your details** for each account (contact number, job title, company), when adding it and later in Settings → Accounts.
- **Default signature**: every account starts with "Thanks and Regards", your name, job title · company and contact number — put in once for accounts that had no signature. **Use the default signature** in Settings → Signatures & replies brings it back.
<!-- closes: #33 #34 #35 #36 #37 #38 -->

## 2.1.0 (30 Sep 2026)
- **Back up your settings** (Settings → General): one file with everything you set up in Magpie — accounts and their sign-ins, rules, signatures, quick replies, templates, tags, Gatekeeper lists, look and layout — locked with a password you choose. After reinstalling Magpie (or on another PC), **Restore from a backup** puts it all back and the accounts sign in by themselves. The Add account window offers it on first start too.
- **Where your mail is kept** (Settings → General): move your mail to another folder or drive, for example a BitLocker or VeraCrypt drive to keep it encrypted. Magpie restarts and moves it, and only removes the old copy once the new one is checked. If that drive is locked or unplugged when Magpie starts, it waits for you instead of starting an empty mailbox.
- **Downloading, per account**: when you add an account (and later in Settings → Accounts) choose how much email to download — the last 30 days, **90 days** (the default), 6 months, a year or everything — and whether attachments come too or **only when you open the email** (the default). Emails in that time open instantly and can be read and searched offline; older ones are still listed and download when you open them.
- **Apply** in Settings: save your changes and keep Settings open (next to **Save and close**).
- **Several AI connections** (Settings → AI): set up more than one — say OpenAI, Claude and a local model — each with its own model and key, test each one, pick the one in use with one click, and delete the ones you don't need.
- **Signatures**: fixed not being able to type a signature. **Edit signature…** now opens the signature in its own window, with the same editor as writing an email. Accounts signed in with Google can copy their signature from Gmail with **Get my Gmail signature**.
- **Folder details on hover** can now appear after anything from 50 to 1000 ms, in steps of 50.
- **Send a test notification** (Settings → Notifications) shows what a new-mail notification looks like.
- **Buttons on email rows** now float at the right of the row in a small bar: bigger buttons, the first two set apart from the rest, and **⋯** for every other action. The date and subject stay visible.
<!-- closes: #23 #24 #25 #27 #28 #29 #30 #31 #32 -->

## 2.0.0 (29 Sep 2026)
- **Magpie 2.0.0** carries everything from 1.2.0 and checks the new automatic release: this version was published by merging its pull request, with a test version (2.0.0-beta) to try first.

## 1.2.0 (29 Sep 2026)
- **Version in the title bar**: the version number now sits next to "Magpie" at the top of the window. Point at it for the release date; click it for Settings → About.
- **Dark theme**: Settings → Appearance → Theme: **Match Windows** (the default — Magpie turns dark or light with your Windows setting, straight away), **Light** or **Dark**. Everything follows: the window, menus, icons, the reading pane and the message editor. Emails that bring their own colours (newsletters, receipts) keep them on a white card so they stay readable. The "Toolbar & buttons" page in Settings is now called **Appearance**.
- **Meeting invites** in a conversation show as a card above it: the date, title, time in your time zone, place, a **Join the video call** link (Meet, Teams, Zoom), who organised it and how many are invited, and **Accept / Maybe / Decline** (with "Add a note to my reply"). Your answer goes to the organiser as a proper calendar reply, with the usual Undo. Changes and cancellations from the organiser show on the card, and it warns when the time clashes with another invite you said yes or maybe to.
- **Rules**: Settings → Rules sorts new mail as it arrives. A rule says *when* (From, To or Cc, Subject, Body, Has attachment, Category, Account — contains / is / starts with / ends with / doesn't contain; all or any of them) and *then* (Move to folder, Tag, Mark as read, Pin, Set aside, Snooze, Skip notification, Delete to Trash). Rules run top to bottom on this PC before you're notified; switch each one on or off, change the order with the arrows, **Preview matches** to see what it would catch in the Inbox, and tick "Also apply to the N matching messages already in Inbox" to tidy up what's there.
- **Signatures & replies** (new Settings page): a signature for each account with **bold, italic, underline, links, fonts, colours and a picture** (your logo is sent inside the email, so it shows even when pictures from the internet are blocked). Choose whether it goes on new messages and/or on replies and forwards (above the quoted text). Your old plain-text signature is carried over.
- **Quick replies**: "Thanks!", "Got it, will do.", "Sounds good to me." (change or add your own in Settings) show under a conversation; **one click sends the reply** — with the usual Undo.
- **Set aside** (key **L**): takes a conversation out of the Inbox without a date — unlike Snooze it doesn't come back by itself. It waits in **Set aside** in the sidebar (with its count) until you open it, archive it, or press L again to put it back on top of the Inbox; **Clear all** puts the whole pile back. Also a toolbar button, a row button and a rule action.
- **Auto-delete**: the Delete button has a **▾** (also in the list's right-click menu): "From <sender> after…" or "From anyone at *@domain after…" — 1 day to 30 years — and future emails from them go to Trash on their own; a toast confirms it with **Undo · Edit**. **Choose sender and time…** opens the full rule (an address or *@domain, one account or all, and — only if you tick it — start the timer on the emails already there). Each email shows its date as a tag ("Deletes 4 Oct 2026", "Deletes tomorrow"; red when under two days, amber under a month), and the reader says when it moves to Trash with **Keep this one** and **Change rule**. **Deleting soon** in the sidebar lists everything due in the next 7 days, with **Keep all**. Settings → Rules → **Auto-delete** lists every rule (waiting emails, next delete) with Edit / Pause / Remove. Pinned mail is never auto-deleted; nothing is deleted while Magpie is closed — overdue emails go when it next starts.
- **OTP delete**: one-time codes and sign-in links from a sender go to Trash 24 hours after they arrive ("OTP · deletes in 23 h 54 m").
- **Gatekeeper** (Settings → General, off unless you switch it on): mail from someone you've never written to or heard from waits at the door instead of landing in the Inbox. A banner says "3 new senders want to reach you · Review"; **Allow** lets them in for good (their waiting mail moves to the Inbox), **Block** sends their mail to Spam, now and later. Nothing is ever deleted, and switching the Gatekeeper off lets everyone waiting in. Blocked senders can be unblocked in Settings.
- **Auto update** (Settings → Updates, on by default): Magpie looks for a new version when it starts and once a day, downloads it, checks it and installs it in the background. The new version starts the next time you open Magpie — or press **Restart now** on the title-bar pill to use it straight away. Untick it and Magpie only updates when you ask.
- **Portable Magpie**: each release has a zip with two folders — **installer** (run the setup to install Magpie) and **portable** (run Magpie.exe from anywhere, e.g. a USB stick; everything it stores stays in a MagpieData folder next to it). Sign-ins are protected with your Windows account, so on another PC the portable copy asks you to sign in again.
- **Installing over a running Magpie**: the installer closes Magpie by itself, installs the new version and starts it again (back in the tray if that's where it was) — no "please close Magpie first".
<!-- closes: #2 #4 #6 #7 #8 #9 #10 #13 #15 #17 #18 -->

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
