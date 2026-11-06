# PR #136 — Compose implementation and acceptance record

Repository: `krishnabhunia/Magpie-email-client`

Pull request: [#136 — ChatGPT-Magpie-Compose-Enhancements](https://github.com/krishnabhunia/Magpie-email-client/pull/136)

Branch: `chatgpt-magpie-compose-enhancements`

Design: [CW2 hybrid composer](designs/CW2-compose-hybrid.md)

Core acceptance: [Issue #132](https://github.com/krishnabhunia/Magpie-email-client/issues/132)

Record updated: 6 November 2026. No merge to `main` is authorized.

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
| D8 live statistics | Debounced words/characters/reading estimate, latest-result-wins, explicit rich/plain count scope | `ComposeWritingToolsTests.cs`, `WritingToolsSmoke.cs` |
| Draft revisions | Scoped MIME snapshots, preview/confirmed restore, backup and rollback, stale-selection clearing | `DraftRevisionTests.cs`, `DraftHistorySmoke.cs` |
| Modern Office preview | Bounded structured Word/Excel/PowerPoint preview and plain-text tab; local-only rendering | `OfficeDocumentPreviewTests.cs`, `OfficePreviewSmoke.cs` |

The continuation also prevents queueing a stale message when fields change during asynchronous send preparation. Review rejects edits made while reading or measuring the message instead of mixing old and new values. Sending, closing or discarding cannot race an active clipboard picture save. MIME text-attachment smoke expectations use decoded snapshot bytes because MIME serialization may normalize line endings; separate binary fixtures verify exact preservation.

## Known limits and remaining acceptance

- Run the exact-head Windows unit tests, application build, native WPF/WebView2 smoke and packaging checks; shared-core changes also require the relevant macOS/Android regressions. Results are recorded on the PR, not inferred from local syntax checks.
- Human Windows acceptance remains: keyboard navigation through open native menus, screen-reader announcements, narrow-window layout, real clipboard file/screenshot behavior, draft restore, and actual mail-server compatibility. Automated fixtures never access the user's mailbox, clipboard or live provider accounts.
- The emoji catalog is a curated set of common entries, not every Unicode emoji.
- Office preview is structured, not a pixel-perfect Office renderer. Images/charts, exact layout, legacy binary Office files and macro-enabled documents are not rendered. Macros/formulas and external resources are never executed.
- B15's main-toolbar settings and native grouped overflow are implemented. Existing controls are reparented rather than recreated, preserving handlers and selected values. Attach and advanced More cannot be hidden; overflow opens through More tools. Run the exact-head native smoke in both themes, then verify real keyboard/screen-reader use on Windows.
- Native spelling on/off and right-click suggestions already satisfy E3; availability follows installed Windows/WebView2 language tools. A separate grammar service is not required by Issue #132.
- Provider integrations remain separate, design/security-gated work: [#133 cloud links and recipient access](https://github.com/krishnabhunia/Magpie-email-client/issues/133), [#134 S/MIME/PGP keys, signing and encryption](https://github.com/krishnabhunia/Magpie-email-client/issues/134), and [#135 delivery-status support/fallback](https://github.com/krishnabhunia/Magpie-email-client/issues/135).

Do not claim the complete 60-feature scope is finished, mark it Ready, or merge solely because the implemented core passes its tests. Verify the remaining approved design and integration acceptance explicitly.
