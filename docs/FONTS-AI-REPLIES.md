# Fonts and AI replies on Windows

Version 9.0.0 implements Q80–Q82 / issues #103–#105, using the [FR1-r1 contract](designs/FR1-r1.md). [PR #109](https://github.com/krishnabhunia/Magpie-email-client/pull/109) contains the implementation and live verification evidence. The previous #108 stays closed.

Settings → Fonts & text edits 53 independent font family/size roles. Search across areas, change a role and use the live sample, reset a role/area/all, then Apply. Sizes are 8–72 logical pixels in half-pixel steps. Invalid rows prevent Apply. Missing local fonts fall back safely. Existing built-in appearance is retained when a role has no override; icons and Windows-owned dialogs/notification balloons keep their own typography. Received-email body/heading/quote overrides require the separate reading-font switch and change only the display copy.

New writing and default signatures use explicit Arial 18px (13.5pt), normal weight. Existing formatted drafts and custom rich signature emphasis are preserved. Settings changes update open Windows views and invalidate the reading-page cache.

Settings → AI reply settings has the existing AI master, an initially-off full-reply feature, and independent Reply/Reply All switches. Defaults include tone, purpose, length, language, latest received message/current conversation and custom instructions. Other AI tools keep their independent switches. Settings are staged until Apply and included in password-protected backups.

Clicking Reply or Reply All opens the addressed composer. Once its editor is ready, an enabled/configured/consented feature generates one preview. Review it, regenerate, cancel or discard. Use draft asks to append or replace when you have typed; the editor checks that text again before insertion. Signature and quote boundaries stay intact. Sending remains explicit. A connection/account/context change or turning AI off invalidates pending results. Temporary tone/purpose choices apply only to that draft. Missing context, setup, declined consent and provider failures leave manual writing available.

The model receives bounded text from the selected account and thread, without attachments. Reply recipients are calculated locally using Reply-To and own-address exclusion; generated text cannot change them. Cloud consent is specific to feature, connection and endpoint. The prompt treats email as untrusted quoted data, asks for body text only, and prohibits invented facts or commitments.

## Verification

Use the repository's Windows publish script for the Core suite, zero-warning app build, generated dependency-property inventory, XAML validation, referenced assemblies and single-file publishing. `windows/build/ProductivitySmoke` additionally constructs real light/dark windows, applies/resets font changes, exercises fake-provider generation and Sent-only fallback, and runs the real WebView2 editor in an isolated temporary profile to verify insertion and formatting. It does not load the user's accounts or contact a real provider.

The single GitHub workflow supplies Windows installer and Mac build/test/DMG verification, then validates one downloadable ZIP with platform folders. Android is skipped because no app source is present. The final PR records the current head, independent review, CI/test-merge relationship and beta identity. No manual tag, merge or publication is part of this work.

## Not verified on a real account/device

- Live mailbox send/receive and real cloud-provider output. Try a non-sensitive conversation with Reply and Reply All; review the preview, recipients and sent formatting.
- Windows 125%/150%/200% DPI, screen-reader behavior and every installed font. Try large roles, keyboard navigation, resetting and Settings Apply/Close.
- Native Mac app observation. Shared code is validated by the Mac CI job; the Windows-only settings pages are not added to Mac.
- Dovecot/SMTP integration tests need the repository's Linux test servers and are excluded on Windows as documented by the repository.

The three D:/Projects/Skills/Github Skills source policy documents were unavailable. Supplied user requirements and the current repository rules were used; no compliance with unread text is claimed.
