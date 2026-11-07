# PR #136 — Compose implementation and acceptance record

Repository: `krishnabhunia/Magpie-email-client`

Pull request: [#136 — ChatGPT-Magpie-Compose-Enhancements](https://github.com/krishnabhunia/Magpie-email-client/pull/136)

Branch: `chatgpt-magpie-compose-enhancements`

Design: [CW2 hybrid composer](designs/CW2-compose-hybrid.md)

Core acceptance: [Issue #132](https://github.com/krishnabhunia/Magpie-email-client/issues/132)

Record updated: 7 November 2026. No merge to `main` is authorized.

This record distinguishes implementation and automated coverage from completed human acceptance. The PR contains the latest exact-head build results; an earlier green build does not validate a later commit.

## Current continuation

| Approved area | Implementation now present | Automated coverage |
|---|---|---|
| A1–A2 recipients | Invalid red chips, raw-input preservation, contact hover, keyboard actions, independent Cc/Bcc rows | `RecipientsSmoke.cs`, in both themes |
| B2 colours | Text/highlight palettes, custom hex, clear highlight, selection snapshots | `FormattingControlsSmoke.cs` |
| B10 emoji | Offline search/category/Recent picker, bounded local history, rich/plain insertion | `ComposeEmojiCatalogTests.cs`, `EmojiPickerSmoke.cs` |
| B14 contextual links | Edit/remove/close bubble, safe URLs, rich-format preservation, no controls serialized into mail | `FormattingControlsSmoke.cs` |
| B15 toolbar and More ordering | Individual main-toolbar show/hide/reorder and isolated reset; grouped native overflow in narrow windows; required Attach/More access; separate advanced More order/reset | `ComposeToolbarSettingsTests.cs`, `ComposeToolbarSmoke.cs`, Settings smoke in `Features.cs` |
| C2 complete size | Actual encoded MIME counting includes body, inline, local and forwarded parts; stale display invalidation | `ComposeMessageSizeTests.cs`, `ComposePayloadSmoke.cs` |
| C4 clipboard | Ctrl+V file routing, explicit inline-versus-attachment picture choice, bounded PNG encoding and durable atomic file save | `ComposePayloadSmoke.cs`; no real clipboard access in tests |
| D1 priority | Priority headers, independent mail-list badge, bounded cached-header backfill, preservation of read/pin state | `MailPriorityTests.cs`, `PriorityIndicatorSmoke.cs` |
| D4 templates | Subject plus body, legacy compatibility, explicit replacement, stale-subject protection, Settings persistence | `ComposeWritingToolsTests.cs`, `WritingToolsSmoke.cs` |
| D5 quick replies | Configured quick replies in Templates; literal editable insertion, no automatic send or template-variable expansion, existing fields preserved | `WritingToolsSmoke.cs` |
| D8 live statistics | Debounced words/characters/reading estimate, latest-result-wins, explicit rich/plain count scope | `ComposeWritingToolsTests.cs`, `WritingToolsSmoke.cs` |
| Draft revisions | Scoped MIME snapshots, preview/confirmed restore, backup and rollback, stale-selection clearing | `DraftRevisionTests.cs`, `DraftHistorySmoke.cs` |
| Modern Office preview | Bounded structured Word/Excel/PowerPoint preview and plain-text tab; local-only rendering | `OfficeDocumentPreviewTests.cs`, `OfficePreviewSmoke.cs` |

The core acceptance list in Issue #132 contains 42 items (A1–A6, B1–B16, C1–C5, D1–D9 and E1–E6). The continuation audit found and connected the remaining D5 quick-reply entry point; the other listed core items have implemented code paths. This is an implementation audit, not a claim that every platform/provider or human interaction has passed acceptance.

The continuation also prevents queueing a stale message when fields change during asynchronous send preparation. Review rejects edits made while reading or measuring the message instead of mixing old and new values. Sending, closing or discarding cannot race an active clipboard picture save. MIME text-attachment smoke expectations use decoded snapshot bytes because MIME serialization may normalize line endings; separate binary fixtures verify exact preservation.

## Six review corrections

The six P2 findings on `d8e59b2` are addressed by the following code paths and regression fixtures. A test's presence is not a passed execution; the final-head results are recorded on the PR after Continuous Integration completes.

| Review finding | Correction | Regression coverage |
|---|---|---|
| Plain-text MIME collapses whitespace | A lossless, inert editor-text conversion preserves spaces, tabs and blank lines without changing rich-mode extraction. Draft reopen, history restore and native fallback use the same representation. | `ComposePlainTextRegressionTests.cs`; `EditorFindQuoteSmoke.cs` tests real WebView2 and native fallback round-trips |
| Renaming an attached email changes only its card | Generic MIME-entity filename updates cover carried message parts and binary parts; an explicit `.eml` filename is not given another extension. | `MimeAttachmentNamesTests.cs`, `MimeAttachmentNameDisplayTests.cs`; native compose review smoke |
| Find Next selects the same match | Compare each match start with the previous selection's collapsed end and wrap only after the final match. | Real WebView2 progression, wrapping, adjacent matches, stale selection and formatting tests in `EditorFindQuoteSmoke.cs` |
| Mac quotes are folded without an expansion control | Folding is an opt-in host capability: Windows enables it; the Mac page keeps the complete quote visible. | Actual Windows and `ForMac` editor page smoke, both themes, unchanged outgoing quote HTML |
| Send & Archive follows a changed From account | Capture the original account/thread separately from the sender, validate that identity and persist it locally for draft reopen and durable queued actions. Unknown or conflicting sources fail closed for this optional action. | `ReplyArchiveSourceTests.cs`; native compose review smoke for From switching, availability and renamed attachments |
| File-picker image reading races Send or Close | Claim the attachment operation before reading; retain it through the editor acknowledgement; bound reads; reject stale/closed editors and release the claim after failure or cancellation. | `InlineImageInsertionSmoke.cs` delayed-read/acknowledgement, send/close, failure/cancellation, size-limit and real WebView2 insertion tests |

Send & Archive's reply-source cache is local to this PC, bounded to 5,000 entries and 180 days, and never placed in delivered MIME. A draft opened on another PC, or after its source context expires, can still be sent normally but cannot safely request automatic archiving. Already queued actions retain a separate durable source. Upgrading disables only legacy automatic-archive actions whose original source cannot be proven; their outgoing or scheduled messages remain intact. Removing a source account never redirects its action to the sending account.

The follow-up to the first correction build preserves the authored plain-text body's exact ending through MIME serialization, including empty text and text without a final newline. Only that authored body uses lossless transfer encoding; carried attachments and nested emails retain their existing content and encoding. The strict round-trip assertions remain in place. Attached-email regressions compare serialized nested-message snapshots, including after draft-history restoration, because the original email's own transport can legitimately normalize line endings. Rich-mode comparison normalizes only the platform's line-ending convention.

## Known limits and remaining acceptance

- Run the exact-head Windows unit tests, application build, native WPF/WebView2 smoke and packaging checks; shared-core changes also require the relevant macOS/Android regressions. Results are recorded on the PR, not inferred from local syntax checks.
- Human Windows acceptance remains: keyboard navigation through open native menus, screen-reader announcements, narrow-window layout, real clipboard file/screenshot behavior, draft restore, and actual mail-server compatibility. Automated fixtures never access the user's mailbox, clipboard or live provider accounts.
- The emoji catalog is a curated set of common entries, not every Unicode emoji.
- Office preview is structured, not a pixel-perfect Office renderer. Images/charts, exact layout, legacy binary Office files and macro-enabled documents are not rendered. Macros/formulas and external resources are never executed.
- Automatic signature replacement/removal is restricted to the rich editor with reliable signature boundaries. Plain-text conversion removes those boundaries, and switching back does not recreate them; retained signature text must be edited manually, rather than guessing which text to remove or appending a duplicate signature. A fresh or restored rich document supports automatic signature selection again. `SignatureModeSmoke.cs` covers this protection and preservation of rich-mode behavior.
- B15's main-toolbar settings and native grouped overflow are implemented. Existing controls are reparented rather than recreated, preserving handlers and selected values. Attach and advanced More cannot be hidden; overflow opens through More tools. Run the exact-head native smoke in both themes, then verify real keyboard/screen-reader use on Windows.
- Native spelling on/off and right-click suggestions already satisfy E3; availability follows installed Windows/WebView2 language tools. A separate grammar service is not required by Issue #132.
- Provider integrations are tracked separately from Issue #132's core acceptance but were included in the original approved 60-feature scope: [#133 cloud links and recipient access](https://github.com/krishnabhunia/Magpie-email-client/issues/133), [#134 S/MIME/PGP keys, signing and encryption](https://github.com/krishnabhunia/Magpie-email-client/issues/134), and [#135 delivery-status support/fallback](https://github.com/krishnabhunia/Magpie-email-client/issues/135). The dependent continuation implements these and the recovered sender-alias, template-category and translation items in #144; see [the remaining-scope record](COMPOSE-REMAINING-SCOPE.md). No additional design approval is required for the already approved scope.

Do not claim the broader 60-feature scope is finished or merge because the implemented core passes its tests. Readiness for review of this core PR requires the exact final-head automated checks and an implementation review. Human acceptance and provider capability limits remain explicit; merging requires the user's approval.

## Case-sensitive attachment follow-up

The fresh review of `a48dfb4` identified a shared MIME deduplication bug on case-sensitive volumes. Shared attachment paths and their default outgoing-name map now compare exactly, preserving distinct `Report.pdf` / `report.pdf` files. Exact repeated paths still produce one MIME part, and stale order entries cannot add an unselected case variant. `AttachmentPathIdentityTests` covers actual distinct binary payloads, renamed ordering, local draft/history/queue persistence and exact-path deduplication. Case-variant file fixtures run where the filesystem can create two such files; exact-path coverage runs everywhere. The Windows picker retains its existing Windows selection behaviour.
