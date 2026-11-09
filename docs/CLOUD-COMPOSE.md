# Compose cloud attachments — issues #133 and #78

## Implemented Windows workflow

Choose **Cloud link…** beside Attach, **Upload & share cloud link…** in Studio → Files, or **Cloud link** on a local attachment. The dialog selects a configured Google or Microsoft account, a local file, the exact To/Cc recipients, and View only or Can edit. It defaults to View only. The explicit upload/access checkbox starts unchecked and resets when the account, file, or role changes.

**Connect cloud account…** opens a separate cloud OAuth consent in the browser. Connecting verifies the selected account but uploads no file. **Upload, share & insert link** uploads a new copy, checks its permissions, grants access to the confirmed people without sending sharing notifications, and inserts a normal link at the captured compose position. The selected text and surrounding message remain intact. Plain-text compose receives `filename: https://…` as literal text. Converting a local attachment removes that attachment only after a successful insertion for the same selected file.

The upload limit is 100 MiB (displayed as 100 MB); files must be nonempty and there must be 1–50 distinct To/Cc addresses. This picker selects a local file to upload; **Browse cloud…** is a separate workflow for files already stored in Drive or OneDrive. Server-carried attachment cards cannot be converted without first saving the attachment locally.

## Browse and share existing Drive/OneDrive files — issue #78

Choose **Browse cloud…** next to the compose cloud upload button or **Browse & share existing cloud file…** in Studio → Files. The browser starts disconnected and cannot list files until you use **Connect existing-file access…**. This is a new, independent consent: Google requests `https://www.googleapis.com/auth/drive` to search and share already-existing files; OneDrive uses delegated `Files.ReadWrite` and `User.Read`. The original narrower upload-only Google permission is unchanged. Tokens are stored separately under `account:<id>:cloud:browse:*` with client/account binding, verified cloud identity, and protected refresh keys. Google OAuth apps requesting broad Drive access may need Google verification; tenant policies can also block consent.

An empty search lists recent owned Google Drive files or direct children of the OneDrive root. A name search finds owned files in the connected drive (including nested items where the provider supports it). The first 50 results are shown; if more exist, narrow your search. Files that are not owned by the signed-in account, folders, inaccessible entries, unsafe URLs, and unsupported remote items are not offered. The selected cloud file is re-fetched before sharing to detect changes to its ID, name, location, or link.

Select an existing file, edit the **link title**, review the exact To/Cc recipient list and **View only / Can edit** role, then tick the separate unchecked confirmation. Changing the account, search, file, title, or role clears that confirmation. **Share existing file & insert link** first checks existing permissions: owner and the reviewed people with the requested role are allowed; public/anonymous links, extra people, group/inherited grants, unknown identities, incomplete pages, and conflicting roles stop the operation **before modifying access**. Only absent reviewed recipients are granted individual permission; sharing notifications are suppressed. Final named permissions are rechecked before the link is inserted at the editor's captured location. Hidden Bcc-only identities remain unsupported.

**This changes the original remote file**, not an uploaded copy. Removing a link or discarding a draft does not revoke completed sharing. On provider error or cancellation *after* a grant may have started, Magpie never deletes the original or assumes its permissions were rolled back: the dialog provides a verified cloud-file link so you can inspect its access. Draft/editor changes during sharing prevent insertion, with the same explicit remote-access warning. Successful links are included in the local access-receipt checks before Send and when delivering scheduled messages. The receipt tracks the provider identity, reviewed recipients, role, and remote permission IDs. A second link title for the same account/file/unchanged named grants can reuse this proof without overwriting it.

## Account and access boundaries

| Behavior | Google Drive | OneDrive |
| --- | --- | --- |
| New-copy upload permission | `https://www.googleapis.com/auth/drive.file` | Delegated `https://graph.microsoft.com/Files.ReadWrite` |
| Independently connected existing-file permission | `https://www.googleapis.com/auth/drive` | Delegated `https://graph.microsoft.com/Files.ReadWrite` with independent refresh keys |
| Identity permission | `openid email`; verified Google userinfo email | `User.Read openid email`; verified `/me` mail or user principal name |
| Continued access | Separate cloud refresh token | Separate cloud refresh token with `offline_access` |
| New-file location | A new app-created binary file in My Drive | A new `Magpie upload <random ID>` folder in the signed-in user's personal or business OneDrive |
| Upload protocol | Generated file ID, new metadata, resumable upload to a validated Google session URL | Streamed PUT into the new folder; conflict behavior explicitly `fail` |
| Reviewed grants | Named `user` permissions with `reader` or `writer` | Single-recipient invitations with `read` or `write`, `requireSignIn=true` |
| Extra notifications | `sendNotificationEmail=false` | `sendInvitation=false` |
| Permission fallback | None | None |

Cloud tokens use the existing protected vault under `account:<id>:cloud:*`, plus a separate in-memory OAuth cache. IMAP/SMTP refresh tokens and their scopes are untouched. Each cloud binding includes provider, account email, and OAuth client ID. Account identity is checked on use; changing/removing the account or changing its OAuth app invalidates the binding. An account change during browser consent cannot save tokens for the stale selection. Removing an account clears its protected cloud keys through the normal account-prefix cleanup.

The new-copy implementation checks the initial permission list before sending file bytes. After granting access, it checks the entire returned permission list again. Only the owner and confirmed named grants are accepted; public links, groups, unexpected inherited identities, changed roles, missing grants, and incomplete/paginated permission results stop the operation. OneDrive owner identity is bound to `/me/drive` as well as the directory identity, because a personal drive's owner identifier can differ from `/me`.

Hidden Bcc-only recipients are blocked. Cloud collaborators can inspect access lists, so adding a hidden person to that list could reveal a Bcc identity. A duplicate address already visible in To/Cc creates no additional hidden identity. The dialog explains the restriction and offers ordinary file attachments as the available path.

Access changes take effect when the user confirms the upload, before the email is sent. Removing a recipient, Undo Send, deleting the email's link, or discarding the draft does not revoke a completed cloud grant. This is stated before approval. Google writer resharing is disabled on the new file; provider and organization rules may still give editors additional capabilities. No anonymous link, ownership transfer, existing-file replacement, existing permission removal, or default permission change is requested.

The selected OAuth application must permit these delegated scopes and the relevant API must be available. Tenant policy, unprovisioned/unavailable storage, denied consent, quota restrictions, external-recipient restrictions, or expired/revoked credentials produce an explicit failure. The implementation does not widen permissions or switch to an anonymous link to bypass those failures. Microsoft may provision the signed-in user's licensed OneDrive when its drive endpoint is first accessed, as documented by Microsoft; this occurs only inside the confirmed upload flow for a new upload.

## Cancellation, cleanup, and draft correctness

File streaming uses an 80 KiB buffer with a fixed declared length. Truncation, growth, oversized responses, unsafe provider URLs, and cancellation are rejected. The file's length and last modification time are rechecked after confirmation; Windows opens it with read sharing only during the operation.

The compose operation is claimed before the upload's first asynchronous wait. Send, Close, application exit, discard, and save-and-close cannot overtake it. Consent, upload, receipt writes, cleanup, and permission checks that may refresh a token use the engine's profile-operation admission. Version switching cancels and drains those operations before its final backup. Existing paired version backups include cloud tokens in `secrets.json` and receipts in `mail.db`. The operation records draft edits, compose mode, sender, recipients, selected cloud account, and OAuth client identity. A stale result never inserts a link. The editor separately verifies its captured body and insertion range, including draft restoration and mode changes.

Failure or cancellation removes only the resource created by that operation: the generated Google file, or the fresh OneDrive upload folder containing its new file. Cleanup has a separate 30-second cancellation budget covering both fresh cloud authorization and deletion. A conflicting creation response never authorizes deletion of an existing object. Failed cleanup remains visible with a safe provider location and instructions to remove or review the remaining copy; the dialog does not silently close over that warning. If a process or device crashes after remote creation, cleanup cannot be guaranteed: the new file/folder can remain in the chosen account and must be reviewed there.

After the editor acknowledges insertion, the target is retained even if a late cancellation or local save failure occurs. The draft is immediately saved locally. If that save fails, the compose status explicitly asks the user to keep the window open and save again. The active link is never deleted behind the visible draft.

A non-secret local receipt records the uploaded file, original cloud account, exact grants, and access role. Receipts are separate from outgoing MIME and independent of draft IDs, so local/server draft reopening, draft history, and undo do not lose known access provenance. The registry has bounded record/receipt/message limits and rejects invalid records. The receipt is retained after account removal so an existing tracked link cannot silently bypass the account check.

Before queuing and before SMTP transport, the engine checks recipients against known receipts. Compose additionally verifies the provider's current permissions before queuing. Scheduled sends verify current provider access again at delivery time. New recipients, hidden Bcc identities, changed/missing permissions, or missing credentials block delivery; these checks never change access or launch interactive consent in the background. To share with a newly added person, remove the old link and explicitly upload/share a new copy or re-review an existing file for those recipients.

Receipts are local profile metadata, not embedded in email or portable exports. Pasted pre-existing links and messages imported on a different PC without that profile's receipt are ordinary links; Magpie cannot claim that their access was checked. Provider access can also change after the final check and before a recipient opens a link. Users can manage already-completed grants in their provider's own UI.

## Regression and acceptance coverage

`CloudFilesTests` and `CloudExistingFileTests` use fake provider HTTP and a local loopback OAuth callback; no credentials, real accounts, remote uploads, sharing actions, or messages are used. They cover both providers/roles, exact uploaded bytes, owned-file search, separate existing-file consent, named grants and disabled notifications, owner identity differences, protected token separation, identity/scope/client binding, stale consent, wrong recipients, permission revocation, public/inherited access, unsafe or absent file/session URLs, explicit creation conflicts, bounded file changes, cancellation, partial grants, no deletion of existing files, failed cleanup, receipt persistence, changed recipients, and delayed-send authorization failure.

`CloudComposeSmoke` is part of the existing Windows ProductivitySmoke executable. It exercises both themed cloud dialogs, separate existing-file browse controls, default access and confirmation resets, exact recipient display, Bcc rejection, native Send/Close/exit/discard protection, stale result rejection, cancellation, cleanup errors, immediate saved MIME, and actual WebView2 rich/plain insertion. These fixtures reject all provider HTTP.

Validation commands:

```sh
dotnet test common/tests/Magpie.Core.Tests -c Release -m:1 --filter "Category!=Integration"
dotnet build windows/build/ProductivitySmoke -c Release -m:1
python3 windows/build/xaml_check.py
```

The Windows smoke must execute on a Windows runner with WebView2; cross-compilation alone is not native acceptance. A real-provider acceptance pass additionally needs disposable Google, Microsoft personal, and Microsoft work/school test accounts and an approved OAuth app. Check each role, denied scope, external-recipient restriction, cancelled upload, revoked access before scheduled send, cleanup visibility, and receipt-preserving reopen/history. This implementation was developed without accessing live accounts or uploading real files.

## API references checked

- [Google Drive API scopes and `drive.file`](https://developers.google.com/workspace/drive/api/guides/api-specific-auth)
- [Google Drive owned-file listing and search](https://developers.google.com/workspace/drive/api/reference/rest/v3/files/list)
- [Google Drive existing-file metadata](https://developers.google.com/workspace/drive/api/reference/rest/v3/files/get)
- [Google Drive upload methods and resumable uploads](https://developers.google.com/workspace/drive/api/guides/manage-uploads)
- [Google generated file IDs](https://developers.google.com/workspace/drive/api/reference/rest/v3/files/generateIds)
- [Google permission creation and notification control](https://developers.google.com/workspace/drive/api/reference/rest/v3/permissions/create)
- [Google permission inspection](https://developers.google.com/workspace/drive/api/reference/rest/v3/permissions/list)
- [Microsoft Graph current user's drive and provisioning behavior](https://learn.microsoft.com/en-us/graph/api/drive-get?view=graph-rest-1.0)
- [Microsoft drive owner identity](https://learn.microsoft.com/en-us/graph/api/resources/drive?view=graph-rest-1.0)
- [Microsoft Graph search for existing drive items](https://learn.microsoft.com/en-us/graph/api/driveitem-search?view=graph-rest-1.0)
- [OneDrive streamed file upload (provider maximum 250 MB)](https://learn.microsoft.com/en-us/graph/api/driveitem-put-content?view=graph-rest-1.0)
- [OneDrive conflict behavior](https://learn.microsoft.com/en-us/graph/api/resources/driveitem?view=graph-rest-1.0)
- [OneDrive named invitations, roles and notification control](https://learn.microsoft.com/en-us/graph/api/driveitem-invite?view=graph-rest-1.0)
- [OneDrive permission inspection](https://learn.microsoft.com/en-us/graph/api/driveitem-list-permissions?view=graph-rest-1.0)
