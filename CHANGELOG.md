# Changelog

<!-- Version rule (design VB1): put the next version's lines at the top, under the heading
    "## Next version (not released yet)" and, inside it, under
    "### New"      a feature added, or a big change to how Magpie looks or works  -> x.0.0
    "### Changed"  a feature changed                                             -> x.y.0
    "### Fixed"    a bug or error fixed                                          -> x.y.z
    then run: python common/scripts/release_prep.py apply --date YYYY-MM-DD   (it works the number out) -->

## 19.0.0 (14 Nov 2026)
<!-- closes: #153 #154 #156 -->
### New
- **See every To, Cc and Bcc address of an email.** The line under the sender no longer cuts names with "…". It shows the first names in full, then "+5 more · cc 4 · bcc 1", and a **Details** button that is always there. Details opens a card with From, every To, Cc and Bcc address on its own line (name and address), the full date, the subject and the folder the email is kept in. Pointing at an address in the card opens its address card, as in the header.
- **Copy one address, one group, or all of them.** In the Details card every address has its own copy button. To, Cc and Bcc each have a **Copy** button when they hold two or more addresses; it copies that group, one address per line. **Copy all 13 addresses** copies every group under the headings To, CC and BCC, one address per line with an empty line between the groups, and it pastes that way in Notepad too. A group without addresses is left out. The button turns into a tick for two seconds and the card says what was copied.
- **Bcc is kept for the emails you sent.** Magpie never saved the Bcc line, so it could not show it. New sent emails show it at once. For sent emails already on this PC Magpie reads the Bcc line from the server in the background, up to 1,000 emails at each mail check, without downloading them again. An email you received never carries a Bcc line.
- **Every email with one address, received and sent.** The address card now counts every email with the address (all, received, you sent, copied in) and starts with three lines and their numbers: **All emails with**, **Emails from** and the new **Emails you sent to**. Each opens a list headed "Emails with …": one row per email saying who it went from and to, its folder and a Bcc mark, four filters (All · From · You sent · Copied in), an **Each email / Conversations** switch and **Back to** where you were. Clicking a row opens its conversation at that email.
- **Search the server too.** While an account is still getting older emails, the list offers to ask the mail server for emails with that address that are not on this PC yet, and lists them.

### Changed
- **The list of a person's emails hides nothing.** "Unread" and snooze no longer filter it, and an email where the person is only in Bcc is found. Trash and Spam are left out, with **Include them** under the list.
- **Searching by `to:` and `with:` also looks in Bcc**, and a person in Bcc is found by name.
- **The browser's own tooltip no longer covers the address card** in the reading pane.
- In the **Each email** list the rows have no tick boxes or row buttons, because those act on whole conversations. Switch to **Conversations** to tick and act, or use the toolbar on the open conversation.

## 18.2.2 (9 Nov 2026)
<!-- closes: #151 -->
### Fixed
- **Drag files into a new message.** Files dropped anywhere on the compose window are attached, also on the message text and on the To, Cc, Bcc and Subject boxes. Before, only the toolbar and footer took them. A dropped folder is not attached and Magpie says why.
- **Every formatting button shows its icon.** Strikethrough, text colour, highlight, align left and centre, indent, outdent, remove link, table, picture, resize picture, emoji, undo and redo showed empty boxes. The numbered-list button showed a checklist icon; it now shows "1.".
- **Focus and Studio are readable in the title bar**, and the one that is on stays highlighted.
- **Contacts… sits next to each recipient box**, with Cc / Bcc and Hide, so the To, Cc and Bcc labels line up with their boxes.
- **Plainer attachment line:** "No attachments · size shown once the draft is saved".

## 18.2.1 (9 Nov 2026)
<!-- closes: #149 -->
### Fixed
- **Magpie keeps looking for updates after you update it.** Updating to a newer version in the version manager used to pin that version and switch Auto update off, so the "Update to vx.y.z" button never came back after a restart. Now only going back to an older version pins it; moving forward keeps your Auto update choice and ends any pin from an earlier roll back. A copy left pinned by an earlier update gets its automatic checks back the first time this version starts.
- **A pin left behind by an older version no longer stays.** When Magpie runs a newer version than the one pinned (for example after the installer was run following a roll back), the old pin is cleared and automatic checks resume.
- **Cancelling a roll back no longer stops update checks.** Cancelling the download or install of an older version left Magpie thinking that version was on offer, so it stopped checking for updates until the next start.
- **Magpie for Mac finds a new stable version behind many test versions.** It read only the 20 newest GitHub releases, so a stable release followed by many test versions was never offered. It now reads all of them, as the Windows app does.

## 18.2.0 (9 Nov 2026)
<!-- closes: #132 -->
### Changed
- **Writing emails settings.** Settings → General → **Writing emails** decides how every new message window starts: check spelling while you write, fold the quoted message in replies, ask when you forget an attachment, always ask for a read receipt, and what a pasted picture does (ask each time, put it in the message, or save and attach it). Every switch starts as Magpie works today.
- **Replies go to.** Each account in Settings → Accounts can have one **Replies go to** address. New messages, replies and forwards from that account start with it as Reply-To; you can still change it while writing, and changing From swaps it for the new account's own unless you typed one yourself. Reopened drafts keep what they had.

## 18.1.0 (9 Nov 2026)
<!-- closes: #142 -->
### Changed
- **The version manager is back to how it was before 14.1.0** (Krishna, 5 Nov 2026). The From → To confirm screen, the "skips the latest stable" note, the Updates / Version history tabs and the "On this PC" marks are removed: you choose Update (newer) or Roll back (older) in the drop-down again, and a short Yes/No question confirms the exact version. Everything else stays — choosing exact stable or beta versions, compatibility checks, backups, the version pin, permanent beta tags and **View full implementation details**.
- Windows only; the Mac app is unchanged.

## 18.0.0 (9 Nov 2026)
<!-- closes: #138 -->
### New
- **Info window.** The **ⓘ** next to the version at the top of the window (or **F1**; on the Mac also ⌘I and Magpie → Magpie Info…, and **Info** in Settings → About) opens **Magpie · Info** with four tabs:
  - **This build** — the version, Beta or Stable, the pull request it was made from, the commit, when it was built, its GitHub tag, the download's name and how it is installed. A test version says "This is a test build. It is beta 007 of Magpie 18.0.0, made from a pull request."
  - **What's implemented** — every feature with the version it arrived in, searchable, with filters for what is new in this version, Windows only and also on the Mac.
  - **What's new** — this version's lines from the changelog (for a test version: the version being tested), with **Full release notes** on Windows (the installed release's full implementation details) and a link to all releases.
  - **Under the hood** — what this copy is made with (.NET, the window toolkit, the mail and storage libraries), where it keeps your data, and how it updates, with **Open data folder** and **Open log file**.
- **Copy build info** puts two lines on the clipboard (version, pull request, commit, build time; Windows or macOS version, how it is installed, .NET) — and the feedback email in Settings → About Me now carries the same lines.
### Changed
- **One ⓘ in the title bar.** The separate What's New button is gone: its full release notes are now in Info → What's new → **Full release notes**.
- **Beta chip.** A test version shows a small **Beta** chip next to its version in the title bar.
### Fixed
- **A portable copy of a test version stays portable.** `Magpie_18.0.0-beta007.exe` (and the older long test names) taken from the zip now keeps its data next to it like `Magpie_18.0.0.exe` does, instead of using the profile folders.

## 17.0.0 (9 Nov 2026)
<!-- closes: #78 -->
### New
- **Browse and share existing Google Drive or OneDrive files from Compose.** Connect independently approved existing-file access, search owned cloud files, choose an existing file, edit its link title, review named To/Cc recipients and View/Edit access, then explicitly confirm before sharing or inserting a link. Magpie rechecks the original file identity and permissions, rejects public/inherited/unreviewed grants, and retains verified access records for scheduled sending. Existing files are never deleted during cancellation; partial sharing shows a recovery link to review permissions. Regular attachments and private-copy cloud uploads remain available.

## 16.1.0 (8 Nov 2026)
<!-- closes: #146 -->
### Changed
- **Review template values before inserting.** Templates now show the expanded subject and message text before anything changes. Choose whether to replace an existing subject, then insert exactly what was reviewed. Use `{company}` or `{{company}}` for the sending account's company, and either single or double braces for the other variables. Missing values remain visible so you can fill them in.
- **Attach an existing email directly.** Studio → Files has an explicit .eml picker for saved email messages.
### Fixed
- **Keep two differently capitalised attachment filenames distinct.** The Windows compose list now preserves their original order and outgoing names through draft editing, saving and optimized-image replacement on case-sensitive folders.

## 16.0.0 (7 Nov 2026)
<!-- closes: #133 #134 #135 #144 -->
### New
- **Share a file through Google Drive or OneDrive.** Choose a local file, a connected cloud account, named To/Cc recipients and View or Edit access, then confirm before uploading and inserting its link. Magpie creates a new copy, checks the granted access and saves the draft. Failed or cancelled work removes the new copy where possible and identifies any copy that still needs attention. Files are limited to 100 MB; anonymous sharing and hidden Bcc-recipient grants are unavailable. Provider consent, tenant policy and recipient-account requirements still apply.
- **Sign or encrypt outgoing messages.** Select S/MIME or OpenPGP and import compatible personal identities and recipient public keys in the account's key manager. Recipient fingerprints require explicit confirmation. Protection is applied before a message enters the outbox, includes attachments, and is retained through scheduling and retry. Keys are protected for the operating-system user; queued recovery data is protected too. Editable drafts remain unencrypted, outer Subject/address headers stay visible, and encrypted Bcc messages require separate sends.
- **Inspect protected messages explicitly.** Open a message's security action to decrypt in memory and inspect signature validity, sender matching and locally confirmed fingerprints. The separate reader blocks external resources, links and scripts; plaintext is not added to the normal message cache or AI context. Saving a decrypted attachment requires an explicit choice. Certificate-chain trust and online revocation are not claimed.
- **Request delivery notifications separately from read receipts.** Studio's new option requests success/failure reports when the SMTP server supports them. The sent-message and activity views retain the request result and server response. Unsupported servers send normally with an explicit unavailable status; rejection is kept as a send failure. A successful send or a requested notification is never labelled proof of delivery or reading.
- **Choose an authorised sender alias.** Add up to 20 provider-authorised aliases per account in Settings and choose Send as while composing. Drafts, history, scheduled mail and Undo retain that identity while authentication stays with its account. Templates, AI author names and external-recipient checks follow the selected sender. A removed identity cannot silently fall back to the primary address.
- **Organise templates into categories.** Add an optional category in Settings or when saving a template. The compose menu groups those templates while older uncategorized entries stay accessible; changing only a category enables Apply.
- **Translate selected text with a review step.** Choose a target language, including Hindi or Marathi, and explicitly press Translate. Your configured AI returns a preview; the selected message text changes only when you choose Replace selection. Translation never sends mail automatically.
- **Find the provider's confidential-message controls.** Studio explains Gmail and Outlook protection capabilities and can open the provider's website or help. The draft stays in Magpie; opening the link copies and sends nothing. Native provider-managed expiry, access revocation and rights management are not implemented by the SMTP sender.
### Changed
- **See when scheduled mail can actually leave.** Send-later presets, custom time selection and scheduled-message status show the local timezone and UTC offset. The screens explain that Magpie must be running and online and that an overdue message waits until it can be sent. Ambiguous or unavailable local times are rejected with a request to choose another time.
### Fixed
- **Undo an applied AI draft.** Inserting or replacing the written body is one editor change that Undo and Redo can reverse, in both the rich editor and plain-text fallback, while retaining the signature and quoted message.
- **Undo table deletions.** Deleting a table, row or column participates in the editor's native history, so Undo restores the removed rich content and Redo repeats the change while retaining the surrounding message.
- **Find phrases across formatting.** Find and Replace recognise visible phrases split by bold, italic or other inline formatting. Paragraph breaks, hidden text and noneditable content remain separate, and Unicode matches retain their original character positions.
- **Keep retained text safe after plain-text conversion.** When the signature and quoted-message boundaries are no longer known, whole-body AI actions leave the draft unchanged and explain how to rewrite selected text. That restriction survives saving, history, scheduling and Undo Send; selected-text edits retain native Undo and Redo.
- **Know when automatic retries have stopped.** A message that reaches the retry limit asks for review and a manual retry instead of continuing to promise an automatic retry.
- **Keep sender configuration stable during editing.** An unrelated Settings save preserves the selected alias and current composer state. Incomplete alias input reports validation without crashing settings change tracking or the default-signature action.

## 15.0.0 (5 Nov 2026)
### New
- **Compose in three ways.** Keep the balanced email editor, select Focus for writing with fewer distractions, or open Studio for attachment details, a pre-send review and message options. Switching modes keeps the draft intact.
- **Attachments you can find.** Attach is visible at the top and bottom of the editor, with a Studio drop area, file-type icons, safe Open actions for common document/picture types, and Remove all.
- **Preview attached files.** Picture thumbnails appear beside attachments, and Preview opens images, PDFs and common text files inside Magpie without launching their usual apps.
- **Preview forwarded attachments.** Forwarded pictures, PDFs and text attachments can also open inside Magpie when the file type is supported. Temporary picture/PDF copies are isolated and removed after closing the preview.
- **Personalise saved quick templates.** Insert {{first_name}}, {{last_name}}, {{full_name}}, {{recipient_email}}, {{sender_name}}, {{sender_email}}, {{subject}}, {{date}} or {{time}} in a template. Magpie fills supported variables from the current compose window when inserted, leaving missing values visible.
- **Choose from multiple signatures.** Add and edit up to 20 named rich signatures per account in Settings, then replace the signature in a message without changing the message text or quoted replies. The existing automatic signature stays as the default.
- **Structured Office previews.** Preview local and forwarded `.docx`, `.xlsx` and `.pptx` files with Word headings and tables, named Excel sheets with cell positions and cached formula results, or ordered PowerPoint slides and tables. A plain-text tab remains available. Previews work locally in light/dark mode, with scripts, macros, formulas, downloads and external resources disabled. Images, charts, exact page/slide layout, legacy formats and macro-enabled formats are not rendered; size and expansion limits protect large files.
- **Local draft revision history.** Open Draft history beside Save draft to preview and restore the recipients, subject, message, sending options and captured attachments of an earlier revision. Restoring first checkpoints the current content, asks for confirmation and never sends mail; clearing history keeps the open and saved draft. Keep up to 30 revisions per draft for 30 days on this PC, within a 32 MB per-revision and 200 MB total payload limit. Attachment snapshots remain recoverable if the original file is moved or deleted.
- **Compress pictures before sending.** Save a resized JPEG or PNG copy and attach it, with an option for quality versus smaller file size. The original picture is never overwritten; animated GIFs are left unchanged.
- **Edit and remove links.** Click a link or place the cursor in it to open an Edit / Remove / Close bubble. Changing its address preserves the visible text and rich formatting; removing it keeps the words. Unsafe links and stale edit selections are rejected, and the bubble is never included in sent email.
- **Choose text and highlight colours.** Separate palettes offer 16 named colours each, custom six-digit hexadecimal colours and a No highlight action. A captured text selection is restored after using the palette; a changed or restored draft rejects stale formatting requests.
- **Search common emoji and symbols.** The offline picker includes 56 named choices, keyword search, six categories and a Recent view. Up to 24 recent choices stay on this PC; emoji insert as Unicode text in rich or plain-text messages, not as remote pictures.
- **Resize inline pictures.** Click a picture in the message and choose 25%, 50%, 75% or 100% of the message width; the setting stays with the formatted email.
- **Language and text direction.** More includes paragraph-level writing-language hints (English, Hindi, Bengali, Chinese and Arabic) and left-to-right/right-to-left directions. These attributes persist in the saved message; available spelling suggestions depend on the installed browser language tools.
- **Clearer screen-reader labels.** Sender, recipients, subject, both message editor modes and attachment actions expose descriptive accessibility names.
- **Discard changes explicitly.** A footer action asks for confirmation, then discards this compose window's unsaved changes while retaining the previously saved draft version, if one exists.
- **Save a message to an .eml file.** Export the current message in standard email format, including its attachments, without sending it or closing the editor.
- **Privacy warning for large recipient lists.** When more than 20 people are visible in To/Cc, review the audience or move everyone except the first recipient into Bcc. After moving, you review the updated recipients and press Send again; no message is sent automatically.
- **Save subject and text as a quick template.** Templates can capture the subject and new message text, excluding signatures, quoted mail and attachments. Inserting fills a blank subject; replacing an existing subject requires an explicit choice. Body text inserts at the cursor, old body-only templates remain compatible, and Settings can edit both fields.
- **Quick replies while composing.** Templates now also offers the saved Quick replies list. Choosing a reply inserts editable literal text at the current selection without sending, changing the subject, or expanding template variables; existing recipients and attachments stay intact.
- **Keyboard shortcut help.** Press Ctrl+/ in the editor or choose More → Keyboard shortcuts to see the available compose commands without leaving your draft.
- **Find and replace message text.** Ctrl+F finds the next match and Ctrl+H adds Replace and Replace all. Case matching is optional; both the rich editor and plain-text fallback support the search panel.
- **Fold quoted messages while replying.** Original reply/forward text starts collapsed and can be expanded with one click, including in Focus mode. Folding affects only the editor display; the full quote remains in the sent or saved HTML.
- **Table picker and editing menu.** Insert a table from a 6-by-6 grid; right-click a table cell to add or remove rows or columns, merge right/down, split a merged cell, or delete the table. Row and column edits preserve rectangular merged cells, formatting and inline content; incompatible layouts are rejected.
- **Spelling toggle.** More lets you switch spelling checks on or off while composing; the rich-text editor uses the browser's installed spelling tools, while the plain-text fallback uses Windows spelling suggestions.
- **Arrange the More menu.** Settings → Appearance includes Up/Down controls for the compose toolbar's More commands and a reset to the default order.
- **Personalise the main compose toolbar.** Settings → Appearance can show, hide and reorder individual formatting buttons, with a compose-only Reset. Narrow windows move whole right-most groups into a labelled More tools popup; expanding the window brings them back. Attach and More remain available, and the original controls keep their values and handlers when moved.
- **True plain-text messages.** More can switch the outgoing message to text/plain only after confirming removal of rich formatting. The mode survives local/server draft saves and reopening; regular file attachments remain.
- **Paragraph and code styles.** Normal, Heading 1–3 and Code block are available in the paragraph-style menu; code blocks retain monospace formatting in the outgoing message.
- **Mention a contact.** Type `@name` in either editor, choose a suggested contact, and Magpie inserts the mention and adds their address to To unless they are already in To, Cc or Bcc.
- **Send & Archive for replies.** The optional reply action archives the Inbox conversation only after successful SMTP acceptance. Undo Send and delivery failure leave it in the Inbox, and an interrupted post-send archive job resumes after restarting.
- **Remember the compose-window size and position.** Reopening New message restores your last dimensions and monitor location when still on screen, otherwise starts near Magpie's main window.
- **Complete message-size guidance.** Autosave, Review and Send count the encoded message including headers, body, inline pictures, local files and forwarded MIME attachments. Compare the result with Gmail 25 MB / Microsoft 20 MB guidance or an editable 1–100 MB value for custom IMAP/SMTP accounts. These are advisory thresholds, not a claim about the server's actual limit; edits invalidate a stale displayed size.
- **Attach recent files.** The top and bottom Recent menus show up to 12 files you previously attached from this computer. The history is local-only, excludes missing files and is never synced with account settings.
- **More editing tools within the message.** Choose a font and approximate size, text colour, highlight, strikethrough, alignment, indentation, headings, tables, inline pictures, emoji, paste without formatting, undo and redo. The existing default Arial 18 px and rich signatures remain in place.
- **Review delivery choices.** Set High, Normal or Low priority, an optional Reply-To, and request a read receipt. Sender-marked high-priority conversations show an accessible exclamation indicator in the mail list, separate from your pinned messages. Existing cached mail is checked gradually with bounded header-only requests. Receipt availability still depends on the recipient's software.
- **Recipient chips and separate Cc/Bcc controls.** Valid and invalid addresses appear as editable/removable chips; invalid input stays visible in red without being dropped. Hover or Alt+Down shows contact details and address actions. Open Cc and Bcc independently; non-empty fields cannot be hidden or cleared accidentally.
- **Move recipients between To, Cc and Bcc.** Drag a reviewed recipient chip onto another address field; Cc/Bcc appear during the drag, names are preserved, duplicates are avoided, and invalid addresses elsewhere are never silently dropped.
- **Choose from Contacts.** Use the new picker next to To, Cc or Bcc to search saved and recent contacts, select several people at once, or add an entire saved contact group; duplicates are skipped.
- **Paste files or pictures.** Ctrl+V in either message editor attaches copied Explorer files. For a clipboard picture, explicitly choose Insert inline or Save and attach PNG; plain text permits only an attachment. Picture encoding is bounded, saved copies use a user-chosen durable location, and ordinary text paste remains unchanged.
- **Rename and rearrange attachments.** Edit an outgoing filename without changing the file stored on your PC; move any file earlier or later. The chosen names and order survive saving a draft and reopening it.
- **Live writing statistics.** The footer updates word count, Unicode-aware character count and approximate reading time while you type. Rich-text counts exclude signatures and quoted mail; plain-text counts clearly state that they cover all plain text. Studio retains its optional external-address warning and pre-send review.
### Fixed
- **Keep Unicode Find and Replace on the selected text.** Case-insensitive matching uses the original text's positions, including dotted capital I and supplementary letters, so a match cannot replace neighbouring characters after case conversion.
- **Reopen plain-text drafts correctly on Mac.** The editor restores its saved mode before editing; inserting a link keeps the complete destination as literal text through saving, scheduling and Undo.
- **Preserve Send & Archive when conversations merge.** Reply provenance and queued archive actions move with the original source conversation, including replies sent from a different account. Undo retains the same verified source.
- **Keep distinct attachments on case-sensitive disks.** Files such as `Report.pdf` and `report.pdf` retain their own bytes, outgoing names and ordering when saved or sent. A stale order entry cannot substitute a different file whose path differs only by case.
- **Plain-text spacing survives saving and sending.** Code indentation, tabs, repeated spaces and intentional blank lines are preserved in outgoing plain-text MIME, reopened drafts, revision restoration and the native editor fallback. Saving or sending does not add a blank line to an empty message or a newline after its final character. Literal markup and URLs remain text.
- **Attached emails keep their renamed filenames.** Renaming a carried `.eml` now updates the message part's MIME metadata as well as its visible card, including saved/reopened drafts and draft-history restoration.
- **Find Next advances correctly.** Repeated searches move beyond the current selection, wrap after the last match and preserve message formatting.
- **Quoted mail stays visible on Mac.** Quote folding is enabled only by a compose host with an expansion control. Windows keeps its fold/expand button; the Mac editor shows the complete original quote.
- **Send & Archive remembers the original account.** Changing the From account no longer redirects archiving to that sender's Inbox. Trusted source context stays local and survives draft reopen, scheduling, restart and Undo; drafts without a known source cannot request this action. Legacy automatic-archive actions without a provable source are disabled on upgrade, while their outgoing and scheduled messages are retained.
- **File-picker pictures cannot be overtaken by Send or Close.** Loading and inserting an inline picture holds the same operation protection as clipboard pictures, rejects closed/changed editors, enforces an 8 MB read limit and releases protection after failures.
- **Attachments cannot silently disappear from outgoing mail.** Missing files are detected before queueing and again while building the message. Duplicate file paths are ignored and the composer reports estimated encoded attachment size.
- **Safer sending.** Magpie warns about duplicate recipients, absent attachments mentioned in the message, and empty messages. A duplicated email address gets only one copy of the message.
- **Delivery options persist in drafts.** Reply-To, priority and read-receipt requests are restored when reopening a locally saved or server-stored MIME draft.
- **Safer whole-draft restoration.** Replacing the editor clears stale AI rewrite, mention, image and table selections. An editor failure restores the current checkpoint; concurrent edits cancel the restore. Rich-text revisions require the rich editor so the fallback cannot silently flatten their formatting. Clearing all text in the fallback editor no longer brings back the opening message body.
- **More-menu ordering can be saved on its own.** Reordering the compose More commands now enables Apply in Appearance settings, even when no other preference changes.
- **No stale or incomplete send during background work.** An edit made during asynchronous send review cancels that attempt and asks for a fresh review. Sending or closing waits for an active clipboard-picture attachment, and stale picture selections are discarded safely.
- **Plain-text signatures stay under your control.** Converting to plain text retains the visible signature but removes its rich-text boundary. Automatic signature replacement/removal is unavailable after that conversion, even when switching back to rich text, with a clear explanation to edit the retained text manually. A fresh or restored rich document with intact boundaries supports signature selection again.

## 14.2.0 (5 Nov 2026)
### Changed
- **Permanent three-digit beta versions on Windows, Mac and Android builds.** Each pull-request head gets one immutable `vX.Y.Z-beta001` to `beta999` reservation from GitHub, shared by independent platform pipelines. A rebuild of the same commit keeps its tag; a new commit gets the next number; the pipeline fails if a version's 999 allocations are exhausted.
- **What's New in the Windows app.** The title bar now offers an information button for the currently installed version, and the version manager has a full-size implementation detail window for each selected release. The published release notes identify actual changes instead of a shifting beta rank; version selection, checksums and rollback are unchanged.
- **Release note quality check.** Publish-time validation requires detailed changes for each version; new beta tags are reserved before platform workflows publish.
<!-- closes: #140 -->

## 14.1.0 (4 Nov 2026)
### Changed
- **A confirm screen before every version change on Windows.** After you choose a version, Magpie shows where you are and where you are going (From → To), a short note when it matters, and the exact steps it will take: check that the version works with your mail, download it (or use the copy on this PC), save a recovery copy of the version you have now, install it, and pin it. **Pick a different version** takes you back to the list; nothing starts until you press **Update to v…** or **Roll back to v…**.
- **The confirm screen says when a beta skips a newer stable.** "This skips the latest stable (v14.2.0)" — and going back to an older version reminds you that newer features will be missing until you update again.
- **Updates and Version history & roll back are tabs** at the top of the version manager instead of a drop-down.
- **"On this PC".** Versions whose copy Magpie already kept in its version backups are marked, and choosing one skips the download when the copy still matches the release's checksum (otherwise it is downloaded as before).
- Windows only; the Mac app is unchanged.
<!-- closes: #130 -->

## 14.0.0 (3 Nov 2026)
### New
- **Start-up screen.** While Magpie opens you now see a card with the Magpie mark and version on the left and, on the right, the four things Magpie does before its window appears: checking the last update, opening your mail folder, loading settings, look and fonts, and starting your accounts. Each step ticks off with how long it took, your accounts show as small chips in their colours, and the card fades as soon as the window is ready (it stays at least 0.8 seconds so it never just flickers). It follows Light, Dark or Match Windows, and appears where the Magpie window will open.
- **If start-up is slow or fails, it says so.** After 10 seconds the card adds "This is taking longer than usual" with **Open log folder**. If Magpie can't start, the step that failed turns red and the message shows on the card with **Open log folder**, **Close** and, on the first start after an update, **Go back to the previous version**, instead of a plain Windows message box.
- **Settings → General → Start-up screen:** every time Magpie opens (the default), only the first time after an update, or never, plus **Show my accounts on it**. Magpie never shows it when it starts with Windows into the notification area, or when it's already running. Settings search finds it ("splash", "start-up", "loading").
- Windows only for now; the Mac app starts as before.

## 13.0.2 (3 Nov 2026)
### Fixed
- **Recognisable beta versions on Windows.** The newest compatible beta in each release series is displayed as `vX.Y.Z-beta 1` (LATEST when it is also the newest beta overall), followed by `beta 2`, `beta 3`, and so on; these are view-only ranks and can change when another build is published. The immutable full release tag is shown beneath each beta, with the release date and local time, for reliable exact-version installation or rollback. The newest compatible stable release is also marked LATEST.
<!-- closes: #128 -->

## 13.0.1 (3 Nov 2026)
### Fixed
- **Windows version chooser shows one current build per release channel.** Normal updates show the newest compatible Windows stable and/or beta release, not every earlier PR build; **Show all builds** restores exact historical choices and rollback continues to list all older versions. The update badge counts channels instead of every beta commit.
<!-- closes: #126 -->

## 13.0.0 (3 Nov 2026)
### New
- **AI note when you click Forward.** Turn on Settings → AI writing → **When I click Forward** and Magpie writes the note above the forwarded email. Pick why you're forwarding (FYI only, Please handle, Please review, Introduce, Escalate or Custom) or type it in a few words. It can add a 2-line summary and mention the attachment by name; attachment contents are never sent. The note starts "Hi," and gets the name as soon as you add a recipient, without writing it again. Forward asks for its own permission once per cloud AI connection.
- **"What the reply says".** Under the editor, Magpie shows what the email asks ("They ask: can you finish the report by Friday?") and the answer it suggests. Change it with one click (Yes, I'll do it · No, I can't · Yes, if… · Need details · Later) or type your own, for example change "Yes, I will do it" to "No, I will not do it", then **Rewrite** (Ctrl+Enter in the panel). The last 5 versions of each draft stay available with ‹ ›.
- **Tone, formality, culture and etiquette for AI drafts.** Tone is now separate from a 5-step **Formality** scale (Very casual to Very formal). New **Culture & etiquette** options: English style (Indian, British, American, global plain, or match the sender), business etiquette (Indian, Japanese, German, Middle East, US), directness, greeting, form of address (first name, title + surname, Sir / Madam, name + ji) and emoji. Change them for one draft in the panel, or use **Save these choices as my defaults**. Optional **Remember my choices per contact** (off) reuses a style you changed the next time you reply to that person.
### Changed
- **The AI draft panel moved under the editor** for Reply, Reply All and Forward, and "AI reply settings" is now **Settings → AI writing**. Changing a choice no longer throws the draft away: changes are counted on the Rewrite button and sent together in one request.
- **Formal and Informal moved from Tone to Formality.** A saved Formal tone becomes Professional + Formal, and Informal becomes Friendly + Casual. More draft languages: Marathi, Tamil, Telugu, Gujarati, Spanish, French, German and Japanese.
- With **Bring my accounts to other PCs** sharing AI settings, the new AI writing choices come along to your other PCs; the choices remembered per contact stay on the PC where you made them.
<!-- closes: #113 #114 #115 -->

## 12.0.0 (2 Nov 2026)
### New
- **Sign in once on a new PC and every account comes along.** Settings → Accounts → **Bring my accounts to other PCs** keeps a locked copy of your accounts in one mailbox you choose (the home account), in a hidden "Magpie Sync" folder. On another PC, add that account as usual: Magpie finds the copy, asks for the sync password, and adds every account you tick in one step. Their first downloads take turns, 4 at a time, Inbox first, and you can read mail from the first one that's ready.
- **What comes along:** every account with its sign-in, and (if you choose) your rules, signatures, quick replies, templates, tags, Gatekeeper lists, look, and AI connections. Emails never go into the copy. The copy is locked with a sync password of at least 10 characters that only you know; Magpie can't recover it.
- **Stays in step:** an account added, signed in again or changed, or a setting changed, reaches your other PCs at their next mail check; the newest change wins. Removing an account never removes it elsewhere by itself: the other PCs ask **Remove it here too** or **Keep it**. A new sync password is asked once on the other PCs.
- **Google and Microsoft sign-in work straight away:** Magpie now comes with its own sign-in apps, so a new PC needs nothing typed before the first sign-in. Your own sign-in apps in Settings still win.
- Windows only for now; the Mac signs in with the built-in apps too but has no accounts copy yet.
<!-- closes: #85 -->

## 11.1.0 (2 Nov 2026)
### Changed
- **Gmail labels fold in the sidebar on Windows.** A label made inside another one ("Clients/Acme") now sits under its parent with an arrow to open or close it, also when Gmail keeps the parent only as a name with no mail of its own. A closed parent shows how many unread emails its labels hold.
- **All your labels in one group.** Gmail's own folders (Inbox, Sent, Drafts, All Mail, Spam, Trash) stay on top; your labels are in one **Labels** group (**Folders** for other accounts) that opens or closes with one click and stays as you left it.
- **More labels.** Labels with no unread mail that you haven't opened for 30 days move into **More labels** at the end of the group, shown with their full name. They come back by themselves when new mail arrives; right-click a label → **Always show** keeps it out of More labels.
- **Right-click an account or its Labels group** to **Open all labels**, **Close all labels** or **Only show labels with unread mail**.
- Windows only for now; the Mac sidebar is unchanged.
<!-- closes: #110 -->

## 11.0.0 (30 Oct 2026)
### New

The version-manager changes in this release are for Windows. Krishna deferred the macOS feature on
1 Nov 2026 (IST); the existing Mac updater and build pipeline remain in place.

- **Choose exact stable or beta versions on Windows.** Browse newer or older releases, filter by channel, read the release notes and check availability before choosing a version. Nothing is preselected or installed by an automatic check.
- **Compatibility before changing versions.** Magpie checks whether the selected version supports the current mail database, settings and Windows portable or per-user installation. Missing compatibility information or unsafe downgrades are blocked before any files are replaced.
- **Protected version changes.** Keep a recovery copy of the application and local data, view the transition history and restore a checked backup. A selected version stays pinned, including when an older application would otherwise update itself automatically.
- **Earlier beta versions stay available.** New builds retain stable and beta release files on GitHub so later updates do not erase the version you may want to return to.

### Fixed

- **Windows can finish restarting after replacing its application file.** Required application components stay available while a version change or recovery starts the selected copy.
<!-- closes: #117 -->

## 10.0.0 (24 Oct 2026)
### New
- **Fonts & text on Windows.** Choose a font and size independently for 53 types of text across navigation, the inbox, reading, writing, AI, Settings and the calendar. Search the roles, preview a change, reset one role or a whole area, then Apply. New writing and signatures start in Arial 18 px (13.5 pt), normal weight. Existing formatted drafts keep their appearance. Reading-font overrides are optional and leave the original email intact.
- **AI drafts from Reply and Reply All on Windows.** Enable full reply drafts in Settings, with separate switches for each button. Choose tone, purpose, length, language, conversation context and custom instructions. A reply opens normally and generates a preview after its editor is ready. Review it, regenerate, discard or explicitly use it; existing typed text asks before appending or replacing. Recipients, signatures and quoted messages stay protected, and sending remains your action. Each cloud connection asks for this feature's consent; turning AI off cancels a pending result.
<!-- closes: #103 #104 #105 -->

## 9.0.0 (24 Oct 2026)
### New
- **Contacts.** A new **Contacts** page in the sidebar (Ctrl+3) shows the contacts saved in your Google accounts, plus the people you've written to (marked **Recent**). Search by name, email, company or phone, and switch between **All**, **Frequent** and **Groups**. Each person shows their emails, work, phone, groups, notes, and your **conversations**, **attachments** and **events** with them, with **New message** and **New event** buttons.
- **Add and edit contacts** from Magpie: **New contact**, **Edit**, **Add to contacts** for someone you wrote to, or **Add to contacts** on the card that opens when you point at an email address. Changes are saved on this PC at once and reach Google in the background.
- **Better suggestions while typing a recipient**: saved contacts and people you've mailed, the ones you write to most first. Unsaved people are marked **Recent**.
- Saved contacts count as known senders, so the Gatekeeper lets them straight in.
- **Before it works:** the Google People API must be turned on in the Google Cloud project Magpie signs in with (APIs & Services → Library → Google People API), and each Google account signs in again once so Magpie may read and save its contacts — the Contacts page says so and offers **Sign in again**. Until then the page lists only the people you wrote to (Recent). Windows only for now; the Mac keeps its suggestions from people seen in mail.
<!-- closes: #5 -->

## 8.1.1 (24 Oct 2026)
### Fixed
- **No internet no longer fills the log with errors.** An account that can't reach its server shows "Offline" and writes one line when it goes offline and one when it's back, instead of an error with a full stack trace at every retry.
- **Accounts no longer stop after the PC wakes up** because Windows couldn't yet reach the server that checks whether certificates were withdrawn (the 8.0.0 certificate rule covers this case too; it is now tested, and a certificate reported as broken with no reason given is refused).
- **Reads, pins and moves made on this PC are no longer lost** when the server renumbers a folder. Magpie now finds the email by its ID. Changes Gmail refuses with "System Error" are tried again at the next checks (up to 5 times) instead of being dropped.
- **A folder keeps updating when Gmail refuses a few of its emails** ("Some messages could not be fetched"); only those emails are left out. This happened in Gmail's Bin.
- **An email that can't be read no longer stops the other downloads.** The log now also says what was wrong with it.
<!-- closes: #1 -->

## 8.1.0 (22 Oct 2026)
### Changed
- **Pick the AI model from a list.** Settings → AI asks your provider (Anthropic, OpenAI, Ollama or any OpenAI-compatible server) which models your key can use and shows them newest first, with "New" on anything added since Magpie last looked, so a new Claude appears by itself. You can still type a model id. The API key now sits above the model; the endpoint is under "Server (advanced)". A creativity (temperature) slider per connection.
### Fixed
- **New models no longer refuse Magpie's requests.** Claude Fable / Mythos, Claude 5.x and OpenAI's o-series / GPT-5 reject the temperature or max_tokens settings; Magpie now sends only what each model accepts, retries without the refused setting, and remembers it (Test connection says "sent without temperature").
<!-- closes: #98 -->

## 8.0.0 (10 Oct 2026)
### New
- **Magpie for Mac** (Apple Silicon: M1 and newer, macOS 14 Sonoma or later). Download `Magpie_<version>.dmg` (in the release, or in `macOS/` of `Magpie_<version>.zip`), open it and drag Magpie to Applications. It reads and sends your mail with the same engine as Windows: your accounts and folders with unread counts in the sidebar, the message list with search, conversations shown by the Mac's own web view (links open in your browser; pictures from the internet follow Ask / Always, with Show pictures), Reply, Reply all, Forward, Archive, Delete (in Trash and Spam: Delete forever, after asking), Mark read / unread and Pin, and a compose window with suggestions from your contacts, links (⌘K), attachments, Send later and a few seconds to Undo. Drafts open again with Edit draft — also the ones kept on this Mac while offline, marked "On this Mac".
- **Accounts on the Mac**: email and password for any IMAP mailbox (server settings found by themselves), or Sign in with Google / Microsoft in your browser (the same client IDs as on Windows: Settings → Accounts → Sign-in apps, or Import Google client JSON…). Passwords and sign-ins are encrypted with a key kept in your Keychain; mail and settings stay in ~/Library/Application Support/Magpie.
- **The Mac app updates itself** like the Windows one: it checks GitHub at every start, shows "Update to vx.y.z" at the top right only when there is a newer version (click: it downloads, checks the SHA-256, installs and restarts), and with Auto update on it checks daily and installs a downloaded version when you quit. The disk image is checked again just before it is installed, and the previous version is kept (outside Applications) in case the new one doesn't start. The version is shown next to the name in the main window.
- **Opening it the first time**: Magpie for Mac isn't signed with an Apple Developer ID (by choice), so macOS asks the first time. Right-click Magpie in Applications → Open → Open (or System Settings → Privacy & Security → Open Anyway). After that it opens normally, and updates don't ask again.
- Still on Windows only for now: calendar, rules, auto-delete, Gatekeeper, snooze / set aside / reminders, tags, AI, hover cards, settings backup and the other Settings pages.
### Fixed
- **Gmail works on the Mac.** The Mac's own check can't find out whether Google's new certificates were revoked, so every Gmail connection failed with "Secure connection failed". Magpie now accepts a valid certificate whose revocation status can't be found out (as browsers do); a wrong, expired, untrusted or revoked certificate is still refused.
- **Mac: ⌘ shortcuts work while typing a message** (⌘W closes the window, ⌘N, ⌘, …) and in the reading pane's header — the web view used to swallow them. Not yet inside the email text itself.
- **Mac: a crash in the first minutes after an update** now also offers to go back to the previous version.
- **A refused secure connection now says why** (Windows and Mac): when a mail server's certificate isn't trusted, the account check names who issued it, and says that a company network, VPN or antivirus that inspects secure connections can cause this. The details also go to the log.

## 7.2.0 (10 Oct 2026)
### Changed
- **New download layout.** Each build is now one file, `Magpie_<version>.zip`, holding `portable/Magpie_<version>.exe` (run from anywhere; its data stays next to it), `windows-x64/Magpie_<version>.exe` (the installer), and soon `macOS/Magpie_<version>.dmg`. The old `installer/` and `portable/` folders and `portable.txt` are gone; an older portable copy with `portable.txt` keeps working.
### Fixed
- **Magpie checks for a new version every time it starts**, even with Auto update off (it then only shows the "Update to vx.y.z" button and waits for you to install).

## 7.1.0 (9 Oct 2026)
### Changed
- The top-right update button appears only after GitHub finds a newer version and reads “Update to vx.y.z”. It keeps that version while downloading and when ready; check manually in Settings → Updates.
<!-- closes: #90 -->

## 7.0.1 (9 Oct 2026)
### Fixed
- Magpie opens again after updating to 7.0.0: the Attachments, Pinned and Save view search buttons now use the right style. Windows checks also construct the full main window in light and dark themes.
<!-- closes: #88 -->

## 7.0.0 (8 Oct 2026)
### New
- **Commands (Ctrl+K)** in the title bar: find actions, folders, saved views, reminders and drafting without leaving the keyboard. Unavailable actions explain what is needed.
- **Important (pinned) and Other inbox**, plus saved inbox views and searches by sender, exact domain, recipient, subject or local label, with an account scope. Views stay on this PC.
- **Local search filters** for attachments and pinned mail, a Save view button and a reminder that body search covers downloaded email.
- **Reminders due** shows reminders ready to act on; Draft with AI opens the existing preview-and-insert assistant without generating or sending automatically.
### Changed
- Ctrl+Shift+A in Compose opens or closes the enabled AI panel.
### Fixed
- No-reply reminders clear when a synced reply is detected, even before their deadline; replies in another account and your own messages leave them active.
- Mail letter shortcuts leave text/password fields, editable controls and the reader alone while typing. Obsolete neighbour preparation stops before expensive rendering.
<!-- closes: #83 -->

## 6.0.0 (8 Oct 2026)
### New
- **Update button in the title bar**, always there (Calendar · Sync · Settings · **Update**): grey when Magpie is up to date (hover shows when it last checked), green "Update 4.1.0" with a dot when a newer version is out, a filling bar while it downloads, "Restart to update" when it is ready, amber when the last check didn't work. Click to check now (even with Auto update off) and see what's new, with Download and install · Later · Skip this version, Cancel while downloading, the Auto update and test-versions switches and the release notes; right-click opens that at any time. It replaces the green pill.
- **Shift+Delete deletes for good**, from any folder: it asks once ("Delete 3 emails for good?"), skips Trash and removes every copy from this PC and the server — no undo (on Gmail, All Mail may still keep a copy). Also "Delete forever…" in Delete ▾ and the right-click menu; the Trash and Spam "Delete forever" asks the same way.
- **One dialog, "Delete emails from…"**, for the emails already here and those arriving later: type an address or *@domain (it says how many emails are here from them), pick what to keep (the last day … year, nothing, or OTP · 24 h), tick **Emails already here** (delete the ones older than that now — it says how many, the oldest, and how many are kept) and/or **Emails that arrive later** (an auto-delete rule), choose the accounts, and the red button says what will happen ("Delete 212 now and keep deleting"). A few seconds to undo, which also removes a rule made by that run. Opens pre-filled from Delete ▾, the right-click menu ("Delete all N already here…", "Keep only the last week, now and later…", "Delete after a set time…"), the address hover card and Settings → Rules → Auto-delete.
### Changed
- **A new auto-delete rule times the emails already here in every folder** (not only the Inbox) — those not yet past the time; the ones already past it are deleted only when "Emails already here" is ticked. The "older than ▸" submenu and the "Include the N emails already in the Inbox" toggle are gone: the dialog's tick replaces them.
- **A rule's Delete action moves every copy** of the email to Trash (Gmail: the All Mail copy too), not only the copy that matched.
<!-- closes: #59 #60 -->

## 5.0.0 (7 Oct 2026)
### New
- **See when an email will be deleted.** An email with an auto-delete timer says when it goes, in three places, and you choose how in **Settings → Appearance → Deletion dates**:
  - **Pointing at an email in the list**: a line in the date's tooltip, a small card with **Keep this one**, **Change rule** and **Delete now**, or the row's tag growing to the full date and rule.
  - **On the row**: a countdown pill coloured by how soon (red under 48 hours, amber under 30 days, grey later), the deletion date in place of the arrival date, a red edge with a clock and the days left, or a small ring that empties.
  - **In the reading pane**: a banner with the full date and time, the rule, **Keep this one** / **Change rule** and a bar of the time left; a chip next to the subject; or the time on each email of the conversation.

## 4.0.2 (7 Oct 2026)
### Fixed
- Opening saved emails uses this PC first. Switching between recently opened conversations reuses their text, prepared pages and rendered views without showing a loading page again.
- The 90-day download window keeps filling in the background. Switching selection keeps shared downloads saving to disk, and changing the download window starts the new work at once.
- Reader memory stays bounded, changed messages and image permissions refresh correctly, and modern calendar attachments no longer trigger unnecessary full-message reads.

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
