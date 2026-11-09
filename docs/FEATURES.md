# What Magpie can do

One row per feature, in plain words. "Since" is the first version in `CHANGELOG.md` that had it. This table is built into the app (Info → What's implemented) and checked by CI (`common/scripts/features_check.py`): a PR with a `### New` line in its CHANGELOG section must add or change a row here.

| SrNo. | Area | What it does | Since | Windows | Mac |
|---|---|---|---|---|---|
| 1 | Accounts | Gmail (Google sign-in or app password), Outlook / Microsoft 365 (Microsoft sign-in) and any IMAP/SMTP mailbox, with server settings found by themselves; passwords and tokens kept encrypted | 1.0.0 | Yes | Yes |
| 2 | Sync | Gets new mail as it arrives where the server allows it, checks regularly, and keeps a copy on this computer so mail opens and searches offline | 1.0.0 | Yes | Yes |
| 3 | Unified inbox | All accounts in one list with a colour dot per account, plus each account's own folders | 1.0.0 | Yes | Yes |
| 4 | Smart inbox | People · Notifications · Newsletters tabs with unread counts; "always put this sender in People" | 1.0.0 | Yes | Not yet |
| 5 | Conversations | Emails grouped into conversations, quoted text folded, emails shown safely with internet pictures blocked until you allow them | 1.0.0 | Yes | Yes |
| 6 | Triage | Archive, delete, pin, mark read or unread and move to a folder, with the next conversation opening by itself | 1.0.0 | Yes | Yes |
| 7 | Snooze, reminders and tags | Snooze a conversation, "remind me if nobody replies by…", and coloured tags | 1.0.0 | Yes | Not yet |
| 8 | Compose | Rich editor, Cc / Bcc, attachments and pasted pictures, templates, Send later and Undo send | 1.0.0 | Yes | Yes |
| 9 | Search | Searches every email on this computer, with words like `from:`, `to:`, `subject:`, `has:attachment`, `is:unread`, `before:`, `after:` | 1.0.0 | Yes | Yes |
| 10 | Views | Inbox, Pinned, Snoozed, Follow up, Scheduled, Sent, Drafts, Archive, Spam, Trash and one view per tag | 1.0.0 | Yes | Not yet |
| 11 | Desktop | Notification-area icon, close to the notification area, start with Windows, new-mail notifications, one copy running, keyboard shortcuts | 1.0.0 | Yes | Not yet |
| 12 | AI features | Optional: summarise a conversation, write a draft from a prompt, rewrite a selection and suggest replies, with OpenAI, Anthropic, a local Ollama model or any compatible server; asks before sending anything | 1.0.0 | Yes | Not yet |
| 13 | Drafts on this computer | A message is saved on this computer a few seconds after each change and uploaded to Drafts when you are back online | 1.0.1 | Yes | Yes |
| 14 | Sidebar | Folders, accounts and tags fold open or closed, subfolders as a tree, width and state remembered | 1.0.1 | Yes | Not yet |
| 15 | Updates | Checks GitHub for a newer version, checks the download's SHA-256 and installs it; test versions only when you ask for them | 1.1.0 | Yes | Yes |
| 16 | Colourful look | A coloured icon for every folder and action, coloured sender initials, tags in their colours | 1.1.0 | Yes | Not yet |
| 17 | Toolbar | Icon and name on every button; show, hide and order them, choose the style | 1.1.0 | Yes | Not yet |
| 18 | Folder numbers | Unread / total conversations where mail arrives (3 / 10), a single count elsewhere, none for Sent | 1.1.0 | Yes | Not yet |
| 19 | Status bar | Online or offline, download progress, what just arrived, sending with Undo, and problems with their fix | 1.1.0 | Yes | Not yet |
| 20 | Dark theme | Light, Dark or the computer's own setting, for the whole app | 1.2.0 | Yes | Yes |
| 21 | Rules | Sort new mail as it arrives: move, tag, mark read, pin, set aside, snooze, skip the notification or delete | 1.2.0 | Yes | Not yet |
| 22 | Signatures and quick replies | A formatted signature per account, and one-click replies under a conversation | 1.2.0 | Yes | Not yet |
| 23 | Gatekeeper and Set aside | New senders wait at the door until you allow them; set a conversation aside without a date | 1.2.0 | Yes | Not yet |
| 24 | Auto-delete | Emails from a sender or domain go to Trash after the time you choose; one-time codes after 24 hours | 1.2.0 | Yes | Not yet |
| 25 | Meeting invites | An invite shows as a card with Accept, Maybe or Decline and a clash warning | 1.2.0 | Yes | Not yet |
| 26 | Settings backup | All your settings and sign-ins in one password-locked file, restored in one step after reinstalling | 2.1.0 | Yes | Not yet |
| 27 | Mail folder | Keep your mail in a folder or drive you choose, for example an encrypted drive | 2.1.0 | Yes | Not yet |
| 28 | Downloads per account | Choose how many days of email to keep on this computer and whether attachments come only when opened | 2.1.0 | Yes | Not yet |
| 29 | Calendar | Google Calendar as Day, Week, Month or Agenda, with event editing, Meet links, invitation answers and reminders | 3.0.0 | Yes | Not yet |
| 30 | Hover cards | Point at an address, the subject or an attachment in an email for a card of what you can do with it | 3.0.0 | Yes | Not yet |
| 31 | Tick boxes and Select | Tick boxes on every row, Select by filter, select every conversation in a folder; Empty Trash and Spam | 4.0.0 | Yes | Not yet |
| 32 | Deletion dates | See when an auto-deleted email will go: on hover, on its row and in the reading pane | 5.0.0 | Yes | Not yet |
| 33 | Delete three ways | Shift+Delete deletes for good; one dialog deletes the emails already here and those arriving later from a sender | 6.0.0 | Yes | Not yet |
| 34 | Commands and saved views | Ctrl+K finds any action, folder or view; Important and Other inbox; saved inbox views and searches | 7.0.0 | Yes | Not yet |
| 35 | Contacts | Google Contacts in Magpie: search, add and edit, with better recipient suggestions | 9.0.0 | Yes | Not yet |
| 36 | Fonts and text | Choose a font and size for each kind of text in the app | 10.0.0 | Yes | Not yet |
| 37 | AI reply drafts | Reply and Reply All can start with an AI draft in the tone, length and language you choose | 10.0.0 | Yes | Not yet |
| 38 | Version manager | Choose an exact stable or test version, see if it works with your mail, confirm the exact version, and roll back | 11.0.0 | Yes | Not yet |
| 39 | Gmail labels fold | Labels inside other labels sit under their parent; quiet labels move into More labels | 11.1.0 | Yes | Not yet |
| 40 | Accounts on a new computer | Sign in once on a new computer and every account (and, if you like, your settings) comes along | 12.0.0 | Yes | Not yet |
| 41 | AI writing | AI note when you forward, "What the reply says" with one-click answers, tone, formality, culture and etiquette | 13.0.0 | Yes | Not yet |
| 42 | Start-up screen | While Magpie opens: its version, the start-up steps with their times and your accounts; a failed start says why | 14.0.0 | Yes | Not yet |
| 43 | Compose window | Balanced, Focus and Studio ways to write: recipient chips, separate Cc / Bcc, a contacts picker, attachment cards with previews, several signatures per account, templates with variables, draft history, find and replace, tables, emoji, Send & Archive, priority and read receipts | 15.0.0 | Yes | Not yet |
| 44 | Cloud file links | Share a file through Google Drive or OneDrive, with a review of who gets View or Edit access before the link goes in | 16.0.0 | Yes | Not yet |
| 45 | Signed and encrypted mail | Sign or encrypt with S/MIME or OpenPGP, and open a protected message on its own to check its signature | 16.0.0 | Yes | Not yet |
| 46 | Delivery notifications | Ask the mail server for a delivered / failed report, separately from a read receipt | 16.0.0 | Yes | Not yet |
| 47 | Send as an alias | Up to 20 sender addresses your provider allows, per account, chosen while writing | 16.0.0 | Yes | Not yet |
| 48 | Translate a selection | Translate the selected text with your AI and see it before it replaces anything | 16.0.0 | Yes | Not yet |
| 49 | Existing cloud files | Browse your own Google Drive or OneDrive files while writing and share one with a review of who gets View or Edit access | 17.0.0 | Yes | Not yet |
| 50 | Info window | ⓘ next to the version (or F1): this build's details, what's implemented, what's new and what it is made of, with Copy build info and the full release notes | 18.0.0 | Yes | Yes |
