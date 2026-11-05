# CW2 — Complete compose window, approved 5 November 2026

## Design decision

Krishna approved all 60 features in the 5 November compose audit and then selected **a combination of Designs A, B and C**.

- **Balanced (default):** From/To/Cc/Bcc/Subject, a rich-text toolbar, editor, attachment strip and persistent Send / Send later / Attach / Templates / Save draft.
- **Focus:** one-click writing mode, temporarily hiding the extra toolbar and panels; switching back must preserve the current message, selection where possible, signature, attached files and pending AI draft.
- **Studio:** optional side panel with Files, Review, Options and AI controls. It must not displace the existing AI Reply/Reply All/Forward panel or silently generate/send content.

## Safety and preservation

1. Never drop a missing attachment from a sent MIME message without informing the sender. Count encoded bytes, not just raw file bytes. Distinguish a provider's published cap from an estimate.
2. Recipients must be validated and deduplicated across To/Cc/Bcc. Bcc-only contacts stay hidden. Do not let AI select recipients.
3. Review-before-send must cover missing files, missing-attachment language, empty message, invalid or duplicate recipients and optional delivery settings. Undo send and Send later continue through the existing outbox.
4. Preserve Arial 18 px normal defaults for new writing, rich signatures, saved drafts and quoted message boundaries. AI results are editable, reviewable and never automatically sent.
5. Keep local draft autosave and server-draft fallback; adding fields must not erase data from a previously saved draft.

## Phases and completion rules

| Phase | Scope | Completion requirement |
|---|---|---|
| 1 · Essential | Attachments, recipients, core formatting, safety checks, combined layouts | Core tests, XAML validation, Windows build and user-visible smoke test |
| 2 · Productivity | Full editor controls, contacts picker, advanced signatures/templates, accessibility and draft versions | Screens and keyboard test coverage, persistent-draft regression checks |
| 3 · Integrations | Drive/OneDrive links, security signing/encryption, delivery receipts, provider-specific confidential delivery | Permission and threat reviews, provider integration tests, explicit graceful fallback |

**Tracking:** [Q90 / Issue #132](https://github.com/krishnabhunia/Magpie-email-client/issues/132) covers the central Windows composer. Cloud links, signing/encryption and delivery receipts remain independently tracked in [#133](https://github.com/krishnabhunia/Magpie-email-client/issues/133), [#134](https://github.com/krishnabhunia/Magpie-email-client/issues/134) and [#135](https://github.com/krishnabhunia/Magpie-email-client/issues/135). A feature is not complete because its button appears; it must actually work and be tested.

Do not merge or release the compose branch until the human reviewer approves it.
