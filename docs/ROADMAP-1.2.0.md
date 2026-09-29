# Magpie — roadmap and approved designs for 1.2.0

Everything here was **approved by Krishna on 28 Sep 2026** (designs B1–B7, AD1–AD4 on the design canvas). This file is the text version so Claude Code can build without the canvas. Build order (default, Krishna may reorder): dark theme → rules → signatures + quick replies → Gatekeeper + Set aside → auto-delete + OTP delete → invites → contacts → calendar.

Ship as one release **1.2.0** or as 1.2.0 / 1.2.1 / … — releasing is automatic: merging a PR that sets a new version publishes it (design E1, `docs/CI-CD.md`).

## Queue

| # | Item | Design | Status | GitHub |
|---|---|---|---|---|
| 5 | Fixes from real Gmail use | — | Waiting on Krishna's `magpie.log` | [#1](https://github.com/krishnabhunia/Magpie-email-client/issues/1) |
| 6 | Dark theme (Match Windows / Light / Dark) | B1 | Built — not yet tried on Windows | [#2](https://github.com/krishnabhunia/Magpie-email-client/issues/2) |
| 7 | Calendar — Day / Week / Month / Agenda, Google Calendar sync | B2 | Approved — needs Calendar API enabled + re-sign-in | [#3](https://github.com/krishnabhunia/Magpie-email-client/issues/3) |
| 8 | Meeting invites in threads — Accept / Maybe / Decline | B3 | Built — not yet tried on Windows. Until #7: answers aren't added to Google Calendar, no "Open in Calendar", clash line only knows invites answered in Magpie | [#4](https://github.com/krishnabhunia/Magpie-email-client/issues/4) |
| 9 | Contacts (Google Contacts) + recipient auto-complete | B4 | Approved — needs People API | [#5](https://github.com/krishnabhunia/Magpie-email-client/issues/5) |
| 10 | Rules / filters | B5 | Built — not yet tried on Windows | [#6](https://github.com/krishnabhunia/Magpie-email-client/issues/6) |
| 11 | Signatures per account + quick replies | B6 | Built — not yet tried on Windows | [#7](https://github.com/krishnabhunia/Magpie-email-client/issues/7) |
| 12 | Gatekeeper + Set aside (key L) | B7 | Built — not yet tried on Windows; set aside / gate are kept on this PC (not synced to phone) | [#8](https://github.com/krishnabhunia/Magpie-email-client/issues/8) |
| 13 | Auto-delete future mail from a sender / domain | AD1–AD4 | Built — not yet tried on Windows (schema v5) | [#9](https://github.com/krishnabhunia/Magpie-email-client/issues/9) |
| 14 | OTP delete — per sender, 24 h after arrival | AD1–AD3 | Built — not yet tried on Windows | [#10](https://github.com/krishnabhunia/Magpie-email-client/issues/10) |
| 15 | Code signing | — | Parked (stay unsigned) | [#11](https://github.com/krishnabhunia/Magpie-email-client/issues/11) |
| 16 | Version number in the main window (title bar) | V1 (below) | Approved 29 Sep 2026 · Built — not yet tried on Windows | [#13](https://github.com/krishnabhunia/Magpie-email-client/issues/13) |
| 17 | Automatic CI + CD: release PR, test versions, release on merge | R1 → E1 (`docs/CI-CD.md`) | R1 built 29 Sep 2026, replaced the same day by E1 (approved 29 Sep 2026: the evict-uninstaller way, no token) · Built | [#14](https://github.com/krishnabhunia/Magpie-email-client/issues/14) |
| 18 | Release zip with only `installer/` and `portable/` folders | Z1 (`docs/CI-CD.md`) | Approved 29 Sep 2026 · Built — not yet tried on Windows | [#15](https://github.com/krishnabhunia/Magpie-email-client/issues/15) |
| 19 | "Auto update": install new versions in the background, check once a day | A1 | Approved 29 Sep 2026 · Built — not yet tried on Windows | [#17](https://github.com/krishnabhunia/Magpie-email-client/issues/17) |
| 20 | Installer updates a running Magpie by itself | I1 | Approved 29 Sep 2026 · Built — not yet tried on Windows | [#18](https://github.com/krishnabhunia/Magpie-email-client/issues/18) |
| 21 | Back up and restore every setting in one password-locked file (reinstall without setting anything up) | EX1 (below) | Design shown 29 Sep 2026 · Built — not yet tried on Windows | [#23](https://github.com/krishnabhunia/Magpie-email-client/issues/23) |
| 22 | Keep the mail in a folder you choose (another / encrypted drive) | DL1 (below) | Design shown 29 Sep 2026 · Move built — **encryption: waiting for Krishna's pick A / B / C** | [#24](https://github.com/krishnabhunia/Magpie-email-client/issues/24) |
| 23 | Download 90 days, attachments only when opened; changeable when adding an account and later | DS1 (below) | Design shown 29 Sep 2026 · Built — not yet tried on Windows | [#25](https://github.com/krishnabhunia/Magpie-email-client/issues/25) |

Anything Krishna mentions in conversation is added here automatically (his standing rule); it leaves only when he explicitly rejects it. Every open item also has a GitHub issue (title prefix `[Q<n>]`, labels `1.2.0` / `approved` / `waiting-on-krishna` / `parked`); a new queue item gets an issue too, and the issue is closed when the item ships.

## Smoke test first (1.1.1 + 1.1.2 have not been tried on the laptop)

| Check | Expect |
|---|---|
| Update | Settings → Updates or the green pill → Download → Restart now; About shows 1.1.2 |
| Old folders | `1_Forever/Godrej Hill Retreat` (kdb3107@gmail.com) shows its 2022 email; status bar "Getting older emails · … · N left" until every folder is listed; All Mail lists everything |
| About Me | Settings → About → click kri.subsc@gmail.com → "Bug found" → compose opens pre-filled |
| Send now | Send a message → green **Send now** next to Undo (and Ctrl+Shift+Enter) |
| Settings search | Type "undo" / "dark" → results → click → page opens, row flashes |
| Sidebar | Drag the edge narrow → icon rail with badges; double-click resets; Ctrl+Shift+B hides |
| Hover card | Point at a folder ~0.6 s → card with unread/total/today/… |
| Row actions | Hover a row → buttons; click a sender's initials → tick → bulk bar → Delete → Undo within 8 s |

Fix anything broken here before 1.2.0 work (queue it as 1.1.3 if it needs a release).


## Build history — steps 1–32 (from the claude.ai design chat, checked against CHANGELOG.md on 29 Sep 2026)

The chat's task list showed some steps as "Not started" / "Stopped" that did ship; the status below is what the CHANGELOG says.

| Step | Task (as named in the chat) | Chat said | Actual | Where |
|---|---|---|---|---|
| 1 | Write S1-Settings-AllOff.dc.html | Not started | Not needed — AI toggles S1–S5 shipped | 1.0.0 |
| 2 | Writing toggle-settings artboards | Stopped | Not needed — same as step 1 | 1.0.0 |
| 3 | Set up toolchain and scaffold Magpie solution | Done | Done | 1.0.0 |
| 4 | Core: accounts, OAuth, IMAP/SMTP sync, SQLite + FTS5 store | Done | Done | 1.0.0 |
| 5 | AI layer with unified toggle settings (S1–S5) | Done | Done | 1.0.0 |
| 6 | WPF UI matching approved designs | Done | Done | 1.0.0 |
| 7 | Test, publish single-file EXE, deliver + project status doc | Done | Done | 1.0.0 |
| 8 | Magpie 1.0.1 — presets, collapsible sidebar, local drafts, undo per send | Stopped | Done | 1.0.1 |
| 9 | Magpie 1.1.0 — Build 2 features + auto-delete | Not started | Build 2 done; **auto-delete not built** → queue #13 / #14 | 1.1.0 |
| 10 | 1.0.1 · AI quick-setup presets (Off/A/B/C/Custom) | Not started | Done | 1.0.1 |
| 11 | 1.0.1 · Collapsible sidebar sections, remembered state | Not started | Done | 1.0.1 |
| 12 | 1.0.1 · Local draft autosave + offline-safe drafts | Not started | Done | 1.0.1 |
| 13 | 1.0.1 · Undo toast per send (stacked) | Not started | Done | 1.0.1 |
| 14 | 1.0.1 · Test, review, push to GitHub, verify CI | Not started | Done (1.0.1 is on `main`) | 1.0.1 |
| 15 | Automatic GitHub releases (CI) | Done | Done | 1.1.0 |
| 16 | Update inside Magpie (U1) | Done | Done | 1.1.0 |
| 17 | Colourful look + icon set (C1, C4) | Done | Done | 1.1.0 |
| 18 | Icon + name buttons and toolbar settings (C3) | Done | Done | 1.1.0 |
| 19 | Folder counts unread / total (C2) | Done | Done | 1.1.0 |
| 20 | Status bar (S1) | Done | Done | 1.1.0 |
| 21 | Loading state when switching emails (R1) | Done | Done | 1.1.0 |
| 22 | Test, review, build, deliver | Done | Done | 1.1.0 |
| 23 | Find why folders miss older mail | Done | Done | 1.1.1 |
| 24 | Sync full headers list, bodies on demand | Done | Done | 1.1.1 |
| 25 | Test, review, queue as 1.1.1 and update project doc | Done | Done | 1.1.1 |
| 26 | A1 About Me block in Settings → About | Done | Done | 1.1.2 |
| 27 | SN1 Send now next to Undo | Done | Done | 1.1.2 |
| 28 | SS1 Search in Settings | Done | Done | 1.1.2 |
| 29 | H1 Sidebar resize + icon rail | Done | Done | 1.1.2 |
| 30 | H2 Folder hover details + settings | Done | Done | 1.1.2 |
| 31 | H3 Row hover actions + multi-select bulk bar + settings | Done | Done | 1.1.2 |
| 32 | Tests, build, review, doc; wait for deploy word | — | Done (1.1.2 is on `main`); on-laptop smoke test still open → queue #5 | 1.1.2 |

Only open work out of steps 1–32: auto-delete (step 9 → #13, #14) and the 1.1.1 / 1.1.2 smoke test. Everything else from here on is the queue above.

---

## V1 — Version in the main window (#16, approved 29 Sep 2026)

- Title bar, right after "Magpie": the version in small muted text, e.g. **Magpie** `1.2.0` (same grey as the view name; pre-releases show their suffix, e.g. `1.2.0-beta`).
- Hover: "Magpie 1.2.0 · released 29 Sep 2026 · click for About". Click opens Settings → About.
- After an update the number changes on restart; the green update pill (U1) still says what's waiting.
- Window title (taskbar / Alt+Tab) stays "Magpie".

---

## B1 — Dark theme (#6)

- Settings → Toolbar & buttons (or a new "Appearance" page): **Match Windows / Light / Dark**. Default: Match Windows (read the `AppsUseLightTheme` registry value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`; react to `SystemEvents.UserPreferenceChanged`).
- Add `src/Magpie.App/Themes/Dark.xaml` defining **every** key in `Light.xaml` (the XAML check enforces parity — `theme-parity`). Palette from the design: window `#111417`, title bar `#0B0D0F`, sidebar / list `#15191D`, reader `#181C20`, cards `#1E2328` with border `#2C3238`, dividers `#22272C`, text `#E8E6E1`, secondary `#9AA1A8`, tertiary `#7D858C`, selected row `#1B2A2E` with accent bar `#6CC3CF`, nav selected `#22282E`, accent (buttons) `#1F7F8C`, links `#6CC3CF` / hover `#9FE3EC`, AI purple `#6A5BC7`, tag reds/ambers slightly desaturated (`#E0645A`, `#E0A04A`).
- Set `Icons.Dark = true` so icons use their dark colour pair (already in `Icons.All`); `Brush.UpdatePill.*` and `Brush.Badge.Local.*` need dark values too.
- Reader (WebView2): mail bodies render dark too (`HtmlRenderer` gets a dark stylesheet: dark page, light text); a message with its own light background stays inside a rounded white "paper" card so it remains readable. Compose editor likewise.
- Switch at run time without restart: swap the merged dictionary, raise `Icons.Changed`, re-render the open message.

## B2 — Calendar (#7)

- New sidebar entry **Calendar** (under Folders) and a title-bar mode; the main body becomes: left rail (New event button, mini month with the visible week highlighted, CALENDARS list with a checkbox + colour per calendar — one per Google account, plus "Holidays in India"), main area with **Today ‹ ›**, the range title ("5 – 11 October 2026"), a Day / Week / Month / Agenda tab strip.
- Week view: 56 px time gutter (09:00…17:00 by default, scrollable 00–24), 7 day columns, today's column tinted `#FBFAF7`, events as rounded blocks in the calendar's colour (`#DFE9E9` fill / `#14606E` bar for the teal calendar; `#FCEBD2`/`#B45309` amber; `#EDE8F7`/`#4B3F86` purple). Unanswered invites drawn with a dashed border ("Invite · not answered").
- Sync: **Google Calendar API** (needs the API enabled in Krishna's Google Cloud project + scope `calendar.events`; re-sign-in). Two-way: create / edit / delete events; local cache in SQLite (new tables `calendars`, `events`) so the view works offline; poll every 5 min + on demand.
- New event dialog: title, date/time or all-day, calendar, attendees (from Contacts), location / Meet link, reminder, repeat (none / daily / weekly / monthly).
- Reminders: notification 10 min before (reuse the reminder/notification pipeline).

## B3 — Meeting invites in threads (#8)

- When a message carries a `text/calendar` part (METHOD:REQUEST), the reader shows an **invite card** above the body: date tile (month band / day number / weekday), title, time range with time zone, location / Meet link, organiser + attendees, a **free/busy line** ("You are free then · after 'Quarterly review' (11:00–13:00)") computed from the local calendar cache, and **Accept / Maybe / Decline** buttons (Accept teal, Decline red text).
- Replying sends the standard iCalendar reply (METHOD:REPLY with PARTSTAT) to the organiser, optionally with a note ("Add a note to my reply" checkbox), and adds the event to the calendar (Google when synced, else local).
- Works for Gmail, Outlook and IMAP accounts; "Open in Calendar" link.
- Updates / cancellations (METHOD:CANCEL, SEQUENCE bump) update or remove the local event and say so in the card.

## B4 — Contacts (#9)

- New sidebar entry **Contacts**. Left list (360 px): search box "Search name, email, company", tabs **All N / Frequent / Groups**, alphabetical sections, rows with coloured initials avatar + name + primary email. Right: header (big avatar, name, title · company; buttons New message / New event / Edit), cards DETAILS (Email, Work, Phone, Groups, Saved in) and NOTES, and a tabbed list Conversations N / Attachments N / Events N.
- Source: **Google People API** (scope `contacts`; needs the API enabled + re-sign-in); two-way (create / edit); local cache in SQLite (`contacts` table). Existing `contacts` learned from Sent mail stay as "Recent" entries.
- Compose: typing in To/Cc suggests contacts ranked by how often Krishna writes to them; unsaved people he mailed appear as "Recent". Replace the current simple suggest popup with this.

## B5 — Rules / filters (#10)

- Settings page **Rules** (nav order: General · Accounts · AI features · Rules · Signatures & replies · Appearance). Left: rule list with a toggle per rule, "New rule", reorder by drag (or ↑/↓); rules run **top to bottom on new mail, on this PC**. Right: editor.
- Editor: Rule name; "WHEN A MESSAGE ARRIVES IN [All accounts ▾] AND [all ▾ / any ▾] OF THESE MATCH" with condition rows `[field ▾] [operator ▾] [value] ✕` + "Add condition"; "THEN" action rows `[action ▾] [target]` + "Add action".
  - Conditions: From · To/Cc · Subject · Body · Has attachment · Category (People / Notifications / Newsletters) · Account. Operators: contains / is / starts with / ends with / doesn't contain.
  - Actions: Move to folder · Tag · Mark as read · Pin · Set aside · Snooze (preset) · Skip notification · Delete (to Trash).
- Footer: "Also apply to the N matching messages already in Inbox" checkbox, **Preview matches**, **Save rule**.
- Engine: run rules in `AccountSync` after new rows are inserted (before the New-mail notification, so "Skip notification" works); store rules in settings.json (`List<Rule>`); actions reuse the existing local-first operations (queued to the server).

## B6 — Signatures per account + quick replies (#11)

- Settings page **Signatures & replies**. Signatures: one tab chip per account; rich editor (B / I / U / Link / Image / Font / Colour) — an image (logo) is **embedded** in the message (cid), so it shows even when the recipient blocks remote pictures; checkboxes "Add to new messages", "Add to replies and forwards (above the quoted text)". Replaces the plain-text `Account.Signature` box under Accounts (migrate existing text).
- Quick replies: list of short texts (default "Thanks!", "Got it, will do.", "Sounds good to me."), Edit / Add. Under a thread they appear as teal outlined chips ("Quick reply · Thanks! · …"); **one click sends the reply straight from the thread**, with the normal Undo window. AI suggested replies (indigo chips) stay separate and only when that switch is on.

## B7 — Gatekeeper + Set aside (#12)

- **Gatekeeper** (off by default; Settings → General): mail from someone Krishna has never written to or received from **waits at the door** instead of landing in the Inbox. A banner at the top of the Inbox: "3 new senders want to reach you · Review". Review list: sender, first subject, count; **Allow** (they go straight in from then on; their waiting mail moves to the Inbox) / **Block** (their mail moves to Spam on the server and keeps doing so; nothing is ever deleted). Known senders = contacts + anyone in Sent + anyone allowed.
- **Set aside**: key **L** (for later) or the "Set aside" action on a conversation: it leaves the Inbox **without a date** (unlike Snooze) into one pile. Sidebar entry **Set aside · N** under Folders; the view shows cards (sender · time, subject, preview) with **Open / Done / Back to Inbox**, and "Clear all". Also in the thread toolbar (toolbar id `setaside`, icon exists in `Icons`), the list's right-click menu, hover row actions, and Rules. Implement as a flag/tag on the conversation (local + a server label/flag where the provider supports it).

## AD1–AD4 — Auto-delete and OTP delete (#13, #14)

Decisions already taken: delete = move to Trash · pinned mail is never auto-deleted · rules apply to future mail; existing mail opt-in (off by default) · runs while Magpie runs, overdue deletes happen at the next start · OTP delete is per sender only.

- **AD1 Delete drop-down**: the toolbar Delete button gets a ▾: "Delete now (Del)"; section AUTO-DELETE FUTURE EMAILS: "From <sender> after…", "From anyone at *@domain after…" (submenu: DAYS 1 · 3 · 7 · 14; MONTHS 1 · 3 · 6 · 12; YEARS 1 · 2 · 3 · 4 · 5 · 10 · 15 · 20 · 30 — shows "Next email arriving today would be deleted on <date>"), "OTP delete — this sender's emails 24 h after they arrive", "Choose sender and time…", "Manage auto-delete rules". Picking a time creates the rule at once + toast "Future emails from X will be deleted 7 days after they arrive · Undo · Edit". Same menu on the list's right-click menu.
- **AD2 Rule dialog** ("Choose sender and time…"): FROM — exact address or pattern (`*@xyz.com`); DELETE — "After a set time" (the same day/month/year grid) or "OTP — 24 hours after it arrives" (explained: for one-time codes and login links); "All my accounts" or one; "Also start the timer on the N emails already here from this sender" (off by default); preview line "An email from X arriving now (date, time) will be deleted on <when>. It will carry this tag: <tag>"; Cancel / Create rule.
- **AD3 Tagged mail**: each matched email gets a delete-date tag on its row: "OTP · deletes in 23 h 54 m", "Deletes 4 Oct 2026", "Deletes tomorrow", "Deletes 20 Sep 2036". Tag colour by time left: **red** under 48 h (and every OTP), **amber** under 30 days, **grey** later; hover shows the rule. In the reader a bar: "Moves to Trash on 4 Oct 2026 at 11:20 (in 6 days). Rule: emails from X · 7 days after arrival. **Keep this one** · Change rule". Keep this one removes the timer from that email only; pinning also keeps it.
- **AD4 Manage**: Settings → Rules → tabs **Filters N / Auto-delete N**; table FROM · DELETE AFTER · ACCOUNTS · WAITING · NEXT DELETE with Edit / Pause / Remove per rule (Paused rules: no new tags, nothing deleted until Resume). Remove asks: keep the timers already on existing emails, or clear them all. Sidebar smart folder **Deleting soon · N** (next 7 days) with "Keep all".
- Engine: `auto_delete_rules` + per-message `delete_at` column (schema v5); a timer pass every minute moves due messages to Trash (queued server op) and refreshes counts; overdue ones on start.

---

## Later ideas (not approved, not scheduled)

| Idea | Notes |
|---|---|
| Auto-delete as a row hover button | Left out of H3 until #13 exists |
| Signed installer | Krishna chose to stay unsigned for now |

## EX1 · DL1 · DS1 — Backup, mail folder, downloads (#21–#23, shown 29 Sep 2026)

Screens: https://claude.ai/artifact/AYSaARGCjHk7ihh5Uao2kr (six artboards).

**EX1 Back up and restore every setting.** Settings → General → *Back up your settings*: **Save a backup…** asks for a
password (8+ characters, typed twice), then where to save `Magpie settings <date>.magpie-backup`. The file is
PBKDF2-SHA256 (600 000 rounds) → AES-256-GCM. In it: settings.json (every setting, all accounts, rules, signatures,
quick replies, templates, tags, Gatekeeper lists, look, layout, updates …), every secret (passwords, Google / Microsoft
sign-ins, AI key) and the mail folder. Not in it: emails. **Restore from a backup…** (also a link in Add account):
file → password → what it holds → *Replace my settings and restart*. The restore is staged (protected with DPAPI) and put
in place at the next start before anything loads; the old settings.json is kept as `settings.json.before-restore`.

**DL1 Where your mail is kept.** Settings → General: folder, size, free space, *Open folder*, *Move…*. Moving restarts
Magpie and copies `mail.db` (+ journal files) and `messages\` with a progress window, checks every file, then removes the
old copy. A folder that already has Magpie mail: *Use the mail that's there* / *Replace it*. A chosen folder that can't be
reached at start: *Try again* / *Choose another folder…* / Cancel (close) — never an empty mailbox in its place.
Settings, sign-ins, log and caches stay in the Windows profile (`mail-folder.txt` there names the mail folder).
Encryption, pick one: **A** encrypted drive (BitLocker / VeraCrypt, recommended, works now), **B** Magpie's own
password at every start, **C** Windows file encryption (Pro only, tied to the Windows account).

**DS1 Downloading.** Add account and each account in Settings → Accounts: *Download emails from the last* 30 days /
**90 days** / 6 months / 1 year / Everything, and *Attachments*: **Only when I open the email** / Download them too.
Emails inside the window download in the background (150 per check, inbox first, then Sent, then the other folders; not
Trash, Junk or Drafts). "Only when I open": just the text, HTML and any invite are fetched (the server's BODYSTRUCTURE lists
the attachments), and opening such an email fetches the whole email. Older emails stay listed and download when opened.

