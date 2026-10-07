# Magpie — roadmap and approved designs for 1.2.0

Everything here was **approved by Krishna on 28 Sep 2026** (designs B1–B7, AD1–AD4 on the design canvas). This file is the text version so Claude Code can build without the canvas. Build order (default, Krishna may reorder): dark theme → rules → signatures + quick replies → Gatekeeper + Set aside → auto-delete + OTP delete → invites → contacts → calendar.

Ship as one release **1.2.0** or as 1.2.0 / 1.2.1 / … — releasing is automatic: merging a PR that sets a new version publishes it (design E1, `docs/CI-CD.md`).

## Queue

| # | Item | Design | Status | GitHub |
|---|---|---|---|---|
| 5 | Fixes from real Gmail use | — | Waiting on Krishna's `magpie.log` | [#1](https://github.com/krishnabhunia/Magpie-email-client/issues/1) |
| 6 | Dark theme (Match Windows / Light / Dark) | B1 | Built — not yet tried on Windows | [#2](https://github.com/krishnabhunia/Magpie-email-client/issues/2) |
| 7 | Calendar — Day / Week / Month / Agenda, Google Calendar sync | B2 | Built (3.0.0) — not yet tried on Windows; each Google account must sign in again once (calendar scope) | [#3](https://github.com/krishnabhunia/Magpie-email-client/issues/3) |
| 8 | Meeting invites in threads — Accept / Maybe / Decline | B3 | Built — not yet tried on Windows. Calendar is now built (#7); still to do: an answer in the email also shown in Magpie's calendar right away, "Open in Calendar", clash line from the calendar | [#4](https://github.com/krishnabhunia/Magpie-email-client/issues/4) |
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
| 22 | Keep the mail in a folder you choose (another / encrypted drive) | DL1 (below) | Design shown 29 Sep 2026 · Encryption option A approved 29 Sep 2026 · Built — not yet tried on Windows | [#24](https://github.com/krishnabhunia/Magpie-email-client/issues/24) |
| 23 | Download 90 days, attachments only when opened; changeable when adding an account and later | DS1 (below) | Design shown 29 Sep 2026 · Built — not yet tried on Windows | [#25](https://github.com/krishnabhunia/Magpie-email-client/issues/25) |
| 24 | Settings: "Apply" next to "Save and close" | (below) | Shown 29 Sep 2026 · Built — not yet tried on Windows | [#27](https://github.com/krishnabhunia/Magpie-email-client/issues/27) |
| 25 | Several AI connections: add, customise, test, delete | AI2 (below) | Shown 29 Sep 2026 · Built — not yet tried on Windows | [#28](https://github.com/krishnabhunia/Magpie-email-client/issues/28) |
| 26 | Folder details card delay: 50 to 1000 ms in steps of 50 | (below) | Built | [#29](https://github.com/krishnabhunia/Magpie-email-client/issues/29) |
| 27 | Better layout for the buttons on email rows | RB1 (below) | RB1 approved 29 Sep 2026 · Built — not yet tried on Windows | [#30](https://github.com/krishnabhunia/Magpie-email-client/issues/30) |
| 28 | Bug: can't type a signature; get the Gmail signature | B6 fix (below) | Built — not yet tried on Windows | [#31](https://github.com/krishnabhunia/Magpie-email-client/issues/31) |
| 29 | Send a test notification | (below) | Built | [#32](https://github.com/krishnabhunia/Magpie-email-client/issues/32) |
| 30 | Bug: emails reload every time they're opened — reader loads from this PC first | RL1 (below) | Built 30 Sep 2026 — not yet tried on Windows | [#33](https://github.com/krishnabhunia/Magpie-email-client/issues/33) |
| 31 | Settings: Apply · Apply and Close · Cancel, enabled only when something changed; Apply is the default | AP1 (below) | Built 30 Sep 2026 — not yet tried on Windows | [#34](https://github.com/krishnabhunia/Magpie-email-client/issues/34) |
| 32 | Collapsible: Sign-in apps, each account card, each template | CL1 (below) | Built 30 Sep 2026 — not yet tried on Windows | [#35](https://github.com/krishnabhunia/Magpie-email-client/issues/35) |
| 33 | "Sign in again" enabled only when the sign-in has expired | (below) | Built 30 Sep 2026 | [#36](https://github.com/krishnabhunia/Magpie-email-client/issues/36) |
| 34 | Your details per account: contact number, job title, company | AC1 (below) | Built 30 Sep 2026 | [#37](https://github.com/krishnabhunia/Magpie-email-client/issues/37) |
| 35 | Default signature: "Thanks and Regards", name, contact number | SG1 (below) | Built 30 Sep 2026 | [#38](https://github.com/krishnabhunia/Magpie-email-client/issues/38) |
| 36 | Version rule: x = feature added / big UI change, y = feature changed, z = fix; a new number with every update | VB1 (below) | Approved 30 Sep 2026 · Built | [#41](https://github.com/krishnabhunia/Magpie-email-client/issues/41) |
| 37 | Settings: "Cancel" becomes "Close" | (below) | Built 30 Sep 2026 | [#44](https://github.com/krishnabhunia/Magpie-email-client/issues/44) |
| 38 | Bug: picking another email still shows the previous one — switch at once (or blank) | (below) | Built 30 Sep 2026 — not yet tried on Windows | [#45](https://github.com/krishnabhunia/Magpie-email-client/issues/45) |
| 39 | Bug: emails still come from the server and open slowly — superfast loading | (below) | Built 30 Sep 2026 — not yet tried on Windows | [#46](https://github.com/krishnabhunia/Magpie-email-client/issues/46) |
| 40 | Check that the chosen days (e.g. 90) really are on this PC | (below) | Built 30 Sep 2026 — tested against a real IMAP server | [#47](https://github.com/krishnabhunia/Magpie-email-client/issues/47) |
| 41 | Row buttons on hover not arranged properly | RB4 (below) | RB4 approved 30 Sep 2026 · Built — not yet tried on Windows | [#43](https://github.com/krishnabhunia/Magpie-email-client/issues/43) |
| 42 | Release workflow closes the issues a release ships | (CI) | Built 30 Sep 2026 — runs first on the next release | [#48](https://github.com/krishnabhunia/Magpie-email-client/issues/48) |
| 43 | Hover menus on email address, subject and attachments in the reading pane | HM1 (HTML) | Approved 30 Sep 2026 (all options) · Released 3.0.0 — tried in a browser, not yet on Windows; E6 (Add to contacts) comes with #9 | [#50](https://github.com/krishnabhunia/Magpie-email-client/issues/50) |
| 44 | Bin / Trash: Empty Trash, move to folder, more options | TB1 (HTML) | Approved 1 Oct 2026 · Built (4.0.0) — server side tested against Dovecot; not yet tried on Windows | [#51](https://github.com/krishnabhunia/Magpie-email-client/issues/51) |
| 45 | Delete options that include past emails | DP1 (HTML) | Approved 1 Oct 2026 · Built (4.0.0) — not yet tried on Windows; folded into the DX1 dialog (#51) | [#52](https://github.com/krishnabhunia/Magpie-email-client/issues/52) |
| 46 | Checkboxes on email rows + select by filter | SL1 (HTML) | Approved 1 Oct 2026 · Built (4.0.0) — not yet tried on Windows | [#53](https://github.com/krishnabhunia/Magpie-email-client/issues/53) |
| 47 | Show when an auto-deleted email will be deleted: on hover, on its row, in the reading pane | DD1 (HTML) | Design shown 1 Oct 2026 — waiting for approval | [#55](https://github.com/krishnabhunia/Magpie-email-client/issues/55) |
| 48 | Bug: the same auto-delete rule can be added twice | (fix) | Built (4.0.0) — one rule per sender, doubles merged at start | [#55](https://github.com/krishnabhunia/Magpie-email-client/issues/55) |
| 49 | Bug: menus show a vertical cut line (submenus in the Windows look) | (fix) | Built (4.0.0) — not yet tried on Windows | — |
| 50 | Update button at the top-right of the title bar (always visible; replaces the green pill) | UB1 (HTML) | Approved 7 Oct 2026 — UB1-a · Built — not yet tried on Windows | [#59](https://github.com/krishnabhunia/Magpie-email-client/issues/59) |
| 51 | Delete three ways: Shift+Del forever · past emails from a sender/domain keeping the last N · future auto-delete, one dialog | DX1 (HTML) | Approved 7 Oct 2026 — DX1-A + DX1-B1 · Built — not yet tried on Windows | [#60](https://github.com/krishnabhunia/Magpie-email-client/issues/60) |

Anything Krishna mentions in conversation is added here automatically (his standing rule); it leaves only when he explicitly rejects it. Every open item also has a GitHub issue (title prefix `[Q<n>]`, labels `1.2.0` / `approved` / `waiting-on-krishna` / `parked`); a new queue item gets an issue too, and the issue is closed when the item ships.

## Smoke test first (1.1.1 + 1.1.2 have not been tried on the laptop)

| Check | Expect |
|---|---|
| Update | Settings → Updates or the title-bar Update button (5.0.0; the green pill before) → Download → Restart now; About shows the version |
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
- Set `Icons.Dark = true` so icons use their dark colour pair (already in `Icons.All`); `Brush.Update.*` (the UB1 button; `Brush.UpdatePill.*` before 5.0.0) and `Brush.Badge.Local.*` need dark values too.
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

Decisions already taken: delete = move to Trash · pinned mail is never auto-deleted · rules apply to future mail; existing mail opt-in (the "Emails already here" tick / `AutoDeleteIncludePast` for the not-yet-due ones) · runs while Magpie runs, overdue deletes happen at the next start · OTP delete is per sender only.

**Since 5.0.0 (design DX1, #51) AD1 and AD2 are superseded** — kept here for the history:

- **AD1 Delete drop-down → DX1 menu**: Delete now (Del) · Delete forever… (Shift+Del) · EMAILS FROM <sender>: "Delete all N already here…", "Keep only the last week, now and later…", "Delete after a set time… (rule only)", "OTP delete — 24 h after arrival" (acts at once) · EMAILS FROM ANYONE AT *@domain: the first two · Keep this one · Manage auto-delete rules. Every "…" item opens the one dialog pre-filled. Same list on the list's right-click menu ("Delete emails from…"), the address hover card (E10) and Settings → Rules → Auto-delete → New / Edit. (Before 5.0.0: the "From <sender> after…" day/month/year submenus, "Delete … older than ▸" and the "Include the N emails already in the Inbox" toggle.)
- **AD2 Rule dialog → DX1-B1 "Delete emails from…"**: FROM (address or `*@xyz.com`, live count "240 emails here from them"); KEEP THE LAST chips 1 day · 3 days · 1 week · 2 weeks · 1 month · 3 months · 6 months · 1 year · Nothing · OTP 24 h · More… (the AD2 grid); ☑ **Emails already here** — "delete the 212 older than 1 week now (oldest 12 Mar 2024). Keeps 28 from the last 1 week, pinned ones, Sent and Drafts."; ☑ **Emails that arrive later** — "delete each one 1 week after it arrives (an auto-delete rule…)"; ACCOUNTS; preview "Next email from X arriving today 14:30 would be deleted on … and carry the tag <tag>". Nothing greys FUTURE, OTP greys PAST. Button: "Delete 212 now" / "Create rule" / "Delete 212 now and keep deleting" (red when deleting). The rule exists at once; the past delete waits 8 s (toast Undo · Edit rule; Undo also removes a rule that run created). Existing-mail timers cover every folder the past delete covers (all but Trash, Spam, Sent, Drafts) — only the emails not yet past the time; those already past it go only with the PAST tick.
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
Encryption: **A — approved 29 Sep 2026**: keep the mail folder on an encrypted drive (BitLocker / VeraCrypt); Magpie
just uses it and waits for the drive to be unlocked at start. Not chosen: B (Magpie's own password at every start),
C (Windows file encryption, Pro only, tied to the Windows account).

**DS1 Downloading.** Add account and each account in Settings → Accounts: *Download emails from the last* 30 days /
**90 days** / 6 months / 1 year / Everything, and *Attachments*: **Only when I open the email** / Download them too.
Emails inside the window download in the background (150 per check, inbox first, then Sent, then the other folders; not
Trash, Junk or Drafts). "Only when I open": just the text, HTML and any invite are fetched (the server's BODYSTRUCTURE lists
the attachments), and opening such an email fetches the whole email. Older emails stay listed and download when opened.

## Items #24–#29 (asked 29 Sep 2026)

Screens: https://claude.ai/artifact/AYSaARGCjHk7ihh5Uao2kr (rows "Your six new items" and "Signature fix, Apply …").

- **#24 Apply.** Settings bottom bar: Cancel · Apply · Save and close. Apply saves and keeps Settings open ("✓ Saved" for 2.5 s).
- **#25 AI2 Several AI connections.** Settings → AI → AI connections: rows (name, "In use" badge, provider · model, last test
  result) with a radio to pick the one in use, Edit, Delete; "+ Add an AI connection". The form below edits the chosen row
  (name, provider, endpoint, model, API key, Test connection). `AiSettings.Connections` + `ActiveId`; Provider / Endpoint /
  Model mirror the one in use, so everything that used one provider still works. Keys: `SecretVault.AiKeyFor(id)`; the first
  connection (made from older settings, id "main") keeps the old key name.
- **#26** "Appears after": 50, 100, … 1000 ms; other values round to the nearest 50.
- **#27 RB Row buttons — RB1 approved 29 Sep 2026.** RB1 floating bar on the right, centred on the row, 28 px buttons grouped
  Archive · Delete | Snooze · Read | ⋯; date and subject stay visible. RB2 buttons with words on the last line while hovered.
  RB3 a slim column always on the right (3 buttons + ⋯).
- **#28 B6 fix.** Settings → Signatures shows a preview; "Edit signature…" opens `SignatureEditorWindow` (a normal WebView2, like
  compose; plain-text box if WebView2 can't start). "Get my Gmail signature" (Google sign-in accounts) reads Gmail's sendAs
  settings (`GmailSignature`); needs the Gmail API on in the Google Cloud project, and says so if it's off.
- **#29** Settings → Notifications → "Send a test notification": a sample balloon (+ sound if on), from the page's switches.

## Items #30–#35 (asked 30 Sep 2026)

Screens: https://claude.ai/artifact/AYSaARGCjHk7ihh5Uao2kr (row "30 Sep: reader from this PC, Settings buttons, details").

- **#30 RL1 Reader loads from this PC.** Cause: the pictures inside an email were never kept with its text, so every opening
  read the whole email again (server, or the message file) and drew the page twice. Now `MessageBody.Images` (cid → data
  URI, ≤ 4 MB, `bodies.images`, schema v6) is filled when an email is downloaded — text-only accounts fetch the inline
  picture parts too (`SaveTextOnlyAsync`), never attachments; `MimeText.NeedsDownload` is true only for a picture not kept
  yet; `LoadAsync` re-saves a pre-2.2.0 body with its pictures from the message file (`refreshBody`). The reader renders
  once from the store; "Loading…" only when `HasUncachedBody`; a 24-entry page cache keyed by a fingerprint of the rows.
- **#31 AP1 Apply buttons.** `SettingsChanges.cs`: `Snapshot()` builds the settings as Apply would save them (pure twin of
  `SaveCore`, rules via `ProjectRules` on a copy) and compares with the baseline taken at load / after Apply; every property
  and observable list is watched (debounced 200 ms). `HasChanges` enables Apply (IsDefault) and Apply and Close.
- **#32 CL1 Collapsible.** `Expander.Group` for Sign-in apps (closed), each account card (header: dot, address, kind,
  "sign-in expired", Sign in again / Remove; open when it's the only account) and each template (header: its name; a new
  one starts open).
- **#33** `EditableAccount.NeedsSignIn` from `StatusOf(id).State == NeedsSignIn`, kept fresh from `StatusChanged`.
- **#34 AC1** `Account.Phone / JobTitle / Company` (Add account, account card "Your details"; in backups).
- **#35 SG1** `Composer.DefaultSignatureHtml`: "Thanks and Regards", <b>name</b>, job title · company, number. Put in on
  Add account and once by `Normalise` for accounts with no signature (`SignatureDefaultApplied`); never replaces one.

## #36 VB1 — Version rule (approved 30 Sep 2026)

Krishna: *"All software must show their respective version number in home window page. Version number must be changed
as soon as there is an update. x to be updated when there is a major change with UI/UX or when there is an added feature,
y when there is a feature modification and z when there is a bug or error fix."*

- Version in the main window: already there (design V1, title bar); it follows the built version.
- CHANGELOG: `## Next version (not released yet)` with lines under `### New` (x), `### Changed` (y), `### Fixed` (z).
  `release_prep.py apply` works the number out from the newest dated release + the biggest kind.
- CI: `needs-version` fails a PR that changes `src/`, `installer/`, `Directory.Build.props`, `global.json` or `Magpie.sln`
  while its version is already released; `check` fails a number that breaks the rule. Release notes show the kinds as
  **New** / **Changed** / **Fixed**.
- Releases up to 2.2.0 keep their numbers (renumbering would break auto-update). Evict needs the same rule from its own chat.

## Items #37–#41 (asked 30 Sep 2026)

- **#37** Settings footer: Close · Apply · Apply and Close (Close = discard, as Cancel did).
- **#38** `ThreadViewModel.Show` replaces the page body at once (script, no navigation) with the new conversation's subject and
  sender (`LoadingBody(downloading: false)`; "Loading…" only when it must be downloaded). After a conversation is shown,
  `WarmNeighboursAsync` prepares the next three and the one above (`MainViewModel` sets `Reader.Neighbours`): pages built
  into the page cache off the UI thread; emails not here are asked of the sync first (`WantBodies`).
- **#39** Causes and fixes: `MimeText.NeedsDownload` counted any inline part (calendar, signature file) and pictures over the
  4 MB cap, so those emails were read again on every opening — now only a picture the HTML shows (`cid:`), and never once
  `MessageBody.ImagesComplete` (schema v7, `bodies.images_done`) says every picture was looked at; bigger pictures come from
  the message file on this PC only. `MailStore.SaveBody` gives the body to the email's copies in other folders (same
  Message-ID; Gmail Inbox + All Mail), `ShareBodiesWithCopies` for older data; `HasUncachedBody` counts an email as here when
  any copy is. Opening an email not here (`AccountSync.FetchBodyAsync`): the message file, else whole when attachments come
  with emails or it is ≤ 512 KB, else text and pictures only. Text-only downloads keep an invite that came as an .ics file;
  an invite read from the message file is saved with the text.
- **#40** The window was 150 emails per full check (every 5 min), skipped emails ≥ 5 MB and fetched Gmail copies twice. Now
  `PrefetchWindowAsync` runs every round, back to back until the window is complete (`PrefetchPerRound` 200), emails
  ≤ 512 KB (all < 5 MB when attachments come too) are fetched 25 per request (`ImapFolder.GetStreamsAsync`), bigger ones get
  text and pictures; emails the server no longer has are not retried. `MailStore.WindowProgress` (each email once, Trash /
  Spam / Drafts left out) feeds the account card line (`AccountSync.DescribeWindow`) and the status "Downloading emails to
  this PC… N left". Integration test `Download_window_fills_in_batches_and_emails_open_from_this_pc` (Dovecot).
- **#41 RB4 approved** (30 Sep 2026) and built: in `MainWindow.xaml` the first line's date slot holds the date (`RowDate`
  style, hidden) and the buttons (`RowBar`: every chosen action, 26 px, plain coloured icons 16 px, 4 px gaps, ⋯ last,
  negative margins so the row doesn't grow); the full date is a tooltip (`ThreadItem.DateTip`). Replaces RB1's floating
  bar. Designs shown: RB4 (icons take the date's place on the top line), RB5 (centred bar, text fades), RB6 (a column
  kept free, 2 × 2), RB7 (icon + word in place of the preview): https://claude.ai/artifact/AYSaARGCjHk7ihh5Uao2kr
