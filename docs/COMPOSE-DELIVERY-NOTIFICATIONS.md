# Compose delivery notifications and provider protection

Implementation for [Issue #135](https://github.com/krishnabhunia/Magpie-email-client/issues/135), within the approved hybrid compose design. Provider-managed confidential sending is recorded separately below because it needs the provider's own protection service.

## Delivery notifications

Studio → Options has an independent **Request delivery notification** checkbox. The existing read-receipt checkbox continues to request a recipient application's disposition notification; the new option requests SMTP delivery-status notifications.

| Situation | Behaviour and displayed result |
|---|---|
| SMTP advertises the DSN extension | Request `NOTIFY=SUCCESS,FAILURE` for every To/Cc/Bcc envelope recipient, with a bounded stable `ENVID` and `RET=HDRS`. After the final acceptance response, show **Delivery notification requested — delivery is not confirmed**. |
| SMTP does not advertise DSN | Send ordinary mail without DSN parameters. The compose explanation discloses this fallback in advance. After acceptance, show that the server accepted the message without a notification request. |
| The server advertises DSN but rejects the transaction | Retain the ordinary failed-send state and the server's error. Do not silently retry without DSN or claim acceptance. A later retry makes the same requested transaction; Undo allows the user to change the option. |
| Sending is delayed, retried, cancelled or undone | Preserve the request separately from the queued MIME. Cancelled mail does not contact SMTP. Undo restores the request to the composer. |
| A notification eventually arrives | It is an ordinary incoming email from the mail infrastructure. The local request status is not promoted to “delivered” or “read”. |

The activity panel shows the latest request result and the server's acceptance response. Opening a corresponding sent message shows the durable local result and response, including after restarting Magpie. These are records of a transaction sent on this PC; they are never inferred from an incoming message's headers. Server text is bounded, control characters are removed, and the reader escapes it as text.

### Persistence and message boundaries

- Local and server drafts store `X-Magpie-Delivery-Receipt: success,failure` as draft-only metadata. The flag also participates in local revision snapshots.
- `Composer.BuildForDraft` is used only for draft saves. Ordinary `Composer.Build` produces outgoing MIME without this internal header.
- The outbox stores request/outcome metadata in its own `outbox_delivery_receipts` table, keyed by outbox ID. The original sending account and Message-ID remain the lookup scope, including when Gmail assigns the sent message a different server conversation key.
- SMTP transmission strips this draft-only header defensively. The transmitted MIME has no `Return-Receipt-To` workaround or invented delivery header.
- The successful outbox transition and DSN outcome are written in one database transaction. A failed QUIT after SMTP acceptance is not retried as another send.
- Reopening a draft on a platform that preserves its fields retains the flag. The new control and result presentation are Windows features.

No live mailbox is used by regression tests. A loopback SMTP peer checks the actual commands emitted by MailKit, including unsupported capability, rejected DSN request, retry, hidden-recipient envelope use and absence of internal/Bcc headers in the DATA payload.

## Provider-managed confidential sending

Studio → Options → **Provider-managed protection…** explains the current account's supported route and offers explicit links to the provider and its official help. Opening either link does not copy, upload or send any message content, recipients, account identity, attachments or credentials. The draft stays in Magpie.

| Provider | Available route and limit |
|---|---|
| Gmail | Open Gmail and select its confidential mode there, including any available expiry, access-removal or passcode settings. The Gmail message/send API documents MIME submission, with no documented per-message confidential-mode setting. Magpie's SMTP sender cannot apply it. |
| Microsoft | Open the appropriate Outlook website and use the protection controls available to that account/subscription/organisation. A normal **Confidential sensitivity level** is an informational label and does not block forwarding. It is not substituted for Encrypt, Do Not Forward or an organisation's encryption-bearing sensitivity labels. |
| Other SMTP providers | Explain that provider-managed expiry/revocation is not supported by the SMTP connection. Do not guess a webmail URL, add fake expiry headers or imply revocation of delivered copies. |

This is an explicit provider handoff, **not an implementation of native Gmail confidential mode or Microsoft rights management in Magpie**. S/MIME/OpenPGP signing and encryption are tracked separately in #134. No hosted message portal or invented revocation service is introduced.

## Verification

| Coverage | Fixture |
|---|---|
| Real SMTP DSN negotiation, unsupported fallback, parameter rejection/retry | `DeliveryReceiptTests.cs` loopback SMTP tests |
| Draft MIME, local saves, revisions, queue, scheduling, restart, Undo, response persistence | `DeliveryReceiptTests.cs` lifecycle tests |
| Provider capability text, safe browser destinations, no content handoff | `ProviderProtectionTests.cs` |
| Result escaping and reader cache invalidation | `ProviderProtectionTests.cs` |
| Real Windows checkbox binding, accessible explanation, revision restoration, scheduling and Undo | `DeliveryReceiptSmoke.cs`, light and dark themes |

Linux core tests and Windows cross-compilation can validate code and fixtures; native Windows Continuous Integration must execute the WPF smoke. Provider-specific delivery and real mailbox acceptance remain subject to the chosen servers. No code claims that requesting a receipt guarantees one will arrive.

## Primary references checked on 7 November 2026

- [RFC 3461 — SMTP delivery-status notification extension](https://www.rfc-editor.org/rfc/rfc3461.html), including capability negotiation, `NOTIFY`, `RET` and `ENVID`.
- [MailKit — GetDeliveryStatusNotifications](https://mimekit.net/docs/html/M_MailKit_Net_Smtp_SmtpClient_GetDeliveryStatusNotifications.htm) and [GetEnvelopeId](https://mimekit.net/docs/html/M_MailKit_Net_Smtp_SmtpClient_GetEnvelopeId.htm).
- [Gmail API — Message resource](https://developers.google.com/workspace/gmail/api/reference/rest/v1/users.messages) and [messages.send](https://developers.google.com/workspace/gmail/api/reference/rest/v1/users.messages/send). The absence of a confidential-mode field is a documentation-based capability assessment, not a claim that Google could never add such an API.
- [Gmail — Send and open confidential emails](https://support.google.com/mail/answer/7674059).
- [Microsoft Graph — Message resource](https://learn.microsoft.com/en-us/graph/api/resources/message?view=graph-rest-1.0).
- [Microsoft — Normal, Personal, Private or Confidential sensitivity levels](https://support.microsoft.com/en-gb/outlook/mark-your-email-as-normal-personal-private-or-confidential-in-outlook).
- [Microsoft — Encrypted messages with Personal or Family subscriptions](https://support.microsoft.com/en-us/outlook/send-encrypted-messages-with-a-microsoft-365-personal-or-family-subscription).
