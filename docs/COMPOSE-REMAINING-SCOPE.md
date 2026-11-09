# Remaining approved compose features

This continuation follows Krishna's original approval of all 60 compose features and the Balanced + Focus + Studio hybrid design. PR #136 covers the core editor work. Issues #133, #134, #135 and #144 track the remaining integrations and the original checklist items recovered during the continuation audit. Their separation into issues does not require another design approval.

## Original checklist mapping

| Original item | Implementation | Boundaries |
|---|---|---|
| 12 — Drive/OneDrive upload and share links | Account-bound cloud consent, explicit file/audience/access review, named-recipient sharing and editor insertion | Requires a configured provider application and cloud consent. No anonymous links; hidden-recipient sharing is blocked to avoid exposing identities through a provider's access list. See [cloud sharing](CLOUD-COMPOSE.md). |
| 22 — Sender identities and aliases | Settings → Accounts → Sender aliases; the compose **Send as** picker | Add addresses already authorised by the mail provider. Magpie does not provision or verify server-side send-as rights; the server can reject an unauthorised sender. |
| 41 — Read and delivery receipts | Existing read-receipt control plus independent SMTP delivery-notification request | SMTP capability and recipient systems decide availability. Server acceptance is never labelled delivery or reading. See [delivery notifications](COMPOSE-DELIVERY-NOTIFICATIONS.md). |
| 42 — Sending progress and retry | Existing outbox plus accurate retry-exhaustion status | A message at the automatic retry limit requires review and manual retry. |
| 44 — Timezone and offline scheduling | Resolved local-zone/UTC-offset labels in Send later, custom time selection and scheduled items | Magpie must be running and online. Overdue messages wait until sending is possible; unavailable or ambiguous wall times are handled explicitly. |
| 45 — Encryption and signing | Per-account key/certificate management, final MIME protection and explicit secure-message reader | Requires imported compatible key material and explicit trust. See [secure mail](SECURE-MAIL.md) for supported formats and limits. |
| 46 — Provider-dependent confidential sending | Explicit provider capability explanation and web/help handoff | Gmail confidential mode and Microsoft rights management are **not applied natively by Magpie**. The handoff does not copy or send message content. It does not simulate expiry or revocation. |
| 50 — Template variables and categories | Existing variables, sender-account `company`, single/double brace support, review of expanded subject/body before insertion, plus optional named categories in Settings and the compose Templates menu | Missing placeholder values remain visible; the review is explicit and its text is invalidated by changes in compose context. Uncategorized legacy templates retain their top-level menu placement. Categories are limited to 48 characters. |
| 51 — AI grammar and translation | Existing grammar action plus target-language picker and explicit Translate action | Uses the configured AI provider and existing consent rules. Only selected text is rewritten; the output remains a preview until the user applies it. |
| 53 — Review AI output and Undo | Explicit previews plus native Undo/Redo transactions for body insert/replace and selection rewrite | Whole-body edits require known signature/quote boundaries; converted or unmarked imported drafts use selected-text rewriting. The restriction survives saving and recovery. |

## Sender identity behaviour

Sender aliases are scoped to an account. Each entry accepts an address or `Display Name <address@example.com>`, one per line. Validation rejects invalid addresses, duplicate addresses, the primary address, control characters, excessive field lengths and more than 20 aliases. Legacy account settings load with an empty alias list; malformed externally edited aliases are removed during normalization. Account cloning does not share mutable alias entries.

The selected alias changes the MIME `From` address and the read-receipt destination. The authenticated account ID, server credentials and original reply/archive provenance remain unchanged. Switching the account chooses that account's primary identity. Reopened drafts, local revisions, queued messages and Undo retain the chosen address. A removed or unconfigured sender stays visibly unavailable and cannot silently become the primary sender when queueing. Removing an alias after scheduling also blocks transport until the message is reviewed.

Template sender variables, AI author names, compose review and external-domain warnings use the selected identity. Reply All treats configured aliases as the user's own addresses. Account-copy serialization includes the alias configuration through the account model; it does not transfer cloud permissions or server-side send-as authorisation.

Unrelated Settings saves do not rebuild an unchanged sender list or invalidate an otherwise current AI result. Updating a sender's name or availability is an explicit context change. An incomplete alias being typed in Settings does not crash Apply change tracking or an unrelated signature-editing command; Apply validates the complete alias list before changing settings.

Scheduling and native AI Undo are documented in [their implementation record](COMPOSE-SCHEDULING-AND-AI-UNDO.md).

## Categories and translation

Categories are optional metadata on existing templates. Settings load, change tracking and save include the category, so a category-only edit enables Apply. The Templates menu groups category names case-insensitively, preserves template content, and leaves old uncategorized entries accessible. Saving the current message as a template asks for an optional category; an invalid or overlong value is rejected rather than silently changing the entered name.

Template insertion now opens a read-only preview of the expanded message text and resulting subject, with an explicit choice before replacing an existing subject. `{{company}}` and `{company}` use the selected sender account's Company setting. Previously supported variables accept either brace form; unresolved values stay visible for manual editing. If recipients, sender details or the draft change while the preview is open, insertion is rejected and a fresh preview is required.

Translation accepts a single-line target language of 1–60 characters, including Hindi, Marathi and other user-entered languages. Selecting a language makes no request. Translate captures the selected editor range and requests a translation preserving meaning, names, numbers, paragraphs and tone. The existing AI preview, stale-context checks, explicit Replace selection and editor Undo remain in control of application. Translation never queues or sends mail.

## Verification

| Risk | Coverage |
|---|---|
| Wrong sender or changed authentication account | `SenderIdentityTests`: MIME sender, read-receipt destination, immutable account identity, invalid/removed aliases |
| Identity lost after persistence | `SenderIdentityTests`: local MIME, history, queue, scheduling and Undo |
| Combined alias, signing/encryption and delivery request | `Release1600Tests`: both S/MIME and OpenPGP through local drafts, revisions, Undo and the actual outbox transport selection |
| Encryption hides a cloud link from permission checks | `Release1600Tests`: authenticated in-memory recovery content is checked before SMTP; a removed cloud account leaves the protected message queued and recoverable |
| Mac editing drops an unexposed sending option | Avalonia headless fixtures: actual sender picker, account switch, save/schedule/Undo and retained security, DSN, read-receipt, priority and Reply-To choices |
| Duplicate self recipients | `SenderIdentityTests`: Reply All excludes primary and alias identities |
| Legacy or malformed settings | `SenderIdentityTests` and `ComposeRemainingWritingTests`: normalization, cloning, limits and disk reload |
| Category edits not saved | `ComposeRemainingFeaturesSmoke`: category-only Apply and actual grouped native menu |
| Alias edits break Settings or disturb compose | Native fixture: incomplete-input tracking, default signature, unchanged composer after unrelated Apply |
| Translation applied or sent automatically | Native fixture with a local fake provider: request contents, preview, no editor replacement and no SMTP call |
| Real Windows bindings | `ComposeRemainingFeaturesSmoke` runs the sender and translation controls in both light and dark theme passes |

The core tests use temporary stores and fake providers. Cloud and secure-mail tests use isolated provider responses and generated key material. A successful automated run does not constitute testing against Krishna's live mailboxes, cloud storage or production certificates. Native Windows CI executes the WPF/WebView2 fixtures; cross-compilation alone does not execute them.

The PR remains subject to Krishna's review and explicit merge approval. No direct changes to `main`, manual release or tag are part of this continuation.
