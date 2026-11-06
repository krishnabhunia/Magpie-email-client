# The compose window (15.0.0)

Version 15.0.0 implements Q91 / issue #132 from the approved [CW1 design](designs/CW1-compose.html) (features A1–E6 approved 5 Nov 2026, design approved the same day). Windows only; the Mac compose window is unchanged, though it shares the editor page and so has the new editor functions available for a later Mac round. On the Mac the menu bar's ⌘ keys (forwarded by `MacMenus.KeyForwardScript`) win: the editor skips any key the app already took (`e.defaultPrevented`), so ⌘, opens Settings without subscripting text.

## What each feature does

| Id | Feature | Where it lives |
|---|---|---|
| A1 | Recipient chips — initials in a colour from the address, the name, ✕; red with "!" when the address doesn't parse. Hover card (name, address, "Saved contact · you wrote to them N times", Move to Cc / Bcc, Open contact, Copy address, Remove). Click a chip to edit it as text; Backspace on an empty field turns the last chip back into text; a pasted "a@x, b@y; c@z" becomes three chips. | `Controls/RecipientBox.cs`; `Text` is still one comma-separated string, so `ComposeViewModel.To/Cc/Bcc`, drafts, autosave and the AI code are untouched |
| A2 | **Cc**, **Bcc** and **Reply-To** are separate links; an opened row hides its link | `ComposeViewModel.ShowCc / ShowBcc / ShowReplyTo` |
| A3 | Drag a chip to another row (data format `magpie/recipient`; the source removes it when the drop is a Move) | `RecipientBox.OnDrop` |
| A4 | 📖 address book: All / Frequent / Groups from the Contacts page's data; tick people (a group ticks its members), To → / Cc → / Bcc →, Done | `Views/AddressBookDialog.cs`, `ComposeViewModel.AddFromAddressBook` |
| A5 | Reply-To row (one address) → `Reply-To` header; Settings → Accounts → **Replies go to** fills it for new messages | `Draft.ReplyTo`, `Account.ReplyTo`, `Composer.Build` |
| A6 | Duplicates merged per field (`Composer.MergeDuplicates`, at Send and when adding chips); To + Cc above 20 → note with **Move them to Bcc** (keeps the first To address visible) | `Composer.VisibleRecipients`, `ComposeViewModel.MoveAllToBcc` |
| B1–B14 | The toolbar: Undo · Redo │ Paragraph style · Font · Size │ B · I · U · S │ Text colour · Highlight │ Alignment ▾ · Bullets · Numbers · Outdent · Indent │ Quote · Line · Table · Picture · Emoji · Link │ Clear. Every command goes through `fmt()` / the new helpers in the editor page (`styleWithCSS` on, so colours and fonts are inline spans). Sizes are pt in the drop-down, sent as px (`setFontSize` replaces the `fontSize 7` placeholder). Table: 8 × 6 grid picker, inline-styled cells, right-click menu via the editor's `table` message → `tableCmd`. Link bubble via `linkat`; picture bubble (Small / Medium / Original / Attach as file / Remove) via `img`. The buttons light up from the editor's `state` message. | `ComposeWindow.Features.cs` (`BuildToolbar`, `MakeTool`, `ApplyEditorState`), `Views/ComposePopups.cs`, `EditorPage.cs` |
| B15 | More ▾: buttons that don't fit (right-most first, measured on `SizeChanged`) plus Alignment, Superscript, Subscript, Paste as plain text, Plain text message, Choose the buttons…. Settings → Appearance → **New message toolbar** (show / hide, order, Reset). | `ApplyOverflow`, `ComposeSettings.Toolbar`, `SettingsViewModel.ComposeToolbarRows` |
| B16 | Plain text message: asks once, converts the HTML to text and shows the plain editor (the one used when WebView2 is missing); the toolbar greys; sent as `text/plain` only (`Draft.PlainText`). Switching back wraps the new text in paragraphs and puts the signature and quoted email back as the HTML they were. A reopened plain draft stays plain without asking; when WebView2 is missing the switch is off (the plain editor is the only editor). | `SwitchPlainTextAsync`, `Composer.Build` |
| C1 | Attachment cards: type badge (XLS / DOC / PDF / ZIP / IMG …), name, size, ✕; click opens; right-click Open · Show in folder · Put in the message instead (pictures) · Remove. The same file twice is one attachment. | `AttachmentItem.Kind / KindColour`, `OnAttachmentMenu` |
| C2 | Size bar: files + carried parts + inline pictures (data: URIs) against `Composer.AttachmentLimit` (account's own MB, else Gmail 25 / Outlook 20 / IMAP 25). Red when over; Send asks once. The body is measured again 400 ms after typing stops (pasted / deleted pictures) and once more just before the over-limit question. The old 25 MB per-file warning is gone. | `ComposeViewModel.RefreshAttachmentSummary`, `AskOverLimitAsync` |
| C3 | Forgot-attachment check at Send: the new text (not the quote) matches attach / attached / attachment / enclosed / PFA / "see the file" and there is no file → "Did you mean to attach a file?" (Send anyway / Add a file). Settings → General → Writing emails switch. | `Composer.MentionsAttachment`, `ChecksBeforeSendAsync` |
| C4 | Paste: a file copied in Explorer → attached (editor posts `pastefiles`, the window reads the clipboard's file list). A pasted picture → asks "Put in message / Attach as file" with "Always do this" (Settings → General → A pasted picture); attached pictures are written to `<mail folder>/attachments-pasted/` as `picture-<time>-<id>.<ext>`, where the extension comes from a fixed list of picture types (`Composer.PastedPictureExtension`; anything else is refused) — the page's media type is never part of the file name. | `OnPastedFiles`, `OnPastedPicture`, `ComposeViewModel.AttachDataUri` |
| C5 | Attach ▾: Attach files… (Ctrl+Shift+O), Picture in the message…, Recent (last 10 attached from this PC, newest first, missing ones greyed) | `ComposeSettings.RecentAttachments`, `OnAttachMenu` |
| D1 | Priority High / Normal / Low → `X-Priority`, `Importance`, `X-MSMail-Priority` (none for Normal); "! High" / "↓ Low" on the subject row; received High shows a red ! in the message list (`MessageFlags.HighPriority`, read from the headers during sync, kept across flag updates) | `Composer.PriorityHeaders / PriorityOf`, `AccountSync.ToRow`, `ThreadItem.IsHighPriority` |
| D2 | Request a read receipt → `Disposition-Notification-To: <From>`; Settings → General → Always ask for a read receipt | `Draft.ReadReceipt` |
| D3 | Signature ▾: each signature of the From account, None, Edit signatures…. Picking one swaps the `.magpie-signature` block in place (`setSignature`); changing From swaps to that account's default. Settings → Signatures & replies keeps several per account (+ New signature, Rename, Use for new messages, Remove); the old single `SignatureHtml` became the first signature and still mirrors the default one for older versions and the accounts copy. | `Account.Signatures / DefaultSignatureId / NormaliseSignatures`, `Composer.SignatureHtmlFor`, `EditableAccount.Signatures` |
| D4 | Templates ▾ → Save this message as a template… (name; the new text without signature and quote) | `ComposeViewModel.SaveAsTemplateAsync` |
| D5 | Templates ▾ lists the quick replies too (insert at the cursor) | `InsertQuickReply` |
| D6 | Send ▾ → Send & Archive (replies and forwards; Ctrl+Alt+Enter): the conversation is archived one second after the undo window, unless the message was taken back first — Undo, or Cancel & edit in Scheduled (every recall goes through `MainViewModel.RecallToDraft`, which marks `_recalled`). The folders are decided when Send is pressed (the view replied from; if that view isn't this account's mail — Sent, Drafts, another account — the account's Inbox and other synced folders), so changing the view meanwhile changes nothing. | `SendAndArchiveAsync`, `MainViewModel.ArchiveFoldersFor / ArchiveAfterUndo` |
| D7 | Footer Discard: "Discard this message?" → the copy on this PC and the server draft it was opened from are deleted (unlike closing without saving, which only drops unsaved changes) | `OnDiscard` → `DiscardForeverAsync` |
| D8 | Word count of the new text (`stats` message, 400 ms after typing); the tooltip adds characters | `Composer.WordCount`, `UpdateStats` |
| D9 | Options ▾ → Save to file (.eml): the same MIME Send would build | `Composer.ToEml` |
| E1 | Find & replace bar (Ctrl+F / Ctrl+H): matches highlighted with `<mark class="mf">`, Enter / Shift+Enter step, Aa = match case, Replace / All; marks never reach the saved or sent HTML (`cleanCopy`) | `findAll / findNext / replaceOne / replaceAll / findClear` |
| E2 | Replies start with the quoted email folded to one pill ("··· Show quoted text · who, when"); click to unfold and edit. Forwards and reopened drafts start open. Settings → General → Fold the quoted text in replies. | `foldQuote / unfoldQuote` |
| E3 | Spelling on / off (Settings → General → Check spelling while I write → `setSpell`); right-click suggestions and Add to dictionary come from WebView2's own menu; languages are Windows' display languages. A per-message language picker is not possible in WebView2. | `EditorPage.setSpell` |
| E4 | Shortcut sheet (Ctrl+/ or Options ▾); every compose shortcut is handled inside the editor page too, because keys don't reach WPF while WebView2 has the focus (`key` messages) | `ComposePopups.Shortcuts`, `OnEditorKey` |
| E5 | The last size, position and maximised state are saved (1 s after a change) and restored; a window that would open off-screen is pulled back; several open windows stagger by 28 px | `ComposeSettings.Left/Top/Width/Height/Maximized`, `RestorePlacement` |
| E6 | `@` + 2 letters in the text → the recipient suggestions; Enter / Tab / ↑ ↓ work in the list; the pick becomes a `mailto:` link and the person is added to To if not there yet ("… added to To") | `checkMention / mention`, `AddMention` |

## Shortcuts

| Message | Keys | Text | Keys |
|---|---|---|---|
| Send | Ctrl+Enter | Bold · Italic · Underline | Ctrl+B · I · U |
| Send & Archive | Ctrl+Alt+Enter | Strikethrough | Ctrl+Shift+X |
| Save draft | Ctrl+S | Bulleted · Numbered list | Ctrl+Shift+8 · 7 |
| Attach files | Ctrl+Shift+O | Indent · Outdent | Ctrl+] · [ |
| Open Cc · Bcc | Ctrl+Shift+C · B | Align left · centre · right · justify | Ctrl+Shift+L · E · R · J |
| Assistant panel | Ctrl+Shift+A | Link · Remove link | Ctrl+K · Ctrl+Shift+K |
| Find · Replace | Ctrl+F · H | Normal · Heading 1–3 · Code | Ctrl+Alt+0 · 1 · 2 · 3 · C |
| Shortcut sheet | Ctrl+/ | Superscript · Subscript | Ctrl+. · , |
| Close | Esc | Clear formatting · Paste as plain text | Ctrl+\ · Ctrl+Shift+V |

Ctrl+Shift+Enter stays "Send now" in the main window's Undo toast.

## Settings added

| Page | Row | Setting |
|---|---|---|
| General → Writing emails (`RowCompose`) | Check spelling while I write · Fold the quoted text in replies · Ask when I forget an attachment · Always ask for a read receipt · A pasted picture (Ask / In the message / Attach) | `ComposeSettings.CheckSpelling / FoldQuote / AttachmentCheck / AlwaysReadReceipt / PastePicture` |
| Appearance → New message toolbar (`RowComposeToolbar`) | Show / hide and order of the formatting buttons, Reset to default | `ComposeSettings.Toolbar` |
| Accounts (`RowAccounts`) | Replies go to · Attachment limit (MB, 0 = the usual limit) | `Account.ReplyTo / AttachmentLimitMb` |
| Signatures & replies (`RowSignatures`) | Several signatures per account: chips, + New signature, Rename, Use for new messages (default), Remove | `Account.Signatures / DefaultSignatureId` |

Not in Settings (saved as they happen): the recent attachments, the recent emoji row, the compose window's size and place.

## Upgrade

`settings.json` from 14.x loads unchanged: each account's `SignatureHtml` becomes its first signature (named "Signature", the default); `Compose` gets defaults (every button shown, spelling on, fold on, attachment check on, no read receipt, ask about pasted pictures). `SignatureHtml` / `Signature` keep mirroring the default signature, so an older Magpie and the accounts copy (SY1) still read a signature. A signature edited on an older PC through the accounts copy updates only the mirror; the newer PC's signature list wins at the next normalise.

## Not verified in the cloud session

The Windows app could not be run here. Built, unit-tested (757 Core tests), XAML-checked and check-refs'ed; the editor page's new functions were exercised in Chromium (tables, find / replace, fold, mention, signature swap, `getHtml` cleaning). To try on Windows: chips (typing, paste, Backspace, drag), the hover card, the address book, every toolbar button incl. colour / table / emoji popups and More ▾ folding at a narrow width, the picture and link bubbles, the right-click table menu, pasting a file and a screenshot, the forgot-attachment and over-limit questions, Send & Archive after the undo window, the signature switcher, plain text and back, find & replace, the folded quote, @mentions, the shortcut sheet, the remembered window size, Settings rows (Apply enabled on change), and the red ! on a received High-priority email.
