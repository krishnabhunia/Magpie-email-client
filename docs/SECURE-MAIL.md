# Message signing and encryption

Issue #134 adds Windows compose and reading support for S/MIME and OpenPGP. The crypto implementation is in the shared core; Windows supplies key management, compose controls, and the isolated reader.

## Using it

1. Open **Settings → Accounts → Message security keys…**, or **Compose → Studio → Options → Manage security keys…**.
2. Select the account. Import a private identity for the exact address used in **From**, including a configured sender alias when applicable. Enter the private-key file password in the password field.
3. To encrypt to somebody else, import their public certificate or key under that same sending account. Compare its entire displayed fingerprint with the person through a separate trusted channel, then choose **Confirm fingerprint and select…**. Importing a recipient's public key does not automatically trust or select it.
4. In compose Options, choose **S/MIME** or **OpenPGP**, then **Sign message**, **Encrypt message**, or both. The app checks all required keys before queueing and again before SMTP delivery.
5. In a received message, choose **Security…** to verify signatures or decrypt with the private keys imported for that message's account. Decryption is an explicit action.

Private identities are selected automatically only when there is no existing selected private identity for that address and protocol. A subsequent import requires explicit selection, so rotation cannot silently replace the sending identity. Old private identities may remain imported for reading older encrypted messages. **Remove…** removes a selected entry from local storage; retaining the original private-key backup is essential for future decryption. **Export public key…** never exports private material or passwords.

Removing an account also removes its local security keys. Other accounts' keys remain intact.

## Supported profiles and limits

| Area | Implemented behavior |
| --- | --- |
| S/MIME identities | RSA keys of at least 2048 bits. One private identity per PKCS#12 `.p12` / `.pfx` import; `.cer`, `.crt` or PEM public certificate imports. The certificate must bind the entered email address and permit the requested email usage. |
| OpenPGP identities | Version 4 RSA keys of at least 2048 bits, binary or armored. The entered address must have a cryptographically valid primary-key self-certification. Primary signing keys and verified encryption subkeys are supported. |
| Unsupported private identities | Hardware-token stubs, non-RSA outgoing identities, and OpenPGP signing-subkey-only identities are rejected with a readable error. No private key generation, keyservers, GnuPG files, operating-system certificate-store scanning, or automatic remote key retrieval. |
| Outgoing algorithms | SHA-256 signatures and AES-256 encryption through MimeKit 4.18 and its pinned BouncyCastle dependency. Sign-and-encrypt signs the MIME body first, then encrypts the signed entity. |
| Sender identity | The actual MIME `From` address selects the identity within the chosen sending account. The authentication account ID is unchanged. |
| Encrypted recipients | Every To/Cc recipient needs an explicitly selected, confirmed public key. The sender is also an encryption recipient and must have the matching private identity, so their Sent copy remains readable. |
| Bcc | Signing-only messages may use Bcc. Encryption with Bcc is rejected: a shared encrypted recipient envelope can expose hidden recipients' key identifiers. Send each Bcc recipient a separate encrypted message. |
| Size | At most 4 MB per imported key file, 64 key entries and a 20 MB protected key store per profile. Secure messages and decrypted output are bounded to 32 MB. At most six nested security layers are opened. |

Unsupported requests fail closed. There is no automatic unencrypted fallback.

## What signatures and encryption establish

Signatures protect the MIME body and attachments. Outer Subject, From, To, Cc, dates, and routing headers are not covered by this implementation's body signature. Encryption likewise leaves the Subject and routing addresses visible.

The reader reports these independently:

- Whether the signature mathematics verified, failed, or could not be checked.
- Whether the signing identity establishes the message's From address.
- Whether that fingerprint has been explicitly confirmed in this local account.
- Whether the key is currently within the supported validity policy.

**Certificate-chain trust and online revocation are not checked.** A self-signed S/MIME certificate can have a valid body signature while its sender fingerprint remains unconfirmed. The app does not label that as trusted by a certificate authority. Locally imported OpenPGP revocation packets, identity certifications, binding signatures, key flags, and expiry are checked. Without a fresh imported key, the app cannot know about a remote revocation.

OpenPGP signing subkeys may have mathematically verifiable signatures, but this version does not establish their primary-key ownership through back-signature validation. Their signatures are never used to claim that the pinned primary key establishes the From address.

An encrypted message without a verified sender signature does not authenticate its sender. In particular, S/MIME enveloped data alone is not a signed message. For OpenPGP decryption, the app rejects legacy encrypted packets without integrity protection and validates the integrity check before returning plaintext. MimeKit 4.18's `OpenPgpContext.DecryptTo` does not itself call the BouncyCastle encrypted-data `Verify` method, so the service performs that bounded integrity pass before asking MimeKit to parse the decrypted MIME.

## Drafts, scheduling, Undo, and exports

Editable drafts remain ordinary editable drafts on this PC and, when saved there, in server Drafts. The Options panel says this explicitly. The reserved `X-Magpie-Message-Security` draft header preserves the requested method and sign/encrypt intent across local saves and server drafts. Revision snapshots preserve the same options. Malformed or duplicate security headers are not silently treated as a request to send without protection.

Queueing builds and protects the final outgoing MIME before storing it. A separate operating-system-protected recovery snapshot preserves the editable original for Undo / Cancel & edit. The snapshot is bound to the account, Message-ID, and SHA-256 digest of the exact queued wire bytes. It is inserted in the same SQLite transaction as the outbox row. Undo verifies and restores it before cancellation, so a missing or inaccessible snapshot cannot cancel and lose the queued message. Undo works even if the sender later removes the private identity.

Keys must be valid now and at the planned send time. Before SMTP, the app checks that the same selected, confirmed key material is still present and usable. A removed, rotated, or expired key leaves the queued message failed and recoverable; it never causes an unencrypted resend. Recovery snapshots are cleared after successful delivery or cancellation. A profile restart preserves pending snapshots.

`ReadQueuedBody` exposes only authenticated recovery body strings for other pre-send checks, such as cloud-link permissions. It neither cancels the outbox item nor caches the body, and disposes the temporary MIME tree internally.

**Save as `.eml`** applies the selected signing/encryption options. The wire export contains no private draft security header. A signed or encrypted wire message cannot be reopened directly as an editable draft, because that would discard its original protection; inspect it using the secure reader instead.

## Private storage and the reader boundary

The entire local key store, including private material and file passwords, is protected through `ISecretProtector` before an application-created file exists. Windows uses current-user DPAPI. Replacement files are also protected before the atomic rename. The app does not stage plaintext private-key files or log passwords and key contents. An inaccessible or corrupt protected store is retained and reported rather than overwritten.

The protected key store is local to this operating-system user. Account-copy synchronization does not copy it. Import the original private keys on another PC; an ordinary copied DPAPI file is not a portable private-key backup.

Paired application-version snapshots include the protected key file alongside the mail database, so restoring a version also restores the keys matching its encrypted outbox. Key-manager writes join the engine's profile-operation gate and finish before a version-change snapshot or handoff proceeds.

The **Security…** reader:

- Decrypts only in memory, without writing to the message body database, search index, AI context, or rendered-page file cache.
- Uses the existing HTML sanitizer with remote content blocked, plus a separate WebView2 InPrivate profile whose private mode is checked before content is supplied.
- Serves its one exact document URL through an in-memory response with `Cache-Control: no-store`. It does not map a local folder or call the normal disk-backed HTML publisher.
- Disables document scripts, host messages, host objects, developer tools, default browser export menus, downloads, permissions, external navigation, frames, and remote resource requests. A restrictive CSP provides another boundary.
- Falls back to a native read-only plaintext view if that isolated browser is unavailable. It clears the native text and disposes the MIME result on close.
- Writes decrypted attachments only after the person explicitly chooses **Save attachment…**, confirms that an ordinary plaintext file will be created, and selects the destination.

This prevents application-level plaintext caching. It does not claim locked memory, guaranteed erasure of managed strings, protection against another process running as the same Windows user, or whole-profile encryption. Editable draft history is still ordinary local draft data.

## Verification

`SecureMailTests` generates all certificates and OpenPGP keys offline. It covers serialized sign-only, encrypt-only and sign-plus-encrypt roundtrips; sender and recipient decryption; signature tampering; OpenPGP integrity tampering and missing integrity protection; encrypted account isolation; selected-address identity binding; confirmed versus unconfirmed trust; missing, expired and wrong-password keys; future scheduling; encrypted Bcc rejection; verified encryption subkeys; protected storage and public-only export; local draft and revision persistence; queue restart, protected Undo, payload mismatch, exact pre-SMTP key rechecks, and absence of reader-created plaintext files.

The native `SecureMailSmoke` exercises the real Windows DPAPI protector, compose option bindings, protected `.eml` export, key-manager controls, InPrivate reader profile, script/host-message restrictions, remote image and navigation rejection, render-cache preservation, and clearing the native plaintext view. It is part of the existing Windows ProductivitySmoke gate. Cross-compilation alone does not establish that these native runtime checks pass.

## Primary API references

- [MimeKit cryptography API](https://mimekit.net/docs/html/N_MimeKit_Cryptography.htm)
- [MimeKit 4.18 OpenPgpContext source](https://github.com/jstedfast/MimeKit/blob/4.18.0/MimeKit/Cryptography/OpenPgpContext.cs)
- [MimeKit signature verification API](https://mimekit.net/docs/html/M_MimeKit_Cryptography_IDigitalSignature_Verify_1.htm)
- [WebView2 controller options](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2controlleroptions)
- [Windows ProtectedData](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata)

The implementation was checked against the XML API documentation installed with MimeKit 4.18.0, BouncyCastle.Cryptography 2.7.0, and Microsoft.Web.WebView2 1.0.4191.47, plus the pinned MimeKit source.
